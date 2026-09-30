using System.IO.Compression;
using System.Text.RegularExpressions;
using DRGX.Engine;
using DRGX.Text;
using Sylvan.Data.Excel;

namespace DRGX.Host;

// ============================================================================
// BatchParsing — 批量分组输入的纯函数解析层
//
// 从 Program.cs 拆出的无副作用静态类:CSV(RFC4180)/编码回退/分隔符探测/
// HQMS 列头映射/病案记录构造。独立成类以便单元测试覆盖脏输入,
// Web 端点仅做编排,不再内嵌解析逻辑。
// ============================================================================

/// <summary>批量导入内容超过上限(映射为 HTTP 413,与"格式错误"区分开)。</summary>
public sealed class BatchTooLargeException : Exception
{
    public BatchTooLargeException(string message) : base(message) { }
}

/// <summary>
/// 批量导入的硬上限(单一定义处:端点、解析器、表单配置共用,避免三处数字漂移)。
///
/// <para><b>定标依据(2026-09-12 实测,非估算)</b>。xlsx 解析库由 ClosedXML 换为
/// <c>Sylvan.Data.Excel</c>(MIT),解析侧成本降了约两个数量级,故上限随之放宽。
/// 实测四库读「首个工作表为字符串网格」(分配总量 / 墙钟):</para>
/// <code>
///   场景                     ClosedXML        MiniExcel        Sylvan.Data.Excel
///   120 列 × 5000 行         0.62s / 256MB    0.75s / 151MB    0.03s /   5MB
///   811 列 × 7001 行         未测(会拖垮)    16.9s / 4145MB   0.07s /  44MB
///   120 列 × 10 万行         未测(会拖垮)     6.7s / 3030MB   0.26s /  99MB
/// </code>
/// <para>关键结论有两条,都影响本类的取值:</para>
/// <list type="number">
///   <item><description><b>真正的内存大头是"分配总量"(GC churn),不是驻留</b>。三种读法最终
///     存下来的网格都是同一份 <c>List&lt;string[]&gt;</c>,驻留都是约 <b>8.5 字节/格</b>
///     (实测 567 万格 ≈ 43MB)。MiniExcel 的 4.1GB 全是转手即弃的垃圾,
///     但它足以在解析途中把进程顶到 OOM —— 所以"驻留一样"不等于"可以不管"。</description></item>
///   <item><description>解析库已足够快,于是<b>上限的瓶颈从"解析"移到了"交付"</b>:
///     20 万行的响应里分组明细(trace)一项就占 92%,前端逐行建 DOM 又是 180 万节点。
///     本类因此把"回传明细"与"渲染"的行数也作为上限管理(见
///     <see cref="MaxDetailRows"/> / <see cref="MaxIssueRows"/>) —— 上限不只管内存,也管交付。</description></item>
/// </list>
///
/// <para><b>HQMS 的真实宽度(定标前提,勿按"窄表"直觉调小)</b>:据随仓库的权威文件
/// <c>docs/HQMS首页数据采集质量与接口标准.docx</c>,该标准共 <b>772 个字段代码</b>
/// (A 25 / B 19 / C 554 / D 30 / F 144),其中「其他诊断编码」与「其他手术操作编码」
/// 各编到 40(C06X01C…C06X40C、C35X01C…C35X40C,正好等于
/// <see cref="ResolveColumns"/> 的 maxO/maxP)。<b>也就是说一份合规的 HQMS 首页导出
/// 本身就是 800 列上下的宽表</b> —— 实测遇到的 811 列属标准宽度,不是"HIS 导出残留的格式列"。</para>
/// </summary>
public static class BatchLimits
{
    /// <summary>上传文件字节上限(同时也是 Kestrel 请求体与 multipart 表单体积上限的依据)。
    ///
    /// <para>体积上限只兜网络与磁盘 I/O;文件"里有多少东西"由
    /// <see cref="MaxSheetBytes"/> 与 <see cref="MaxRows"/> 分别管。</para>
    /// <para><b>取 128MB 的来处</b>:真实全列导出(811 列、每行约 144 个非空格)在
    /// <see cref="MaxRows"/> 的 10 万行下,压缩后实测 <b>108MB</b> —— 要让这一档真能传进来,
    /// 本值就得在它之上留出余量。历史值 20MB → 64MB → 100MB → 128MB,每次都是被
    /// "行数合规、体积偏大"的真实文件顶上去的(最早踩的坑:2 万行 30MB 的导出上传即 413)。</para>
    /// <para>⚠ 定这个数要拿<b>修好的</b>测试件量:本次一度按 93MB 估到 100MB,
    /// 而那份件其实有列错位(诊断值被写进了列号很小的列),列引用短、体积虚低 ——
    /// 修正后同形状涨到 108MB。凡按"体积"定闸,都要先确认样例本身是对的。</para>
    /// <para>xlsx 路径另有独立护栏:<see cref="GuardXlsx"/> 按解压后真实字节数限流,不受本值放宽影响。</para>
    /// </summary>
    public const long MaxUploadBytes = 128L * 1024 * 1024;

    /// <summary>数据行上限(不含表头)。超限直接拒绝,不再静默跳过 —— 静默截断会让用户拿到一份看起来完整的结果。
    ///
    /// <para>约束来自业务量与耗时,不是内存:每行都要装配成 BatchRow 回传
    /// (前端分片渲染,行数直接等于 DOM 行数)。</para>
    /// <para><b>10 万行的来处</b>:拿真实导出校准过 —— 一份三甲医院的月度 HQMS 结算清单
    /// (<c>hqmsts_2026M01.xlsx</c>)是 <b>3,345 行</b>,即 <b>一年约 4 万行</b>。
    /// 所以 10 万行 ≈ <b>该院两年半的出院量</b>,足以覆盖任何合理的批量场景;
    /// 再往上放的驱动力通常不是业务需要,而是"一次把全部历史跑完"的冲动,
    /// 而代价由内存与交付承担(实测 20 万行时响应 42MB、服务端增量 141MB)。</para>
    /// <para><b>注意它管不住什么</b>:本值是<b>行数</b>闸,与列数无关 ——
    /// 宽表真正的闸门是 <see cref="MaxSheetBytes"/>(解压后的工作表体积)。
    /// 也就是说"10 万行 × 全列(每行 130 个非空格)"仍然过不去,见该字段的说明。</para>
    /// </summary>
    public const int MaxRows = 100_000;

    /// <summary>「只看异常」最多渲染的问题行数(<b>纯前端渲染上限</b>,与服务端回传无关)。
    ///
    /// <para><b>2026-09-12 由 20,000 收紧到 1,000</b>,三条理由:</para>
    /// <list type="number">
    ///   <item><description>原值 20,000 是为"问题行每条都点得开、逐条核对"设的;而<b>批量结果的逐行展开已移除</b>,
    ///     把两万行几乎全错的记录铺在屏幕上,已无对应需求。</description></item>
    ///   <item><description><b>实测渲染成本</b>:20,000 行 × 24 列 = 599,956 个 DOM 元素,整表渲染 <b>12.8 秒</b>
    ///     (2,000 行只要 0.3 秒 —— 随行数近二次增长)。1,000 行约 0.2 秒,用户感觉不到等。</description></item>
    ///   <item><description>需要逐条分析走 <b>导出 CSV</b>(全量、不脱敏、30 列)—— 那才是分析载体,
    ///     屏幕上不需要同时铺两万行。</description></item>
    /// </list>
    /// <para>与 <see cref="MaxDetailRows"/> 取同一个值:正常行与异常行看到的上限一致,用户不必记两套规则。</para>
    /// </summary>
    public const int MaxIssueRows = 1_000;

    /// <summary>结果表默认(未筛异常时)最多渲染的行数。<b>纯前端渲染上限</b>。
    ///
    /// <para>2026-09-12 由 2,000 收紧到 1,000,与 <see cref="MaxIssueRows"/> 对齐。
    /// 这不是性能问题(实测 2,000 行渲染只需 0.3 秒),而是"一屏该给多少行"的<b>口径</b>问题:
    /// 结果表一行一份 DOM,判定所需字段已逐列摆开,再多也只是滚动;
    /// 要全量看走导出 CSV。</para>
    /// </summary>
    public const int MaxDetailRows = 1_000;

    /// <summary>xlsx 单个工作表解压后的字节上限(<b>xlsx 路径唯一的内存闸门</b>)。
    ///
    /// <para>它同时挡两件事,所以<b>不必再单设"单元格总数"上限</b>:
    /// ① 解压炸弹;② 网格规模 —— 工作表 xml 里每个单元格至少要写一个
    /// <c>&lt;c r="A1"/&gt;</c> 量级的元素(约 20 字节),于是 128MB 解压体积在数学上
    /// 就把单元格数压在<b>百万</b>量级,按实测驻留 8.5 字节/格不过几十 MB。</para>
    /// <para>曾经的 <c>MaxCells</c> / <c>MaxCols</c> 已删除:前者按"宽 × 行"的<b>面积</b>估算,
    /// 把稀疏 xlsx 的内存成本高估两个数量级(811 列 × 8 万行:面积算 6488 万格 vs 实际 40 万格),
    /// 于是合规的满宽 HQMS 导出会在远未到行数上限时被一个与真实内存无关的数字拦下;
    /// 后者(16384)本就是 Excel 自身的天花板,当业务上限用只会误伤宽表。</para>
    /// <para><b>换算成实际形状(2026-09-12 实测,别按直觉估)</b>:解压体积的大头不是"值的字节",
    /// 而是<b>每个单元格的标记与列引用</b> —— <c>&lt;c r="AEE199999" t="s"&gt;&lt;v&gt;1234&lt;/v&gt;&lt;/c&gt;</c>
    /// 就是 30~57 字节/格。所以"每行填几个格"才是决定体积的变量,列宽只是把它放大:</para>
    /// <list type="bullet">
    ///   <item><description>811 列表头、每行填 <b>10</b> 格:10 万行 ≈ 解压 <b>54MB</b>(文件 7.3MB)</description></item>
    ///   <item><description>811 列表头、每行填 <b>14</b> 格:10 万行 ≈ 解压 <b>79MB</b>(文件 11.3MB)</description></item>
    ///   <item><description>真实全列导出、每行约 <b>130</b> 格:10 万行 ≈ 解压 <b>574MB</b>(文件约 93MB)</description></item>
    /// </list>
    /// <para><b>本值与 <see cref="MaxRows"/> 的分工</b>:行数归 <see cref="MaxRows"/> 管(它是行闸),
    /// 本值管"一格里有多少内容"的规模。两者取交集后,<b>满宽全列下的行数上限由本值决定</b> ——
    /// 也就是说"10 万行 × 全列"能不能过,取决于本值给不给得下 574MB 这一档。</para>
    /// <para><b>与 <see cref="MaxTotalUncompressedBytes"/> 的先后关系</b>:整包总量在<b>本值之前</b>判定,
    /// 所以整包上限必须 ≥ 本值 + 余量,否则本值形同虚设(整包先把大表拦掉)。改本值时务必一起看。</para>
    /// <para><b>残留风险(已知并接受)</b>:CSV 路径没有此处对应的格数护栏, 它的规模只由
    /// <see cref="MaxUploadBytes"/> 间接兜住 —— 理论上一个 100MB 的逗号串能撑出数千万个空字段。
    /// 真要收紧,应在 <c>ParseCsv</c> 里加"单行长度上限",而不是恢复面积口径。</para>
    /// </summary>
    public const long MaxSheetBytes = 800L * 1024 * 1024;

    /// <summary>xlsx 解压后<b>整包</b>总字节上限(所有条目的声明大小之和)。
    ///
    /// <para>它挡的是"很多个大条目"型压缩包;单表规模归 <see cref="MaxSheetBytes"/>。
    /// 两者是<b>先后关系</b>:本值在单表闸之前判定,所以必须留出余量 ——
    /// 一张 800MB 的工作表之外还有 sharedStrings(宽表/长表的字符串表可以有几十 MB)、
    /// styles、theme 等条目。本值贴着单表值设,等于把单表闸废掉。</para>
    /// <para>取 1GB:单表 800MB + 约 200MB 余量。两个数要一起调。</para>
    /// </summary>
    public const long MaxTotalUncompressedBytes = 1024L * 1024 * 1024;

    /// <summary>xlsx 条目数上限(挡"海量小条目"型压缩包)。</summary>
    public const int MaxZipEntries = 2_048;

    /// <summary>HIS 按日期批量提取的就诊号条数(上限 / 默认)。
    ///
    /// <para>HIS 路径与文件路径共用同一套装配与明细预算(<see cref="MaxDetailRows"/> /
    /// <see cref="MaxIssueRows"/>),但条数上限<b>刻意不共用</b> <see cref="MaxRows"/>:
    /// 文件是一次读一堆,而 HIS 是<b>逐条远程提取</b>(每条一次连接往返),
    /// 20 万条在连接器上是要跑几小时的事,塞进一个同步请求只会把页面拖死。
    /// 真要跑全年,应走 HIS 侧的批量导出,而不是把这里抬到 20 万。</para>
    /// </summary>
    public const int MaxHisIds = 1_000;

    /// <summary>HIS 批量提取的默认条数(界面不填时)。</summary>
    public const int DefaultHisIds = 100;

    /// <summary>上限的 MB 表示(错误文案与配置共用)。</summary>
    public static long MaxUploadMb => MaxUploadBytes / 1024 / 1024;

    /// <summary>任意字节数的 MB 文本(1 位小数):"文件过大"必须报出实际体积,否则用户无从判断要拆多少。</summary>
    public static string Mb(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>CSV 首页字段映射结果:各角色列索引;-1 表示缺失(对应数据被忽略)。
/// AgeDay/Weight 为新生儿专用可选列;末尾四个是<b>识别类列</b> —— 它们不参与分组,
/// 但结果要能被拿去做后续分析(对账、回 HIS、按时间/次数分层),所以一并解析出来。
/// 中文名取自仓库内的权威标准 <c>docs/HQMS首页数据采集质量与接口标准.docx</c>,不靠猜。</summary>
public sealed record ColIdx(
    int Gender, int Age, int MainDx, int MainProc, int AgeDay, int Weight,
    IReadOnlyList<int> OtherDx, IReadOnlyList<int> OtherProc,
    /// <summary>病案号(A48)。与住院次数一起才能唯一确定一次就诊 —— 缺了它,导出的结果对不回 HIS。</summary>
    int RecordNo = -1,
    /// <summary>住院次数(A49)。</summary>
    int VisitNo = -1,
    /// <summary>入院时间(B12)。</summary>
    int AdmitAt = -1,
    /// <summary>出院时间(B15)。</summary>
    int DischargeAt = -1,
    /// <summary>姓名(A11)。个人信息,屏幕按脱敏开关显示。</summary>
    int PatientName = -1,
    /// <summary>证件号码(A20)。个人信息。</summary>
    int IdNo = -1);

/// <summary>批量结果行:Error 非空为解析失败行;否则携带分组结果。</summary>
public sealed record BatchRow(
    int Line,
    string Summary,
    string? PatientId = null,
    string? Error = null,
    string? Status = null,
    string? StatusText = null,
    string? Code = null,
    string? Name = null,
    string? Weight = null,
    string? Cost = null,
    string? Mdc = null,
    string? Adrg = null,
    string? Reason = null,
    IReadOnlyList<DRGX.Engine.TraceStep>? Trace = null,
    IReadOnlyList<DRGX.Engine.CodeMapping>? Mappings = null,
    IReadOnlyList<DRGX.Engine.ExcludedComplication>? ExcludedComplications = null,
    /// <summary>直赋档说明(高危妊娠直赋/机器人直赋);非直赋落位为 null。与单病例端点同判定。</summary>
    string? DrgDirect = null,
    /// <summary>HIS 路径携带的 HIS 总费用(元,原样透传供对账);文件路径为 null。</summary>
    decimal? Fee = null,
    // ---------------- 病例明细与分组层级 ----------------
    // 结果列表要"分列展示、能直接分析",所以这些字段必须各自独立下发,
    // 不能让前端从 <see cref="Summary"/> 那个拼好的字符串(如 "女·33岁 R57.000 +其他诊断3")里反解 ——
    // 反解一旦遇到姓名含「·」或诊断条数变化就会错位,而它又恰好是给医生看的第一手信息。
    /// <summary>性别 "1"=男 "2"=女;无法识别为 null。</summary>
    string? Gender = null,
    /// <summary>年龄显示文本(如 "81岁" / "3天");未知为 null(不写 0:0 岁与"未知"必须能区分)。</summary>
    string? Age = null,
    /// <summary>主要诊断编码(病案 Diagnoses[0])。</summary>
    string? MainDx = null,
    /// <summary>其他诊断条数(明细串可能被截断,故条数单独给)。</summary>
    int? OtherDxCount = null,
    /// <summary>手术操作条数。</summary>
    int? ProcCount = null,
    /// <summary>其他诊断编码,分号连接(不含主诊断);供列表分列与导出分析。</summary>
    string? OtherDx = null,
    /// <summary>手术操作编码,分号连接;主操作在首位。</summary>
    string? Procs = null,
    // 识别类字段(见 ColIdx 同名成员)。这些是"结果拿去对账/回 HIS/分层分析"的必要条件,
    // 所以即使不参与分组也一路带出来;源文件没有这些列时保持 null(不写空串)。
    /// <summary>病案号(A48)。</summary>
    string? RecordNo = null,
    /// <summary>住院次数(A49)。</summary>
    string? VisitNo = null,
    /// <summary>入院时间(B12),原样透传(不重新格式化,避免与源口径不一致)。</summary>
    string? AdmitAt = null,
    /// <summary>出院时间(B15),原样透传。</summary>
    string? DischargeAt = null,
    /// <summary>患者姓名(A11)。属个人信息:屏幕显示走脱敏开关,导出文件不脱敏(见 exportCsv 说明)。</summary>
    string? PatientName = null,
    /// <summary>证件号码(A20)。同上,个人信息。</summary>
    string? IdNo = null,
    // ---------------- 费用测算明细 ----------------
    // 逐行给"这条按地区算法能拿多少钱"的分解。字段与 /api/fee/estimate 同源(同一个
    // FeeEstimate 结果直接摊到行上),不在批量路径里另写一套公式 —— 地区算法是可插拔的,
    // 各地区的公式并不一样(见 FeeAlgorithms.Resolve)。
    /// <summary>系数(地区/等级调整系数)。</summary>
    string? Factor = null,
    /// <summary>点值(元/点)。</summary>
    string? PointValue = null,
    /// <summary>点数合计。</summary>
    string? TotalPoint = null,
    /// <summary>倍率类型(如 低倍率/正常/高倍率)。</summary>
    string? RateType = null,
    /// <summary>支付标准(元)。</summary>
    string? StandardFee = null,
    /// <summary>估算支付(元)。</summary>
    string? EstimatedFee = null,
    /// <summary>低倍率阈值(元),地区包未给则 null。</summary>
    string? LowRateFee = null,
    /// <summary>高倍率阈值(元),地区包未给则 null。</summary>
    string? HighRateFee = null)
{
    /// <summary>问题行 = 解析失败 / 未入组 / 歧义。与前端 <c>isBatchIssue</c> 同一判定:
    /// 前端写作 <c>!!row.error || (!!row.status &amp;&amp; row.status !== 'Success')</c>。
    /// 两侧必须一致 —— 服务端按它决定"给不给 trace",前端按它决定"渲染不渲染",错开就会出现
    /// "渲染了却没有明细"或"有明细却永远看不到"。</summary>
    [System.Text.Json.Serialization.JsonIgnore]   // 判定结果是过程量,不进响应(20 万行会凭空多出几 MB)
    public bool IsIssue => Error is not null || (Status is not null && Status != "Success");
}

public static class BatchParsing
{
    /// <summary>原始性别码归一为引擎口径 1/2;无法识别返回 null(与 HIS/CSV 两条路径共用)。</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull("raw")]
    public static string? NormalizeGender(string? raw) => raw?.Trim() switch
    {
        "1" or "男" or "m" or "M" => "1",
        "2" or "女" or "f" or "F" => "2",
        _ => null,
    };

    /// <summary>批量结果行(分组成功/失败路径共用)。Weight/Cost 为支付参数,由 Web 宿主从
    /// 独立费用模块按 DRG 码 join 回填(分组器不承载权重)。
    /// 直赋档说明直接取自 <see cref="GroupOutcome.DrgDirect"/> —— 判定由引擎在命中档位处完成,
    /// 调用方不再需要传入(原先三条路径各自调 WebApp.ResolveDrgDirect 重判一次)。</summary>
    public static BatchRow OutcomeRow(int line, string summary, GroupOutcome o, string? patientId = null) => new(
        line,
        summary,
        PatientId: patientId,
        Status: o.Status.ToString(),
        StatusText: o.StatusText,
        Code: o.Code,
        Name: o.Group?.Name,
        Mdc: o.Mdc,
        Adrg: o.Adrg,
        Reason: o.ReasonText,
        Trace: o.Trace,
        Mappings: o.Mappings,
        ExcludedComplications: o.ExcludedComplications,
        DrgDirect: o.DrgDirect);

    /// <summary>把病案的临床明细补进结果行(性别/年龄/主诊断/其他诊断/手术各自成列,供人工分析)。</summary>
    public static BatchRow WithCase(this BatchRow row, MedicalRecord rec) => row with
    {
        Gender = rec.Gender,
        Age = AgeText(rec.Age, rec.AgeDay),
        MainDx = rec.Diagnoses.Count > 0 ? rec.Diagnoses[0] : null,
        OtherDxCount = Math.Max(rec.Diagnoses.Count - 1, 0),
        ProcCount = rec.Procedures.Count,
        OtherDx = Join(rec.Diagnoses.Skip(1)),
        Procs = Join(rec.Procedures),
    };

    /// <summary>补上识别类字段(病案号 / 住院次数 / 入出院时间 / 姓名 / 证件号码)。
    /// 缺列时保持 null,<b>不写空串</b> —— "源文件没有这一列"与"这一列的值是空的"在分析时含义不同。</summary>
    public static BatchRow WithIds(this BatchRow row, string? recordNo, string? visitNo, string? admitAt, string? dischargeAt,
        string? patientName = null, string? idNo = null) =>
        row with
        {
            RecordNo = Nz(recordNo),
            VisitNo = Nz(visitNo),
            AdmitAt = Nz(admitAt),
            DischargeAt = Nz(dischargeAt),
            PatientName = Nz(patientName),
            IdNo = Nz(idNo),
        };

    /// <summary>数值转展示文本(invariant)。参数收 <c>object?</c> 是刻意的:费用测算结果的
    /// 各字段类型由算法实现决定(decimal/int/double 都可能),批量路径不该为此绑定具体类型 ——
    /// 绑定就意味着"换一个地区算法就要改批量路径"。</summary>
    public static string? Num(object? v) => v switch
    {
        null => null,
        string s => s,
        System.IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>年龄显示文本:优先岁,其次日龄(新生儿);都没有则 null。
    /// <b>不写 0</b> —— "0 岁"与"年龄未知"在核对时是两回事(引擎也按 null 处理);
    /// 但日龄的 0 是合法值(出生当天),不能一并当未知剔掉。
    /// 收 age/ageDay 而不是收病案对象:批量结果列与 HIS 详情页的输入类型不同
    /// (MedicalRecord / HisMedicalRecord),文案口径只应有一份。</summary>
    public static string? AgeText(int? age, int? ageDay) =>
        age is int a and > 0 ? $"{a}岁" : ageDay is int d ? $"{d}天" : null;

    /// <summary>病案摘要:姓名(可选)·性别·年龄。性别码无法识别时给 "?",不假装是女。</summary>
    public static string CaseSummary(string? name, string? gender, string? ageText)
    {
        var g = gender == "1" ? "男" : gender == "2" ? "女" : "?";
        return $"{(name is { Length: > 0 } n ? n + "·" : "")}{g}·{ageText}";
    }

    /// <summary>编码明细的分号连接(空表返回 null,避免下发一个空串让前端再判一次)。</summary>
    private static string? Join(IEnumerable<string> codes)
    {
        var s = string.Join(';', codes);
        return s.Length == 0 ? null : s;
    }

    /// <summary>去掉批量行的"判定明细"(分组 trace / 编码映射 / 被排除的并发症),只留表格与 CSV 要的字段。
    ///
    /// <para>批量结果页<b>不再提供逐行展开</b>,这三项在批量路径上就没有任何消费者了 ——
    /// 而它们是响应体积的大头(trace 实测 <b>612 字节/行</b>,占比最高时达 92%),留着纯属白传。
    /// 单病例视图走自己的提取路径,不受影响。</para>
    /// <para>原"明细预算"(预览段 + 问题行段)随之取消:现在要么全不给,不存在"只给前 N 行"。
    /// 前端的渲染行数上限仍在(<see cref="BatchLimits.MaxDetailRows"/> /
    /// <see cref="BatchLimits.MaxIssueRows"/>),但那只约束浏览器建多少行 DOM,与服务端回传无关。</para></summary>
    public static BatchRow TrimDetail(BatchRow row) =>
        row with { Trace = null, Mappings = null, ExcludedComplications = null };

    /// <summary>仅裁剪空白,不强制大写:词典键含规范小写 "x"(如 J42.x00、33.6x00),全大写会破坏匹配。
    /// 大小写容错交由数据包字典/集合的 OrdinalIgnoreCase 比较器处理。</summary>
    public static string Clean(string? code) => (code ?? "").Trim();

    // ================================ 编码切分 ================================

    /// <summary>把单元格内的多个编码切分开:';' 与 '、' 均视为分隔符。</summary>
    public static List<string> SplitCodes(string cell)
    {
        var result = new List<string>();
        foreach (var part in cell.Split([';', '、']))
        {
            var code = Clean(part); // 仅裁剪空白,保留规范小写 "x";大小写容错交由字典比较器
            if (code.Length > 0) result.Add(code);
        }
        return result;
    }

    /// <summary>容错解析整数字段:空白→null;含数字则取首个连续数字序列;非法→null(不阻断整行,仅视为缺失)。</summary>
    static int? ParseIntField(string? raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return null;
        var m = Regex.Match(t, @"\d+");
        return m.Success ? int.Parse(m.Value) : null;
    }

    /// <summary>统一构造 MedicalRecord:性别/年龄/主诊断/其他诊断/手术操作。CSV 文件批量路径使用。
    /// ageDayRaw/weightRaw 为可选(新生儿专用),缺失或非法时按 null 处理,不阻断分组。</summary>
    public static (MedicalRecord Record, string? Error, string Summary) BuildRecord(
        string index, string genderRaw, string ageRaw, string mainDxRaw,
        IReadOnlyList<string> otherDx, IReadOnlyList<string> procedures,
        string? ageDayRaw = null, string? weightRaw = null)
    {
        var gender = NormalizeGender(genderRaw);
        if (gender is null)
            return (null!, $"性别无法识别: \"{genderRaw.Trim()}\"(应为 1/男 或 2/女)", "");

        int? age = null;
        var ageText = ageRaw.Trim();
        if (ageText.Length > 0)
        {
            var m = Regex.Match(ageText, @"\d+");
            if (!m.Success)
                return (null!, $"年龄无法识别: \"{ageText}\"", "");
            age = int.Parse(m.Value);
        }

        // 新生儿专用可选字段:缺失/非法→null(交由引擎按通用规则分组,而非整行失败)
        int? ageDay = ParseIntField(ageDayRaw);
        int? weight = ParseIntField(weightRaw);

        var mainDx = Clean(mainDxRaw);
        if (mainDx.Length == 0)
            return (null!, "主要诊断为空", "");

        var diagnoses = new List<string>(otherDx.Count + 1) { mainDx };
        diagnoses.AddRange(otherDx.Select(Clean).Where(c => c.Length > 0));
        var procs = procedures.Select(Clean).Where(c => c.Length > 0).ToList();
        var summary = $"{(gender == "1" ? "男" : "女")}·{(age is null ? "?" : $"{age}岁")}"
            + (ageDay is { } ad ? $"({ad}天)" : "")
            + $" {mainDx}"
            + (otherDx.Count > 0 ? $" +其他诊断{otherDx.Count}" : "")
            + (procs.Count > 0 ? $" +手术{procs.Count}" : "")
            + (weight is { } w ? $" 体重{w}g" : "");

        return (new MedicalRecord
        {
            Index = index,
            Gender = gender,
            Age = age,
            AgeDay = ageDay,
            Weight = weight,
            Diagnoses = diagnoses,
            Procedures = procs,
        }, null, summary);
    }

    // ================================ CSV 与编码 ================================

    /// <summary>自动识别分隔符:逗号(默认)/Tab/分号,以首行出现频次最高者为准。</summary>
    public static char DetectDelimiter(string text)
    {
        int nl = text.IndexOf('\n');
        var first = (nl < 0 ? text : text[..nl]).Replace("\r", "");
        int comma = first.Count(c => c == ',');
        int tab = first.Count(c => c == '\t');
        int semi = first.Count(c => c == ';');
        if (tab > comma && tab > semi) return '\t';
        if (semi > comma && semi > tab) return ';';
        return ',';
    }

    // ================================ HQMS 列头映射 ================================

    public static string NormalizeHeader(string h) => (h ?? "").Trim().ToLowerInvariant();

    public static int FindAlias(Dictionary<string, int> map, params string[] aliases)
    {
        foreach (var a in aliases)
            if (map.TryGetValue(NormalizeHeader(a), out int col)) return col;
        return -1;
    }

    // 列头可能为代码(如 C03C / A12C),也可能为中文(如 出院主要诊断编码);大小写/空格不敏感。
    public static string[] GenderAliases() => ["a12c", "性别"];
    public static string[] AgeAliases() => ["a14", "年龄（岁）", "年龄(岁)", "年龄"];
    public static string[] MainDxAliases() => ["c03c", "出院主要诊断编码"];
    public static string[] MainProcAliases() => ["c14x01c", "主要手术操作编码"];
    public static string[] OtherDxCodeAliases(int n) => [$"c06x{n:D2}c", $"出院其他诊断编码{n}"];
    public static string[] OtherProcCodeAliases(int n) => [$"c35x{n:D2}c", $"其他手术操作编码{n}"];
    // 新生儿专用可选列。HQMS 标准确有独立字段:A16=日龄(天)、A17=新生儿入院体重(克)(A18x01…为出生体重,作回退)。
    public static string[] AgeDayAliases() =>
        ["a16", "年龄不足1周岁的年龄（天）", "年龄不足1周岁的年龄(天)", "日龄", "日龄(天)", "日龄（天）", "新生儿日龄"];
    public static string[] WeightAliases() =>
        ["a17", "新生儿入院体重（克）", "新生儿入院体重(克)", "入院体重", "a18x01", "新生儿出生体重（克）", "新生儿出生体重(克)", "出生体重"];

    // 识别类列(不参与分组,但结果要能拿去做后续分析)。中文名逐字取自
    // docs/HQMS首页数据采集质量与接口标准.docx:A48=病案号(字符50,必填)、A49=住院次数(数字4,必填)、
    // B12=入院时间(日期时间)、B15=出院时间(日期时间)。别凭印象改这几个字。
    public static string[] RecordNoAliases() => ["a48", "病案号"];
    public static string[] VisitNoAliases() => ["a49", "住院次数"];
    public static string[] AdmitAtAliases() => ["b12", "入院时间"];
    public static string[] DischargeAtAliases() => ["b15", "出院时间"];
    // A11=姓名(字符40)、A20=证件号码(字符18),同样逐字取自官方标准。属个人信息:
    // 屏幕上过脱敏开关(maskSummary/maskName),导出 CSV 不脱敏(用户已定口径)。
    public static string[] PatientNameAliases() => ["a11", "姓名"];
    public static string[] IdNoAliases() => ["a20", "证件号码", "身份证号"];

    /// <summary>根据表头映射出各角色列索引;-1 表示该列缺失(相应数据被忽略)。</summary>
    public static ColIdx ResolveColumns(Dictionary<string, int> map)
    {
        const int maxO = 40; // HQMS 支持最多 40 条其他诊断
        const int maxP = 40; // HQMS 支持最多 40 条其他手术操作
        var otherDx = new List<int>();
        for (int n = 1; n <= maxO; n++)
            otherDx.Add(FindAlias(map, OtherDxCodeAliases(n)));
        var otherProc = new List<int>();
        for (int n = 1; n <= maxP; n++)
            otherProc.Add(FindAlias(map, OtherProcCodeAliases(n)));
        return new ColIdx(
            FindAlias(map, GenderAliases()),
            FindAlias(map, AgeAliases()),
            FindAlias(map, MainDxAliases()),
            FindAlias(map, MainProcAliases()),
            FindAlias(map, AgeDayAliases()),
            FindAlias(map, WeightAliases()),
            otherDx, otherProc,
            FindAlias(map, RecordNoAliases()),
            FindAlias(map, VisitNoAliases()),
            FindAlias(map, AdmitAtAliases()),
            FindAlias(map, DischargeAtAliases()),
            FindAlias(map, PatientNameAliases()),
            FindAlias(map, IdNoAliases()));
    }

    /// <summary>由表头行构建 归一化列名→列索引 映射(重名取首个)。</summary>
    public static Dictionary<string, int> BuildHeaderMap(string[] header)
    {
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < header.Length; c++)
        {
            var k = NormalizeHeader(header[c]);
            if (k.Length > 0 && !headerMap.ContainsKey(k)) headerMap[k] = c;
        }
        return headerMap;
    }

    // ================================ xlsx 解析 ================================

    /// <summary>是否为 xlsx 文件:扩展名 .xlsx,或字节魔数为 ZIP(PK\x03\x04,xlsx 本质是 zip+xml)。</summary>
    public static bool LooksLikeXlsx(string fileName, byte[] bytes)
    {
        if (!string.IsNullOrEmpty(fileName) && fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return true;
        return bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;
    }

    /// <summary>读取 xlsx 首个工作表为字符串网格,复用后续表头映射与分组流水线。
    /// 空单元格输出空串;数值/日期按 Sylvan 的 GetString 文本化(首页导出通常为文本/数值)。
    ///
    /// <para>解析器为 Sylvan.Data.Excel(MIT):<b>前向逐行</b>读,不把工作表建成对象模型。
    /// 进入解析前先过 <see cref="GuardXlsx"/>(独立于解析库的 zip 解压炸弹防线,也是本路径唯一的规模闸门);
    /// 行数上限在读取过程中<b>边读边核</b>,超限立即中断,不把整表读完再判定。</para>
    /// </summary>
    public static List<string[]> ParseXlsx(byte[] bytes)
    {
        GuardXlsx(bytes);

        var rows = new List<string[]>();
        using var stream = new MemoryStream(bytes, writable: false);
        using var edr = ExcelDataReader.Create(stream, ExcelWorkbookType.ExcelXml);

        // ⚠ Sylvan 默认<b>就把首行当表头吃掉</b>:RowCount 如实报 4 行,Read() 却从 RowNumber=2 开始,
        // 首行文本改由 GetName(i) 提供。这不是可配的开关(ExcelDataReaderOptions 里没有 HasHeaderRow),
        // 所以这里显式把首行还原成 rows[0] —— 本流水线要求与 CSV 路径同构:
        // 下标 0 必须是表头行。曾经踩到:直接 Read() 会把第一行数据当表头,列全部映射不上
        // (症状是 matchedColumns = 0 且每行报"性别无法识别: 空")。
        int width = Math.Max(edr.FieldCount, 0);
        if (width > 0)
        {
            var header = new string[width];
            for (int i = 0; i < width; i++) header[i] = edr.GetName(i) ?? "";
            rows.Add(header);
        }
        // 这里刻意<b>不设列数上限</b>:宽表不是病态形状,只是表头宽一点的稀疏表。
        // 规模的真实闸门是 <see cref="BatchLimits.MaxSheetBytes"/>(见其说明:xml 里每个格子
        // 至少一个元素,解压体积在数学上就界住了格子数)。
        //
        // 列宽以表头行为准:列只有先在表头里出现才可能被 ResolveColumns 映射上,
        // 表头右侧之外的单元格对本流水线没有意义(无列名→无角色),故不为此扩张宽度。
        // 行数组刻意<b>不补齐成矩形</b>:下游按 idx 取值时已做 col &lt; r.Length 边界保护,
        // 补齐只会为超宽表凭空造出几十万个空串。
        while (edr.Read())
        {
            int n = Math.Min(edr.RowFieldCount, width);
            var cells = new string[n];
            for (int i = 0; i < n; i++) cells[i] = edr.GetString(i) ?? "";
            rows.Add(cells);

            // 边读边核:超限立即中断,不把整表读完再拒绝(计数单调递增,末轮即全表上界)。
            int dataRows = rows.Count - 1;                   // 下标 0 是表头
            if (dataRows > BatchLimits.MaxRows)
                throw new BatchTooLargeException(
                    $"工作表数据行超过上限 {BatchLimits.MaxRows}(已读 {dataRows} 行),请拆分后重试");
        }

        return rows;
    }

    /// <summary>
    /// 交给解析库之前的文件级体检(与解析库无关,换库后保留 —— 它挡的是 zip 炸弹,
    /// 而任何解析库在这一点上都会老实地把炸弹解出来)。
    /// <b>这是 xlsx 路径解压侧的限流点。</b>
    ///
    /// <para>三层检查:①条目数与解压总量(声明的);②目标工作表声明大小;
    /// ③**实际解压字节数**——zip 头里的声明长度可被伪造,所以边解压边数,超限立即中断。
    /// 这样即使遇到"声明很小、实际解压爆炸"的构造文件,也不会把内存吃光。</para>
    /// </summary>
    public static void GuardXlsx(byte[] bytes)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"xlsx 不是有效的 zip 包 - {ex.Message}");
        }

        using (zip)
        {
            if (zip.Entries.Count > BatchLimits.MaxZipEntries)
                throw new BatchTooLargeException($"xlsx 条目数 {zip.Entries.Count} 超过上限 {BatchLimits.MaxZipEntries}");

            long declaredTotal = 0;
            ZipArchiveEntry? sheet = null;
            foreach (var e in zip.Entries)
            {
                declaredTotal += e.Length;
                if (sheet is null
                    && e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                    && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    sheet = e;
            }

            if (declaredTotal > BatchLimits.MaxTotalUncompressedBytes)
                throw new BatchTooLargeException($"xlsx 解压后总大小约 {declaredTotal / 1024 / 1024}MB,超过上限 {BatchLimits.MaxTotalUncompressedBytes / 1024 / 1024}MB");
            if (sheet is null)
                throw new InvalidDataException("xlsx 内未找到工作表(xl/worksheets/sheet*.xml)");
            if (sheet.Length > BatchLimits.MaxSheetBytes)
                throw new BatchTooLargeException($"工作表解压后约 {sheet.Length / 1024 / 1024}MB,超过上限 {BatchLimits.MaxSheetBytes / 1024 / 1024}MB");

            // 声明大小不可信:实际解压时按真实字节数再数一遍
            using var s = sheet.Open();
            var buffer = new byte[64 * 1024];
            long actual = 0;
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                actual += n;
                if (actual > BatchLimits.MaxSheetBytes)
                    throw new BatchTooLargeException($"工作表实际解压超过上限 {BatchLimits.MaxSheetBytes / 1024 / 1024}MB(文件头声明的 {sheet.Length / 1024 / 1024}MB 不可信),已拒绝解析");
            }
        }
    }
}
