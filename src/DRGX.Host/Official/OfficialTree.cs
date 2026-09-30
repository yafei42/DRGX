using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// OfficialTree — 分组主干(MDC → ADRG → DRG)的层级视图
//
// 这三张官方表在「数据一览」里原本各占一个子标签,平铺成三张互不关联的宽表,读不出
// 层级。本模块按官方外键(adrg.mdc → mdc.code / drg.adrg → adrg.code)把它们嵌成一棵树,
// 一次下发全树(26 + 513 + 846 = 1385 个节点,约 90KB),前端本地展开 / 检索 / 导出,不再分页。
// (编码集合走另一棵树,见 OfficialSetTree —— 它的成员 10 万行,只能按需加载。)
//
// 「00 类」(MDC 0000 / ADRG X00 / DRG X000)**不进树**:它们是"什么都没命中"的落位桶
// (编码与名称在官方表里都是空的),混进层级骨架只会把主干读成一张明细表。剔除是硬过滤 ——
// 不留开关,因为多一套口径就多一处"树上的数字为何与官方工作簿对不上"的解释成本;
// 三张原始表(mdc / adrg / drg 子标签)仍逐行呈现,想查 00 类去那里看。
//
// 注意:不要把 00 类与 DRG 表的 is_fallback 列混为一谈 —— 该列在 DRG 层标了 538 条
// (含 AA19 心肺移植这类正常先期分组),与"00 类"不是一回事;本模块只按编码形状判定,
// 不看任何 is_fallback 值,也就不改写、不依赖那列的口径。
//
// 数据源与取值口径完全复用 OfficialTable(同一份官方工作簿、同样按列名取值),本模块只做嵌套。
// ============================================================================

/// <summary>树的一级。<paramref name="Count"/> 是入树节点数,<paramref name="ZeroCount"/> 是被剔除的 00 类档数,
/// <paramref name="QyCount"/> 是其中的歧义组(XQY,官方名称留空、与前缀组并列的单列档)。</summary>
/// <param name="Tag">是行首"竖轴"层级徽标文本(MDC / ADRG / DRG)。</param>
internal sealed record OfficialTreeLevel(string Id, string Label, string Tag, int Count, int ZeroCount, int QyCount);

/// <summary>单个树节点:编码 + 名称 + 官方入组条件原文(DSL) + 子节点(DRG 层恒为空)。
/// <paramref name="Count"/> 是本节点自带的计数(编码集合树的集合节点 = 官方 member_count;
/// 主干树没有这个口径,恒 null —— 前端只在有值时显示,不编 0 出来)。</summary>
internal sealed record OfficialTreeNode(
    string Code, string Name, string Dsl, List<OfficialTreeNode> Children, int? Count = null);

/// <summary>整棵树:根节点 + 各层计数 + 被剔除的 00 类总条数(仅用于表源说明如实标注)。</summary>
internal sealed record OfficialTreeResult(OfficialTreeNode[] Roots, OfficialTreeLevel[] Levels, int ZeroTotal);

internal static class OfficialTree
{
    /// <summary>"数据一览"里的子标签 id(与其余官方表并列)。</summary>
    public const string Id = "tree";

    public const string Label = "MDC → ADRG → DRG";

    public const string Desc = "分组主干的三级层级:MDC 大类 → ADRG 核心组 → DRG 细分组";

    /// <summary>本树覆盖的官方 sheet(表源说明用:前缀是工作簿文件名,由 OfficialPackView.FileName 提供)。</summary>
    public const string SourceSheets = "MDC / ADRG / DRG";

    public static OfficialTreeResult Build(OfficialWorkbook book)
    {
        var mdc = book.Table(OfficialWorkbook.MdcTable);
        var adrg = book.Table(OfficialWorkbook.AdrgTable);
        var drg = book.Table(OfficialWorkbook.DrgTable);

        var levels = new int[3, 3];   // [层, 0=入树数 1=剔除的 00 类数 2=其中的歧义组数]

        // DRG → 按所属 ADRG 归并。子节点顺序 = 工作簿行序(装载时已按官方「排序」列排好)。
        var drgByAdrg = new Dictionary<string, List<OfficialTreeNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in drg.Rows)
        {
            var code = drg.Cell(row, "code");
            var parent = drg.Cell(row, "adrg");
            if (parent.Length == 0) continue;   // 无父键:无法入树,丢弃比挂到假父下更诚实
            if (IsZeroGroup(2, code)) { levels[2, 1]++; continue; }
            levels[2, 0]++;
            if (IsQyGroup(code)) levels[2, 2]++;
            if (!drgByAdrg.TryGetValue(parent, out var list)) drgByAdrg[parent] = list = [];
            list.Add(new OfficialTreeNode(code, drg.Cell(row, "name"), drg.Cell(row, "dsl"), []));
        }

        // ADRG → 按所属 MDC 归并,并把上一步归好的 DRG 挂进来
        var adrgByMdc = new Dictionary<string, List<OfficialTreeNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in adrg.Rows)
        {
            var code = adrg.Cell(row, "code");
            if (code.Length == 0) continue;
            var parent = adrg.Cell(row, "mdc");
            if (parent.Length == 0) continue;
            if (IsZeroGroup(1, code)) { levels[1, 1]++; continue; }
            levels[1, 0]++;
            if (IsQyGroup(code)) levels[1, 2]++;
            drgByAdrg.TryGetValue(code, out var kids);
            if (!adrgByMdc.TryGetValue(parent, out var list)) adrgByMdc[parent] = list = [];
            list.Add(new OfficialTreeNode(code, adrg.Cell(row, "name"), adrg.Cell(row, "dsl"), kids ?? []));
        }

        var roots = new List<OfficialTreeNode>();
        foreach (var row in mdc.Rows)
        {
            var code = mdc.Cell(row, "code");
            if (code.Length == 0) continue;
            if (IsZeroGroup(0, code)) { levels[0, 1]++; continue; }
            levels[0, 0]++;
            if (IsQyGroup(code)) levels[0, 2]++;
            adrgByMdc.TryGetValue(code, out var kids);
            roots.Add(new OfficialTreeNode(code, mdc.Cell(row, "name"), mdc.Cell(row, "dsl"), kids ?? []));
        }

        var meta = new OfficialTreeLevel[]
        {
            new("mdc", "MDC 大类", "MDC", levels[0, 0], levels[0, 1], levels[0, 2]),
            new("adrg", "ADRG 核心组", "ADRG", levels[1, 0], levels[1, 1], levels[1, 2]),
            new("drg", "DRG 细分组", "DRG", levels[2, 0], levels[2, 1], levels[2, 2]),
        };
        var zeroTotal = levels[0, 1] + levels[1, 1] + levels[2, 1];
        return new OfficialTreeResult(roots.ToArray(), meta, zeroTotal);
    }

    /// <summary>是否"00 类"落位档:MDC 0000 / ADRG X00 / DRG X000 —— 官方表里编码与名称皆空,
    /// 只是"前面规则全没命中"时的落位桶,不属于层级骨架,进树前直接剔除。
    /// 只看编码形状,不读 is_fallback 列(该列在 DRG 层把大量正常先期分组也标成了 1)。</summary>
    private static bool IsZeroGroup(int level, string code) => level switch
    {
        0 => code.Equals("0000", StringComparison.OrdinalIgnoreCase),
        1 => code.EndsWith("00", StringComparison.OrdinalIgnoreCase),
        _ => code.EndsWith("000", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>是否歧义组(XQY,如 BQY/FQY):官方名称同样留空,但**保留在树里** ——
    /// 它们是"诊断/手术信息不足以细分到具体 ADRG"时的正式落位组,与 00 类(纯兜底)性质不同,
    /// 所以要能数得出来、看得见,只是计数上与正常组分开标。</summary>
    private static bool IsQyGroup(string code) => code.EndsWith("QY", StringComparison.OrdinalIgnoreCase);
}
