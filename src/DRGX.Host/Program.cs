using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DRGX.Engine;
using DRGX.Host;
using DRGX.Host.PluginHosting;
using Microsoft.AspNetCore.Http.Features;

// ============================================================================
// DRGX.Host — CHS-DRG 分组 Web 服务(面向一线编码人员)
//
// 设计要点:
//   * 同栈托管:直接引用 DRGX.Engine 类库,界面与引擎零边界,分组口径与 CLI 完全一致;
//   * 拷贝即用:数据包默认取 exe 同级 data/packs/chs-drg-3.0,无安装、无配置文件;
//     交付形态 = 框架依赖 publish(scripts\pack-release.bat),exe 名 DRGX.exe 且与 plugins\ 同级,
//     目标机需 .NET 10 运行时;别改成自包含/单文件(自包含 zip 16MB→58MB;单文件会把插件一起
//     塞进去、破坏按目录加载,症状是"插件 0 个"且退出码 0,详见 pack-release.bat 与发布技能)。
//   * 面向编码员的接口面:字典检索(名称/编码)、单病例分组(含判定轨迹)、批量分组。
//
// 结构(本文件只做"参数 → 加载 → 组装 → 启动"):
//   WebApp          共享服务容器(启动期加载 ADRG 明细/基层病组/地区费用包/HIS 连接器/字典索引)
//   Endpoints/*     按域拆分的端点注册(Info/Search/Official/Fee/Group/His)
//   Contracts/*     请求/响应 DTO、错误响应出口、地区费用包持有者、JSON 统一口径(ApiJson)
//   WebPaths        路径解析(数据包/费用根/HIS 配置/静态资源内容根)
//
// 配置(优先级 高 → 低,前四级由框架的配置链给出,不需要自己实现):
//   命令行(--xxx)  >  环境变量(Drgx__Port)  >  appsettings.{环境}.json  >
//   appsettings.json(exe 同级、随发布包分发)  >  本文件里的 const 默认值
//   字段与含义见 appsettings.json 自身的注释;这里只记三条纪律:
//     * 不再新增第二套配置源(自定义 ini / 注册表 / 环境变量前缀):框架的链已覆盖
//       全部场景,加第二套只会让"改了没生效"变成排查噩梦;
//     * 敏感值(如 HIS 连接串)不进 appsettings.json,它随包分发 —— 走 *.local.json
//       或环境变量;
//     * 配置项只放"运维真的会改"的东西。业务口径类常量(批量行数/体积上限等)留在
//       BatchLimits 里:它们是单一真值(经 /api/info 下发给界面),放开成配置只会
//       多一种"界面说行、后端说不行"的漂移方式。
//   `DRGX.exe --print-config` 打印生效配置(ASCII 的 key=value 逐行)后退出,不加载任何数据:
//     给 scripts\start-web.bat 取端口用(启动器不自己维护第二套默认值),也可供运维核对。
//
// 退出码: 0=正常退出 / 2=启动配置错误(参数或 appsettings.json)/ 3=数据包不可用 /
//         4=启动失败(端口被占等)
// ============================================================================

// 注册代码页提供程序,使 GBK/GB18030 等非 UTF 编码可用(InvariantGlobalization 下默认禁用)
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

const string UsageLine =
    "用法: DRGX.exe [--pack <数据包目录>] [--port <端口>] [--regions-root <目录>] " +
    "[--regions-reload-seconds <秒>] [--his <配置json>] " +
    "[--plugins-root <目录>] [--no-plugins] [--listen <地址>] [--print-config]";

// ---- 主机建造:builder 必须早于参数解析 ----
// 配置是从 builder 身上取的(CreateBuilder 内部已加载完 appsettings.json)。官方也明确
// 不建议"为了拿配置再调一次 CreateBuilder" —— 那会把整套主机服务重复注册一遍。
WebApplicationBuilder builder;
try
{
    builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        // Args 同时交给框架自带的命令行配置源:它负责通用键值形态(--environment、
        // --Logging:LogLevel:Default=Debug 之类),下方自研解析只管本程序自己的旗标。
        Args = args,
        ContentRootPath = WebPaths.ResolveContentRoot(),
    });
}
catch (FormatException ex)
{
    // 框架的命令行配置源只认 -/--// 开头的键值,裸位置参数(DRGX.exe 后面敲了个词)
    // 在这里就抛,且早于下面的自研解析 —— 不兜住就会以未处理异常收场。
    Console.Error.WriteLine($"启动配置错误: {ex.Message}");
    Console.Error.WriteLine(UsageLine);
    return 2;
}

var drgx = builder.Configuration.GetSection("Drgx");

// ---- 缺省值(配置文件与命令行都覆盖它) ----
const int DefaultPort = 8080;
const int DefaultRegionsReloadSeconds = 30;
const int MaxRegionsReloadSeconds = 86_400;   // 一天:再长等于实际关闭,徒增误解
const string DefaultListen = "127.0.0.1";     // 仅本机:不要默认把患者数据摊到局域网上

// 读配置项的三个小工具:留空 = 当没配(回落默认值);配了但写错则报错,不静默回落
// —— 静默回落会让运维"改了文件却看不出没生效"。
string WithFallback(string? value, string fallback) =>
    string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

int ReadInt(string key, int fallback, int min, int max)
{
    var raw = drgx[key];
    if (string.IsNullOrWhiteSpace(raw)) return fallback;
    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
        ? v
        : throw new ArgumentException($"配置项 Drgx:{key} 需要 {min}~{max} 的整数,收到 \"{raw}\"");
}

bool ReadBool(string key, bool fallback)
{
    var raw = drgx[key];
    if (string.IsNullOrWhiteSpace(raw)) return fallback;
    return bool.TryParse(raw, out var v)
        ? v
        : throw new ArgumentException($"配置项 Drgx:{key} 需要 true 或 false,收到 \"{raw}\"");
}

// 数值参数解析:非法时给出可操作提示并退出,而不是抛未处理异常(栈跟踪对运维无用)。
int ParseInt(string flag, string raw, int min, int max) =>
    int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
        ? v
        : throw new ArgumentException($"{flag} 需要 {min}~{max} 的整数,收到 \"{raw}\"");

var packArg = "data/packs/chs-drg-3.0";
string? regionsRootArg = null;
string? hisArg = null;
string? pluginsRootArg = null;
var noPlugins = false;
var printConfig = false;
var port = DefaultPort;
var regionsReloadSeconds = DefaultRegionsReloadSeconds;
var listenAddress = DefaultListen;

try
{
    // ---- 第一层:appsettings.json / 环境变量 ----
    packArg = WithFallback(drgx["PackPath"], packArg);
    regionsRootArg = NonBlank(drgx["RegionsRoot"]);
    hisArg = NonBlank(drgx["HisConfig"]);
    pluginsRootArg = NonBlank(drgx["PluginsRoot"]);
    noPlugins = ReadBool("NoPlugins", noPlugins);
    port = ReadInt("Port", port, 1, 65535);
    regionsReloadSeconds = ReadInt("RegionsReloadSeconds", regionsReloadSeconds, 0, MaxRegionsReloadSeconds);
    listenAddress = WithFallback(drgx["Listen"], listenAddress);

    // ---- 第二层:命令行(同名项一律覆盖上面的取值) ----
    for (int i = 0; i < args.Length; i++)
    {
        var flag = args[i];
        if (flag == "--no-plugins") { noPlugins = true; continue; }   // 开关型:不取值
        if (flag == "--print-config") { printConfig = true; continue; }   // 开关型:打印生效配置后退出

        // 其余参数都需要取值。缺值时报错而非静默忽略 —— 尾部 flag 被吞掉会导致
        // "参数明明传了却不生效"这类极难排查的问题。
        if (i + 1 >= args.Length) throw new ArgumentException($"{flag} 缺少取值");
        var raw = args[++i];

        if (flag is "--pack" or "-p") packArg = raw;
        else if (flag == "--port") port = ParseInt(flag, raw, 1, 65535);
        else if (flag == "--regions-root") regionsRootArg = raw;
        else if (flag == "--regions-reload-seconds")
            regionsReloadSeconds = ParseInt(flag, raw, 0, MaxRegionsReloadSeconds);
        else if (flag == "--his") hisArg = raw;
        else if (flag == "--plugins-root") pluginsRootArg = raw;
        else if (flag == "--listen") listenAddress = raw.Trim();
        else throw new ArgumentException($"未知参数: {flag}");
    }

    if (listenAddress.Length == 0) throw new ArgumentException("--listen 不能为空");
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"启动配置错误: {ex.Message}");
    Console.Error.WriteLine(UsageLine);
    return 2;
}

// ---- 路径解析(只做存在性探测,不读任何数据) ----
var packPath = WebPaths.Resolve(packArg);

// 默认插件根按发布形态探测两个候选:
//   发布包 —— plugins 就在 exe 同级(打包时把 artifacts 层拍平);
//   开发态 —— 插件工程直接输出到 artifacts/plugins。
// 只写死其中一个,另一种形态下都是"插件 0 个"且毫无报错,所以两个都试(显式 --plugins-root 仍优先)。
//
// 注意不能简单地写裸 "plugins" 了事:仓库里另有 src/plugins(插件**源码**)与
// src/DRGX.Host/PluginHosting(**加载器**)两个同族目录,而候选根以 CWD 优先,
// 若 CWD 恰好是 src\ 就会命中源码目录 —— 所以先按存在性探测,且 launcher 一律
// 传绝对路径 --plugins-root。
var pluginsRoot = pluginsRootArg is not null
    ? WebPaths.Resolve(pluginsRootArg, packPath)
    : WebPaths.ResolveAny(["plugins", "artifacts/plugins"], packPath);

// ---- HIS 配置:显式指定优先,留空则按约定探测 ----
// 为什么必须在这里补:start-web.bat 一直会自动带上 configs\his\healthone-1.local.json,
// 而**直接双击 DRGX.exe 什么参数都不带**。同一棵树、同一份 appsettings.json,
// 两种启动方式一种有 HIS 一种没有,而 appsettings.json 的 Drgx:HisConfig 默认是空串,
// 于是直启得到的是一句 503「HIS 连接器未加载」——现场只会判断成"HIS 坏了"或"包少了东西"。
// 探测规则与启动器同约定(启动器那行保留,不冲突:显式值永远优先)。
hisArg ??= ProbeLocalHisConfig(packPath);

// 配置文件的落点要摆出来:运维改了 appsettings.json 却不生效时,第一件要确认的就是
// "程序找的到底是哪个文件"(发布树里它和 DRGX.exe 同级;没有它则全部走内置默认值)。
var configFile = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");

// ---- --print-config:打印生效配置后立即退出 ----
// 为什么需要它:scripts\start-web.bat 必须知道实际端口(它要等端口起来再开浏览器),
// 而批处理没有可靠的 JSON 解析手段。让"端口的真值"只留在 appsettings.json,由程序把
// 生效值吐出来、启动器照抄 —— 免得启动器再维护第二套默认端口(那正是两边数字开始不一致
// 的来源)。输出刻意是纯 ASCII 的 key=value 逐行,便于批处理用 findstr 取,且不含任何患者数据。
// 空值 = 该项未配置(regions 除外,它总有个默认目录)。
if (printConfig)
{
    Console.WriteLine($"port={port}");
    Console.WriteLine($"listen={listenAddress}");
    Console.WriteLine($"pack={packPath}");
    Console.WriteLine($"regions={WebPaths.Resolve(regionsRootArg ?? "data/regions", packPath)}");
    Console.WriteLine($"plugins={pluginsRoot}");
    Console.WriteLine($"noPlugins={(noPlugins ? "true" : "false")}");
    Console.WriteLine($"regionsReloadSeconds={regionsReloadSeconds}");
    Console.WriteLine($"his={hisArg ?? ""}");
    Console.WriteLine($"config={(File.Exists(configFile) ? configFile : "")}");
    return 0;
}

if (File.Exists(configFile))
    Console.WriteLine($"配置文件: {configFile}");
else
    Console.Error.WriteLine($"配置文件: 未找到 {configFile} —— 全部使用内置默认值(端口 {DefaultPort} 等)。");

// ---- 暴露面提示 ----
// 服务没有账号体系,/api/his/* 会返回患者姓名、病案号、费用等敏感信息。
// 非本机监听时把这件事摆在启动日志里(内网部署场景常见,不再拒绝启动)。
if (!IsLoopbackListen(listenAddress))
{
    Console.Error.WriteLine($"[提示] --listen {listenAddress} 会把服务暴露到非本机网络,而本服务无内置认证:");
    Console.Error.WriteLine("    /api/his/record、/api/his/group、/api/his/group-by-date(姓名/病案号/费用/诊断)");
    Console.Error.WriteLine("    仅本机访问请保持默认(--listen 127.0.0.1);确需暴露请前置带认证的反向代理。");
}
// ---- 插件装载 ----
// (packPath / pluginsRoot 已在上面解析,这里只做加载 —— --print-config 也复用同一份解析结果。)
// 每个插件独立 AssemblyLoadContext;契约程序集(DRGX.Abstractions/Fee/His/Text)一律由默认
// 上下文提供,否则插件里的 IHisConnector 与宿主里的会是两个不同类型。
// 单个插件加载失败只记警告,不影响宿主启动与其他插件。
// 插件根与数据包同在仓库内,packPath 上溯可多几个候选根(dotnet run 的工作目录是工程目录,
// 只靠 CWD 会找不到插件根 —— 症状是"HIS 未加载")。
var pluginResult = noPlugins
    ? new PluginLoadResult(new PluginRegistrar(), [], [], [])
    : PluginLoader.Load(pluginsRoot);

foreach (var err in pluginResult.Errors)
    Console.Error.WriteLine($"警告: 插件 {err}");
if (noPlugins)
    Console.WriteLine("插件: 已关闭(--no-plugins / Drgx:NoPlugins=true) —— 费用算法与 HIS 连接器均不可用");
else if (pluginResult.Loaded.Count == 0)
    Console.WriteLine($"插件: 未加载任何插件({pluginsRoot})");
else
    foreach (var p in pluginResult.Loaded)
        Console.WriteLine($"插件已加载: {p.Id} {p.Version} — {p.Name}");

// ---- 共享服务容器:整包加载 fail-fast,其余加载均"失败降级不炸启动"(细节见 WebApp) ----
WebApp svc;
try
{
    svc = new WebApp(packPath, regionsRootArg, hisArg, pluginResult.Registrar);
}
catch (PackException ex)
{
    Console.Error.WriteLine($"数据包契约错误: {ex.Message}");
    return 3;
}
catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
{
    Console.Error.WriteLine($"未找到数据包目录: {packPath}");
    Console.Error.WriteLine("  检查 --pack 指向的目录,或 appsettings.json 里的 Drgx:PackPath。");
    Console.Error.WriteLine(UsageLine);
    return 3;
}

// 日志级别现在来自 appsettings.json 的 Logging 段(标准键,框架已在 CreateBuilder 里接好)。
// 原先硬编码在这里的 AddFilter("Microsoft"/"System", Warning) 已搬进配置文件 ——
// 硬编码的规则是"后加者覆盖先加者",留在代码里的话配置文件就永远改不动它们。
//
// 唯一留在代码里的是下面这条,它不是可调项,而是对一处 UX 缺陷的绕行:绑定失败时通用主机
// 会先用自己的 logger 打一遍 "Hosting failed to start" 加完整托管栈,把下面那几行可操作
// 提示淹掉;而真需要栈的场景(本过滤未识别的启动异常)由运行时原样打印,信息不会丢。
// 它追加在配置段之后,故必然生效。
builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
builder.WebHost.UseUrls($"http://{listenAddress}:{port}");
builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Apply(o.SerializerOptions)); // JSON 统一口径(单源)

// 表单与请求体上限与 BatchLimits 同源。
// 不设的话 multipart 走框架默认 128MB —— 比我们自己声明的 20MB 大 6 倍,
// 意味着"文件过大"的判定会在整个请求体被缓冲之后才发生。
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = BatchLimits.MaxUploadBytes;
    o.MultipartHeadersLengthLimit = 64 * 1024;
});
builder.WebHost.ConfigureKestrel(k =>
    k.Limits.MaxRequestBodySize = BatchLimits.MaxUploadBytes);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---- 端点注册(按域拆分,见 Endpoints/*) ----
InfoEndpoints.Map(app, svc);
SearchEndpoints.Map(app, svc);
OfficialEndpoints.Map(app, svc);   // 数据一览:官方配置表(官方工作簿)原样回放
FeeEndpoints.Map(app, svc);
GroupEndpoints.Map(app, svc);
HisEndpoints.Map(app, svc);

// 方案版本 / 字典规模 / 可选表就绪这三件事实合成一行(每次启动都打同样内容的字,摊成三行只是
// 把运维的滚动条填满);基层病组缺失属静默降级(结果不再标注该徽标),仍单独走 stderr 并给补法。
var pack = svc.Pack;
var primaryGroups = pack.PrimaryGroups.Count;
Console.WriteLine($"已加载数据包: {pack.Manifest.Scheme} {pack.Manifest.Version} " +
                  $"(配置信息 {pack.Manifest.SourceDate} · 包修订 {pack.Manifest.Revision}) — " +
                  $"诊断 {pack.DiagnosisNames.Count:N0} / 操作 {pack.ProcedureNames.Count:N0} / " +
                  $"DRG 组 {pack.Groups.Count:N0} / 基层病组 {(primaryGroups > 0 ? primaryGroups.ToString() : "无")}");
if (primaryGroups == 0)
    Console.Error.WriteLine("提示: 数据包未提供 primary-groups.csv,分组结果将不标注「基层病组」;如需标注请在包根放置该码表(code,name,category)。");

// ---- 官方配置表轨:数据源就是官方发布的工作簿本身,启动期把装载报告打出来留痕 ----
// 列位移、排除表补录条数、名称更正这些只有装载时才知道;静默通过等于把隐患藏起来
// —— 官方改版会让 ADRG 整表左移一位,按固定索引读会把「名称」当「编码」,而界面上看不出来。
// 所以这十几行是**刻意打全**的:它是工作簿替换掉可 diff 的 CSV 之后,信息科唯一的核对痕迹。
var officialDir = OfficialTable.DirOf(svc.PackPath);
if (OfficialTable.Available(officialDir))
{
    Console.WriteLine("  官方配置表(数据源:官方工作簿,无中间转换产物):");
    foreach (var line in OfficialWorkbook.Load(officialDir).Log) Console.WriteLine($"    {line}");
}

// ---- 启动 ----
// 端口被占是运维最常见的启动失败,不能让它以"未处理异常 + 8KB 英文栈"的形态退出:
// 那是给开发者看的,对着一线排查毫无用处。绑定就发生在 StartAsync 这一步,
// 所以先用 StartAsync 再打 banner —— 旧写法先打 banner 再 Run,端口冲突时会先看到
// "服务已启动,请在浏览器打开 http://localhost:8080/",紧接着才是异常,两句话自相矛盾。
try
{
    await app.StartAsync();
}
catch (Exception ex) when (IsBindFailure(ex))
{
    ReportBindFailure(ex, listenAddress, port);
    return 4;
}

Console.WriteLine("服务已启动,请在浏览器打开:");
Console.WriteLine($"  本机:   http://localhost:{port}/");
if (IsLoopbackListen(listenAddress))
{
    Console.WriteLine("  监听:   仅本机(127.0.0.1)");
}
else
{
    var lan = LanIpv4();
    if (lan is not null)
        Console.WriteLine($"  局域网: http://{lan}:{port}/  (首次运行可能需在防火墙放行)");
    Console.WriteLine("  监听:   全网卡(本服务无内置认证,请确保前置反向代理已做认证或仅限内网访问)");
}

// ---- 地区费用包热更新:周期重扫 regions 根,成功即原子换引用,全程免重启 ----
svc.StartRegionHotReload(app.Lifetime, regionsReloadSeconds);

await app.WaitForShutdownAsync();
return 0;

/// <summary>
/// 按约定探测本地 HIS 配置:<c>configs/his/*.local.json</c>。
/// 仅在 Drgx:HisConfig 留空且命令行未给 <c>--his</c> 时兜底(显式值永远优先)。
/// </summary>
/// <remarks>
/// 刻意<b>不认 *.sample.json</b>:样例里是占位符连接串(<c>&lt;HIS_HOST&gt;</c>),
/// 自动启用它只会把「未启用」变成「连不上」,而后者看起来像网络故障,
/// 比直白地说"没有本地配置"更难排查。
/// 候选多于一个且不含约定文件名时返回 null 并告警,不猜 —— 猜错的表现是"连上了别的库"。
/// </remarks>
static string? ProbeLocalHisConfig(string packPath)
{
    var dir = WebPaths.Resolve("configs/his", packPath);
    if (!Directory.Exists(dir)) return null;

    var candidates = Directory.GetFiles(dir, "*.local.json");
    if (candidates.Length == 0) return null;
    if (candidates.Length == 1) return candidates[0];

    // 多份候选:只认约定名;拿不准就当没有(让运维显式配 Drgx:HisConfig 或 --his)
    var conventional = Path.Combine(dir, "healthone-1.local.json");
    var hit = Array.Find(candidates, f => string.Equals(f, conventional, StringComparison.OrdinalIgnoreCase));
    if (hit is not null) return hit;

    Console.Error.WriteLine($"警告: {dir} 下有 {candidates.Length} 份 *.local.json,无法判断用哪份,本次不启用 HIS。");
    Console.Error.WriteLine($"  指定其中一份:DRGX.exe --his \"{candidates[0]}\"");
    return null;
}

/// <summary>
/// 启动异常是否属于"端口绑定失败"。只做判定,不打日志 —— 异常筛选器可能被执行两次,
/// 带副作用的筛选器会把提示打两遍。
/// </summary>
/// <remarks>
/// Kestrel 的包装链是 IOException("Failed to bind to address http://…: address already in use.")
/// → AddressInUseException → SocketException(10048)。认内层的 <see cref="SocketError"/> 比匹配
/// 文案可靠:系统语言一变文案就跟着变,"address already in use" 到时就不匹配了。
/// </remarks>
static bool IsBindFailure(Exception ex)
{
    for (var e = ex; e is not null; e = e.InnerException)
    {
        if (e is SocketException se)
            return se.SocketErrorCode is SocketError.AddressAlreadyInUse
                or SocketError.AccessDenied
                or SocketError.AddressNotAvailable;
    }
    return false;
}

/// <summary>把绑定失败写成一行结论加几条可操作选项;不打印栈跟踪。</summary>
static void ReportBindFailure(Exception ex, string address, int port)
{
    // 内层 SocketError 决定该给哪条建议;判不出来时给通用那条,不编造原因。
    SocketError? error = null;
    for (var e = ex; e is not null; e = e.InnerException)
    {
        if (e is SocketException se) { error = se.SocketErrorCode; break; }
    }

    switch (error)
    {
        case SocketError.AddressAlreadyInUse:
            Console.Error.WriteLine($"启动失败: 端口 {port} 已被占用,服务无法启动。");
            Console.Error.WriteLine($"    本机已有程序在监听 {address}:{port}(常见:另一个 DRGX 实例,或占用该端口的其他本地服务)。");
            Console.Error.WriteLine($"    换个端口:      DRGX.exe --port 8090");
            Console.Error.WriteLine($"    用 launcher:   改 scripts\\start-web.bat 顶部的 PORT");
            Console.Error.WriteLine($"    查占用者:      netstat -ano | findstr :{port}");
            break;
        case SocketError.AccessDenied:
            Console.Error.WriteLine($"启动失败: 没有权限绑定 {address}:{port}。");
            Console.Error.WriteLine("    低端口号(1024 以下)在部分系统上需要特权,也可能被安全软件或系统保留区间挡住。");
            Console.Error.WriteLine("    换 1024 以上的端口再试。");
            break;
        case SocketError.AddressNotAvailable:
            Console.Error.WriteLine($"启动失败: 监听地址 {address} 在本机不可用。");
            Console.Error.WriteLine("    该地址可能不属于本机任何网卡。仅本机访问用默认的 127.0.0.1。");
            break;
        default:
            Console.Error.WriteLine($"启动失败: 无法绑定 {address}:{port}。");
            Console.Error.WriteLine("    " + ex.Message);
            break;
    }
}

/// <summary>监听地址是否仅限本机(用于决定启动提示)。</summary>
static bool IsLoopbackListen(string address) =>
    address.Equals("localhost", StringComparison.OrdinalIgnoreCase)
    || (IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip));

/// <summary>取首选出口网卡 IPv4(不实际发包),用于启动时提示局域网地址;失败返回 null。</summary>
static string? LanIpv4()
{
    try
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
        socket.Connect("10.255.255.255", 65530); // 不实际发包,仅取首选出口网卡
        return socket.LocalEndPoint is IPEndPoint ep ? ep.Address.ToString() : null;
    }
    catch
    {
        return null;
    }
}
