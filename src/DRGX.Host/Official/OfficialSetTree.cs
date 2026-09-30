using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// OfficialSetTree — 编码集合(官方工作簿「集合」sheet 派生的索引 + 成员两张表)的层级视图
//
// 这两张表在「数据一览」里原本各占一个子标签:集合索引(634 行)是主表,集合成员(106,837 行)
// 是明细表,靠 set_id 外键下钻互跳。平铺成两张互不关联的宽表读不出「类型 → 集合 → 成员」这层
// 结构,故与分组主干同样交给树呈现。与主干树的根本差别是体量 —— 主干全树 1,384 个节点可一次
// 下发(约 90KB),而成员 106,837 行整包下发要数 MB,所以**只有前两级进树**:第一级集合类型、
// 第二级集合号,成员由右栏按需分页拉取(见 /api/official/setmembers、/api/official/setsearch)。
//
// 树上的成员数恒等于右栏能加载出来的条数:两边同源,都取码索引表的 member_count 与成员表的
// 实际行数(已逐集合核对 634/634 一致,合计 106,837)。
//
// 层级只用官方列:第一级 type(官方只给 DI / OP 两个值,成员编码可印证 —— DI 成员是 ICD-10
// 诊断码,OP 成员是 ICD-9-CM-3 手术操作码),第二级 set_id。
// **不按 MDC / ADRG 归并**:集合号后缀确实多为 ADRG 编码(622/634),但它只是命名规律而非官方
// 外键 —— 599 个集合各自只被一个同后缀 ADRG 引用,OP_ALL 却被 27 个歧义组(QY)共同引用,
// 另有 DI_OBD / OP_ARB 无人引用。拿它当归属层级,等于把"树上的数字为何与官方表对不上"变成
// 日常解释成本(与主干树剔除 00 类同一个取舍:口径只留官方那一套)。
// ============================================================================

/// <summary>编码集合树:<paramref name="Roots"/> 为集合类型根,<paramref name="MemberTotal"/> 为全部集合的成员合计。</summary>
internal sealed record OfficialSetTreeResult(OfficialTreeNode[] Roots, OfficialTreeLevel[] Levels, int MemberTotal);

internal static class OfficialSetTree
{
    /// <summary>"数据一览"里的子标签 id(与其余官方表并列)。</summary>
    public const string Id = "sets";

    public const string Label = "编码集合";

    public const string Desc = "官方打包好的编码集合,被各 ADRG 的入组条件直接引用:类型 → 集合号,成员编码在右侧按需加载";

    /// <summary>本树覆盖的官方 sheet(表源说明用:前缀是工作簿文件名,由 OfficialPackView.FileName 提供)。</summary>
    public const string SourceSheets = "集合";

    /// <summary>
    /// 由本树统一呈现的表:集合索引是树的第一/二级来源,集合成员是右栏的懒加载来源,
    /// 两者都不再各自成页(见 OfficialEndpoints 的表清单组装)。
    /// </summary>
    public static readonly string[] MemberIds = [OfficialWorkbook.CodeSetIndexTable, OfficialWorkbook.CodeSetsTable];

    /// <summary>集合类型 → 中文名。官方 type 列只有 DI / OP 两个取值,且全表无例外。</summary>
    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DI"] = "诊断编码集合",
        ["OP"] = "手术操作编码集合",
    };

    public static OfficialSetTreeResult Build(OfficialWorkbook book)
    {
        var idx = book.Table(OfficialWorkbook.CodeSetIndexTable);
        var setCol = idx.IndexOf("set_id");
        if (setCol < 0)   // 列缺失(非官方形态的表):给空树,不抛 —— 与"表缺失则整表不出现"同一口径
            return new OfficialSetTreeResult([], Levels(0, 0), 0);

        var order = new List<string>();                                              // 类型的出现顺序 = 工作簿行序
        var buckets = new Dictionary<string, List<OfficialTreeNode>>(StringComparer.OrdinalIgnoreCase);
        var memberTotal = 0;

        foreach (var row in idx.Rows)
        {
            var set = idx.Cell(row, "set_id");
            if (set.Length == 0) continue;
            var type = idx.Cell(row, "type");
            if (type.Length == 0) type = "—";                                        // 官方不会缺;缺了也不丢集合
            var count = int.TryParse(idx.Cell(row, "member_count"), out var n) ? n : 0;
            memberTotal += count;

            if (!buckets.TryGetValue(type, out var list)) { buckets[type] = list = []; order.Add(type); }
            list.Add(new OfficialTreeNode(set, "", "", [], count));
        }

        var roots = new List<OfficialTreeNode>(order.Count);
        var setCount = 0;
        foreach (var type in order)
        {
            var name = TypeNames.TryGetValue(type, out var known) ? known : "";       // 未知类型不编中文名
            setCount += buckets[type].Count;
            roots.Add(new OfficialTreeNode(type, name, "", buckets[type]));
        }
        return new OfficialSetTreeResult(roots.ToArray(), Levels(roots.Count, setCount), memberTotal);
    }

    /// <summary>层级元数据:第一级集合类型、第二级集合号。zeroCount / qyCount 是主干树专有口径,这里恒 0。</summary>
    private static OfficialTreeLevel[] Levels(int typeCount, int setCount) =>
    [
        new("settype", "集合类型", "类型", typeCount, 0, 0),
        new("set", "集合", "集合", setCount, 0, 0),
    ];

}
