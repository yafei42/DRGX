namespace DRGX.Fee;

/// <summary>费用估算请求(算法无关的通用入参;具体字段如何消费由各算法定义)。</summary>
public sealed record FeeEstimateRequest(
    string DrgCode,
    /// <summary>病例实际费用(元);缺省则只返回标准(正常倍率)支付,不做倍率判定。</summary>
    decimal? PatientFee,
    /// <summary>医院等级(系数键,如 三甲/三级/市二级/县二级/一级/乡级,键集由地区包 levels 定义)。</summary>
    string? HospitalLevel,
    /// <summary>人员类型(点值键,如 居民/职工,键集由地区包 pointValues 定义)。</summary>
    string? PatientType);

/// <summary>算法执行上下文:权重条目(已合并地区稀疏覆盖)+ 地区费用包。</summary>
public sealed record FeeEstimateContext(
    FeeWeightEntry Entry,
    /// <summary>权重来源:base(全国基准) / override(地区覆盖)。</summary>
    string WeightSource,
    RegionPack Pack);

/// <summary>
/// 费用估算结果(算法无关的通用出参;算法私有明细放 Details,如各类参考支付额)。
/// 溯源四元组由宿主组装:packRevision + weightsDataVersion + regionDataVersion + algorithmVersion。
///
/// <para><b>金额精度契约:</b> <see cref="StandardFee"/> / <see cref="EstimatedFee"/> /
/// <see cref="FeeGap"/>,以及 <see cref="Details"/> 中所有以「元」为单位的条目,
/// 都已按地区包 <c>parameters.json → rounding</c> 的口径量化(默认 2 位、四舍五入)。
/// 算法内部保留全精度,只在输出边界收敛一次,以保证 Σ(逐项舍入) = 舍入(合计)。
/// 需要全精度中间值的对拍场景请读 <see cref="Details"/> 里 <c>raw.*</c> 键。</para>
/// </summary>
public sealed record FeeEstimate(
    string DrgCode,
    string DrgName,
    /// <summary>病组标记(基础病组/非稳定病组/基础病组 非稳定病种;无标记为空串)。</summary>
    string? Mark,
    /// <summary>权重(RW)。</summary>
    decimal Weight,
    string WeightSource,
    /// <summary>医院系数(按等级取,缺省走 defaultFactor)。</summary>
    decimal Factor,
    /// <summary>点值(按人员类型取)。</summary>
    decimal? PointValue,
    /// <summary>倍率 = 实际费用 / 标准费用;未提供实际费用时为 null。</summary>
    decimal? Ratio,
    /// <summary>倍率类型:低倍率/正常倍率/高倍率;无法判定时为 null。</summary>
    string? RateType,
    /// <summary>总点数;未提供实际费用时为 null。</summary>
    decimal? TotalPoint,
    /// <summary>正常倍率标准支付(元,已量化)。</summary>
    decimal StandardFee,
    /// <summary>按实际费用估算的支付(元,已量化);未提供实际费用时为 null。</summary>
    decimal? EstimatedFee,
    /// <summary>估算支付 − 实际费用(元,已量化;正为盈,负为亏)。</summary>
    decimal? FeeGap,
    string Algorithm,
    string AlgorithmVersion,
    IReadOnlyDictionary<string, decimal?>? Details);

/// <summary>费用算法接缝:新增地区算法 = 新增一个实现类并注册,宿主与数据格式均不动。</summary>
public interface IFeeAlgorithm
{
    /// <summary>算法标识(manifest.algorithm 按此选择实现,大小写不敏感)。</summary>
    string AlgorithmId { get; }

    /// <summary>算法版本(进入溯源四元组)。</summary>
    string AlgorithmVersion { get; }

    FeeEstimate Estimate(FeeEstimateRequest request, FeeEstimateContext context);
}

/// <summary>
/// 算法注册表:按 manifest.algorithm 解析实现;fail-fast,未知算法拒绝。
///
/// 算法实现不在此程序集内 —— 它们由 src/plugins/ 下的插件通过 IPluginRegistrar 登记,
/// 宿主启动时用登记结果填充本表。因此本程序集只承载契约与模型,不引用任何具体算法,
/// 也就不会把某种地区口径固化进核心。
/// </summary>
public sealed class FeeAlgorithmRegistry
{
    private readonly Dictionary<string, IFeeAlgorithm> _algorithms = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IFeeAlgorithm algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm.AlgorithmId))
            throw new FeeWeightsException("算法 AlgorithmId 不能为空");
        if (!_algorithms.TryAdd(algorithm.AlgorithmId, algorithm))
            throw new FeeWeightsException($"算法 \"{algorithm.AlgorithmId}\" 重复注册");
    }

    /// <summary>已注册的算法标识(排序,用于启动日志与错误提示)。</summary>
    public IReadOnlyList<string> Ids =>
        _algorithms.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

    public IFeeAlgorithm Resolve(string algorithmId) =>
        _algorithms.TryGetValue((algorithmId ?? "").Trim(), out var a)
            ? a
            : throw new FeeWeightsException(
                _algorithms.Count == 0
                    ? $"未知费用算法 \"{algorithmId}\": 当前没有任何费用算法插件被加载,费用估算不可用。" +
                      "请确认插件目录下存在 DRGX.Fee.* 插件(发布包为 exe 同级的 plugins/,开发态为 " +
                      "artifacts/plugins/,或用 --plugins-root 指定),以及是否被 --no-plugins 关闭。"
                    : $"未知费用算法 \"{algorithmId}\",已注册: {string.Join("/", _algorithms.Keys)}");
}
