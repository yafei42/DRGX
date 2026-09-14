using DRGX.Abstractions;

namespace DRGX.Host.PluginHosting;

/// <summary>
/// 能力登记表:插件在 Register 期登记「契约类型 + key → 工厂」,宿主在请求/启动期按同样二元组解析。
///
/// 键统一大小写不敏感 —— 配置里写 <c>Point-Value</c> 与 <c>point-value</c> 必须等价,
/// 否则用户只能靠猜大小写,而失败表现是"未知算法"。
/// </summary>
internal sealed class PluginRegistrar : IPluginRegistrar
{
    private readonly Dictionary<string, Func<PluginContext, object>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析时不需要任何配置的能力(如费用算法)用这个上下文。</summary>
    public static readonly PluginContext NoContext = new(PluginDirectory: "", ConfigPath: null, Config: null);

    public void Register<TService>(string key, Func<PluginContext, TService> factory) where TService : class
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException($"{typeof(TService).Name} 的登记键不能为空", nameof(key));
        ArgumentNullException.ThrowIfNull(factory);

        if (!_factories.TryAdd(Compose(typeof(TService), key), ctx => factory(ctx)))
            throw new InvalidOperationException(
                $"契约 {typeof(TService).Name} 的键 \"{key}\" 已被登记(插件之间键冲突)");
    }

    public void Register<TService>(string key, TService instance) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        Register<TService>(key, _ => instance);
    }

    /// <summary>按契约与键解析;未登记返回 false(调用方决定是降级还是报错)。</summary>
    public bool TryResolve<TService>(string? key, PluginContext context, out TService? service) where TService : class
    {
        service = null;
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!_factories.TryGetValue(Compose(typeof(TService), key), out var factory)) return false;
        service = (TService)factory(context);
        return true;
    }

    /// <summary>某契约下已登记的键(排序,用于错误提示与启动日志)。</summary>
    public IReadOnlyList<string> KeysOf<TService>() where TService : class
    {
        var prefix = typeof(TService).FullName + "\u0001";
        return _factories.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..])
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>「契约全名 + \u0001 + 键」复合键:一个字典搞定多契约,且复用同一套大小写规则。</summary>
    private static string Compose(Type contract, string key) =>
        contract.FullName + "\u0001" + key.Trim();
}
