using System.Globalization;
using System.Text.Json;

namespace DRGX.Fee;

/// <summary>低倍率临界值取法:Flat=病组次均×低倍线;LowerOf=就低(系数&lt;1 时再乘系数)。</summary>
public enum LowRateRule { Flat, LowerOf }

/// <summary>低倍率结算:ByActual=按实支付;PointsNoFactor=实际费用×控费系数。</summary>
public enum LowSettleMode { ByActual, PointsNoFactor }

/// <summary>高倍率结算:Deduct=折减式;LinearAdd=线性累加(正常总点数+追加点数);Cap=封顶。</summary>
public enum HighSettleMode { Deduct, LinearAdd, Cap }

/// <summary>高倍率分段:按 RW 升序取首个 rw&lt;UpTo 的段;末段 UpTo 为 null(兜底)。</summary>
public sealed record HighRateSegment(decimal? UpTo, decimal Rate);

/// <summary>
/// 倍率判定与结算策略(parameters.json → multiplier 节,数据驱动)。
/// 只含基本测算:低/高倍率临界与结算模式。地区特殊结算分支(非稳定折减/未入组/超长住院)
/// 依赖各地细则,DRG 3.0 后尚未发布,暂不建模。
/// </summary>
public sealed record MultiplierPolicy(
    decimal LowRate,
    LowRateRule LowRule,
    IReadOnlyList<HighRateSegment> HighSegments,
    HighSettleMode HighSettle,
    LowSettleMode LowSettle)
{
    /// <summary>按 RW 取高倍率上限(K 倍率)。</summary>
    public decimal HighCapFor(decimal rw)
    {
        foreach (var s in HighSegments)
            if (s.UpTo is null || rw < s.UpTo.Value) return s.Rate;
        return HighSegments.Count > 0 ? HighSegments[^1].Rate : 3m;
    }

    public static MultiplierPolicy Default => new(
        0.35m, LowRateRule.Flat,
        new[] { new HighRateSegment(1m, 3m), new HighRateSegment(2m, 2.5m), new HighRateSegment(3m, 2m),
                new HighRateSegment(5m, 1.5m), new HighRateSegment(null, 1.3m) },
        HighSettleMode.Deduct, LowSettleMode.PointsNoFactor);
}

/// <summary>
/// 地区费用参数(parameters.json 的强类型视图)。
/// cityAvgCost 为计算基准:病组次均 = RW × cityAvgCost,标准点值 = cityAvgCost ÷ pointScale。
/// </summary>
public sealed record FeeParameters(
    /// <summary>全市次均费用(元/RW,即基准费率)。显式给出时参与强校验;缺失由权重表推断。</summary>
    decimal? CityAvgCost,
    /// <summary>点数尺度(基准点数 = RW × pointScale)。实测 1 / 100 / 1000 三档。</summary>
    int PointScale,
    /// <summary>cityAvgCost 是否由权重表推断(推断值时不做强校验,仅告警)。</summary>
    bool CityAvgCostInferred,
    /// <summary>pointScale 是否在 parameters.json 显式声明(实测 1/100/1000 三档,缺省 100 仅作兼容)。</summary>
    bool PointScaleExplicit,
    IReadOnlyDictionary<string, decimal> PointValues,
    IReadOnlyDictionary<string, string> Levels,
    /// <summary>全局等级系数(等级→系数,整地区统一,如佛山 0.82~1.07)。逐 DRG 列缺失时的回退。</summary>
    IReadOnlyDictionary<string, decimal> GlobalLevelFactors,
    decimal DefaultFactor,
    MultiplierPolicy Multiplier,
    /// <summary>金额分位量化的小数位。默认 2(元→分)。</summary>
    int RoundingScale = 2,
    /// <summary>金额舍入方式。默认 AwayFromZero(四舍五入,不用银行家舍入)。</summary>
    MidpointRounding RoundingMode = MidpointRounding.AwayFromZero)
{
    /// <summary>
    /// 金额分位量化。所有以「元」为单位的对外输出都必须经此收敛一次。
    ///
    /// <para>为什么必须量化:算法内部是连续的 <c>decimal</c> 除法(如 controlFactor = 点值 ÷ 标准点值),
    /// 除不尽时会带 28 位有效数字。若直接把这种中间值当金额返回,API 契约就没有定义单位精度;
    /// 更要命的是 Σ(逐项舍入) ≠ 舍入(合计),与医保结算对拍时永远对不上。
    /// 做法是「内部保持全精度,只在输出边界量化一次」。</para>
    /// </summary>
    public decimal Quantize(decimal amount) => decimal.Round(amount, RoundingScale, RoundingMode);
}

/// <summary>parameters.json 解析器:fail-fast,结构非法即抛 FeeWeightsException。</summary>
public static class FeeParametersReader
{
    public static FeeParameters Read(JsonElement root, decimal? inferredCityAvgCost)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new FeeWeightsException("parameters.json: 根节点必须是对象");

        var cityAvgCost = ReadDecimal(root, "cityAvgCost");
        bool inferred = cityAvgCost is null;
        if (inferred)
            cityAvgCost = inferredCityAvgCost;
        if (cityAvgCost is <= 0)
            throw new FeeWeightsException("parameters.json: cityAvgCost 缺失且无法从权重表推断(需为正数)");

        bool pointScaleExplicit = root.TryGetProperty("pointScale", out _);
        var pointScale = ReadIntStrict(root, "pointScale", "parameters.json") ?? DefaultPointScale;
        if (pointScale <= 0)
            throw new FeeWeightsException($"parameters.json: pointScale={pointScale} 非法,必须为正整数");

        var pointValues = ReadNumberMap(root, "pointValues");
        var levels = ReadStringMap(root, "levels");
        var globalLevelFactors = ReadNumberMap(root, "levelFactors");
        var defaultFactor = ReadDecimal(root, "defaultFactor") ?? 1m;
        var multiplier = ReadMultiplier(root);
        var (roundingScale, roundingMode) = ReadRounding(root);

        return new FeeParameters(cityAvgCost, pointScale, inferred, pointScaleExplicit,
                                 pointValues, levels, globalLevelFactors, defaultFactor, multiplier,
                                 roundingScale, roundingMode);
    }

    /// <summary>
    /// 读金额量化口径:<c>"rounding": { "scale": 2, "mode": "awayFromZero" }</c>,整节可缺省(默认 2 位、四舍五入)。
    /// 未知 mode 报错而不是回退 —— 舍入方式写错会让每一笔金额都差一分,且看起来完全正常。
    /// </summary>
    private static (int Scale, MidpointRounding Mode) ReadRounding(JsonElement root)
    {
        if (!root.TryGetProperty("rounding", out var r) || r.ValueKind != JsonValueKind.Object)
            return (2, MidpointRounding.AwayFromZero);

        var scale = ReadIntStrict(r, "scale", "parameters.json rounding") ?? 2;
        if (scale is < 0 or > 6)
            throw new FeeWeightsException($"parameters.json: rounding.scale={scale} 非法,应在 0~6 之间");

        var modeText = ReadString(r, "mode");
        if (modeText is null) return (scale, MidpointRounding.AwayFromZero);

        return modeText.Trim().ToLowerInvariant() switch
        {
            "awayfromzero" => (scale, MidpointRounding.AwayFromZero),
            "toeven" => (scale, MidpointRounding.ToEven),
            "towardzero" => (scale, MidpointRounding.ToZero),
            "tozero" => (scale, MidpointRounding.ToZero),
            "tonegativeinfinity" => (scale, MidpointRounding.ToNegativeInfinity),
            "topositiveinfinity" => (scale, MidpointRounding.ToPositiveInfinity),
            _ => throw new FeeWeightsException(
                $"parameters.json: rounding.mode=\"{modeText}\" 未知,可选 awayFromZero(默认)/toEven/towardZero/" +
                "toNegativeInfinity/toPositiveInfinity"),
        };
    }

    private static MultiplierPolicy ReadMultiplier(JsonElement root)
    {
        if (!root.TryGetProperty("multiplier", out var m) || m.ValueKind != JsonValueKind.Object)
        {
            // 旧格式:lowRate / highRateSegments 平铺在根节点
            return MultiplierPolicy.Default with
            {
                LowRate = ReadDecimal(root, "lowRate") ?? 0.35m,
                HighSegments = ReadLegacySegments(root) ?? MultiplierPolicy.Default.HighSegments,
            };
        }

        var lowRate = ReadDecimal(m, "lowRate") ?? 0.35m;
        var lowRule = ReadString(m, "lowRateRule")?.Equals("lowerOf", StringComparison.OrdinalIgnoreCase) == true
            ? LowRateRule.LowerOf : LowRateRule.Flat;
        var highSegments = ReadSegments(m) ?? ReadLegacySegments(root) ?? MultiplierPolicy.Default.HighSegments;
        var highSettle = ReadString(m, "highSettle") switch
        {
            "linearAdd" => HighSettleMode.LinearAdd,
            "cap" => HighSettleMode.Cap,
            _ => HighSettleMode.Deduct,
        };
        var lowSettle = ReadString(m, "lowSettle")?.Equals("byActual", StringComparison.OrdinalIgnoreCase) == true
            ? LowSettleMode.ByActual : LowSettleMode.PointsNoFactor;

        return new MultiplierPolicy(lowRate, lowRule, highSegments, highSettle, lowSettle);
    }

    private static IReadOnlyList<HighRateSegment>? ReadSegments(JsonElement m)
    {
        if (!m.TryGetProperty("highRate", out var h)) return null;
        if (h.ValueKind == JsonValueKind.Array) return ParseSegments(h);
        if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("segments", out var s))
            return ParseSegments(s);
        return null;
    }

    private static IReadOnlyList<HighRateSegment>? ReadLegacySegments(JsonElement root) =>
        root.TryGetProperty("highRateSegments", out var a) && a.ValueKind == JsonValueKind.Array
            ? ParseSegments(a) : null;

    private static IReadOnlyList<HighRateSegment>? ParseSegments(JsonElement arr)
    {
        var list = new List<HighRateSegment>();
        decimal? prev = null;
        int i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            i++;
            var upTo = ReadDecimal(e, "upTo");
            var rate = ReadDecimal(e, "rate")
                ?? throw new FeeWeightsException($"parameters.json: highRate 第{i}段缺少 rate");
            if (rate <= 0)
                throw new FeeWeightsException($"parameters.json: highRate 第{i}段 rate={rate} 非正数");
            if (upTo is not null && prev is not null && upTo.Value <= prev.Value)
                throw new FeeWeightsException($"parameters.json: highRate 的 upTo 必须严格递增(第{i}段)");
            if (upTo is not null) prev = upTo;
            list.Add(new HighRateSegment(upTo, rate));
        }
        if (list.Count == 0) return null;
        if (list[^1].UpTo is not null)
            throw new FeeWeightsException("parameters.json: highRate 末段 upTo 必须为 null(兜底)");
        return list;
    }

    private static decimal? ReadDecimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDecimal(out var d) ? d : null;

    /// <summary>缺省点值量级:1 元 = 100 点。</summary>
    public const int DefaultPointScale = 100;

    /// <summary>
    /// 读整数,<b>存在但形状不对即报错</b>,不回退默认值。
    ///
    /// <para>为什么不复用 <c>TryGetInt32</c> 后 <c>?? 默认</c>:JSON 里 <c>1.0</c> / <c>100.0</c> /
    /// <c>1e2</c> 的 ValueKind 都是 Number 但带小数位或指数,<c>TryGetInt32</c> 一律返回 false;
    /// 字符串 <c>"100"</c> 同理。于是 <c>"pointScale": 1.0</c> 会静默变成 100 —— 支付额差 100 倍,
    /// 而加载期因为"字段已显式声明"不会给出任何告警。这类错误只能靠 fail-fast 挡住。</para>
    /// </summary>
    private static int? ReadIntStrict(JsonElement e, string name, string label)
    {
        if (!e.TryGetProperty(name, out var v)) return null;

        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out var i)) return i;
            if (v.TryGetDecimal(out var d))
            {
                // 整数值写成小数形式(1.0):可救,但必须让作者知道写法不对
                if (d == decimal.Truncate(d))
                    throw new FeeWeightsException(
                        $"{label}: {name} 必须写成整数,当前为小数形式 {d}。请改为 \"{name}\": {(long)d}");
                throw new FeeWeightsException($"{label}: {name}={d} 不是整数");
            }
            throw new FeeWeightsException($"{label}: {name} 超出 32 位整数范围,或不是有效数字");
        }

        throw new FeeWeightsException(
            $"{label}: {name} 类型应为整数,实际为 {v.ValueKind}" +
            (v.ValueKind == JsonValueKind.String ? $"(值 \"{v.GetString()}\";数字不要加引号)" : ""));
    }

    private static string? ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyDictionary<string, decimal> ReadNumberMap(JsonElement e, string name)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDecimal(out var d))
                    throw new FeeWeightsException($"parameters.json: {name}.{p.Name} 不是数值(Invariant 十进制)");
                map[p.Name] = d;
            }
        return map;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement e, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    throw new FeeWeightsException($"parameters.json: {name}.{p.Name} 必须是字符串(系数列名)");
                map[p.Name] = p.Value.GetString() ?? "";
            }
        return map;
    }
}
