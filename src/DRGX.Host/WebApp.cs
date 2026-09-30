using DRGX.Engine;
using DRGX.Host.PluginHosting;

namespace DRGX.Host;

// ============================================================================
// WebApp — Web 宿主的共享服务容器(组装根状态)
//
// 启动期一次性完成全部加载(除数据包运行时外均"失败降级不炸启动"):ADRG 入组明细 /
// 地区费用包 / HIS 连接器(基层病组清单随包根码表 primary-groups.csv 加载,见 DataPack.PrimaryGroups)。
// 数据包运行时(Pack/Engine/字典索引/MDC 原始
// 形态)聚在 PackRuntime 快照里,启动期装载一次、之后不再变更(改 official/ 下的工作簿需重启)。
// 端点类(Endpoints/*)只读本容器,不各自捕获顶层状态——Program.cs 拆分后的状态中枢。
//
// 本类只做「持有 + 转发」:
//   * 数据包派生索引  → Pack/PackIndexes.cs
//   * 外部资源装载    → Composition/RuntimeLoader.cs
//   * 地区包热更新    → Composition/RegionHotReload.cs
// ============================================================================

internal sealed class WebApp
{
    private readonly PackRuntime _runtime; // 构造期装载一次;PackRuntime.Load 失败则构造中止,不会留 null
    private int _seq; // 病案索引号发生器(跨端点单调)

    public string PackPath { get; }

    /// <summary>运行时快照(启动期装载一次)。端点统一经此取值、不各自捕获引用,
    /// 让「谁读了数据包」保持单一入口。</summary>
    public PackRuntime Runtime => _runtime;

    // —— 运行时快照委托:全部经 Runtime 转发 ——
    public DataPack Pack => Runtime.Pack;
    public GrouperEngine Engine => Runtime.Engine;
    /// <summary>按编码版本取字典索引(检索候选 + 精确查码)。</summary>
    public CodeDictIndex Index(CodeSystem system) => Runtime.Index(system);

    /// <summary>
    /// ADRG 入组明细:由数据包运行时派生(rules 内 ADRG 的 origin + 码表规模),
    /// 不再依赖随包分发的 adrg-info.json。构造期随快照一并派生。
    ///
    /// <para>只承载**展示增强**信息(ADRG 名、所属 MDC 名、"可入 N 种"的码数)。
    /// 判定结论(官方入组条件原文、分档、直赋档)一律随 <see cref="DRGX.Engine.GroupOutcome"/>
    /// 下发,本表不再参与任何判定。</para>
    /// </summary>
    public Dictionary<string, AdrgInfo> AdrgInfo => _adrgInfo;
    private readonly Dictionary<string, AdrgInfo> _adrgInfo;

    /// <summary>基层病组(支付/管理口径的 DRG 子集)清单:随包根码表 <c>primary-groups.csv</c>
    /// 加载(见 <see cref="DataPack.PrimaryGroups"/>),缺失即空表。仅用于分组结果标注,不影响分组判定。</summary>
    public IReadOnlyList<PrimaryGroupInfo> PrimaryGroups => _runtime.Pack.PrimaryGroups;
    public DRGX.Fee.FeeAlgorithmRegistry FeeAlgorithms { get; }
    public FeeRegionSetHolder FeeRegions { get; }

    /// <summary>地区费用包根目录(热更新任务按此重扫)。</summary>
    public string RegionsRoot { get; }

    public DRGX.His.IHisConnector? HisConnector { get; }

    /// <param name="plugins">插件登记表:费用算法与 HIS 连接器均由此解析,宿主不 new 任何实现。</param>
    public WebApp(string packPath, string? regionsRootArg, string? hisArg, PluginRegistrar plugins)
    {
        PackPath = packPath;
        _runtime = PackRuntime.Load(packPath);
        _adrgInfo = PackIndexes.BuildAdrgInfo(_runtime.Pack);
        FeeAlgorithms = RuntimeLoader.LoadFeeAlgorithms(plugins);
        RegionsRoot = WebPaths.Resolve(regionsRootArg ?? "data/regions", packPath);
        FeeRegions = RuntimeLoader.LoadFeeRegions(RegionsRoot);
        HisConnector = RuntimeLoader.LoadHisConnector(hisArg, packPath, plugins);
    }

    /// <summary>生成病案索引号(跨端点单调;前缀区分来源:W=页面手工,H=HIS 提取)。</summary>
    public string NextIndex(string prefix) => $"{prefix}{Interlocked.Increment(ref _seq):000000}";

    /// <summary>启动地区费用包热更新轮询;intervalSeconds≤0 关闭。实现见 <see cref="RegionHotReload"/>。</summary>
    public void StartRegionHotReload(IHostApplicationLifetime lifetime, int intervalSeconds) =>
        RegionHotReload.Start(FeeRegions, RegionsRoot, lifetime, intervalSeconds);
}
