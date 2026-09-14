namespace DRGX.Fee;

/// <summary>权重条目:DRG 码 + 名称 + 相对权重 + 参考费用 + 状态。</summary>
public sealed record FeeWeightEntry(
    string DrgCode,
    string DrgName,
    /// <summary>相对权重(RW);unpublished 为 null。</summary>
    decimal? Rw,
    /// <summary>参考费用(元);缺失为 null。</summary>
    decimal? AvgCost,
    /// <summary>published / unpublished。</summary>
    string Status);

/// <summary>费用数据契约错误(fail-fast)。</summary>
public sealed class FeeWeightsException : Exception
{
    public FeeWeightsException(string message) : base(message) { }
}
