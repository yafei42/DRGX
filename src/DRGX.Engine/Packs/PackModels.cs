using System.Collections.Frozen;

namespace DRGX.Engine;

/// <summary>编译后的 ADRG：有序亚组规则的容器。</summary>
public sealed class CompiledAdrg
{
    public required string Code { get; init; }
    /// <summary>官方入组条件原文（rules 内 adrg.origin），仅展示/溯源用，不参与求值。</summary>
    public string Origin { get; init; } = "";
    public required IReadOnlyList<CompiledEntry> Entries { get; init; }
    public required IReadOnlyList<CompiledSplit> Splits { get; init; }
    public required bool ProcedureDriven { get; init; }
}

/// <summary>编译后的 ADRG 入组路径：任一命中即入组。</summary>
public sealed record CompiledEntry(Condition When, string Label);

/// <summary>编译后的 ADRG 落位档：有序，首个 when 满足者落位，缺省 when 恒真。</summary>
public sealed record CompiledSplit(string Code, Condition? When, string Label)
{
    /// <summary>官方入组条件原文（rules 内 split.origin，由合并症等级/年龄属性/特殊入组条件三列综合），
    /// 仅展示/溯源用，不参与求值。</summary>
    public string Origin { get; init; } = "";

    /// <summary>本档的分档（伴严重并发症 / 伴并发症 / 不伴并发症），由 <see cref="When"/> 派生。
    ///
    /// <para>刻意做成**计算属性**而非 <c>init</c> 属性：init 只在构造时算一次，<c>with { When = … }</c>
    /// 之后就会与 When 脱节；计算属性恒与 When 一致，也不会给 record 的值相等性引入惰性状态。
    /// when 树只有几个节点，每次访问重走一遍的代价可忽略。</para></summary>
    public SeverityTier Tier => SplitTraits.TierOf(When);

    /// <summary>本档是否为直赋档（机器人直赋 / 高危妊娠直赋），由 <see cref="When"/> 派生；非直赋为 null。
    /// 请求期是否**确实**命中特殊身份条件，另经 <see cref="SplitTraits.ResolveDirect"/> 复核。</summary>
    public DirectRule? Direct => SplitTraits.DirectRuleOf(When);
}

/// <summary>编译后的 MDC 门控与 ADRG 规则（scheme.json 的运行时形态）。</summary>
public sealed class CompiledMdc
{
    public required string Code { get; init; }
    public required Condition Gate { get; init; }
    /// <summary>部位→编码集映射（siteCountGte 使用）；可为 null。</summary>
    public IReadOnlyDictionary<string, FrozenSet<string>>? Sites { get; init; }
    /// <summary>
    /// 是否为「未入组」全局兜底 MDC（官方 <c>0000</c>，mdc.csv 的 is_fallback 列）。
    ///
    /// 该 MDC 不是内科组，而是全链落空后的汇总桶：引擎对手术病例跳过内科组
    /// （<see cref="GrouperEngine"/> 内 <c>isSurgeryCase &amp;&amp; !adrg.ProcedureDriven</c>）时
    /// 必须放行它，否则"手术病例 + 主诊断落不到任何字母 MDC 门控"会报无法入组，
    /// 而非手术病例同样的输入却落 0000 —— 同一语义两种结果，无官方依据。
    /// </summary>
    public bool IsFallback { get; init; }
    public required IReadOnlyList<CompiledAdrg> Adrgs { get; init; }
}

/// <summary>
/// 编译后的数据包：全部结构加载期一次性校验并冻结，运行期只读、线程安全。
/// </summary>
public sealed class DataPack
{
    public required ManifestInfo Manifest { get; init; }
    /// <summary>诊断字典 —— **分组侧**口径，为两版并集（源 diagnoses.csv）。
    /// 引擎以它做「主诊断是否在码表」门控，并回显 MCC/CC/有效操作的中文名。
    /// 输入侧检索请用 <see cref="Yibao"/> / <see cref="Guolin"/>，不要用这张表按版本过滤。</summary>
    public required FrozenDictionary<string, string> DiagnosisNames { get; init; }
    /// <summary>操作字典 —— 分组侧口径，两版并集（源 procedures.csv）。语义同 <see cref="DiagnosisNames"/>。</summary>
    public required FrozenDictionary<string, string> ProcedureNames { get; init; }
    /// <summary>医保版输入字典（dict/*.yibao.csv）。</summary>
    public required CodeDictionaries Yibao { get; init; }
    /// <summary>国临版输入字典（dict/*.guolin.csv）。</summary>
    public required CodeDictionaries Guolin { get; init; }
    /// <summary>按版本取输入字典。</summary>
    public CodeDictionaries Codes(CodeSystem system) => system == CodeSystem.Guolin ? Guolin : Yibao;
    public required FrozenDictionary<string, string> DiagnosisMap { get; init; }
    public required FrozenDictionary<string, string> ProcedureMap { get; init; }
    public required FrozenSet<string> NonPrincipalDiagnoses { get; init; }
    public required FrozenSet<string> NonGroupingProcedures { get; init; }
    public required FrozenDictionary<string, string> Cc { get; init; }
    public required FrozenDictionary<string, string> Mcc { get; init; }
    public required FrozenDictionary<string, string> Exclusions { get; init; }
    public required FrozenSet<string> ValidProcedures { get; init; }
    /// <summary>
    /// 官方歧义组（QY）判定规则：MDC 码（如 "B"）→ 编译后的入组条件。
    /// QY 是官方白名单：仅 21 个 MDC 有定义（B~Z，<b>不含 A/S/T/X/Y</b>），且 PQY 附带
    /// 「日龄&lt;29」条件，因此<b>禁止</b>按 <c>{MDC}QY</c> 拼接推导——表内无定义即不判 QY。
    /// 未命中白名单的手术病例退回未入组（官方口径下会落 SB1/TB1/XJ1/YC1 等正式手术组或 MDC 兜底组）。
    /// 源：官方配置信息工作簿 ADRG 表中 code 以 QY 结尾的行。
    /// </summary>
    public required FrozenDictionary<string, Condition> QyRules { get; init; }
    /// <summary>机器人辅助手术触发码表(官方 code-sets.csv 的 OP_ARB 集合,T005 官方 5 码 17.4100~17.4500)。
    /// 注意与 T004 灰码表(NonGroupingProcedures)口径不同:17.4900/17.4900x001/17.4901 属 17.4 家族但无直赋资格。</summary>
    public required FrozenSet<string> RobotProcedures { get; init; }
    /// <summary>MDC 大类中文名(mdc.csv,键为 A~Z 大写字母)。可选文件:缺失时为空表,
    /// 结果页只显示大类码、不显示中文名。纯展示用,不参与分组判定。</summary>
    public required FrozenDictionary<string, string> MdcNames { get; init; }
    /// <summary>基层病组清单(primary-groups.csv,支付/管理口径的 DRG 子集)。<b>行序即
    /// /api/primary-groups 的下发顺序</b>,故用有序列表而非字典。可选文件:缺失时为空表,
    /// 结果页不挂「基层病组」徽标。纯展示用,不参与任何分组判定。</summary>
    public required IReadOnlyList<PrimaryGroupInfo> PrimaryGroups { get; init; }
    public required FrozenDictionary<string, GroupInfo> Groups { get; init; }
    public required IReadOnlyList<CompiledMdc> MdcChain { get; init; }
    /// <summary>本包全部**可达落位码**:每条 split 的码 ∪ QY 白名单动态产出的 <c>{MDC}QY</c>。
    /// 由 <see cref="PackCoverage.Emitted"/> 在装载期算一次 —— 测试与覆盖率告警都读它,
    /// 不再各自从 <see cref="MdcChain"/> 重推一遍(重推会与装载轨的判据分叉)。</summary>
    public required FrozenSet<string> EmittedCodes { get; init; }
    /// <summary>覆盖率警告：<see cref="Groups"/> 中存在但无任何落位规则可输出的码。
    /// 官方包应为空(850 条 split 码 + 21 个 QY = 871 = 组表行数)。</summary>
    public required IReadOnlyList<string> UnreachableGroups { get; init; }
}

public sealed record ManifestInfo(
    string Scheme,
    string Version,
    int Revision,
    DateTimeOffset BuiltAt,
    string EngineRequirement,
    IReadOnlyDictionary<string, string> Files,
    string Notes)
{
    /// <summary>MDC 链式求值顺序(码序列)。链式首中语义依赖此顺序,与 rules/ 文件一一对应。</summary>
    public IReadOnlyList<string> MdcOrder { get; init; } = [];

    /// <summary>官方配置信息的发布日期(yyyy-MM-dd),对外的数据批次标识:页头徽标与 README 都用它。
    /// 界面刻意不展示 <see cref="Revision"/> —— 那个数字是我方数据包的修订号,随入库形态(如 xlsx→直接解析
    /// 官方工作簿)变化,并非官方版本号;写成 rev N 会被读成"医保局发过 N 版"。只有官方重发配置信息才变更本字段。</summary>
    public string SourceDate { get; init; } = "";
}

/// <summary>数据包契约异常基类（加载期校验不通过等，附具体原因）。</summary>
public class PackException(string message) : Exception(message);
/// <summary>条件求值上下文。不可变:换 MDC 时用 <c>with</c> 生成新的部位映射视图。</summary>
public sealed record EvaluationContext
{
    public required MedicalRecord Record { get; init; }
    public required string MainDiagnosis { get; init; }
    public required IReadOnlyList<string> OtherDiagnoses { get; init; }
    public required string? MainProcedure { get; init; }
    public required IReadOnlyList<string> OtherProcedures { get; init; }
    /// <summary>机器人辅助手术标志：病例含 17.4x 机器人码。</summary>
    public required bool RobotAssist { get; init; }
    public required IReadOnlyList<string> MajorComplications { get; init; }
    public required IReadOnlyList<string> MinorComplications { get; init; }
    /// <summary>有效操作集合（引擎级）。</summary>
    public required IReadOnlySet<string> ValidProcedureSet { get; init; }
    /// <summary>当前 MDC 的部位映射（siteCountGte）；可为 null。</summary>
    public IReadOnlyDictionary<string, FrozenSet<string>>? Sites { get; init; }
}



