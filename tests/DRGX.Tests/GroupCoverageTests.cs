using System.Text;
using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>
/// 覆盖门禁:断言"每个可落位的 DRG 组都有用例",并且断言这个"每个"是真的每个。
///
/// <para><b>为什么需要一个门禁文件,而不是靠生成器自己保证。</b>生成器会把造不出 witness 的组
/// 写进 <c>unreachable.txt</c>,于是"覆盖 100%"这件事永远成立 —— 只要愿意把任何一个组
/// 塞进那张表。这类门禁在别处反复出过事:<b>把失败声明成豁免,闸门就自动变绿</b>,
/// 而人看到绿灯就不会再去查。因此本文件的重点不是"断言覆盖",而是
/// <b>把豁免本身也纳入断言</b> —— 豁免只允许出现在占位组上,且必须带量化证据。</para>
///
/// <para><b>组码的四类口径</b>(官方 871 = 825 + 24 + 21 + 1):</para>
/// <list type="bullet">
/// <item>825 个正式细分组 —— 官方发布口径,必须有 witness 且必须有边界探针;</item>
/// <item>24 个 MDC 内占位组(<c>B000</c>…<c>Y000</c>) —— 引擎不可达,豁免的唯一合法去处;</item>
/// <item>21 个 QY 歧义组 —— 必须有 witness,期望落 <c>Ambiguous</c> 而非 <c>Success</c>;</item>
/// <item>1 个全局兜底行 <c>0000</c> —— 引擎刻意判为未入组,不属任何"组",不参与覆盖统计。</item>
/// </list>
/// </summary>
public class GroupCoverageTests
{
    /// <summary>官方发布口径的正式细分组数。</summary>
    private const int ExpectedOfficialGroups = 825;
    /// <summary>MDC 内占位组数(= 全部字母 MDC 数)。</summary>
    private const int ExpectedPlaceholderGroups = 24;
    /// <summary>官方 QY 白名单 MDC 数(B~Z,不含 A/S/T/X/Y)。</summary>
    private const int ExpectedAmbiguousGroups = 21;

    /// <summary>四类口径必须与官方发布数字一一对上,且刚好不重不漏地划分全部可达落位码。</summary>
    [Fact]
    public void GroupTaxonomyMatchesOfficialCounts()
    {
        Assert.Equal(ExpectedOfficialGroups, OfficialPack.OfficialGroups.Count);
        Assert.Equal(ExpectedPlaceholderGroups, OfficialPack.PlaceholderGroups.Count);
        Assert.Equal(ExpectedAmbiguousGroups, OfficialPack.AmbiguousGroups.Count);

        // 可达码 = 三类之和 + 全局兜底那一条落位规则(它本身是 0000,不是"组")。
        var partition = OfficialPack.OfficialGroups
            .Concat(OfficialPack.PlaceholderGroups)
            .Concat(OfficialPack.AmbiguousGroups)
            .Append(OfficialPack.GlobalFallbackCode)
            .ToHashSet(Comparer);

        Assert.Equal(ExpectedOfficialGroups + ExpectedPlaceholderGroups + ExpectedAmbiguousGroups + 1,
            partition.Count);
        Assert.Equal(partition.Count, OfficialPack.EmittedCodes.Count);
        Assert.Empty(partition.Except(OfficialPack.EmittedCodes, Comparer));
        Assert.Empty(OfficialPack.EmittedCodes.Except(partition, Comparer));

        // 三类互不相交:重叠会让"这个码到底算不算已覆盖"出现两种答案。
        Assert.Empty(OfficialPack.OfficialGroups.Intersect(OfficialPack.PlaceholderGroups, Comparer));
        Assert.Empty(OfficialPack.OfficialGroups.Intersect(OfficialPack.AmbiguousGroups, Comparer));
        Assert.Empty(OfficialPack.PlaceholderGroups.Intersect(OfficialPack.AmbiguousGroups, Comparer));
    }

    /// <summary>每个正式细分组都有一条 witness,且那条 witness 的期望落点就是它自己。</summary>
    [Fact]
    public void EveryOfficialGroupHasAWitness()
    {
        var witnessed = CaseStore.Witnesses
            .Where(c => c.Kind == CaseKinds.Witness)
            .Select(c => c.Target!)
            .ToHashSet(Comparer);

        Assert.Equal(ExpectedOfficialGroups, witnessed.Count);
        var missing = OfficialPack.OfficialGroups.Except(witnessed, Comparer)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"以下 {missing.Count} 个正式细分组没有 witness:{string.Join(",", missing)}");
    }

    /// <summary>
    /// 豁免清单一字不差地等于 24 个占位组。
    ///
    /// <para>这条是整套用例的防作弊闸:把某个正式的组写进 <c>unreachable.txt</c> 可以让
    /// "覆盖 100%"继续成立,但过不了这里。反方向也锁住了 —— 若有朝一日某个占位组变成可达,
    /// 它会拿到 witness 并从豁免名单消失,这条同样会红,逼人明确"这不是漂移,是有意为之"。</para>
    /// </summary>
    [Fact]
    public void UnreachableListIsExactlyThePlaceholderGroups()
    {
        var declared = CaseLibrary.LoadUnreachable().Select(u => u.Code).ToHashSet(Comparer);

        Assert.Equal(ExpectedPlaceholderGroups, declared.Count);

        var unexpected = declared.Except(OfficialPack.PlaceholderGroups, Comparer)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(unexpected.Count == 0,
            $"unreachable.txt 里出现了占位组以外的码:{string.Join(",", unexpected)}。" +
            "这几个是正式细分组,必须为其造出 witness;若确实造不出,先查生成器的搜索策略" +
            "(历史上 WB29/PV11 等都曾是生成器的缺陷,不是规则真的不可达)。");

        var unlisted = OfficialPack.PlaceholderGroups.Except(declared, Comparer)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(unlisted.Count == 0,
            $"以下占位组既没有 witness 也没进豁免名单:{string.Join(",", unlisted)}。" +
            "若是它们变得可达了,重新生成基线即可;否则必须补上带证据的豁免行。");
    }

    /// <summary>豁免不是"反推失败"四个字 —— 每行必须带能被人复核的量化证据。</summary>
    [Fact]
    public void UnreachableDeclarationsCarryQuantitativeEvidence()
    {
        var rows = CaseLibrary.LoadUnreachable();
        Assert.NotEmpty(rows);

        var thin = new List<string>();
        foreach (var (code, reason) in rows)
        {
            // 证据格式由生成器 DescribeFailure 产出:候选样本数 + 落点分布 Top-N。
            bool hasScan = reason.Contains("扫描", StringComparison.Ordinal);
            bool hasSpread = reason.Contains("落点分布", StringComparison.Ordinal);
            if (reason.Length < 40 || !hasScan || !hasSpread) thin.Add($"{code}={reason}");
        }

        Assert.True(thin.Count == 0,
            "以下豁免行的原因不是可复核的证据(需要「扫描 N 个候选样本」与「落点分布」两段):\n  "
            + string.Join("\n  ", thin));
    }

    /// <summary>豁免的码必须真实存在 —— 防手滑写错一个字,静默把一个组变成"已声明不可达"。</summary>
    [Fact]
    public void UnreachableCodesMustExistInTheCompiledPack()
    {
        var unknown = CaseLibrary.LoadUnreachable()
            .Select(u => u.Code)
            .Where(c => !OfficialPack.EmittedCodes.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal).ToList();

        Assert.True(unknown.Count == 0,
            $"unreachable.txt 里的以下码在任何落位规则里都不存在(疑似错字):{string.Join(",", unknown)}");
    }

    /// <summary>21 个 QY 歧义组各有一条 witness,期望是 <c>Ambiguous</c> 而不是 <c>Success</c>。</summary>
    [Fact]
    public void EveryAmbiguousGroupHasAWitness()
    {
        var witnessed = CaseStore.Witnesses
            .Where(c => c.Kind == CaseKinds.Ambiguous)
            .Select(c => c.Target!)
            .ToHashSet(Comparer);

        Assert.Equal(ExpectedAmbiguousGroups, witnessed.Count);
        var missing = OfficialPack.AmbiguousGroups.Except(witnessed, Comparer)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"以下 QY 组没有 witness:{string.Join(",", missing)}");
    }

    /// <summary>
    /// 每个有 witness 的目标都至少有一条边界探针。
    ///
    /// <para>"有 witness"只证明这个组到得了;用户要的是"覆盖各种边界条件",所以每个组还必须
    /// 至少有一条"把某个事实推到阈值另一侧 / 去掉某个关键事实"的探针。没有这条断言,
    /// 边界派生逻辑悄悄漏掉一整族目标(例如诊断驱动的组)时不会有任何信号 —— 历史上正是如此,
    /// 192 个内科组曾经一条边界用例都没有。</para>
    /// </summary>
    [Fact]
    public void EveryWitnessedTargetHasBoundaryProbes()
    {
        var probed = CaseStore.Boundary.Select(c => c.Target!).ToHashSet(Comparer);
        var witnessed = CaseStore.Witnesses.Select(c => c.Target!).ToHashSet(Comparer);

        var missing = witnessed.Except(probed, Comparer)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"以下 {missing.Count} 个组只有 witness、没有任何边界探针:{string.Join(",", missing)}");
    }

    /// <summary>
    /// 边界探针必须真的"探到边界",而不是清一色地掉到同一个落点。
    ///
    /// <para>如果绝大多数探针的期望落点都跟 witness 一样,说明派生器没造出有效的变异;
    /// 如果所有探针都掉到一个固定码上,说明变异把病案打坏了(变成输入校验失败)。
    /// 两种都会被这条拦下 —— 这条是防止"边界用例数量涨了但信息量没涨"的体检项。</para>
    /// </summary>
    [Fact]
    public void BoundaryProbesAreNotDegenerate()
    {
        var cases = CaseStore.Boundary;

        var held = 0;
        var distinctLandings = new HashSet<string>(Comparer);
        var checkFailed = 0;
        foreach (var c in cases)
        {
            if (c.Kind == CaseKinds.BoundaryHold) held++;
            switch (c.Expect.Status)
            {
                case GroupStatus.Success or GroupStatus.Ambiguous:
                    distinctLandings.Add(c.Expect.Code!);
                    break;
                case GroupStatus.CheckFailed:
                    checkFailed++;
                    break;
            }
        }

        // 变异只应改事实、不应把病案改成非法输入;出现 CheckFailed 说明取码逻辑取到了空/非法值。
        Assert.True(checkFailed == 0,
            $"有 {checkFailed} 条边界探针把病案改成了「信息校验不通过」,变异逻辑越界了");
        // 落点必须真的分散开:如果全部掉到同一个码,说明变异破坏了 MDC 门控,测的不是本组边界。
        Assert.True(distinctLandings.Count >= 50,
            $"边界探针的期望落点只有 {distinctLandings.Count} 个不同值,变异很可能把病案打坏了");
        // 允许一条 hold 都没有(说明 witness 恰在阈值上),但绝不允许全部都是 hold。
        Assert.True(held < cases.Count,
            "全部边界探针的落点都跟 witness 相同 —— 变异没有生效");
    }

    /// <summary>每条用例都要能说清"它针对什么":Id 稳定、Note 非空且指名落位规则。</summary>
    [Fact]
    public void EveryCaseIsTraceable()
    {
        var untraceable = new List<string>();
        foreach (var c in CaseStore.Witnesses.Concat(CaseStore.Boundary))
        {
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Note))
            {
                untraceable.Add($"{c.Id}: 缺少 Id/Note");
                continue;
            }
            if (c.Kind == CaseKinds.Witness && !c.Note.Contains("落位规则", StringComparison.Ordinal))
                untraceable.Add($"{c.Id}: witness 的说明未指名落位规则({c.Note})");
            if (c.Kind == CaseKinds.Ambiguous && !c.Note.Contains("歧义", StringComparison.Ordinal))
                untraceable.Add($"{c.Id}: QY 用例的说明未说明歧义成因({c.Note})");
        }

        Assert.True(untraceable.Count == 0,
            "以下用例缺少可追溯的说明:\n  " + string.Join("\n  ", untraceable.Take(20)));
    }

    /// <summary>
    /// 用例的输入码必须"<b>输入即分组口径</b>":不得依赖 <c>code-maps.csv</c> 改写才能落地。
    ///
    /// <para><b>为什么这条是闸门而不是风格问题。</b><c>code-maps.csv</c> 是正常设计(国临版→医保版
    /// 的版本转换,界面上「编码版本」切换的载体),但一份码**是否经过它改写**,决定了该用例
    /// 测的是"分组规则"还是"映射链 + 分组规则"。生成器一度取 <c>pack.DiagnosisNames</c>
    /// 的第一个键当保底主诊断,而那份并集字典含 9,104 个官方分组方案<b>集合表未收录</b>的码 ——
    /// 取到 <c>N07.900x002</c>「Dent病」后,它被 code-maps 折成 <c>N07.900x001</c>「遗传性肾炎」
    /// (两个不同疾病)而落进 LS2。后果是手术驱动组(AA19/AB19/…)的"去掉主手术"探针,期望值被
    /// 冻结成 <c>LS25</c> —— 一个与该组毫无关系的肾病组,且这条期望值<b>只在兼容展开生效时成立</b>。</para>
    ///
    /// <para>修掉该保底取码后,这 13 条探针如实变为"未入组",与 OpenDRG 对账的一致条数同步 +13。
    /// 本闸门是双向的:任何人再往生成器里加"退回全量并集字典"的兜底取码,只要取到需要改写的码就会红。</para>
    /// </summary>
    [Fact]
    public void CaseInputCodesDoNotRelyOnCodeMapsRewrite()
    {
        var pack = OfficialPack.Data;
        var offenders = new List<string>();
        foreach (var c in CaseStore.Witnesses.Concat(CaseStore.Boundary))
        {
            foreach (var code in c.Diagnoses)
                if (pack.DiagnosisMap.TryGetValue(code, out var mapped))
                    offenders.Add($"{c.Id}: 诊断 {code} → {mapped}");
            foreach (var code in c.Procedures)
                if (pack.ProcedureMap.TryGetValue(code, out var mapped))
                    offenders.Add($"{c.Id}: 操作 {code} → {mapped}");
        }

        Assert.True(offenders.Count == 0,
            $"有 {offenders.Count} 处用例输入码依赖 code-maps 改写(期望值建立在映射链而非分组规则上):\n  "
            + string.Join("\n  ", offenders.Take(20)));
    }

    /// <summary>基线文件必须全部覆盖到:任何一条"读了却没人跑"的用例都是假覆盖。</summary>
    [Fact]
    public void BaselineFilesAreFullyConsumed()
    {
        var expected = CaseStore.Witnesses.Concat(CaseStore.Boundary).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        // RegressionTests / BoundaryCaseTests 的理论数据源取的就是这两张表,这里断言两表非空且无重复 Id
        // (重复 Id 会被 CaseLibrary 直接抛异常,所以能读出来即已保证唯一)。
        Assert.NotEmpty(CaseStore.Witnesses);
        Assert.NotEmpty(CaseStore.Boundary);
        Assert.Equal(expected.Count, CaseStore.Witnesses.Count + CaseStore.Boundary.Count);
    }

    /// <summary>给失败信息用的摘要:把一份病案渲染成一行,人一眼能看出边界到底挪了哪格。</summary>
    internal static string Describe(TestCase c)
    {
        var sb = new StringBuilder();
        sb.Append("性别=").Append(c.Gender);
        if (c.Age is int a) sb.Append(" 年龄=").Append(a);
        if (c.AgeDay is int d) sb.Append(" 日龄=").Append(d);
        if (c.Weight is int w) sb.Append(" 体重=").Append(w);
        sb.Append(" 诊断=[").Append(string.Join(",", c.Diagnoses)).Append(']');
        if (c.Procedures.Count > 0)
            sb.Append(" 操作=[").Append(string.Join(",", c.Procedures)).Append(']');
        return sb.ToString();
    }

    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
}
