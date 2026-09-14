namespace DRGX.Fee.PointValue;

/// <summary>
/// 点数法(point-value)费用算法。
/// 口径对齐达州 DRG 点数法:支付标准 = RW × 系数 × 点值 × 点数尺度,
/// 倍率 = 实际费用 ÷ 病组次均费用,零点倍率 Z = 系数 × 医院点值控费系数。
/// 计算基准为全市次均费用(cityAvgCost,即元/RW 基准费率),标准点值 = cityAvgCost ÷ pointScale。
/// </summary>
public sealed class PointValueAlgorithm : IFeeAlgorithm
{
    public string AlgorithmId => "point-value";

    /// <summary>算法版本:v2 起引入 cityAvgCost/pointScale/零点倍率,并修正倍率分母(旧版多乘 RW 与系数)。</summary>
    public string AlgorithmVersion => "2.0";

    public FeeEstimate Estimate(FeeEstimateRequest request, FeeEstimateContext context)
    {
        var pack = context.Pack;
        var p = pack.Parsed;
        var entry = context.Entry;

        if (entry.Status != "published" || entry.Rw is null)
            throw new FeeWeightsException($"DRG \"{entry.DrgCode}\" 未公布权重,不参与费用估算");

        decimal rw = entry.Rw.Value;
        decimal cityAvgCost = p.CityAvgCost!.Value;
        int scale = p.PointScale;

        // 病组次均费用:优先取权重表实测值,缺失则由 RW × 全市次均推导
        decimal drgAvgCost = entry.AvgCost ?? rw * cityAvgCost;

        // 医院等级系数
        decimal factor = pack.ResolveLevelFactor(entry.DrgCode, request.HospitalLevel);

        // 点值(按人员类型;单值口径可缺省类型)
        decimal pointValue = ResolvePointValue(p, request.PatientType);

        // 标准点值与控费系数:控费系数 = 实际点值 ÷ 标准点值,体现医保基金动态调整
        decimal stdPointValue = cityAvgCost / scale;
        decimal controlFactor = stdPointValue > 0 ? pointValue / stdPointValue : 0m;

        // 零点倍率 Z:盈亏分界点(实际费用 = DRG 费用时的倍率)
        decimal zeroRatio = factor * controlFactor;

        // 正常倍率:总点数 = RW × 系数 × 尺度;DRG 费用 = 总点数 × 点值 = 病组次均 × Z
        decimal basePoint = rw * scale;
        decimal normalPoint = rw * factor * scale;
        decimal standardFee = normalPoint * pointValue;

        var details = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cityAvgCost"] = cityAvgCost,
            ["drgAvgCost"] = drgAvgCost,
            ["stdPointValue"] = stdPointValue,
            ["controlFactor"] = controlFactor,
            ["zeroRatio"] = zeroRatio,
            ["basePoint"] = basePoint,
            ["normalFee"] = standardFee,
        };

        var mark = pack.ResolveMark(entry.DrgCode);
        string? rateType = null;
        decimal? ratio = null;
        decimal? totalPoint = null;
        decimal? estimatedFee = null;

        if (request.PatientFee is > 0)
        {
            ratio = request.PatientFee.Value / drgAvgCost;
            var m = p.Multiplier;
            decimal cap = m.HighCapFor(rw);

            // 低倍率临界值:LowerOf(就低)时系数<1 再乘系数
            decimal lowRateFee = m.LowRule == LowRateRule.LowerOf
                ? drgAvgCost * m.LowRate * Math.Min(1m, factor)
                : drgAvgCost * m.LowRate;
            decimal highRateFee = drgAvgCost * cap;

            details["lowRateFee"] = p.Quantize(lowRateFee);
            details["highRateFee"] = p.Quantize(highRateFee);
            details["raw.lowRateFee"] = lowRateFee;      // 全精度原值,供对拍/审计
            details["raw.highRateFee"] = highRateFee;
            details["highCap"] = cap;

            if (ratio.Value < m.LowRate)
            {
                rateType = "低倍率";
                (totalPoint, estimatedFee) = SettleLow(m, request.PatientFee.Value, rw, factor, scale,
                                                      cityAvgCost, pointValue, controlFactor);
            }
            else if (ratio.Value > cap)
            {
                rateType = "高倍率";
                (totalPoint, estimatedFee) = SettleHigh(m, request.PatientFee.Value, rw, factor, scale,
                                                        drgAvgCost, cityAvgCost, pointValue, controlFactor,
                                                        cap, ratio.Value, normalPoint, standardFee);
            }
            else
            {
                rateType = "正常倍率";
                totalPoint = normalPoint;
                estimatedFee = standardFee;
            }

            details["gapByZeroRatio"] = drgAvgCost * (zeroRatio - ratio.Value); // 达州口径盈亏式(全精度)
        }
        else
        {
            // 无实际费用:仅给正常倍率标准支付,不做倍率判定
            totalPoint = normalPoint;
            estimatedFee = standardFee;
        }

        // 输出边界统一量化到「分」:内部全程保留全精度(controlFactor 等除法除不尽),
        // 只在返回给调用方时收敛一次,保证 Σ(逐项舍入) = 舍入(合计),可与医保结算对拍。
        // 全精度原值留在 Details 的 raw.* 键,便于审计与差异定位。
        details["raw.normalFee"] = standardFee;
        details["raw.estimatedFee"] = estimatedFee;

        var standardFeeOut = p.Quantize(standardFee);
        var estimatedFeeOut = estimatedFee is null ? (decimal?)null : p.Quantize(estimatedFee.Value);
        var feeGapOut = estimatedFeeOut is null || request.PatientFee is null
            ? (decimal?)null
            : p.Quantize(estimatedFeeOut.Value - request.PatientFee.Value);

        details["normalFee"] = standardFeeOut;   // 对外一律量化;全精度值见 raw.normalFee

        return new FeeEstimate(
            entry.DrgCode, entry.DrgName, mark, rw, context.WeightSource,
            factor, pointValue, ratio, rateType, totalPoint, standardFeeOut, estimatedFeeOut, feeGapOut,
            AlgorithmId, AlgorithmVersion, details);
    }

    /// <summary>
    /// 低倍率结算。
    /// PointsNoFactor(周口/达州):总点数 = 实际费用 ÷ 全市次均 × 尺度,DRG 费用 = 实际费用 × 控费系数(不含等级系数)。
    /// ByActual(盘锦/武汉):按实支付。
    /// </summary>
    private static (decimal Point, decimal Fee) SettleLow(
        MultiplierPolicy m, decimal actualFee, decimal rw, decimal factor, int scale,
        decimal cityAvgCost, decimal pointValue, decimal controlFactor)
    {
        if (m.LowSettle == LowSettleMode.ByActual)
            return (actualFee / cityAvgCost * scale, actualFee);

        decimal point = cityAvgCost > 0 ? actualFee / cityAvgCost * scale : 0m;
        return (point, actualFee * controlFactor);
    }

    /// <summary>
    /// 高倍率结算。
    /// LinearAdd(达州/枣庄):总点数 = 正常总点数 + (倍率 − K) × 基准点数(追加部分不含系数);
    ///   DRG 费用 = 病组次均 × Z + (实际费用 − K × 病组次均) × 控费系数。
    /// Deduct(周口历史):总点数 = (实际费用 ÷ 全市次均 − (K−1) × RW × 系数) × 尺度。
    /// Cap(盘锦):封顶为高倍率临界值对应支付。
    /// </summary>
    private static (decimal Point, decimal Fee) SettleHigh(
        MultiplierPolicy m, decimal actualFee, decimal rw, decimal factor, int scale,
        decimal drgAvgCost, decimal cityAvgCost, decimal pointValue, decimal controlFactor,
        decimal cap, decimal ratio, decimal normalPoint, decimal standardFee)
    {
        switch (m.HighSettle)
        {
            case HighSettleMode.LinearAdd:
                decimal addPoint = (ratio - cap) * rw * scale;
                decimal total = normalPoint + addPoint;
                return (total, drgAvgCost * (factor * controlFactor) + (actualFee - cap * drgAvgCost) * controlFactor);

            case HighSettleMode.Cap:
                decimal capFee = drgAvgCost * cap * (factor * controlFactor);
                return (capFee / pointValue, capFee);

            default: // Deduct
                decimal deductPoint = (actualFee / cityAvgCost - (cap - 1m) * rw * factor) * scale;
                return (deductPoint, deductPoint * pointValue);
        }
    }

    private static decimal ResolvePointValue(FeeParameters p, string? patientType)
    {
        if (p.PointValues.Count == 0)
            throw new FeeWeightsException("parameters.json: 缺少 pointValues(点值)配置");

        if (patientType is { Length: > 0 })
        {
            if (p.PointValues.TryGetValue(patientType.Trim(), out var v)) return v;
            throw new FeeWeightsException(
                $"未知人员类型 \"{patientType}\",本地区可选: {string.Join("/", p.PointValues.Keys)}");
        }
        // 未指定类型:单值口径直接取;多值口径拒绝(避免静默取错险种)
        if (p.PointValues.Count == 1) return p.PointValues.Values.First();
        throw new FeeWeightsException(
            $"本地区点值按人员类型区分({string.Join("/", p.PointValues.Keys)}),请指定 type 参数");
    }
}
