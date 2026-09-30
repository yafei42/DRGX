using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>把"引擎回放一条用例"与"比对冻结期望"合成一步,保证三个回放文件给出同一套失败信息。</summary>
internal static class OutcomeCheck
{
    /// <summary>用真实引擎回放一条用例,落点与冻结期望不一致即失败。</summary>
    public static void MatchesFrozenExpectation(TestCase c)
    {
        var outcome = OfficialPack.Engine.Group(c.ToRecord());
        var actual = new ExpectedOutcome(outcome.Status, outcome.Code, outcome.Reason);
        if (c.Expect == actual) return;

        var detail = outcome.ReasonText is { Length: > 0 } t ? $"\n  引擎说明: {t}" : "";
        Assert.Fail(
            $"{c.Id} 的落点与冻结基线不一致\n" +
            $"  期望: {c.Expect.Describe()}\n" +
            $"  实际: {actual.Describe()}{detail}\n" +
            $"  病案: {GroupCoverageTests.Describe(c)}\n" +
            $"  说明: {c.Note}");
    }

    /// <summary>回放并要求落点等于给定码 —— 用于 witness 这类"用例存在的意义就是证明能落到这里"的场景。</summary>
    public static void LandsOn(TestCase c, GroupStatus status, string code)
    {
        var outcome = OfficialPack.Engine.Group(c.ToRecord());
        Assert.True(outcome.Status == status && string.Equals(outcome.Code, code, StringComparison.OrdinalIgnoreCase),
            $"{c.Id} 应落 {status}/{code},实际 {outcome.Status}/{outcome.Code}\n" +
            $"  病案: {GroupCoverageTests.Describe(c)}\n" +
            $"  说明: {c.Note}");
    }
}
