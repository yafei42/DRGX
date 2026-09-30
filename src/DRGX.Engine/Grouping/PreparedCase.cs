using System.Collections.Frozen;

namespace DRGX.Engine;

/// <summary>
/// 待判定病案:病案经「编码规范化 → 主诊断/其他诊断拆分 → 并发症计算(排除表) →
/// 机器人辅助标志 → 有效操作筛选」之后的形态。<b>引擎真正判定的对象就是它。</b>
///
/// <para>它存在的理由:上述五步原先只活在 <c>GrouperEngine.Pipeline.Run()</c> 内部,于是
/// 每个需要"知道引擎到底判定了什么"的地方都得自己重做一遍 —— 宿主侧
/// <c>WebApp.ResolveDrgDirect</c> 重做码映射,测试侧 <c>Tests/Support/Primitives.cs</c> 的
/// <c>Ctx.Build()</c> 手工拼整套求值上下文。收进本类型后,这些重做都消失,
/// 而"怎么从病案得到可判定的形态"这条不变量只剩一处。</para>
///
/// <para>不可变:所有集合在构造时冻结。<see cref="ContextFor"/> 换 MDC 只换部位映射视图,
/// 上下文其余部分不变。</para>
/// </summary>
public sealed record PreparedCase
{
    /// <summary>原始病案。求值器仍要读它的性别/年龄/日龄/体重(这些不参与规范化)。</summary>
    public required MedicalRecord Record { get; init; }

    /// <summary>规范化后的全部诊断(主诊断在首位)。国临版输入时已转医保版。</summary>
    public required IReadOnlyList<string> Diagnoses { get; init; }

    /// <summary>规范化后的全部操作(主操作在首位)。<b>未经</b>不参与分组剔除 ——
    /// 与 <see cref="ValidProcedures"/> 同源,故保留原列表以便轨迹与有效操作计数一致。</summary>
    public required IReadOnlyList<string> Procedures { get; init; }

    /// <summary>主要诊断(规范化后,即引擎判定用的码)。</summary>
    public required string MainDiagnosis { get; init; }

    /// <summary>其他诊断(规范化后)。</summary>
    public required IReadOnlyList<string> OtherDiagnoses { get; init; }

    /// <summary>主要手术操作(规范化后、已剔除不参与分组的项);无主操作时为 null。</summary>
    public required string? MainProcedure { get; init; }

    /// <summary>其他手术操作(规范化后、已剔除不参与分组的项)。</summary>
    public required IReadOnlyList<string> OtherProcedures { get; init; }

    /// <summary>机器人辅助手术标志:病例含官方 OP_ARB 触发码。在剔除不参与分组项**之前**整体识别。</summary>
    public required bool RobotAssist { get; init; }

    /// <summary>按排除机制过滤后的严重并发症列表。</summary>
    public required IReadOnlyList<string> MajorComplications { get; init; }

    /// <summary>按排除机制过滤后的并发症列表。</summary>
    public required IReadOnlyList<string> MinorComplications { get; init; }

    /// <summary>命中有效操作清单的操作(自 <see cref="Procedures"/> 筛出)。</summary>
    public required IReadOnlyList<string> ValidProcedures { get; init; }

    /// <summary>包级「有效操作」码表(官方 OP_ALL)。求值原语 hasValidProcedure / noValidMainProcedure 用。</summary>
    public required IReadOnlySet<string> ValidProcedureSet { get; init; }

    /// <summary>主诊断的排除组号;主诊断不在排除表时为 null。</summary>
    public required string? ExclusionGroup { get; init; }

    /// <summary>编码映射记录(结构化;只有真正发生过转换的项在内)。</summary>
    public required IReadOnlyList<CodeMapping> Mappings { get; init; }

    /// <summary>主诊断排除表命中且未计入并发症的项(结构化)。</summary>
    public required IReadOnlyList<ExcludedComplication> ExcludedComplications { get; init; }

    /// <summary>目标口径(医保版)目录里查不到的输入码(去重,保持首次出现顺序)。仅用于轨迹提示。</summary>
    public required IReadOnlyList<string> ForeignCodes { get; init; }

    /// <summary>主操作不参与分组、已被按"无主操作"处理时的原码;否则 null。</summary>
    public string? BlockedMainProcedure { get; init; }

    /// <summary>其他操作中不参与分组、已被剔除的码。</summary>
    public IReadOnlyList<string> BlockedOtherProcedures { get; init; } = [];

    /// <summary>
    /// 绑定某个 MDC 的部位映射,得到条件求值上下文。**这是构造求值上下文的唯一入口** ——
    /// 主诊断/主操作的拆分、并发症、机器人标志、有效操作集合的装配口径都在这里,
    /// 调用方(引擎的 MDC 链、测试的合成用例)不必也不应自己拼。
    /// </summary>
    /// <param name="sites">该 MDC 的部位映射(siteCountGte 用);无则为 null。</param>
    public EvaluationContext ContextFor(IReadOnlyDictionary<string, FrozenSet<string>>? sites = null) => new()
    {
        Record = Record,
        MainDiagnosis = MainDiagnosis,
        OtherDiagnoses = OtherDiagnoses,
        MainProcedure = MainProcedure,
        OtherProcedures = OtherProcedures,
        RobotAssist = RobotAssist,
        MajorComplications = MajorComplications,
        MinorComplications = MinorComplications,
        ValidProcedureSet = ValidProcedureSet,
        Sites = sites,
    };
}

/// <summary>待判定病案的准备结果:成功时 <see cref="Case"/> 非 null;失败时
/// <see cref="Status"/>/<see cref="Reason"/>/<see cref="Error"/> 给出终止原因,调用方据此直接产出结果。
///
/// <para><see cref="Mappings"/> 在两种情况下都有值:阶段 ② 的编码映射发生在主诊断识别之前,
/// 主诊断被拒时它已经收集完毕 —— 丢弃它会让"输入码被转换过"这件事在结果里消失。</para></summary>
public sealed record CasePreparationResult(
    PreparedCase? Case,
    GroupStatus Status,
    UngroupedReason Reason,
    string? Error,
    IReadOnlyList<CodeMapping> Mappings)
{
    public static CasePreparationResult Ok(PreparedCase prepared) =>
        new(prepared, GroupStatus.Success, UngroupedReason.None, null, prepared.Mappings);

    public static CasePreparationResult Failed(
        GroupStatus status, UngroupedReason reason, string error, IReadOnlyList<CodeMapping> mappings) =>
        new(null, status, reason, error, mappings);
}
