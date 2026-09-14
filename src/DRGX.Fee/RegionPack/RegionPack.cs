using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DRGX.Fee;

/// <summary>
/// 地区费用包(费用数据唯一来源):加载并校验 data/regions/&lt;regionId&gt;/&lt;版本&gt;/ 目录。
/// 结构:manifest.json + weights.csv(全国基准 RW/参考费用,必选) +
///       parameters.json(算法参数,由各算法自行解析) +
///       drg-attributes.csv(可选,病组标记/分等级系数) + weights-override.csv(可选,稀疏权重覆盖)。
/// 权重查询 = weights.csv 基准 ⊕ 本包稀疏覆盖(加载期一次合并)。
/// </summary>
public sealed partial class RegionPack
{
    public const int SupportedSchemaVersion = 2;
    public const string ManifestKind = "region-fee-config";

    private readonly FrozenDictionary<string, FeeWeightEntry> _weights;
    private readonly FrozenSet<string> _overrideCodes;
    private readonly FrozenDictionary<string, DrgAttributes> _attributes;
    private readonly FrozenDictionary<string, IReadOnlyDictionary<string, decimal>> _payments;
    private readonly DateOnly? _effectiveFrom;
    private readonly DateOnly? _effectiveUntil;

    private RegionPack(RegionPackManifest manifest, JsonElement parameters, FeeParameters parsed,
        Dictionary<string, FeeWeightEntry> weights, List<string> overrideCodes,
        Dictionary<string, DrgAttributes> attributes,
        Dictionary<string, IReadOnlyDictionary<string, decimal>> payments,
        List<string> warnings)
    {
        Manifest = manifest;
        Parameters = parameters;
        Parsed = parsed;
        Warnings = warnings;
        WeightRows = weights.Count;
        WeightPublished = weights.Values.Count(e => e.Status == "published");
        // 生效期在此一次性解析（Invariant + fail-fast）：既避免每次 IsEffectiveOn 重复 Parse，
        // 也杜绝非 invariant 文化下把 "2026-07-01" 解析错（如 th-TH 的佛历）。
        _effectiveFrom = ParseOptionalDate(manifest.EffectiveFrom, "effectiveFrom");
        _effectiveUntil = ParseOptionalDate(manifest.EffectiveUntil, "effectiveUntil");
        _weights = weights.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _overrideCodes = overrideCodes.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _attributes = attributes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _payments = payments.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>可选的 yyyy-MM-dd 日期；空 → null，非法格式在加载期 fail-fast。</summary>
    private static DateOnly? ParseOptionalDate(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw new FeeWeightsException($"manifest.json: {field}=\"{raw}\" 不是合法日期(应为 yyyy-MM-dd)");
        return d;
    }

    public RegionPackManifest Manifest { get; }

    /// <summary>算法参数(parameters.json 根对象;由各算法按自身 schema 解析)。</summary>
    public JsonElement Parameters { get; }

    /// <summary>参数强类型视图(含 cityAvgCost/pointScale 与倍率策略),由加载期解析并校验。</summary>
    public FeeParameters Parsed { get; }

    /// <summary>加载期非阻断告警(参数未显式声明、点值量级可疑等;包仍可用)。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>权重行数(含覆盖合并后的全集)。</summary>
    public int WeightRows { get; }

    /// <summary>已公布权重行数(published;覆盖给出 RW 的 unpublished 行随覆盖翻转)。</summary>
    public int WeightPublished { get; }

    /// <summary>被地区覆盖的 DRG 码集(规范码,大小写不敏感)。</summary>
    public IReadOnlyCollection<string> OverrideCodes => _overrideCodes;

    /// <summary>
    /// 按 DRG 码查权重(大小写不敏感)。Source = "base"(weights.csv 基准) / "override"(地区覆盖);未命中 null。
    /// </summary>
    public (FeeWeightEntry Entry, string Source)? FindWeight(string? drgCode)
    {
        if (!_weights.TryGetValue((drgCode ?? "").Trim(), out var e)) return null;
        return (e, _overrideCodes.Contains(e.DrgCode) ? "override" : "base");
    }

    /// <summary>按 DRG 码查地区属性(大小写不敏感);未登记返回 false。</summary>
    public bool TryGetAttributes(string drgCode, out DrgAttributes? attributes) =>
        _attributes.TryGetValue((drgCode ?? "").Trim(), out attributes);

    /// <summary>全部权重条目(只读枚举,供宿主列表/抽样;含合并覆盖后的最终权重)。</summary>
    public IEnumerable<FeeWeightEntry> Entries => _weights.Values;

    /// <summary>
    /// 解析医院等级系数。顺序:逐 DRG 系数列(<c>levels</c> → drg-attributes) →
    /// 全局等级系数(<c>levelFactors</c>) → <c>defaultFactor</c>。
    ///
    /// 未知等级**抛错**而非静默回退 defaultFactor —— 静默回退会把「三甲」按默认系数算,
    /// 金额错得毫无痕迹。未指定等级时按 defaultFactor(调用方语义:不做等级区分)。
    /// </summary>
    /// <remarks>
    /// 本方法原为 PointValueAlgorithm 的 internal 静态方法,被 DirectPaymentAlgorithm 复用,
    /// 导致两个算法插件互相耦合。二者本质上都是「地区包的查询」,故上移到此处,
    /// 使算法插件之间零依赖。
    /// </remarks>
    public decimal ResolveLevelFactor(string drgCode, string? level)
    {
        var p = Parsed;
        string? column = null;
        if (level is { Length: > 0 })
        {
            var name = level.Trim();
            if (p.Levels.TryGetValue(name, out column))
            {
                // 命中逐 DRG 列映射,走 drg-attributes 查询
            }
            else if (p.GlobalLevelFactors.TryGetValue(name, out var globalFactor))
            {
                return globalFactor;
            }
            else
            {
                var options = new List<string>();
                options.AddRange(p.Levels.Keys);
                options.AddRange(p.GlobalLevelFactors.Keys);
                throw new FeeWeightsException(
                    $"未知医院等级 \"{level}\",本地区可选: {(options.Count > 0 ? string.Join("/", options) : "(未配置)")}");
            }
        }

        if (column is null || !TryGetAttributes(drgCode, out var attrs) || attrs is null)
            return p.DefaultFactor;

        return attrs.Factors.TryGetValue(column, out var f) && f is not null ? f.Value : p.DefaultFactor;
    }

    /// <summary>
    /// 病组标记(支付/管理口径):基础病组 / 非稳定病组 / 基础病组 非稳定病种;无标记或未登记返回 null。
    /// 纯展示与算法共用口径,不参与分组判定。
    /// </summary>
    public string? ResolveMark(string drgCode)
    {
        if (!TryGetAttributes(drgCode, out var a) || a is null) return null;
        if (a.Basic && a.Unstable) return "基础病组 非稳定病种";
        if (a.Basic) return "基础病组";
        if (a.Unstable) return "非稳定病组";
        return null;
    }

    private string? _fingerprint;

    /// <summary>
    /// 内容指纹：由<b>已加载内容</b>（权重全集 + 分等级系数 + 支付标准 + manifest 关键字段）
    /// 推出的稳定 SHA-256 短摘要（前 16 字节十六进制）。
    ///
    /// 用途：地区包热更新判定「是否真的变了」。只比对包 key 集合与告警条数会漏掉最常见的运维动作
    /// ——在同一个 <c>&lt;region&gt;/&lt;version&gt;/</c> 目录里就地改 <c>weights.csv</c>：内容变了，
    /// 但键与告警数不变，于是永远不换引用、一直供旧权重。
    /// 不用文件 mtime/大小：一次 <c>touch</c> 或换行符差异就会触发无意义的换引用。
    /// </summary>
    public string ContentFingerprint => _fingerprint ??= ComputeFingerprint();

    private string ComputeFingerprint()
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append(Manifest.DataVersion).Append('|')
          .Append(Manifest.SchemeVersion).Append('|')
          .Append(Manifest.EffectiveFrom).Append('|')
          .Append(Manifest.EffectiveUntil).Append('|')
          .Append(Manifest.DataLevel).Append('|')
          .Append(WeightRows).Append('|').Append(WeightPublished).Append(';');

        foreach (var e in _weights.Values.OrderBy(e => e.DrgCode, StringComparer.Ordinal))
            sb.Append(e.DrgCode).Append(':').Append(Fmt(e.Rw)).Append(':').Append(Fmt(e.AvgCost))
              .Append(':').Append(e.Status).Append(';');

        foreach (var (code, a) in _attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(code).Append(a.Basic ? 'B' : '-').Append(a.Unstable ? 'U' : '-');
            foreach (var (k, v) in a.Factors.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(',').Append(k).Append('=').Append(Fmt(v));
            sb.Append(';');
        }

        foreach (var (code, pays) in _payments.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(code).Append(':');
            foreach (var (k, v) in pays.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(',').Append(k).Append('=').Append(v.ToString(CultureInfo.InvariantCulture));
            sb.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())).AsSpan(0, 16));

        static string Fmt(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>
    /// 按 DRG 码查支付标准表(direct-payment 算法):口径名 → 支付标准(元)。
    /// 无 payment.csv 或该 DRG 无行返回 false。
    /// </summary>
    public bool TryGetPayments(string drgCode, out IReadOnlyDictionary<string, decimal>? payments) =>
        _payments.TryGetValue((drgCode ?? "").Trim(), out payments);

    /// <summary>日期是否在包生效期内(effectiveFrom/effectiveUntil 均为闭区间,空为不设限)。</summary>
    public bool IsEffectiveOn(DateOnly date)
    {
        if (_effectiveFrom is { } from && from > date) return false;
        if (_effectiveUntil is { } until && until < date) return false;
        return true;
    }
}
