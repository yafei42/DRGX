using System.Reflection;
using System.Runtime.Loader;
using DRGX.Abstractions;

namespace DRGX.Host.PluginHosting;

/// <summary>插件加载结果:登记表 + 成功清单 + 失败清单(失败不阻断,只记录)。</summary>
internal sealed record PluginLoadResult(
    PluginRegistrar Registrar,
    IReadOnlyList<PluginInfo> Loaded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PluginLoadContext> Contexts);

/// <summary>
/// 插件目录扫描与装载。
///
/// 约定:&lt;插件根&gt;/&lt;插件目录&gt;/&lt;同名&gt;.dll 为入口程序集(缺省时取目录内唯一带 .deps.json 的 dll)。
/// 插件根源自 --plugins-root;不给则由 Program.cs 按 plugins → artifacts/plugins 顺序探测,
/// 以覆盖"发布包把 plugins 放在 exe 同级"与"开发态插件工程输出到 artifacts/plugins"两种形态。
/// 仓库里另有 src/plugins(插件**源码**)与 PluginHosting(**加载器**)两个同族目录,
/// 所以探测时按存在性筛选,且 launcher 一律传绝对路径,避免候选根按 CWD 命中源码目录
/// ——那种情况下症状是"插件加载成功 0 个"且毫无报错。
/// 每个插件独立一个 <see cref="PluginLoadContext"/>,互不干扰;契约程序集由默认上下文提供。
///
/// 失败隔离:单个插件加载/实例化/登记任一环节抛异常,只丢弃该插件并记入 Errors,
/// 宿主照常启动 —— 一个坏插件不能让整个分组服务起不来。
/// </summary>
internal static class PluginLoader
{
    public static PluginLoadResult Load(string root)
    {
        var registrar = new PluginRegistrar();
        var loaded = new List<PluginInfo>();
        var errors = new List<string>();
        var contexts = new List<PluginLoadContext>();

        if (!Directory.Exists(root))
            return new PluginLoadResult(registrar, loaded, errors, contexts);

        // 目录名排序:保证加载顺序确定,键冲突时报错可复现
        var pluginDirs = Directory.GetDirectories(root)
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var dir in pluginDirs)
        {
            var name = Path.GetFileName(dir);
            var entry = ResolveEntryAssembly(dir);
            if (entry is null)
            {
                errors.Add($"{name}: 未找到插件入口程序集(期望 {name}.dll 或目录内带 .deps.json 的 dll)");
                continue;
            }

            // 这个插件登记的能力都归属它自己的目录 —— 解析时由登记表填进
            // PluginContext.PluginDirectory,插件因此总能读到同目录的附属资源。
            registrar.CurrentPluginDirectory = dir;

            PluginLoadContext context;
            Assembly assembly;
            try
            {
                context = new PluginLoadContext(entry);
                assembly = context.LoadFromAssemblyPath(entry);
            }
            catch (Exception ex)
            {
                // 常见于:插件针对不兼容的目标框架、依赖缺失、原生库架构不符
                errors.Add($"{name}: 程序集加载失败 - {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            contexts.Add(context);

            foreach (var type in SafeGetLoadableTypes(assembly, name, errors))
            {
                if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    errors.Add($"{name}: {type.FullName} 实现 IPlugin 但缺少公共无参构造函数");
                    continue;
                }

                try
                {
                    var plugin = (IPlugin)Activator.CreateInstance(type)!;
                    plugin.Register(registrar);
                    loaded.Add(plugin.Info);
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}: {type.FullName} 登记失败 - {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return new PluginLoadResult(registrar, loaded, errors, contexts);
    }

    /// <summary>取插件入口程序集:优先与目录同名的 dll,否则取目录内唯一带 .deps.json 的 dll。</summary>
    private static string? ResolveEntryAssembly(string dir)
    {
        var name = Path.GetFileName(dir);
        var same = Path.Combine(dir, name + ".dll");
        if (File.Exists(same)) return Path.GetFullPath(same);

        var candidates = Directory.GetFiles(dir, "*.dll")
            .Where(d => File.Exists(Path.ChangeExtension(d, ".deps.json")))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.Length > 0 ? Path.GetFullPath(candidates[0]) : null;
    }

    /// <summary>GetTypes 在依赖不全时抛 ReflectionTypeLoadException;这里只取能加载的类型,并把加载器异常记进 errors。</summary>
    private static IEnumerable<Type> SafeGetLoadableTypes(Assembly assembly, string pluginName, List<string> errors)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var le in ex.LoaderExceptions.Where(e => e is not null).Take(3))
                errors.Add($"{pluginName}: 部分类型无法加载 - {le!.Message}");
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
        catch (Exception ex)
        {
            errors.Add($"{pluginName}: 类型枚举失败 - {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }
}
