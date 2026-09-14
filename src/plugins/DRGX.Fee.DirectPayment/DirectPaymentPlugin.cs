using DRGX.Abstractions;

namespace DRGX.Fee.DirectPayment;

/// <summary>直付型费用算法插件入口。</summary>
public sealed class DirectPaymentPlugin : IPlugin
{
    public PluginInfo Info { get; } = new(
        "DRGX.Fee.DirectPayment",
        "直付型费用算法(点数法/总额预付)",
        "1.0.0",
        "按病组直接给定支付标准(等级×险种二维表),不做点值换算；缺失支付标准时回退参考次均费用。");

    public void Register(IPluginRegistrar registrar)
    {
        var algorithm = new DirectPaymentAlgorithm();
        // key = manifest.algorithm,地区包按此选择实现
        registrar.Register<IFeeAlgorithm>(algorithm.AlgorithmId, algorithm);
    }
}
