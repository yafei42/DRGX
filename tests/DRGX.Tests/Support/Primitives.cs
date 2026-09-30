using System.Collections.Frozen;
using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>
/// 合成条件与合成求值上下文。
///
/// <para><b>为什么需要它。</b>官方工作簿下发的是<b>实例化后的规则</b> —— 它只用到 44 个条件原语
/// 里的 29 个。剩下 15 个(<c>ageGt</c>/<c>noValidMainProcedure</c>/<c>siteCountGte</c>/
/// <c>robotAssist</c> 等)是<b>零覆盖的出货代码</b>:官方数据换了批次就可能用上,而且引擎的
/// DSL 编译器本来就会产出它们。没有合成用例,这些分支改坏了不会有任何信号。</para>
///
/// <para>这里刻意<b>不走数据包</b>:直接构造 <see cref="Condition"/> 树 + <see cref="EvaluationContext"/>
/// 调 <see cref="ConditionEvaluator"/>。理由是这些原语的语义细节(严格/非严格、缺失标量、
/// dayAge 护栏、交集去重计数)与"包从哪儿来"无关,走包只会引入一堆与断言无关的装载前置条件。</para>
/// </summary>
internal static class Primitives
{
    public static Condition Logic(ConditionKind kind, params Condition[] children) =>
        new() { Kind = kind, Children = children };

    public static Condition In(ConditionKind kind, params string[] codes) =>
        new() { Kind = kind, Codes = codes, CodeSet = codes.ToFrozenSet(StringComparer.OrdinalIgnoreCase) };

    public static Condition Num(ConditionKind kind, double value) => new() { Kind = kind, Value = value };

    /// <summary>交集基数原语(<c>procedureIntersectCountGte</c>/<c>diagnosisIntersectCountGte</c>):
    /// 既有阈值又有码表。这两个原语由 DSL 编译器产出,JSON 规则包解析不出(见
    /// <c>ConditionPrimitiveTests.JSON 原语清单</c>的说明),所以只能这样手工构造。</summary>
    public static Condition CountIn(ConditionKind kind, double value, params string[] codes) => new()
    {
        Kind = kind,
        Codes = codes,
        CodeSet = codes.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        Value = value,
    };

    public static Condition Gender(string value) => new() { Kind = ConditionKind.GenderIs, ExpectedValue = value };

    public static Condition Prefix(params string[] prefixes) =>
        new() { Kind = ConditionKind.AnyDiagnosisPrefix, Prefixes = prefixes };

    public static Condition Plain(ConditionKind kind) => new() { Kind = kind };

    public static bool Eval(Condition condition, Ctx ctx) => ConditionEvaluator.Evaluate(condition, ctx.Build());

    public static Ctx Context() => new();
}

/// <summary>
/// 合成求值上下文(限流式构建)。默认值刻意选"最小合法病案":一条主诊断、无操作、无并发症、
/// 生效性别男、年龄缺省 —— 每条断言只需声明它真正关心的那几个字段。
/// </summary>
internal sealed class Ctx
{
    private string _gender = "1";
    private int? _age;
    private int? _ageDay;
    private int? _weight;
    private readonly List<string> _diagnoses = ["A00.000"];
    private readonly List<string> _procedures = [];
    private readonly List<string> _mcc = [];
    private readonly List<string> _cc = [];
    private readonly List<string> _validProcedures = [];
    private bool _robot;
    private IReadOnlyDictionary<string, FrozenSet<string>>? _sites;

    public Ctx Dx(string main, params string[] others)
    {
        _diagnoses.Clear();
        _diagnoses.Add(main);
        _diagnoses.AddRange(others);
        return this;
    }

    /// <summary>设置操作列表(首项即主操作)。</summary>
    public Ctx Procs(params string[] procedures)
    {
        _procedures.Clear();
        _procedures.AddRange(procedures);
        return this;
    }

    public Ctx Age(int? age) { _age = age; return this; }
    public Ctx AgeDay(int? day) { _ageDay = day; return this; }
    public Ctx Weight(int? weight) { _weight = weight; return this; }
    public Ctx Male() { _gender = "1"; return this; }
    public Ctx Female() { _gender = "2"; return this; }

    public Ctx Mcc(params string[] codes) { _mcc.Clear(); _mcc.AddRange(codes); return this; }
    public Ctx Cc(params string[] codes) { _cc.Clear(); _cc.AddRange(codes); return this; }

    /// <summary>设置"有效操作"码表(引擎级)。只有列在这里的操作才算有效操作。</summary>
    public Ctx ValidProcedures(params string[] codes) { _validProcedures.Clear(); _validProcedures.AddRange(codes); return this; }

    public Ctx Robot(bool on) { _robot = on; return this; }

    /// <summary>部位映射:部位名 → 该部位的编码集(siteCountGte 专用)。</summary>
    public Ctx Sites(params (string Site, string[] Codes)[] sites)
    {
        _sites = sites.ToDictionary(
            s => s.Site,
            s => s.Codes.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
        return this;
    }

    public EvaluationContext Build() => new()    {
        Record = new MedicalRecord
        {
            Index = "synthetic",
            Gender = _gender,
            Age = _age,
            AgeDay = _ageDay,
            Weight = _weight,
            Diagnoses = _diagnoses.ToArray(),
            Procedures = _procedures.ToArray(),
        },
        MainDiagnosis = _diagnoses[0],
        OtherDiagnoses = _diagnoses.Skip(1).ToArray(),
        MainProcedure = _procedures.Count > 0 ? _procedures[0] : null,
        OtherProcedures = _procedures.Skip(1).ToArray(),
        RobotAssist = _robot,
        MajorComplications = _mcc.ToArray(),
        MinorComplications = _cc.ToArray(),
        ValidProcedureSet = _validProcedures.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        Sites = _sites,
    };

    /// <summary>给断言失败信息用的紧凑描述 —— 只列与默认值不同的字段。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (_gender != "1") parts.Add($"性别={_gender}");
        if (_age is int a) parts.Add($"年龄={a}");
        if (_ageDay is int d) parts.Add($"日龄={d}");
        if (_weight is int w) parts.Add($"体重={w}");
        parts.Add("诊断=[" + string.Join(",", _diagnoses) + "]");
        if (_procedures.Count > 0) parts.Add("操作=[" + string.Join(",", _procedures) + "]");
        if (_mcc.Count > 0) parts.Add("MCC=[" + string.Join(",", _mcc) + "]");
        if (_cc.Count > 0) parts.Add("CC=[" + string.Join(",", _cc) + "]");
        if (_validProcedures.Count > 0) parts.Add("有效操作=[" + string.Join(",", _validProcedures) + "]");
        if (_robot) parts.Add("机器人=是");
        if (_sites is not null) parts.Add("部位=" + string.Join("/", _sites.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        return string.Join(" ", parts);
    }
}
