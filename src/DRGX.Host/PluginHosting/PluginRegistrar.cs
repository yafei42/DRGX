using System.Text.Json;
using DRGX.Abstractions;

namespace DRGX.Host.PluginHosting;

/// <summary>
/// 宿主给某个能力的**配置文件**(可选):路径 + 已解析的 JSON。
///
/// <para>它的存在是为了让"这个能力由宿主配文件"与"这个能力不需要配文件"变成同一个接口上的两种取值,
/// 而不是两个并列的解析入口。原先费用算法走 <c>NoContext</c>(一个空上下文的哨兵),
/// 读代码时看不出那是**有理由的口径**还是**漏了接线**。</para>
/// </summary>
/// <param name="Path">配置文件路径;没有则为 null。</param>
/// <param name="Json">配置文件的原始 JSON;没有则为 null。插件自行校验自己认识的字段。</param>
internal readonly record struct PluginConfig(string? Path, JsonElement? Json)
{
    /// <summary>
    /// 宿主不为该能力提供配置文件。<b>费用算法即如此</b>:它的全部参数来自地区费用包
    /// (<c>FeeEstimateContext</c> 的 Entry/Source/Pack),再加一份插件级配置只会变成第二处真相。
    /// 哪天真需要(例如某个算法要读自己目录下的点值表),按 <c>configs/&lt;能力&gt;/</c> 约定补上即可 ——
    /// 接口不用改。
    /// </summary>
    public static readonly PluginConfig None = default;
}

/// <summary>
/// 能力登记表:插件在 Register 期登记「契约类型 + key → 工厂」,宿主在请求/启动期按同样二元组解析。
///
/// <para>键统一大小写不敏感 —— 配置里写 <c>Point-Value</c> 与 <c>point-value</c> 必须等价,
/// 否则用户只能靠猜大小写,而失败表现是"未知算法"。</para>
///
/// <para><b>插件目录由登记表负责填,不由调用方传。</b>解析期的 <see cref="PluginContext.PluginDirectory"/>
/// 是"插件所在目录",只有装载器知道;先前由调用方传,于是 HIS 那条路把**配置文件所在目录**传了进去 ——
/// 与契约注释说的"插件所在目录"不是一回事。现在装载器在登记时按插件记下目录,解析时自动填,
/// 调用方只需给出配置(<see cref="PluginConfig"/>)。</para>
/// </summary>
internal sealed class PluginRegistrar : IPluginRegistrar
{
    /// <summary>一条登记:工厂 + 它所属插件的目录。</summary>
    private sealed record Registration(string PluginDirectory, Func<PluginContext, object> Factory);

    private readonly Dictionary<string, Registration> _registrations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 当前正在登记哪个插件 —— 装载器在调用某个插件的 <see cref="IPlugin.Register"/> 之前设置。
    /// 之后的登记项都归属这个目录。单线程启动期使用,故用可变属性而非参数传递。
    /// </summary>
    public string CurrentPluginDirectory { get; set; } = "";

    public void Register<TService>(string key, Func<PluginContext, TService> factory) where TService : class
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException($"{typeof(TService).Name} 的登记键不能为空", nameof(key));
        ArgumentNullException.ThrowIfNull(factory);

        var registration = new Registration(CurrentPluginDirectory, ctx => factory(ctx));
        if (!_registrations.TryAdd(Compose(typeof(TService), key), registration))
            throw new InvalidOperationException(
                $"契约 {typeof(TService).Name} 的键 \"{key}\" 已被登记(插件之间键冲突)");
    }

    public void Register<TService>(string key, TService instance) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        Register<TService>(key, _ => instance);
    }

    /// <summary>
    /// 按契约与键解析;未登记返回 false(调用方决定是降级还是报错)。
    /// </summary>
    /// <param name="config">宿主为该能力准备的配置文件;没有则 <see cref="PluginConfig.None"/>。
    /// 插件目录由登记表按登记项填,调用方不必也不应传。</param>
    public bool TryResolve<TService>(string? key, PluginConfig config, out TService? service) where TService : class
    {
        service = null;
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!_registrations.TryGetValue(Compose(typeof(TService), key), out var registration)) return false;
        service = (TService)registration.Factory(
            new PluginContext(registration.PluginDirectory, config.Path, config.Json));
        return true;
    }

    /// <summary>某契约下已登记的键(排序,用于错误提示与启动日志)。</summary>
    public IReadOnlyList<string> KeysOf<TService>() where TService : class
    {
        var prefix = typeof(TService).FullName + "\u0001";
        return _registrations.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..])
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>「契约全名 + \u0001 + 键」复合键:一个字典搞定多契约,且复用同一套大小写规则。</summary>
    private static string Compose(Type contract, string key) =>
        contract.FullName + "\u0001" + key.Trim();
}
