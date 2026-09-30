namespace DRGX.Engine;

/// <summary>
/// DRG 细分档。**权威来源是命中的那条 split 的 when 条件**,不是 MCC/CC 的计数。
///
/// <para>为什么不能按计数推断:同一条落位路径下,「伴严重并发症」与「伴并发症」是两个
/// 不同的 split,而 split 的命中还受其他条件(手术/年龄/主诊断清单)影响。计数只反映
/// 病例事实,不反映规则选中了哪一档 —— 直赋档(0 MCC 0 CC)按计数会被误标「不伴并发症」。
/// 由 <see cref="SplitTraits.TierOf"/> 在 split 编译/读取时按条件类型派生。</para>
/// </summary>
public enum SeverityTier
{
    /// <summary>不伴并发症:when 里没有并发症原语(或只有被否定的并发症原语)。</summary>
    NoComplication = 0,
    /// <summary>伴并发症:when 含 hasCc。</summary>
    WithCc = 1,
    /// <summary>伴严重并发症:when 含 hasMcc 或 hasCcOrMcc。</summary>
    WithMcc = 2,
}

/// <summary>直赋档类别:由 split.when 的条件类型决定。</summary>
public enum DirectKind
{
    /// <summary>机器人直赋:手术操作命中机器人辅助手术触发码表(官方 OP_ARB)。
    /// 该档在两条轨上的写法不同,判据见 <see cref="SplitTraits.DirectRuleOf"/>。</summary>
    Robot,
    /// <summary>高危妊娠直赋:主诊断命中官方高危妊娠清单(811 清单)。</summary>
    HighRisk,
}

/// <summary>直赋档规则。<paramref name="Probe"/> 为请求期复核条件(高危妊娠的主诊断清单);
/// 机器人直赋的复核依据是 <see cref="EvaluationContext.RobotAssist"/>,故为 null。
/// 用 readonly record struct 承载:它由 split 的 when 每次派生,不希望在热路径上分配。</summary>
public readonly record struct DirectRule(DirectKind Kind, string Label, Condition? Probe);

/// <summary>
/// split 条件的两条派生:分档(<see cref="SeverityTier"/>)与直赋档(<see cref="DirectRule"/>)。
///
/// <para>这两条知识原先各自活在消费端:分档在前端按 MCC/CC 计数重推
/// (<c>wwwroot/js/evidence.js</c>),直赋档在宿主侧按 DRG 码建映射再复核
/// (<c>Host/Pack/PackIndexes.cs</c> 的 BuildDrgDirect + <c>Host/WebApp.cs</c> 的
/// ResolveDrgDirect)。二者都是对 split.when 结构的重复解读,且都要额外下发一个字段
/// (drgDirect)才能纠正推断错误。收进引擎后,消费端只读结果。</para>
///
/// <para>两者的判据都只认**正向**分支:<see cref="ConditionKind.Not"/> 子树不参与派生 ——
/// <c>not(hasMcc)</c> 表达的是"不伴严重并发症",按字面取 hasMcc 会取反。</para>
/// </summary>
public static class SplitTraits
{
    /// <summary>
    /// 机器人辅助手术触发码的官方集合编号。官方 3.0 工作簿把「机器人直赋」写成集合成员判断
    /// <c>{ZYSS,QTSS} in OP_ARB</c>(而非旧包的 <c>robotAssist</c> 原语),编译后是
    /// <c>SetRef == OP_ARB</c> 的操作原语。<c>SetRef</c> 由 DSL 编译器在「单一集合引用」时
    /// 刻意保留,正是为这种按官方集合编号点名语义的场景(见 <see cref="Condition.SetRef"/>);
    /// 本机官方包 9 条机器人档全靠它识别(EB10/GB40/IB40/IC30/IE20/LA20/MB10/NA10/NC10),
    /// 而 <c>robotAssist</c> 原语在官方包里一次都没用到。
    /// </summary>
    public const string RobotSetRef = "OP_ARB";

    /// <summary>按 when 派生分档。多个并发症原语并存时取**最强**的一档
    /// (any[hasMcc, hasCc] 视为伴严重并发症 —— 与"any 首中即优先"的落位语义一致)。</summary>
    public static SeverityTier TierOf(Condition? when)
    {
        var tier = SeverityTier.NoComplication;
        Walk(when);
        return tier;

        void Walk(Condition? c)
        {
            if (c is null) return;
            switch (c.Kind)
            {
                case ConditionKind.HasMcc:
                case ConditionKind.HasCcOrMcc:
                    if (tier < SeverityTier.WithMcc) tier = SeverityTier.WithMcc;
                    return;
                case ConditionKind.HasCc:
                    if (tier < SeverityTier.WithCc) tier = SeverityTier.WithCc;
                    return;
                case ConditionKind.Not:
                    return;   // 否定分支不参与分档派生
                case ConditionKind.All:
                case ConditionKind.Any:
                    foreach (var child in c.Children) Walk(child);
                    return;
                default:
                    return;
            }
        }
    }

    /// <summary>分档的中文文案(与 <c>GroupStatus</c>/<c>StatusText</c> 同口径:
    /// 机读名与人类可读文案一并下发,前端不另写一份映射)。</summary>
    public static string Label(SeverityTier tier) => tier switch
    {
        SeverityTier.WithMcc => "伴严重并发症",
        SeverityTier.WithCc => "伴并发症",
        _ => "不伴并发症",
    };

    /// <summary>按 when 派生直赋规则。两条轨的机器人直赋写法不同,都要认:
    /// JSON 规则包用 <c>robotAssist</c> 原语;官方 3.0 工作簿用集合成员判断
    /// <c>{ZYSS,QTSS} in OP_ARB</c>(编译后 <c>SetRef == <see cref="RobotSetRef"/></c>)。
    /// 只认前者会让官方轨的 9 条机器人档全部漏标 —— 那正是"按 MCC/CC 计数推断分档"
    /// 误报的地方。<c>mainDiagnosisIn</c> → 高危妊娠直赋(带原条件供请求期复核)。
    /// 沿 any/all 下钻取**首个**直赋条件 —— any 首中语义下即规则里的优先分支。</summary>
    public static DirectRule? DirectRuleOf(Condition? when)
    {
        if (when is null) return null;
        switch (when.Kind)
        {
            case ConditionKind.RobotAssist:
                return new DirectRule(DirectKind.Robot, "机器人直赋", null);

            // 官方 3.0 的写法:操作命中官方 OP_ARB 集合(SetRef 由编译器保留)。
            case ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn or ConditionKind.AnyOtherProcedureIn
                when when.SetRef == RobotSetRef:
                return new DirectRule(DirectKind.Robot, "机器人直赋", null);

            case ConditionKind.MainDiagnosisIn:
                return new DirectRule(DirectKind.HighRisk, "高危妊娠直赋", when);

            case ConditionKind.Any:
            case ConditionKind.All:
                foreach (var child in when.Children)
                {
                    var found = DirectRuleOf(child);
                    if (found is not null) return found;
                }
                return null;
            default: return null;
        }
    }

    /// <summary>请求期复核:落位 DRG 属直赋档、且该病例**确实命中**其特殊身份条件时返回说明,否则 null。
    ///
    /// <para>必须复核而非按落位码静态下发:同一 DRG 常有多条落位路径(纯 MCC 也能落 OB11/OB21),
    /// 静态下发会把常规分档误标成直赋。判定只认规则条件,与是否同时有 MCC 无关 ——
    /// 官方口径:"主诊断在 811 清单内时直接定 OB11/OB21,不再核对合并症"。</para>
    /// </summary>
    public static string? ResolveDirect(in DirectRule rule, EvaluationContext ctx) => rule.Kind switch
    {
        DirectKind.Robot => ctx.RobotAssist ? rule.Label : null,
        DirectKind.HighRisk =>
            rule.Probe is not null && rule.Probe.CodeSet.Contains(ctx.MainDiagnosis) ? rule.Label : null,
        _ => null,
    };
}
