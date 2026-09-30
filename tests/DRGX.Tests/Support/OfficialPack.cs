using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>
/// 全测试共享的官方数据包与引擎实例。
///
/// <para>官方工作簿的解析(3.4 MB xlsx → 7 张表)是秒级的,如果每个测试类各加载一次,
/// 全套测试的墙钟时间会被它主导。因此只加载一次并全程复用:引擎与 <see cref="DataPack"/>
/// 都是只读的(<see cref="GrouperEngine"/> 明确声明了线程安全),xunit 的并行测试类共享同一实例安全。</para>
///
/// <para>组码的四类口径在这里一次分清,后续所有断言都引用这里的集合,避免各处各数一遍
/// (871 = 825 正式细分组 + 24 个 MDC 内占位组 + 21 个 QY 歧义组 + 1 个全局兜底行)。</para>
/// </summary>
internal static class OfficialPack
{
    /// <summary>官方全局兜底行(drg 表的 0000 行)。引擎把它判为**未入组**而非分组成功,故不属任何"组"。</summary>
    public const string GlobalFallbackCode = "0000";

    private static readonly Lazy<DataPack> PackLazy = new(() => PackReader.Load(RepoPaths.PackDirectory));

    public static DataPack Data => PackLazy.Value;

    public static GrouperEngine Engine { get; } = new(PackLazy.Value);

    /// <summary>全部可达落位码:每条 split 的码 + QY 白名单动态产出的 {MDC}QY。
    /// 直接取装载期的派生(<see cref="DataPack.EmittedCodes"/>)—— 测试不再自己重推一遍,
    /// 否则"引擎认为哪些码可达"与"测试认为哪些码可达"会各自漂移。</summary>
    public static IReadOnlySet<string> EmittedCodes => Data.EmittedCodes;

    /// <summary>官方发布口径的 DRG 组(825):细分组名非空,不含占位组、QY 与全局兜底。</summary>
    public static IReadOnlySet<string> OfficialGroups { get; } = BuildOfficialGroups();

    /// <summary>MDC 内占位组(24,如 B000):名称为空的真实落位,语义是"该 MDC 内未细分"。</summary>
    public static IReadOnlySet<string> PlaceholderGroups { get; } = BuildPlaceholderGroups();

    /// <summary>QY 歧义组码(21 个 {MDC}QY)。</summary>
    public static IReadOnlySet<string> AmbiguousGroups { get; } = BuildAmbiguousGroups();

    /// <summary>
    /// 官方数据实际用到的条件原语。
    ///
    /// <para>用途是回答"哪些原语没有官方用例":这 38 个原语里总有一部分官方工作簿一次都没用到
    /// (官方发布的是<b>实例化后的规则</b>,不是 DSL 全集),那些原语就是 0 覆盖的代码路径,
    /// 必须由 <c>ConditionPrimitiveTests</c> 用合成上下文补齐。这份集合是那个补集清单的<b>唯一来源</b>:
    /// 与其手写一份"未用到清单"(改了官方数据就过期),不如每次普查一遍。</para>
    /// </summary>
    public static IReadOnlySet<ConditionKind> UsedPrimitives { get; } = BuildUsedPrimitives();

    private static HashSet<ConditionKind> BuildUsedPrimitives()
    {
        var used = new HashSet<ConditionKind>();
        void Walk(Condition c)
        {
            used.Add(c.Kind);
            foreach (var ch in c.Children) Walk(ch);
        }

        foreach (var mdc in Data.MdcChain)
        {
            Walk(mdc.Gate);
            foreach (var adrg in mdc.Adrgs)
            {
                foreach (var entry in adrg.Entries) Walk(entry.When);
                foreach (var split in adrg.Splits) if (split.When is not null) Walk(split.When);
            }
        }
        foreach (var rule in Data.QyRules.Values) Walk(rule);
        return used;
    }

    private static HashSet<string> BuildPlaceholderGroups() =>
        Data.Groups.Values
            .Where(g => string.IsNullOrWhiteSpace(g.Name))
            .Select(g => g.Code)
            .Where(c => !c.EndsWith("QY", StringComparison.Ordinal)
                        && !c.Equals(GlobalFallbackCode, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> BuildAmbiguousGroups() =>
        Data.Groups.Keys.Where(c => c.EndsWith("QY", StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> BuildOfficialGroups()
    {
        var excluded = BuildPlaceholderGroups();
        excluded.Add(GlobalFallbackCode);
        return Data.MdcChain.SelectMany(m => m.Adrgs).SelectMany(a => a.Splits)
            .Select(s => s.Code)
            .Where(c => !excluded.Contains(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
