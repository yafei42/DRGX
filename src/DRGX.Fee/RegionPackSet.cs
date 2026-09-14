using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DRGX.Fee;

/// <summary>
/// 地区费用包集合(多区域注册表):启动期扫描 data/regions/&lt;regionId&gt;/&lt;dataVersion&gt;/ 一次加载,
/// 运行期只读。单包加载失败或不在生效期只记警告跳过,不影响其它包(降级风格与单包时代一致)。
/// Key = 相对根目录的路径('/' 分隔,如 "zhoukou/2026-r1"),与目录结构一一对应,即请求级 region 参数。
/// </summary>
public sealed class RegionPackSet
{
    private readonly FrozenDictionary<string, RegionPack> _packs;

    private RegionPackSet(Dictionary<string, RegionPack> packs, List<string> warnings)
    {
        _packs = packs.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Warnings = warnings;
        DefaultKey = PickDefaultKey(packs);
        ContentFingerprint = ComputeFingerprint();
    }

    /// <summary>
    /// 集合内容指纹：包 key → 各包 <see cref="RegionPack.ContentFingerprint"/> 的稳定拼接（含告警文本）。
    /// 热更新据此判断「是否真的变了」——只比 key 集合会漏掉「就地改 weights.csv」这类内容变更。
    /// </summary>
    public string ContentFingerprint { get; }

    private string ComputeFingerprint()
    {
        var sb = new StringBuilder();
        foreach (var w in Warnings) sb.Append("W:").Append(w).Append('\n');
        foreach (var (key, pack) in _packs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(key).Append('=').Append(pack.ContentFingerprint).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())).AsSpan(0, 16));
    }

    /// <summary>已加载的地区包(key = 相对根目录路径,大小写不敏感)。</summary>
    public IReadOnlyDictionary<string, RegionPack> Packs => _packs;

    /// <summary>加载期警告(坏包/非生效包/重复 key,均已跳过)。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>默认包 key:official 包中 EffectiveFrom 最新者;无 official 包时退回全体包中最新者;无包为 null。</summary>
    public string? DefaultKey { get; }

    /// <summary>
    /// 默认包(请求未带 region 时的回退);无包时为 null。
    /// </summary>
    public RegionPack? Default => DefaultKey is null ? null : _packs[DefaultKey];

    /// <summary>按请求 key 取包(大小写不敏感);未命中或 key 为空返回 null,由调用方回退默认或报错。</summary>
    public RegionPack? Resolve(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null
        : _packs.TryGetValue(key.Trim(), out var p) ? p : null;

    /// <summary>
    /// 默认包选择:<b>先在 <c>dataLevel = official</c>(该地区公布的值)中取 EffectiveFrom 最新者;
    /// 一个 official 包都没有时,退回全体包中 EffectiveFrom 最新者</b>(可能是 reference 非本地值包);
    /// 无包为 null。并列取 regionId 序,再 dataVersion 自然序。
    ///
    /// <para><b>为什么 official 必须优先:</b> <c>reference</c> 是非该地区公布值的包(外部参考 / 演示用),
    /// 不作为地方结算依据。若与 official 包同场竞争,一个生效日期更晚的非本地值包
    /// 会把真正的地方执行包挤下去 —— 于是"不传 region"的调用者拿到的默认值恰是**不可用于结算**的那个,
    /// 且金额看起来完全正常。这类错误没有任何外部征兆,只能靠这一层的过滤挡住。</para>
    ///
    /// <para><b>为什么一个 official 包都没有时仍给默认:</b> 只部署非本地值包的场景(演示 / 评估)
    /// 若默认位为空,不带 region 的费用调用会一律 400,页面费用面板也要先手动选一次地区才可用。
    /// 此时退回最新包,其性质在三处标注(<c>/api/fee/regions</c> 的 <c>dataLevel</c>/<c>demo</c> 字段、
    /// 启动日志逐包标注、前端下拉与费用面板的「非本地值 · 演示数据」),不会冒充地方执行口径。</para>
    ///
    /// <para>dataVersion 用自然序比较,防 "2026-r10" 字典序 &lt; "2026-r2" 的陷阱。</para>
    /// </summary>
    private static string? PickDefaultKey(Dictionary<string, RegionPack> packs) =>
        PickNewest(packs, onlyOfficial: true) ?? PickNewest(packs, onlyOfficial: false);

    /// <summary>
    /// 取 EffectiveFrom 最新者(<paramref name="onlyOfficial"/> 时只看 official 包);
    /// 并列取 regionId 序,再 dataVersion 自然序。无候选返回 null。
    /// </summary>
    private static string? PickNewest(Dictionary<string, RegionPack> packs, bool onlyOfficial)
    {
        string? bestKey = null;
        string bestRegion = "";
        var bestFrom = DateOnly.MinValue;
        string bestVersion = "";
        foreach (var (key, pack) in packs)
        {
            // 第一轮只看官方口径包:非本地值包不得挤掉地方执行包
            if (onlyOfficial &&
                !string.Equals(pack.Manifest.DataLevel, "official", StringComparison.OrdinalIgnoreCase))
                continue;

            var regionId = pack.Manifest.Id;
            var from = ParseDate(pack.Manifest.EffectiveFrom);
            var version = pack.Manifest.DataVersion;
            bool better = bestKey is null
                || from > bestFrom
                || (from == bestFrom && (Ordinal(regionId, bestRegion) < 0
                    || Ordinal(regionId, bestRegion) == 0 && NaturalCompare(version, bestVersion) > 0));
            if (!better) continue;
            bestKey = key;
            bestRegion = regionId;
            bestFrom = from;
            bestVersion = version;
        }
        return bestKey;
    }

    private static int Ordinal(string a, string b) => string.Compare(a, b, StringComparison.Ordinal);

    private static DateOnly ParseDate(string? raw) =>
        DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var d) ? d : DateOnly.MinValue;

    /// <summary>自然序比较:数字段按数值、其余按 Ordinal;保证 r2 &lt; r10。</summary>
    static int NaturalCompare(string a, string b)
    {
        for (int i = 0, j = 0; i < a.Length || j < b.Length;)
        {
            if (i < a.Length && j < b.Length && char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                long va = 0, vb = 0;
                while (i < a.Length && char.IsDigit(a[i])) va = va * 10 + (a[i++] - '0');
                while (j < b.Length && char.IsDigit(b[j])) vb = vb * 10 + (b[j++] - '0');
                if (va != vb) return va < vb ? -1 : 1;
            }
            else
            {
                char ca = i < a.Length ? a[i++] : '\0';
                char cb = j < b.Length ? b[j++] : '\0';
                if (ca != cb) return ca < cb ? -1 : 1;
            }
        }
        return 0;
    }

    /// <summary>
    /// 扫描地区根目录并加载全部生效包(两级:&lt;regionId&gt;/&lt;dataVersion&gt;/manifest.json)。
    /// 根目录不存在视为零包(纯分组部署),不报错;每个包的加载与校验规则与 RegionPack.Load 一致。
    /// </summary>
    public static RegionPackSet LoadRoot(string rootDir, DateOnly asOf)
    {
        var packs = new Dictionary<string, RegionPack>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        if (!Directory.Exists(rootDir))
            return new RegionPackSet(packs, warnings);

        foreach (var regionDir in SubDirs(rootDir))
        foreach (var versionDir in DirsWithManifest(regionDir))
        {
            var key = Path.GetRelativePath(rootDir, versionDir).Replace('\\', '/');
            try
            {
                var pack = RegionPack.Load(versionDir);
                if (!pack.IsEffectiveOn(asOf))
                {
                    warnings.Add($"{key}: 不在生效期({pack.Manifest.EffectiveFrom ?? "∞"} ~ {pack.Manifest.EffectiveUntil ?? "∞"}),已跳过。");
                    continue;
                }
                if (!packs.TryAdd(key, pack))
                    warnings.Add($"{key}: 与其它目录的清单重复,已跳过后者。");
            }
            catch (Exception ex)
            {
                warnings.Add($"{key}: 加载失败,已跳过 - {ex.Message}");
            }
        }
        return new RegionPackSet(packs, warnings);
    }

    /// <summary>按 key 序枚举直接子目录(确定性加载顺序 → Default 稳定)。</summary>
    private static IEnumerable<string> SubDirs(string dir) =>
        Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal);

    /// <summary>子目录中含 manifest.json 的(即地区包版本目录);目录碎片静默跳过。</summary>
    private static IEnumerable<string> DirsWithManifest(string dir) =>
        SubDirs(dir).Where(d => File.Exists(Path.Combine(d, "manifest.json")));
}
