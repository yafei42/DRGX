using System.Collections.Frozen;

namespace DRGX.Engine;

/// <summary>
/// 条件求值器:对编译后的条件树在给定上下文中求值。
/// 语义契约见规范 §5;数值比较一律不变文化;缺失标量一律 false;
/// age ≥ 1 时日龄(ageDay)条件一律 false(一致性防护)。
/// 传入 <see cref="ConditionTrace"/> 收集器时,每个求值节点追加一条结构化轨迹
/// (短路顺序即轨迹顺序;组合节点在其子节点之后输出)。
/// </summary>
public static class ConditionEvaluator
{
    public static bool Evaluate(Condition condition, EvaluationContext ctx,
        List<ConditionTrace>? trace = null, string path = "")
    {
        return EvaluateNode(condition, ctx, trace, path);
    }

    private static bool EvaluateNode(Condition condition, EvaluationContext ctx,
        List<ConditionTrace>? trace, string path)
    {
        bool result;
        string? detail = null;
        // 容器节点的判定账:应满足 / 已满足 / **未参与判定**(短路跳过)。
        // 短路求值下"已发出的子行数 ≠ 应满足项数",消费端数行会得出对不上的分母
        // (且拿不到"没轮到"的语义),故由求值器就地记清。
        int? total = null, passed = null, unjudged = null;
        // 命中事实的编码与角色:结构化下发(ConditionTrace.Codes/Role),
        // 前端据此就地补中文名、区分主诊断/主手术,不必从 detail 文案正则抠码
        List<string>? codes = null;
        string? role = null;
        void NoteCode(string? c) { if (!string.IsNullOrEmpty(c)) (codes ??= []).Add(c!); }
        void NoteCodes(IEnumerable<string> cs) { foreach (var c in cs) NoteCode(c); }
        void NoteHit(string r, string? c) { role = r; NoteCode(c); }

        // 全部码表比较统一用 OrdinalIgnoreCase:词典键含规范小写 "x"(如 J42.x00),
        // 与 scheme.json 落位码表的大小写容错保持一致,避免用户输入大小写变体时漏判。
        switch (condition.Kind)
        {
            // ---- 逻辑组合:先求子节点(收集轨迹),后输出自身 ----
            case ConditionKind.True:
                result = true;
                break;

            case ConditionKind.All:
            {
                result = true;
                int ok = 0, judged = 0;
                for (int i = 0; i < condition.Children.Count; i++)
                {
                    judged++;
                    if (!EvaluateNode(condition.Children[i], ctx, trace, ChildPath(path, i)))
                    {
                        result = false;
                        break;
                    }
                    ok++;
                }
                total = condition.Children.Count;
                passed = ok;
                unjudged = total - judged;
                break;
            }

            case ConditionKind.Any:
            {
                result = false;
                int judged = 0;
                for (int i = 0; i < condition.Children.Count; i++)
                {
                    judged++;
                    if (EvaluateNode(condition.Children[i], ctx, trace, ChildPath(path, i)))
                    {
                        result = true;
                        break;
                    }
                }
                total = condition.Children.Count;
                passed = result ? 1 : 0;
                unjudged = total - judged;
                break;
            }

            case ConditionKind.Not:
                result = !EvaluateNode(condition.Children[0], ctx, trace, ChildPath(path, 0));
                break;

            // ---- 诊断维度 ----
            case ConditionKind.MainDiagnosisIn:
                result = condition.CodeSet.Contains(ctx.MainDiagnosis);
                if (result) { detail = $"命中主要诊断 {ctx.MainDiagnosis}"; NoteHit("mainDiagnosis", ctx.MainDiagnosis); }
                else detail = $"主要诊断 {ctx.MainDiagnosis} 不在码表内";
                break;

            case ConditionKind.AnyDiagnosisIn:
            {
                result = false;
                if (condition.CodeSet.Contains(ctx.MainDiagnosis))
                {
                    result = true;
                    detail = $"命中主要诊断 {ctx.MainDiagnosis}";
                    NoteHit("mainDiagnosis", ctx.MainDiagnosis);
                }
                else
                {
                    foreach (var d in ctx.OtherDiagnoses)
                    {
                        if (condition.CodeSet.Contains(d))
                        {
                            result = true;
                            detail = $"命中其他诊断 {d}";
                            NoteHit("otherDiagnosis", d);
                            break;
                        }
                    }
                }
                if (!result) detail = "码表内不含任何诊断";
                break;
            }

            case ConditionKind.OtherDiagnosisIn:
            {
                result = false;
                foreach (var d in ctx.OtherDiagnoses)
                {
                    if (condition.CodeSet.Contains(d))
                    {
                        result = true;
                        detail = $"命中其他诊断 {d}";
                        NoteHit("otherDiagnosis", d);
                        break;
                    }
                }
                if (!result) detail = ctx.OtherDiagnoses.Count == 0
                    ? "无其他诊断"
                    : $"其他诊断({ctx.OtherDiagnoses.Count}项)无命中";
                break;
            }

            case ConditionKind.AnyDiagnosisPrefix:
            {
                result = false;
                if (TryPrefix(ctx.MainDiagnosis, condition.Prefixes, out var hitPrefix))
                {
                    result = true;
                    detail = $"命中主要诊断 {ctx.MainDiagnosis} (前缀 {hitPrefix})";
                    NoteHit("mainDiagnosis", ctx.MainDiagnosis);
                }
                else
                {
                    foreach (var d in ctx.OtherDiagnoses)
                    {
                        if (TryPrefix(d, condition.Prefixes, out hitPrefix))
                        {
                            result = true;
                            detail = $"命中其他诊断 {d} (前缀 {hitPrefix})";
                            NoteHit("otherDiagnosis", d);
                            break;
                        }
                    }
                }
                if (!result) detail = "无诊断匹配前缀";
                break;
            }

            // ---- 操作维度 ----
            case ConditionKind.MainProcedureIn:
                result = ctx.MainProcedure is not null && condition.CodeSet.Contains(ctx.MainProcedure);
                if (result) { detail = $"命中主要手术 {ctx.MainProcedure}"; NoteHit("mainProcedure", ctx.MainProcedure); }
                else detail = ctx.MainProcedure is null
                    ? "无主要手术"
                    : $"主要手术 {ctx.MainProcedure} 不在码表内";
                break;

            case ConditionKind.AnyProcedureIn:
            {
                result = false;
                if (ctx.MainProcedure is not null && condition.CodeSet.Contains(ctx.MainProcedure))
                {
                    result = true;
                    detail = $"命中主要手术 {ctx.MainProcedure}";
                    NoteHit("mainProcedure", ctx.MainProcedure);
                }
                else
                {
                    foreach (var p in ctx.OtherProcedures)
                    {
                        if (condition.CodeSet.Contains(p))
                        {
                            result = true;
                            detail = $"命中其他操作 {p}";
                            NoteHit("otherProcedure", p);
                            break;
                        }
                    }
                }
                if (!result) detail = "码表内不含任何操作";
                break;
            }

            case ConditionKind.AnyOtherProcedureIn:
            {
                result = false;
                foreach (var p in ctx.OtherProcedures)
                {
                    if (condition.CodeSet.Contains(p))
                    {
                        result = true;
                        detail = $"命中其他操作 {p}";
                        NoteHit("otherProcedure", p);
                        break;
                    }
                }
                if (!result) detail = ctx.OtherProcedures.Count == 0
                    ? "无其他操作"
                    : $"其他操作({ctx.OtherProcedures.Count}项)无命中";
                break;
            }

            case ConditionKind.ProcedureCountGte:
            {
                int count = (ctx.MainProcedure is not null ? 1 : 0) + ctx.OtherProcedures.Count;
                result = count >= condition.Value;
                detail = $"执行了 {count} 项操作";
                break;
            }

            case ConditionKind.HasProcedure:
            {
                int count = (ctx.MainProcedure is not null ? 1 : 0) + ctx.OtherProcedures.Count;
                result = count > 0;
                detail = $"执行了 {count} 项操作";
                break;
            }

            case ConditionKind.NoValidMainProcedure:
                if (ctx.MainProcedure is null)
                {
                    result = true;
                    detail = "无主要手术";
                }
                else if (ctx.ValidProcedureSet.Contains(ctx.MainProcedure))
                {
                    result = false;
                    detail = $"主要手术 {ctx.MainProcedure} 为有效手术操作";
                    NoteHit("mainProcedure", ctx.MainProcedure);
                }
                else
                {
                    result = true;
                    detail = $"主要手术 {ctx.MainProcedure} 不是有效手术操作";
                    NoteHit("mainProcedure", ctx.MainProcedure);
                }
                break;

            case ConditionKind.HasValidProcedure:
            {
                result = false;
                if (ctx.MainProcedure is not null && ctx.ValidProcedureSet.Contains(ctx.MainProcedure))
                {
                    result = true;
                    detail = $"主要手术 {ctx.MainProcedure} 为有效手术操作";
                    NoteHit("mainProcedure", ctx.MainProcedure);
                }
                else
                {
                    foreach (var p in ctx.OtherProcedures)
                    {
                        if (ctx.ValidProcedureSet.Contains(p))
                        {
                            result = true;
                            detail = $"其他操作 {p} 为有效操作";
                            NoteHit("otherProcedure", p);
                            break;
                        }
                    }
                }
                if (!result) detail = "无有效操作";
                break;
            }

            // ---- 并发症维度 ----
            case ConditionKind.HasOtherDiagnosis:
                result = ctx.OtherDiagnoses.Count > 0;
                detail = $"存在 {ctx.OtherDiagnoses.Count} 项其他诊断";
                break;

            case ConditionKind.HasMcc:
                result = ctx.MajorComplications.Count > 0;
                detail = ctx.MajorComplications.Count > 0
                    ? $"存在 {ctx.MajorComplications.Count} 项 MCC：{string.Join(", ", ctx.MajorComplications)}"
                    : "存在 0 项严重并发症(MCC)";
                if (result) NoteCodes(ctx.MajorComplications);
                break;

            case ConditionKind.HasCc:
                result = ctx.MinorComplications.Count > 0;
                detail = ctx.MinorComplications.Count > 0
                    ? $"存在 {ctx.MinorComplications.Count} 项 CC：{string.Join(", ", ctx.MinorComplications)}"
                    : "存在 0 项并发症(CC)";
                if (result) NoteCodes(ctx.MinorComplications);
                break;

            case ConditionKind.HasCcOrMcc:
                result = ctx.MajorComplications.Count > 0 || ctx.MinorComplications.Count > 0;
                var mccCodes = ctx.MajorComplications.Count > 0 ? $"MCC {ctx.MajorComplications.Count} 项：{string.Join(", ", ctx.MajorComplications)}" : "MCC 0 项";
                var ccCodes = ctx.MinorComplications.Count > 0 ? $"CC {ctx.MinorComplications.Count} 项：{string.Join(", ", ctx.MinorComplications)}" : "CC 0 项";
                detail = $"{mccCodes} / {ccCodes}";
                NoteCodes(ctx.MajorComplications);
                NoteCodes(ctx.MinorComplications);
                break;

            // ---- 人口学维度 ----
            case ConditionKind.AgeGte:
            {
                result = ctx.Record.Age is int age && age >= condition.Value;
                detail = DescribeAge(ctx, condition.Value, ">=");
                break;
            }

            case ConditionKind.AgeGt:
            {
                result = ctx.Record.Age is int ageGt && ageGt > condition.Value;
                detail = DescribeAge(ctx, condition.Value, ">");
                break;
            }

            case ConditionKind.AgeLte:
            {
                result = ctx.Record.Age is int ageLte && ageLte <= condition.Value;
                detail = DescribeAge(ctx, condition.Value, "<=");
                break;
            }

            case ConditionKind.AgeEq:
            {
                result = ctx.Record.Age is int ageEq && ageEq == condition.Value;
                detail = DescribeAge(ctx, condition.Value, "=");
                break;
            }

            case ConditionKind.AgeLt:
            {
                result = ctx.Record.Age is int age2 && age2 < condition.Value;
                detail = DescribeAge(ctx, condition.Value, "<");
                break;
            }

            case ConditionKind.AgeNot:
            {
                result = ctx.Record.Age is int age3 && age3 != condition.Value;
                detail = DescribeAge(ctx, condition.Value, "≠");
                break;
            }

            case ConditionKind.AgeDayGte:
            {
                result = DayAgeUsable(ctx) && ctx.Record.AgeDay is int dayGte && dayGte >= condition.Value;
                detail = DescribeAgeDay(ctx, condition.Value, ">=");
                break;
            }

            case ConditionKind.AgeDayLt:
            {
                result = DayAgeUsable(ctx) && ctx.Record.AgeDay is int dayLt && dayLt < condition.Value;
                detail = DescribeAgeDay(ctx, condition.Value, "<");
                break;
            }

            case ConditionKind.AgeDayLte:
            {
                result = DayAgeUsable(ctx) && ctx.Record.AgeDay is int dayLte && dayLte <= condition.Value;
                detail = DescribeAgeDay(ctx, condition.Value, "<=");
                break;
            }

            case ConditionKind.AgeDayGt:
            {
                result = DayAgeUsable(ctx) && ctx.Record.AgeDay is int dayGt && dayGt > condition.Value;
                detail = DescribeAgeDay(ctx, condition.Value, ">");
                break;
            }

            case ConditionKind.WeightGte:
            {
                result = ctx.Record.Weight is int weightGte && weightGte >= condition.Value;
                detail = DescribeWeight(ctx, condition.Value, "≥");
                break;
            }

            case ConditionKind.WeightGt:
            {
                result = ctx.Record.Weight is int weightGt && weightGt > condition.Value;
                detail = DescribeWeight(ctx, condition.Value, ">");
                break;
            }

            case ConditionKind.WeightLte:
            {
                result = ctx.Record.Weight is int weightLte && weightLte <= condition.Value;
                detail = DescribeWeight(ctx, condition.Value, "≤");
                break;
            }

            case ConditionKind.WeightLt:
            {
                result = ctx.Record.Weight is int weight && weight < condition.Value;
                detail = DescribeWeight(ctx, condition.Value, "<");
                break;
            }

            case ConditionKind.GenderIs:
            {
                result = ctx.Record.Gender == condition.ExpectedValue;
                detail = $"实际性别 {ctx.Record.Gender ?? "缺失"}";
                break;
            }

            // ---- 交集基数（官方 DSL: length(S ∩ {字段}) >= n） ----
            case ConditionKind.ProcedureIntersectCountGte:
            {
                int n = CountDistinct(new[] { ctx.MainProcedure }.Concat(ctx.OtherProcedures), condition.CodeSet);
                result = n >= condition.Value;
                detail = $"{IntersectSetName(condition)}码表内有 {n} 项操作被执行，需≥{condition.Value:0.##} 项";
                break;
            }

            case ConditionKind.DiagnosisIntersectCountGte:
            {
                int n = CountDistinct(new[] { ctx.MainDiagnosis }.Concat(ctx.OtherDiagnoses), condition.CodeSet);
                result = n >= condition.Value;
                detail = $"{IntersectSetName(condition)}码表内有 {n} 项诊断，需≥{condition.Value:0.##} 项";
                break;
            }

            // ---- 特殊维度 ----
            case ConditionKind.SiteCountGte:
                (result, detail) = EvaluateSiteCount(condition, ctx);
                break;

            case ConditionKind.RobotAssist:
                result = ctx.RobotAssist;
                detail = ctx.RobotAssist ? "病例含机器人辅助手术(17.4x)" : "不含机器人辅助手术";
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(condition), condition.Kind, "未知条件原语");
        }

        trace?.Add(new ConditionTrace(path, Describe(condition), result, detail, codes, role,
            string.IsNullOrEmpty(condition.SetRef) ? null : condition.SetRef,
            condition.Kind.ToString(), total, passed, unjudged));
        return result;
    }

    // ---- 部位计数(含明细) ----
    private static (bool Result, string Detail) EvaluateSiteCount(Condition condition, EvaluationContext ctx)
    {
        if (ctx.Sites is null)
            return (false, "所在 MDC 无部位映射");

        var hitSites = new List<string>();
        foreach (var (site, codes) in ctx.Sites)
        {
            if (codes.Contains(ctx.MainDiagnosis))
            {
                hitSites.Add(site);
                continue;
            }
            foreach (var d in ctx.OtherDiagnoses)
            {
                if (codes.Contains(d))
                {
                    hitSites.Add(site);
                    break;
                }
            }
        }
        bool result = hitSites.Count >= condition.Value;
        string detail = hitSites.Count > 0
            ? $"命中 [{string.Join(", ", hitSites)}] 共 {hitSites.Count} 个部位"
            : "无部位命中";
        return (result, detail);
    }

    /// <summary>集合与病例编码列表的交集基数（按不同编码去重计数）。</summary>
    private static int CountDistinct(IEnumerable<string?> codes, IReadOnlySet<string> set)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            if (!string.IsNullOrEmpty(code) && set.Contains(code!))
                seen.Add(code!);
        }
        return seen.Count;
    }

    // ---- 描述与工具 ----
    // 措辞面向一线编码员:统一"主要诊断/主要手术"术语,逻辑节点用自然中文,不用"取反/恒真"等工程词
    // 码表条件带规模(共 N 个编码):一线看条件原文即可感知"这是个多大的池子",不用点开码表数
    // 本表是条件短语的单一真相源:轨迹(Expression)与树形描述(DescribeTree)共用,防止双实现漂移
    /// <summary>语义命名集合的展示名(OrdinalIgnoreCase)。官方 DSL 的集合编号(OP_ARB 等)
    /// 对一线用户是无意义的代号,但这几类集合是官方为特殊分组语义单独设的表,条件描述
    /// 直接点名(如「机器人辅助手术码表」「产科严重合并症或并发症码表」)。官方工作簿
    /// 「集合」表只有 集合编号/编码/名称/类型 四列,没有集合级显示名,此处按官方语义人工标注
    /// (语义均与官方工作簿对应表的行名核实过);未标注的集合在短语里带官方集合编号,
    /// 可与「数据一览 → 编码集合」对账。</summary>
    private static readonly FrozenDictionary<string, string> SetDisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OP_ARB"] = "机器人辅助手术", // 官方 OP_ARB:机器人辅助手术触发码(17.4100~17.4500,脚注18)
            ["DI_O00"] = "妊娠、分娩及产褥期", // 官方 DI_O00:MDCO 门控=MDCO 主要诊断分类全集(1331 码,含 O24/Z35/A34),勿标"高危妊娠"
            ["DI_OZ1"] = "妊娠期相关疾病",   // 官方 DI_OZ1:OZ1「妊娠期相关疾病」入组码表(1014 码)
            ["DI_OBD"] = "产科严重合并症或并发症", // 官方 DI_OBD:OB11/OB21「伴严重合并症或并发症」主诊断清单(直赋口径,793 码)
            ["OP_ALL"] = "全部手术操作",   // 官方 OP_ALL:全部有效手术操作;主形态走下方 OP_ALL 特判短句
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>码表条件标签:集合带语义名时点名(「主要诊断在产科严重合并症或并发症码表内」);
    /// 未标注集合带官方集合编号(「主要手术在码表 OP1_AA1 内」,与官方工作簿「集合」表对账);
    /// 仅当条件未保留集合引用(JSON 规则包/多集合并集)才退回「指定码表」。</summary>
    private static string CodeSetLabel(Condition c, string subject)
    {
        if (SetDisplayNames.TryGetValue(c.SetRef, out var name)) return $"{subject}在{name}码表内";
        return string.IsNullOrEmpty(c.SetRef) ? $"{subject}在指定码表内" : $"{subject}在码表 {c.SetRef} 内";
    }

    /// <summary>交集基数条件的码表定语:带语义名给「机器人辅助手术」,未标注给集合编号,无引用给「指定」。
    /// 调用方自行拼接「码表」后缀,此处不重复。</summary>
    private static string IntersectSetName(Condition c) =>
        SetDisplayNames.TryGetValue(c.SetRef, out var name) ? name
        : string.IsNullOrEmpty(c.SetRef) ? "指定" : c.SetRef;

    internal static string Describe(Condition c) => c.Kind switch
    {
        ConditionKind.True => "无附加条件",
        ConditionKind.All => $"同时满足以下 {c.Children.Count} 项条件",
        ConditionKind.Any => "满足以下任一条件",
        ConditionKind.Not => "以下条件均不满足",
        // OP_ALL(全部有效手术操作)的唯一用法「ZYSS in OP_ALL」语义就是"有有效主要手术",
        // 按「在码表内(共 N 万个编码)」显示全是噪音;否定式(not in)由 Not 包裹,读作
        // 「不满足(主要手术为有效手术操作)」,语义同样成立
        ConditionKind.MainProcedureIn when c.SetRef.Equals("OP_ALL", StringComparison.OrdinalIgnoreCase)
            => "主要手术为有效手术操作",
        ConditionKind.MainDiagnosisIn => DescribeCodes(c, CodeSetLabel(c, "主要诊断")),
        ConditionKind.AnyDiagnosisIn => DescribeCodes(c, CodeSetLabel(c, "任一诊断")),
        ConditionKind.OtherDiagnosisIn => DescribeCodes(c, CodeSetLabel(c, "其他诊断")),
        ConditionKind.AnyDiagnosisPrefix => "诊断符合指定前缀",
        ConditionKind.MainProcedureIn => DescribeCodes(c, CodeSetLabel(c, "主要手术")),
        ConditionKind.AnyProcedureIn => DescribeCodes(c, CodeSetLabel(c, "任一手术操作")),
        ConditionKind.AnyOtherProcedureIn => DescribeCodes(c, CodeSetLabel(c, "其他手术操作")),
        ConditionKind.ProcedureCountGte => $"手术操作数≥{c.Value:0.##}",
        ConditionKind.HasProcedure => "存在手术操作",
        ConditionKind.NoValidMainProcedure => "主要手术不是有效手术操作",
        ConditionKind.HasValidProcedure => "存在有效手术操作",
        ConditionKind.HasOtherDiagnosis => "存在其他诊断",
        ConditionKind.HasMcc => "存在严重并发症(MCC)",
        ConditionKind.HasCc => "存在并发症(CC)",
        ConditionKind.HasCcOrMcc => "存在并发症(MCC 或 CC)",
        ConditionKind.AgeGte => $"年龄≥{c.Value:0.##}岁",
        ConditionKind.AgeGt => $"年龄>{c.Value:0.##}岁",
        ConditionKind.AgeLte => $"年龄≤{c.Value:0.##}岁",
        ConditionKind.AgeEq => $"年龄={c.Value:0.##}岁",
        ConditionKind.AgeLt => $"年龄<{c.Value:0.##}岁",
        ConditionKind.AgeNot => $"年龄≠{c.Value:0.##}岁",
        ConditionKind.AgeDayGte => $"日龄≥{c.Value:0.##}天",
        ConditionKind.AgeDayLt => $"日龄<{c.Value:0.##}天",
        ConditionKind.AgeDayLte => $"日龄≤{c.Value:0.##}天",
        ConditionKind.AgeDayGt => $"日龄>{c.Value:0.##}天",
        ConditionKind.WeightGte => $"体重≥{c.Value:0.##}g",
        ConditionKind.WeightGt => $"体重>{c.Value:0.##}g",
        ConditionKind.WeightLte => $"体重≤{c.Value:0.##}g",
        ConditionKind.WeightLt => $"体重<{c.Value:0.##}g",
        ConditionKind.GenderIs => $"性别={c.ExpectedValue}",
        ConditionKind.ProcedureIntersectCountGte => DescribeCodes(c, $"{IntersectSetName(c)}码表内被执行的手术操作数≥{c.Value:0.##} 项"),
        ConditionKind.DiagnosisIntersectCountGte => DescribeCodes(c, $"{IntersectSetName(c)}码表内的诊断数≥{c.Value:0.##} 项"),
        ConditionKind.SiteCountGte => $"受累部位数≥{c.Value:0.##}",
        ConditionKind.RobotAssist => "机器人辅助手术",
        _ => c.Kind.ToString(),
    };

    /// <summary>整棵 when 树展开为单行自然语言:逻辑节点内联子条件(且/或),叶子短语复用 Describe。
    /// 树形场景(如 ADRG 入组理由兜底)用本方法,轨迹用单节点 Describe,短语同源不漂移。</summary>
    internal static string DescribeTree(Condition condition) => condition.Kind switch
    {
        ConditionKind.All => $"同时满足以下 {condition.Children.Count} 项条件({string.Join(" 且 ", condition.Children.Select(DescribeTree))})",
        ConditionKind.Any => $"满足以下任一条件({string.Join(" 或 ", condition.Children.Select(DescribeTree))})",
        ConditionKind.Not => $"不满足({DescribeTree(condition.Children[0])})",
        _ => Describe(condition),
    };

    private static string DescribeAge(EvaluationContext ctx, double threshold, string op) =>
        ctx.Record.Age is int age ? $"实际 {age} 岁,需 {op} {threshold:0.##}" : "年龄缺失";

    /// <summary>码表条件描述:基础短语 + 规模(共 N 个编码,N0 千分位),无码表时只给短语。</summary>
    private static string DescribeCodes(Condition c, string label) =>
        c.Codes.Count > 0 ? $"{label}（共 {c.Codes.Count:N0} 个编码）" : label;

    private static string DescribeWeight(EvaluationContext ctx, double threshold, string op) =>
        ctx.Record.Weight is int w ? $"实际 {w} g,需 {op} {threshold:0.##}" : "体重缺失";

    private static string DescribeAgeDay(EvaluationContext ctx, double threshold, string op)
    {
        if (!DayAgeUsable(ctx)) return "age ≥ 1,日龄条件忽略";
        return ctx.Record.AgeDay is int d
            ? $"实际 {d} 天,需 {op} {threshold:0.##}"
            : "日龄缺失";
    }

    /// <summary>
    /// age/ageDay 一致性防护:日龄条件仅在「年龄缺失或 0 岁」且「确实给出了日龄」时才有意义。
    ///
    /// <para>两道护栏缺一不可:前半段挡住"成年人误填 ageDay=0"的常见上报习惯;
    /// 后半段(<c>AgeDay is not null</c>)挡住"年龄未知 + 年龄明细里根本没有日级信息"的病例 ——
    /// 那种情况下 AgeDay 为 null,若放行就会被当作 0 天新生儿命中 ageDayLt 一类入组条件。</para>
    /// </summary>
    private static bool DayAgeUsable(EvaluationContext ctx) =>
        (ctx.Record.Age is not int age || age == 0) && ctx.Record.AgeDay is not null;

    private static bool TryPrefix(string code, IReadOnlyList<string> prefixes, out string prefix)
    {
        foreach (var p in prefixes)
        {
            if (code.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                prefix = p;
                return true;
            }
        }
        prefix = "";
        return false;
    }

    private static string ChildPath(string path, int index) =>
        path.Length == 0 ? $"of[{index}]" : $"{path}.of[{index}]";
}


