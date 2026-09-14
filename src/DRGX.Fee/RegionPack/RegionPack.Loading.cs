using DRGX.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DRGX.Fee;

public sealed partial class RegionPack
{
    // ================================ 加载 ================================

    public static RegionPack Load(string directory)
    {
        if (!Directory.Exists(directory))
            throw new FeeWeightsException($"地区费用包目录不存在: {directory}");

        var manifest = ReadManifest(Path.Combine(directory, "manifest.json"));
        VerifyFileHashes(directory, manifest.Files);   // 解析之前先验完整性:坏包绝不进入计费路径
        var parameters = ReadParameters(Path.Combine(directory, "parameters.json"));
        var weights = ParseWeights(Path.Combine(directory, "weights.csv"), manifest);
        var overrideCodes = new List<string>();
        if (manifest.Files.ContainsKey("weights-override.csv"))
            ApplyOverrides(ParseOverrides(Path.Combine(directory, "weights-override.csv")), weights, overrideCodes);
        var attributes = manifest.Files.ContainsKey("drg-attributes.csv")
            ? ParseAttributes(Path.Combine(directory, "drg-attributes.csv"))
            : new Dictionary<string, DrgAttributes>(StringComparer.OrdinalIgnoreCase);
        var payments = manifest.Files.ContainsKey("payment.csv")
            ? ParsePayments(Path.Combine(directory, "payment.csv"), weights)
            : new Dictionary<string, IReadOnlyDictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);

        // 参数解析 + 一致性校验(cityAvgCost 缺失时由权重表推断)
        var warnings = new List<string>();
        var parsed = FeeParametersReader.Read(parameters, InferCityAvgCost(weights));
        Validate(parsed, weights, warnings);

        return new RegionPack(manifest, parameters, parsed, weights, overrideCodes, attributes, payments, warnings);
    }

    /// <summary>
    /// 由权重表推断全市次均费用:取 avg_cost ÷ rw 的中位数(理论上市次均应为常数)。
    /// 仅在 parameters.json 未显式声明 cityAvgCost 时启用,结果仅供兼容,不作为校验基准。
    /// </summary>
    private static decimal? InferCityAvgCost(Dictionary<string, FeeWeightEntry> weights)
    {
        var ratios = weights.Values
            .Where(e => e.Status == "published" && e.Rw is > 0 && e.AvgCost is > 0)
            .Select(e => e.AvgCost!.Value / e.Rw!.Value)
            .OrderBy(v => v)
            .ToList();
        return ratios.Count == 0 ? null : ratios[ratios.Count / 2];
    }

    /// <summary>
    /// 加载期一致性校验。三条规则针对已确认的真实故障:
    /// ① avg_cost 与 rw×cityAvgCost 冲突(数据源错用,如非本地公布值当地方执行值)—— 显式声明时强校验;
    /// ② 点值量级与 cityAvgCost/pointScale 不自洽(控费系数越界)—— 告警;
    /// ③ pointScale 未显式声明 —— 告警。
    /// </summary>
    private static void Validate(FeeParameters p, Dictionary<string, FeeWeightEntry> weights, List<string> warnings)
    {
        if (p.CityAvgCostInferred)
        {
            warnings.Add($"parameters.json 未声明 cityAvgCost,已由权重表推断为 {p.CityAvgCost:F2} 元" +
                         "(推断值无法校验数据源,建议显式填写地方公布的全市次均费用)");
        }
        else
        {
            int total = 0, mismatched = 0;
            string? sample = null;
            foreach (var e in weights.Values)
            {
                if (e.Status != "published" || e.Rw is null or <= 0 || e.AvgCost is null) continue;
                total++;
                var expected = e.Rw.Value * p.CityAvgCost!.Value;
                if (Math.Abs(e.AvgCost.Value - expected) <= Math.Abs(expected) * 0.01m) continue;
                mismatched++;
                sample ??= $"{e.DrgCode}(avg_cost={e.AvgCost.Value:F2}, rw×cityAvgCost={expected:F2})";
            }
            if (total > 0 && mismatched > 0)
                throw new FeeWeightsException(
                    $"weights.csv 与 parameters.json 数据冲突: {mismatched}/{total} 行的 avg_cost 与 " +
                    $"rw×cityAvgCost({p.CityAvgCost:F2}) 偏差超过 1%,例如 {sample}。" +
                    "常见原因是把非本地公布的数据当作地方执行值,请核对数据源,或删除 avg_cost 列改由 rw×cityAvgCost 推导");
        }

        foreach (var (type, value) in p.PointValues)
        {
            var control = value * p.PointScale / p.CityAvgCost!.Value;
            if (control is >= 0.5m and <= 1.5m) continue;
            warnings.Add($"点值 \"{type}\"={value} 与 cityAvgCost={p.CityAvgCost:F2}、pointScale={p.PointScale} " +
                         $"推算的控费系数为 {control:F3},超出合理区间 0.5~1.5(实测 56 地区为 0.518~1.342)," +
                         "请核对点值量级或 pointScale");
        }

        if (!p.PointScaleExplicit)
            warnings.Add($"parameters.json 未声明 pointScale,按 100 处理" +
                         "(实测存在 1/100/1000 三档:天津=1、多数地区=100、枣庄/南昌=1000,建议显式填写)");
    }

    /// <summary>
    /// 校验 manifest.files 声明的 SHA-256 与实际文件内容一致(小写十六进制,大小写不敏感比较)。
    ///
    /// <para><b>为什么必须有这一步:</b> 地区费用包的既定运维动作就是"就地改 weights.csv 调点值/权重"
    /// (RegionPack 的 ContentFingerprint 热更新正是为此设计)。没有哈希校验时,篡改、损坏、
    /// 写到一半的文件都会被原样加载并用于计费,而溯源信息(/api/fee/estimate 的 provenance)
    /// 仍然声称是 manifest 声明的那个版本 —— 出数无法追溯。</para>
    ///
    /// <para>反向也校验:manifest 未登记但目录里存在的文件不检查(允许附注类文件),
    /// 但已登记却缺失、或声明了空哈希,都视为清单本身有错。</para>
    /// </summary>
    private static void VerifyFileHashes(string directory, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (name, declared) in files)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                throw new FeeWeightsException($"manifest.files 登记了 {name},但该文件不存在({directory})");
            if (string.IsNullOrWhiteSpace(declared))
                throw new FeeWeightsException(
                    $"manifest.files 未给出 {name} 的 sha256。哈希是「包内容与声明版本一致」的唯一凭证," +
                    "留空等于放弃完整性校验;请用 sha256sum 生成小写十六进制后填入。");

            string actual;
            try
            {
                actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            catch (IOException ex)
            {
                throw new FeeWeightsException($"{name} 读取失败,无法校验完整性 - {ex.Message}");
            }

            if (!actual.Equals(declared.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new FeeWeightsException(
                    $"{name} 内容与 manifest 声明不符: 声明 {declared.Trim().ToLowerInvariant()},实际 {actual.ToLowerInvariant()}。" +
                    "该文件已被改动或损坏;若确为有意更新,请同步更新 manifest.files 的哈希并提升 dataVersion。");
        }
    }

    private static RegionPackManifest ReadManifest(string path)
    {
        if (!File.Exists(path))
            throw new FeeWeightsException("地区费用包缺少 manifest.json");
        var json = ReadJsonRoot(path, "manifest.json");

        string Get(string name) => json.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? "" : "";

        // 必填整数:缺失或形状不对都报错。
        // 旧实现"非 Number 一律返回 0"会让 "weightsRows": "825"(带引号)静默变成 0,
        // 从而把行数校验整个关掉;"825.0" 则抛裸 FormatException 逃出 FeeWeightsException 契约。
        int GetInt(string name)
        {
            if (!json.TryGetProperty(name, out var e))
                throw new FeeWeightsException($"manifest.json: 缺少必填整数字段 {name}");
            if (e.ValueKind != JsonValueKind.Number)
                throw new FeeWeightsException(
                    $"manifest.json: {name} 类型应为整数,实际为 {e.ValueKind}" +
                    (e.ValueKind == JsonValueKind.String ? $"(值 \"{e.GetString()}\";数字不要加引号)" : ""));
            if (e.TryGetInt32(out var i)) return i;
            if (e.TryGetDecimal(out var d) && d == decimal.Truncate(d))
                throw new FeeWeightsException(
                    $"manifest.json: {name} 必须写成整数,当前为小数形式 {d}。请改为 \"{name}\": {(long)d}");
            throw new FeeWeightsException($"manifest.json: {name} 不是有效整数");
        }

        var kind = Get("kind");
        if (kind != ManifestKind)
            throw new FeeWeightsException($"manifest.json: kind=\"{kind}\" 非法,应为 \"{ManifestKind}\"");
        var schemaVersion = GetInt("schemaVersion");
        if (schemaVersion != SupportedSchemaVersion)
            throw new FeeWeightsException(
                $"manifest.json: schemaVersion={schemaVersion} 不受支持(当前 v{SupportedSchemaVersion}:权重已内嵌 weights.csv)");

        var id = Get("id");
        if (id.Length == 0) throw new FeeWeightsException("manifest.json: id(地区标识)不能为空");
        var algorithm = Get("algorithm");
        if (algorithm.Length == 0) throw new FeeWeightsException("manifest.json: algorithm(算法标识)不能为空");
        var dataVersion = Get("dataVersion");
        if (dataVersion.Length == 0) throw new FeeWeightsException("manifest.json: dataVersion 不能为空");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Object)
            foreach (var f in filesEl.EnumerateObject())
                files[f.Name] = f.Value.GetString() ?? "";
        if (!files.ContainsKey("parameters.json"))
            throw new FeeWeightsException("manifest.json: files 清单缺少 parameters.json");
        if (!files.ContainsKey("weights.csv"))
            throw new FeeWeightsException("manifest.json: files 清单缺少 weights.csv(v2 起权重内嵌地区包,费用数据唯一来源)");

        return new RegionPackManifest(
            kind, id, Get("regionName"), algorithm,
            json.TryGetProperty("baseWeightsId", out var bw) && bw.ValueKind == JsonValueKind.String ? bw.GetString() : null,
            Get("schemeVersion"), dataVersion,
            ReadDataLevel(json),
            ReadDemo(json),
            json.TryGetProperty("effectiveFrom", out var ef) && ef.ValueKind == JsonValueKind.String ? ef.GetString() : null,
            json.TryGetProperty("effectiveUntil", out var eu) && eu.ValueKind == JsonValueKind.String ? eu.GetString() : null,
            schemaVersion, GetInt("weightsRows"), GetInt("weightsPublished"), files, Get("notes"));
    }

    /// <summary>是否演示数据:未声明按 false(既有/正式包保持原语义);非布尔类型视为清单错误。</summary>
    private static bool ReadDemo(JsonElement json)
    {
        if (!json.TryGetProperty("demo", out var e) || e.ValueKind == JsonValueKind.Null)
            return false;
        if (e.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return e.GetBoolean();
        throw new FeeWeightsException(
            $"manifest.json: demo 类型应为布尔,实际为 {e.ValueKind}(若要标注演示数据请写 \"demo\": true)");
    }

    /// <summary>数据级别:仅允许 official / reference;未声明按 official(既有包保持原语义)。</summary>
    private static string ReadDataLevel(JsonElement json)
    {
        if (!json.TryGetProperty("dataLevel", out var e) || e.ValueKind != JsonValueKind.String)
            return "official";
        var level = (e.GetString() ?? "").Trim().ToLowerInvariant();
        if (level.Length == 0) return "official";
        return level switch
        {
            "official" or "reference" => level,
            _ => throw new FeeWeightsException(
                $"manifest.json: dataLevel=\"{level}\" 非法,应为 official(该地区公布值)或 reference(非该地区公布值)"),
        };
    }

    private static JsonElement ReadParameters(string path)
    {
        if (!File.Exists(path))
            throw new FeeWeightsException("地区费用包缺少 parameters.json(manifest.files 已登记但文件缺失)");
        var json = ReadJsonRoot(path, "parameters.json");
        if (json.ValueKind != JsonValueKind.Object)
            throw new FeeWeightsException("parameters.json: 根节点必须是对象");
        return json;
    }

    /// <summary>
    /// 解析 JSON 文件并返回<b>脱离 JsonDocument 的独立根元素</b>。
    ///
    /// <c>JsonDocument</c> 由 <c>ArrayPool</c> 租借缓冲；<c>RootElement</c> 反向持有 document，
    /// 只要根元素可达，池化缓冲就不会归还（<c>RegionPack.Parameters</c> 长期持有 → 持续占用）。
    /// 故此处 <c>using</c> 立即释放 document，并用 <see cref="JsonElement.Clone"/> 让返回的根元素
    /// 自带独立副本（manifest/parameters 各仅一次解析，复制开销可忽略）。
    /// </summary>
    private static JsonElement ReadJsonRoot(string path, string label)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FeeWeightsException($"{label}: JSON 解析失败 - {ex.Message}");
        }
    }

    // ================================ 权重(weights.csv,必选) ================================

    /// <summary>weights.csv:表头 drg_code,drg_name,rw,avg_cost,rw_status。fail-fast:重复码/状态与数值矛盾/行数与清单不符一律拒绝。</summary>
    private static Dictionary<string, FeeWeightEntry> ParseWeights(string path, RegionPackManifest manifest)
    {
        if (!File.Exists(path))
            throw new FeeWeightsException("地区费用包缺少 weights.csv(manifest.files 已登记但文件缺失)");
        var rows = Csv.Read(FileText.ReadText(path));
        if (rows.Count == 0)
            throw new FeeWeightsException("weights.csv: 无内容");

        var header = rows[0];
        int Col(string name) => header.FindIndex(h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        var cCode = Col("drg_code"); var cName = Col("drg_name");
        var cRw = Col("rw"); var cCost = Col("avg_cost"); var cStatus = Col("rw_status");
        if (cCode < 0 || cName < 0 || cRw < 0 || cStatus < 0)
            throw new FeeWeightsException("weights.csv: 表头缺少必需列(drg_code/drg_name/rw/rw_status)");

        var entries = new Dictionary<string, FeeWeightEntry>(StringComparer.OrdinalIgnoreCase);
        int published = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(f => f.Trim().Length == 0)) continue; // 空行静默跳过
            string line = $"weights.csv 第{i + 1}行";
            string F(int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
            var code = F(cCode);
            if (code.Length == 0) throw new FeeWeightsException($"{line}: drg_code 为空");
            if (!entries.TryAdd(code, ParseEntry(code, F(cName), F(cRw), F(cCost), F(cStatus), line)))
                throw new FeeWeightsException($"{line}: DRG 码重复 \"{code}\"");
            if (F(cStatus) == "published") published++;
        }
        if (entries.Count == 0)
            throw new FeeWeightsException("weights.csv: 无数据行");
        if (manifest.WeightsRows > 0 && manifest.WeightsRows != entries.Count)
            throw new FeeWeightsException(
                $"weights.csv: 数据行数 {entries.Count} 与 manifest.weightsRows={manifest.WeightsRows} 不一致,清单与数据未同步");
        if (manifest.WeightsPublished > 0 && manifest.WeightsPublished != published)
            throw new FeeWeightsException(
                $"weights.csv: published 行数 {published} 与 manifest.weightsPublished={manifest.WeightsPublished} 不一致,清单与数据未同步");

        return entries;
    }

    private static FeeWeightEntry ParseEntry(string code, string name, string rwRaw, string costRaw, string status, string line)
    {
        if (status != "published" && status != "unpublished")
            throw new FeeWeightsException($"{line}: rw_status=\"{status}\" 非法,应为 published/unpublished");

        decimal? ParseDec(string raw, string field)
        {
            if (raw.Length == 0) return null;
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                throw new FeeWeightsException($"{line}: {field}=\"{raw}\" 不是合法数值(Invariant 十进制)");
            return v;
        }

        var rw = ParseDec(rwRaw, "rw");
        var cost = ParseDec(costRaw, "avg_cost");
        if (status == "published" && rw is null)
            throw new FeeWeightsException($"{line}: rw_status=published 但 rw 为空,数据自相矛盾");
        if (status == "unpublished" && rw is not null)
            throw new FeeWeightsException($"{line}: rw_status=unpublished 但 rw 有值,数据自相矛盾");

        return new FeeWeightEntry(code, name, rw, cost, status);
    }

    // ================================ 可选数据文件 ================================

    /// <summary>
    /// payment.csv(direct-payment 算法的支付标准查表):表头 drg_code + 任意口径列(如 支付标准_三级职工)。
    /// 码必须已在 weights.csv 登记且已公布(无 RW 的组不参与支付);数值 &gt; 0;码唯一。
    /// </summary>
    private static Dictionary<string, IReadOnlyDictionary<string, decimal>> ParsePayments(
        string path, Dictionary<string, FeeWeightEntry> weights)
    {
        if (!File.Exists(path))
            throw new FeeWeightsException("payment.csv 缺失(manifest.files 已登记但文件不存在)");
        var rows = Csv.Read(FileText.ReadText(path));
        if (rows.Count == 0)
            throw new FeeWeightsException("payment.csv: 无内容");

        var header = rows[0];
        int cCode = header.FindIndex(h => h.Trim().Equals("drg_code", StringComparison.OrdinalIgnoreCase));
        if (cCode < 0)
            throw new FeeWeightsException("payment.csv: 表头缺少必需列(drg_code)");
        if (header.Count < 2)
            throw new FeeWeightsException("payment.csv: 除 drg_code 外至少需要一列支付标准口径");

        var result = new Dictionary<string, IReadOnlyDictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(f => f.Trim().Length == 0)) continue;
            string line = $"payment.csv 第{i + 1}行";
            string F(int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
            var code = F(cCode);
            if (code.Length == 0) throw new FeeWeightsException($"{line}: drg_code 为空");
            if (!result.TryAdd(code, ReadRow())) throw new FeeWeightsException($"{line}: DRG 码重复 \"{code}\"");

            IReadOnlyDictionary<string, decimal> ReadRow()
            {
                if (!weights.TryGetValue(code, out var entry) || entry.Status != "published")
                    throw new FeeWeightsException(
                        $"{line}: DRG \"{code}\" 不在 weights.csv 或未公布 RW(无权重组不参与支付查表)");
                var std = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < header.Count; c++)
                {
                    if (c == cCode) continue;
                    var name = header[c].Trim();
                    if (name.Length == 0) continue;
                    var raw = F(c);
                    if (raw.Length == 0) continue; // 该口径未公布,留空由算法报错或回退
                    if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0)
                        throw new FeeWeightsException($"{line}: {name}=\"{raw}\" 不是正数(Invariant 十进制)");
                    std[name] = v;
                }
                if (std.Count == 0)
                    throw new FeeWeightsException($"{line}: 无任何口径的支付标准数值");
                return std;
            }
        }
        if (result.Count == 0)
            throw new FeeWeightsException("payment.csv: 无数据行");
        return result;
    }

    /// <summary>weights-override.csv:表头 drg_code,rw(仅登记与本包基准不同的 DRG;码唯一)。</summary>
    private static List<WeightOverride> ParseOverrides(string path)
    {
        var rows = Csv.Read(FileText.ReadText(path));
        if (rows.Count == 0) return new List<WeightOverride>();

        var header = rows[0];
        int Col(string name) => header.FindIndex(h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        var cCode = Col("drg_code"); var cRw = Col("rw");
        if (cCode < 0 || cRw < 0)
            throw new FeeWeightsException("weights-override.csv: 表头缺少必需列(drg_code/rw)");

        var overrides = new List<WeightOverride>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(f => f.Trim().Length == 0)) continue;
            string line = $"weights-override.csv 第{i + 1}行";
            string F(int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
            var code = F(cCode);
            if (code.Length == 0) throw new FeeWeightsException($"{line}: drg_code 为空");
            if (!seen.Add(code)) throw new FeeWeightsException($"{line}: DRG 码重复 \"{code}\"");
            var rwRaw = F(cRw);
            if (rwRaw.Length == 0) throw new FeeWeightsException($"{line}: rw 为空(覆盖行必须给出权重)");
            if (!decimal.TryParse(rwRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var rw))
                throw new FeeWeightsException($"{line}: rw=\"{rwRaw}\" 不是合法数值(Invariant 十进制)");
            overrides.Add(new WeightOverride(code, rw));
        }
        return overrides;
    }

    /// <summary>
    /// 稀疏覆盖应用到基准权重上(就地修改)。覆盖键集必须 ⊆ 基准键集;
    /// 覆盖行给出 RW 即视为该地区已公布,unpublished 基础行随之翻转为 published。
    /// </summary>
    private static void ApplyOverrides(List<WeightOverride> overrides,
        Dictionary<string, FeeWeightEntry> weights, List<string> overrideCodes)
    {
        foreach (var o in overrides)
        {
            if (!weights.TryGetValue(o.DrgCode, out var baseEntry))
                throw new FeeWeightsException(
                    $"weights-override.csv: DRG 码 \"{o.DrgCode}\" 不在本包 weights.csv 中(覆盖键集必须 ⊆ 基准键集)");
            weights[o.DrgCode] = baseEntry with { Rw = o.Rw, Status = "published" };
            overrideCodes.Add(baseEntry.DrgCode); // 存基准行的规范码,避免大小写差异影响 FindWeight 的 Source 判定
        }
    }

    /// <summary>
    /// drg-attributes.csv:表头 drg_code,basic_flag,unstable_flag + 任意多个 factor_* 系数列。
    /// 标记取值 "是"/"true"/"1" 视为真,其余为假;系数留空为 null(走 defaultFactor)。
    /// </summary>
    private static Dictionary<string, DrgAttributes> ParseAttributes(string path)
    {
        var rows = Csv.Read(FileText.ReadText(path));
        var result = new Dictionary<string, DrgAttributes>(StringComparer.OrdinalIgnoreCase);
        if (rows.Count == 0) return result;

        var header = rows[0];
        var factorCols = header
            .Select((h, i) => (Name: h.Trim(), Idx: i))
            .Where(x => x.Name.StartsWith("factor_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var cCode = header.FindIndex(h => h.Trim().Equals("drg_code", StringComparison.OrdinalIgnoreCase));
        var cBasic = header.FindIndex(h => h.Trim().Equals("basic_flag", StringComparison.OrdinalIgnoreCase));
        var cUnstable = header.FindIndex(h => h.Trim().Equals("unstable_flag", StringComparison.OrdinalIgnoreCase));
        if (cCode < 0 || cBasic < 0 || cUnstable < 0)
            throw new FeeWeightsException("drg-attributes.csv: 表头缺少必需列(drg_code/basic_flag/unstable_flag)");

        static bool Flag(string raw) => raw is "是" or "true" or "1";
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(f => f.Trim().Length == 0)) continue;
            string line = $"drg-attributes.csv 第{i + 1}行";
            string F(int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
            var code = F(cCode);
            if (code.Length == 0) throw new FeeWeightsException($"{line}: drg_code 为空");
            if (!result.TryAdd(code, new DrgAttributes(code, Flag(F(cBasic)), Flag(F(cUnstable)), ReadFactors())))
                throw new FeeWeightsException($"{line}: DRG 码重复 \"{code}\"");

            Dictionary<string, decimal?> ReadFactors()
            {
                var factors = new Dictionary<string, decimal?>();
                foreach (var (name, idx) in factorCols)
                {
                    var raw = F(idx);
                    decimal? v = null;
                    if (raw.Length > 0)
                    {
                        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                            throw new FeeWeightsException($"{line}: {name}=\"{raw}\" 不是合法数值(Invariant 十进制)");
                        v = parsed;
                    }
                    factors[name] = v;
                }
                return factors;
            }
        }
        return result;
    }
}
