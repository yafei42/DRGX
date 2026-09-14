namespace DRGX.His;

/// <summary>HIS 连接器契约违反或提取失败的异常(fail-fast,不带病提取)。</summary>
public sealed class HisConnectorException : Exception
{
    public HisConnectorException(string message) : base(message) { }
    public HisConnectorException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// HIS 提取接缝:换 HIS = 换一个 IHisConnector 实现,分组器/费用模块零感知。
/// 实现只做"提取 + 映射为中立记录",不做任何分组/费用计算。
/// </summary>
public interface IHisConnector
{
    /// <summary>连接器标识(来自配置 id),用于响应溯源。</summary>
    string ConnectorId { get; }

    /// <summary>按就诊号/病案号取一条病案;HIS 中无此号返回 null。</summary>
    HisMedicalRecord? FetchRecord(string patientId);

    /// <summary>
    /// 按出院日期区间取就诊号清单(最多 limit 条)。
    /// 取号 SQL 由配置 sql.list 提供(各 HIS 出院日期字段不一,不做默认实现);未配置时抛 HisConnectorException。
    /// </summary>
    IReadOnlyList<string> FetchIdsByDate(DateOnly from, DateOnly to, int limit);
}
