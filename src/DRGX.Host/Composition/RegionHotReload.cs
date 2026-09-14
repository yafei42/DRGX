using Microsoft.Extensions.Hosting;

namespace DRGX.Host;

/// <summary>
/// 地区费用包热更新:周期重扫 regions 根,成功即原子换引用,全程免重启。
///
/// <para>与 <see cref="RuntimeLoader"/> 的分工:后者负责启动期那一次装载,本类负责运行期的
/// 持续重扫。两者共用「按内容指纹比对、内容没变就不换引用」这一条判据 ——
/// 只在启动期写一遍会漏掉最常见的运维动作(就地改 weights.csv)。</para>
/// </summary>
internal static class RegionHotReload
{
    /// <param name="holder">要换引用的容器(启动期由 <see cref="RuntimeLoader.LoadFeeRegions"/> 建好)。</param>
    /// <param name="regionsRoot">地区费用包根目录。</param>
    /// <param name="intervalSeconds">轮询间隔(秒);≤0 表示关闭热更新。</param>
    public static void Start(FeeRegionSetHolder holder, string regionsRoot,
        IHostApplicationLifetime lifetime, int intervalSeconds)
    {
        if (intervalSeconds <= 0) return;
        // CTS 存活期 = 进程生命周期:不能用 using(app.Run 阻塞前的块退出会立即 Dispose,任务随即死亡)
        var cts = new CancellationTokenSource();
        lifetime.ApplicationStopping.Register(() => cts.Cancel());
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, intervalSeconds)));
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    try
                    {
                        // asOf 每轮重算:未来生效日的包(如提前部署的调点值版本)到期后自动激活
                        var next = DRGX.Fee.RegionPackSet.LoadRoot(regionsRoot, DateOnly.FromDateTime(DateTime.Today));
                        var current = holder.Current;
                        // 按内容指纹比对(而非仅 key 集合 + 告警条数):后者会漏掉最常见的运维动作
                        // ——在同一 <region>/<version>/ 目录里就地改 weights.csv,内容变了却永远不换引用。
                        if (next.ContentFingerprint == current.ContentFingerprint)
                            continue; // 内容无变化(含哈希校验一致的快照),不打日志不换引用
                        holder.Swap(next);
                        Console.WriteLine($"地区费用包已热更新: {string.Join(", ", next.Packs.Keys.OrderBy(k => k, StringComparer.Ordinal))}(共 {next.Packs.Count} 个)");
                        foreach (var w in next.Warnings)
                            Console.Error.WriteLine($"警告: 地区费用包 {w}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"警告: 地区费用包热更新失败,沿用当前快照 - {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { /* 服务停止,正常退出 */ }
        });
    }
}
