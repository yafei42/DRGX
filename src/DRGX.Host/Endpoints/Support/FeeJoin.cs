namespace DRGX.Host;

/// <summary>
/// 费用 join 展示层:把地区费用包的权重(RW/参考费用)拼到分组结果上。
/// 分组器不承载支付参数——join 只发生在 Web 宿主(Group/His 端点共用)。
/// </summary>
internal static class FeeJoin
{
    /// <summary>按 DRG 码取地区包权重(RW/参考费用);地区包为空或未命中返回 null。</summary>
    public static object? FeeInfo(string? code, DRGX.Fee.RegionPack? rp)
    {
        if (rp is null || string.IsNullOrWhiteSpace(code)) return null;
        var f = rp.FindWeight(code);
        return f is null
            ? null
            : new { rw = f.Value.Entry.Rw, avgCost = f.Value.Entry.AvgCost, status = f.Value.Entry.Status };
    }

    /// <summary>费用 join 回填:按 DRG 码从请求级区域包取 RW/参考费用填入批量行。</summary>
    public static BatchRow WithFee(BatchRow row, DRGX.Fee.RegionPack? rp)
    {
        if (rp is null || row.Code is null) return row;
        var f = rp.FindWeight(row.Code);
        if (f is null) return row;
        return row with
        {
            Weight = f.Value.Entry.Rw?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Cost = f.Value.Entry.AvgCost?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>费用测算回填:按行的 DRG 码跑<b>地区算法本身</b>,把测算分解摊到行上。
    ///
    /// <para>关键:这里必须复用 <c>FeeAlgorithms.Resolve(...).Estimate(...)</c> ——
    /// 与 <c>/api/fee/estimate</c> 同一条代码路径、同一个 <c>FeeEstimate</c> 结果。
    /// 地区算法是<b>可插拔</b>的(佛山/枣庄/天津/周口的公式并不一样),在批量路径里
    /// 另写一遍"支付 = RW × 系数 × 点值"就是把同一件事实现两次,早晚与单病例视图对不上。</para>
    /// <para>权重表里没有该 DRG 码、或地区包参数有问题时<b>原样返回该行</b>(不退化为整单失败):
    /// 一条码查不到不该让几万行的批量结果作废。</para>
    /// </summary>
    public static BatchRow WithFeeEstimate(this BatchRow row, WebApp svc, DRGX.Fee.RegionPack? rp,
        string? level, string? type)
    {
        if (rp is null || string.IsNullOrWhiteSpace(row.Code)) return row;
        var found = rp.FindWeight(row.Code);
        if (found is null) return row;
        try
        {
            var algo = svc.FeeAlgorithms.Resolve(rp.Manifest.Algorithm);
            var est = algo.Estimate(
                new DRGX.Fee.FeeEstimateRequest(row.Code, null, level, type),
                new DRGX.Fee.FeeEstimateContext(found.Value.Entry, found.Value.Source, rp));
            return row with
            {
                Factor = BatchParsing.Num(est.Factor),
                PointValue = BatchParsing.Num(est.PointValue),
                TotalPoint = BatchParsing.Num(est.TotalPoint),
                RateType = est.RateType,
                StandardFee = BatchParsing.Num(est.StandardFee),
                EstimatedFee = BatchParsing.Num(est.EstimatedFee),
                LowRateFee = est.Details?.TryGetValue("lowRateFee", out var l) == true ? BatchParsing.Num(l) : null,
                HighRateFee = est.Details?.TryGetValue("highRateFee", out var h) == true ? BatchParsing.Num(h) : null,
            };
        }
        catch (DRGX.Fee.FeeWeightsException)
        {
            return row;
        }
    }
}
