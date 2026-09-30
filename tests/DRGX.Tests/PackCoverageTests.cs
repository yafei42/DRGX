using System.Collections.Frozen;
using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>
/// 数据包**可达性派生**的回归闸门(<see cref="PackCoverage"/>)。
///
/// <para><b>为什么值得钉住。</b>这些派生不参与分组判定,错了不会让任何一条病案落错组 ——
/// 但它们决定"哪些码被当成可达",而覆盖率告警与测试侧的剔除清单都读它。两条装载轨
/// (官方工作簿 / 旧 JSON)曾经各写一遍、判据还不一样(大小写、是否排 QY),测试侧又独立
/// 推导了第三遍。三份推导里任何一份走偏都不会有任何信号,直到有人拿它对账。</para>
/// </summary>
public class PackCoverageTests
{
    /// <summary>机器人辅助手术触发码(官方 OP_ARB,T005 五码)。</summary>
    private static readonly string[] RobotCodes =
        ["17.4100", "17.4200", "17.4300", "17.4400", "17.4500"];

    /// <summary>
    /// 官方包应当**完全可达**:组表 871 行 = 850 条 split 码 + 21 个 QY 白名单码。
    /// 这条把"组表里没有孤儿码"钉死 —— 一旦有码从规则里消失(改坏了 rules / 换了工作簿),
    /// 它会变成非空,而不是安静地留在包里。
    /// </summary>
    [Fact]
    public void OfficialPackHasNoUnreachableGroup()
    {
        var pack = OfficialPack.Data;

        Assert.Empty(pack.UnreachableGroups);

        var splitCodes = pack.MdcChain.SelectMany(m => m.Adrgs).SelectMany(a => a.Splits)
            .Select(s => s.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(850, splitCodes);
        Assert.Equal(21, pack.QyRules.Count);
        Assert.Equal(pack.Groups.Count, splitCodes + pack.QyRules.Count);   // 871 = 850 + 21
        Assert.Equal(pack.Groups.Count, pack.EmittedCodes.Count);
    }

    /// <summary>
    /// QY 白名单动态产出的 <c>{MDC}QY</c> 必须算进可达码。
    ///
    /// <para>它们不是任何 split 的码,而是运行时按 <c>{MDC}QY</c> 拼出来的。漏掉这一条,
    /// 21 个 QY 组会被误报成"不可达"—— 这正是两轨原先分叉的地方(官方轨按后缀排除了它们,
    /// JSON 轨没有)。</para>
    /// </summary>
    [Fact]
    public void EmittedCodesIncludeTheDynamicallyProducedQyGroups()
    {
        var pack = OfficialPack.Data;
        Assert.NotEmpty(pack.QyRules);

        foreach (var mdc in pack.QyRules.Keys)
            ShouldContain(pack.EmittedCodes, mdc + "QY", "可达码");
    }

    /// <summary>
    /// 机器人辅助手术码**不是**"非分组操作":官方 3.0 把机器人直赋写成 DRG 分档规则的一部分
    /// (<c>{ZYSS,QTSS} in OP_ARB</c>),把它们当非分组码剔除掉,规则就永远命中不了。
    /// 对照项是 17.4901(康复机器人):同属 17.4 家族但不在 OP_ARB,必须保留剔除 ——
    /// 这条同时挡住"按 17.4 前缀一刀切"的改法。
    /// </summary>
    [Fact]
    public void RobotTriggerCodesAreNotTreatedAsNonGrouping()
    {
        var pack = OfficialPack.Data;

        Assert.Equal(
            RobotCodes.OrderBy(c => c, StringComparer.Ordinal),
            pack.RobotProcedures.OrderBy(c => c, StringComparer.Ordinal));

        foreach (var code in RobotCodes)
        {
            ShouldNotContain(pack.NonGroupingProcedures, code, "非分组操作");
            ShouldContain(pack.ValidProcedures.Union(pack.RobotProcedures), code, "有效操作∪机器人码");
        }

        // 17.4901 不在 OP_ARB:官方不给它直赋资格,故仍是非分组操作
        ShouldNotContain(pack.RobotProcedures, "17.4901", "机器人触发码");
        ShouldContain(pack.NonGroupingProcedures, "17.4901", "非分组操作");
    }

    /// <summary><see cref="PackCoverage.NonGroupingOf"/> 只按**码表成员资格**减,不按前缀。</summary>
    [Fact]
    public void NonGroupingOfSubtractsOnlyExactMembers()
    {
        var robot = FrozenSet.ToFrozenSet(RobotCodes, StringComparer.OrdinalIgnoreCase);
        var declared = new[] { "17.4100", "17.4900", "17.4900x001", "17.4901", "99.9900" };

        var result = PackCoverage.NonGroupingOf(declared, robot);

        ShouldNotContain(result, "17.4100", "非分组操作");
        ShouldContain(result, "17.4900", "非分组操作");
        ShouldContain(result, "17.4900x001", "非分组操作");
        ShouldContain(result, "17.4901", "非分组操作");
        ShouldContain(result, "99.9900", "非分组操作");
        Assert.Equal(4, result.Count);
    }

    /// <summary>
    /// 可达码与"不可达"判定一律大小写不敏感 —— 组码是编码,<c>ab1</c> 与 <c>AB1</c> 是同一个组。
    /// 两轨原先一处敏感一处不敏感,这条把口径钉成同一个。
    /// </summary>
    [Fact]
    public void CoverageComparisonsIgnoreCase()
    {
        var chain = new[]
        {
            new CompiledMdc
            {
                Code = "A",
                Gate = new Condition { Kind = ConditionKind.True },
                Adrgs =
                [
                    new CompiledAdrg
                    {
                        Code = "AA1",
                        Entries = [],
                        Splits = [new CompiledSplit("AB1", null, "组名")],
                        ProcedureDriven = false,
                    },
                ],
            },
        };

        var emitted = PackCoverage.Emitted(chain, ["B"]);
        ShouldContain(emitted, "ab1", "可达码");
        ShouldContain(emitted, "bqy", "可达码");
        Assert.Equal(2, emitted.Count);

        // 组表里写的是另一种大小写,仍算可达
        Assert.Empty(PackCoverage.Unreachable(["AB1", "BQY"], emitted));
        Assert.Equal(["ZZ9"], PackCoverage.Unreachable(["AB1", "ZZ9"], emitted));
    }

    // FrozenSet<T> 同时实现 ISet<T> 与 IReadOnlySet<T>,xUnit 的 Assert.Contains 会二义;
    // 这两个helper 把参数收成 IEnumerable<string>,走实例方法 Contains(即集合自己的比较器)。
    private static void ShouldContain(IEnumerable<string> set, string code, string what) =>
        Assert.True(set.Contains(code), $"「{code}」应在{what}内,实际不在");

    private static void ShouldNotContain(IEnumerable<string> set, string code, string what) =>
        Assert.False(set.Contains(code), $"「{code}」不应在{what}内,实际在");
}
