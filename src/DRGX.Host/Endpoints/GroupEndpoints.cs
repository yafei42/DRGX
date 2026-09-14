using System.Text.Json;
using DRGX.Engine;
using DRGX.Text;

namespace DRGX.Host;

// ============================================================================
// 分组端点:单病例(/api/group,含判定轨迹,费用随结果一次下发)、CSV/XLSX 文件批量
// (/api/group/batch/csv,HQMS 首页格式)。
// 行级解析细节在 BatchParsing(纯函数,单测覆盖);本类只做编排与行数上限控制。
// ============================================================================

internal static class GroupEndpoints
{
    // 行数/体积上限单一定义在 BatchLimits(端点、解析器、表单配置共用,避免多处数字漂移)

    /// <summary>与 HttpJsonOptions 同参(经 ApiJson 单源):分组结果 JSON 组装(内嵌费用信息)用。</summary>
    private static readonly JsonSerializerOptions ApiJsonOpts = ApiJson.Create();

    // 直赋档说明(高危妊娠直赋/机器人直赋)一律走 WebApp.ResolveDrgDirect(病案级重载):
    // 单病例/文件批量/HIS 三条路径共用同一判定,口径唯一。

    public static void Map(WebApplication app, WebApp svc)
    {
        // 注:engine 一律经 svc.Engine 解析,不在 Map 期缓存引用(保持单一入口)

        // ---- 单病例分组:默认携带命中路径轨迹,verbose 追加未命中明细 ----
        app.MapPost("/api/group", (GroupRequest req) =>
        {
            var (rp, regionErr) = svc.FeeRegions.ResolveRegionLoose(req.Region);
            if (regionErr is not null) return regionErr;
            if (string.IsNullOrWhiteSpace(req.Gender))
                return ApiResults.BadRequest("请选择患者性别");
            var main = BatchParsing.Clean(req.MainDiagnosis);
            if (main.Length == 0)
                return ApiResults.BadRequest("请填写主要诊断");

            var diagnoses = new List<string> { main };
            diagnoses.AddRange((req.OtherDiagnoses ?? []).Select(BatchParsing.Clean).Where(c => c.Length > 0));
            var procedures = new List<string>();
            var mainProc = BatchParsing.Clean(req.MainProcedure);
            if (mainProc.Length > 0) procedures.Add(mainProc);
            procedures.AddRange((req.OtherProcedures ?? []).Select(BatchParsing.Clean).Where(c => c.Length > 0));

            var record = new MedicalRecord
            {
                Index = svc.NextIndex("W"),
                Gender = req.Gender.Trim(),
                Age = req.Age,
                AgeDay = req.AgeDay,
                Weight = req.Weight,
                Diagnoses = diagnoses,
                Procedures = procedures,
            };
            var system = CodeSystemParam.Parse(req.Version, req.UseCodeMap);
            var outcome = svc.Engine.Group(record, new GroupingOptions
            {
                Trace = true,
                VerboseTrace = req.Verbose == true,
                CodeSystem = system,
            });
            // ADRG 的 origin(rules 内官方入组条件原文)是权威描述,优先于引擎生成的条件树描述
            string? adrgOrigin = null;
            if (outcome.Adrg is not null && svc.AdrgInfo.TryGetValue(outcome.Adrg, out var adrgMeta) && !string.IsNullOrEmpty(adrgMeta.Cond))
            {
                outcome = outcome with { AdrgReason = adrgMeta.Cond };
                adrgOrigin = adrgMeta.Cond;
            }
            // 费用数据随分组结果一次下发(RW/参考费用来自请求级区域包),前端无需二次请求费用接口
            var node = JsonSerializer.SerializeToNode(outcome, ApiJsonOpts);
            if (node is not null)
            {
                // adrgOrigin 与 adrgReason 同值但语义不同:前者是官方原文(前端"条件原文"直接取用),
                // 后者是给未升级前端/日志看的兼容字段。二者都来自 rules 内该 ADRG 的 origin。
                if (adrgOrigin is not null)
                    node["adrgOrigin"] = JsonSerializer.SerializeToNode(adrgOrigin, ApiJsonOpts);
                // drgOrigin = 细分组(DRG)的官方条件原文(rules 内 split.origin,合并症等级/年龄属性/特殊入组条件三列综合)
                if (!string.IsNullOrEmpty(outcome.Code) && svc.DrgOrigin.TryGetValue(outcome.Code, out var drgOrigin))
                    node["drgOrigin"] = JsonSerializer.SerializeToNode(drgOrigin, ApiJsonOpts);
                // drgDirect = 直赋档说明(高危妊娠直赋/机器人直赋):前端标题优先于 MCC/CC 推断文案。
                // 判定按 split 条件,与批量路径共用 ResolveDirect(口径单一)。
                var drgDirect = svc.ResolveDrgDirect(outcome, record, system);
                if (drgDirect is not null)
                    node["drgDirect"] = JsonSerializer.SerializeToNode(drgDirect, ApiJsonOpts);
                var fee = FeeJoin.FeeInfo(outcome.Code, rp);
                if (fee is not null)
                    node["fee"] = JsonSerializer.SerializeToNode(fee, ApiJsonOpts);
            }
            return Results.Ok(node);
        });

        // ---- 批量分组:上传 CSV/XLSX 文件(HQMS 首页),服务端按 HQMS 规范字段映射 ----
        // 参照 文档/HQMS首页数据采集质量与接口标准。
        // 列头按别名匹配(可能为代码如 C03C/A12C,也可能为中文如 出院主要诊断编码),大小写/空格不敏感。
        app.MapPost("/api/group/batch/csv", async (HttpRequest req) =>
        {
            try
            {
                if (!req.HasFormContentType)
                    return ApiResults.BadRequest("请使用 multipart/form-data 表单上传 CSV/XLSX 文件");

                // 先按声明的请求体长度拒绝 —— 此刻 body 还没被读进内存。
                // 旧顺序是「ReadFormAsync → 再比 file.Length」,等于让框架先把整个 multipart
                // 缓冲下来(FormOptions 默认上限 128MB)才回复"文件过大",上限形同虚设。
                if (req.ContentLength is > BatchLimits.MaxUploadBytes)
                    return ApiResults.TooLarge(
                        $"上传内容 {BatchLimits.Mb(req.ContentLength.Value)}MB,超过上限 {BatchLimits.MaxUploadMb}MB;" +
                        "请拆分文件后分批上传");

                var form = await req.ReadFormAsync();
                var file = form.Files["file"];
                var format = "hqms"; // 仅支持 HQMS 首页格式;请求不传 format(前端已删假参数),响应回显实际采用的格式
                var (rp, regionErr) = svc.FeeRegions.ResolveRegionLoose(form["region"].ToString());
                // 费用测算口径:医院等级 / 人员(医保)类型。留空 → 由地区算法取自己的默认值,
                // 与单病例「费用测算」不填时的行为一致(不在这里另设默认,免得两处口径漂移)。
                var level = form["level"].ToString().Trim();
                var type = form["type"].ToString().Trim();
                // 编码版本:与单病例路径同一开关,表单字段 version=guolin|yibao,缺省走默认版本。
                var system = CodeSystemParam.Parse(form["version"].ToString());
                if (level.Length == 0) level = null;
                if (type.Length == 0) type = null;
                // 进度流是 opt-in:请求带 progress=1 才走 NDJSON 分块响应。
                // 不带时完全照旧(单次 JSON)—— 回归脚本与其它调用方一行都不用改。
                var wantProgress = form["progress"].ToString() == "1";
                if (wantProgress) BatchProgress.ApplyHeaders(req);
                if (regionErr is not null) return regionErr;
                if (file is null || file.Length == 0)
                    return ApiResults.BadRequest("未收到文件或文件为空");
                if (file.Length > BatchLimits.MaxUploadBytes)
                    return ApiResults.TooLarge(
                        $"文件 {BatchLimits.Mb(file.Length)}MB,超过上限 {BatchLimits.MaxUploadMb}MB;" +
                        "请拆分文件后分批上传");

                // 直接读进精确大小的数组:不再 MemoryStream + CopyTo + ToArray 三次拷贝
                var bytes = new byte[checked((int)file.Length)];
                await using (var fs = file.OpenReadStream())
                    await fs.ReadExactlyAsync(bytes);
                // 支持 CSV(文本)与 XLSX(二进制):按扩展名/魔数分流,后续表头映射与分组流水线完全共用
                List<string[]> rows;
                if (BatchParsing.LooksLikeXlsx(file.FileName, bytes))
                {
                    try { rows = BatchParsing.ParseXlsx(bytes); }
                    catch (BatchTooLargeException ex) { return ApiResults.TooLarge("xlsx " + ex.Message); }
                    catch (Exception ex) { return ApiResults.BadRequest("xlsx 解析失败:" + ex.Message); }
                }
                else
                {
                    var text = TextDecoder.Decode(bytes);
                    var delim = BatchParsing.DetectDelimiter(text);
                    rows = Csv.ReadRows(text, delim);
                }
                if (rows.Count == 0)
                    return Results.Ok(new { total = 0, ok = 0, fail = 0, parseErrors = 0, skipped = 0, matchedColumns = 0, format, rows = Array.Empty<object>(), note = "文件无内容" });

                var header = rows[0].Select(h => (h ?? "").Trim()).ToArray();
                var headerMap = BatchParsing.BuildHeaderMap(header);
                var idx = BatchParsing.ResolveColumns(headerMap);
                var dataRows = rows.Skip(1).Where(r => r.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();
                // CSV 路径:行数超限直接拒绝(与 xlsx 路径口径一致),不静默截断
                if (dataRows.Count > BatchLimits.MaxRows)
                    return ApiResults.TooLarge(
                        $"数据行 {dataRows.Count} 超过上限 {BatchLimits.MaxRows},请拆分后重试");

                var outRows = new List<BatchRow>();
                int okCount = 0, failCount = 0, parseErrorCount = 0, lineNo = 0;
                foreach (var r in dataRows)
                {
                    lineNo++;
                    await BatchProgress.ReportAsync(req, wantProgress, lineNo, dataRows.Count);
                    string Get(int col) => col >= 0 && col < r.Length ? (r[col] ?? "") : "";
                    var genderRaw = Get(idx.Gender);
                    var ageRaw = Get(idx.Age);
                    var mainDxRaw = Get(idx.MainDx);
                    var mainProcRaw = Get(idx.MainProc);
                    // 每个其他诊断/手术列内的 ';'/'、' 视为多码分隔符,避免多码同列时被当成单个非法码
                    var otherDx = idx.OtherDx.Where(c => c >= 0).SelectMany(col => BatchParsing.SplitCodes(Get(col))).Where(s => s.Length > 0).ToList();
                    var otherProc = idx.OtherProc.Where(c => c >= 0).SelectMany(col => BatchParsing.SplitCodes(Get(col))).Where(s => s.Length > 0).ToList();
                    var procedures = new List<string>();
                    if (mainProcRaw.Trim().Length > 0) procedures.Add(mainProcRaw);
                    procedures.AddRange(otherProc);

                    var ageDayRaw = Get(idx.AgeDay);
                    var weightRaw = Get(idx.Weight);
                    // 识别类列:解析失败的行也要带上,否则用户拿着一张"第 37 行性别不识别"
                    // 的结果找不到那是哪位病人(病案号是回 HIS 核对的唯一凭据)。
                    string? recNo = Get(idx.RecordNo), visitNo = Get(idx.VisitNo);
                    // 个人信息:屏幕过脱敏开关,导出 CSV 不脱敏(用户已定口径)。
                    string? nameRaw = Get(idx.PatientName), idNoRaw = Get(idx.IdNo);
                    string? admitAt = Get(idx.AdmitAt), dischargeAt = Get(idx.DischargeAt);
                    var (rec, err, summary) = BatchParsing.BuildRecord($"C{lineNo:0000}", genderRaw, ageRaw, mainDxRaw, otherDx, procedures, ageDayRaw, weightRaw);
                    if (err is not null)
                    {
                        parseErrorCount++;
                        outRows.Add(new BatchRow(lineNo, summary, Error: err).WithIds(recNo, visitNo, admitAt, dischargeAt, nameRaw, idNoRaw));
                        continue;
                    }
                    // 文件批量与单病例同一编码版本口径(默认国临版→转医保版)
                    var outcome = svc.Engine.Group(rec!, new GroupingOptions { Trace = true, CodeSystem = system });
                    if (outcome.Status == GroupStatus.Success) okCount++; else failCount++;
                    outRows.Add(BatchParsing.TrimDetail(FeeJoin.WithFee(BatchParsing.OutcomeRow(lineNo, summary, outcome,
                        drgDirect: svc.ResolveDrgDirect(outcome, rec!, system)), rp)
                        .WithCase(rec!).WithIds(recNo, visitNo, admitAt, dischargeAt, nameRaw, idNoRaw)
                        .WithFeeEstimate(svc, rp, level, type)));
                }

                var matchedColumns = new[] { idx.Gender, idx.Age, idx.MainDx, idx.MainProc, idx.AgeDay, idx.Weight }
                    .Concat(idx.OtherDx).Concat(idx.OtherProc).Count(c => c >= 0);
                var payload = (object)new
                {
                    total = okCount + failCount,
                    ok = okCount,
                    fail = failCount,
                    parseErrors = parseErrorCount,
                    skipped = 0,   // 保留字段以兼容前端;超限现在是直接拒绝(413),不再静默跳过
                    matchedColumns,
                    format,
                    rows = outRows,
                };
                if (wantProgress)
                {
                    await BatchProgress.FinalAsync(req, payload);
                    return BatchProgress.Written;   // 响应体已亲手写完,框架别再动它
                }
                return Results.Ok(payload);
            }
            // Kestrel 自身也会按 MaxRequestBodySize 拒绝请求体(无 Content-Length 的分块上传走这条),
            // 它抛的是 413 的 BadHttpRequestException;不接住就会落到下面的通用分支变成 500 + 无信息文案。
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return ApiResults.TooLarge(
                    $"上传内容超过上限 {BatchLimits.MaxUploadMb}MB;请拆分文件后分批上传");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CSV 处理异常: {ex}"); // 完整堆栈只进服务端日志
                return Results.Problem(title: "CSV 处理异常", detail: ex.Message); // 堆栈不回传客户端
            }
        });
    }
}
