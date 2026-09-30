using DRGX.Engine;

namespace DRGX.Tests.Generation;

/// <summary>
/// 边界探针生成:把一条 witness 变成若干条"踩在边界上"的用例。
///
/// <para><b>为什么边界要单独造，而不是只留 witness。</b>witness 只证明"这个组能到达";
/// 它证明不了"这个组<b>只</b>在满足条件时到达"。把某个事实往阈值另一侧挪一格
/// (<c>ageGte(70)</c> 挪到 69、<c>ageDayLt(3)</c> 挪到 3、去掉那个 MCC、换个性别),落点是否
/// 就不再是本组 —— 这才是分组规则的边界语义。反过来,挪完之后<b>仍然</b>落在本组的,
/// 同样是有效信息:说明本组存在两条等价路径(例如"伴并发症"与"不伴并发症"两个档都命中),
/// 这类"看起来该变却没变"的用例恰恰是最容易被后续改动静默破坏的一类,所以一并冻结。</para>
///
/// <para><b>数值阈值两侧都要探。</b>只探外侧是不够的。阈值条件唯一的错法是 off-by-one
/// (该含等号的地方写成不含),而 off-by-one 有<b>两个</b>方向:<c>ageGte(70)</c> 写成
/// <c>age &gt; 70</c> 会让 <b>70</b> 掉出去 —— 这个方向只有把年龄正好推到 <b>70</b>
/// (阈值内侧的紧邻值)才测得出来;把 80 挪到 69 测的是"不满足时确实不落本组",完全测不到
/// 等号写错。因此每个数值叶子派两格:紧邻内侧(<c>boundary-hold</c> 预期)与紧邻外侧
/// (<c>boundary-violate</c> 预期)。</para>
///
/// <para><b>Not 子树按"违反"探,不按"反推"探。</b>反推 <c>Not(X)</c> 需要证明某事实不存在
/// (那是另一族判据,且容易构造出永远成立的空事实),但<b>边界探针不需要反推</b>——
/// witness 已经满足 <c>Not(X)</c>,要测的是"把 X 变成真,落点是否改变",这是构造性的、
/// 唯一的。官方数据里"不伴并发症"档几乎全是 <c>Not(hasMcc)</c> 形态,若跳过 Not 子树,
/// 每个 ADRG 就有一半的档位完全没有边界用例。</para>
///
/// <para><b>同 ADRG 的兄弟档也要探。</b>只看 witness 命中的那一档,会漏掉一整族"档位边界":
/// 官方把"不伴并发症"档写成 <c>when = 恒真</c> 的兜底,落在这档的病案条件树里一条并发症条件
/// 都没有,于是"加一个 MCC 就该掉到伴并发症档"这件事没有任何用例去测 —— 而这恰恰是
/// DRG 分档里改动最频繁、后果最直接(直接影响支付)的一类。兄弟档的条件当下为假,
/// 所以它们的变异方向与否定叶一致:把条件推成真。</para>
///
/// <para><b>每类事实只挪一格。</b>数值族取阈值两侧的<b>相邻整数</b>值(而不是随手 ±10),
/// 因为 off-by-one 是阈值条件唯一的错法;其余族只做"去掉/加入/翻转"一种最小改动,
/// 改得越多越说不清是哪一条条件在起作用。</para>
///
/// <para><b>选码必须排序后再取。</b>MCC/CC/诊断/操作候选来自 <c>FrozenDictionary</c>,
/// 枚举顺序不保证稳定。基线文件要提交进仓库,顺序漂了 diff 就变成整文件重写,故所有
/// 候选一律 <c>OrderBy(码, Ordinal)</c> 之后再取首个。</para>
/// </summary>
internal static class BoundaryMutator
{
    /// <summary>每个目标最多派生的边界探针数。数值族(含阈值两侧)优先,其次是
    /// 并发症的"加入/去掉",再次是手术与诊断 —— 前三族才是真正会被静默改坏的语义;
    /// 诊断族往往只反映"换了个主诊断当然换组"。
    ///
    /// <para>上限放宽到 18 是为了给"同 ADRG 兄弟档"那一族留位置:官方一个 ADRG 最多 7 档
    /// (如 BV1:伴 MCC 且≥70 岁 / 伴 MCC / 伴 CC 且≥70 岁 / 伴 CC / ≥70 岁 / 兜底),
    /// 每档 2~4 个叶子,再加上命中档与入口的探针,14 个名额会把入口那一族挤掉。</para>
    /// </summary>
    public const int MaxPerTarget = 18;

    /// <summary>
    /// 为一批 witness 派生边界探针。
    ///
    /// <para><b>门控级探针每个 MDC 只出一次。</b>MDC 门控是整条链共用的(例如
    /// <c>mainDiagnosisIn(DI_F00,2027 码)</c>),按 target 各派一份会得到几十份
    /// "把主诊断换成码表外的码 → 未入组"的近似重复用例 —— 门控码表一旦调整,十几条
    /// 测试同时变红,真正的失败点反而淹没在重复里。因此这类探针挂在每个 MDC 内
    /// <b>链序最靠前</b>的那个 ADRG 上,一个 MDC 一份。</para>
    ///
    /// <para>承载者的选择用链序下标而不是码序:链序决定"谁先判",码序只是个字符串顺序。</para>
    /// </summary>
    public static IEnumerable<TestCase> DeriveAll(IEnumerable<WitnessEvidence> evidence)
    {
        var list = evidence.ToList();
        var primaryByMdc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in list)
        {
            var index = ChainIndexOf(w.Mdc, w.Adrg);
            if (!primaryByMdc.TryGetValue(w.Mdc.Code, out var cur) || index < cur)
                primaryByMdc[w.Mdc.Code] = index;
        }

        foreach (var w in list)
        {
            var isPrimary = primaryByMdc[w.Mdc.Code] == ChainIndexOf(w.Mdc, w.Adrg);
            foreach (var c in Derive(w, isPrimary)) yield return c;
        }
    }

    /// <summary>ADRG 在 MDC 内 ADRG 链上的下标(即"判定顺序");找不到时给一个很大的值,
    /// 让它不可能被选为承载者。</summary>
    private static int ChainIndexOf(CompiledMdc mdc, CompiledAdrg adrg)
    {
        for (int i = 0; i < mdc.Adrgs.Count; i++)
            if (ReferenceEquals(mdc.Adrgs[i], adrg)) return i;
        for (int i = 0; i < mdc.Adrgs.Count; i++)
            if (string.Equals(mdc.Adrgs[i].Code, adrg.Code, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    public static IReadOnlyList<TestCase> Derive(WitnessEvidence w, bool emitGateLevelProbes)
    {
        var record = w.Record;
        var leaves = new List<Leaf>();
        Collect(w.Mdc.Gate, leaves, negated: false, gateLevel: true);
        foreach (var entry in w.Adrg.Entries) Collect(entry.When, leaves, negated: false, gateLevel: false);
        if (w.Split.When is not null) Collect(w.Split.When, leaves, negated: false, gateLevel: false);

        // 同 ADRG 的**其他档**(兄弟 split):witness 落在这档,说明那些档的条件当下为假,
        // 因此它们的变异方向与"否定叶"一致 —— 目标是把它们推成真。
        //
        // 这一族不能省。官方把"不伴并发症"档写成 `when = 恒真` 的兜底(而不是 not(hasCcOrMcc)),
        // 于是落在这档的 witness 条件树里一条并发症条件都没有;只看命中档的话,
        // 这些组就只剩"换主诊断"一条探针,而"加一个 MCC 就该掉到另一档"这个最核心的档位边界
        // 完全没有用例 —— 实测有 191 个组(几乎整个 B/C/D… 内科链)处在这个状态。
        foreach (var split in w.Adrg.Splits)
        {
            if (ReferenceEquals(split, w.Split) || split.When is null) continue;
            Collect(split.When, leaves, negated: true, gateLevel: false);
        }

        var produced = new List<TestCase>();
        var seenInputs = new HashSet<string>(StringComparer.Ordinal) { Signature(record) };
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var probe = BuildContext(w);

        foreach (var leaf in Ordered(leaves))
        {
            if (produced.Count >= MaxPerTarget) break;

            foreach (var (mutant, slug, note) in Mutate(leaf, record, probe, emitGateLevelProbes))
            {
                if (produced.Count >= MaxPerTarget) break;
                // 变异没改变任何输入(例如阈值外侧与 witness 恰好同值、或该族无候选取码)→ 没有信息量。
                var signature = Signature(mutant);
                if (!seenInputs.Add(signature)) continue;

                var outcome = OfficialPack.Engine.Group(mutant);

                // 落点仍然是本组 → 边界保持(存在等价路径);否则 → 边界失效。
                var landedOnTarget = outcome.Status == GroupStatus.Success
                                     && string.Equals(outcome.Code, w.Target, StringComparison.OrdinalIgnoreCase);
                var kind = landedOnTarget ? CaseKinds.BoundaryHold : CaseKinds.BoundaryViolate;

                var id = Id(w.Target, kind, slug, usedIds);
                produced.Add(new TestCase
                {
                    Id = id,
                    Kind = kind,
                    Target = w.Target,
                    Note = note,
                    Gender = mutant.Gender ?? "1",
                    Age = mutant.Age,
                    AgeDay = mutant.AgeDay,
                    Weight = mutant.Weight,
                    Diagnoses = mutant.Diagnoses,
                    Procedures = mutant.Procedures,
                    Expect = new ExpectedOutcome(outcome.Status, outcome.Code, outcome.Reason),
                });
            }
        }
        return produced;
    }

    private static string Id(string target, string kind, string slug, HashSet<string> used)
    {
        var prefix = kind == CaseKinds.BoundaryHold ? "bh" : "bv";
        var id = $"{prefix}:{target}:{slug}";
        if (used.Add(id)) return id;
        for (int i = 2; ; i++)
        {
            var candidate = $"{id}#{i}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string Signature(MedicalRecord r) => string.Join('|',
        r.Gender, r.Age, r.AgeDay, r.Weight,
        string.Join(',', r.Diagnoses), "//", string.Join(',', r.Procedures));

    // ---------------- 叶子收集与排序 ----------------

    private enum Family { Numeric, Gender, Complication, Procedure, Diagnosis }

    private static readonly IReadOnlySet<string> NoCodes =
        System.Collections.Frozen.FrozenSet<string>.Empty;

    private sealed record Leaf(Family Family, ConditionKind Kind, double Value,
        IReadOnlySet<string> Codes, string Text, bool Negated, bool GateLevel = false)
    {
        /// <summary>展示用文本:否定叶显式写成 <c>非(...)</c>,免得读基线时把
        /// "去掉并发症"与"加入并发症"看成同一件事。</summary>
        public string Display => Negated ? $"非({Text})" : Text;

        /// <summary>判定优先级(小的先探)。数值族的阈值语义最容易被改坏,放最前面;
        /// 同为并发症/手术/诊断时,"加入/置为"(否定叶的违反侧)比"去掉"更接近阈值语义。</summary>
        public int Priority => Family switch
        {
            Family.Numeric => 0,
            Family.Complication => Negated ? 1 : 2,
            Family.Procedure => Negated ? 3 : 4,
            Family.Diagnosis => Negated ? 5 : 6,
            _ => 7,
        };
    }

    private static IEnumerable<Leaf> Ordered(List<Leaf> leaves) =>
        leaves.Select((l, i) => (l, i))
            .OrderBy(x => x.l.Priority)
            .ThenBy(x => x.i)
            .Select(x => x.l);

    private static void Collect(Condition c, List<Leaf> leaves, bool negated, bool gateLevel)
    {
        if (c.Kind == ConditionKind.Not)
        {
            // 取反只翻转一次;Not(Not(X)) 在官方 DSL 里不出现,但语义上等价的写法要能正确塌缩。
            foreach (var ch in c.Children) Collect(ch, leaves, !negated, gateLevel);
            return;
        }

        var leaf = c.Kind switch
        {
            ConditionKind.AgeGte or ConditionKind.AgeGt or ConditionKind.AgeLte
                or ConditionKind.AgeLt or ConditionKind.AgeEq or ConditionKind.AgeNot
                or ConditionKind.AgeDayGte or ConditionKind.AgeDayGt
                or ConditionKind.AgeDayLte or ConditionKind.AgeDayLt
                or ConditionKind.WeightGte or ConditionKind.WeightGt
                or ConditionKind.WeightLte or ConditionKind.WeightLt
                => new Leaf(Family.Numeric, c.Kind, c.Value, NoCodes, Describe(c), negated, gateLevel),

            ConditionKind.GenderIs
                => new Leaf(Family.Gender, c.Kind, c.Value, NoCodes, Describe(c), negated, gateLevel),

            ConditionKind.HasMcc or ConditionKind.HasCc or ConditionKind.HasCcOrMcc
                => new Leaf(Family.Complication, c.Kind, c.Value, NoCodes, Describe(c), negated, gateLevel),

            ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn
                or ConditionKind.AnyOtherProcedureIn or ConditionKind.ProcedureIntersectCountGte
                => new Leaf(Family.Procedure, c.Kind, c.Value, c.CodeSet, Describe(c), negated, gateLevel),

            ConditionKind.MainDiagnosisIn or ConditionKind.OtherDiagnosisIn
                or ConditionKind.AnyDiagnosisIn or ConditionKind.DiagnosisIntersectCountGte
                => new Leaf(Family.Diagnosis, c.Kind, c.Value, c.CodeSet, Describe(c), negated, gateLevel),

            _ => null,
        };
        if (leaf is not null) leaves.Add(leaf);

        foreach (var ch in c.Children) Collect(ch, leaves, negated, gateLevel);
    }

    private static string Describe(Condition c) => c.Kind switch
    {
        ConditionKind.AgeGte => $"年龄≥{c.Value:0}",
        ConditionKind.AgeGt => $"年龄>{c.Value:0}",
        ConditionKind.AgeLte => $"年龄≤{c.Value:0}",
        ConditionKind.AgeLt => $"年龄<{c.Value:0}",
        ConditionKind.AgeEq => $"年龄={c.Value:0}",
        ConditionKind.AgeNot => $"年龄≠{c.Value:0}",
        ConditionKind.AgeDayGte => $"日龄≥{c.Value:0}",
        ConditionKind.AgeDayGt => $"日龄>{c.Value:0}",
        ConditionKind.AgeDayLte => $"日龄≤{c.Value:0}",
        ConditionKind.AgeDayLt => $"日龄<{c.Value:0}",
        ConditionKind.WeightGte => $"体重≥{c.Value:0}",
        ConditionKind.WeightGt => $"体重>{c.Value:0}",
        ConditionKind.WeightLte => $"体重≤{c.Value:0}",
        ConditionKind.WeightLt => $"体重<{c.Value:0}",
        ConditionKind.GenderIs => $"性别={c.ExpectedValue}",
        ConditionKind.HasMcc => "存在严重并发症(MCC)",
        ConditionKind.HasCc => "存在并发症(CC)",
        ConditionKind.HasCcOrMcc => "存在并发症(MCC 或 CC)",
        ConditionKind.HasOtherDiagnosis => "存在其他诊断",
        ConditionKind.OtherDiagnosisIn => "其他诊断命中码表",
        ConditionKind.AnyDiagnosisIn => "任一诊断命中码表",
        ConditionKind.MainDiagnosisIn => "主要诊断命中码表",
        ConditionKind.MainProcedureIn => "主手术命中码表",
        ConditionKind.AnyProcedureIn => "任一操作命中码表",
        ConditionKind.AnyOtherProcedureIn => "其他操作命中码表",
        ConditionKind.ProcedureIntersectCountGte => $"码表内操作数≥{c.Value:0}",
        ConditionKind.DiagnosisIntersectCountGte => $"码表内诊断数≥{c.Value:0}",
        _ => c.Kind.ToString(),
    };

    // ---------------- 变异 ----------------

    private static IEnumerable<(MedicalRecord Record, string Slug, string Note)> Mutate(
        Leaf leaf, MedicalRecord r, ProbeContext probe, bool emitGateLevelProbes)
    {
        switch (leaf.Family)
        {
            case Family.Numeric:
            {
                var field = FieldOf(leaf.Kind);
                foreach (var (value, side) in NumericProbes(leaf))
                {
                    var current = field switch
                    {
                        "age" => r.Age,
                        "ageDay" => r.AgeDay,
                        _ => r.Weight,
                    };
                    if (current is not null && value == current.Value) continue;

                    var mutant = field switch
                    {
                        // 日龄探针必须同时把年龄清空:引擎的 DayAgeUsable 护栏在 age ≥ 1 时
                        // 一律不认日龄条件,留着 witness 的年龄会让探针测不到日龄本身。
                        "ageDay" => r with { Age = null, AgeDay = value },
                        "weight" => r with { Weight = value },
                        _ => r with { Age = value },
                    };
                    yield return (mutant, $"{field}{SlugOf(leaf.Kind)}{side.Tag}{value}",
                        $"{leaf.Display}:{field} 取 {value}({side.Text}" +
                        (current is not null ? $";witness 为 {current})" : ")"));
                }
                break;
            }

            case Family.Gender:
            {
                var flip = r.Gender == "1" ? "2" : "1";
                yield return (r with { Gender = flip }, $"gender{flip}",
                    $"{leaf.Display}:性别 {r.Gender}→{flip}");
                break;
            }

            case Family.Complication:
            {
                if (!leaf.Negated)
                {
                    var drop = r.Diagnoses.Skip(1).Where(dx => IsComplication(dx, r.Diagnoses[0], leaf.Kind)).ToList();
                    if (drop.Count == 0) yield break;
                    var kept = r.Diagnoses.Where(dx => !drop.Contains(dx)).ToList();
                    yield return (r with { Diagnoses = kept }, $"dropcomp{drop.Count}",
                        $"{leaf.Display}:去掉并发症 {string.Join(",", drop)}");
                }
                else
                {
                    var add = PickComplication(leaf.Kind, r);
                    if (add is null) yield break;
                    yield return (r with { Diagnoses = [.. r.Diagnoses, add] }, "addcomp1",
                        $"{leaf.Display}:加入并发症 {add}");
                }
                break;
            }

            case Family.Procedure:
            {
                if (!leaf.Negated)
                {
                    // ① 去掉:证明"没有这个操作就不落本组"。
                    var drop = r.Procedures.Where(leaf.Codes.Contains).ToList();
                    if (drop.Count > 0)
                    {
                        var kept = r.Procedures.Where(p => !drop.Contains(p)).ToList();
                        yield return (r with { Procedures = kept }, $"dropproc{drop.Count}",
                            $"{leaf.Display}:去掉操作 {string.Join(",", drop)}");
                    }

                    // ② 换成兄弟 ADRG 的操作:证明"操作是否在本组码表内"确实被区分,
                    // 而不是"只要有个有效主手术就能落本组"。缺了这条,手术驱动组就只剩
                    // "去掉主手术"一种探针 —— 而"有手术但不是这个手术"才是真实世界里最常见的边界。
                    if (leaf.Kind is ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn
                        && r.Procedures.Count > 0)
                    {
                        var swap = PickAlternativeMainProcedure(leaf.Codes, r, probe);
                        if (swap is not null)
                            yield return (r with { Procedures = [swap, .. r.Procedures.Skip(1)] },
                                "swapmainproc1",
                                $"{leaf.Display}:把主手术换成码表外的 {swap}");
                    }
                }
                else
                {
                    var add = PickProcedure(leaf.Codes, r);
                    if (add is null) yield break;

                    // 让否定条件为真,必须按原语真正读取的位置放:
                    // mainProcedureIn 只认首项,anyOtherProcedureIn 只认非首项 —— 放错位置探针就测不到东西。
                    if (leaf.Kind == ConditionKind.MainProcedureIn)
                    {
                        yield return (r with { Procedures = [add, .. r.Procedures] }, "setmainproc1",
                            $"{leaf.Display}:把主手术置为 {add}");
                    }
                    else if ((leaf.Kind is ConditionKind.AnyOtherProcedureIn or ConditionKind.ProcedureIntersectCountGte)
                             && r.Procedures.Count == 0)
                    {
                        // 无操作时追加会变成"主手术",而这两个原语都不看主手术 —— 加了等于没加。
                        yield break;
                    }
                    else
                    {
                        yield return (r with { Procedures = [.. r.Procedures, add] }, "addproc1",
                            $"{leaf.Display}:追加操作 {add}");
                    }
                }
                break;
            }

            case Family.Diagnosis:
            {
                if (leaf.Kind == ConditionKind.MainDiagnosisIn)
                {
                    // 门控级的那一份只由本 MDC 的承载档出:见 DeriveAll 的说明。
                    if (leaf.GateLevel && !emitGateLevelProbes) yield break;

                    // 主诊断类要动的是首位,不是"其他诊断列表":非否定时换成码表外的码,
                    // 否定时换成码表内的码(让 Not 为假)。
                    var add = leaf.Negated
                        ? PickMainDiagnosis(leaf.Codes, r)
                        : PickAlternativeMainDiagnosis(leaf.Codes, r, probe);
                    if (add is null) yield break;
                    yield return (r with { Diagnoses = [add, .. r.Diagnoses.Skip(1)] },
                        leaf.Negated ? "setmaindx1" : "swapmaindx1",
                        leaf.Negated
                            ? $"{leaf.Display}:把主诊断置为 {add}"
                            : $"{leaf.Display}:把主诊断换成码表外的 {add}");
                    break;
                }

                if (!leaf.Negated)
                {
                    var drop = r.Diagnoses.Skip(1).Where(leaf.Codes.Contains).ToList();
                    if (drop.Count == 0) yield break;
                    var kept = r.Diagnoses.Where(dx => !drop.Contains(dx)).ToList();
                    yield return (r with { Diagnoses = kept }, $"dropdx{drop.Count}",
                        $"{leaf.Display}:去掉其他诊断 {string.Join(",", drop)}");
                }
                else
                {
                    var add = PickOtherDiagnosis(leaf.Codes, r);
                    if (add is null) yield break;
                    yield return (r with { Diagnoses = [.. r.Diagnoses, add] }, "adddx1",
                        $"{leaf.Display}:追加其他诊断 {add}");
                }
                break;
            }
        }
    }

    /// <summary>
    /// 主诊断类探针的候选池。纯诊断驱动的组(内科组为主)入口就是
    /// <c>mainDiagnosisIn(DI_XX)</c>,它们的条件树里<b>没有</b>可"去掉"的其他诊断、
    /// 也不含手术条件 —— 若不换主诊断,这些组一条边界用例都派不出来。
    ///
    /// <para>候选顺序刻意是"兄弟 ADRG 的主诊断码 → 本 MDC 门控码"而不是全字典随便挑一个:
    /// 换成一个兄弟组的码,期望落点就应该是那个兄弟组,这条断言直接检验
    /// <b>ADRG 之间的判别力</b>(码表是否互相覆盖、顺序是否让某个组永远拿不到);
    /// 换成字典里随便一个码只能证明"不在码表内就不落本组",信息量低得多。</para>
    /// </summary>
    private sealed record ProbeContext(
        IReadOnlyList<string> AlternativeMainDiagnoses,
        IReadOnlyList<string> AlternativeMainProcedures);

    private static ProbeContext BuildContext(WitnessEvidence w)
    {
        var dxs = new List<string>();
        var procs = new List<string>();
        void Walk(Condition c)
        {
            // anyDiagnosisIn 也把主诊断算在内,故同属"改主诊断即可命中"的候选。
            if (c.Kind is ConditionKind.MainDiagnosisIn or ConditionKind.AnyDiagnosisIn) dxs.AddRange(c.Codes);
            if (c.Kind is ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn) procs.AddRange(c.Codes);
            foreach (var ch in c.Children) Walk(ch);
        }

        foreach (var adrg in w.Mdc.Adrgs)
        {
            if (string.Equals(adrg.Code, w.Adrg.Code, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var entry in adrg.Entries) Walk(entry.When);
            foreach (var split in adrg.Splits) if (split.When is not null) Walk(split.When);
        }
        Walk(w.Mdc.Gate);

        var seenDx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenProc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new ProbeContext(
            dxs.Where(seenDx.Add).ToList(),
            procs.Where(seenProc.Add).ToList());
    }

    private readonly record struct Side(string Tag, string Text);
    private static readonly Side InnerSide = new("h", "阈值内侧,应仍满足");
    private static readonly Side OuterSide = new("v", "阈值外侧,应破坏条件");

    /// <summary>阈值两侧的紧邻整数值。含等号的比较(Gte/Lte)内侧就是阈值本身,外侧是阈值 ∓1;
    /// 不含等号的(Gt/Lt)恰好相反。<see cref="ConditionKind.AgeNot"/> 的内外两侧分别是
    /// "阈值 ±1"与"阈值本身"(等于阈值即破坏 ≠)。</summary>
    private static IEnumerable<(int Value, Side Side)> NumericProbes(Leaf leaf)
    {
        var threshold = (int)leaf.Value;
        var inner = SatisfyingValue(leaf.Kind, threshold);
        var outer = ViolatingValue(leaf.Kind, threshold);
        // 否定叶的"满足/破坏"与内层原语相反:Not(ageGte(70)) 在 69 处满足、在 70 处破坏。
        if (leaf.Negated) (inner, outer) = (outer, inner);
        yield return (inner, InnerSide);
        yield return (outer, OuterSide);
    }

    /// <summary>仍满足该原语的紧邻整数值。</summary>
    private static int SatisfyingValue(ConditionKind kind, int threshold) => kind switch
    {
        ConditionKind.AgeGte or ConditionKind.AgeDayGte or ConditionKind.WeightGte => threshold,
        ConditionKind.AgeGt or ConditionKind.AgeDayGt or ConditionKind.WeightGt => threshold + 1,
        ConditionKind.AgeLte or ConditionKind.AgeDayLte or ConditionKind.WeightLte => threshold,
        ConditionKind.AgeLt or ConditionKind.AgeDayLt or ConditionKind.WeightLt => threshold - 1,
        ConditionKind.AgeEq => threshold,
        ConditionKind.AgeNot => threshold == int.MaxValue ? threshold - 1 : threshold + 1,
        _ => threshold,
    };

    /// <summary>破坏该原语的紧邻整数值。</summary>
    private static int ViolatingValue(ConditionKind kind, int threshold) => kind switch
    {
        ConditionKind.AgeGte or ConditionKind.AgeDayGte or ConditionKind.WeightGte => threshold - 1,
        ConditionKind.AgeGt or ConditionKind.AgeDayGt or ConditionKind.WeightGt => threshold,
        ConditionKind.AgeLte or ConditionKind.AgeDayLte or ConditionKind.WeightLte => threshold + 1,
        ConditionKind.AgeLt or ConditionKind.AgeDayLt or ConditionKind.WeightLt => threshold,
        ConditionKind.AgeEq => threshold + 1,
        ConditionKind.AgeNot => threshold,
        _ => threshold,
    };

    private static string FieldOf(ConditionKind kind) => kind switch
    {
        ConditionKind.AgeDayGte or ConditionKind.AgeDayGt or ConditionKind.AgeDayLte or ConditionKind.AgeDayLt => "ageDay",
        ConditionKind.WeightGte or ConditionKind.WeightGt or ConditionKind.WeightLte or ConditionKind.WeightLt => "weight",
        _ => "age",
    };

    private static string SlugOf(ConditionKind kind) => kind switch
    {
        ConditionKind.AgeGte or ConditionKind.AgeDayGte or ConditionKind.WeightGte => "gte",
        ConditionKind.AgeGt or ConditionKind.AgeDayGt or ConditionKind.WeightGt => "gt",
        ConditionKind.AgeLte or ConditionKind.AgeDayLte or ConditionKind.WeightLte => "lte",
        ConditionKind.AgeLt or ConditionKind.AgeDayLt or ConditionKind.WeightLt => "lt",
        ConditionKind.AgeEq => "eq",
        _ => "ne",
    };

    // ---------------- 取码(一律先排序,保证基线可复现) ----------------

    private static bool IsComplication(string code, string mainDx, ConditionKind kind)
    {
        var pack = OfficialPack.Data;
        var group = pack.Exclusions.GetValueOrDefault(mainDx);
        bool InMcc = pack.Mcc.TryGetValue(code, out var mg) && mg != group;
        bool InCc = pack.Cc.TryGetValue(code, out var cg) && cg != group;
        return kind switch
        {
            ConditionKind.HasMcc => InMcc,
            ConditionKind.HasCc => InCc,
            _ => InMcc || InCc,
        };
    }

    /// <summary>挑一个"加入后确实会被算作该类并发症"的编码 —— 必须避开与主诊断同排除组的码,
    /// 否则引擎按排除表把它剔掉,探针测了个寂寞。</summary>
    private static string? PickComplication(ConditionKind kind, MedicalRecord r)
    {
        var pack = OfficialPack.Data;
        var group = pack.Exclusions.GetValueOrDefault(r.Diagnoses[0]);
        var present = r.Diagnoses.ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<KeyValuePair<string, string>> Tables() => kind switch
        {
            ConditionKind.HasMcc => pack.Mcc,
            ConditionKind.HasCc => pack.Cc,
            _ => pack.Mcc.Concat(pack.Cc),
        };

        return Tables()
            .Where(kv => kv.Value != group && !present.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string? PickProcedure(IReadOnlySet<string> codes, MedicalRecord r)
    {
        var present = r.Procedures.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return codes
            .Where(c => !present.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>把主手术换到本组码表之外:优先兄弟 ADRG 的手术码(期望落点是那个兄弟组,
    /// 直接检验手术驱动组之间的判别力),其次全量有效操作表。
    ///
    /// <para>候选一律限定在<b>有效操作表内</b>:换成清单外的操作会让病例从"手术病例"变成
    /// "内科病例",于是引擎改走内科组链路 —— 那样测到的是 <c>isSurgeryCase</c> 的分流,
    /// 不是本组手术码表的边界,两种语义混在一条用例里说不清。</para></summary>
    private static string? PickAlternativeMainProcedure(
        IReadOnlySet<string> codes, MedicalRecord r, ProbeContext probe)
    {
        bool Usable(string c) => !codes.Contains(c) && !r.Procedures.Contains(c, StringComparer.OrdinalIgnoreCase);

        var fromSiblings = probe.AlternativeMainProcedures
            .Where(c => Usable(c) && OfficialPack.Data.ValidProcedures.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
        if (fromSiblings is not null) return fromSiblings;

        return OfficialPack.Data.ValidProcedures
            .Where(Usable)
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string? PickOtherDiagnosis(IReadOnlySet<string> codes, MedicalRecord r)
    {
        var pack = OfficialPack.Data;
        var present = r.Diagnoses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return codes
            .Where(c => !present.Contains(c) && pack.DiagnosisNames.ContainsKey(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>换主诊断的候选必须"能被识别且参与分组" —— 落在官方第五章"不作为分组规则"清单里的码
    /// 会让引擎在进 MDC 之前就判 <see cref="UngroupedReason.MainDiagnosisNotGroupable"/>,
    /// 那样探针测到的是输入校验而非分组规则。否定叶用本组码表内的码(让 Not 为假)。</summary>
    private static string? PickMainDiagnosis(IReadOnlySet<string> codes, MedicalRecord r) =>
        codes.Where(c => IsUsableDiagnosisCode(c, r))
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>把主诊断换到本组码表之外:优先兄弟 ADRG 的主诊断码,其次本 MDC 门控码,
    /// 最后才退回全字典。全字典那一档只是保底(保证每个诊断驱动的组都有边界用例),
    /// 它证明的命题最弱,故排最后。</summary>
    private static string? PickAlternativeMainDiagnosis(
        IReadOnlySet<string> codes, MedicalRecord r, ProbeContext probe)
    {
        var fromSiblings = probe.AlternativeMainDiagnoses
            .Where(c => !codes.Contains(c) && IsUsableDiagnosisCode(c, r))
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
        if (fromSiblings is not null) return fromSiblings;

        return OfficialPack.Data.DiagnosisNames.Keys
            .Where(c => !codes.Contains(c) && IsUsableDiagnosisCode(c, r))
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsUsableDiagnosisCode(string code, MedicalRecord r)
    {
        var pack = OfficialPack.Data;
        // !DiagnosisMap.ContainsKey:排除需要 code-maps 改写才能落地的码。
        // 否则探针冻结出的期望值建立在"兼容展开"上(实测 N07.900x002「Dent病」被折成
        // N07.900x001「遗传性肾炎」落 LS25),测的是映射链而非分组规则。
        return !r.Diagnoses.Contains(code, StringComparer.OrdinalIgnoreCase)
               && pack.DiagnosisNames.ContainsKey(code)
               && !pack.NonPrincipalDiagnoses.Contains(code)
               && !pack.DiagnosisMap.ContainsKey(code);
    }
}
