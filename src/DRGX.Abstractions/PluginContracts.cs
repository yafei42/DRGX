using System.Text.Json;

namespace DRGX.Abstractions;

/// <summary>插件元信息(用于启动日志与 /api/info 的溯源展示)。</summary>
/// <param name="Id">插件唯一标识(与插件目录名一致,如 DRGX.His.SqlServer)。</param>
/// <param name="Name">人读名称。</param>
/// <param name="Version">插件版本。</param>
/// <param name="Description">可选说明。</param>
public sealed record PluginInfo(string Id, string Name, string Version, string? Description = null);

/// <summary>宿主交给插件的上下文。插件在 <see cref="IPlugin.Register"/> 期间拿到它做一次性准备,不应在请求期使用。</summary>
/// <param name="PluginDirectory">插件所在目录(用于读取同目录的附属资源)。</param>
/// <param name="ConfigPath">宿主传入的配置文件路径(可为 null,如费用算法插件不需要)。</param>
/// <param name="Config">配置文件的原始 JSON(可为 null)。插件自行校验自己认识的字段。</param>
public sealed record PluginContext(string PluginDirectory, string? ConfigPath, JsonElement? Config);

/// <summary>
/// 插件入口:一个插件程序集可实现多个 IPlugin。
/// 宿主在插件加载期实例化(要求公共无参构造函数)并调用 <see cref="Register"/>。
/// </summary>
public interface IPlugin
{
    /// <summary>插件元信息。</summary>
    PluginInfo Info { get; }

    /// <summary>登记本插件提供的能力。此方法抛异常时,宿主只丢弃该插件,不影响其他插件与宿主启动。</summary>
    void Register(IPluginRegistrar registrar);
}

/// <summary>
/// 能力登记回调。宿主按「契约类型 + key」解析实现:
/// HIS 用 manifest/config 里的 connector id 作 key,费用算法用 algorithm id 作 key。
/// </summary>
/// <remarks>
/// 用泛型而非强类型方法,是为了让本程序集不必引用各能力契约程序集(避免循环依赖)。
/// 泛型实参由插件在编译期确定,宿主按 <c>typeof(TService)</c> 索引,类型安全由编译期保证。
/// </remarks>
public interface IPluginRegistrar
{
    /// <summary>登记一个需要外部配置才能构造的能力实现(如 HIS 连接器:要连接串与 SQL 模板)。</summary>
    void Register<TService>(string key, Func<PluginContext, TService> factory) where TService : class;

    /// <summary>登记一个无状态/无配置的能力实现(如费用算法)。</summary>
    void Register<TService>(string key, TService instance) where TService : class;
}
