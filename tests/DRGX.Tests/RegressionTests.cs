using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>
/// 正例回归:逐条回放 <c>cases/witnesses.jsonl</c>,断言引擎的落点与冻结基线一致。
///
/// <para><b>两类正例</b>:</para>
/// <list type="bullet">
/// <item><c>witness</c>(825 条)—— 每个正式细分组各一条,证明该组可达且落点就是它自己;</item>
/// <item><c>ambiguous</c>(21 条)—— 21 个 QY 歧义组各一条:主手术有效但不属该 MDC 任何手术组,
/// 期望落 <c>Ambiguous/{MDC}QY</c> 而非 <c>Success</c>。</item>
/// </list>
///
/// <para><b>为什么用例 Id 是理论参数而不是把整条用例塞进去。</b>见 <see cref="CaseStore"/>。</para>
///
/// <para><b>这些用例不是手写的。</b>它们由 <c>Generation/</c> 下的规则反推器从每条
/// 落位规则的条件树反推出满足它的病案,再经真实引擎验证落点后冻结。<b>不要手工修改
/// cases/*.jsonl</b> —— 改了就成了"我以为引擎会这样",而不是"引擎确实这样"。要更新就跑
/// <c>DRGX_WRITE_CASES=1 dotnet test tests/DRGX.Tests --filter Category=Regen</c>。</para>
/// </summary>
public class RegressionTests
{
    [Theory]
    [MemberData(nameof(CaseStore.WitnessIds), MemberType = typeof(CaseStore))]
    public void WitnessLandsOnItsFrozenGroup(string id)
    {
        var c = CaseStore.Witness(id);

        // 先断言"这条用例想证明什么"仍然成立,再断言"引擎还跟基线一样"。
        // 顺序重要:前者失败说明这条用例本身站不住(基线被改坏),
        // 后者失败才是引擎行为漂移 —— 两种原因的处置完全不同。
        switch (c.Kind)
        {
            case CaseKinds.Witness:
                OutcomeCheck.LandsOn(c, GroupStatus.Success, c.Target!);
                break;
            case CaseKinds.Ambiguous:
                OutcomeCheck.LandsOn(c, GroupStatus.Ambiguous, c.Target!);
                break;
            default:
                Assert.Fail($"{c.Id} 出现在 witnesses.jsonl 里,但类别是 {c.Kind}");
                break;
        }

        OutcomeCheck.MatchesFrozenExpectation(c);
    }

    /// <summary>
    /// QY 期望必须是官方白名单内的 <c>{MDC}QY</c>,且与病案所属 MDC 一致。
    ///
    /// <para>QY 是官方白名单(21 个 MDC,B~Z,不含 A/S/T/X/Y),<b>不能</b>按 <c>{MDC}QY</c>
    /// 拼接推导 —— 这条断言把"白名单"这件事钉在数据上,防止有人把 QY 改成"任意 MDC 拼一拼"。</para>
    /// </summary>
    [Fact]
    public void AmbiguousCasesStayWithinTheOfficialWhitelist()
    {
        var cases = CaseStore.Witnesses.Where(c => c.Kind == CaseKinds.Ambiguous).ToList();
        Assert.NotEmpty(cases);

        foreach (var c in cases)
        {
            Assert.Contains(c.Target!, OfficialPack.AmbiguousGroups);
            Assert.Equal(GroupStatus.Ambiguous, c.Expect.Status);
            Assert.Equal(c.Target, c.Expect.Code);
            // 期望码 = 病案落位 MDC + "QY";病案记录的 Target 就是它。
            Assert.EndsWith("QY", c.Target!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 全局兜底 <c>0000</c> 永远不能被判成"分组成功"。
    ///
    /// <para>它是一条<b>刻意判为未入组</b>的汇总行:若哪天代码让它变成 <c>Success/0000</c>,
    /// 全院的分组成功率会凭空变好看,而真正的未入组病例全部失踪。这条断言把该语义钉住。</para>
    /// </summary>
    [Fact]
    public void GlobalFallbackIsNeverReportedAsAGroup()
    {
        foreach (var c in CaseStore.Witnesses.Concat(CaseStore.Boundary))
            if (string.Equals(c.Expect.Code, OfficialPack.GlobalFallbackCode, StringComparison.OrdinalIgnoreCase))
                Assert.Fail($"{c.Id} 的冻结期望落点成了全局兜底码 {OfficialPack.GlobalFallbackCode}");
    }
}
