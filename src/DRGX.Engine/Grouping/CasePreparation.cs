namespace DRGX.Engine;

/// <summary>
/// 病案 → <see cref="PreparedCase"/> 的唯一一处。对应原 <c>GrouperEngine.Pipeline.Run()</c> 的
/// ① 输入校验 → ② 编码映射 → ③ 主诊断识别与无效码 → ④ 并发症计算(排除表) 四个阶段。
///
/// <para>阶段顺序不可换,且轨迹文案与既有实现逐字一致 —— 调用方(引擎、测试)拿到的是同一套
/// 人类可读轨迹,不因提取而漂移。</para>
///
/// <para>纯函数:不改动 <paramref name="pack"/>,除向 <paramref name="steps"/> 追加轨迹外无副作用。
/// 轨迹收集器为 null 时不产生任何轨迹(引擎默认关 Trace 时即如此)。</para>
/// </summary>
public static class CasePreparation
{
    /// <summary>
    /// 准备病案。<paramref name="codeSystem"/> 决定输入侧字典与是否做国临→医保转换。
    /// 返回失败时 <see cref="CasePreparationResult.Case"/> 为 null,调用方应按
    /// <see cref="CasePreparationResult.Status"/>/<see cref="CasePreparationResult.Reason"/> 直接产出结果。
    /// </summary>
    public static CasePreparationResult Prepare(
        MedicalRecord record, DataPack pack, CodeSystem codeSystem, List<TraceStep>? steps = null)
    {
        // ---- ① 输入校验(镜像规范 §6 契约) ----
        var invalid = ValidateInput(record, steps);
        if (invalid is not null)
            return CasePreparationResult.Failed(
                GroupStatus.CheckFailed, UngroupedReason.InputInvalid, invalid, []);

        // ---- ② 编码映射(国临版→医保版标准化,可选) ----
        var mappings = new List<CodeMapping>();
        var foreignCodes = new List<string>();
        var (diagnoses, procedures) = NormalizeCodes(record, pack, codeSystem, mappings, foreignCodes, steps);

        var mainDiagnosis = diagnoses[0];
        var otherDiagnoses = diagnoses.Skip(1).ToList();

        // 机器人辅助手术标志:17.4x 机器人码既是"非分组操作"(从常规匹配/计数剔除),
        // 又是"机器人辅助组"的触发信号。在剔除前整体识别,供 robotAssist 条件路由。
        // 触发码表来自官方 OP_ARB 集合(T005 精确 5 码,脚注18)——不可用 "17.4" 前缀
        // 匹配:17.4900/17.4900x001/17.4901 同属 17.4 家族但官方无直赋资格(误伤会错直赋 0 档)。
        var robotAssist = procedures.Any(p => pack.RobotProcedures.Contains(p));

        // ---- ③ 主诊断识别与无效码 ----
        var blocked = RejectMainDiagnosis(mainDiagnosis, pack, steps, mappings);
        if (blocked is not null) return blocked;

        // 主操作不参与分组 → 视为"无主操作";其他操作中的不参与分组项直接剔除。诊断路径继续。
        var (mainProcedure, otherProcedures, blockedMain, blockedOthers) =
            ApplyProcedureBlocklist(procedures, pack, steps);

        // ---- ④ 并发症计算(排除表机制;MCC 与 CC 独立归类) ----
        var (mccList, ccList, exclusionGroup, excluded) =
            ComputeComplications(otherDiagnoses, mainDiagnosis, pack, steps);

        var validProcedures = procedures.Where(p => pack.ValidProcedures.Contains(p)).ToList();
        Step(steps, "并发症", $"MCC {mccList.Count} 项 / CC {ccList.Count} 项 / 有效操作 {validProcedures.Count} 项");

        return CasePreparationResult.Ok(new PreparedCase
        {
            Record = record,
            Diagnoses = diagnoses,
            Procedures = procedures,
            MainDiagnosis = mainDiagnosis,
            OtherDiagnoses = otherDiagnoses,
            MainProcedure = mainProcedure,
            OtherProcedures = otherProcedures,
            RobotAssist = robotAssist,
            MajorComplications = mccList,
            MinorComplications = ccList,
            ValidProcedures = validProcedures,
            ValidProcedureSet = pack.ValidProcedures,
            ExclusionGroup = exclusionGroup,
            Mappings = mappings,
            ExcludedComplications = excluded,
            ForeignCodes = foreignCodes,
            BlockedMainProcedure = blockedMain,
            BlockedOtherProcedures = blockedOthers,
        });
    }

    // ---------------- ① 校验 ----------------

    /// <summary>返回不通过原因文案;null 表示通过。</summary>
    private static string? ValidateInput(MedicalRecord record, List<TraceStep>? steps)
    {
        if (string.IsNullOrWhiteSpace(record.Gender))
        {
            Step(steps, "校验", "患者性别为空");
            return "患者性别为空";
        }
        if (record.Gender is not ("1" or "2"))
        {
            Step(steps, "校验", "性别只能填 1（男）或 2（女）");
            return "性别只能填 1（男）或 2（女）";
        }
        if (record.Diagnoses.Count == 0)
        {
            Step(steps, "校验", "诊断信息为空");
            return "诊断信息为空";
        }
        return null;
    }

    // ---------------- ② 映射 ----------------

    private static (List<string> Diagnoses, List<string> Procedures) NormalizeCodes(
        MedicalRecord record, DataPack pack, CodeSystem codeSystem,
        List<CodeMapping> mappings, List<string> foreignCodes, List<TraceStep>? steps)
    {
        var yibao = codeSystem == CodeSystem.Yibao;
        // 输入侧字典按所选版本选取:检索/校验用哪一版的目录,就按哪一版受理输入
        var diagDict = yibao ? pack.Yibao.Diagnoses : pack.Guolin.Diagnoses;
        var procDict = yibao ? pack.Yibao.Procedures : pack.Guolin.Procedures;
        var diagnoses = Normalize(record.Diagnoses, pack.DiagnosisMap, diagDict, "诊断", mappings, foreignCodes, !yibao, pack);
        var procedures = Normalize(record.Procedures, pack.ProcedureMap, procDict, "操作", mappings, foreignCodes, !yibao, pack);
        Step(steps, "映射", yibao
            ? $"医保版输入，未做编码转换:诊断 {record.Diagnoses.Count} 项 / 操作 {procedures.Count} 项"
            : $"编码规范化完成(国临版→医保版):诊断 {record.Diagnoses.Count} 项 / 操作 {procedures.Count} 项");
        // 目标口径(医保版)目录里查不到的码:分组仍会按原码判定，但结果基本只会是「未入组」。
        // 单独说一句，免得把「版本选错」误读成「这例真的分不了组」。
        if (foreignCodes.Count > 0)
        {
            var shown = string.Join("、", foreignCodes.Take(6));
            var more = foreignCodes.Count > 6 ? $" 等 {foreignCodes.Count} 条" : "";
            Step(steps, "映射", yibao
                ? $"医保版目录无此编码:{shown}{more}（请确认输入是否为医保版编码）"
                : $"国临版目录无医保版对应，按原码直判:{shown}{more}");
        }
        return (diagnoses, procedures);
    }

    private static List<string> Normalize(
        IReadOnlyList<string> codes, IReadOnlyDictionary<string, string> map, CodeDictionary dict,
        string what, List<CodeMapping> mappings, List<string> foreignCodes, bool applyMap, DataPack pack)
    {
        var result = new List<string>(codes.Count);
        foreach (var raw in codes)
        {
            var code = raw.Trim();
            if (applyMap && map.TryGetValue(code, out var target))
            {
                mappings.Add(new CodeMapping(Orig: code, Mapped: target, Type: what));
                result.Add(target);
                if (!InTargetDictionary(target)) foreignCodes.Add(code);
                continue;
            }
            result.Add(code);
            if (!dict.Contains(code) && !InTargetDictionary(code)) foreignCodes.Add(code);
        }
        return result;

        // 目标口径 = 医保版。刻意只查医保版目录:国临码转成医保码后若医保目录也没有，
        // 说明该码只是"两版都写过、分组方案没收录"，同样值得点出来。
        bool InTargetDictionary(string c) =>
            pack.Yibao.Diagnoses.Contains(c) || pack.Yibao.Procedures.Contains(c);
    }

    // ---------------- ③ 主诊断与操作 ----------------

    /// <summary>主诊断不可识别/不参与分组时返回终止结果,否则 null。
    /// <paramref name="mappings"/> 是阶段 ② 已收集的编码映射,一并带出以免结果里丢失
    /// "输入码被转换过"这条事实。</summary>
    private static CasePreparationResult? RejectMainDiagnosis(
        string mainDiagnosis, DataPack pack, List<TraceStep>? steps, IReadOnlyList<CodeMapping> mappings)
    {
        if (!pack.DiagnosisNames.ContainsKey(mainDiagnosis))
        {
            Step(steps, "结果", $"主诊断 {mainDiagnosis} 不在诊断字典中");
            return CasePreparationResult.Failed(GroupStatus.Ungroupable,
                UngroupedReason.MainDiagnosisUnrecognized, $"主诊断 {mainDiagnosis} 不在诊断字典中", mappings);
        }
        if (pack.NonPrincipalDiagnoses.Contains(mainDiagnosis))
        {
            Step(steps, "结果", $"主诊断 {mainDiagnosis} 不参与分组");
            return CasePreparationResult.Failed(GroupStatus.Ungroupable,
                UngroupedReason.MainDiagnosisNotGroupable, $"主诊断 {mainDiagnosis} 不参与分组", mappings);
        }
        return null;
    }

    private static (string? MainProcedure, List<string> OtherProcedures, string? BlockedMain, List<string> BlockedOthers)
        ApplyProcedureBlocklist(IReadOnlyList<string> procedures, DataPack pack, List<TraceStep>? steps)
    {
        string? mainProcedure = procedures.Count > 0 ? procedures[0] : null;
        var otherProcedures = procedures.Skip(1).ToList();
        string? blockedMain = null;
        var blockedOthers = new List<string>();

        if (mainProcedure is not null && pack.NonGroupingProcedures.Contains(mainProcedure))
        {
            Step(steps, "校验", $"主操作 {mainProcedure} 不参与分组,按无主操作处理");
            blockedMain = mainProcedure;
            mainProcedure = null;
        }
        if (otherProcedures.Count > 0)
        {
            blockedOthers = otherProcedures.Where(pack.NonGroupingProcedures.Contains).ToList();
            if (blockedOthers.Count > 0)
            {
                Step(steps, "校验", $"其他操作不参与分组,已剔除 {blockedOthers.Count} 项: {string.Join(", ", blockedOthers)}");
                otherProcedures = otherProcedures.Except(blockedOthers, StringComparer.Ordinal).ToList();
            }
        }
        return (mainProcedure, otherProcedures, blockedMain, blockedOthers);
    }

    // ---------------- ④ 并发症 ----------------

    private static (List<string> Mcc, List<string> Cc, string? ExclusionGroup, List<ExcludedComplication>) ComputeComplications(
        IReadOnlyList<string> otherDiagnoses, string mainDiagnosis, DataPack pack, List<TraceStep>? steps)
    {
        var exclusionGroup = pack.Exclusions.GetValueOrDefault(mainDiagnosis);
        if (exclusionGroup is not null)
            Step(steps, "并发症", $"主诊断 {mainDiagnosis} 已按排除表排除并发症组 {exclusionGroup}");

        var mccList = new List<string>();
        var ccList = new List<string>();
        var excludedByMain = new List<ExcludedComplication>();
        foreach (var d in otherDiagnoses)
        {
            if (pack.Mcc.TryGetValue(d, out var g))
            {
                if (g != exclusionGroup) mccList.Add(d);
                else excludedByMain.Add(new ExcludedComplication(d, "MCC", g));
            }
            if (pack.Cc.TryGetValue(d, out var g2))
            {
                if (g2 != exclusionGroup) ccList.Add(d);
                else excludedByMain.Add(new ExcludedComplication(d, "CC", g2));
            }
        }
        if (excludedByMain.Count > 0)
        {
            Step(steps, "并发症", $"主诊断排除表生效,不计并发症: {string.Join(", ", excludedByMain.Select(e => $"{e.Code}({e.Kind},组{e.Group})"))}");
        }
        return (mccList, ccList, exclusionGroup, excludedByMain);
    }

    // ---------------- 轨迹 ----------------

    /// <summary>追加一条流程轨迹;收集器为 null(未开 Trace)时不做任何事。</summary>
    private static void Step(List<TraceStep>? steps, string stage, string message)
        => steps?.Add(new TraceStep(stage, message));
}
