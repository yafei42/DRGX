using DRGX.Engine;

namespace DRGX.Host;

/// <summary>
/// 数据包的派生索引:全部是 <c>DataPack → 查找表</c> 的纯派生(无 IO、无插件、无副作用)。
///
/// <para>与 <c>Pack/PackRuntime.cs</c> 的区别:PackRuntime 负责"把数据包读进来",
/// 本类负责"从读进来的包算出宿主要用的**展示**视图"(ADRG 入组明细 / 码表规模)。
/// 两者都只在启动期跑一次,但变更理由不同:换数据格式动前者,加展示维度动后者。</para>
///
/// <para>注意边界:凡是**判定结论**(分档、直赋档、官方条件原文、有效操作)都不在这里 ——
/// 它们随 <c>GroupOutcome</c> 由引擎下发(见 DRGX.Engine 的 SplitTraits 与 PreparedCase)。
/// 本类原先还派生过 drg-origin / drg-direct 两张按 DRG 码回查的表,那是把引擎的规则解读
/// 复制到了宿主侧:同一个 DRG 的落位分档要由消费端重推,且每次新增特例都要再补一张表。
/// 现已收进引擎。</para>
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
}
