using System.Reflection;
using System.Runtime.Loader;

namespace DRGX.Host.PluginHosting;

/// <summary>
/// 插件加载上下文:契约程序集一律交回默认上下文,只有插件私有依赖才在本上下文解析。
///
/// 这是整个插件体系能成立的前提。若放任插件自带一份 DRGX.His.dll,那么插件里的
/// IHisConnector 与宿主里的 IHisConnector 会是**两个不同类型**(同名不同 Assembly 实例),
/// 插件登记进去的能力宿主永远解析不出来 —— 且失败方式是"查不到该键",极难排查。
/// 因此本类对共享程序集名返回 null,由 AssemblyLoadContext 回退到默认上下文。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// 必须由默认上下文提供的程序集(契约层 + 能力契约 + 共享工具)。
    /// 与各插件 csproj 上的 ProjectReference ExcludeAssets="runtime" 是一对双保险:
    /// 那边保证不复制,这边保证即便被复制也不加载。
    /// </summary>
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "DRGX.Abstractions",
        "DRGX.Text",
        "DRGX.Engine",
        "DRGX.Fee",
        "DRGX.His",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base($"Plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}")
        => _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && SharedAssemblies.Contains(name))
            return null; // 交回默认上下文:保证类型同一性

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null
            ? null // 框架程序集等同样交回默认上下文
            : LoadFromAssemblyPath(path); // 插件私有依赖:从插件目录加载
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
