using System.Text.Json;
using DRGX.Abstractions;
using DRGX.Host.PluginHosting;

namespace DRGX.Host;

/// <summary>
/// 启动期装载:把"外部可失败的资源"装进宿主——地区费用包、HIS 连接器、费用算法。
///
/// <para>全部遵循「失败降级不炸启动」:单个资源不可用只打警告,分组功能始终可用
/// (唯一例外是数据包本身,它在 <c>PackRuntime.Load</c> 里 fail-fast)。
/// 这里是宿主里唯一认识"插件登记表"的地方 —— 具体怎么连 HIS、费用怎么算,全在插件里。</para>
/// </summary>
internal static class RuntimeLoader
{
    /// <summary>地区费用包:启动期扫描根目录加载全部生效包;单包失败只降级(该包不可用),分组不受影响。</summary>
    public static FeeRegionSetHolder LoadFeeRegions(string regionsRoot)
    {
        var holder = new FeeRegionSetHolder();
        holder.Swap(DRGX.Fee.RegionPackSet.LoadRoot(regionsRoot, DateOnly.FromDateTime(DateTime.Today)));
        foreach (var w in holder.Current.Warnings)
            Console.Error.WriteLine($"警告: 地区费用包 {w}");
        foreach (var (key, rp) in holder.Current.Packs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"地区费用包已加载: {key} ({rp.Manifest.RegionName}, algorithm={rp.Manifest.Algorithm}, " +
                              $"权重 {rp.WeightPublished}/{rp.WeightRows} published, 权重覆盖 {rp.OverrideCodes.Count} 组" +
                              $"{DemoSuffix(rp)}" +
                              $"{(string.Equals(rp.Manifest.DataLevel, "reference", StringComparison.OrdinalIgnoreCase) ? ", 非本地值" : "")})");

        if (holder.Current.Packs.Count == 0)
        {
            Console.Error.WriteLine("提示: 未加载任何地区费用包,RW/参考费用缺失、费用估算(/api/fee/estimate)不可用;地区包应位于 data/regions/<地区>/<版本>/(或 --regions-root <目录>)。");
        }
        else
        {
            var demoCount = holder.Current.Packs.Values.Count(p => p.Manifest.Demo);
            if (demoCount == holder.Current.Packs.Count)
                Console.Error.WriteLine(
                    $"提示[!]: 已加载的 {demoCount} 个地区费用包全部标记为「演示数据」(manifest.json 的 demo:true), " +
                    "费用测算结果不得用于真实结算、审核或对账。");
            else if (demoCount > 0)
                Console.Error.WriteLine($"提示[!]: {demoCount}/{holder.Current.Packs.Count} 个地区费用包标记为「演示数据」, 这些包的测算结果不得用于真实结算。");
        }
        return holder;
    }

    /// <summary>启动日志里的演示数据标记(非演示包返回空串,不污染日志)。</summary>
    private static string DemoSuffix(DRGX.Fee.RegionPack rp) => rp.Manifest.Demo ? ", 演示数据" : "";

    /// <summary>
    /// HIS 病案提取连接器(可选):由插件提供。
    /// 宿主只读配置里的 connector 字段作为键,具体怎么连、SQL 模板长什么样,全在插件里,
    /// 宿主不认识 SqlClient,也不认识HealthOne —— 换 HIS 只换插件。
    /// 加载失败只降级(HIS 端点 503),分组与费用功能不受影响。
    /// </summary>
    public static DRGX.His.IHisConnector? LoadHisConnector(string? hisArg, string packPath, PluginRegistrar plugins)
    {
        // 这一行以前是静默 return(唯一一条不吭声的降级路径):插件数、地区包、费用算法
        // 都会在启动日志里报一声,唯独"HIS 没配"什么都不说,于是双击 exe 的用户只看到
        // 界面上 503,启动窗口里没有任何线索。
        if (hisArg is null)
        {
            Console.WriteLine("HIS 连接器: 未启用 —— 未指定 --his,configs\\his 下也没有 *.local.json;"
                              + " /api/his/* 返回 503,分组与费用功能不受影响。");
            return null;
        }
        try
        {
            var path = WebPaths.Resolve(hisArg, packPath, isFile: true);
            // JSON 必须按 UTF-8 字节解析(JsonDocument 不接受带编码回退的文本),故不走 FileText
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            var connectorKey = root.TryGetProperty("connector", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(connectorKey))
            {
                Console.Error.WriteLine($"警告: HIS 配置缺少 connector 字段({path}),无法选择插件。");
                return null;
            }

            // RootElement.Clone():doc 出 using 后仍要持有这份 JSON 交给插件。
            // 插件目录由登记表按插件填(不在这里传),这里只给配置。
            var ctx = new PluginConfig(path, root.Clone());
            if (!plugins.TryResolve<DRGX.His.IHisConnector>(connectorKey, ctx, out var connector) || connector is null)
            {
                var available = plugins.KeysOf<DRGX.His.IHisConnector>();
                Console.Error.WriteLine($"警告: 没有插件提供 HIS 连接器 \"{connectorKey}\"。");
                Console.Error.WriteLine(available.Count == 0
                    ? "  当前没有任何 HIS 插件被加载(检查 --plugins-root 指向的插件目录,或是否用了 --no-plugins)。"
                    : $"  已加载的 HIS 连接器: {string.Join("/", available)}");
                return null;
            }

            Console.WriteLine($"HIS 连接器已加载: {connector.ConnectorId} (插件 {connectorKey})");
            return connector;
        }
        catch (Exception ex)
        {
            // 带上异常类型:有一类失败(实测于裁剪版)ex.GetType().Name 是 TypeLoadException
            // 而 ex.Message 是**空串**。只打 Message 会得到「加载失败(): 」这种无法排障的输出。
            Console.Error.WriteLine($"警告: HIS 连接器加载失败({hisArg}): {ex.GetType().Name}: {ex.Message}");
            // 消息为空时把完整异常给出来 —— 此时类型名之外再无任何线索,
            // 不打印等于让这条警告不可用。正常异常的 Message 非空,不会走到这里。
            if (string.IsNullOrWhiteSpace(ex.Message))
                Console.Error.WriteLine(ex.ToString());
            Console.Error.WriteLine("  分组功能不受影响,但 HIS 提取接口(/api/his/*)将不可用。");
            return null;
        }
    }

    /// <summary>费用算法:全部来自插件登记表。没有任何算法插件时不报错,只提示 —— 分组仍可用。
    ///
    /// <para>与 HIS 连接器走**同一条**解析路径,唯一差别是配置:<see cref="PluginConfig.None"/> ——
    /// 费用算法的参数全部来自地区费用包,没有插件级配置文件。这个差别是一条口径,不是漏接线。</para>
    /// </summary>
    public static DRGX.Fee.FeeAlgorithmRegistry LoadFeeAlgorithms(PluginRegistrar plugins)
    {
        var registry = new DRGX.Fee.FeeAlgorithmRegistry();
        foreach (var id in plugins.KeysOf<DRGX.Fee.IFeeAlgorithm>())
        {
            try
            {
                if (plugins.TryResolve<DRGX.Fee.IFeeAlgorithm>(id, PluginConfig.None, out var algo) && algo is not null)
                    registry.Register(algo);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"警告: 费用算法 \"{id}\" 实例化失败: {ex.Message}");
            }
        }

        Console.WriteLine(registry.Ids.Count == 0
            ? "费用算法: 无(未加载任何费用算法插件,费用估算不可用)"
            : $"费用算法已加载: {string.Join(", ", registry.Ids)}");
        return registry;
    }
}
