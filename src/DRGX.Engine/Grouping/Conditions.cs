using System.Collections.Frozen;
using System.Text.Json;

namespace DRGX.Engine;

/// <summary>条件原语种类(§5 冻结集)。</summary>
public enum ConditionKind
{
    // 逻辑组合
    True,
    All,
    Any,
    Not,

    // 诊断维度
    MainDiagnosisIn,
    AnyDiagnosisIn,
    OtherDiagnosisIn,
    AnyDiagnosisPrefix,

    // 操作维度
    MainProcedureIn,
    AnyProcedureIn,
    AnyOtherProcedureIn,
    ProcedureCountGte,
    HasProcedure,
    NoValidMainProcedure,
    HasValidProcedure,

    // 并发症维度
    HasOtherDiagnosis,
    HasMcc,
    HasCc,
    HasCcOrMcc,

    // 人口学维度
    AgeGte,
    AgeGt,
    AgeLte,
    AgeEq,
    AgeLt,
    AgeNot,
    AgeDayGte,
    AgeDayLt,
    AgeDayLte,
    AgeDayGt,
    WeightGte,
    WeightGt,
    WeightLte,
    WeightLt,
    GenderIs,

    /// <summary>
    /// 手术操作与指定码表的交集基数 ≥ Value（官方 DSL：length(S ∩ {ZYSS,QTSS})&gt;=n）。
    /// 由 DSL 编译器产出；JSON 规则包不使用。
    /// </summary>
    ProcedureIntersectCountGte,
    /// <summary>
    /// 诊断与指定码表的交集基数 ≥ Value（官方 DSL：length(S ∩ {ZYZD,QTZD})&gt;=n）。
    /// 由 DSL 编译器产出；JSON 规则包不使用。
    /// </summary>
    DiagnosisIntersectCountGte,

    // 特殊维度
    SiteCountGte,
    /// <summary>机器人辅助手术标志(病例含 17.4x 机器人码)。</summary>
    RobotAssist,

}

/// <summary>
/// 编译后的条件节点。逻辑节点含 Children;集合类节点含 Codes/Prefixes;
/// 数值类节点含 Value。
/// </summary>
public sealed class Condition
{
    public required ConditionKind Kind { get; init; }
    public IReadOnlyList<Condition> Children { get; init; } = [];
    public IReadOnlyList<string> Codes { get; init; } = [];
    public IReadOnlyList<string> Prefixes { get; init; } = [];

    /// <summary>
    /// 解析期物化的码表集合(OrdinalIgnoreCase)。求值端用 O(1) 哈希查找替代对
    /// Codes 的 LINQ 线性扫描;比较器与词典键/ToCodeSet 保持一致。
    /// </summary>
    public FrozenSet<string> CodeSet { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// 官方 DSL 的集合编号(如 OP_ARB/DI_O00),单一集合引用时由编译器保留,供条件
    /// 描述点名语义(「机器人辅助手术码表」);多集合并集或 JSON 规则包时为空。
    /// </summary>
    public string SetRef { get; init; } = "";

    public double Value { get; init; }
    /// <summary>期望匹配的字符串值(如 genderIs 的 "1"/"2")。</summary>
    public string ExpectedValue { get; init; } = "";

}

/// <summary>条件解析失败异常(fail-fast,消息含 JSON 路径)。归入数据包契约异常家族。</summary>
public sealed class ConditionParseException(string message) : PackException(message);

public static class ConditionParser
{
    private static readonly Dictionary<string, ConditionKind> KindMap = new()
    {
        ["always"] = ConditionKind.True,
        ["all"] = ConditionKind.All,
        ["any"] = ConditionKind.Any,
        ["not"] = ConditionKind.Not,
        ["mainDiagnosisIn"] = ConditionKind.MainDiagnosisIn,
        ["anyDiagnosisIn"] = ConditionKind.AnyDiagnosisIn,
        ["otherDiagnosisIn"] = ConditionKind.OtherDiagnosisIn,
        ["anyDiagnosisPrefix"] = ConditionKind.AnyDiagnosisPrefix,
        ["mainProcedureIn"] = ConditionKind.MainProcedureIn,
        ["anyProcedureIn"] = ConditionKind.AnyProcedureIn,
        ["anyOtherProcedureIn"] = ConditionKind.AnyOtherProcedureIn,
        ["procedureCountGte"] = ConditionKind.ProcedureCountGte,
        ["hasProcedure"] = ConditionKind.HasProcedure,
        ["noValidMainProcedure"] = ConditionKind.NoValidMainProcedure,
        ["hasValidProcedure"] = ConditionKind.HasValidProcedure,
        ["hasOtherDiagnosis"] = ConditionKind.HasOtherDiagnosis,
        ["hasMcc"] = ConditionKind.HasMcc,
        ["hasCc"] = ConditionKind.HasCc,
        ["hasCcOrMcc"] = ConditionKind.HasCcOrMcc,
        ["ageGte"] = ConditionKind.AgeGte,
        ["ageGt"] = ConditionKind.AgeGt,
        ["ageLte"] = ConditionKind.AgeLte,
        ["ageEq"] = ConditionKind.AgeEq,
        ["ageLt"] = ConditionKind.AgeLt,
        ["ageNot"] = ConditionKind.AgeNot,
        ["ageDayGte"] = ConditionKind.AgeDayGte,
        ["ageDayLt"] = ConditionKind.AgeDayLt,
        ["ageDayLte"] = ConditionKind.AgeDayLte,
        ["ageDayGt"] = ConditionKind.AgeDayGt,
        ["weightGte"] = ConditionKind.WeightGte,
        ["weightGt"] = ConditionKind.WeightGt,
        ["weightLte"] = ConditionKind.WeightLte,
        ["weightLt"] = ConditionKind.WeightLt,
        ["genderIs"] = ConditionKind.GenderIs,
        ["siteCountGte"] = ConditionKind.SiteCountGte,
        ["robotAssist"] = ConditionKind.RobotAssist,
    };

    public static Condition Parse(JsonElement element, string path, IReadOnlyDictionary<string, IReadOnlyList<string>>? codeSets = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ConditionParseException($"{path}: 条件必须是对象");
        if (!element.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
            throw new ConditionParseException($"{path}: 条件缺少 kind 字符串");

        var kind = ResolveKind(kindEl, path);

        // codesRef resolution: resolve named code set reference
        if (element.TryGetProperty("codesRef", out var refEl))
        {
            if (codeSets is null)
                throw new ConditionParseException($"{path}: 条件使用 codesRef 但数据包未定义 codeSets");
            var refName = refEl.GetString() ?? throw new ConditionParseException($"{path}: codesRef 含非字符串值");
            if (!codeSets.TryGetValue(refName, out var resolved))
                throw new ConditionParseException($"{path}: codesRef \"{refName}\" 在 codeSets 中未定义");
            if (kind is not (ConditionKind.MainDiagnosisIn or ConditionKind.AnyDiagnosisIn or ConditionKind.OtherDiagnosisIn
                or ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn or ConditionKind.AnyOtherProcedureIn))
                throw new ConditionParseException($"{path}: 原语 \"{kind}\" 不支持 codesRef");
            return new Condition { Kind = kind, Codes = resolved, CodeSet = resolved.ToFrozenSet(StringComparer.OrdinalIgnoreCase) };
        }

        switch (kind)
        {
            case ConditionKind.All or ConditionKind.Any:
            {
                var of = Require(element, "of", path);
                if (of.ValueKind != JsonValueKind.Array)
                    throw new ConditionParseException($"{path}.of: 必须是条件数组");
                var children = new List<Condition>();
                int i = 0;
                foreach (var child in of.EnumerateArray())
                    children.Add(Parse(child, $"{path}.of[{i++}]", codeSets));
                return new Condition { Kind = kind, Children = children };
            }
            case ConditionKind.Not:
            {
                var of = Require(element, "of", path);
                return new Condition { Kind = kind, Children = [Parse(of, $"{path}.of", codeSets)] };
            }
            case ConditionKind.MainDiagnosisIn or ConditionKind.AnyDiagnosisIn or ConditionKind.OtherDiagnosisIn
                or ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn or ConditionKind.AnyOtherProcedureIn:
            {
                var codes = ReadCodes(Require(element, "codes", path), $"{path}.codes");
                if (codes.Count == 0)
                    throw new ConditionParseException($"{path}.codes: 至少需要一个编码");
                return new Condition { Kind = kind, Codes = codes, CodeSet = codes.ToFrozenSet(StringComparer.OrdinalIgnoreCase) };
            }
            case ConditionKind.AnyDiagnosisPrefix:
            {
                var prefixes = Require(element, "prefixes", path);
                if (prefixes.ValueKind != JsonValueKind.Array)
                    throw new ConditionParseException($"{path}.prefixes: 必须是字符串数组");
                var list = prefixes.EnumerateArray()
                    .Select(p => p.GetString() ?? throw new ConditionParseException($"{path}.prefixes: 含非字符串项"))
                    .ToList();
                if (list.Count == 0)
                    throw new ConditionParseException($"{path}.prefixes: 至少需要一个前缀");
                return new Condition { Kind = kind, Prefixes = list };
            }
            case ConditionKind.ProcedureCountGte or ConditionKind.SiteCountGte
                or ConditionKind.AgeGte or ConditionKind.AgeGt or ConditionKind.AgeLte
                or ConditionKind.AgeEq or ConditionKind.AgeLt or ConditionKind.AgeNot
                or ConditionKind.AgeDayGte or ConditionKind.AgeDayLt
                or ConditionKind.AgeDayLte or ConditionKind.AgeDayGt
                or ConditionKind.WeightGte or ConditionKind.WeightGt
                or ConditionKind.WeightLte or ConditionKind.WeightLt:
            {
                var value = Require(element, "value", path);
                if (value.ValueKind != JsonValueKind.Number)
                    throw new ConditionParseException($"{path}.value: 必须是数值");
                return new Condition { Kind = kind, Value = value.GetDouble() };
            }
            case ConditionKind.GenderIs:
            {
                var value = Require(element, "value", path);
                var v = value.GetString();
                if (v is not ("1" or "2"))
                    throw new ConditionParseException($"{path}.value: 性别只允许 \"1\"/\"2\"");
                return new Condition { Kind = kind, ExpectedValue = v };
            }
            default:
            {
                // 无参原语:hasProcedure / noValidMainProcedure / hasOtherDiagnosis / hasMcc / hasCc / hasCcOrMcc
                return new Condition { Kind = kind };
            }
        }
    }

    private static ConditionKind ResolveKind(JsonElement kindEl, string path)
    {
        var kindName = kindEl.GetString()!;
        if (!KindMap.TryGetValue(kindName, out var kind))
            throw new ConditionParseException($"{path}: 未知条件原语 \"{kindName}\"");
        return kind;
    }

    private static JsonElement Require(JsonElement element, string name, string path) =>
        element.TryGetProperty(name, out var v)
            ? v
            : throw new ConditionParseException($"{path}: 缺少必需字段 \"{name}\"");

    private static IReadOnlyList<string> ReadCodes(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new ConditionParseException($"{path}: 必须是字符串数组");
        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var s = item.GetString() ?? throw new ConditionParseException($"{path}: 含非字符串项");
            list.Add(s.Trim());
        }
        return list;
    }
}









