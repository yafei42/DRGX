namespace DRGX.Host;

/// <summary>
/// 地区费用包集合持有者:热更新后台任务整批替换不可变的 RegionPackSet 快照,
/// 请求端读到的引用要么是旧集要么是新集,不存在半新半旧(volatile 保证跨线程可见)。
/// 请求级 region → 包解析也收敛在此(严格版供费用必需端点,宽松版供分组端点费用 join)。
/// </summary>
internal sealed class FeeRegionSetHolder
{
    private volatile DRGX.Fee.RegionPackSet? _current;

    /// <summary>当前快照;构造方在启动期即换入初始集,请求期恒非空。</summary>
    public DRGX.Fee.RegionPackSet Current =>
        _current ?? throw new InvalidOperationException("地区费用包集合尚未初始化");

    public void Swap(DRGX.Fee.RegionPackSet next) => _current = next;

    /// <summary>请求级地区包解析(费用必需端点):未传 region → 默认包;未知 key → 400;无包 → 503。</summary>
    public (DRGX.Fee.RegionPack? Pack, IResult? Error) ResolveRegion(string? region)
    {
        var regionSet = Current;
        if (regionSet.Packs.Count == 0)
            return (null, Results.Problem(statusCode: 503, title: "地区费用包未加载",
                detail: "费用数据(RW/参考费用)内嵌地区费用包;请将包放入 data/regions/<地区>/<版本>/(热更新 ≤30s 自动加载,或 --regions-root <目录>)"));

        // 有包则默认位恒非空:official 包优先,一个 official 包都没有时退回最新包
        // (见 RegionPackSet.PickDefaultKey;该包的 dataLevel/demo 会在响应与页面上标注)。
        if (string.IsNullOrWhiteSpace(region))
            return (regionSet.Default, null);

        var pack = regionSet.Resolve(region);
        return pack is null
            ? (null, ApiResults.BadRequest($"未知区域: {region}(可用区域见 GET /api/fee/regions)"))
            : (pack, null);
    }

    /// <summary>
    /// 请求级地区包解析(宽松版,分组端点的费用 join 用):未指定 region → 默认包(有包时非空),
    /// 无任何包时返回 null 包继续分组(仅缺 RW/参考费用,与历史"未加载地区包"口径一致);
    /// 显式传了未知 key → 400。
    /// </summary>
    public (DRGX.Fee.RegionPack? Pack, IResult? Error) ResolveRegionLoose(string? region)
    {
        var regionSet = Current;
        if (string.IsNullOrWhiteSpace(region))
            return (regionSet.Default, null);   // 无任何包时 Default 为 null → 不做费用 join,不影响分组

        var pack = regionSet.Resolve(region);
        return pack is null
            ? (null, ApiResults.BadRequest($"未知区域: {region}(可用区域见 GET /api/fee/regions)"))
            : (pack, null);
    }
}
