namespace DRGX.His;

/// <summary>诊断/手术编码条目:原始码 + 名称 + 医保对照码(对照缺失为 null)。</summary>
public sealed record HisCodeRef(string Icd, string? Name, string? Yibao);

/// <summary>
/// 中立病案记录:HIS 侧提取的唯一出口形态。
/// 字段口径对齐 HQMS 首页子集(分组必需),不携带 HIS 原生表结构。
/// Weight 为新生儿出生体重(克),与分组引擎 MedicalRecord.Weight 同口径。
/// </summary>
public sealed record HisMedicalRecord(
    string PatientId,
    string? Name,
    string? Gender,          // 原始性别码(HealthOne vae96),宿主层归一为 1/2
    int? Age,                // 岁(成年患者);未知为 null —— 不要用 0 表示未知
    int? AgeDay,             // 日龄(新生儿);无日级证据为 null。0 是合法值(出生当天),与 null 语义不同
    int? Weight,             // 克
    string? InsuranceType,   // 居民/职工/自费/其他
    string? AgeDetail,       // HealthOne 原始年龄明细(如 00Y03M05D),随响应回传供人工核对
    int? InHospitalDays,     // 住院天数
    string? LeavingType,     // 离院方式(原始码)
    decimal? TotalFee,       // 总费用(元)
    IReadOnlyList<HisCodeRef> Diagnoses,   // 按主次顺序(首条为主诊断)
    IReadOnlyList<HisCodeRef> Operations); // 按顺序(首条视为主手术)
