using DRGX.Abstractions;

namespace DRGX.Fee.PointValue;

/// <summary>点值法费用算法插件入口。</summary>
public sealed class PointValuePlugin : IPlugin
{
    public PluginInfo Info { get; } = new(
        "DRGX.Fee.PointValue",
        "点值法费用算法",
        "1.0.0",
        "标准支付 = 权重 × 等级系数 × 点值 ÷ pointScale；倍率 = 实际费用 ÷ 病组次均，按地区分段策略判定高低倍率。");

    public void Register(IPluginRegistrar registrar)
    {
        var algorithm = new PointValueAlgorithm();
        // key = manifest.algorithm,地区包按此选择实现
        registrar.Register<IFeeAlgorithm>(algorithm.AlgorithmId, algorithm);
    }
}
