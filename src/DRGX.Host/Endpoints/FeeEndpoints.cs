using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// 费用端点:区域清单、估算、计算选项。费用数据(RW/参考费用/算法参数)内嵌地区费用包,
// 请求级 region 参数选择区域(费用必需端点,严格解析);算法由 manifest.algorithm 选择,
// 宿主零耦合——费用模块不引用分组器。
// ============================================================================

internal static class FeeEndpoints
{
    public static void Map(WebApplication app, WebApp svc)
    {
        // 注:pack 一律经 svc.Pack 解析,不在 Map 期缓存引用(保持单一入口)

        // ---- 可用区域清单(前端区域下拉数据源):key 即各端点的请求级 region 参数 ----
        app.MapGet("/api/fee/regions", () => Results.Ok(svc.FeeRegions.Current.Packs
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new
            {
                key = kv.Key,
                id = kv.Value.Manifest.Id,
                regionName = kv.Value.Manifest.RegionName,
                dataVersion = kv.Value.Manifest.DataVersion,
                algorithm = kv.Value.Manifest.Algorithm,
                weights = kv.Value.WeightRows,
                published = kv.Value.WeightPublished,
                isDefault = kv.Key == svc.FeeRegions.Current.DefaultKey,
                dataLevel = kv.Value.Manifest.DataLevel, // reference=非该地区公布值,前端下拉标注「非本地值」
                demo = kv.Value.Manifest.Demo,           // true=演示数据,前端下拉标注「演示数据」并在费用面板提示
            })));

        // ---- 费用估算(地区算法):DRG 码 + 实际费用(可选) + 医院等级 + 人员类型 ----
        app.MapGet("/api/fee/estimate", IResult (string drg, decimal? fee, string? level, string? type,
            string? region) =>
        {
            var (rp, regionErr) = svc.FeeRegions.ResolveRegion(region);
            if (regionErr is not null) return regionErr;
            try
            {
                var algorithm = svc.FeeAlgorithms.Resolve(rp!.Manifest.Algorithm);
                var request = new DRGX.Fee.FeeEstimateRequest(drg, fee, level, type);
                var found = rp.FindWeight(drg);
                DRGX.Fee.FeeEstimate result;
                if (found is null)
                    return ApiResults.NotFound($"地区包权重表无此 DRG 码: {drg}");
                result = algorithm.Estimate(request,
                    new DRGX.Fee.FeeEstimateContext(found.Value.Entry, found.Value.Source, rp));
                return Results.Ok(new
                {
                    result.DrgCode,
                    result.DrgName,
                    result.Mark,
                    weight = result.Weight,
                    weightSource = result.WeightSource,
                    factor = result.Factor,
                    pointValue = result.PointValue,
                    ratio = result.Ratio,
                    rateType = result.RateType,
                    totalPoint = result.TotalPoint,
                    standardFee = result.StandardFee,
                    estimatedFee = result.EstimatedFee,
                    feeGap = result.FeeGap,
                    // 实际费用相对「支付标准」的偏离(元,服务端量化)。
                    // 刻意由服务端算:前端用 JS double 做 curFee - standardFee 会引入二进制浮点误差,
                    // 而这是展示给经办人员看的金额。注意它与 feeGap 不是一回事 ——
                    // feeGap = 估算支付 − 实际费用(盈亏),standardGap = 实际费用 − 标准支付(超支/结余)。
                    standardGap = fee is null ? (decimal?)null : rp!.Parsed.Quantize(fee.Value - result.StandardFee),
                    lowRateThreshold = result.Details?.TryGetValue("lowRateFee", out var l) == true ? l : null,
                    highRateThreshold = result.Details?.TryGetValue("highRateFee", out var h) == true ? h : null,
                    details = result.Details,
                    provenance = new
                    {
                        scheme = rp.Manifest.SchemeVersion,
                        packRevision = svc.Pack.Manifest.Revision,
                        regionId = rp.Manifest.Id,
                        regionDataVersion = rp.Manifest.DataVersion,
                        algorithm = result.Algorithm,
                        algorithmVersion = result.AlgorithmVersion,
                    },
                });
            }
            catch (DRGX.Fee.FeeWeightsException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        // ---- 费用计算选项:医院等级 / 人员(医保)类型(来自地区包 parameters.json,驱动前端下拉) ----
        app.MapGet("/api/fee/options", IResult (string? region) =>
        {
            var (rp, regionErr) = svc.FeeRegions.ResolveRegion(region);
            if (regionErr is not null) return regionErr;
            try
            {
                var p = rp!.Parameters;
                string[] Keys(string name) => p.TryGetProperty(name, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? e.EnumerateObject().Select(x => x.Name).ToArray() : [];
                return Results.Ok(new
                {
                    regionId = rp.Manifest.Id,
                    regionName = rp.Manifest.RegionName,
                    dataVersion = rp.Manifest.DataVersion, // 费用面板标题展示口径版本(换版后可区分 2026-r1/r2)
                    dataLevel = rp.Manifest.DataLevel,     // reference 时费用面板顶部提示「非执行口径」
                    demo = rp.Manifest.Demo,               // true 时费用面板顶部提示「演示数据,不得用于结算」
                    // 等级两个来源:逐 DRG 系数列(levels)与全局等级系数(levelFactors),并集供前端选择
                    levels = Keys("levels").Concat(Keys("levelFactors")).Distinct().ToArray(),
                    types = Keys("pointValues"),
                });
            }
            catch (DRGX.Fee.FeeWeightsException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });
    }
}
