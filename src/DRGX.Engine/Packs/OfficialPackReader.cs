using System.Collections.Frozen;
using DRGX.Engine.Dsl;
using DRGX.Text;

namespace DRGX.Engine;

/// <summary>
/// 官方轨数据包读取器（P3）：直接由国家医保局发布的**配置信息工作簿（.xlsx）**构建
/// 以官方规则为准的 <see cref="DataPack"/>（解析见 <see cref="OfficialWorkbook"/>）。
///
/// <para><b>数据源说明</b>：本读取器不再依赖任何「xlsx → csv」的转换产物。此前那一步由
/// 外部 Python 工具链完成，而工具链不随本仓库发布 —— 结果是拿到本程序的人即使拿到新版官方
/// Excel 也无法更新数据，且「数据源就是官方那份 Excel」这句话中间隔着一只别人的手。
/// 现在官方原件置于 <c>&lt;pack&gt;/official/</c>，程序在装载期自行解析。</para>
///
/// 构建策略——<b>规则与码表以官方为准，字典与运维表沿用现有包</b>：
/// <list type="bullet">
/// <item>MDC 链 / ADRG / DRG：官方 DSL 经 <see cref="DslCompiler"/> 编译为现有 <see cref="Condition"/> 树，
/// 顺序按官方 <c>排序</c> 列（装载时已把排错位置的兜底组挪到作用域末尾）。</item>
/// <item>MCC / CC / 排除表：工作簿「CC」表（类型列区分 MCC/CC）提供并发症分级与「作为并发症时引用的
/// 排除表」；「排除表」表（148 表 / 21,043 码）提供「主诊断 → 所属排除表」的成员映射。
/// 引擎按主诊断查排除表，故 <see cref="DataPack.Exclusions"/> 必须取自后者——两者语义不同，
/// 不可互相替代；用前者只能覆盖 37.5% 的主诊断，会让第四位码系统性偏高。</item>
/// <item>有效操作 = 官方「集合」表的 OP_ALL 集合；QY 白名单（ADRG 表中 QY 行）经
/// <see cref="PackReader.ReadQyRules"/> 显式编译，条件引用官方 OP_ALL。</item>
/// <item>基础侧表（manifest/字典/映射/非分组码表）经 <see cref="PackReader.LoadSideTables"/>
/// 加载；<b>不调用 <see cref="PackReader.LoadCore"/></b>，因此 <c>rules/*.json</c> 与
/// <c>code-sets/</c> 不参与官方链路，避免双真源与硬依赖。</item>
/// <item>诊断/操作字典、编码映射、非主诊/非分组码表沿用现有包（官方工作簿无对应表）；
/// 机器人辅助手术触发码取自官方 OP_ARB 集合，包根不再需要 <c>robot-procedures.csv</c>。</item>
/// <item>QY 行不编译为普通 ADRG（由 QyRules 白名单生效，避免在歧义判定前被命中为 Success）；
/// MDC <c>0000</c> 全局兜底按业务确认纳入正式链路（链尾恒真落 <c>0000</c>）。</item>
/// </list>
/// </summary>
public static class OfficialPackReader
{
    public static DataPack Load(string packDirectory)
    {
        var officialDir = Path.Combine(packDirectory, "official");
        if (!Directory.Exists(officialDir))
            throw new PackException($"官方数据目录不存在: {officialDir}");

        // 数据源 = 官方工作簿本身。同一份实例被分组链路与「数据一览」页共用(缓存见 OfficialWorkbook)。
        var book = OfficialWorkbook.Load(officialDir);

        // ---- 集合表（规则编译的唯一码表来源） ----
        var codeSets = ReadCodeSets(book.Table(OfficialWorkbook.CodeSetsTable));
        var compiler = new DslCompiler(codeSets);

        // ---- 基础侧表（字典/映射/非分组码表） ----
        // 官方工作簿无对应表，必须沿用现有包；但分组规则与官方码表不在此列——官方轨以工作簿为
        // 唯一真源，故不再调用 LoadCore，消除"改了 rules 却不生效"的双真源陷阱与对旧 JSON 布局的硬依赖。
        // 机器人辅助手术触发码同属官方来源：官方 OP_ARB 集合（T005 五码 17.4100~17.4500），
        // 因此包根不再分发 robot-procedures.csv。
        if (!codeSets.TryGetValue(SplitTraits.RobotSetRef, out var opArb) || opArb.Count == 0)
            throw new PackException($"官方工作簿「集合」表: 缺少 {SplitTraits.RobotSetRef} 集合（机器人辅助手术触发码表）");
        var side = PackReader.LoadSideTables(
            packDirectory, opArb.ToFrozenSet(StringComparer.OrdinalIgnoreCase));

        // ---- MDC / ADRG / DRG ----
        var mdcRows = ReadTable(book.Table(OfficialWorkbook.MdcTable), "MDC", 5);
        var adrgRows = ReadTable(book.Table(OfficialWorkbook.AdrgTable), "ADRG", 6);
        var drgRows = ReadTable(book.Table(OfficialWorkbook.DrgTable), "DRG", 6);

        var drgByAdrg = drgRows
            .GroupBy(r => r["adrg"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(SortKey).ToList(), StringComparer.OrdinalIgnoreCase);

        var mdcChain = new List<CompiledMdc>();
        var mdcNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mdc in mdcRows.OrderBy(SortKey))
        {
            var code = mdc["code"];
            // MDC 0000 = 全局兜底(规则空=恒真,官方 sort 27 居链尾),业务确认后纳入正式链路。
            var letter = code.Equals("0000", StringComparison.Ordinal) ? code
                : code.Length == 4 && code.StartsWith("MDC", StringComparison.Ordinal) ? code[3..]
                : throw new PackException($"官方工作簿 MDC sheet: MDC 码异常 \"{code}\"");

            mdcNames[letter] = mdc["name"];

            var adrgs = new List<CompiledAdrg>();
            foreach (var adrg in adrgRows
                .Where(r => r["mdc"].Equals(code, StringComparison.OrdinalIgnoreCase))
                .OrderBy(SortKey))
            {
                var adrgCode = adrg["code"];
                if (adrgCode.EndsWith("QY", StringComparison.Ordinal))
                    continue; // QY 由白名单机制承担，不作普通 ADRG

                if (!drgByAdrg.TryGetValue(adrgCode, out var splits))
                    throw new PackException($"官方工作簿 ADRG sheet: ADRG {adrgCode} 无任何 DRG 落位行");

                var entryWhen = compiler.Compile(adrg["dsl"]);
                var compiledSplits = splits.Select(s => new CompiledSplit(
                    s["code"],
                    s["dsl"].Length == 0 ? null : compiler.Compile(s["dsl"]),
                    s["name"])
                {
                    Origin = s["dsl"],
                }).ToList();

                foreach (var split in compiledSplits)
                    if (!split.Code.StartsWith(adrgCode, StringComparison.Ordinal))
                        throw new PackException($"官方工作簿 DRG sheet: DRG {split.Code} 与所属 ADRG {adrgCode} 前缀不符");

                adrgs.Add(new CompiledAdrg
                {
                    Code = adrgCode,
                    Origin = adrg["dsl"],
                    Entries = [new CompiledEntry(entryWhen, adrg["name"])],
                    Splits = compiledSplits,
                    // 语义与 PackReader.CompileAdrgs 保持一致：entry 或任一 split 含操作原语即为手术驱动。
                    // 仅看 entry 会让"entry 为诊断条件、靠 split 判手术"的 ADRG 被当成内科组而在
                    // 手术病例下被跳过（当前官方数据下恰好没有这种 ADRG，属靠数据巧合成立）。
                    ProcedureDriven = ContainsProcedureCondition(entryWhen)
                        || compiledSplits.Any(s => s.When is not null && ContainsProcedureCondition(s.When)),
                });
            }

            if (adrgs.Count == 0)
                throw new PackException($"官方工作簿 ADRG sheet: MDC {letter} 无可编译 ADRG（仅 QY/兜底缺失时不应发生）");

            mdcChain.Add(new CompiledMdc
            {
                Code = letter,
                Gate = compiler.Compile(mdc["dsl"]),
                Sites = null, // 官方 Z 门控为 9 路精确组合，无 siteCount 原语
                IsFallback = mdc["is_fallback"].Equals("1", StringComparison.Ordinal),
                Adrgs = adrgs,
            });
        }

        // ---- 组索引（官方 871，含 QY/兜底；未被规则引用的进 UnreachableGroups 警告） ----
        var groups = drgRows.ToDictionary(
            r => r["code"],
            r => new GroupInfo(r["code"], r["name"]),
            StringComparer.OrdinalIgnoreCase);

        // ---- MCC / CC / 排除表 ----
        // 两张表语义不同，必须分别取用：
        //   工作簿「CC」表      —— 并发症分级表：码 → MCC/CC 归类 + 它作为并发症时引用的排除表（引用方）
        //   工作簿「排除表」表  —— 排除表成员表：主诊断码 → 所属排除表（被引用成员，148 表 / 21,043 码）
        // 引擎按「主诊断」查 Exclusions（GrouperEngine），所以必须用后者建映射；用前者只能覆盖
        // 7,904/21,043（37.5%），会让 62.5% 的主诊断丢失排除能力 → 第四位码系统性偏高。
        var mcc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var diagnosisNames = new Dictionary<string, string>(side.DiagnosisNames, StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadTable(book.Table(OfficialWorkbook.CcTable), "CC", 4))
        {
            var (code, table) = (row["icd_code"], row["exclusion_table"]);
            if (!row["type"].Equals("MCC", StringComparison.OrdinalIgnoreCase))
                cc[code] = table;
            else
                mcc[code] = table;
            diagnosisNames.TryAdd(code, row["name"]);
        }
        // 主诊断 → 排除表。表内码同时补进字典（含 icd_name），避免主诊断校验误杀。
        foreach (var row in ReadTable(book.Table(OfficialWorkbook.ExclusionsTable), "排除表", 3))
        {
            exclusions[row["icd_code"]] = row["set_id"];
            diagnosisNames.TryAdd(row["icd_code"], row["icd_name"]);
        }

        // ---- 有效操作 = 官方 OP_ALL ----
        if (!codeSets.TryGetValue("OP_ALL", out var opAll) || opAll.Count == 0)
            throw new PackException("官方工作簿「集合」表: 缺少 OP_ALL 集合（有效操作表）");
        var validProcedures = opAll.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // QY 歧义组白名单：直接用官方 OP_ALL 编译（与基础包 valid-procedures.csv 逐码一致，
        // 已验证 9514/9514），使官方链路不回头依赖基础包侧表。ADRG 表恒存在，传字母序仅为
        // 给降级分支一个确定的 MDC 顺序（官方轨走不到该分支）。
        var qyRules = PackReader.ReadQyRules(
            book.Table(OfficialWorkbook.AdrgTable), validProcedures, mdcChain.Select(m => m.Code).ToList());

        var emitted = PackCoverage.Emitted(mdcChain, qyRules.Keys);
        // QY 组由白名单动态产出（{MDC}QY），已算进 emitted，不再单独按后缀过滤
        var unreachable = PackCoverage.Unreachable(groups.Keys, emitted);

        return new DataPack
        {
            Manifest = side.Manifest with
            {
                Scheme = side.Manifest.Scheme,
                Version = $"{side.Manifest.Version}+official",
                Notes = $"官方工作簿驱动（{book.FileName} 为分组规则与码表权威来源；字典/映射/非分组码表沿用现有包；rules/*.json 与 code-sets/ 不参与）",
            },
            DiagnosisNames = diagnosisNames.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ProcedureNames = side.ProcedureNames,
            Yibao = side.Yibao,
            Guolin = side.Guolin,
            DiagnosisMap = side.DiagnosisMap,
            ProcedureMap = side.ProcedureMap,
            NonPrincipalDiagnoses = side.NonPrincipalDiagnoses,
            // 官方 3.0 的 DRG 分档规则（如 EB10 = {ZYSS,QTSS} in OP_ARB）直接用机器人 5 码（OP_ARB）
            // 判定，因此官方模式下不得把机器人码当"非分组操作"剔除——blocked.csv 的机器人条目是
            // 旧包脚注18 直赋机制的手工残留。判据与"为什么不能按 17.4 前缀切"见 PackCoverage.NonGroupingOf。
            NonGroupingProcedures = PackCoverage.NonGroupingOf(side.NonGroupingProcedures, side.RobotProcedures),
            Cc = cc.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            Mcc = mcc.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            Exclusions = exclusions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ValidProcedures = validProcedures,
            QyRules = qyRules,
            RobotProcedures = side.RobotProcedures,
            MdcNames = mdcNames.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            // 基层病组：官方配置表无此标记，源为包根 primary-groups.csv（可选，缺失即空表）。
            PrimaryGroups = side.PrimaryGroups,
            Groups = groups.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            MdcChain = mdcChain,
            EmittedCodes = emitted,
            UnreachableGroups = unreachable,
        };
    }

    /// <summary>官方 Z 门控为 9 路 OR 精确组合；此处仅保留与现有引擎一致的"手术驱动"判定逻辑。</summary>
    private static bool ContainsProcedureCondition(Condition condition)
    {
        switch (condition.Kind)
        {
            case ConditionKind.MainProcedureIn:
            case ConditionKind.AnyProcedureIn:
            case ConditionKind.AnyOtherProcedureIn:
            case ConditionKind.RobotAssist:
            case ConditionKind.ProcedureCountGte:
            case ConditionKind.HasProcedure:
            case ConditionKind.NoValidMainProcedure:
            case ConditionKind.HasValidProcedure:
                return true;
            case ConditionKind.All or ConditionKind.Any:
                return condition.Children.Any(ContainsProcedureCondition);
            case ConditionKind.Not:
                return ContainsProcedureCondition(condition.Children[0]);
            default:
                return false;
        }
    }

    /// <summary>官方工作簿「集合」表(set_id,icd_code) → 集合编号 → 编码列表。</summary>
    private static Dictionary<string, IReadOnlyList<string>> ReadCodeSets(OfficialTableData table)
    {
        var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadTable(table, "集合", 2))
        {
            if (!members.TryGetValue(row["set_id"], out var list))
                members[row["set_id"]] = list = [];
            list.Add(row["icd_code"]);
        }
        return members.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>官方表 → 「表头 → 值」行字典，附加 _sort 列；重复码 fail-fast。
    /// 取值与空白折叠已在 <see cref="OfficialWorkbook"/> 装载期完成，此处只做列名映射与契约校验。</summary>
    private static List<Dictionary<string, string>> ReadTable(OfficialTableData table, string what, int minColumns)
    {
        if (table.Header.Length < minColumns)
            throw new PackException($"官方工作簿「{what}」表: 表头至少 {minColumns} 列（实际 {table.Header.Length}）");

        var result = new List<Dictionary<string, string>>(table.Rows.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Rows.Length; i++)
        {
            var raw = table.Rows[i];
            if (raw.Length == 0 || raw.All(string.IsNullOrWhiteSpace))
                continue;
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < table.Header.Length; c++)
                dict[table.Header[c]] = c < raw.Length ? raw[c].Trim() : "";
            var code = dict.GetValueOrDefault("code", "");
            if (code.Length > 0 && !seen.Add(code))
                throw new PackException($"官方工作簿「{what}」表: 编码重复 \"{code}\"(第 {i + 1} 行)");
            dict["_sort"] = int.TryParse(dict.GetValueOrDefault("sort", ""), out var s) ? s.ToString() : "0";
            result.Add(dict);
        }
        return result;
    }

    private static int SortKey(Dictionary<string, string> row) =>
        int.TryParse(row.GetValueOrDefault("_sort", ""), out var v) ? v : 0;
}
