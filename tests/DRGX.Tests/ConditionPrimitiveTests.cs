using System.Text.Json;
using DRGX.Engine;
using P = DRGX.Tests.Primitives;

namespace DRGX.Tests;

/// <summary>
/// 条件原语的语义边界。
///
/// <para><b>为什么需要这个文件。</b>引擎有 38 个条件原语,而官方工作簿下发的规则只用到其中 23 个 ——
/// 它发布的是<b>实例化后的规则</b>,不是 DSL 全集。剩下 15 个原语是<b>零覆盖的出货代码</b>:
/// <c>anyDiagnosisPrefix</c>、<c>anyOtherProcedureIn</c>、<c>procedureCountGte</c>、
/// <c>hasProcedure</c>、<c>noValidMainProcedure</c>、<c>hasValidProcedure</c>、
/// <c>hasOtherDiagnosis</c>、<c>ageGt</c>、<c>ageLt</c>、<c>ageNot</c>、<c>ageDayLte</c>、
/// <c>ageDayGt</c>、<c>diagnosisIntersectCountGte</c>、<c>siteCountGte</c>、<c>robotAssist</c>。</para>
///
/// <para>它们不是死代码:官方换个批次就可能用上,引擎的 DSL 编译器本来就会产出它们。
/// 用 <see cref="Primitives"/> 合成上下文逐条钉住语义,比等下一个数据批次"碰巧测到"可靠得多。</para>
///
/// <para><b>这个文件也负责盯住"哪些原语还没被官方数据用到"。</b>
/// <see cref="OfficialPack.UsedPrimitives"/> 每次普查,补集就是本文件的必测清单 ——
/// 手写一份"未使用清单"必然过期,普查不会。官方数据开始用上某个原语时,
/// <see cref="UnusedPrimitiveCensusMatchesTheData"/> 会红,提示把它从清单里划掉。</para>
/// </summary>
public class ConditionPrimitiveTests
{
    // ==================================================================
    // 一、官方数据未用到的原语:逐条钉住语义
    // ==================================================================

    /// <summary>严格与非严格比较的分界 —— 不含等号的那一侧必须把阈值本身排除掉。</summary>
    [Fact]
    public void StrictAgeComparisonsExcludeTheThresholdItself()
    {
        Expect(P.Num(ConditionKind.AgeGt, 70),
            ("年龄 71", P.Context().Age(71), true),
            ("年龄 70", P.Context().Age(70), false),
            ("年龄 69", P.Context().Age(69), false),
            ("年龄未知", P.Context().Age(null), false));

        // ageLt(1):1 不算。新生儿场景靠它区分"未满 1 岁"与"满 1 岁"。
        Expect(P.Num(ConditionKind.AgeLt, 1),
            ("年龄 0", P.Context().Age(0), true),
            ("年龄 1", P.Context().Age(1), false),
            ("年龄 2", P.Context().Age(2), false),
            ("年龄未知", P.Context().Age(null), false));

        // ageNot(28):只有恰好等于阈值才为假。
        Expect(P.Num(ConditionKind.AgeNot, 28),
            ("年龄 29", P.Context().Age(29), true),
            ("年龄 27", P.Context().Age(27), true),
            ("年龄 28", P.Context().Age(28), false),
            ("年龄未知", P.Context().Age(null), false));
    }

    /// <summary>
    /// 日龄条件的"年龄护栏":年龄 ≥ 1 岁时,一切日龄条件一律为假。
    ///
    /// <para>护栏存在的理由是真实上报习惯 —— 成年人病案常被顺手填 <c>ageDay=0</c>,
    /// 若放行就会把成年人当成 0 天新生儿命中新生儿组。这是本引擎里最容易被后续改动
    /// 悄悄去掉的一条防护(它单看"多余"),所以四个日龄原语都要逐条钉住。</para>
    /// </summary>
    [Fact]
    public void DayAgeConditionsAreDisabledOnceAgeIsKnown()
    {
        Expect(P.Num(ConditionKind.AgeDayGte, 29),
            ("日龄 29、年龄缺省", P.Context().AgeDay(29), true),
            ("日龄 28、年龄缺省", P.Context().AgeDay(28), false),
            ("日龄 29、年龄 0", P.Context().Age(0).AgeDay(29), true),
            ("日龄 29、年龄 1", P.Context().Age(1).AgeDay(29), false),
            ("日龄 29、年龄 45", P.Context().Age(45).AgeDay(29), false),
            ("日龄未知、年龄缺省", P.Context().AgeDay(null), false));

        // 不含等号:29 不算 > 29。
        Expect(P.Num(ConditionKind.AgeDayGt, 29),
            ("日龄 30", P.Context().AgeDay(30), true),
            ("日龄 29", P.Context().AgeDay(29), false),
            ("日龄 30、年龄 1", P.Context().Age(1).AgeDay(30), false));

        // 含等号。
        Expect(P.Num(ConditionKind.AgeDayLte, 3),
            ("日龄 3", P.Context().AgeDay(3), true),
            ("日龄 2", P.Context().AgeDay(2), true),
            ("日龄 4", P.Context().AgeDay(4), false),
            ("日龄 3、年龄 1", P.Context().Age(1).AgeDay(3), false));

        // 不含等号(官方 PV1/PV2 的"日龄 < 29"靠它)。
        Expect(P.Num(ConditionKind.AgeDayLt, 29),
            ("日龄 28", P.Context().AgeDay(28), true),
            ("日龄 29", P.Context().AgeDay(29), false),
            ("日龄 28、年龄 2", P.Context().Age(2).AgeDay(28), false));
    }

    /// <summary>体重族四个方向各取阈值两侧一格,物证"含不含等号"没写反。</summary>
    [Fact]
    public void WeightComparisonsCoverBothSidesOfEveryThreshold()
    {
        Expect(P.Num(ConditionKind.WeightGte, 2000),
            ("体重 2000", P.Context().Weight(2000), true),
            ("体重 1999", P.Context().Weight(1999), false),
            ("体重未知", P.Context().Weight(null), false));
        Expect(P.Num(ConditionKind.WeightGt, 2000),
            ("体重 2001", P.Context().Weight(2001), true),
            ("体重 2000", P.Context().Weight(2000), false));
        Expect(P.Num(ConditionKind.WeightLte, 2499),
            ("体重 2499", P.Context().Weight(2499), true),
            ("体重 2500", P.Context().Weight(2500), false));
        Expect(P.Num(ConditionKind.WeightLt, 2499),
            ("体重 2498", P.Context().Weight(2498), true),
            ("体重 2499", P.Context().Weight(2499), false));
    }

    /// <summary>操作计数与"有没有操作":主操作与次操作一起数。</summary>
    [Fact]
    public void ProcedureCountAndPresenceCountMainAndOtherTogether()
    {
        Expect(P.Num(ConditionKind.ProcedureCountGte, 2),
            ("无操作", P.Context().Procs(), false),
            ("1 项主操作", P.Context().Procs("A"), false),
            ("主操作 + 1 项次操作", P.Context().Procs("A", "B"), true),
            ("3 项", P.Context().Procs("A", "B", "C"), true));

        Expect(P.Plain(ConditionKind.HasProcedure),
            ("无操作", P.Context().Procs(), false),
            ("仅主操作", P.Context().Procs("A"), true),
            ("主操作 + 次操作", P.Context().Procs("A", "B"), true));
    }

    /// <summary>
    /// 最容易被混淆的一对原语:<c>noValidMainProcedure</c> 判的是"<b>主操作</b>不在有效操作表内"
    /// (没有主操作也算),"hasValidProcedure"则主/次操作<b>任一</b>有效即真。
    ///
    /// <para>两者共用同一张有效操作表,但"看哪些操作"与"看几个"都不同 —— 用同一批场景
    /// 并排断言,任何一边被改成另一边都会被抓住。</para>
    /// </summary>
    [Fact]
    public void ValidProcedurePrimitivesDistinguishMainFromAny()
    {
        const string Valid = "31.1000";
        const string Other = "99.9999";

        Expect(P.Plain(ConditionKind.NoValidMainProcedure),
            ("无任何操作", P.Context().ValidProcedures(Valid), true),
            ("主操作有效", P.Context().Procs(Valid).ValidProcedures(Valid), false),
            ("主操作无效", P.Context().Procs(Other).ValidProcedures(Valid), true),
            ("主操作无效但次操作有效", P.Context().Procs(Other, Valid).ValidProcedures(Valid), true));

        Expect(P.Plain(ConditionKind.HasValidProcedure),
            ("无任何操作", P.Context().ValidProcedures(Valid), false),
            ("主操作有效", P.Context().Procs(Valid).ValidProcedures(Valid), true),
            ("主操作无效", P.Context().Procs(Other).ValidProcedures(Valid), false),
            ("主操作无效但次操作有效", P.Context().Procs(Other, Valid).ValidProcedures(Valid), true));
    }

    /// <summary>
    /// <c>anyOtherProcedureIn</c> 只看<b>非主</b>操作 —— 与 <c>anyProcedureIn</c>(主+次)
    /// 和 <c>mainProcedureIn</c>(仅主)构成三档。位置搞错会让"主操作命中却被判为不命中"。
    /// </summary>
    [Fact]
    public void OtherProcedureConditionIgnoresTheMainProcedure()
    {
        Expect(P.In(ConditionKind.AnyOtherProcedureIn, "B"),
            ("仅主操作=B", P.Context().Procs("B"), false),
            ("主=A 次=B", P.Context().Procs("A", "B"), true),
            ("主=B 次=B", P.Context().Procs("B", "B"), true),
            ("无任何操作", P.Context().Procs(), false));

        // 并排对照:同一批场景下 anyProcedureIn 会把主操作也算上。
        Expect(P.In(ConditionKind.AnyProcedureIn, "B"),
            ("仅主操作=B", P.Context().Procs("B"), true),
            ("主=A 次=B", P.Context().Procs("A", "B"), true),
            ("无任何操作", P.Context().Procs(), false));

        Expect(P.In(ConditionKind.MainProcedureIn, "B"),
            ("仅主操作=B", P.Context().Procs("B"), true),
            ("主=A 次=B", P.Context().Procs("A", "B"), false),
            ("无任何操作", P.Context().Procs(), false));
    }

    /// <summary><c>hasOtherDiagnosis</c> 只看非主诊断,空列表为假。</summary>
    [Fact]
    public void OtherDiagnosisPresenceIgnoresTheMainDiagnosis()
    {
        Expect(P.Plain(ConditionKind.HasOtherDiagnosis),
            ("仅主诊断", P.Context().Dx("A00.000"), false),
            ("主诊断 + 1 项其他诊断", P.Context().Dx("A00.000", "B00.000"), true),
            ("主诊断 + 2 项其他诊断", P.Context().Dx("A00.000", "B00.000", "C00.000"), true));
    }

    /// <summary>
    /// <c>anyDiagnosisPrefix</c> 按前缀匹配<b>主诊断与全部其他诊断</b>,且大小写不敏感
    /// (诊断码里带规范小写 <c>x</c>,如 <c>J42.x00</c>)。
    /// </summary>
    [Fact]
    public void DiagnosisPrefixMatchesMainAndOtherDiagnoses()
    {
        var prefix = P.Prefix("I21", "I22");

        Expect(prefix,
            ("主诊断 I21.000", P.Context().Dx("I21.000"), true),
            ("主诊断 I22.900", P.Context().Dx("I22.900"), true),
            ("其他诊断 I21.000", P.Context().Dx("A00.000", "I21.000"), true),
            ("主诊断 I23.000", P.Context().Dx("I23.000"), false),
            ("其他诊断 I23.000", P.Context().Dx("A00.000", "I23.000"), false),
            ("码恰好等于前缀", P.Context().Dx("I21"), true),
            ("大小写混写", P.Context().Dx("i21.000"), true),
            ("前缀比码还长", P.Context().Dx("I2"), false));
    }

    /// <summary>
    /// 交集基数原语(<c>diagnosisIntersectCountGte</c>/<c>procedureIntersectCountGte</c>):
    /// 数的是"病例编码 ∩ 指定码表"里<b>去重后</b>的个数。
    ///
    /// <para>去重是关键 —— 官方 DSL 写的是 <c>length(S ∩ {字段})</c>,集合语义下重复码不算两次。
    /// 若实现改成"出现次数",同一码重复上报就能凑够阈值,绕开分组约束。</para>
    ///
    /// <para>这两个原语只能由 DSL 编译器产出(JSON 规则包解析不出,见
    /// <see cref="EveryJsonPrimitiveNameParsesToItsKind"/>),官方 3.0 工作簿也未使用。</para>
    /// </summary>
    [Fact]
    public void IntersectionCardinalityCountsDistinctCodes()
    {
        var dx = P.CountIn(ConditionKind.DiagnosisIntersectCountGte, 2, "A00.000", "B00.000", "C00.000");

        Expect(dx,
            ("命中 1 项", P.Context().Dx("A00.000"), false),
            ("命中 2 项", P.Context().Dx("A00.000", "B00.000"), true),
            ("命中 3 项", P.Context().Dx("A00.000", "B00.000", "C00.000"), true),
            ("同码重复两次", P.Context().Dx("A00.000", "A00.000"), false),
            ("同码大小写变体", P.Context().Dx("A00.000", "a00.000"), false),
            ("命中 1 项 + 1 项码表外", P.Context().Dx("A00.000", "Z99.999"), false));

        var proc = P.CountIn(ConditionKind.ProcedureIntersectCountGte, 2, "31.1000", "31.2000");

        Expect(proc,
            ("命中 1 项", P.Context().Procs("31.1000"), false),
            ("主 + 次各命中 1 项", P.Context().Procs("31.1000", "31.2000"), true),
            ("同码重复", P.Context().Procs("31.1000", "31.1000"), false),
            ("无操作", P.Context().Procs(), false));
    }

    /// <summary>
    /// <c>siteCountGte</c>:数的是"病案诊断落在<b>几个部位</b>的编码集里",而不是"命中几个码"。
    /// 一个部位只计一次,主诊断与任一其他诊断都能点亮该部位。
    /// </summary>
    [Fact]
    public void SiteCountAggregatesSitesHitByMainOrOtherDiagnoses()
    {
        var twoSites = P.Num(ConditionKind.SiteCountGte, 2);
        var sites = new (string, string[])[] { ("腹部", ["K35.900"]), ("胸部", ["K35.900", "S20.200"]), ("头部", ["S00.900"]) };

        Expect(twoSites,
            ("主诊断同时属腹部与胸部", P.Context().Dx("K35.900").Sites(sites), true),
            ("其他诊断补上第三个部位", P.Context().Dx("K35.900", "S00.900").Sites(sites), true),
            ("只命中头部一个部位", P.Context().Dx("S00.900").Sites(sites), false),
            ("一个部位都没命中", P.Context().Dx("Z99.999").Sites(sites), false),
            ("所在 MDC 无部位映射", P.Context().Dx("K35.900"), false));
    }

    /// <summary><c>robotAssist</c> 直接读病例标志,与操作码表无关(码表判定在引擎的预处理里)。</summary>
    [Fact]
    public void RobotAssistReadsOnlyTheCaseFlag()
    {
        Expect(P.Plain(ConditionKind.RobotAssist),
            ("含机器人辅助", P.Context().Robot(true), true),
            ("不含", P.Context().Robot(false), false));
    }

    /// <summary><c>genderIs</c> 是字符串精确比较:"1"/"2" 之外的取值一律不命中。</summary>
    [Fact]
    public void GenderComparisonIsAnExactStringMatch()
    {
        Expect(P.Gender("2"),
            ("女", P.Context().Female(), true),
            ("男", P.Context().Male(), false));

        Expect(P.Gender("1"),
            ("男", P.Context().Male(), true),
            ("女", P.Context().Female(), false));
    }

    /// <summary>
    /// 逻辑组合的空集语义与短路行为。
    ///
    /// <para><c>all</c> 空真、<c>any</c> 空假是布尔代数的标准约定,但官方 DSL 里
    /// <c>all</c> 常被当成"声明了 N 条就必须全过"的容器 —— 一旦解析期把某个子项悄悄丢掉,
    /// 空真会让整条规则变成恒真、整组病例被误收,所以这条要显式钉住。</para>
    /// </summary>
    [Fact]
    public void LogicalCombinatorsCarryTheStandardVacuityAndShortCircuit()
    {
        Expect(P.Logic(ConditionKind.All),
            ("all 无子项", P.Context(), true));
        Expect(P.Logic(ConditionKind.Any),
            ("any 无子项", P.Context(), false));
        Expect(P.Logic(ConditionKind.True),
            ("always", P.Context(), true));
        Expect(P.Logic(ConditionKind.Not, P.Logic(ConditionKind.True)),
            ("not(always)", P.Context(), false));

        // 短路:any 的第一个子项成立就不再判第二个;all 的第一个子项失败即停。
        var any = P.Logic(ConditionKind.Any,
            P.In(ConditionKind.MainDiagnosisIn, "A00.000"),
            P.Num(ConditionKind.AgeGte, 999));
        Expect(any,
            ("any 首项成立、次项不成立", P.Context().Dx("A00.000").Age(30), true),
            ("两项都不成立", P.Context().Dx("Z99.999").Age(30), false));

        var all = P.Logic(ConditionKind.All,
            P.Num(ConditionKind.AgeGte, 70),
            P.In(ConditionKind.MainDiagnosisIn, "A00.000"));
        Expect(all,
            ("all 两项都成立", P.Context().Age(70).Dx("A00.000"), true),
            ("all 首项不成立", P.Context().Age(69).Dx("A00.000"), false),
            ("all 次项不成立", P.Context().Age(70).Dx("Z99.999"), false));

        // 嵌套取反:not(not(x)) 等价于 x。
        Expect(P.Logic(ConditionKind.Not, P.Logic(ConditionKind.Not,
                P.In(ConditionKind.MainDiagnosisIn, "A00.000"))),
            ("双重否定", P.Context().Dx("A00.000"), true),
            ("双重否定(不命中)", P.Context().Dx("Z99.999"), false));
    }

    // ==================================================================
    // 二、JSON 原语名 → 原语的映射完整性
    // ==================================================================

    /// <summary>
    /// 每个 JSON 原语名都能解析成对应的 <see cref="ConditionKind"/>,且关键字段落到了正确的位置。
    ///
    /// <para>JSON 轨(非官方工作簿轨)的规则全靠这张名字表,而它是一张手写字典 ——
    /// 拼错一个键(如 <c>weightGTE</c>)不会被编译器发现,只会让规则包装载时报"未知条件原语",
    /// 或者更糟:名字拼成另一个存在的原语而静默走错分支。逐条列出名字与期望种类,
    /// 既挡住拼写漂移,也顺带物证每个名字对应的字段形态(<c>codes</c>/<c>value</c>/<c>prefixes</c>)。</para>
    /// </summary>
    [Fact]
    public void EveryJsonPrimitiveNameParsesToItsKind()
    {
        foreach (var (kind, json) in JsonPrimitives)
        {
            var parsed = Parse(json);
            Assert.True(kind == parsed.Kind, $"JSON \"{json}\" 解析成了 {parsed.Kind},期望 {kind}");

            // 顺带物证字段落位:码表类必须有码、数值类必须有阈值、前缀类必须有前缀、性别类必须有值。
            switch (parsed.Kind)
            {
                case ConditionKind.MainDiagnosisIn or ConditionKind.AnyDiagnosisIn
                    or ConditionKind.OtherDiagnosisIn or ConditionKind.MainProcedureIn
                    or ConditionKind.AnyProcedureIn or ConditionKind.AnyOtherProcedureIn:
                    Assert.NotEmpty(parsed.CodeSet);
                    break;
                case ConditionKind.AnyDiagnosisPrefix:
                    Assert.NotEmpty(parsed.Prefixes);
                    break;
                case ConditionKind.AgeGte or ConditionKind.AgeGt or ConditionKind.AgeLte
                    or ConditionKind.AgeEq or ConditionKind.AgeLt or ConditionKind.AgeNot
                    or ConditionKind.AgeDayGte or ConditionKind.AgeDayLt
                    or ConditionKind.AgeDayLte or ConditionKind.AgeDayGt
                    or ConditionKind.WeightGte or ConditionKind.WeightGt
                    or ConditionKind.WeightLte or ConditionKind.WeightLt
                    or ConditionKind.ProcedureCountGte or ConditionKind.SiteCountGte:
                    Assert.True(parsed.Value > 0, $"{json} 的 value 没落到 Value 上");
                    break;
                case ConditionKind.GenderIs:
                    Assert.Contains(parsed.ExpectedValue, new[] { "1", "2" });
                    break;
            }
        }

        // 除两个 DSL 专用原语外,名字表必须覆盖全部原语 —— 少一个就说明有人加了原语忘了加名字。
        var dslOnly = new[] { ConditionKind.ProcedureIntersectCountGte, ConditionKind.DiagnosisIntersectCountGte };
        Assert.Equal(Enum.GetValues<ConditionKind>().Length - dslOnly.Length, JsonPrimitives.Length);
    }

    // ==================================================================
    // 三、普查:哪些原语还没有官方用例
    // ==================================================================

    /// <summary>
    /// 本文件手工维护的"官方数据未使用"清单(<b>只应该往里增减,不应该靠猜</b>)。
    /// 官方数据开始用上某个原语时,<see cref="UnusedPrimitiveCensusMatchesTheData"/> 会红。
    /// </summary>
    private static readonly ConditionKind[] ExpectedUnusedInOfficialData =
    [
        ConditionKind.AnyDiagnosisPrefix,
        ConditionKind.AnyOtherProcedureIn,
        ConditionKind.ProcedureCountGte,
        ConditionKind.HasProcedure,
        ConditionKind.NoValidMainProcedure,
        ConditionKind.HasValidProcedure,
        ConditionKind.HasOtherDiagnosis,
        ConditionKind.AgeGt,
        ConditionKind.AgeLt,
        ConditionKind.AgeNot,
        ConditionKind.AgeDayLte,
        ConditionKind.AgeDayGt,
        ConditionKind.DiagnosisIntersectCountGte,
        ConditionKind.SiteCountGte,
        ConditionKind.RobotAssist,
    ];

    /// <summary>
    /// 官方数据实际用到的原语,必须恰好是全部原语减去上表。
    ///
    /// <para>这条断言是"零覆盖清单"的保鲜机制:官方换数据批次后若某个原语开始被使用,
    /// 这里会红并提示把它从上表划掉(它已经由 <c>BoundaryCaseTests</c>/<c>RegressionTests</c>
    /// 覆盖了);若引擎新增了原语却没人补用例,这里同样会红。</para>
    /// </summary>
    [Fact]
    public void UnusedPrimitiveCensusMatchesTheData()
    {
        var all = Enum.GetValues<ConditionKind>();
        var used = OfficialPack.UsedPrimitives;
        var unused = all.Where(k => !used.Contains(k)).ToHashSet();
        var expected = ExpectedUnusedInOfficialData.ToHashSet();

        Assert.True(used.Count + unused.Count == all.Length,
            "普查把原语漏掉了几个,UsedPrimitives 的遍历没覆盖全部条件树");

        var startedUsing = expected.Except(unused).OrderBy(k => k).ToList();
        Assert.True(startedUsing.Count == 0,
            $"以下原语现在官方数据已经用到了,请从 ExpectedUnusedInOfficialData 里删掉:"
            + string.Join(",", startedUsing));

        var newlyUnused = unused.Except(expected).OrderBy(k => k).ToList();
        Assert.True(newlyUnused.Count == 0,
            $"官方数据不再使用以下原语(或引擎新增了原语),请为本文件补上用例后再登记到清单里:"
            + string.Join(",", newlyUnused));
    }

    // ==================================================================
    // 辅助:断言表与 JSON 名字表
    // ==================================================================

    /// <summary>逐场景断言同一棵条件树的求值结果。一次列出全部场景,失败信息里带完整场景描述。</summary>
    private static void Expect(Condition condition, params (string Scene, Ctx Context, bool Expected)[] rows)
    {
        var failures = new List<string>();
        foreach (var (scene, context, expected) in rows)
        {
            var actual = ConditionEvaluator.Evaluate(condition, context.Build());
            if (actual != expected)
                failures.Add($"  {scene} → {context.Describe()} 期望 {expected},实际 {actual}");
        }

        Assert.True(failures.Count == 0,
            $"{Describe(condition)} 的语义与预期不符:\n" + string.Join("\n", failures));
    }

    private static Condition Parse(string json) =>
        ConditionParser.Parse(JsonDocument.Parse(json).RootElement, "test");

    /// <summary>条件树的紧凑描述,用于断言失败信息。</summary>
    private static string Describe(Condition c) => c.Kind switch
    {
        ConditionKind.All => "all(" + string.Join(" & ", c.Children.Select(Describe)) + ")",
        ConditionKind.Any => "any(" + string.Join(" | ", c.Children.Select(Describe)) + ")",
        ConditionKind.Not => "not(" + Describe(c.Children[0]) + ")",
        ConditionKind.True => "always",
        ConditionKind.AnyDiagnosisPrefix => $"anyDiagnosisPrefix[{string.Join(",", c.Prefixes)}]",
        ConditionKind.GenderIs => $"genderIs({c.ExpectedValue})",
        ConditionKind.ProcedureIntersectCountGte or ConditionKind.DiagnosisIntersectCountGte =>
            $"{c.Kind}≥{c.Value:0}[{c.Codes.Count} 码]",
        _ => c.Codes.Count > 0
            ? $"{c.Kind}[{c.Codes.Count} 码]"
            : c.Value != 0 ? $"{c.Kind}({c.Value:0})" : c.Kind.ToString(),
    };

    /// <summary>
    /// 全部 36 个可由 JSON 表达的原语名(另外 2 个 <c>*IntersectCountGte</c> 是 DSL 编译器专用,
    /// <see cref="ConditionParser"/> 不接受它们的 <c>codes</c>/<c>value</c>,故不在表内)。
    /// </summary>
    private static readonly (ConditionKind Kind, string Json)[] JsonPrimitives =
    [
        (ConditionKind.True, """{"kind":"always"}"""),
        (ConditionKind.All, """{"kind":"all","of":[{"kind":"always"}]}"""),
        (ConditionKind.Any, """{"kind":"any","of":[{"kind":"always"}]}"""),
        (ConditionKind.Not, """{"kind":"not","of":{"kind":"always"}}"""),

        (ConditionKind.MainDiagnosisIn, """{"kind":"mainDiagnosisIn","codes":["A00.000"]}"""),
        (ConditionKind.AnyDiagnosisIn, """{"kind":"anyDiagnosisIn","codes":["A00.000"]}"""),
        (ConditionKind.OtherDiagnosisIn, """{"kind":"otherDiagnosisIn","codes":["A00.000"]}"""),
        (ConditionKind.AnyDiagnosisPrefix, """{"kind":"anyDiagnosisPrefix","prefixes":["I21"]}"""),

        (ConditionKind.MainProcedureIn, """{"kind":"mainProcedureIn","codes":["31.1000"]}"""),
        (ConditionKind.AnyProcedureIn, """{"kind":"anyProcedureIn","codes":["31.1000"]}"""),
        (ConditionKind.AnyOtherProcedureIn, """{"kind":"anyOtherProcedureIn","codes":["31.1000"]}"""),
        (ConditionKind.ProcedureCountGte, """{"kind":"procedureCountGte","value":2}"""),
        (ConditionKind.HasProcedure, """{"kind":"hasProcedure"}"""),
        (ConditionKind.NoValidMainProcedure, """{"kind":"noValidMainProcedure"}"""),
        (ConditionKind.HasValidProcedure, """{"kind":"hasValidProcedure"}"""),

        (ConditionKind.HasOtherDiagnosis, """{"kind":"hasOtherDiagnosis"}"""),
        (ConditionKind.HasMcc, """{"kind":"hasMcc"}"""),
        (ConditionKind.HasCc, """{"kind":"hasCc"}"""),
        (ConditionKind.HasCcOrMcc, """{"kind":"hasCcOrMcc"}"""),

        (ConditionKind.AgeGte, """{"kind":"ageGte","value":70}"""),
        (ConditionKind.AgeGt, """{"kind":"ageGt","value":70}"""),
        (ConditionKind.AgeLte, """{"kind":"ageLte","value":6}"""),
        (ConditionKind.AgeEq, """{"kind":"ageEq","value":28}"""),
        (ConditionKind.AgeLt, """{"kind":"ageLt","value":1}"""),
        (ConditionKind.AgeNot, """{"kind":"ageNot","value":28}"""),
        (ConditionKind.AgeDayGte, """{"kind":"ageDayGte","value":29}"""),
        (ConditionKind.AgeDayLt, """{"kind":"ageDayLt","value":29}"""),
        (ConditionKind.AgeDayLte, """{"kind":"ageDayLte","value":3}"""),
        (ConditionKind.AgeDayGt, """{"kind":"ageDayGt","value":29}"""),
        (ConditionKind.WeightGte, """{"kind":"weightGte","value":2000}"""),
        (ConditionKind.WeightGt, """{"kind":"weightGt","value":2000}"""),
        (ConditionKind.WeightLte, """{"kind":"weightLte","value":2499}"""),
        (ConditionKind.WeightLt, """{"kind":"weightLt","value":2499}"""),
        (ConditionKind.GenderIs, """{"kind":"genderIs","value":"2"}"""),

        (ConditionKind.SiteCountGte, """{"kind":"siteCountGte","value":2}"""),
        (ConditionKind.RobotAssist, """{"kind":"robotAssist"}"""),
    ];
}
