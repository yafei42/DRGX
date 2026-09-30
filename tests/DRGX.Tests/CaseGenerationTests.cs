using DRGX.Tests.Generation;

namespace DRGX.Tests;

/// <summary>只在设了 <c>DRGX_WRITE_CASES=1</c> 时才执行的测试(xunit 2.x 无动态跳过,
/// 因此按"发现期跳过"实现)。</summary>
internal sealed class RegenFactAttribute : FactAttribute
{
    public RegenFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DRGX_WRITE_CASES") != "1")
            Skip = "用例基线生成默认关闭(要跑几十万次引擎探针)。重新生成:DRGX_WRITE_CASES=1 + Category=Regen。";
    }
}

/// <summary>
/// 用例基线(重)生成入口。
///
/// <para><b>为什么把生成器放进测试工程,而不是单独一个命令行工具。</b>用例有两个消费者:
/// 生成器(写)和闸门(读)。分成两个工程就必然有一个"两边都要跟着改"的契约,而契约一旦漂移,
/// 表现是闸门读着旧格式静默通过 —— 比没有闸门更危险。放在一起之后,只有一条代码路径,
/// 契约就是同一批类型。</para>
///
/// <para><b>默认不跑。</b>生成要跑几十万次引擎探针,而日常回归只需要回放已冻结的基线。
/// 因此本测试默认跳过,只有显式设了环境变量才执行:</para>
/// <code>
///   DRGX_WRITE_CASES=1 dotnet test tests/DRGX.Tests --filter Category=Regen
/// </code>
/// <para>写入 <c>tests/DRGX.Tests/cases/{witnesses,boundary}.jsonl</c> 与 <c>unreachable.txt</c>,
/// 三者必须一起提交 —— 闸门同时依赖它们(witness 提供正例,unreachable 提供剔除依据)。</para>
/// </summary>
public class CaseGenerationTests
{
    [RegenFact]
    [Trait("Category", "Regen")]
    public void Regenerate()
    {

        var (witnesses, unreachable) = CaseGenerator.GenerateWitnesses();
        var ambiguous = CaseGenerator.GenerateAmbiguous();

        var witnessCases = witnesses.Select(BuildWitnessCase).Concat(ambiguous.Select(BuildAmbiguousCase)).ToList();
        var boundaryCases = BoundaryMutator.DeriveAll(witnesses.Concat(ambiguous)).ToList();

        CaseLibrary.WriteWitnesses(witnessCases);
        CaseLibrary.WriteBoundary(boundaryCases);
        CaseLibrary.WriteUnreachable(unreachable);

        var official = witnessCases.Select(c => c.Target!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = OfficialPack.OfficialGroups
            .Concat(OfficialPack.PlaceholderGroups)
            .Except(official, StringComparer.OrdinalIgnoreCase)
            .Except(unreachable.Select(u => u.Code), StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(missing.Count == 0,
            $"生成结束仍有 {missing.Count} 个可达组没有 witness,且不在 unreachable 清单里:{string.Join(",", missing)}");

        Assert.Equal(witnesses.Count + ambiguous.Count, witnessCases.Count);
        Assert.True(boundaryCases.Count > 0, "边界探针一条都没生成,派生逻辑可能失效");
    }

    private static TestCase BuildWitnessCase(WitnessEvidence w) => new()
    {
        Id = $"w:{w.Target}",
        Kind = CaseKinds.Witness,
        Target = w.Target,
        Note = $"{w.Mdc.Code}/{w.Adrg.Code} 落位规则({w.Strategy}):{w.Split.Origin}",
        Gender = w.Record.Gender ?? "1",
        Age = w.Record.Age,
        AgeDay = w.Record.AgeDay,
        Weight = w.Record.Weight,
        Diagnoses = w.Record.Diagnoses,
        Procedures = w.Record.Procedures,
        Expect = new ExpectedOutcome(w.Outcome.Status, w.Outcome.Code, w.Outcome.Reason),
    };

    private static TestCase BuildAmbiguousCase(WitnessEvidence w) => new()
    {
        Id = $"qy:{w.Target}",
        Kind = CaseKinds.Ambiguous,
        Target = w.Target,
        Note = $"{w.Mdc.Code} 歧义病案:主手术有效但不属该 MDC 任何手术组",
        Gender = w.Record.Gender ?? "1",
        Age = w.Record.Age,
        AgeDay = w.Record.AgeDay,
        Weight = w.Record.Weight,
        Diagnoses = w.Record.Diagnoses,
        Procedures = w.Record.Procedures,
        Expect = new ExpectedOutcome(w.Outcome.Status, w.Outcome.Code, w.Outcome.Reason),
    };
}
