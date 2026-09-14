using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace DRGX.His.SqlServer;

/// <summary>
/// SQL Server 连接器,默认契约 = HealthOne视图(与历史项目 DrgGrouperApiHis/DataAccess.cs 1:1):
///   病案: vae1(主档) + v_vak_full/vbm2(费用回退) + bdp02(险种映射);
///   诊断: VAO2(ACF01=3,VAO10=0,VAO11=3,按 VAO06 排序) join BAK1(医保对照 BAK24);
///   手术: RAB1(按 ROWNR 排序) join BAK1(bak17>=getdate() 生效期)。
/// 对历史实现的三处修正:
///   1) ageDay 不再硬编码 0 — 由 AgeDetail(000Y00M00D00W) 解析;
///   2) 全程命名参数,杜绝拼接;
///   3) 连接工厂可注入;行→记录映射抽为 BuildRecord 可直测(无需真实 SQL Server)。
/// </summary>
public sealed class SqlServerHisConnector : IHisConnector
{
    private readonly HisConfig _config;
    private readonly Func<DbConnection> _connectionFactory;

    public string ConnectorId => _config.Id;

    public SqlServerHisConnector(HisConfig config) : this(config, null) { }

    /// <summary>connectionFactory 供测试注入;生产态按配置连接串创建 SqlConnection。</summary>
    public SqlServerHisConnector(HisConfig config, Func<DbConnection>? connectionFactory)
    {
        _config = config ?? throw new HisConnectorException("HIS 配置不能为 null");
        _connectionFactory = connectionFactory ?? (() => new SqlConnection(config.ConnectionString));
    }

    // ---- 默认提取 SQL(可被配置 sql.* 覆盖);参数一律 @id。public 以便契约测试直查 ----
    public const string DefaultRecordSql = @"
DECLARE @vak08 NUMERIC
SELECT @vak08 = vak08 FROM v_vak_full WHERE vaa07 = @id AND vak06 = 4 AND vak01a = 0
IF (@vak08 IS NOT NULL)
    SELECT vae01 id, vae94 inpatientNum, vae95 name, vae96 gender,
           CASE a.aau01 WHEN 'Y' THEN vae46 ELSE 0 END age,
           vae87 ageDetail,
           CASE a.bdp02 WHEN '城乡居民' THEN '居民' WHEN '城镇职工' THEN '职工' WHEN '自费' THEN '自费' ELSE '其他' END insurType,
           vae90 weight, vae27 inHospitalTime,
           CASE WHEN abv01 IS NULL THEN 9 ELSE abv01 END leavingType,
           @vak08 fee
    FROM vae1 a WITH (NOLOCK) WHERE a.vae01 = @id
ELSE
    SELECT vae01 id, vae94 inpatientNum, vae95 name, vae96 gender,
           CASE a.aau01 WHEN 'Y' THEN vae46 ELSE 0 END age,
           vae87 ageDetail,
           CASE a.bdp02 WHEN '城乡居民' THEN '居民' WHEN '城镇职工' THEN '职工' WHEN '自费' THEN '自费' ELSE '其他' END insurType,
           vae90 weight, vae27 inHospitalTime,
           CASE WHEN abv01 IS NULL THEN 9 ELSE abv01 END leavingType,
           b.vbm05 fee
    FROM vae1 a WITH (NOLOCK)
    JOIN vbm2 b WITH (NOLOCK) ON a.vae01 = b.vaa07 AND a.vaa01 = b.VAA01 AND b.acf01 = 2
    WHERE a.vae01 = @id";

    public const string DefaultDiagnosesSql = @"
SELECT b.BAK02 icd, b.BAK05 name, b.BAK24 yibao
FROM VAO2 a WITH (NOLOCK)
LEFT JOIN BAK1 b WITH (NOLOCK) ON a.BAK01A = b.BAK01
WHERE a.ACF01 = 3 AND a.VAO10 = 0 AND a.VAO11 = 3 AND a.VAA07 = @id
ORDER BY a.VAO06";

    public const string DefaultOperationsSql = @"
SELECT a.BAK02 icd, a.BAK05 name, h.BAK24 yibao
FROM RAB1 a WITH (NOLOCK)
LEFT JOIN BAK1 h WITH (NOLOCK) ON a.BAK02 = h.BAK02 AND h.bak17 >= GETDATE()
WHERE a.VAA07 = @id
ORDER BY a.ROWNR";

    /// <summary>默认按出院日期区间取就诊号清单(HealthOne vae1.vae26=出院日期)。</summary>
    public const string DefaultListSql = @"
SELECT vae01
FROM vae1 WITH (NOLOCK)
WHERE vae26 >= @from AND vae26 < DATEADD(DAY, 1, @to)";

    public HisMedicalRecord? FetchRecord(string patientId)
    {
        var id = (patientId ?? "").Trim();
        if (id.Length == 0)
            throw new HisConnectorException("就诊号不能为空");
        try
        {
            using var conn = _connectionFactory();
            conn.Open();
            var row = QueryOne(conn, _config.RecordSql ?? DefaultRecordSql, id);
            if (row is null) return null;
            var diags = QueryRefs(conn, _config.DiagnosesSql ?? DefaultDiagnosesSql, id);
            var ops = QueryRefs(conn, _config.OperationsSql ?? DefaultOperationsSql, id);
            return BuildRecord(row, diags, ops, id);
        }
        catch (HisConnectorException) { throw; }
        catch (DbException ex)
        {
            throw new HisConnectorException($"HIS 提取失败({_config.Id}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 行→中立病案映射(公开以便直测):按列名取值,列缺失/脏数据视为 null,不炸整单。
    ///
    /// <para><b>Age / AgeDay 的口径(易错,勿简化):</b>
    /// <c>Age</c> 未知时保持 null(绝不写 0 —— 0 岁是"新生儿"的合法取值,用它表示未知会让引擎
    /// 启用日龄判定);<c>AgeDay</c> 只在年龄明细含月/日/周证据时给出,成年患者(age&gt;0)
    /// 与"只有年/时/分"的明细一律为 null。二者区分开,引擎才不会把成人当 0 天新生儿。</para>
    /// </summary>
    public static HisMedicalRecord BuildRecord(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<HisCodeRef> diags,
        IReadOnlyList<HisCodeRef> ops,
        string fallbackId)
    {
        var age = row.GetIntOrNull("age");
        var ageDetail = row.GetStringOrNull("ageDetail");
        return new HisMedicalRecord(
            PatientId: row.GetStringOrNull("id") ?? fallbackId,
            Name: row.GetStringOrNull("name"),
            Gender: row.GetStringOrNull("gender"),
            Age: age,
            // age>0 = 明确成年 → 无日龄;否则看明细里有没有日级证据(没有则为 null,不是 0)
            AgeDay: age is > 0 ? null : AgeDetailParser.Parse(ageDetail),
            Weight: row.GetIntOrNull("weight"),
            InsuranceType: row.GetStringOrNull("insurType"),
            AgeDetail: ageDetail,
            InHospitalDays: row.GetIntOrNull("inHospitalTime"),
            LeavingType: row.GetStringOrNull("leavingType"),
            TotalFee: row.GetDecimalOrNull("fee"),
            Diagnoses: diags,
            Operations: ops);
    }

    // ---- 行读取:按列名取值(列缺失视为 null,宽容 HIS 视图字段增减) ----

    private Dictionary<string, object?>? QueryOne(DbConnection conn, string sql, string id)
    {
        using var cmd = CreateCommand(conn, sql, ("@id", id));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private List<HisCodeRef> QueryRefs(DbConnection conn, string sql, string id)
    {
        var list = new List<HisCodeRef>();
        using var cmd = CreateCommand(conn, sql, ("@id", id));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = ReadRow(reader);
            var icd = row.GetStringOrNull("icd");
            if (string.IsNullOrWhiteSpace(icd)) continue; // 对照缺失行(无编码)跳过,不计入
            list.Add(new HisCodeRef(icd.Trim(), row.GetStringOrNull("name"), row.GetStringOrNull("yibao")));
        }
        return list;
    }

    /// <summary>按出院日期区间取就诊号清单:执行配置 sql.list(@from/@to) 或默认 HealthOne SQL,客户端截断到 limit。</summary>
    public IReadOnlyList<string> FetchIdsByDate(DateOnly from, DateOnly to, int limit)
    {
        var sql = _config.ListSql ?? DefaultListSql;
        if (limit < 1) limit = 1;
        try
        {
            using var conn = _connectionFactory();
            conn.Open();
            var ids = new List<string>();
            using (var cmd = CreateCommand(conn, sql, ("@from", from), ("@to", to)))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read() && ids.Count < limit)
                {
                    var v = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0))?.Trim();
                    if (!string.IsNullOrEmpty(v)) ids.Add(v);
                }
            }
            return ids;
        }
        catch (HisConnectorException) { throw; }
        catch (DbException ex)
        {
            throw new HisConnectorException($"HIS 提取失败({_config.Id}): {ex.Message}", ex);
        }
    }

    private DbCommand CreateCommand(DbConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = Math.Clamp(_config.CommandTimeoutSeconds, 1, 600);
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static Dictionary<string, object?> ReadRow(DbDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }
}

/// <summary>行取值扩展:列缺失/DBNull/脏数据一律归 null,不炸整单。</summary>
file static class RowExtensions
{
    public static string? GetStringOrNull(this IReadOnlyDictionary<string, object?> row, string col) =>
        row.TryGetValue(col, out var v) && v is not null and not DBNull ? Convert.ToString(v)?.Trim() : null;

    public static int? GetIntOrNull(this IReadOnlyDictionary<string, object?> row, string col)
    {
        if (!row.TryGetValue(col, out var v) || v is null or DBNull) return null;
        try { return Convert.ToInt32(v); }
        catch (FormatException) { return null; }
        catch (InvalidCastException) { return null; }
        catch (OverflowException) { return null; }
    }

    public static decimal? GetDecimalOrNull(this IReadOnlyDictionary<string, object?> row, string col)
    {
        if (!row.TryGetValue(col, out var v) || v is null or DBNull) return null;
        try { return Convert.ToDecimal(v); }
        catch (FormatException) { return null; }
        catch (InvalidCastException) { return null; }
        catch (OverflowException) { return null; }
    }
}
