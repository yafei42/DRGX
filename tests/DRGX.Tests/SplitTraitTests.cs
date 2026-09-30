using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>
/// split 条件的两条派生 —— 分档(<see cref="SeverityTier"/>)与直赋档(<see cref="DirectRule"/>)—— 的回归闸门。
///
/// <para><b>为什么值得单独钉住。</b>这两条派生是"落位码 → 人话"的唯一桥梁。它们错了不会让落位码变,
/// 只会让界面把「机器人直赋」说成「不伴并发症」:引擎侧测试全绿,用户看到错话。
/// 更麻烦的是官方包与 JSON 包对同一件事有两种写法,只认一种就会让整条轨静默失效
/// —— 本文件把这个"两种写法"的事实钉在数据上。</para>
/// </summary>
public class SplitTraitTests
{
    /// <summary>
    /// 官方 3.0 工作簿里全部直赋档的落位码。这是一份**冻结的官方数据事实**:工作簿换版时
    /// 它会失败,提示按新数据重新核对,而不是悄悄漂移。
    /// </summary>
    private static readonly string[] OfficialRobotTiers =
        ["EB10", "GB40", "IB40", "IC30", "IE20", "LA20", "MB10", "NA10", "NC10"];

    private static readonly string[] OfficialHighRiskTiers = ["OB11", "OB21"];

    /// <summary>
    /// 机器人直赋在官方轨里**不是** <c>robotAssist</c> 原语,而是集合成员判断
    /// <c>{ZYSS,QTSS} in OP_ARB</c>(编译后 <c>SetRef == OP_ARB</c>)。
    ///
    /// <para>这条断言把该事实钉死:如果哪天官方工作簿改用 <c>robotAssist</c> 原语,
    /// <see cref="SplitTraits.DirectRuleOf"/> 的 SetRef 分支就成了死代码,这里会先报出来。</para>
    /// </summary>
    [Fact]
    public void OfficialPackExpressesRobotTierAsSetMembershipNotAsThePrimitive()
    {
        var robotPrimitives = 0;
        var setMembershipTiers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(Condition c, string? splitCode)
        {
            if (c.Kind == ConditionKind.RobotAssist) robotPrimitives++;
            if (splitCode is not null
                && c.Kind is ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn or ConditionKind.AnyOtherProcedureIn
                && c.SetRef == SplitTraits.RobotSetRef)
                setMembershipTiers.Add(splitCode);
            foreach (var child in c.Children) Walk(child, splitCode);
        }

        foreach (var mdc in OfficialPack.Data.MdcChain)
        {
            Walk(mdc.Gate, null);
            foreach (var adrg in mdc.Adrgs)
            {
                foreach (var entry in adrg.Entries) Walk(entry.When, null);
                foreach (var split in adrg.Splits) if (split.When is not null) Walk(split.When, split.Code);
            }
        }

        Assert.Equal(0, robotPrimitives);
        Assert.Equal(
            OfficialRobotTiers.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
            setMembershipTiers.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <see cref="SplitTraits.DirectRuleOf"/> 必须把官方轨的 9 条机器人档 + 2 条高危妊娠档
    /// 全部认成直赋档,且不多认。只认 <c>robotAssist</c> 原语的实现会漏掉前 9 条。
    /// </summary>
    [Fact]
    public void EveryOfficialDirectTierIsDerived()
    {
        var robot = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var highRisk = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, rule) in AllSplitsWithDirectRule())
        {
            if (rule.Kind == DirectKind.Robot) robot.Add(code);
            else highRisk.Add(code);
        }

        Assert.Equal(OfficialRobotTiers.OrderBy(c => c, StringComparer.OrdinalIgnoreCase), robot);
        Assert.Equal(OfficialHighRiskTiers.OrderBy(c => c, StringComparer.OrdinalIgnoreCase), highRisk);
    }

    /// <summary>
    /// 端到端:用真实的 witness 病案走完整引擎,断言直赋档确实出现在判定结果里。
    ///
    /// <para>这是本文件里唯一"用户视角"的断言 —— 前面的派生正确但没接到
    /// <c>GroupOutcome.DrgDirect</c> 上的话,只有这条会红。</para>
    /// </summary>
    [Fact]
    public void DirectTiersSurviveEndToEndGrouping()
    {
        foreach (var code in OfficialRobotTiers)
        {
            var outcome = Group(code);
            Assert.Equal("机器人直赋", outcome.DrgDirect);
        }

        foreach (var code in OfficialHighRiskTiers)
        {
            var c = CaseStore.Witness($"w:{code}");
            var outcome = OfficialPack.Engine.Group(c.ToRecord());
            Assert.Equal(code, outcome.Code);

            // OB11/OB21 的 when 是 any[主诊断∈产科清单, hasMcc]:同一码既能由主诊断直赋、
            // 也能由纯 MCC 落位。直赋说明只在主诊断确实在清单内时给出 —— 否则常规分档会被误标。
            var probe = SplitTraits.DirectRuleOf(FindSplit(code).When)!.Value.Probe!;
            var expected = probe.CodeSet.Contains(c.ToRecord().Diagnoses[0]) ? "高危妊娠直赋" : null;
            Assert.Equal(expected, outcome.DrgDirect);
        }
    }

    /// <summary>至少有一条高危妊娠 witness 是靠主诊断清单(而非纯 MCC)落位的 —— 否则上面那条
    /// "主诊断在清单内"的分支从未被真实病案走到。</summary>
    [Fact]
    public void AtLeastOneHighRiskWitnessLandsViaItsMainDiagnosis()
    {
        var viaDiagnosis = OfficialHighRiskTiers
            .Select(code => CaseStore.Witness($"w:{code}"))
            .Count(c => FindSplit(c.Target!).When is { } w
                        && SplitTraits.DirectRuleOf(w)!.Value.Probe!.CodeSet.Contains(c.ToRecord().Diagnoses[0]));

        Assert.True(viaDiagnosis > 0,
            $"没有一条高危妊娠 witness 的主诊断落在官方产科清单内,直赋复核路径缺真实覆盖。");
    }

    /// <summary>
    /// 派生只认**正向**分支:<c>not(...)</c> 子树不参与。按字面取 <c>not(hasMcc)</c> 里的 hasMcc
    /// 会把"不伴严重并发症"读成"伴严重并发症"。
    /// </summary>
    [Fact]
    public void NegatedBranchesDoNotContribute()
    {
        Assert.Equal(SeverityTier.NoComplication, SplitTraits.TierOf(Primitives.Logic(ConditionKind.Not, Primitives.Plain(ConditionKind.HasMcc))));
        Assert.Equal(SeverityTier.NoComplication, SplitTraits.TierOf(Primitives.Logic(ConditionKind.Not, Primitives.Plain(ConditionKind.RobotAssist))));

        Assert.Null(SplitTraits.DirectRuleOf(Primitives.Logic(ConditionKind.Not, Primitives.Plain(ConditionKind.RobotAssist))));
        Assert.Null(SplitTraits.DirectRuleOf(Primitives.Logic(ConditionKind.Not, Primitives.In(ConditionKind.MainDiagnosisIn, "A00.000"))));
    }

    /// <summary>多个并发症原语并存时取**最强**档,与"any 首中即优先"的落位语义一致。</summary>
    [Fact]
    public void StrongestComplicationPrimitiveWins()
    {
        Assert.Equal(SeverityTier.WithMcc,
            SplitTraits.TierOf(Primitives.Logic(ConditionKind.Any, Primitives.Plain(ConditionKind.HasCc), Primitives.Plain(ConditionKind.HasMcc))));
        Assert.Equal(SeverityTier.WithCc,
            SplitTraits.TierOf(Primitives.Logic(ConditionKind.All, Primitives.Plain(ConditionKind.HasCc), Primitives.Num(ConditionKind.AgeGte, 18))));
        Assert.Equal(SeverityTier.NoComplication,
            SplitTraits.TierOf(Primitives.Num(ConditionKind.AgeGte, 18)));
    }

    /// <summary>分档文案与 <see cref="SeverityTier"/> 一一对应(前端直接显示这个字符串)。</summary>
    [Fact]
    public void TierLabelsAreStable()
    {
        Assert.Equal("伴严重并发症", SplitTraits.Label(SeverityTier.WithMcc));
        Assert.Equal("伴并发症", SplitTraits.Label(SeverityTier.WithCc));
        Assert.Equal("不伴并发症", SplitTraits.Label(SeverityTier.NoComplication));
    }

    /// <summary>机器人直赋按 <c>EvaluationContext.RobotAssist</c> 复核。</summary>
    [Fact]
    public void RobotDirectIsRecheckedAgainstTheRobotFlag()
    {
        var rule = SplitTraits.DirectRuleOf(FindSplit("EB10").When)!.Value;
        Assert.Equal(DirectKind.Robot, rule.Kind);
        Assert.Null(rule.Probe);   // 复核依据是 RobotAssist,不是条件本身

        Assert.Equal("机器人直赋", SplitTraits.ResolveDirect(rule, Primitives.Context().Robot(true).Build()));
        Assert.Null(SplitTraits.ResolveDirect(rule, Primitives.Context().Robot(false).Build()));
    }

    /// <summary>
    /// 高危妊娠直赋按主诊断复核:主诊断不在清单内时**即使有 MCC** 也不给直赋说明。
    /// 这正是"不能按落位码静态下发"的原因。
    /// </summary>
    [Fact]
    public void HighRiskDirectIsRecheckedAgainstTheMainDiagnosis()
    {
        var rule = SplitTraits.DirectRuleOf(FindSplit("OB11").When)!.Value;
        Assert.Equal(DirectKind.HighRisk, rule.Kind);
        var probe = rule.Probe!;

        var inList = probe.CodeSet.First();
        var notInList = OfficialPack.Data.DiagnosisNames.Keys.First(d => !probe.CodeSet.Contains(d));

        Assert.Equal("高危妊娠直赋", SplitTraits.ResolveDirect(rule, Primitives.Context().Dx(inList).Build()));
        Assert.Null(SplitTraits.ResolveDirect(rule, Primitives.Context().Dx(notInList).Build()));
        Assert.Null(SplitTraits.ResolveDirect(rule, Primitives.Context().Dx(notInList).Mcc("A00.000").Build()));
    }

    // ---------------- 辅助 ----------------

    private static GroupOutcome Group(string code)
    {
        var c = CaseStore.Witness($"w:{code}");
        var outcome = OfficialPack.Engine.Group(c.ToRecord());
        Assert.True(outcome.Status == GroupStatus.Success && string.Equals(outcome.Code, code, StringComparison.OrdinalIgnoreCase),
            $"witness w:{code} 应落 Success/{code},实际 {outcome.Status}/{outcome.Code}");
        return outcome;
    }

    private static CompiledSplit FindSplit(string code) =>
        OfficialPack.Data.MdcChain.SelectMany(m => m.Adrgs).SelectMany(a => a.Splits)
            .First(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Code, DirectRule Rule)> AllSplitsWithDirectRule() =>
        OfficialPack.Data.MdcChain.SelectMany(m => m.Adrgs).SelectMany(a => a.Splits)
            .Select(s => (s.Code, Rule: SplitTraits.DirectRuleOf(s.When)))
            .Where(x => x.Rule is not null)
            .Select(x => (x.Code, x.Rule!.Value));
}
