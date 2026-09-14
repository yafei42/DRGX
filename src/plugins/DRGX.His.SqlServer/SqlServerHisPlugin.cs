using DRGX.Abstractions;

namespace DRGX.His.SqlServer;

/// <summary>SQL Server / HealthOne HIS 病案提取连接器插件入口。</summary>
public sealed class SqlServerHisPlugin : IPlugin
{
    public PluginInfo Info { get; } = new(
        "DRGX.His.SqlServer",
        "SQL Server HIS 病案提取连接器(HealthOne)",
        "1.0.0",
        "按 his-connector-config 配置取病案、诊断、手术与出院日期区间就诊号清单;SQL 模板可逐条覆盖。");

    public void Register(IPluginRegistrar registrar)
    {
        // key = 配置里的 connector 字段。宿主先读 configs/his/*.json 的 connector,再按此解析插件。
        registrar.Register<IHisConnector>(HisConfig.KnownConnector, Build);

        // 旧 id(sqlserver-junwei1)也登记,但工厂直接抛错 —— 只为把"已改名"这句送到用户眼前。
        // 不登记的话宿主只会报「没有插件提供 HIS 连接器 "sqlserver-junwei1"」,
        // 虽然也列出了可用 id,但没说明是改名,用户会以为是插件没装上。
        registrar.Register<IHisConnector>(HisConfig.LegacyConnector, _ =>
            throw new HisConnectorException(
                $"配置里的 connector=\"{HisConfig.LegacyConnector}\" 是改名前的旧值。" +
                $"请改为 \"{HisConfig.KnownConnector}\" —— 本连接器针对 HealthOne 系统," +
                "视图与 SQL 模板均无变化,改这一处即可。"));
    }

    /// <summary>按宿主传入的配置构造连接器。配置非法一律抛 HisConnectorException,由宿主降级为「HIS 不可用」。</summary>
    private static IHisConnector Build(PluginContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.ConfigPath))
            throw new HisConnectorException("HIS 插件需要配置文件路径,但宿主未提供");
        return new SqlServerHisConnector(HisConfig.Load(ctx.ConfigPath));
    }
}
