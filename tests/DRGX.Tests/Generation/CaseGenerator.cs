using System.Collections.Frozen;
using DRGX.Engine;

namespace DRGX.Tests.Generation;

/// <summary>一条经引擎验证过的见证病案,连同它命中的规则位置(边界探针要靠这份位置信息)。</summary>
internal sealed record WitnessEvidence(
    CompiledMdc Mdc,
    CompiledAdrg Adrg,
    CompiledSplit Split,
    MedicalRecord Record,
    GroupOutcome Outcome,
    string Strategy)
{
    public string Target => Split.Code;
}

/// <summary>
/// witness 生成器:从规则树反推出"每个 DRG 落位规则都有病案能落进去"的**证据**。
///
/// <para>它不是手写用例的替代品,而是把"825 个组都要有测试"这件事从"人肉穷举"变成"可重放"。
/// 生成过程有三道闸:① 条件的机械反推(<see cref="RuleInversion"/>)给出候选病案;
/// ② 真实引擎作 oracle,落点不等于目标码即视为失败;③ 失败时先做**有界缩减**
/// (去掉并发症/手术/多诊断,专治"被同 ADRG 更靠前的兄弟组截获"),再退到枚举式反搜
/// (<see cref="SearchWitness"/>)。三条路都走不通才记入 <c>unreachable.txt</c>,
/// 且必须带上"落点其实是哪"这种可核对的原因。</para>
///
/// <para>引擎是唯一事实源:这里不实现任何分组判定,只构造输入、读回结果。</para>
/// </summary>
internal static class CaseGenerator
{
    /// <summary>单个目标最多尝试的引擎探针次数(扫描是穷举,必须有界)。</summary>
    private const int MaxProbesPerTarget = 9000;

    public static (List<WitnessEvidence> Witnesses, List<(string Code, string Reason)> Unreachable) GenerateWitnesses()
    {
        var baseCtx = BaseContext();
        var witnesses = new List<WitnessEvidence>();
        var unreachable = new List<(string Code, string Reason)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mdc in baseCtx.Pack.MdcChain)
        {
            var ctx = baseCtx.WithSites(mdc.Sites);
            for (int ai = 0; ai < mdc.Adrgs.Count; ai++)
            {
                var adrg = mdc.Adrgs[ai];
                var gateProcCodes = CollectProcCodes(mdc.Gate);
                var mainDx = FindMainDxCode(mdc, adrg, ctx.Pack, ctx.Contested) ?? ctx.FallbackMainDiagnosis;

                // 手术驱动组:主操作必须落在本 MDC 的门控码集里,否则链式首中会落到更早的 MDC。
                string? routingProc = null;
                if (adrg.ProcedureDriven && gateProcCodes.Count > 0)
                    routingProc = PickRoutingProc(gateProcCodes, mdc.Code, mainDx) ?? gateProcCodes.FirstOrDefault();

                for (int si = 0; si < adrg.Splits.Count; si++)
                {
                    var split = adrg.Splits[si];
                    if (!seen.Add(split.Code)) continue; // 同一码只留首条落位规则
                    ctx.Probes = 0;
                    ctx.Contested = ContestedCodes(mdc, adrg, si);

                    var draft = new Draft { MainDx = mainDx, MainProc = routingProc, Gender = DefaultGender(mdc) };
                    var evidence = Build(mdc, adrg, split, draft, ctx)
                                   ?? TryReach(mdc, adrg, split, draft, ctx)
                                   ?? Search(mdc, adrg, split, draft, ctx);
                    if (evidence is not null) witnesses.Add(evidence);
                    else unreachable.Add((split.Code, DescribeFailure(mdc, adrg, split, ctx)));
                }
            }
        }

        // 全局兜底行 0000 不是"组":引擎刻意把它判为未入组(无名称、MDC/ADRG 均为占位),
        // 因此它既不需要 witness,也不该出现在不可达清单里。这里显式记一笔,免得下一个人
        // 看到"850 条 split 只有 849 条有 witness"时以为漏了。
        unreachable.RemoveAll(u => u.Code.Equals(OfficialPack.GlobalFallbackCode, StringComparison.OrdinalIgnoreCase));

        return (witnesses, unreachable);
    }

    /// <summary>21 个 QY 歧义组的见证:手术病例 + 主手术与该 MDC 的任何 ADRG 都不匹配。</summary>
    public static List<WitnessEvidence> GenerateAmbiguous()
    {
        var baseCtx = BaseContext();
        var result = new List<WitnessEvidence>();

        foreach (var (mdcCode, qyWhen) in baseCtx.Pack.QyRules.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var mdc = baseCtx.Pack.MdcChain.FirstOrDefault(m => m.Code.Equals(mdcCode, StringComparison.OrdinalIgnoreCase));
            if (mdc is null) continue;

            var ctx = baseCtx.WithSites(mdc.Sites);
            if (TryAmbiguous(mdc, qyWhen, ctx, out var record, out var outcome))
            {
                // QY 不是细分组,没有对应的 CompiledSplit;用 QY 规则自身充当"位置"信息。
                result.Add(new WitnessEvidence(mdc, mdc.Adrgs[0],
                    new CompiledSplit($"{mdcCode}QY", qyWhen, $"歧义病案({mdcCode})"), record, outcome, "qy"));
            }
        }
        return result;
    }

    private static bool TryAmbiguous(CompiledMdc mdc, Condition qyWhen, GeneratorContext ctx,
        out MedicalRecord record, out GroupOutcome outcome)
    {
        record = null!;
        outcome = null!;
        var target = $"{mdc.Code}QY";

        // 主诊断候选:门控里的 mainDiagnosisIn 码集;门控不含主诊断(如 MDCA 的 always 门控、
        // MDCP 的年龄门控)时退回"任一可作主诊断的码"。
        var dxSets = new List<IReadOnlySet<string>>();
        CollectCodeSets(mdc.Gate, ConditionKind.MainDiagnosisIn, dxSets);
        var dxCandidates = dxSets.SelectMany(s => s)
            .Where(c => ctx.Pack.DiagnosisNames.ContainsKey(c) && !ctx.Pack.NonPrincipalDiagnoses.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8).ToList();
        if (dxCandidates.Count == 0) dxCandidates.Add(ctx.FallbackMainDiagnosis);

        // 主操作候选:有效操作里那些**不属于本 MDC 任何 ADRG 码集**的,这样手术驱动组全都不命中。
        var adrgProc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in mdc.Adrgs) foreach (var c in CollectProcCodes(a)) adrgProc.Add(c);
        var procCandidates = ctx.ValidProcedures.Where(p => !adrgProc.Contains(p)).Take(600).ToList();
        if (procCandidates.Count == 0) procCandidates = ctx.ValidProcedures.Take(600).ToList();

        foreach (var dx in dxCandidates)
            foreach (var proc in procCandidates)
            {
                var draft = new Draft { MainDx = dx, MainProc = proc, Gender = DefaultGender(mdc) };
                if (!RuleInversion.Satisfy(mdc.Gate, draft, ctx)) continue;
                if (!RuleInversion.Satisfy(qyWhen, draft, ctx)) continue;
                draft.MainProc = proc;
                draft.Procs.Clear();
                draft.Procs.Add(proc);

                var rec = draft.ToRecord($"qy-{mdc.Code}");
                var o = OfficialPack.Engine.Group(rec);
                if (o.Status == GroupStatus.Ambiguous && string.Equals(o.Code, target, StringComparison.OrdinalIgnoreCase))
                {
                    record = rec; outcome = o;
                    return true;
                }
            }
        return false;
    }

    // ---------------- 单目标:三条路依次尝试 ----------------

    private static WitnessEvidence? Build(CompiledMdc mdc, CompiledAdrg adrg, CompiledSplit split,
        Draft draft, GeneratorContext ctx)
    {
        var d = draft.Clone();
        if (!RuleInversion.Satisfy(mdc.Gate, d, ctx)) return null;

        var entryOk = false;
        foreach (var entry in adrg.Entries)
        {
            var cand = d.Clone();
            if (RuleInversion.Satisfy(entry.When, cand, ctx)) { d.CopyFrom(cand); entryOk = true; break; }
        }
        if (!entryOk) return null;

        if (split.When is not null && !RuleInversion.Satisfy(split.When, d, ctx)) return null;

        return Probe(mdc, adrg, split, d.ToRecord($"gen-{split.Code}"), ctx, "inverse");
    }

    /// <summary>有界缩减:反推出的候选病案可能"过满足"——多出来的并发症/手术让它先被
    /// 同 ADRG 更靠前的兄弟组截获。按"去掉哪些事实"枚举若干变体,取首个真正落到目标码的。</summary>
    private static WitnessEvidence? TryReach(CompiledMdc mdc, CompiledAdrg adrg, CompiledSplit split,
        Draft draft, GeneratorContext ctx)
    {
        var baseRecord = draft.ToRecord($"reach-{split.Code}");
        if (baseRecord.Diagnoses.Count == 0) return null;

        var others = baseRecord.Diagnoses.Skip(1).ToList();
        var compVariants = new List<IReadOnlyList<string>> { others, Array.Empty<string>() };
        foreach (var dx in others) compVariants.Add(new[] { dx });

        var procVariants = new List<IReadOnlyList<string>> { baseRecord.Procedures, Array.Empty<string>() };
        foreach (var p in baseRecord.Procedures) procVariants.Add(new[] { p });

        foreach (var comps in compVariants)
            foreach (var procs in procVariants)
            {
                var rec = baseRecord with { Diagnoses = [baseRecord.Diagnoses[0], .. comps], Procedures = procs };
                var hit = Probe(mdc, adrg, split, rec, ctx, "reduce");
                if (hit is not null) return hit;
            }
        return null;
    }

    /// <summary>
    /// 落点错配后的分阶段扫描。<b>阶段顺序就是优先级</b>:先把最省事、最能命中的一类扫完,
    /// 再逐步加事实。每一阶段都用引擎当 oracle,落点等于目标码即停。
    ///
    /// <list type="number">
    /// <item>① 只换主诊断 —— 覆盖"入口没有额外条件,只是被更靠前的核心组/兄弟档抢走"这一大类。
    /// 官方数据里每个 MDC 那个 <c>X000</c> 兜底组全靠它。</item>
    /// <item>② 主诊断 × 另一个"其他诊断"(取自本组条件里的 <c>otherDiagnosisIn</c>/<c>anyDiagnosisIn</c>
    /// 码表)—— 覆盖 <c>Any(其他诊断∈A | 主诊断且其他诊断∈B)</c> 这类"两条并列路径"的组:
    /// 走 A 那条会被更靠前的组抢走时,B 那条才是真正能落进来的。</item>
    /// <item>③ 主诊断 × 一个并发症 —— 覆盖"伴/不伴并发症"两档里靠并发症区分的那一档。</item>
    /// <item>④ 主诊断 × 手术形状(无手术、单操作、跨码表双操作) —— 覆盖需要主操作、或需要
    /// 主操作 + 其他操作的组。</item>
    /// <item>⑤ ④ × 一个并发症。</item>
    /// </list>
    ///
    /// <para><b>候选码按"是否被先判的规则吃掉"排序。</b>这是关键一步:同一个码若出现在更靠前的
    /// 核心组或同一核心组更靠前的档里,候选病案必然被它们截获,扫它只是浪费预算。把"先判规则
    /// 引用过的码"排到后面,前若干个候选就几乎总能落在目标组上 —— 否则像 MDCO 那样门控有 1331 个码、
    /// 其中 793 个归 OB11 的情况,按原顺序取前 60 个会全军覆没。</para>
    ///
    /// <para>年龄/日龄/体重/性别不在这里变:它们已由反推阶段按条件写好(见 <see cref="Build"/>)。
    /// 在这里乱改只会让候选病案偏离条件树,而这类事实的正确取值本来就只有条件本身知道。</para>
    /// </summary>
    private static WitnessEvidence? Search(CompiledMdc mdc, CompiledAdrg adrg, CompiledSplit split,
        Draft draft, GeneratorContext ctx)
    {
        var dxCandidates = OrderByPriority(DiagnosisCandidates(mdc, adrg, split, ctx), ctx.Contested);
        if (dxCandidates.Count == 0) return null;

        var shapes = ProcedureShapes(mdc, adrg, split, ctx);
        var otherCodes = OtherDiagnosisCandidates(mdc, adrg, split, ctx);
        var needsProc = RequiresProcedure(mdc.Gate) || RequiresProcedure(adrg.Entries[0].When)
                        || (split.When is not null && RequiresProcedure(split.When));
        var genders = DefaultGender(mdc) == "2" ? new[] { "2", "1" } : new[] { "1", "2" };

        foreach (var gender in genders)
        {
            var seed = draft.Clone();
            seed.Gender = gender;
            var others = seed.OtherDx.ToList();
            // 反推已按条件写好的手术形状(需要手术时它就是那条正解),其余形状交给 ④ 兜。
            var baseProcs = needsProc && seed.MainProc is not null
                ? (IReadOnlyList<string>)new[] { seed.MainProc }
                : Array.Empty<string>();

            // ① 主诊断扫描
            foreach (var dx in dxCandidates)
            {
                var hit = Probe(mdc, adrg, split, Record(seed, dx, others, baseProcs), ctx, "sweep-dx");
                if (hit is not null) return hit;
            }

            // ② 主诊断 × 另一个"其他诊断"
            foreach (var dx in dxCandidates.Take(200))
                foreach (var other in otherCodes)
                {
                    var hit = Probe(mdc, adrg, split, Record(seed, dx, [other], baseProcs), ctx, "sweep-dx-other");
                    if (hit is not null) return hit;
                }

            // ③ 主诊断 × 一个并发症
            foreach (var dx in dxCandidates.Take(200))
                foreach (var comp in ComplicationCandidates(dx, ctx).Take(6))
                {
                    var hit = Probe(mdc, adrg, split, Record(seed, dx, [comp], baseProcs), ctx, "sweep-dx-comp");
                    if (hit is not null) return hit;
                }

            // ④ 主诊断 × 手术形状
            foreach (var dx in dxCandidates.Take(80))
                foreach (var procs in shapes)
                {
                    var hit = Probe(mdc, adrg, split, Record(seed, dx, others, procs), ctx, "sweep-dx-proc");
                    if (hit is not null) return hit;
                }

            // ⑤ 主诊断 × 手术形状 × 并发症
            foreach (var dx in dxCandidates.Take(60))
                foreach (var procs in shapes)
                foreach (var comp in ComplicationCandidates(dx, ctx).Take(4))
                {
                    var hit = Probe(mdc, adrg, split, Record(seed, dx, [comp], procs), ctx, "sweep-dx-proc-comp");
                    if (hit is not null) return hit;
                }
        }
        return null;

        static MedicalRecord Record(Draft seed, string dx, IReadOnlyList<string> others, IReadOnlyList<string> procs) => new()
        {
            Index = "sweep",
            Gender = seed.Gender ?? "1",
            Age = seed.Age,
            AgeDay = seed.AgeDay,
            Weight = seed.Weight,
            Diagnoses = [dx, .. others],
            Procedures = procs,
        };
    }

    /// <summary>本组条件里 <c>otherDiagnosisIn</c>/<c>anyDiagnosisIn</c> 码表的候选码
    /// (同样把被先判规则引用过的码排后),用于尝试"另一条并列路径"。</summary>
    private static List<string> OtherDiagnosisCandidates(CompiledMdc mdc, CompiledAdrg adrg, CompiledSplit split,
        GeneratorContext ctx)
    {
        var sets = new List<IReadOnlySet<string>>();
        foreach (var kind in new[] { ConditionKind.OtherDiagnosisIn, ConditionKind.AnyDiagnosisIn })
        {
            CollectCodeSets(mdc.Gate, kind, sets);
            foreach (var e in adrg.Entries) CollectCodeSets(e.When, kind, sets);
            if (split.When is not null) CollectCodeSets(split.When, kind, sets);
        }

        return OrderByPriority(sets.SelectMany(s => s)
                .Where(c => ctx.Pack.DiagnosisNames.ContainsKey(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(), ctx.Contested)
            .Take(6).ToList();
    }

    /// <summary>被先判规则引用过的码:同 MDC 中排在本核心组之前的核心组(入口 + 全部档),
    /// 以及本核心组更靠前的档。反推与扫描都据此把"注定被截获"的码排到后面。</summary>
    private static IReadOnlySet<string> ContestedCodes(CompiledMdc mdc, CompiledAdrg adrg, int splitIndex)
    {
        var contested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in mdc.Adrgs)
        {
            if (ReferenceEquals(other, adrg)) break;
            foreach (var e in other.Entries) AddPositiveCodes(e.When, contested);
            foreach (var s in other.Splits) if (s.When is not null) AddPositiveCodes(s.When, contested);
        }
        for (int i = 0; i < splitIndex; i++)
            if (adrg.Splits[i].When is not null) AddPositiveCodes(adrg.Splits[i].When, contested);
        return contested;
    }

    private static List<string> OrderByPriority(List<string> candidates, IReadOnlySet<string> contested) =>
        candidates.OrderBy(c => contested.Contains(c) ? 1 : 0).ToList();

    /// <summary>条件树里出现过的编码(不进 <c>Not</c> 子树:否定式引用的码不是"会吃掉候选"的码)。</summary>
    private static void AddPositiveCodes(Condition? c, HashSet<string> into)
    {
        if (c is null || c.Kind == ConditionKind.Not) return;
        foreach (var code in c.Codes) into.Add(code);
        foreach (var child in c.Children) AddPositiveCodes(child, into);
    }

    /// <summary>该条件树是否**必然**要求存在手术操作(用于跳过"无手术"那一族探针)。
    /// <c>All</c>:任一子项要求 ⇒ 要求;<c>Any</c>:所有子项都要求 ⇒ 要求(存在不用手术的分支就还能走);
    /// <c>Not</c>:不表态(它的语义是"不要某操作",不构成"要有操作")。</summary>
    private static bool RequiresProcedure(Condition c) => c.Kind switch
    {
        ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn or ConditionKind.AnyOtherProcedureIn
            or ConditionKind.HasProcedure or ConditionKind.HasValidProcedure
            or ConditionKind.ProcedureIntersectCountGte or ConditionKind.RobotAssist
            or ConditionKind.NoValidMainProcedure => true,
        ConditionKind.All => c.Children.Any(RequiresProcedure),
        ConditionKind.Any => c.Children.Count > 0 && c.Children.All(RequiresProcedure),
        ConditionKind.Not => false,
        _ => false,
    };

    /// <summary>主诊断候选:门控与入口(及落位条件)里 <c>mainDiagnosisIn</c> 码集的交集;
    /// 交集为空时退回门控码集。</summary>
    private static List<string> DiagnosisCandidates(CompiledMdc mdc, CompiledAdrg adrg, CompiledSplit split,
        GeneratorContext ctx)
    {
        var sets = new List<IReadOnlySet<string>>();
        CollectCodeSets(mdc.Gate, ConditionKind.MainDiagnosisIn, sets);
        foreach (var e in adrg.Entries) CollectCodeSets(e.When, ConditionKind.MainDiagnosisIn, sets);
        if (split.When is not null) CollectCodeSets(split.When, ConditionKind.MainDiagnosisIn, sets);

        var all = new List<IEnumerable<string>>();
        if (sets.Count == 0)
        {
            // 无 mainDiagnosisIn:退回"该 MDC 门控里出现的所有诊断码",再退回任一可作主诊断的码。
            var loose = new List<IReadOnlySet<string>>();
            CollectCodeSets(mdc.Gate, ConditionKind.AnyDiagnosisIn, loose);
            CollectCodeSets(mdc.Gate, ConditionKind.OtherDiagnosisIn, loose);
            all.Add(loose.Count > 0 ? loose.SelectMany(s => s) : [ctx.FallbackMainDiagnosis]);
        }
        else
        {
            var inter = new HashSet<string>(sets[0], StringComparer.OrdinalIgnoreCase);
            foreach (var s in sets.Skip(1)) inter.IntersectWith(s);
            all.Add(inter.Count > 0 ? inter : sets[0]);
        }

        return all.SelectMany(x => x)
            .Where(c => ctx.Pack.DiagnosisNames.ContainsKey(c) && !ctx.Pack.NonPrincipalDiagnoses.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>手术形状:无手术、每个正向操作码表各取前若干码的单操作、"主操作取自首个码表 +
    /// 其他操作取自后续码表"的双操作组合。顺序靠前者先试(它们覆盖的组更多)。</summary>
    private static List<IReadOnlyList<string>> ProcedureShapes(CompiledMdc mdc, CompiledAdrg adrg,
        CompiledSplit split, GeneratorContext ctx)
    {
        var sets = new List<IReadOnlySet<string>>();
        foreach (var kind in new[]
                 {
                     ConditionKind.MainProcedureIn, ConditionKind.AnyProcedureIn,
                     ConditionKind.AnyOtherProcedureIn, ConditionKind.ProcedureIntersectCountGte,
                 })
        {
            CollectCodeSets(mdc.Gate, kind, sets);
            foreach (var e in adrg.Entries) CollectCodeSets(e.When, kind, sets);
            if (split.When is not null) CollectCodeSets(split.When, kind, sets);
        }

        var distinct = sets
            .Select(s => OrderByPriority(s.Where(ctx.Pack.ValidProcedures.Contains).ToList(), ctx.Contested)
                .Take(6).ToList())
            .Where(s => s.Count > 0)
            .ToList();

        var shapes = new List<IReadOnlyList<string>> { Array.Empty<string>() };
        foreach (var s in distinct) foreach (var p in s) shapes.Add(new[] { p });
        if (distinct.Count >= 2)
            foreach (var p1 in distinct[0])
                foreach (var p2 in distinct[1])
                    shapes.Add(new[] { p1, p2 });
        return shapes;
    }

    /// <summary>候选并发症码:优先"字典里没有排除关系"的 MCC/CC,数量上限由调用方控制。</summary>
    private static IEnumerable<string> ComplicationCandidates(string mainDiagnosis, GeneratorContext ctx)
    {
        foreach (var code in OrderByPriority(ctx.CcCodes.Where(c => !ctx.Excluded(c, mainDiagnosis)).ToList(), ctx.Contested).Take(4)) yield return code;
        foreach (var code in OrderByPriority(ctx.MccCodes.Where(c => !ctx.Excluded(c, mainDiagnosis)).ToList(), ctx.Contested).Take(4)) yield return code;
    }

    private static WitnessEvidence? Probe(CompiledMdc mdc, CompiledAdrg adrg, CompiledSplit split,
        MedicalRecord record, GeneratorContext ctx, string strategy)
    {
        if (ctx.Probes++ >= MaxProbesPerTarget) return null;
        var outcome = OfficialPack.Engine.Group(record);
        return outcome.Status == GroupStatus.Success
               && string.Equals(outcome.Code, split.Code, StringComparison.OrdinalIgnoreCase)
            ? new WitnessEvidence(mdc, adrg, split, record, outcome, strategy)
            : null;
    }

    /// <summary>
    /// 无法自动合成时的原因。<b>不接受"反推失败"这种含糊理由</b> —— 这类清单最后会被当成
    /// "官方数据里有若干组不可达"的证据被引用,所以必须给出复核者能自己重跑一遍的量化事实:
    /// 候选样本一共扫了多少个、分别落到了哪些组、有没有一个落进本组。
    /// </summary>
    private static string DescribeFailure(CompiledMdc mdc, CompiledAdrg adrg, CompiledSplit split, GeneratorContext ctx)
    {
        var draft = new Draft
        {
            MainDx = FindMainDxCode(mdc, adrg, ctx.Pack, ctx.Contested) ?? ctx.FallbackMainDiagnosis,
            Gender = DefaultGender(mdc),
        };

        // 先看条件本身能不能满足:不能的话,原因在条件而不在排序竞争。
        if (!RuleInversion.Satisfy(mdc.Gate, draft, ctx))
            return $"{mdc.Code} 门控无法满足:{ConditionSummary(mdc.Gate)}";

        var entered = false;
        foreach (var entry in adrg.Entries)
        {
            var cand = draft.Clone();
            if (RuleInversion.Satisfy(entry.When, cand, ctx)) { draft = cand; entered = true; break; }
        }
        if (!entered)
            return $"{mdc.Code}/{adrg.Code} 入组条件无法满足:{ConditionSummary(adrg.Entries[0].When)}";
        if (split.When is not null && !RuleInversion.Satisfy(split.When, draft, ctx))
            return $"{mdc.Code}/{adrg.Code} 可入组,但落位条件无法满足:{ConditionSummary(split.When)}"
                   + (split.Origin.Length > 0 ? $"(官方原文 {split.Origin})" : "");

        // 条件都满足,那就是被先判的组吸收:按候选主诊断逐个扫一遍,给出落点分布。
        var dxCandidates = DiagnosisCandidates(mdc, adrg, split, ctx);
        var shapes = ProcedureShapes(mdc, adrg, split, ctx);
        var landing = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = 0;
        var hitSelf = false;
        var capped = false;

        foreach (var dx in dxCandidates)
            foreach (var procs in shapes)
            {
                if (samples >= MaxProbesPerTarget) { capped = true; break; }
                samples++;
                var o = OfficialPack.Engine.Group(new MedicalRecord
                {
                    Index = "why",
                    Gender = draft.Gender ?? "1",
                    Age = draft.Age,
                    AgeDay = draft.AgeDay,
                    Weight = draft.Weight,
                    Diagnoses = [dx, .. draft.OtherDx],
                    Procedures = procs,
                });
                var key = o.Status == GroupStatus.Success ? o.Code! : $"({o.Reason})";
                landing[key] = landing.GetValueOrDefault(key) + 1;
                if (string.Equals(o.Code, split.Code, StringComparison.OrdinalIgnoreCase)) hitSelf = true;
            }

        if (hitSelf)
            return "扫描中出现过落点=本组的样本,但未被采纳(生成器缺陷,请报告)";

        var top = string.Join("、", landing.OrderByDescending(kv => kv.Value).Take(4)
            .Select(kv => $"{kv.Key} {kv.Value}"));
        return $"扫描 {samples}{(capped ? "(已截断)" : "")} 个候选样本({dxCandidates.Count} 个主诊断 × {shapes.Count} 种手术形状)"
               + $",无一落入本组;落点分布:{top}"
               + (split.Origin.Length > 0 ? $";落位条件原文 {split.Origin}" : "");
    }

    /// <summary>条件摘要:只给结构不给全集(码表动辄几千码,写进清单没人看)。</summary>
    private static string ConditionSummary(Condition? c) => c is null
        ? "(恒真)"
        : c.Kind switch
        {
            ConditionKind.True => "(恒真)",
            ConditionKind.MainDiagnosisIn => $"主诊断∈{c.SetRef}({c.Codes.Count})",
            ConditionKind.AnyDiagnosisIn => $"任一诊断∈{c.SetRef}({c.Codes.Count})",
            ConditionKind.OtherDiagnosisIn => $"其他诊断∈{c.SetRef}({c.Codes.Count})",
            ConditionKind.MainProcedureIn => $"主手术∈{c.SetRef}({c.Codes.Count})",
            ConditionKind.AnyProcedureIn => $"任一操作∈{c.SetRef}({c.Codes.Count})",
            ConditionKind.AnyOtherProcedureIn => $"其他操作∈{c.SetRef}({c.Codes.Count})",
            ConditionKind.All => $"All({string.Join(" & ", c.Children.Select(ConditionSummary))})",
            ConditionKind.Any => $"Any({string.Join(" | ", c.Children.Select(ConditionSummary))})",
            ConditionKind.Not => $"Not({ConditionSummary(c.Children[0])})",
            _ => c.Kind + (c.Value > 0 ? $"[{c.Value:0}]" : ""),
        };

    // ---------------- 位置与码集 ----------------

    /// <summary>整套反推共用的基础上下文(码表只物化一次,换 MDC 只换部位映射)。</summary>
    internal static GeneratorContext BaseContext()
    {
        var pack = OfficialPack.Data;
        return new GeneratorContext
        {
            Pack = pack,
            MccCodes = pack.Mcc.Keys.ToList(),
            CcCodes = pack.Cc.Keys.ToList(),
            ValidProcedures = pack.ValidProcedures.ToList(),
            RobotProcedures = pack.RobotProcedures.ToList(),
            FallbackMainDiagnosis = NeutralMainDiagnosis(pack),
        };
    }

    /// <summary>
    /// 保底主诊断(只在"判定与主诊断无关"的组上兜底,如手术驱动组)的选取。
    ///
    /// <para><b>不能用 <c>pack.DiagnosisNames</c> 的"第一个键"。</b>那份字典是输入侧并集目录
    /// (37,722 条),含 9,104 个<b>官方分组方案集合表未收录</b>的诊断码。取到这类码会立刻踩两个坑:</para>
    ///
    /// <para>① <b>被 <c>code-maps.csv</c> 改写</b>,于是用例的期望值建立在"映射链"而非分组规则上。
    /// 实测:<c>N07.900x002</c>「Dent病」正是该字典的第一个键,而它在 code-maps 里被折成
    /// <c>N07.900x001</c>「遗传性肾炎」(两个不同疾病),命中 <c>DI_LS2</c> 落 LS25。后果是手术驱动组
    /// (AA19/AB19/…) 的"去掉手术"边界探针,期望值被冻结成 <c>LS25</c> —— 一个与该组毫无关系的
    /// 肾病组,且这条期望值只在"兼容展开"生效时才成立。</para>
    ///
    /// <para>② <b>误触并发症/门控</b>:若保底码落在 MCC/CC 表里,会让"主诊断只是陪衬"的组凭空
    /// 多出并发症判定。</para>
    ///
    /// <para>因此保底值只从<b>对分组完全惰性</b>的码里取:在字典内、不被 code-maps 改写、不被任何
    /// 条件树引用、不属 MCC/CC、不在"不参与分组"清单。实测这类码单独输入恒为
    /// <c>NoSubgroupMatched</c>(既不进 MDC 也不落组),与 <c>N07.900x002</c> 那种"看着无害、实则
    /// 会被改写并落组"的码有本质区别。</para>
    /// </summary>
    internal static string NeutralMainDiagnosis(DataPack pack)
    {
        var referenced = ReferencedCodes(pack);
        return pack.DiagnosisNames.Keys.FirstOrDefault(k =>
                   !pack.NonPrincipalDiagnoses.Contains(k)
                   && !pack.DiagnosisMap.ContainsKey(k)
                   && !referenced.Contains(k)
                   && !pack.Mcc.ContainsKey(k)
                   && !pack.Cc.ContainsKey(k))
               ?? "A00.000";
    }

    /// <summary>条件树引用到的全部码(诊断 + 操作)。用于筛"对分组完全惰性"的保底码。</summary>
    internal static HashSet<string> ReferencedCodes(DataPack pack)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(Condition c)
        {
            if (c.CodeSet is not null) foreach (var x in c.CodeSet) set.Add(x);
            if (c.Codes is not null) foreach (var x in c.Codes) set.Add(x);
            foreach (var ch in c.Children) Walk(ch);
        }

        foreach (var mdc in pack.MdcChain)
        {
            Walk(mdc.Gate);
            foreach (var adrg in mdc.Adrgs)
            {
                foreach (var e in adrg.Entries) Walk(e.When);
                foreach (var s in adrg.Splits) if (s.When is not null) Walk(s.When);
            }
        }
        foreach (var rule in pack.QyRules.Values) Walk(rule);
        return set;
    }

    /// <summary>产科组(O)与新生儿组(P)的性别/年龄轴不能用默认值试。</summary>
    internal static string DefaultGender(CompiledMdc mdc) => mdc.Code.Equals("O", StringComparison.OrdinalIgnoreCase) ? "2" : "1";

    /// <summary>收集**正向要求**的操作码:<c>Not(...)</c> 子树下的操作条件不产生"要有某个操作"的
    /// 要求,把它算进来会造出自相矛盾的候选病案。
    ///
    /// <para>实测教训:MDCP 的门控是 <c>Any(All(… 且 Not(主手术∈OP_ALL)) | All(…))</c>,
    /// 修掉之前这里会收集到 OP_ALL 的全集(9,514 码)并被当作"本 MDC 的手术码",于是给新生儿组
    /// 造出的候选病案带着一个有效主手术 —— 恰好违反它自己门控里的那个 Not,PV11/PV13/PV15
    /// 三个组因此被误判成"不可达"。</para></summary>
    internal static IReadOnlyCollection<string> CollectProcCodes(CompiledAdrg adrg)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in adrg.Entries) CollectProcCodes(e.When, set);
        foreach (var s in adrg.Splits) if (s.When is not null) CollectProcCodes(s.When, set);
        return set;
    }

    internal static IReadOnlyCollection<string> CollectProcCodes(Condition w)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectProcCodes(w, set);
        return set;
    }

    private static void CollectProcCodes(Condition w, HashSet<string> set)
    {
        if (w.Kind == ConditionKind.Not) return;
        if (w.Kind is ConditionKind.MainProcedureIn or ConditionKind.AnyProcedureIn or ConditionKind.AnyOtherProcedureIn)
            foreach (var code in w.Codes) set.Add(code);
        foreach (var ch in w.Children) CollectProcCodes(ch, set);
    }

    /// <summary>收集条件树里的码集(同一个"只收正向要求"的口径:不进 <c>Not</c> 子树)。
    /// 否定式码集既不能用来选主诊断(选中的码恰好是它要排除的),也不能用来选操作。</summary>
    internal static void CollectCodeSets(Condition? w, ConditionKind kind, List<IReadOnlySet<string>> outSets)
    {
        if (w is null || w.Kind == ConditionKind.Not) return;
        if (w.Kind == kind && w.CodeSet.Count > 0) outSets.Add(w.CodeSet);
        foreach (var ch in w.Children) CollectCodeSets(ch, kind, outSets);
    }

    /// <summary>主诊断取 gate 与 entry 中 mainDiagnosisIn 码集的**交集**:ADRG 常把主诊断
    /// 窄化到专属码表,只满足 gate 的码会被 entry 挡下。交集为空(首个码集异常)时退回首集。</summary>
    internal static string? FindMainDxCode(CompiledMdc mdc, CompiledAdrg adrg, DataPack pack,
        IReadOnlySet<string> contested)
    {
        var sets = new List<IReadOnlySet<string>>();
        CollectCodeSets(mdc.Gate, ConditionKind.MainDiagnosisIn, sets);
        foreach (var e in adrg.Entries) CollectCodeSets(e.When, ConditionKind.MainDiagnosisIn, sets);
        if (sets.Count == 0) return null;

        var inter = new HashSet<string>(sets[0], StringComparer.OrdinalIgnoreCase);
        foreach (var s in sets.Skip(1)) inter.IntersectWith(s);

        var all = (inter.Count > 0 ? inter : sets[0]).ToList();
        // 优先没被先判规则引用过的码:选中的主诊断若出现在更靠前的组里,反推出来的病案必然被截获。
        return OrderByPriority(all, contested)
                   .FirstOrDefault(c => pack.DiagnosisNames.ContainsKey(c) && !pack.NonPrincipalDiagnoses.Contains(c))
               ?? all.FirstOrDefault();
    }

    /// <summary>在码集内挑一个"用引擎探针确认落点确为本 MDC"的主操作码,规避链式首中误落更早 MDC。</summary>
    private static string? PickRoutingProc(IReadOnlyCollection<string> candidates, string mdcCode, string mainDx)
    {
        var ordered = candidates.Where(OfficialPack.Data.ValidProcedures.Contains)
            .Concat(candidates).Distinct(StringComparer.OrdinalIgnoreCase).Take(60);
        foreach (var proc in ordered)
        {
            var probe = OfficialPack.Engine.Group(new MedicalRecord
            {
                Index = "probe",
                Gender = "1",
                Age = 40,
                Diagnoses = [mainDx],
                Procedures = [proc],
            });
            if (string.Equals(probe.Mdc, $"MDC{mdcCode}", StringComparison.OrdinalIgnoreCase)) return proc;
        }
        return null;
    }
}
