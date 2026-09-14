using DRGX.Engine;

namespace DRGX.Host;

/// <summary>直赋档类别:由 split.when 的条件类型决定。</summary>
internal enum DrgDirectKind
{
    /// <summary>机器人辅助手术(脚注18):判定见 DataPack.RobotProcedures。</summary>
    Robot,

    /// <summary>高危妊娠主诊断:判定见 split 内 mainDiagnosisIn 的码表。</summary>
    HighRisk,
}

/// <summary>直赋档规则:Label 为前端标题文案(高危妊娠直赋/机器人直赋);Probe 为请求期复核条件
/// (高危妊娠 = mainDiagnosisIn 原条件;机器人走 RobotProcedures,故为 null)。</summary>
internal sealed record DrgDirectRule(DrgDirectKind Kind, string Label, Condition? Probe);
