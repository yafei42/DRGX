using DRGX.Engine;

namespace DRGX.Host;

/// <summary>
/// 结果行上的**识别类字段**打包:病案号 / 住院次数 / 入出院时间 / 姓名 / 证件号码。
///
/// <para>它们不参与分组,但结果要能被拿去做后续分析(对账、回 HIS、按时间/次数分层),
/// 所以一路带出来。三条批量路径的来源不同(CSV 列 / HIS 病案),落到结果行的位置却相同 ——
/// 这个类型就是那个"相同位置"的载体,免得每处再排一次六个可空字符串的顺序。</para>
/// </summary>
/// <param name="RecordNo">病案号(A48)。</param>
/// <param name="VisitNo">住院次数(A49)。</param>
/// <param name="AdmitAt">入院时间(B12)。</param>
/// <param name="DischargeAt">出院时间(B15)。</param>
/// <param name="PatientName">姓名(A11)。个人信息,屏幕过脱敏开关。</param>
/// <param name="IdNo">证件号码(A20)。个人信息。</param>
internal readonly record struct RowIds(
    string? RecordNo = null,
    string? VisitNo = null,
    string? AdmitAt = null,
    string? DischargeAt = null,
    string? PatientName = null,
    string? IdNo = null);

/// <summary>
/// 批量分组的**逐行装配**。一次批量运行构造一次,之后每行调 <see cref="GroupedRow"/>。
///
/// <para><b>它收口的是什么。</b>文件批量(CSV/XLSX)与 HIS 按日期提取两条路径,取数方式
/// 完全不同(前者读一次上传的网格,后者逐条远程提取),但**产出结果行的配方是同一套**:
/// 按请求级编码版本分组 → 按地区包 join 权重与测算分解 → 补病例明细列与识别类字段 →
/// 裁掉判定明细。这套配方原先在两处各写一遍,顺序还不同(trace 开关、<c>WithIds</c> 与
/// <c>WithFeeEstimate</c> 的先后),任何一处改动都要记得同步另一处 —— 而"忘了同步"的症状
/// 是两条路径的结果表在界面上悄悄长得不一样。</para>
///
/// <para><b>为什么分组开关也归它管。</b><see cref="GroupingOptions.Trace"/> 在批量路径上是
/// 个陷阱:结果行紧接着就被 <see cref="BatchParsing.TrimDetail"/> 把 trace 裁掉,
/// 于是"要 trace"这个开关在批量路径上等于开了又扔 —— 实测 trace 是响应体积的大头
/// (612 字节/行),白算一遍。把开关收进来,这个判断就只有一个落点,不会有人再顺手打开它。</para>
/// </summary>
internal sealed class BatchRun
{
    private readonly WebApp _svc;
    private readonly CodeSystem _codeSystem;
    private readonly DRGX.Fee.RegionPack? _region;
    private readonly string? _level;
    private readonly string? _type;

    /// <param name="svc">组合根:提供引擎与地区费用算法。</param>
    /// <param name="codeSystem">请求级编码版本(界面「编码版本」)。</param>
    /// <param name="region">请求级地区费用包;为 null 时不做费用 join。</param>
    /// <param name="level">医院等级(费用测算参数,可空)。</param>
    /// <param name="type">人员(医保)类型(费用测算参数,可空)。</param>
    public BatchRun(WebApp svc, CodeSystem codeSystem, DRGX.Fee.RegionPack? region,
        string? level = null, string? type = null)
    {
        _svc = svc;
        _codeSystem = codeSystem;
        _region = region;
        _level = level;
        _type = type;
    }

    /// <summary>
    /// 分组一行并装配成结果行。返回判定结果本身,是因为调用方还要按
    /// <see cref="GroupOutcome.Status"/> 分「入组 / 未入组」计数 —— 那是**统计口径**,
    /// 不属于结果行的形状,故不塞进 <see cref="BatchRow"/> 里再让调用方从字符串反解。
    /// </summary>
    /// <param name="totalFee">HIS 路径透传的总费用(对账用);文件路径无此字段,保持 null。</param>
    public (BatchRow Row, GroupOutcome Outcome) GroupedRow(
        int line, string summary, MedicalRecord record, in RowIds ids,
        string? patientId = null, decimal? totalFee = null)
    {
        var outcome = _svc.Engine.Group(record, new GroupingOptions
        {
            Trace = false,          // 见类型注释:批量行不留 trace,开了也会被 TrimDetail 扔掉
            CodeSystem = _codeSystem,
        });

        var row = FeeJoin.WithFee(BatchParsing.OutcomeRow(line, summary, outcome, patientId), _region);
        if (totalFee is { } fee) row = row with { Fee = fee };

        return (BatchParsing.TrimDetail(row
            .WithCase(record)
            .WithIds(ids.RecordNo, ids.VisitNo, ids.AdmitAt, ids.DischargeAt, ids.PatientName, ids.IdNo)
            .WithFeeEstimate(_svc, _region, _level, _type)), outcome);
    }

    /// <summary>
    /// 装配一行**失败行**(取数/解析阶段就失败了,没有分组结果可看)。
    /// 与 <see cref="GroupedRow"/> 的失败结果同形状 —— 前端据此只判 <c>error</c> 有没有值,
    /// 不必区分"解析失败"与"分组未入组"两种来路。
    /// </summary>
    public static BatchRow ErrorRow(int line, string summary, string error, in RowIds ids,
        string? patientId = null) =>
        new BatchRow(line, summary, PatientId: patientId, Error: error)
            .WithIds(ids.RecordNo, ids.VisitNo, ids.AdmitAt, ids.DischargeAt, ids.PatientName, ids.IdNo);
}
