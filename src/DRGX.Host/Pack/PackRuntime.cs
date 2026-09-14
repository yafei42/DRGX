using System.Collections.Frozen;
using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// PackRuntime — 数据包运行时快照(Pack + Engine + 两版字典索引)
//
// 整包一次装配、启动后不再变更(改 official/ 下的工作簿需重启):Pack / Engine / 字典索引
// 同批构建、同批生效,不会出现"Pack 是新的、Engine 还是旧的"的撕裂组合。
// 规则 JSON 的原始形态不再常驻内存:「数据一览」改为直接回放官方工作簿的各个 sheet,
// 编译后的 Condition 树足以支撑分组判定。
//
// 字典索引按「编码版本」各建一套:检索候选按所选版本过滤(国临版不会列出仅医保版的码,
// 反之亦然),而**分组判定属性**(灰码/有效操作/MCC-CC/机器人)一律按医保版目标码判定 ——
// 因为 MCC/CC、排除表、有效操作清单全是医保版口径。
// ============================================================================

/// <summary>某一编码版本下的字典索引:检索用有序数组 + 精确查码用哈希表。</summary>
internal sealed class CodeDictIndex
{
    public required DictEntry[] Diagnoses { get; init; }
    public required DictEntry[] Procedures { get; init; }
    public required FrozenDictionary<string, DictEntry> DiagByCode { get; init; }
    public required FrozenDictionary<string, DictEntry> ProcByCode { get; init; }
}

internal sealed class PackRuntime
{
    public DataPack Pack { get; }
    public GrouperEngine Engine { get; }
    /// <summary>医保版索引:输入即分组口径,不做转换。</summary>
    public CodeDictIndex Yibao { get; }
    /// <summary>国临版索引:条目上带 <see cref="DictEntry.MappedTo"/>,选中后提交的仍是国临原码,由引擎转换。</summary>
    public CodeDictIndex Guolin { get; }

    private PackRuntime(DataPack pack)
    {
        Pack = pack;
        Engine = new GrouperEngine(pack);
        Yibao = BuildIndex(pack, CodeSystem.Yibao);
        Guolin = BuildIndex(pack, CodeSystem.Guolin);
    }

    /// <summary>整包加载并构建运行时快照;PackReader.Load 失败时原样抛出,由调用方决定是否中止启动。</summary>
    public static PackRuntime Load(string packPath) =>
        new(PackReader.Load(packPath));

    public CodeDictIndex Index(CodeSystem system) => system == CodeSystem.Guolin ? Guolin : Yibao;

    /// <summary>
    /// 按版本构建字典索引。
    ///
    /// <para><b>名称</b>取所选版本的目录原文(同码在两版下名称可能不同,如 C92.000x018 ——
    /// 医保版「急性髓系白血病，t（6；9）…」/ 国临版「急性髓细胞白血病，M0型」);
    /// <b>分组属性</b>(灰码/有效操作/MCC-CC/机器人)一律按 <c>map[code] ?? code</c> 判定,
    /// 因为规则表只有医保版码。国临版模式下再额外挂 <c>MappedTo</c>,让下拉能显示
    /// 「国临原码 → 医保目标码」。</para>
    /// </summary>
    private static CodeDictIndex BuildIndex(DataPack pack, CodeSystem system)
    {
        var dicts = pack.Codes(system);
        var withMap = system == CodeSystem.Guolin; // 只有国临输入需要转换
        var diagMap = pack.DiagnosisMap;
        var procMap = pack.ProcedureMap;

        var diags = BuildEntries(dicts.Diagnoses.Names, diagMap, withMap, isProcedure: false, pack,
            out var diagByCode);
        var procs = BuildEntries(dicts.Procedures.Names, procMap, withMap, isProcedure: true, pack,
            out var procByCode);
        return new CodeDictIndex
        {
            Diagnoses = diags,
            Procedures = procs,
            DiagByCode = diagByCode,
            ProcByCode = procByCode,
        };
    }

    private static DictEntry[] BuildEntries(
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, string> map,
        bool withMap,
        bool isProcedure,
        DataPack pack,
        out FrozenDictionary<string, DictEntry> byCode)
    {
        var list = new List<DictEntry>(names.Count);
        var index = new Dictionary<string, DictEntry>(names.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (code, name) in names)
        {
            // 目标码 = 分组判定真正吃的那个码
            var target = withMap && map.TryGetValue(code, out var mapped) ? mapped : code;
            // 目标码落在医保版目录之外 = 这次转换没有出口(国临独有且无映射)。
            // 标出来,否则用户只看到"未入组",分不清是版本选错还是这例真分不了组。
            var hasTarget = !withMap
                || pack.Yibao.Diagnoses.Contains(target) || pack.Yibao.Procedures.Contains(target);
            var entry = isProcedure
                ? new DictEntry(
                    code, name,
                    Blocked: pack.NonGroupingProcedures.Contains(target),
                    Valid: pack.ValidProcedures.Contains(target),
                    Robot: pack.RobotProcedures.Contains(target))
                : new DictEntry(
                    code, name,
                    Blocked: pack.NonPrincipalDiagnoses.Contains(target),
                    Valid: null,
                    Comp: pack.Mcc.ContainsKey(target) ? "MCC" : pack.Cc.ContainsKey(target) ? "CC" : null);
            if (withMap && map.TryGetValue(code, out var to))
            {
                var toName = isProcedure
                    ? pack.ProcedureNames.GetValueOrDefault(to)
                    : pack.DiagnosisNames.GetValueOrDefault(to);
                entry = entry with { MappedTo = to, MappedToName = toName };
            }
            if (!hasTarget) entry = entry with { Unmapped = true };
            list.Add(entry);
            index.TryAdd(code, entry);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Code, b.Code));
        byCode = index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        return list.ToArray();
    }
}
