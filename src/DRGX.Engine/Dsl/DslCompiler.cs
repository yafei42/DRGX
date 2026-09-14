using System.Collections.Frozen;

namespace DRGX.Engine.Dsl;

/// <summary>
/// 官方 DSL 变量表（官方全集 8 个 + 2 个虚拟集合）。
/// </summary>
public static class DslVariables
{
    public const string MainDiagnosis = "ZYZD";
    public const string OtherDiagnosis = "QTZD";
    public const string MainProcedure = "ZYSS";
    public const string OtherProcedure = "QTSS";
    public const string Age = "NL";
    public const string Gender = "XB";
    public const string AgeDay = "XSRTL";
    public const string Weight = "XSRTZ";

    /// <summary>严重并发症（虚拟集合，成员资格依赖运行期主诊断的排除表）。</summary>
    public const string Mcc = "MCC";
    /// <summary>并发症（虚拟集合，成员资格依赖运行期主诊断的排除表）。</summary>
    public const string Cc = "CC";

    private static readonly FrozenSet<string> Scalars =
        FrozenSet.ToFrozenSet([Age, Gender, AgeDay, Weight], StringComparer.OrdinalIgnoreCase);

    public static bool IsScalar(string name) => Scalars.Contains(name);

    public static bool IsVirtualSet(string name) =>
        name.Equals(Mcc, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(Cc, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 官方 DSL 编译器：把规则文本编译为现有 <see cref="Condition"/> 树。
/// 编译产物复用现有求值层，分组流程无需改动。
/// </summary>
/// <remarks>
/// 硬约束：<c>MCC</c>/<c>CC</c> 是虚拟集合，成员资格依赖运行期主诊断（排除表），
/// 只能编译为 HasMcc/HasCc/HasCcOrMcc 动态原语，禁止在编译期展开为静态 CodeSet。
/// </remarks>
public sealed class DslCompiler
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _codeSets;

    /// <param name="codeSets">集合编号 → 编码列表（OrdinalIgnoreCase 键）。</param>
    public DslCompiler(IReadOnlyDictionary<string, IReadOnlyList<string>> codeSets)
    {
        _codeSets = codeSets;
    }

    /// <summary>编译一条规则；空规则编译为恒真（官方语义：兜底落位）。</summary>
    public Condition Compile(string? dsl) => CompileNode(DslParser.Parse(dsl));

    private Condition CompileNode(DslNode node) => node switch
    {
        DslTrue => new Condition { Kind = ConditionKind.True },
        DslAnd and => new Condition { Kind = ConditionKind.All, Children = and.Items.Select(CompileNode).ToList() },
        DslOr or => new Condition { Kind = ConditionKind.Any, Children = or.Items.Select(CompileNode).ToList() },
        DslNot not => new Condition { Kind = ConditionKind.Not, Children = [CompileNode(not.Inner)] },
        DslCompare cmp => CompileCompare(cmp),
        DslMembership mem => CompileMembership(mem),
        DslIntersectCount ic => CompileIntersectCount(ic),
        _ => throw new DslException($"无法编译的语法节点：{node.GetType().Name}"),
    };

    private Condition CompileCompare(DslCompare node)
    {
        var variable = node.Variable.ToUpperInvariant();
        return variable switch
        {
            DslVariables.Age => new Condition { Kind = node.Op switch
            {
                "=" => ConditionKind.AgeEq,
                ">=" => ConditionKind.AgeGte,
                ">" => ConditionKind.AgeGt,
                "<=" => ConditionKind.AgeLte,
                "<" => ConditionKind.AgeLt,
                _ => throw Unsupported(variable, node.Op),
            }, Value = node.Value },
            DslVariables.AgeDay => CompileNumericRange(variable, node.Op, node.Value,
                ConditionKind.AgeDayGte, ConditionKind.AgeDayGt, ConditionKind.AgeDayLte, ConditionKind.AgeDayLt),
            DslVariables.Weight => CompileNumericRange(variable, node.Op, node.Value,
                ConditionKind.WeightGte, ConditionKind.WeightGt, ConditionKind.WeightLte, ConditionKind.WeightLt),
            DslVariables.Gender => CompileGender(node),
            _ => throw new DslException($"未知的标量变量 '{node.Variable}'"),
        };
    }

    /// <summary>日龄/体重：等值拆为"≥n 且 ≤n"，避免为单一写法新增原语。</summary>
    private static Condition CompileNumericRange(string variable, string op, double value,
        ConditionKind gte, ConditionKind gt, ConditionKind lte, ConditionKind lt) => op switch
        {
            ">=" => new Condition { Kind = gte, Value = value },
            ">" => new Condition { Kind = gt, Value = value },
            "<=" => new Condition { Kind = lte, Value = value },
            "<" => new Condition { Kind = lt, Value = value },
            "=" => new Condition
            {
                Kind = ConditionKind.All,
                Children =
                [
                    new Condition { Kind = gte, Value = value },
                    new Condition { Kind = lte, Value = value },
                ],
            },
            _ => throw Unsupported(variable, op),
        };

    private static Condition CompileGender(DslCompare node)
    {
        if (node.Op != "=")
            throw Unsupported(node.Variable, node.Op);
        var value = ((int)node.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is not ("1" or "2"))
            throw new DslException($"性别取值只允许 1/2，实际 {value}");
        return new Condition { Kind = ConditionKind.GenderIs, ExpectedValue = value };
    }

    /// <summary>
    /// length(集合 ∩ 字段集合) 算子 数值。官方仅出现 &gt;= （ADRG IC2），
    /// 其余算子 fail-fast，避免猜测语义。
    /// </summary>
    private Condition CompileIntersectCount(DslIntersectCount node)
    {
        if (node.Op != ">=")
            throw new DslException($"length() 暂不支持比较算子 '{node.Op}'（官方规则仅出现 >=）");
        if (DslVariables.IsVirtualSet(node.Set))
            throw new DslException($"length() 不支持虚拟集合 '{node.Set}'（MCC/CC 成员资格依赖运行期主诊断）");
        if (!_codeSets.TryGetValue(node.Set, out var members))
            throw new DslException($"集合 '{node.Set}' 未在数据包中定义");

        bool procedure = node.Fields.Any(f => f.Equals(DslVariables.MainProcedure, StringComparison.OrdinalIgnoreCase)
                                              || f.Equals(DslVariables.OtherProcedure, StringComparison.OrdinalIgnoreCase));
        bool diagnosis = node.Fields.Any(f => f.Equals(DslVariables.MainDiagnosis, StringComparison.OrdinalIgnoreCase)
                                              || f.Equals(DslVariables.OtherDiagnosis, StringComparison.OrdinalIgnoreCase));
        if (procedure == diagnosis)
            throw new DslException($"length() 的字段集合 [{string.Join(",", node.Fields)}] 必须同属诊断或手术操作");

        return new Condition
        {
            Kind = procedure ? ConditionKind.ProcedureIntersectCountGte : ConditionKind.DiagnosisIntersectCountGte,
            Codes = members,
            CodeSet = members.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            SetRef = node.Set.ToUpperInvariant(),
            Value = node.Value,
        };
    }

    private Condition CompileMembership(DslMembership node)
    {
        var kind = ResolveFieldKind(node.Fields);
        var condition = BuildSetCondition(kind, node.Sets);
        return node.Negated
            ? new Condition { Kind = ConditionKind.Not, Children = [condition] }
            : condition;
    }

    /// <summary>左侧字段集合 → 条件原语（花括号在左侧表示字段并集，任一命中）。</summary>
    private static ConditionKind ResolveFieldKind(IReadOnlyList<string> fields)
    {
        bool mainDx = fields.Contains(DslVariables.MainDiagnosis, StringComparer.OrdinalIgnoreCase);
        bool otherDx = fields.Contains(DslVariables.OtherDiagnosis, StringComparer.OrdinalIgnoreCase);
        bool mainPx = fields.Contains(DslVariables.MainProcedure, StringComparer.OrdinalIgnoreCase);
        bool otherPx = fields.Contains(DslVariables.OtherProcedure, StringComparer.OrdinalIgnoreCase);

        if ((mainDx || otherDx) && (mainPx || otherPx))
            throw new DslException($"字段集合 [{string.Join(",", fields)}] 混用了诊断与手术操作字段");

        if (mainDx || otherDx)
            return (mainDx, otherDx) switch
            {
                (true, true) => ConditionKind.AnyDiagnosisIn,
                (true, false) => ConditionKind.MainDiagnosisIn,
                _ => ConditionKind.OtherDiagnosisIn,
            };

        if (mainPx || otherPx)
            return (mainPx, otherPx) switch
            {
                (true, true) => ConditionKind.AnyProcedureIn,
                (true, false) => ConditionKind.MainProcedureIn,
                _ => ConditionKind.AnyOtherProcedureIn,
            };

        throw new DslException($"未知的字段集合 [{string.Join(",", fields)}]");
    }

    /// <summary>
    /// 右侧集合引用 → 条件。花括号在右侧表示集合并集，编译期合并为单一 CodeSet；
    /// MCC/CC 为虚拟集合，走动态原语，禁止展开。
    /// </summary>
    private Condition BuildSetCondition(ConditionKind kind, IReadOnlyList<string> sets)
    {
        var virtualSets = sets.Where(DslVariables.IsVirtualSet)
            .Select(s => s.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var namedSets = sets.Where(s => !DslVariables.IsVirtualSet(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Condition? virtualCondition = virtualSets.Count switch
        {
            0 => null,
            1 when virtualSets[0] == DslVariables.Mcc => new Condition { Kind = ConditionKind.HasMcc },
            1 => new Condition { Kind = ConditionKind.HasCc },
            _ => new Condition { Kind = ConditionKind.HasCcOrMcc },
        };

        if (namedSets.Count == 0)
        {
            return virtualCondition
                ?? throw new DslException("集合引用为空");
        }

        var codes = new List<string>();
        foreach (var name in namedSets)
        {
            if (!_codeSets.TryGetValue(name, out var members))
                throw new DslException($"集合 '{name}' 未在数据包中定义");
            codes.AddRange(members);
        }

        var codeCondition = new Condition
        {
            Kind = kind,
            Codes = codes,
            CodeSet = codes.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            // 单一集合引用时保留官方集合编号,条件描述可点名语义(如 OP_ARB=机器人辅助手术);
            // 多集合并集无法对应单一语义,留空走「指定码表」
            SetRef = namedSets.Count == 1 ? namedSets[0].ToUpperInvariant() : "",
        };

        // 虚拟集合与码表集合混用（官方未出现）：取并集，保持语义不丢
        return virtualCondition is null
            ? codeCondition
            : new Condition { Kind = ConditionKind.Any, Children = [virtualCondition, codeCondition] };
    }

    private static DslException Unsupported(string variable, string op) =>
        new($"变量 {variable} 不支持比较算子 '{op}'");
}
