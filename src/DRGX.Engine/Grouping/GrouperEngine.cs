namespace DRGX.Engine;

/// <summary>分组求值选项。</summary>
public sealed record GroupingOptions
{
    public static readonly GroupingOptions Default = new();
    /// <summary>记录判定轨迹(命中路径)。</summary>
    public bool Trace { get; init; }
    /// <summary>额外记录每次门控/落位的未命中细节。</summary>
    public bool VerboseTrace { get; init; }
    /// <summary>输入侧编码体系(界面「编码版本」)。默认国临版 —— 与历史默认「勾选转换医保版编码」等价。
    /// 选医保版时不做任何转换(两版存在同码不同义，无条件转换会误转)。</summary>
    public CodeSystem CodeSystem { get; init; } = CodeSystem.Guolin;
}

/// <summary>
/// 分组引擎:编码映射 → 字典/无效码校验 → 并发症计算(排除表)→ MDC 链首中 → 结果解析。
/// Trace 开启时输出判定轨迹(含排除明细);命中门控/落位附带条件级轨迹
/// (原语表达式、结果、命中码/实际值),VerboseTrace 追加全部未命中明细。
/// 线程安全:依赖的 <see cref="DataPack"/> 只读;实例创建后可跨线程并发分组。
///
/// <para>前四个阶段(病案 → <see cref="PreparedCase"/>)在 <see cref="CasePreparation"/> 里,
/// 本类只负责 MDC 链首中与结果装配。分开的理由见 <see cref="PreparedCase"/> 的说明。</para>
/// </summary>
public sealed class GrouperEngine
{
    private readonly DataPack _pack;

    public GrouperEngine(DataPack pack) => _pack = pack;

    public GroupOutcome Group(MedicalRecord record, GroupingOptions? options = null) =>
        new Pipeline(_pack, record, options ?? GroupingOptions.Default).Run();

    /// <summary>批量分组(引擎只读,可安全并行)。</summary>
    public IReadOnlyList<GroupOutcome> Group(IEnumerable<MedicalRecord> records, GroupingOptions? options = null) =>
        records.Select(r => Group(r, options)).ToArray();

    // ---------------- 内部 ----------------

    /// <summary>
    /// 单病例分组流水线。阶段顺序与轨迹文案与既有实现一致:
    /// ①~④ 输入校验/编码映射/主诊断识别/并发症计算 由 <see cref="CasePreparation.Prepare"/> 承担,
    /// 本类只做 ⑤ MDC 链首中与结果装配。
    /// </summary>
    private sealed class Pipeline(DataPack pack, MedicalRecord record, GroupingOptions options)
    {
        private readonly DataPack _pack = pack;
        private readonly MedicalRecord _record = record;
        private readonly GroupingOptions _options = options;
        private readonly List<TraceStep>? _steps = options.Trace ? [] : null;

        public GroupOutcome Run()
        {
            // ---- ①~④ 病案 → 待判定病案(校验/映射/主诊断/并发症) ----
            var prep = CasePreparation.Prepare(_record, _pack, _options.CodeSystem, _steps);
            if (prep.Case is null)
                return Outcome(prep.Status, prep.Reason, prep.Error, mappings: prep.Mappings);

            // ---- ⑤ MDC 链首中 ----
            return MatchMdcChain(prep.Case);
        }

        // ---------------- ⑤ MDC 链 ----------------

        private GroupOutcome MatchMdcChain(PreparedCase prepared)
        {
            var failures = new List<MdcFailure>();

            // 歧义病案(QY)判定依据(官方 3.0 脚注17 + 2.0 术语定义):
            // 主手术 ∈「所有手术或操作(分组内涵)」(9,514 码,即 ValidProcedures)的病例为手术病例,
            // 只走手术驱动落位(外科组/操作组);MDC 门控通过但未命中 → 歧义组 {MDC}QY,
            // 不被内科组兜底吸收、不穿透到后续 MDC。清单外的 66 类操作(康复/通气/血浆置换等)
            // 不构成手术病例,永不判 QY。
            var isSurgeryCase = prepared.MainProcedure is not null
                && _pack.ValidProcedures.Contains(prepared.MainProcedure);
            var anyGatePassed = false;

            for (int mi = 0; mi < _pack.MdcChain.Count; mi++)
            {
                var mdc = _pack.MdcChain[mi];
                // 换 MDC 只换部位映射视图:上下文本身不可变
                var mdcCtx = prepared.ContextFor(mdc.Sites);

                // 门控节点少(1~3个),Trace 开启时随判定直接收集
                List<ConditionTrace>? gateTrace = _options.Trace ? [] : null;
                var gatePassed = ConditionEvaluator.Evaluate(mdc.Gate, mdcCtx, gateTrace, "gate");
                TagScope(gateTrace, TraceScope.Gate, mdc.Code);
                if (!gatePassed)
                {
                    // 默认(非 verbose)也要给一句"为什么没进入这个 MDC":只报"进入过谁"而不报
                    // "为何没进其它",用户无法回答"为什么落位 MDCB 而不是 MDCP"。
                    // 原因取门控轨迹里第一条带事实的失败叶子 —— gateTrace 在 Trace 开启时恒收集,
                    // 与 verbose 无关,故此处无额外求值成本。
                    var gateWhy = FirstFailReason(gateTrace);
                    Step("MDC", $"MDC{mdc.Code} 未进入：{gateWhy ?? "入组条件不满足"}",
                        "mdcGateFailed", mdc.Code, ("mdc", mdc.Code), ("why", gateWhy ?? "入组条件不满足"));
                    if (_options.VerboseTrace) EmitConditionTraces($"MDC{mdc.Code}", gateTrace);
                    continue;
                }
                // always 门控(MDCA 手术驱动,与主诊断无关)不计入"MDC 已归属",
                // 否则全链落空的病例会被误判为 NoSubgroupMatched
                anyGatePassed |= mdc.Gate.Kind != ConditionKind.True;
                Step("MDC", $"进入 MDC{mdc.Code}", "mdcEntered", mdc.Code);
                EmitConditionTraces($"MDC{mdc.Code}", gateTrace);

                var candidates = 0;      // 本 MDC 内真正参与判定的核心组数
                var skippedMedical = 0;  // 手术病例按规则跳过的内科组数(非条件不满足,是流程排除)
                // 非 verbose 摘要要回答的不只是"几个组没过",还有"为什么都没过"。同 MDC 内各核心组
                // 的失败原因往往高度一致(内科病例在手术驱动 MDC 下清一色"无主要手术"),故累积一份
                // 失败叶子供统计;verbose 下已有逐条明细,不累积。
                var mdcFailures = new List<ConditionTrace>();
                for (int ai = 0; ai < mdc.Adrgs.Count; ai++)
                {
                    var adrg = mdc.Adrgs[ai];
                    // 手术病例只走手术驱动落位（外科组/操作组），内科组跳过 —— 由 QY 歧义机制接手。
                    // 例外：全局兜底 MDC（官方 0000）是"未入组"汇总桶而非内科组，必须放行，
                    // 否则主诊断落不到任何字母 MDC 门控 + 有有效主手术的病例会分裂成
                    // "非手术病例落 0000 / 手术病例报无法入组"（无官方依据，见审查报告 §1.2）。
                    if (isSurgeryCase && !adrg.ProcedureDriven && !mdc.IsFallback)
                    {
                        skippedMedical++;
                        if (_options.VerboseTrace)
                            Step("入组", $"MDC{mdc.Code} 手术病例,跳过内科组 {adrg.Code}",
                                "adrgSkippedMedical", adrg.Code, ("mdc", mdc.Code));
                        continue;
                    }
                    candidates++;

                    // v3: entry 任一命中即入组；入组后按 splits 首中落位。
                    CompiledEntry? hitEntry = null;
                    var hitTrace = new List<ConditionTrace>();
                    for (int i = 0; i < adrg.Entries.Count; i++)
                    {
                        var entry = adrg.Entries[i];
                        var whenPath = $"adrgs[{adrg.Code}].entry[{i}].when";
                        var entryTrace = new List<ConditionTrace>();
                        var entryHit = ConditionEvaluator.Evaluate(entry.When, mdcCtx, entryTrace, whenPath);
                        TagScope(entryTrace, TraceScope.AdrgEntry, adrg.Code);
                        hitTrace.AddRange(entryTrace);
                        if (entryHit)
                        {
                            hitEntry = entry;
                            break;
                        }
                    }

                    if (hitEntry is not null)
                    {
                        var adrgReason = hitEntry.Label.Length > 0
                            ? hitEntry.Label
                            : ConditionEvaluator.DescribeTree(hitEntry.When);
                        for (int j = 0; j < adrg.Splits.Count; j++)
                        {
                            var split = adrg.Splits[j];
                            if (split.When is null)
                            {
                                Step("入组", $"MDC{mdc.Code} 匹配到 {split.Code}", "drgMatched", split.Code);
                                EmitConditionTraces($"MDC{mdc.Code}", hitTrace);
                                return Success(prepared, mdc, mdcCtx, adrg, split, adrgReason, failures);
                            }
                            var splitTrace = new List<ConditionTrace>();
                            var splitWhenPath = $"adrgs[{adrg.Code}].splits[{split.Code}].when";
                            var splitHit = ConditionEvaluator.Evaluate(split.When, mdcCtx, splitTrace, splitWhenPath);
                            TagScope(splitTrace, TraceScope.DrgSplit, split.Code);
                            hitTrace.AddRange(splitTrace);
                            if (splitHit)
                            {
                                Step("入组", $"MDC{mdc.Code} 匹配到 {split.Code}", "drgMatched", split.Code);
                                EmitConditionTraces($"MDC{mdc.Code}", hitTrace);
                                return Success(prepared, mdc, mdcCtx, adrg, split, adrgReason, failures);
                            }
                        }
                    }

                    if (_options.VerboseTrace)
                    {
                        // 结论行在前、明细在后 —— 与"进入 MDCx"/"匹配到 X"两处一致。
                        // 原先此处是 Emit 在前,前端按"结论行 + 其后明细"分块时会把每个核心组的
                        // 条件错记到上一个组名下(差一格),整段明细分不清是哪一组没过。
                        Step("入组", $"MDC{mdc.Code} 未匹配到 {adrg.Code}", "adrgNotMatched", adrg.Code,
                            ("mdc", mdc.Code));
                        EmitConditionTraces($"MDC{mdc.Code}", hitTrace);
                    }
                    // entry 未命中的轨迹:非 verbose 下不逐条下发,但要喂给下面的共性原因统计
                    else if (hitEntry is null) mdcFailures.AddRange(hitTrace);
                }

                // 手术病例在某 MDC 门控通过但未命中任何手术驱动落位 → 歧义组(虚拟组,不在 825 组内)。
                // always 门控(MDCA,手术驱动先期分组)不计——未命中即继续按主诊断 MDC 归属。
                //
                // QY 走官方白名单(DataPack.QyRules),禁止按 {MDC}QY 拼接:
                //   官方仅 21 个 MDC 有 QY(不含 A/S/T/X/Y),且 PQY 额外要求「日龄<29」。
                //   白名单外的手术病例不判 QY,继续走 MDC 链(官方口径下落 SB1/TB1/XJ1/YC1 等
                //   正式手术组或 MDC 兜底组;当前数据包尚无这些组时退回未入组)。
                if (mdc.Gate.Kind != ConditionKind.True && _pack.QyRules.TryGetValue(mdc.Code, out var qyWhen))
                {
                    List<ConditionTrace>? qyTrace = _options.Trace ? [] : null;
                    var qyHit = ConditionEvaluator.Evaluate(qyWhen, mdcCtx, qyTrace, "qy");
                    TagScope(qyTrace, TraceScope.QyGate, mdc.Code);
                    if (qyHit)
                    {
                        var qyCode = $"{mdc.Code}QY";
                        Step("入组", $"歧义病案:主手术与主要诊断无关 → {qyCode}", "ambiguousQy", qyCode);
                        EmitConditionTraces($"MDC{mdc.Code}.QY", qyTrace);
                        return Outcome(GroupStatus.Ambiguous, UngroupedReason.None,
                            "歧义病案：主手术与主要诊断无关（需人工复核）",
                            prepared: prepared, mdc: $"MDC{mdc.Code}", code: qyCode,
                            mdcFailures: failures);
                    }
                    if (_options.VerboseTrace)
                    {
                        Step("MDC", $"MDC{mdc.Code} 不满足歧义组条件", "qyNotMatched", mdc.Code);
                        EmitConditionTraces($"MDC{mdc.Code}.QY", qyTrace);
                    }
                }
                // 非 verbose(前端默认)下,一个 MDC 走完只剩开头的"进入 MDCx"一句,没有任何
                // "为何没落位"的交代 —— 用户看到的"只有进入 MDCA"即由此而来。补一行聚合结论
                // (不逐条列候选,那是 verbose 的事),默认日志也能回答"为什么不在这个 MDC"。
                // verbose 已有逐条明细,不再叠加摘要,避免同一事实两套口径。
                if (!_options.VerboseTrace)
                {
                    var why = new List<string>();
                    if (candidates > 0) why.Add($"{candidates} 个核心组均未命中");
                    if (skippedMedical > 0) why.Add($"{skippedMedical} 个内科组按手术病例跳过");
                    var text = why.Count > 0
                        ? $"MDC{mdc.Code} 未落位：{string.Join("，", why)}"
                        : $"MDC{mdc.Code} 未落位";
                    var dominant = DominantReason(mdcFailures);
                    if (candidates > 0 && dominant is not null) text += $"；多为「{dominant}」";
                    Step("MDC", text, "mdcNotPlaced", mdc.Code,
                        ("candidates", candidates.ToString()),
                        ("skippedMedical", skippedMedical.ToString()));
                }
                else Step("MDC", $"MDC{mdc.Code} 未匹配到具体组", "mdcNoGroupMatched", mdc.Code);

                failures.Add(new MdcFailure(mdc.Code, DominantReason(mdcFailures), candidates, skippedMedical));
            }

            var reason = anyGatePassed ? UngroupedReason.NoSubgroupMatched : UngroupedReason.NoMdcMatched;
            Step("MDC", anyGatePassed
                ? "通过 MDC 入组条件但未匹配到具体组，无法入组"
                : "不符合任何 MDC 的入组条件，无法入组", "ungroupable");
            return Outcome(GroupStatus.Ungroupable, reason,
                anyGatePassed ? "通过 MDC 入组条件但未匹配到具体组" : "主要诊断不符合任何 MDC 的入组条件",
                prepared: prepared, mdcFailures: failures);
        }

        /// <summary>命中落位:装配成功结果。分档与直赋档在这里定 —— 引擎手上有命中的那条 split,
        /// 消费端不必再按 DRG 码回查规则或按 MCC/CC 计数推断。</summary>
        private GroupOutcome Success(PreparedCase prepared, CompiledMdc mdc, EvaluationContext mdcCtx,
            CompiledAdrg adrg, CompiledSplit split, string adrgReason, IReadOnlyList<MdcFailure> failures)
        {
            // 直赋档复核:必须复核而非按落位码静态下发 —— 同一 DRG 常有多条落位路径
            // (纯 MCC 也能落 OB11/OB21),静态下发会把常规分档误标成直赋。
            var direct = split.Direct is { } rule ? SplitTraits.ResolveDirect(rule, mdcCtx) : null;

            return Outcome(GroupStatus.Success, UngroupedReason.None, null,
                prepared: prepared,
                mdc: $"MDC{mdc.Code}",
                code: split.Code,
                adrgReason: adrgReason,
                adrgOrigin: adrg.Origin,
                drgOrigin: split.Origin,
                drgDirect: direct,
                tier: split.Tier,
                mdcFailures: failures);
        }

        // ---------------- 轨迹与结果 ----------------

        /// <summary>追加一条流程轨迹。<paramref name="stepCode"/> 是机读分类键(消费端据此合并/归帧),
        /// <paramref name="code"/> 是本行涉及的候选码 —— 二者让前端不必再从 message 文案里做正则。
        /// 文案(message)仍保留:它是同一事实的人类可读形态,导出与日志直接用它。</summary>
        private void Step(string stage, string message, string? stepCode = null, string? code = null,
            params (string Key, string Value)[] args)
            => _steps?.Add(new TraceStep(stage, message)
            {
                StepCode = stepCode,
                Code = code,
                Args = args.Length > 0 ? args.ToDictionary(a => a.Key, a => a.Value) : null,
            });

        /// <summary>给一批条件轨迹显式标注来源层与归属码。调用点本来就知道自己在判哪一层、
        /// 判的是哪个码,所以在这里赋值 —— 而不是让消费端(或本类别处)再从 Path 字符串里
        /// 解析 "gate"/"splits[" 之类子串:那是把耦合从一端搬到另一端,没有减少。
        /// <para>轨迹元素是 record 的 init 属性,只能整条替换(列表小,成本可忽略)。</para></summary>
        private static void TagScope(List<ConditionTrace>? traces, TraceScope scope, string owner)
        {
            if (traces is null) return;
            for (int i = 0; i < traces.Count; i++)
                traces[i] = traces[i] with { Scope = scope, Owner = owner };
        }

        /// <summary>门控轨迹里第一句"带事实的失败原因"(非 verbose 的 MDC 摘要用)。
        /// 取带 detail 的 falsy 叶子而非首个 falsy 节点:容器节点(同时满足 N 项/满足任一)没有事实,
        /// 拿它当原因只会退化成"入组条件不满足"这种等于没说的文案。</summary>
        private static string? FirstFailReason(List<ConditionTrace>? trace)
            => trace?.FirstOrDefault(t => !t.Result && !string.IsNullOrEmpty(t.Detail))?.Detail;

        /// <summary>未命中原因里占多数的那一条(需过半),给非 verbose 的 MDC 摘要补"多为「…」"。
        /// 原因分散时返回 null —— 宁可少说一句,不可把局部原因说成普遍原因。</summary>
        private static string? DominantReason(List<ConditionTrace> failures)
        {
            var fails = failures.Where(t => !t.Result && !string.IsNullOrEmpty(t.Detail)).ToList();
            if (fails.Count == 0) return null;
            var top = fails.GroupBy(t => t.Detail!).OrderByDescending(g => g.Count()).First();
            return top.Count() * 2 >= fails.Count ? top.Key : null;
        }

        private void EmitConditionTraces(string prefix, List<ConditionTrace>? entries)
        {
            if (_steps is null || entries is null) return;
            foreach (var t in entries)
                _steps.Add(new TraceStep("条件", $"{prefix}.{t.Path}: {t.Expression} → {(t.Result ? "✓" : "✗")}{(t.Detail is null ? "" : $" ({t.Detail})")}")
                {
                    ConditionPath = $"{prefix}.{t.Path}",
                    Expression = t.Expression,
                    Result = t.Result,
                    Detail = t.Detail,
                    Codes = t.Codes,
                    Role = t.Role,
                    SetRef = t.SetRef,
                    Scope = t.Scope,
                    Owner = t.Owner,
                    NodeKind = t.NodeKind,
                    Total = t.Total,
                    Passed = t.Passed,
                    Unjudged = t.Unjudged,
                });
        }

        private GroupOutcome Outcome(
            GroupStatus status, UngroupedReason reason, string? reasonText,
            PreparedCase? prepared = null,
            IReadOnlyList<CodeMapping>? mappings = null,
            string? mdc = null, string? code = null,
            string? adrgReason = null, string? adrgOrigin = null,
            string? drgOrigin = null, string? drgDirect = null,
            SeverityTier? tier = null,
            IReadOnlyList<MdcFailure>? mdcFailures = null)
        {
            // 0000 = 官方 drg.csv 的**全局兜底行**(code=0000 / adrg=000 / mdc=0000 / 名称为空,is_fallback=1)。
            // 它的语义是"没有可用 DRG",不是入组成功 —— 不在这里拦下,单例与批量都会显示
            // 「分组成功」+ 空组名(实测踩到,用户直接指出)。
            // ⚠ 判据必须是 code=0000,**不能**用 is_fallback:各 ADRG 自己的兜底组
            // (B000/C000/AA19… 同样名称为空、is_fallback=1)是**真实分组**,不能被误判成未入组。
            if (status == GroupStatus.Success && string.Equals(code, "0000", StringComparison.Ordinal))
            {
                status = GroupStatus.Ungroupable;
                reason = UngroupedReason.NoSubgroupMatched;
                reasonText = "无有效 DRG:落位到官方全局兜底行 0000(该码无名称,MDC/ADRG 均为占位),不计入组成功";
                code = null;
                mdc = null;   // MDC0000 与 DRG 0000 同属占位,留着会让界面显示一个不存在的系统
                drgOrigin = null;   // 占位码没有"官方条件原文"可言
                tier = null;
            }

            // 码字段仅承载真实结果:未入组不产生 "0000"/"QY" 等哨兵形态,
            // 歧义病案例外:Code = {MDC}QY(官方 2.0 术语定义的虚拟组,按主诊断 MDC 落组,不在 825 组内)
            var adrg = status switch
            {
                GroupStatus.Success when code is { Length: >= 3 } => code[..3],
                GroupStatus.Ambiguous => "QY",
                _ => null,
            };

            return new GroupOutcome
            {
                Index = _record.Index,
                Status = status,
                Reason = reason,
                ReasonText = reasonText,
                Mdc = mdc,
                Adrg = adrg,
                AdrgReason = adrgReason,
                AdrgOrigin = adrgOrigin,
                Code = code,
                Group = status == GroupStatus.Success && _pack.Groups.TryGetValue(code!, out var g) ? g : null,
                DrgOrigin = drgOrigin,
                DrgDirect = drgDirect,
                Tier = tier,
                MdcFailures = mdcFailures ?? [],
                Trace = _steps ?? [],
                Mappings = mappings ?? prepared?.Mappings ?? [],
                ExcludedComplications = prepared?.ExcludedComplications ?? [],
                MajorComplications = prepared?.MajorComplications ?? [],
                MinorComplications = prepared?.MinorComplications ?? [],
                ValidProcedures = prepared?.ValidProcedures ?? [],
                Prepared = prepared,
            };
        }
    }
}
