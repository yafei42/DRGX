using System.Text.Json;

namespace DRGX.His.SqlServer;

/// <summary>
/// HIS 连接器配置(his-config.json):
/// kind=his-connector-config / schemaVersion=1 / connector=sqlserver-junwei1 / connectionString 必填;
/// sql.record/diagnoses/operations 可选覆盖提取模板(须含 @id);
/// sql.list = 按出院日期取就诊号清单(须含 @from/@to)。各院出院日期字段不一,故不提供默认值。
/// </summary>
public sealed record HisConfig(
    string Id,
    string Connector,
    string ConnectionString,
    int CommandTimeoutSeconds,
    string? RecordSql,
    string? DiagnosesSql,
    string? OperationsSql,
    string? ListSql = null)
{
    /// <summary>
    /// 本连接器支持的 <c>connector</c> 键。同时用于宿主解析插件(见 SqlServerHisPlugin.Register)。
    ///
    /// <para>历史沿革:此前误标为 <c>sqlserver-junwei1</c>。本连接器针对的是 <b>HealthOne</b> 系统,
    /// 查询的视图(vae1/VAO2/RAB1/BAK1/… )也是 HealthOne 的;改名后旧 id 不再被接受。</para>
    /// </summary>
    public const string KnownConnector = "sqlserver-healthone";

    /// <summary>
    /// 改名前的旧 id。**故意也登记为可解析的键**(见 SqlServerHisPlugin.Register),
    /// 但其工厂直接抛出说明性异常 —— 这样用户看到的是"已改名,请改成 X",
    /// 而不是宿主层的"没有插件提供该连接器"(宿主只用键找插件,压根读不到本配置)。
    /// </summary>
    public const string LegacyConnector = "sqlserver-junwei1";

    public static HisConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new HisConnectorException($"HIS 配置文件不存在: {path}");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (JsonException ex)
        {
            throw new HisConnectorException($"HIS 配置不是合法 JSON: {ex.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            var kind = ReqString(root, "kind");
            if (kind != "his-connector-config")
                throw new HisConnectorException($"HIS 配置 kind 必须为 his-connector-config,实际为 {kind}");
            if (ReqInt(root, "schemaVersion") != 1)
                throw new HisConnectorException("HIS 配置 schemaVersion 仅支持 1");
            var connector = ReqString(root, "connector");
            if (connector == LegacyConnector)
                throw new HisConnectorException(
                    $"HIS 配置 connector=\"{LegacyConnector}\" 是改名前的旧值,已不再接受。" +
                    $"请改为 \"{KnownConnector}\"(本连接器针对 HealthOne 系统;视图与 SQL 模板均无变化,改这一处即可)。");
            if (connector != KnownConnector)
                throw new HisConnectorException(
                    $"HIS 配置 connector 不支持: {connector}(当前仅 {KnownConnector})");
            var cs = ReqString(root, "connectionString");

            var id = root.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } i
                ? i : Path.GetFileNameWithoutExtension(path);
            var timeout = root.TryGetProperty("commandTimeoutSeconds", out var tEl) && tEl.TryGetInt32(out var t)
                ? Math.Clamp(t, 1, 600) : 30;

            string? Sql(string prop)
            {
                if (root.TryGetProperty("sql", out var sqlEl) && sqlEl.ValueKind == JsonValueKind.Object
                    && sqlEl.TryGetProperty(prop, out var el) && el.GetString() is { Length: > 0 } s)
                {
                    var required = prop == "list" ? new[] { "@from", "@to" } : new[] { "@id" };
                    foreach (var p in required)
                        if (!s.Contains(p, StringComparison.Ordinal))
                            throw new HisConnectorException($"HIS 配置 sql.{prop} 必须包含命名参数 {p}");
                    return s;
                }
                return null;
            }
            return new HisConfig(id, connector, cs.Trim(), timeout, Sql("record"), Sql("diagnoses"), Sql("operations"), Sql("list"));
        }

        static string ReqString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
                throw new HisConnectorException($"HIS 配置缺少必填字段或类型错误: {name}");
            var s = el.GetString();
            return string.IsNullOrWhiteSpace(s)
                ? throw new HisConnectorException($"HIS 配置字段不能为空: {name}") : s;
        }

        static int ReqInt(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
                throw new HisConnectorException($"HIS 配置缺少必填字段或非整数: {name}");
            return v;
        }
    }
}
