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
    /// </summary>
    public Dictionary<string, AdrgInfo> AdrgInfo => _adrgInfo;
    private readonly Dictionary<string, AdrgInfo> _adrgInfo;
    /// <summary>DRG 细分组条件原文(DRG码 → rules 内 split.origin,合并症等级/年龄属性/特殊入组条件三列综合)。
    /// 前端分组结果「条件原文」直接取用;构造期随快照一并派生。</summary>
    public Dictionary<string, string> DrgOrigin => _drgOrigin;
    private readonly Dictionary<string, string> _drgOrigin;

    /// <summary>DRG 码 → 直赋档规则(高危妊娠直赋/机器人直赋):落位由特殊身份条件决定
    /// (高危妊娠码表/机器人辅助手术清单),与合并症分档无关。前端"DRG 细分组落位(…)"标题
    /// 据此替代 MCC/CC 推断文案(直赋档 0 MCC/0 CC 时按计数会误标"不伴并发症")。</summary>
    public Dictionary<string, DrgDirectRule> DrgDirect => _drgDirect;
    private readonly Dictionary<string, DrgDirectRule> _drgDirect;

    /// <summary><see cref="ResolveDrgDirect(string?, string, IReadOnlyList{string})"/> 的病案级重载:
    /// 先按所选编码版本把主诊断与全部手术操作码规范化到医保版(与引擎 Normalize 同口径),再判定。
    /// 单病例/文件批量/HIS 三条路径共用,口径唯一。</summary>
    public string? ResolveDrgDirect(GroupOutcome outcome, MedicalRecord rec, CodeSystem system)
    {
        var pack = Pack; // 经委托取包(单一入口)
        var withMap = system == CodeSystem.Guolin; // 医保版输入不做任何转换
        var main = rec.Diagnoses.Count > 0 ? rec.Diagnoses[0] : "";
        var directMain = withMap && pack.DiagnosisMap.TryGetValue(main, out var mappedMain) ? mappedMain : main;
        var directProcs = withMap
            ? rec.Procedures.Select(p => pack.ProcedureMap.TryGetValue(p, out var mp) ? mp : p).ToList()
            : (IReadOnlyList<string>)rec.Procedures;
        return ResolveDrgDirect(outcome.Code, directMain, directProcs);
    }

    /// <summary>请求期判定直赋档:落位 DRG 属直赋档、且该病例确实命中其特殊身份条件时返回说明,否则 null
    /// (前端回退 MCC/CC 推断文案)。判定只认规则条件,与是否同时有 MCC 无关
    /// ——官方口径:"主诊断在 811 清单内时直接定 OB11/OB21,不再核对合并症"。
    /// 与数据包同源:rules 内 OB11/OB21 的 when 为 any[mainDiagnosisIn, hasMcc],直赋条件已置首
    /// (首中语义即优先级),此处复核的 Probe 就是 when 里那个 mainDiagnosisIn 节点。
    /// 顺序护栏:直赋条件须置于 when 的 any 首位(原由 tools/check_direct_priority.py 校验,
    /// 该工具链未随本仓库发布,改动 rules 时须人工复核此顺序)。
    /// 必须复核而非按落位码静态下发:同一 DRG 常有多条落位路径(纯 MCC 也能落 OB11/OB21),
    /// 静态下发会把常规分档误标成直赋。</summary>
    /// <param name="code">落位 DRG 码。</param>
    /// <param name="mainDiagnosis">已按编码版本规范化(映射后)的主要诊断码。</param>
    /// <param name="procedures">已按编码版本规范化(映射后)的全部手术操作码。</param>
    public string? ResolveDrgDirect(string? code, string mainDiagnosis, IReadOnlyList<string> procedures)
    {
        if (string.IsNullOrEmpty(code)) return null;
        if (!DrgDirect.TryGetValue(code, out var rule)) return null;
        // 一律按规则条件判定,不看合并症:机器人辅助看触发码表(与 GrouperEngine 同源的
        // 官方 OP_ARB 集合),高危妊娠看 split 内 mainDiagnosisIn 的 811 清单。
        return rule.Kind switch
        {
            DrgDirectKind.Robot => procedures.Any(p => Pack.RobotProcedures.Contains(p)) ? rule.Label : null,
            DrgDirectKind.HighRisk => rule.Probe is not null && rule.Probe.CodeSet.Contains(mainDiagnosis) ? rule.Label : null,
            _ => null,
        };
    }
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
        _drgOrigin = PackIndexes.BuildDrgOrigin(_runtime.Pack);
        _drgDirect = PackIndexes.BuildDrgDirect(_runtime.Pack);
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
