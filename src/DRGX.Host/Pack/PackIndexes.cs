using DRGX.Engine;

namespace DRGX.Host;

/// <summary>
/// 数据包的派生索引:全部是 <c>DataPack → 查找表</c> 的纯派生(无 IO、无插件、无副作用)。
///
/// <para>与 <c>Pack/PackRuntime.cs</c> 的区别:PackRuntime 负责"把数据包读进来",
/// 本类负责"从读进来的包算出宿主要用的视图"(ADRG 入组明细 / DRG 条件原文 / 直赋档规则)。
/// 两者都只在启动期跑一次,但变更理由不同:换数据格式动前者,加展示维度动后者。</para>
/// </summary>
internal static class PackIndexes
{
    /// <summary>
    /// 由数据包运行时派生 ADRG 入组明细,唯一真相源是 rules/mdc-&lt;x&gt;.json 内各 ADRG 的
    /// origin(官方入组条件原文)。dx/proc 为该 ADRG 全部入组路径中「主要诊断 /
    /// 主要手术」位置可达的编码条目数(码表并集去重),用于前端"可入 N 种"展示。
    /// 仅增强展示:本函数为纯派生,不影响分组判定。
    /// </summary>
    public static Dictionary<string, AdrgInfo> BuildAdrgInfo(DataPack pack)
    {
        var map = new Dictionary<string, AdrgInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var mdc in pack.MdcChain)
            foreach (var adrg in mdc.Adrgs)
            {
                var dx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var proc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in adrg.Entries) CollectCodeCounts(entry.When, dx, proc);
                // ADRG 名:取首个落位档的 DRG 名,截去"，伴/不伴…"严重度后缀
                var name = "";
                var firstSplit = adrg.Splits.Count > 0 ? adrg.Splits[0] : null;
                if (firstSplit is not null && pack.Groups.TryGetValue(firstSplit.Code, out var gi))
                {
                    var i = gi.Name.IndexOf('，');
                    name = i > 0 ? gi.Name[..i] : gi.Name;
                }
                // MDC 中文名:取自数据包 mdc.csv(可选文件,缺失为空),纯展示不参与判定
                var mdcName = pack.MdcNames.TryGetValue(mdc.Code, out var mn) ? mn : "";
                map[adrg.Code] = new AdrgInfo(name, "MDC" + mdc.Code, mdcName, adrg.Origin, dx.Count, proc.Count);
            }
        return map;
    }

    /// <summary>递归汇总条件树中「主要诊断 / 主要手术」位置引用到的编码集合。</summary>
    private static void CollectCodeCounts(Condition node, HashSet<string> dx, HashSet<string> proc)
    {
        switch (node.Kind)
        {
            case ConditionKind.MainDiagnosisIn: dx.UnionWith(node.CodeSet); break;
            case ConditionKind.MainProcedureIn: proc.UnionWith(node.CodeSet); break;
        }
        foreach (var child in node.Children) CollectCodeCounts(child, dx, proc);
    }

    /// <summary>收集全部 DRG 细分组的 origin 条件原文(DRG码 → split.origin)。</summary>
    public static Dictionary<string, string> BuildDrgOrigin(DataPack pack)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mdc in pack.MdcChain)
            foreach (var adrg in mdc.Adrgs)
                foreach (var split in adrg.Splits)
                    if (!string.IsNullOrWhiteSpace(split.Origin))
                        map[split.Code] = split.Origin;
        return map;
    }

    /// <summary>
    /// 收集直赋档 DRG(DRG码 → 直赋规则):split.when 含 robotAssist → 机器人直赋(脚注18,
    /// 直赋优先于合并症分档);含 mainDiagnosisIn → 高危妊娠直赋(当前包仅 OB11/OB21)。
    /// 由 rules 的 when 结构派生,不改动 origin 官方原文。
    /// 高危妊娠档附带原条件(Probe)供请求期复核主诊断——同一 DRG 也可能由纯 MCC 命中落位,
    /// 静态映射无法区分,故不能只看落位码。
    /// </summary>
    public static Dictionary<string, DrgDirectRule> BuildDrgDirect(DataPack pack)
    {
        var map = new Dictionary<string, DrgDirectRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var mdc in pack.MdcChain)
            foreach (var adrg in mdc.Adrgs)
                foreach (var split in adrg.Splits)
                {
                    var rule = SplitDirectRule(split.When);
                    if (rule is not null) map[split.Code] = rule;
                }
        return map;
    }

    /// <summary>从 split.when 派生直赋规则:robotAssist → 机器人直赋(触发码表在 DataPack,
    /// 与 GrouperEngine 同源);mainDiagnosisIn → 高危妊娠直赋(带原条件供按 811 清单复核)。
    /// 沿 any/all 下钻取首个直赋条件——any 首中语义下即规则里的优先分支。</summary>
    private static DrgDirectRule? SplitDirectRule(Condition? when)
    {
        if (when is null) return null;
        switch (when.Kind)
        {
            case ConditionKind.RobotAssist: return new DrgDirectRule(DrgDirectKind.Robot, "机器人直赋", null);
            case ConditionKind.MainDiagnosisIn: return new DrgDirectRule(DrgDirectKind.HighRisk, "高危妊娠直赋", when);
            case ConditionKind.Any or ConditionKind.All:
                foreach (var child in when.Children)
                {
                    var found = SplitDirectRule(child);
                    if (found is not null) return found;
                }
                return null;
            default: return null;
        }
    }
}
