using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>
/// 边界回归:逐条回放 <c>cases/boundary.jsonl</c>,断言"把某个事实挪到边界另一侧之后,
/// 引擎的落点跟冻结基线一模一样"。
///
/// <para><b>为什么边界用例值得单列一个文件、而不是混进正例。</b>正例失败意味着"某个组到不了了",
/// 一眼能看懂;边界用例失败往往意味着<b>某个阈值写错了等号</b> —— 组还到得了,只是多收/少收了
/// 一批本该在另一档的病例。这类改动不会让任何正例变红,只会静默改变 thousands of 病例的支付结果。
/// 单列一个文件之后,失败列表本身就是"哪些边界动了"的清单。</para>
///
/// <para><b>两种期望</b>:</para>
/// <list type="bullet">
/// <item><c>boundary-violate</c> —— 挪到阈值外侧/去掉关键事实后,落点<b>不再是</b>原组。
/// 这是边界语义的主证:证明该条件确实在起作用。</item>
/// <item><c>boundary-hold</c> —— 挪完仍落原组。说明存在等价路径(例如"伴/不伴并发症"两档
/// 都能命中),或是把某条数值推到了<b>紧邻内侧</b>。这类"看着该变却没变"的用例最容易被
/// 后续改动静默破坏,所以一并冻结而不是丢弃。</item>
/// </list>
///
/// <para><b>用例 Id 里带了变异说明</b>(<c>bv:AA19:dropproc1</c> = 目标 AA19、去掉 1 项操作),
/// 失败时可以直接从测试名读出动的是哪一格,不必先打开 JSONL。</para>
/// </summary>
public class BoundaryCaseTests
{
    [Theory]
    [MemberData(nameof(CaseStore.BoundaryIds), MemberType = typeof(CaseStore))]
    public void BoundaryMutationLandsWhereFrozen(string id)
    {
        var c = CaseStore.BoundaryCase(id);

        Assert.True(c.Kind is CaseKinds.BoundaryHold or CaseKinds.BoundaryViolate,
            $"{c.Id} 出现在 boundary.jsonl 里,但类别是 {c.Kind}");
        Assert.False(string.IsNullOrWhiteSpace(c.Target), $"{c.Id} 缺少 Target —— 边界用例必须挂在某个 DRG 组上");

        OutcomeCheck.MatchesFrozenExpectation(c);
    }

    /// <summary>
    /// "边界失效"那一类必须真的失效 —— 期望落点不得等于目标组。
    ///
    /// <para>否则 <c>boundary-violate</c> 这个名字是骗人的:派生器把落点还算成"变了"的用例
    /// 标成了 violate,人读基线时会以为"这个条件在起作用",实际什么都没证。</para>
    /// </summary>
    [Fact]
    public void ViolatingProbesReallyLeaveTheTargetGroup()
    {
        var lying = new List<string>();
        foreach (var c in CaseStore.Boundary.Where(c => c.Kind == CaseKinds.BoundaryViolate))
        {
            bool stillTarget = c.Expect.Status == GroupStatus.Success
                               && string.Equals(c.Expect.Code, c.Target, StringComparison.OrdinalIgnoreCase);
            if (stillTarget) lying.Add($"{c.Id} (target={c.Target})");
        }

        Assert.True(lying.Count == 0,
            $"以下用例标为 boundary-violate,但期望落点仍是目标组:{string.Join(",", lying.Take(20))}");
    }

    /// <summary>
    /// 同理,"边界保持"那一类必须真的还是目标组。
    /// <para>两个方向都锁上,派生器的分类逻辑就不可能悄悄退化成一侧失效。</para>
    /// </summary>
    [Fact]
    public void HoldingProbesReallyStayInTheTargetGroup()
    {
        var lying = new List<string>();
        foreach (var c in CaseStore.Boundary.Where(c => c.Kind == CaseKinds.BoundaryHold))
        {
            bool stillTarget = c.Expect.Status == GroupStatus.Success
                               && string.Equals(c.Expect.Code, c.Target, StringComparison.OrdinalIgnoreCase);
            if (!stillTarget) lying.Add($"{c.Id} (target={c.Target}, 实际 {c.Expect.Describe()})");
        }

        Assert.True(lying.Count == 0,
            $"以下用例标为 boundary-hold,但期望落点已不是目标组:{string.Join(",", lying.Take(20))}");
    }

    /// <summary>
    /// 边界探针必须覆盖到各个变异族,而不只是"去掉主手术"这一种。
    ///
    /// <para>派生器历史上只产出了 999 条、且清一色是手术族 —— 症状是
    /// "看着有 999 条边界用例",实际上内科组一条都没有。这条按变异族统计并逐族设下限,
    /// 把"数量涨了但族没涨"这种伪覆盖拦住。</para>
    /// </summary>
    [Fact]
    public void BoundaryCoverageSpansEveryMutationFamily()
    {
        var cases = CaseStore.Boundary;
        int numeric = cases.Count(c => c.Id.Contains("gte", StringComparison.Ordinal)
                                       || c.Id.Contains("gt", StringComparison.Ordinal)
                                       || c.Id.Contains("lte", StringComparison.Ordinal)
                                       || c.Id.Contains("lt", StringComparison.Ordinal)
                                       || c.Id.Contains("eq", StringComparison.Ordinal)
                                       || c.Id.Contains("ne", StringComparison.Ordinal));
        int complication = cases.Count(c => c.Id.Contains("comp", StringComparison.Ordinal));
        int procedure = cases.Count(c => c.Id.Contains("proc", StringComparison.Ordinal));
        int diagnosis = cases.Count(c => c.Id.Contains("dx", StringComparison.Ordinal));
        int gender = cases.Count(c => c.Id.Contains("gender", StringComparison.Ordinal));

        Assert.True(numeric >= 100, $"数值阈值族只有 {numeric} 条边界探针(年龄/日龄/体重)");
        Assert.True(complication >= 100, $"并发症族只有 {complication} 条边界探针");
        Assert.True(procedure >= 100, $"手术操作族只有 {procedure} 条边界探针");
        Assert.True(diagnosis >= 100, $"诊断族只有 {diagnosis} 条边界探针");
        Assert.True(gender >= 10, $"性别族只有 {gender} 条边界探针");
    }
}
