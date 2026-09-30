using System.Text.Json.Serialization;
using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>用例类别。取值是机读契约,只增不改。</summary>
internal static class CaseKinds
{
    /// <summary>某 DRG 落位规则的代表病案(正例:期望落点 = <see cref="TestCase.Target"/>)。</summary>
    public const string Witness = "witness";
    /// <summary>歧义病案(QY)正例:期望 Status=Ambiguous、Code={MDC}QY。</summary>
    public const string Ambiguous = "ambiguous";
    /// <summary>边界探针:由 witness 派生,期望落点与 witness **不同**(踩到阈值外/去掉事实)。</summary>
    public const string BoundaryViolate = "boundary-violate";
    /// <summary>边界探针:由 witness 派生,期望落点与 witness **相同**(恰在阈值上,含等号的一侧)。</summary>
    public const string BoundaryHold = "boundary-hold";
}

/// <summary>期望结果。冻结在用例文件里,由引擎回放比对——引擎改动的语义漂移会在这里红。</summary>
internal sealed record ExpectedOutcome(GroupStatus Status, string? Code, UngroupedReason Reason)
{
    public string Describe() => Status switch
    {
        GroupStatus.Success => $"Success/{Code}",
        GroupStatus.Ambiguous => $"Ambiguous/{Code}",
        _ => $"{Status}/{Reason}",
    };
}

/// <summary>
/// 一条测试用例:<b>输入病案 + 冻结的期望落点</b>。
///
/// <para>刻意只存引擎真正消费的字段(Gender/Age/AgeDay/Weight/诊断/操作)。
/// Dept/InHospitalDays/LeavingType 当前方案不使用,存进来只会让"为什么这条用例
/// 期望这个落点"多一层无法解释的输入。</para>
/// </summary>
internal sealed record TestCase
{
    /// <summary>稳定标识(类别:目标码:序号)。同一目标的多条探针靠它区分。</summary>
    public required string Id { get; init; }
    /// <summary><see cref="CaseKinds"/> 之一。</summary>
    public required string Kind { get; init; }
    /// <summary>本条用例针对的 DRG 码(witness/boundary);QY 用例为 {MDC}QY。</summary>
    public string? Target { get; init; }
    /// <summary>生成说明(哪条条件的哪一侧边界)。人读;不参与断言。</summary>
    public string? Note { get; init; }
    public required string Gender { get; init; }
    public int? Age { get; init; }
    public int? AgeDay { get; init; }
    public int? Weight { get; init; }
    public required IReadOnlyList<string> Diagnoses { get; init; }
    public IReadOnlyList<string> Procedures { get; init; } = [];
    public required ExpectedOutcome Expect { get; init; }

    public MedicalRecord ToRecord() => new()
    {
        Index = Id,
        Gender = Gender,
        Age = Age,
        AgeDay = AgeDay,
        Weight = Weight,
        Diagnoses = Diagnoses,
        Procedures = Procedures,
    };
}

internal static class CaseJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(TestCase c) => System.Text.Json.JsonSerializer.Serialize(c, Options);

    public static TestCase Deserialize(string line, string file, int lineNo)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<TestCase>(line, Options)
                   ?? throw new FormatException("反序列化得到 null");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            throw new FormatException($"{file}:{lineNo}: 用例无法解析 - {ex.Message}");
        }
    }
}
