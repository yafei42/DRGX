using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// 基础信息端点:方案版本(/api/info)、ADRG 入组明细(/api/adrg)、基层病组清单。
// ============================================================================

internal static class InfoEndpoints
{
    public static void Map(WebApplication app, WebApp svc)
    {
        // ---- 方案信息(页头版本徽标、页脚声明) ----
        app.MapGet("/api/info", () =>
        {
            var pack = svc.Pack; // 经容器取包(单一入口)
            var yibao = svc.Index(CodeSystem.Yibao);
            var guolin = svc.Index(CodeSystem.Guolin);
            return Results.Ok(new
            {
                scheme = pack.Manifest.Scheme,
                version = pack.Manifest.Version,
                revision = pack.Manifest.Revision,
                // 对外展示的数据批次:页头徽标用这个,不用 revision(那是我方数据包修订号,不是官方版本)
                sourceDate = pack.Manifest.SourceDate,
                builtAt = pack.Manifest.BuiltAt,
                counts = new
                {
                    diagnoses = pack.DiagnosisNames.Count,
                    procedures = pack.ProcedureNames.Count,
                    groups = pack.Groups.Count,
                    mdc = pack.MdcChain.Count,
                    adrg = pack.MdcChain.Sum(m => m.Adrgs.Count),
                    subgroups = pack.MdcChain.Sum(m => m.Adrgs.Sum(a => a.Splits.Count)),
                },
                // 编码版本:选项、默认值与各自的字典规模都由服务端下发,前端不写死标签与数字
                // (前端写一份、后端写一份,迟早变成"界面上写着国临版、实际按医保版分组")。
                codeVersions = new
                {
                    @default = SearchEndpoints.VersionId(CodeSystemParam.Default),
                    options = new object[]
                    {
                        new
                        {
                            id = "guolin",
                            label = "国临版",
                            title = "国家临床版（病案首页口径）：输入按国临目录受理，分组前自动转医保版编码",
                            diagnoses = guolin.Diagnoses.Length,
                            procedures = guolin.Procedures.Length,
                        },
                        new
                        {
                            id = "yibao",
                            label = "医保版",
                            title = "国家医保版（结算清单口径）：输入即分组口径，不做任何编码转换",
                            diagnoses = yibao.Diagnoses.Length,
                            procedures = yibao.Procedures.Length,
                        },
                    },
                },
                // 批量导入上限:界面上的提示文案与前端预检都取这里的数字,不在前端再抄一份
                // (两处各写一个数字,迟早会漂移成"前端说行、后端说不行")。
                limits = new
                {
                    maxUploadMb = BatchLimits.MaxUploadMb,
                    maxRows = BatchLimits.MaxRows,
                    // 逐行明细与结果表的行数上限:两侧共用这两个数(预览段 / 问题段)
                    maxDetailRows = BatchLimits.MaxDetailRows,
                    maxIssueRows = BatchLimits.MaxIssueRows,
                    maxHisIds = BatchLimits.MaxHisIds,
                    defaultHisIds = BatchLimits.DefaultHisIds,
                },
            });
        });

        // ---- ADRG 原始条件描述:结果详情里按 ADRG 码查入组明细(官方文本/主诊断/主手术数量) ----
        app.MapGet("/api/adrg/{code}", (string code) =>
        {
            var hit = svc.AdrgInfo.TryGetValue(code.Trim(), out var v) ? v : null;
            return Results.Ok(new { code = code.Trim(), hit });
        });

        // ---- 基层病组清单:前端结果标注用,整表下发便于前端一次缓存 ----
        // 源为包根 primary-groups.csv(行序即本接口顺序);文件缺失时为空数组,前端不挂徽标。
        app.MapGet("/api/primary-groups", () =>
        {
            var list = svc.PrimaryGroups.Select(x => new
            {
                code = x.Code,
                name = x.Name,
                category = x.Category,
                primary = true,
            });
            return Results.Ok(list);
        });
    }
}
