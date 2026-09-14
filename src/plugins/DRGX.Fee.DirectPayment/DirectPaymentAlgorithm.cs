namespace DRGX.Fee.DirectPayment;

/// <summary>
/// 直付(direct-payment)算法:病组支付标准由 payment.csv 逐 DRG 口径列给出(如 支付标准_三级职工)。
/// 与点数法的本质区别:支付标准来自查表而非公式,因此不需要点值/pointScale 配置;
/// 倍率 = 实际费用 ÷ 支付标准(查表值);正常倍率按表支付,低倍率按实支付,
/// 高倍率暂不估钱(v1 保守口径,地方高倍率结算规则待补)。无查表行时回退权重表 avg_cost。
/// </summary>
public sealed class DirectPaymentAlgorithm : IFeeAlgorithm
{
    public string AlgorithmId => "direct-payment";

    public string AlgorithmVersion => "2.0";

    public FeeEstimate Estimate(FeeEstimateRequest request, FeeEstimateContext context)
    {
        var pack = context.Pack;
        var entry = context.Entry;
        if (entry.Status != "published" || entry.Rw is null)
            throw new FeeWeightsException($"DRG \"{entry.DrgCode}\" 未公布权重,不参与费用估算");

        // 支付标准:payment.csv 口径列 → 权重表 avg_cost 回退
        decimal? standard = null;
        string? column = null;
        if (pack.TryGetPayments(entry.DrgCode, out var payments) && payments is not null)
        {
            column = ResolveKey(payments, request);
            if (column is not null) standard = payments[column];
        }
        standard ??= entry.AvgCost;
        if (standard is null or <= 0m)
            throw new FeeWeightsException(
                $"DRG \"{entry.DrgCode}\" 无 payment.csv 命中口径且权重表无 avg_cost,无法确定支付标准");

        var p = pack.Parsed;
        decimal factor = pack.ResolveLevelFactor(entry.DrgCode, request.HospitalLevel);
        var m = p.Multiplier;

        var details = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
        {
            ["standard"] = standard,
            ["lowRateFee"] = null,
            ["highRateFee"] = null,
            ["highCap"] = null,
        };

        string? rateType = null;
        decimal? ratio = null;
        decimal? totalPoint = null;
        decimal? estimated = null;

        if (request.PatientFee is > 0)
        {
            ratio = request.PatientFee.Value / standard.Value;
            decimal cap = m.HighCapFor(entry.Rw.Value);
            decimal lowRateFee = m.LowRule == LowRateRule.LowerOf
                ? standard.Value * m.LowRate * Math.Min(1m, factor)
                : standard.Value * m.LowRate;
            decimal highRateFee = standard.Value * cap;
            details["lowRateFee"] = p.Quantize(lowRateFee);
            details["highRateFee"] = p.Quantize(highRateFee);
            details["raw.lowRateFee"] = lowRateFee;
            details["raw.highRateFee"] = highRateFee;
            details["highCap"] = cap;

            if (ratio.Value < m.LowRate)
            {
                rateType = "低倍率";
                estimated = request.PatientFee.Value; // 直付口径无点值换算,低倍率按实支付
            }
            else if (ratio.Value > cap)
            {
                rateType = "高倍率"; // v1 保守口径:直付区间外不估钱,待地区规则补充
            }
            else
            {
                rateType = "正常倍率";
                estimated = standard;
            }
        }
        else
        {
            estimated = standard; // 无实际费用:仅给查表支付标准
        }

        // 输出边界统一量化到「分」(见 PointValueAlgorithm 的同名说明);全精度原值留在 raw.* 键
        details["raw.standard"] = standard.Value;
        details["raw.estimated"] = estimated;
        var standardOut = p.Quantize(standard.Value);
        var estimatedOut = estimated is null ? (decimal?)null : p.Quantize(estimated.Value);
        var gapOut = estimatedOut is null || request.PatientFee is null
            ? (decimal?)null
            : p.Quantize(estimatedOut.Value - request.PatientFee.Value);
        details["standard"] = standardOut;

        return new FeeEstimate(
            entry.DrgCode, entry.DrgName, pack.ResolveMark(entry.DrgCode),
            entry.Rw.Value, context.WeightSource,
            factor, null, ratio, rateType, totalPoint, standardOut, estimatedOut, gapOut,
            AlgorithmId, AlgorithmVersion, details);
    }

    /// <summary>按 等级 + 险种 组合匹配口径列名;逐级放宽,单列时直接取。</summary>
    private static string? ResolveKey(IReadOnlyDictionary<string, decimal> payments, FeeEstimateRequest request)
    {
        if (payments.Count == 1) return payments.Keys.First();

        var level = request.HospitalLevel?.Trim();
        var type = request.PatientType?.Trim();
        var keys = payments.Keys.ToList();

        if (!string.IsNullOrEmpty(level) && !string.IsNullOrEmpty(type))
        {
            var hit = keys.FirstOrDefault(k => k.Contains(level, StringComparison.OrdinalIgnoreCase)
                                            && k.Contains(type, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        if (!string.IsNullOrEmpty(level))
        {
            var hit = keys.FirstOrDefault(k => k.Contains(level, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        if (!string.IsNullOrEmpty(type))
        {
            var hit = keys.FirstOrDefault(k => k.Contains(type, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        throw new FeeWeightsException(
            $"payment.csv 口径列无法匹配(level={level ?? "空"}, type={type ?? "空"}),可用口径: {string.Join("/", keys)}");
    }

}
