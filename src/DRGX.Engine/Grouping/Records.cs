namespace DRGX.Engine;

/// <summary>分组状态。</summary>
public enum GroupStatus
{
    /// <summary>分组成功。</summary>
    Success,
    /// <summary>无法入组。</summary>
    Ungroupable,
    /// <summary>歧义病案(QY)。
    ///
    /// <para>该状态**会真实产生**,消费方必须处理:当病例满足某 MDC 的入组门控、但其主手术与主要诊断
    /// 无关时,按官方白名单(DataPack.QyRules,官方 21 个 MDC 有 QY 规则,不含 A/S/T/X/Y)落 QY 组。
    /// 落位形态:<c>Mdc = "MDC{X}"</c>、<c>Adrg = "QY"</c>、<c>Code = "{X}QY"</c>。</para>
    ///
    /// <para>为何单列而不计入 Success:QY 是官方分组器的虚拟组(MDC 级兜底),
    /// CHS-DRG 3.0 的 825 个 DRG 细分组里没有它,故不能与真实 DRG 落位混为一谈,
    /// 也不应算作 Ungroupable —— 它需要人工复核编码,而非"无法入组"。</para>
    /// </summary>
    Ambiguous,
    /// <summary>信息校验不通过(输入非法,未进入分组流程)。</summary>
    CheckFailed,
}

/// <summary>未入组/未通过原因(机读;与 trace 的判定终点一一对应)。</summary>
public enum UngroupedReason
{
    /// <summary>已入组或无原因。</summary>
    None = 0,
    /// <summary>信息校验不通过:性别缺失/非法、诊断列表为空。</summary>
    InputInvalid = 1,
    /// <summary>主诊断无法识别(不在诊断字典)。</summary>
    MainDiagnosisUnrecognized = 2,
    /// <summary>主诊断不参与分组(官方第五章"不作为分组规则"清单)。</summary>
    MainDiagnosisNotGroupable = 3,
    /// <summary>主要诊断不符合任何 MDC 的入组条件(含 MDCZ 部位数不足)。</summary>
    NoMdcMatched = 4,
    /// <summary>通过 MDC 门控但无 ADRG 落位命中。</summary>
    NoSubgroupMatched = 5,
}

/// <summary>DRG 分组输入病案。标量字段可缺失(null);诊断/操作列表不得为 null。</summary>
public sealed record MedicalRecord
{
    public required string Index { get; init; }
    /// <summary>"1"=男 "2"=女;其他取值视为非法。</summary>
    public string? Gender { get; init; }
    /// <summary>年龄(岁)。</summary>
    public int? Age { get; init; }
    /// <summary>日龄(天),新生儿场景使用。</summary>
    public int? AgeDay { get; init; }
    /// <summary>体重(克)。</summary>
    public int? Weight { get; init; }
    /// <summary>保留字段:科室(当前方案不使用)。</summary>
    public string? Dept { get; init; }
    /// <summary>保留字段:住院天数(当前方案不使用)。</summary>
    public int? InHospitalDays { get; init; }
    /// <summary>保留字段:离院方式(当前方案不使用)。</summary>
    public string? LeavingType { get; init; }
    /// <summary>诊断列表,主诊断必须在首位。</summary>
    public required IReadOnlyList<string> Diagnoses { get; init; }
    /// <summary>手术操作列表,主操作必须在首位;可为空。</summary>
    public IReadOnlyList<string> Procedures { get; init; } = [];
}

/// <summary>单条编码映射:录入码 → 医保版码。结构化下发,前端不再正则解析拼接串。</summary>
/// <param name="Orig">原始录入码(HIS/手工录入的院内码或灰码)。</param>
/// <param name="Mapped">映射后的医保版编码。</param>
/// <param name="Type">映射类别:"诊断" / "操作"。</param>
public sealed record CodeMapping(string Orig, string Mapped, string Type);

/// <summary>主诊断排除表命中项:该并发症与主诊断同排除组,未计入并发症分档。</summary>
/// <param name="Code">被排除的并发症编码。</param>
/// <param name="Kind">并发症类别:"MCC" / "CC"。</param>
/// <param name="Group">命中的排除组号(与主诊断的排除组一致;数据包内以字符串承载,如 "1")。</param>
public sealed record ExcludedComplication(string Code, string Kind, string Group);

/// <summary>条件轨迹的来源层:由引擎在调用点显式标注(见 GrouperEngine.TagScope),
/// 消费端据此归帧,不必从 Path 字符串里做子串匹配。</summary>
public enum TraceScope
{
    /// <summary>MDC 入组门控(MDCx.gate)。</summary>
    Gate,
    /// <summary>ADRG 入组路径(adrgs[X].entry[i].when)。</summary>
    AdrgEntry,
    /// <summary>DRG 细分组落位(adrgs[X].splits[DRG].when)。</summary>
    DrgSplit,
    /// <summary>歧义病组(QY)门控(MDCx.QY.qy)。</summary>
    QyGate,
}

/// <summary>条件求值轨迹(单节点):路径/表达式/结果/命中明细。</summary>
/// <param name="Codes">事实涉及的编码(结构化下发,前端就地补中文名,无需正则抠码)。</param>
/// <param name="Role">命中角色:mainDiagnosis / otherDiagnosis / mainProcedure / otherProcedure。</param>
/// <param name="SetRef">码表条件的官方集合编号(OP_ARB 等),前端借以跳「数据一览 → 编码集合」。</param>
/// <param name="NodeKind"><see cref="ConditionKind"/> 名("All"/"Any"/"MainDiagnosisIn"/"HasMcc"…)。
/// 消费端据此判断容器/叶子与具体原语,替代对中文短语的正则匹配。</param>
/// <param name="Total">容器节点的应满足子项数:AND 取声明的 N,OR 取分支数。叶子为 null。</param>
/// <param name="Passed">容器节点的已满足子项数(OR 成功记 1)。叶子为 null。</param>
/// <param name="Unjudged">容器节点中**未参与判定**的子项数(短路跳过)。与"已判定但未通过"是两种语义,
/// 消费端必须区分显示。叶子为 null。</param>
/// <param name="Scope">本节点所属的判定层。</param>
/// <param name="Owner">本节点归属的码:门控/QY 为 MDC 码,入组路径为 ADRG 码,细分组为 DRG 码。</param>
public sealed record ConditionTrace(string Path, string Expression, bool Result, string? Detail,
    IReadOnlyList<string>? Codes = null, string? Role = null, string? SetRef = null,
    string? NodeKind = null, int? Total = null, int? Passed = null, int? Unjudged = null,
    TraceScope? Scope = null, string? Owner = null);

/// <summary>分组过程中的单条轨迹。</summary>
public sealed record TraceStep(string Stage, string Message)
{
    /// <summary>条件轨迹:条件路径(相对数据包方案,如 "gate" / "adrgs[X71].subgroups[X719].when.of[1]")。</summary>
    public string? ConditionPath { get; init; }
    /// <summary>条件轨迹:原语表达式摘要,如 "mainDiagnosisIn(1613码)" / "ageGte(70)"。</summary>
    public string? Expression { get; init; }
    /// <summary>条件轨迹:求值结果(null = 非条件条目)。</summary>
    public bool? Result { get; init; }
    /// <summary>条件轨迹:命中的码 / 实际值 / 缺失说明。</summary>
    public string? Detail { get; init; }
    /// <summary>条件轨迹:事实涉及的编码(结构化,前端就地补中文名)。</summary>
    public IReadOnlyList<string>? Codes { get; init; }
    /// <summary>条件轨迹:命中角色(mainDiagnosis / otherDiagnosis / mainProcedure / otherProcedure)。</summary>
    public string? Role { get; init; }
    /// <summary>条件轨迹:码表条件的官方集合编号(OP_ARB 等),供前端跳「数据一览 → 编码集合」。</summary>
    public string? SetRef { get; init; }
    /// <summary>条件轨迹:所属判定层(引擎显式标注,替代路径子串匹配)。</summary>
    public TraceScope? Scope { get; init; }
    /// <summary>条件轨迹:归属码(MDC/ADRG/DRG),替代从路径里正则抠码。</summary>
    public string? Owner { get; init; }
    /// <summary>条件轨迹:条件原语名(<see cref="ConditionKind"/> 名)。</summary>
    public string? NodeKind { get; init; }
    /// <summary>条件轨迹:容器节点的应满足/已满足/未参与判定子项数(叶子为 null)。</summary>
    public int? Total { get; init; }
    /// <inheritdoc cref="Total"/>
    public int? Passed { get; init; }
    /// <inheritdoc cref="Total"/>
    public int? Unjudged { get; init; }

    /// <summary>流程行的机读消息码(如 "mdcGateFailed"/"adrgNotMatched"/"notJudgedAdrg");
    /// 条件行为 null。消费端据此分类与合并,替代对 message 文案的正则匹配。
    /// 取值是契约的一部分,只增不改。</summary>
    public string? StepCode { get; init; }
    /// <summary>流程行涉及的候选码(MDC/ADRG/DRG),替代从 message 里正则抠码。</summary>
    public string? Code { get; init; }
    /// <summary>流程行的机读参数(如未参与判定数量 count)。</summary>
    public IReadOnlyDictionary<string, string>? Args { get; init; }

    public override string ToString() => Result is null
        ? $"[{Stage}] {Message}"
        : $"[{Stage}] {ConditionPath}: {Expression} → {(Result.Value ? "✓" : "✗")}{(Detail is null ? "" : $" ({Detail})")}";
}

/// <summary>DRG 组索引条目(groups.json)。仅承载分组元数据——权重(RW)/参考费用等支付参数
/// 已剥离至地区费用包(data/regions,权重内嵌 weights.csv),与分组器零耦合,组装层按 DRG 码 join。</summary>
public sealed record GroupInfo(string Code, string Name);

/// <summary>基层病组条目(支付/管理口径的 DRG 子集,31 个):码 + 名称 + 分类。
/// <b>仅用于结果标注,不参与任何分组判定</b>。源为包根 <c>primary-groups.csv</c>(行序即下发顺序)。</summary>
public sealed record PrimaryGroupInfo(string Code, string Name, string Category);

/// <summary>分组结果。</summary>
public sealed record GroupOutcome
{
    public required string Index { get; init; }
    public required GroupStatus Status { get; init; }
    /// <summary>未入组/校验不通过的结构化原因(成功时 None;歧义病案时 None,语义由 Status 承载)。</summary>
    public UngroupedReason Reason { get; init; }
    /// <summary>原因的人类可读描述(成功时 null)。</summary>
    public string? ReasonText { get; init; }
    /// <summary>MDC 名(如 "MDCB");分组成功或歧义病案时有值。未入组不产生 "0000" 等哨兵码,
    public string? Mdc { get; init; }
    /// <summary>ADRG 名(3 字符,如 "AA1");分组成功时有值,歧义病案时为 "QY"。</summary>
    public string? Adrg { get; init; }
    /// <summary>ADRG 入组路径的自然语言描述(v3 entry 条件),分组成功时由引擎填充。</summary>
    public string? AdrgReason { get; init; }
    /// <summary>DRG 码;分组成功时为真实 DRG 码;歧义病案时为 {"MDC"}QY 虚拟组码(如 "FQY",
    /// 不在 825 组内,官方 2.0 术语定义);其余为 null。</summary>
    public string? Code { get; init; }
    /// <summary>命中的 DRG 组元数据(成功时)。</summary>
    public GroupInfo? Group { get; init; }
    /// <summary>判定轨迹(按发生顺序)。</summary>
    public IReadOnlyList<TraceStep> Trace { get; init; } = [];
    /// <summary>编码映射记录(结构化)。</summary>
    public IReadOnlyList<CodeMapping> Mappings { get; init; } = [];
    /// <summary>按排除机制过滤后的严重并发症列表。</summary>
    public IReadOnlyList<string> MajorComplications { get; init; } = [];
    /// <summary>主诊断排除表命中且未计入并发症的项(结构化)。</summary>
    public IReadOnlyList<ExcludedComplication> ExcludedComplications { get; init; } = [];
    /// <summary>按排除机制过滤后的并发症列表。</summary>
    public IReadOnlyList<string> MinorComplications { get; init; } = [];
    /// <summary>命中有效操作清单的操作列表。</summary>
    public IReadOnlyList<string> ValidProcedures { get; init; } = [];

    public string StatusText => Status switch
    {
        GroupStatus.Success => "分组成功",
        GroupStatus.Ungroupable => "无法入组",
        GroupStatus.Ambiguous => "歧义病案",
        GroupStatus.CheckFailed => "信息校验不通过",
        _ => Status.ToString(),
    };
}


