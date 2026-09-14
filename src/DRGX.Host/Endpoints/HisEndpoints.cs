using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// HIS 提取接口(可选能力,连接器未加载一律 503):
//   /api/his/record  中立病案预览(不分组),供前端预览/人工核对;
//   /api/his/group   提取即分组(组合根:提取 → 分组 → 费用 join;编码映射由分组引擎统一处理;
//                    映射只发生在宿主层,HIS 模块/分组器/费用模块互不引用);
//   /api/his/ids     按出院日期取号清单(轻量,预览区间内有哪些就诊号);
//   /api/his/group-by-date 批量提取即分组:数据级问题逐行回报,连接级故障整单 502。
// ============================================================================

internal static class HisEndpoints
{
    /// <summary>按日期取号清单的单次上限:limit 缺省 100,夹取到 [1, 1000]。
    /// 数字本身定义在 <see cref="BatchLimits"/> —— 上限只留一个出处(端点、解析器、表单配置共用),
    /// 但 HIS 的条数上限<b>刻意与文件路径的 MaxRows 分开</b>,理由见那两个常量的说明。</summary>
    private static int ClampHisIdsLimit(int? limit) =>
        Math.Clamp(limit ?? BatchLimits.DefaultHisIds, 1, BatchLimits.MaxHisIds);

    public static void Map(WebApplication app, WebApp svc)
    {
        // 注:engine 一律经 svc.Engine 解析,不在 Map 期缓存引用(保持单一入口)

        // ---- HIS 提取端点:中立病案(不分组),供前端预览/人工核对 ----
        app.MapGet("/api/his/record", (string id) =>
        {
            if (svc.HisConnector is not { } his) return NotLoaded();
            try
            {
                var rec = his.FetchRecord(id);
                return rec is null
                    ? ApiResults.NotFound($"HIS 无此就诊号: {id}")
                    : Results.Ok(new
                    {
                        connector = his.ConnectorId,
                        rec.PatientId, rec.Name, rec.Gender, rec.Age, rec.AgeDay, rec.Weight,
                        rec.InsuranceType, rec.AgeDetail, rec.InHospitalDays, rec.LeavingType, rec.TotalFee,
                        diagnoses = rec.Diagnoses.Select(d => new { d.Icd, d.Name, yibao = d.Yibao }),
                        operations = rec.Operations.Select(o => new { o.Icd, o.Name, yibao = o.Yibao }),
                    });
            }
            catch (DRGX.His.HisConnectorException ex)
            {
                return FetchFailed(ex);
            }
        });

        // ---- HIS 提取即分组(组合根):HIS 提取 → 宿主映射为引擎入参 → 分组 → 费用 join ----
        app.MapGet("/api/his/group", IResult (string id, string? region, string? version, bool? useCodeMap) =>
        {
            if (svc.HisConnector is not { } his) return NotLoaded();
            var (rp, regionErr) = svc.FeeRegions.ResolveRegionLoose(region);
            if (regionErr is not null) return regionErr;
            DRGX.His.HisMedicalRecord rec;
            try
            {
                var fetched = his.FetchRecord(id);
                if (fetched is null)
                    return ApiResults.NotFound($"HIS 无此就诊号: {id}");
                rec = fetched;
            }
            catch (DRGX.His.HisConnectorException ex)
            {
                return FetchFailed(ex);
            }

            var gender = BatchParsing.NormalizeGender(rec.Gender);
            if (gender is null)
                return ApiResults.BadRequest($"HIS 性别码无法识别: \"{rec.Gender}\"(应为 1/男 或 2/女)");
            var mainDx = rec.Diagnoses.Count > 0 ? rec.Diagnoses[0].Icd : null;
            if (string.IsNullOrWhiteSpace(mainDx))
                return ApiResults.BadRequest("HIS 病案无诊断编码,无法分组");

            var record = new MedicalRecord
            {
                Index = svc.NextIndex("H"),
                Gender = gender,
                // 不做 ?? 0:null(年龄未知)与 0(0 岁新生儿)语义不同,
                // 把未知写成 0 会让引擎启用日龄判定(见 Evaluator.DayAgeUsable)。
                Age = rec.Age,
                AgeDay = rec.AgeDay,
                Weight = rec.Weight ?? 0,
                Diagnoses = rec.Diagnoses.Select(d => d.Icd).ToList(),
                Procedures = rec.Operations.Select(o => o.Icd).ToList(), // 首条视为主手术,与引擎主操作口径一致
            };
            var system = CodeSystemParam.Parse(version, useCodeMap);
            var outcome = svc.Engine.Group(record, new GroupingOptions
            {
                Trace = true,
                CodeSystem = system,
            });
            // 年龄/性别文案与批量结果列同一口径(AgeText/CaseSummary),不在这里另写一份
            var summary = BatchParsing.CaseSummary(rec.Name, gender,
                BatchParsing.AgeText(rec.Age, rec.AgeDay) ?? "年龄未知");
            return Results.Ok(new
            {
                connector = his.ConnectorId,
                patient = new
                {
                    rec.PatientId, rec.Name, gender, rec.Age, rec.AgeDay, rec.Weight,
                    rec.InsuranceType, rec.AgeDetail, rec.InHospitalDays, rec.LeavingType, rec.TotalFee,
                },
                mapping = new
                {
                    mainDiagnosis = mainDx,
                    otherDiagnoses = rec.Diagnoses.Skip(1).Select(d => d.Icd),
                    mainProcedure = rec.Operations.Count > 0 ? rec.Operations[0].Icd : null,
                    otherProcedures = rec.Operations.Skip(1).Select(o => o.Icd),
                },
                diagnoses = rec.Diagnoses.Select(d =>
                {
                    // 医保版输入不做转换:code 即原码,mappedFrom 恒为 null(与引擎口径一致)
                    var mappedDx = system == CodeSystem.Guolin ? svc.Pack.DiagnosisMap.GetValueOrDefault(d.Icd, d.Icd) : d.Icd;
                    return new
                    {
                        d.Icd,
                        d.Name,
                        code = mappedDx,
                        mappedFrom = mappedDx == d.Icd ? null : d.Icd,
                        name2 = svc.Pack.DiagnosisNames.GetValueOrDefault(mappedDx, d.Name ?? ""),
                        comp = svc.Pack.Mcc.ContainsKey(mappedDx) ? "MCC"
                             : svc.Pack.Cc.ContainsKey(mappedDx) ? "CC" : null,
                    };
                }),
                operations = rec.Operations.Select(o =>
                {
                    var mappedProc = system == CodeSystem.Guolin ? svc.Pack.ProcedureMap.GetValueOrDefault(o.Icd, o.Icd) : o.Icd;
                    return new
                    {
                        o.Icd,
                        o.Name,
                        code = mappedProc,
                        mappedFrom = mappedProc == o.Icd ? null : o.Icd,
                        name2 = svc.Pack.ProcedureNames.GetValueOrDefault(mappedProc, o.Name ?? ""),
                    };
                }),
                mappings = outcome.Mappings,
                excludedComplications = outcome.ExcludedComplications,
                trace = outcome.Trace,
                fee = FeeJoin.FeeInfo(outcome.Code, rp),
                grouping = FeeJoin.WithFee(BatchParsing.OutcomeRow(1, summary, outcome,
                    drgDirect: svc.ResolveDrgDirect(outcome, record, system)), rp),
            });
        });

        // ---- HIS 按出院日期取号清单(轻量,供前端预览区间内有哪些就诊号) ----
        app.MapGet("/api/his/ids", IResult (DateOnly from, DateOnly to, int? limit) =>
        {
            if (svc.HisConnector is not { } his) return NotLoaded();
            if (from > to) return ApiResults.BadRequest("from 不能晚于 to");
            try
            {
                var ids = his.FetchIdsByDate(from, to, ClampHisIdsLimit(limit));
                return Results.Ok(new { connector = his.ConnectorId, from, to, count = ids.Count, ids });
            }
            catch (DRGX.His.HisConnectorException ex)
            {
                return FetchFailed(ex);
            }
        });

        // ---- HIS 按出院日期批量提取即分组:单条病案失败只记该行错误,不炸整单 ----
        // 连接级故障(库不可达)仍整单 502 中断;数据级问题(性别码/无诊断/分组落空)逐行回报。
        app.MapGet("/api/his/group-by-date", IResult (DateOnly from, DateOnly to, int? limit, string? region, string? version, bool? useCodeMap) =>
        {
            if (svc.HisConnector is not { } his) return NotLoaded();
            var (rp, regionErr) = svc.FeeRegions.ResolveRegionLoose(region);
            if (regionErr is not null) return regionErr;
            if (from > to) return ApiResults.BadRequest("from 不能晚于 to");
            var system = CodeSystemParam.Parse(version, useCodeMap);
            IReadOnlyList<string> ids;
            try { ids = his.FetchIdsByDate(from, to, ClampHisIdsLimit(limit)); }
            catch (DRGX.His.HisConnectorException ex)
            {
                return FetchFailed(ex);
            }

            // 与文件批量路径同构:产出 BatchRow、共用同一套明细预算与同一张结果表。
            // (曾经这里是匿名对象,前端还得做一次字段搬运;类型统一后两路结果在界面上完全一致。)
            var rows = new List<BatchRow>(ids.Count);
            int ok = 0, fail = 0;
            foreach (var id in ids)
            {
                string? error = null;
                string? summary = id;
                DRGX.His.HisMedicalRecord? rec = null;
                MedicalRecord? mrec = null; // 供分组后按病案判定直赋档(HIS 批量与单条同口径)
                GroupOutcome? outcome = null;
                try
                {
                    rec = his.FetchRecord(id);
                    if (rec is null) error = "HIS 无此就诊号";
                }
                catch (DRGX.His.HisConnectorException ex)
                {
                    // 连接级故障在批量内必然逐条复发,整单中断比静默刷错误行更可诊断
                    return Results.Problem(statusCode: 502, title: "HIS 提取失败", detail: $"批量在就诊号 {id} 处中断: {ex.Message}");
                }

                if (rec is not null)
                {
                    var gender = BatchParsing.NormalizeGender(rec.Gender);
                    summary = BatchParsing.CaseSummary(rec.Name, gender,
                        BatchParsing.AgeText(rec.Age, rec.AgeDay) ?? "年龄未知");
                    if (gender is null) error = $"性别码无法识别: \"{rec.Gender}\"";
                    else if (rec.Diagnoses.Count == 0) error = "无诊断编码,无法分组";
                    else
                    {
                        var record = new MedicalRecord
                        {
                            Index = svc.NextIndex("H"),
                            Gender = gender,
                            // 同单病例路径:未知年龄保持 null,不写成 0
                            Age = rec.Age,
                            AgeDay = rec.AgeDay,
                            Weight = rec.Weight ?? 0,
                            Diagnoses = rec.Diagnoses.Select(d => d.Icd).ToList(),
                            Procedures = rec.Operations.Select(o => o.Icd).ToList(),
                        };
                        mrec = record;
                        outcome = svc.Engine.Group(record, new GroupingOptions
                        {
                            Trace = true,
                            CodeSystem = system,
                        });
                    }
                }

                if (error is not null)
                {
                    fail++;
                    // 错误行不带明细(没有分组结果可看),与文件路径的解析失败行同形状
                    rows.Add(new BatchRow(rows.Count + 1, summary, PatientId: id, Error: error));
                }
                else
                {
                    ok++;
                    // HIS 总费用随行透传(对账用),与文件路径的差别只剩这一个字段
                    var row = FeeJoin.WithFee(BatchParsing.OutcomeRow(rows.Count + 1, summary, outcome!,
                        patientId: id,
                        drgDirect: svc.ResolveDrgDirect(outcome!, mrec!, system)), rp)
                        with { Fee = rec?.TotalFee };
                    // 姓名从 HIS 病案带出(界面上唯一会显示姓名的地方,屏幕过脱敏开关)。
                    // 就诊号已在 PatientId 里,故不再重复填病案号;HIS 连接器未取证件号,留 null。
                    rows.Add(BatchParsing.TrimDetail(row.WithCase(mrec!)
                        .WithIds(null, null, null, null, rec?.Name, null)
                        .WithFeeEstimate(svc, rp, null, null)));
                }
            }
            return Results.Ok(new { connector = his.ConnectorId, from, to, count = ids.Count, ok, fail, rows });
        });
    }

    /// <summary>HIS 能力未启用(启动时未传 --his)。</summary>
    private static IResult NotLoaded() => Results.Problem(statusCode: 503, title: "HIS 连接器未加载",
        detail: "启动时可用 --his <his-config.json> 指定连接配置");

    /// <summary>连接级提取故障(库不可达/超时等)。</summary>
    private static IResult FetchFailed(DRGX.His.HisConnectorException ex) =>
        Results.Problem(statusCode: 502, title: "HIS 提取失败", detail: ex.Message);
}
