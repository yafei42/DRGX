using System.Collections.Frozen;
using System.Text.Json;
using DRGX.Engine.Dsl;
using DRGX.Text;

namespace DRGX.Engine;

/// <summary>
/// 数据包读取与编译(两种布局,以 official/ 内是否存在官方工作簿分流):
///   official/*.xlsx        官方配置信息工作簿(MDC/ADRG/DRG/集合/CC/排除表;当前发布的包只走此轨)
///   manifest.json          元数据(方案/版本/引擎版本下限)
///   *.csv                  平面码表(UTF-8,BOM 可选,RFC4180)
///   rules/mdc-&lt;x&gt;.json     每 MDC 一规则文件(仅基础 JSON 轨)
///   code-sets/&lt;业务名&gt;.csv  具名码表(条件树经 codesRef 引用,仅基础 JSON 轨)
/// 加载流程:manifest → 平面码表 → 规则编译为只读运行时。
/// 任一契约不满足即抛 <see cref="PackException"/>(fail-fast,绝不带病运行)。
/// </summary>
public static class PackReader
{
    /// <summary>当前引擎版本(与 manifest.engine 的 ">=x.y.z" 约定比较)。</summary>
    public static readonly Version EngineVersion = new(2, 1, 0);

    /// <summary>
    /// 加载数据包。<c>official/</c> 内含官方配置信息工作簿(.xlsx)时走官方轨
    /// （规则与码表以官方工作簿为准，字典/映射沿用基础包），否则按基础 JSON 规则加载。
    ///
    /// <para>旧形态（<c>official/*.csv</c>，即官方 Excel 的转换产物）**显式拒绝而非静默降级**：
    /// 它会落到基础 JSON 轨并报"rules/mdc-a.json 不存在"，那个报错指不到真正的原因。</para>
    /// </summary>
    public static DataPack Load(string packDirectory)
    {
        var officialDir = Path.Combine(packDirectory, "official");
        if (OfficialWorkbook.HasWorkbook(officialDir))
            return OfficialPackReader.Load(packDirectory);
        if (File.Exists(Path.Combine(officialDir, "mdc.csv")))
            throw new PackException(
                $"数据包仍是旧形态（官方 Excel → official/*.csv）: {officialDir}\n" +
                "  本版本起数据源改为官方发布的配置信息工作簿本身，不再读取转换产物。\n" +
                "  迁移：把官方 .xlsx 放进该目录（保留 official/amendments.json），并移除目录下的 *.csv。");
        return LoadCore(packDirectory);
    }

    /// <summary>
    /// 官方轨仍需「沿用现有包」的基础侧表：manifest、诊断/操作字典、编码映射、
    /// 非主诊/非分组码表。
    ///
    /// 这些表官方工作簿未提供（它是分组方案配置，不含院内字典与映射），因此官方链路必须复用
    /// 基础包。与之相对，<b>分组规则与官方码表不在其列</b>——官方轨一律以官方工作簿为唯一真源。
    /// 此入口把这两类彻底隔离，既省去官方链路对旧 JSON 规则布局的硬依赖，
    /// 也杜绝"改了 rules 却不见效果"的双真源陷阱。
    ///
    /// <para><paramref name="robotProcedures"/>：机器人辅助手术触发码。官方轨由调用方传入
    /// 官方工作簿「集合」表的 <c>OP_ARB</c> 集合（官方 T005 五码 17.4100~17.4500），
    /// 故包根不再需要 <c>robot-procedures.csv</c>；基础 JSON 轨无官方集合表，回落到该文件。</para>
    /// </summary>
    internal static BaseSideTables LoadSideTables(string packDirectory, FrozenSet<string>? robotProcedures = null)
    {
        if (!Directory.Exists(packDirectory))
            throw new PackException($"数据包目录不存在: {packDirectory}");

        var manifest = ReadManifest(Path.Combine(packDirectory, "manifest.json"));

        // ---- 字典 ----
        var diagnosisNames = NameTable(Path.Combine(packDirectory, "diagnoses.csv"));
        if (diagnosisNames.Count == 0)
            throw new PackException("diagnoses.csv: 诊断字典为空");
        var procedureNames = NameTable(Path.Combine(packDirectory, "procedures.csv"));

        // ---- 编码映射 / 不参与分组 ----
        var (diagnosisMap, procedureMap) = KindTables(Path.Combine(packDirectory, "code-maps.csv"));
        var (nonPrincipalDiagnoses, nonGroupingProcedures) = KindSets(Path.Combine(packDirectory, "blocked.csv"));

        // ---- 机器人辅助手术触发码表 ----
        var robot = robotProcedures ?? CodeSet(Path.Combine(packDirectory, "robot-procedures.csv"));
        if (robot.Count == 0)
            throw new PackException("机器人辅助手术触发码表为空（官方 OP_ARB 应含 5 码）");

        // ---- 基层病组清单（primary-groups.csv，可选；仅结果标注用，不参与分组） ----
        var primaryGroups = PrimaryGroups(Path.Combine(packDirectory, "primary-groups.csv"));

        // ---- 输入侧版本字典（dict/，可选） ----
        var (yibao, guolin) = VersionDictionaries(packDirectory, diagnosisNames, procedureNames);

        return new BaseSideTables(
            manifest, diagnosisNames, procedureNames, diagnosisMap, procedureMap,
            nonPrincipalDiagnoses, nonGroupingProcedures, robot, primaryGroups, yibao, guolin);
    }

    /// <summary>
    /// <c>primary-groups.csv</c>（<c>code,name,category</c>）→ 基层病组清单。
    ///
    /// <para><b>可选展示表</b>：该清单来自医保支付政策口径，不在官方配置表内
    /// （官方工作簿的 DRG／ADRG 表无此标记），故由包根码表显式声明。
    /// 缺失或为空一律降级为空表（结果页不挂「基层病组」徽标），不影响分组与启动 ——
    /// 与 <c>mdc.csv</c> 同属"展示用可选表"。文件存在即严格校验：表头须为
    /// <c>code,name,category</c>，空白码／重复码即 fail-fast。</para>
    ///
    /// <para><b>行序即下发顺序</b>：/api/primary-groups 按本表行序返回（内科在前、手术在后），
    /// 返回有序列表而非字典，避免出现"同一份数据、每次启动顺序不同"。</para>
    /// </summary>
    private static IReadOnlyList<PrimaryGroupInfo> PrimaryGroups(string path)
    {
        if (!File.Exists(path)) return [];
        var name = Path.GetFileName(path);
        var list = new List<PrimaryGroupInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int line = 1;
        foreach (var row in ReadRows(path, 3))
        {
            line++;
            if (row.Length < 3)
                throw new PackException($"{name}: 第 {line} 行不足 3 列（须为 code,name,category）");
            var code = row[0].Trim();
            if (code.Length == 0)
                throw new PackException($"{name}: 第 {line} 行存在空白编码");
            if (!seen.Add(code))
                throw new PackException($"{name}: DRG 码重复 \"{code}\"");
            list.Add(new PrimaryGroupInfo(code, row[1].Trim(), row[2].Trim()));
        }
        return list;
    }

    /// <summary>
    /// 读取输入侧双版本字典 <c>dict/{diagnoses,procedures}.{yibao,guolin}.csv</c>（可选）。
    ///
    /// <para><b>本发布包不再分发 dict/</b>：两版一律退回分组侧并集字典（<c>diagnoses.csv</c>／
    /// <c>procedures.csv</c>），因此检索退化成"两版都能搜到"、<c>/api/info</c> 的
    /// <c>codeVersions</c> 两侧规模相同（不再区分国家医保版／国临版目录）。
    /// 缺表**降级不报错**：不会炸启动，也不会让已发布的数据包失效。</para>
    /// </summary>
    private static (CodeDictionaries Yibao, CodeDictionaries Guolin) VersionDictionaries(
        string packDirectory,
        FrozenDictionary<string, string> fallbackDiagnoses,
        FrozenDictionary<string, string> fallbackProcedures)
    {
        var dir = Path.Combine(packDirectory, "dict");
        CodeDictionary Load(string file, FrozenDictionary<string, string> fallback)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) return new CodeDictionary { Names = fallback };
            var table = NameTable(path);
            return table.Count == 0 ? new CodeDictionary { Names = fallback } : new CodeDictionary { Names = table };
        }

        return (
            new CodeDictionaries(Load("diagnoses.yibao.csv", fallbackDiagnoses), Load("procedures.yibao.csv", fallbackProcedures)),
            new CodeDictionaries(Load("diagnoses.guolin.csv", fallbackDiagnoses), Load("procedures.guolin.csv", fallbackProcedures)));
    }

    /// <summary>按基础 JSON 规则加载（不走 official 切换）。供双轨比对等需要显式取旧轨的场景使用。</summary>
    public static DataPack LoadCore(string packDirectory)
    {
        var side = LoadSideTables(packDirectory);
        var manifest = side.Manifest;

        // ---- 并发症（基础包为 code,group 两列表） ----
        var cc = NameTable(Path.Combine(packDirectory, "cc.csv"));
        var mcc = NameTable(Path.Combine(packDirectory, "mcc.csv"));
        var exclusions = NameTable(Path.Combine(packDirectory, "exclusions.csv"));
        foreach (var (_, group) in exclusions)
            if (!cc.Values.Contains(group) && !mcc.Values.Contains(group))
                throw new PackException($"exclusions.csv: 排除组号 \"{group}\" 未出现在 cc/mcc 的组号域中");

        // ---- 有效操作 / 组索引 ----
        var validProcedures = CodeSet(Path.Combine(packDirectory, "valid-procedures.csv"));
        var groupTable = GroupTable(Path.Combine(packDirectory, "groups.csv"));
        // 展示用可选表:mdc.csv 缺失或为空只降级(大类中文名不显示),不影响分组
        var mdcPath = Path.Combine(packDirectory, "mdc.csv");
        var mdcNames = File.Exists(mdcPath)
            ? NameTable(mdcPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        // ---- 规则 ----
        var codeSets = ReadNamedCodeSets(Path.Combine(packDirectory, "code-sets"));
        var mdcChain = CompileRules(packDirectory, manifest.MdcOrder, groupTable, codeSets);
        var qyRules = ReadQyRules(null, validProcedures, manifest.MdcOrder);   // 基础 JSON 轨无官方工作簿 → 走历史降级分支

        // ---- 覆盖率(警告级) ----
            var emitted = mdcChain.SelectMany(m => m.Adrgs).SelectMany(a => a.Splits).Select(t => t.Code).ToHashSet();
        var unreachable = groupTable.Keys.Where(code => !emitted.Contains(code)).ToList();

        return new DataPack
        {
            Manifest = manifest,
            DiagnosisNames = side.DiagnosisNames,
            ProcedureNames = side.ProcedureNames,
            Yibao = side.Yibao,
            Guolin = side.Guolin,
            DiagnosisMap = side.DiagnosisMap,
            ProcedureMap = side.ProcedureMap,
            NonPrincipalDiagnoses = side.NonPrincipalDiagnoses,
            NonGroupingProcedures = side.NonGroupingProcedures,
            Cc = cc,
            Mcc = mcc,
            Exclusions = exclusions,
            ValidProcedures = validProcedures,
            QyRules = qyRules,
            RobotProcedures = side.RobotProcedures,
            MdcNames = mdcNames,
            PrimaryGroups = side.PrimaryGroups,
            Groups = groupTable.ToFrozenDictionary(),
            MdcChain = mdcChain,
            UnreachableGroups = unreachable,
        };
    }

    // ---------------- 歧义组（QY）白名单 ----------------

    /// <summary>
    /// 官方歧义组（QY）判定规则：MDC 码 → 入组条件。
    /// 源为官方工作簿 ADRG 表中 code 以 QY 结尾的行（官方 21 个，不含 A/S/T/X/Y）。
    /// <paramref name="adrg"/> 为 null（基础 JSON 轨，包内无官方工作簿）时降级为
    /// 「除 A 外每个 MDC 均可判 QY，条件为主手术 ∈ 有效操作」——与历史拼接行为等价。
    /// 官方链路传入 <c>OP_ALL</c> 作为 <paramref name="validProcedures"/>（与 valid-procedures.csv 逐码一致），
    /// 避免为 21 条 QY 规则再引入对基础包侧表的依赖。
    /// </summary>
    internal static FrozenDictionary<string, Condition> ReadQyRules(
        OfficialTableData? adrg, FrozenSet<string> validProcedures, IReadOnlyList<string> mdcOrder)
    {
        // OP_ALL 与 valid-procedures.csv 逐码一致（9514/9514 已验证），直接复用，
        // 避免仅为 21 条 QY 规则去加载十万行的官方集合表。
        var compiler = new DslCompiler(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["OP_ALL"] = validProcedures.ToArray(),
        });
        var rules = new Dictionary<string, Condition>(StringComparer.OrdinalIgnoreCase);

        if (adrg is not null)
        {
            // 取列一律按列名（表头已由 OfficialWorkbook 规范化为 code/name/dsl/mdc/sort/is_fallback）
            var cCode = adrg.IndexOf("code");
            var cDsl = adrg.IndexOf("dsl");
            var cMdc = adrg.IndexOf("mdc");
            foreach (var row in adrg.Rows)
            {
                var code = Cell(row, cCode).ToUpperInvariant();
                if (code.Length != 3 || !code.EndsWith("QY", StringComparison.Ordinal))
                    continue;

                var mdc = Cell(row, cMdc);
                if (mdc.StartsWith("MDC", StringComparison.OrdinalIgnoreCase))
                    mdc = mdc[3..];
                if (mdc.Length != 1)
                    throw new PackException($"官方工作簿 ADRG 表: {code} 的所属 MDC 异常 \"{Cell(row, cMdc)}\"");

                var dsl = Cell(row, cDsl);
                if (dsl.Length == 0)
                    throw new PackException($"官方工作簿 ADRG 表: {code} 缺少入组规则（QY 为白名单组，必须显式定义）");
                if (mdc[0] != code[0])
                    throw new PackException($"官方工作簿 ADRG 表: {code} 的 MDC 归属 \"{mdc}\" 与编码首字母不符");

                rules[mdc.ToUpperInvariant()] = compiler.Compile(dsl);
            }
            if (rules.Count == 0)
                throw new PackException("官方工作簿 ADRG 表: 未找到任何 QY 歧义组定义（官方应为 21 个）");
            return rules.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        // 降级：无官方数据时保持历史行为（A 为先期分组 MDC，不判 QY）
        var fallback = compiler.Compile("ZYSS in OP_ALL");
        foreach (var mdc in mdcOrder)
            if (!mdc.Equals("A", StringComparison.OrdinalIgnoreCase))
                rules[mdc.ToUpperInvariant()] = fallback;
        return rules.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>按列下标取单元格文本;下标无效或行短于下标时返回空串(不抛,读表方无须逐列判界)。</summary>
    private static string Cell(string[] row, int index) =>
        index >= 0 && index < row.Length ? row[index] : "";

    // ---------------- manifest ----------------

    private static ManifestInfo ReadManifest(string path)
    {
        var json = ReadJson(path);

        var engineReq = json.TryGetProperty("engine", out var e) ? e.GetString() ?? "" : "";
        if (engineReq.StartsWith(">=", StringComparison.Ordinal))
        {
            if (!Version.TryParse(engineReq[2..], out var min) || EngineVersion < min)
                throw new PackException($"manifest.json: 该数据包要求引擎 >= {engineReq[2..]},当前 {EngineVersion}");
        }

        var mdcOrder = new List<string>();
        if (json.TryGetProperty("mdcOrder", out var moEl))
        {
            if (moEl.ValueKind != JsonValueKind.Array)
                throw new PackException("manifest.json: mdcOrder 必须是 MDC 码数组");
            foreach (var c in moEl.EnumerateArray())
                mdcOrder.Add(c.GetString() ?? throw new PackException("manifest.json: mdcOrder 含非字符串项"));
        }

        return new ManifestInfo(
            json.TryGetProperty("scheme", out var sc) ? sc.GetString() ?? "" : "",
            json.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
            json.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
            json.TryGetProperty("builtAt", out var b) && DateTimeOffset.TryParse(b.GetString(), out var dt) ? dt : default,
            engineReq,
            new Dictionary<string, string>(),
            json.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "")
        {
            MdcOrder = mdcOrder,
            SourceDate = json.TryGetProperty("sourceDate", out var sd) ? sd.GetString() ?? "" : "",
        };
    }

    // ---------------- 规则编译 ----------------

    private static List<CompiledMdc> CompileRules(
        string packDirectory, IReadOnlyList<string> mdcOrder,
        Dictionary<string, GroupInfo> groups, Dictionary<string, IReadOnlyList<string>> codeSets)
    {
        if (mdcOrder.Count == 0)
            throw new PackException("manifest.json: 缺少 mdcOrder(MDC 链式求值顺序)");

        var chain = new List<CompiledMdc>();
        var seenMdc = new HashSet<string>();
        foreach (var code in mdcOrder)
        {
            if (!seenMdc.Add(code))
                throw new PackException($"manifest.json: mdcOrder 中 MDC \"{code}\" 重复");

            var file = $"rules/mdc-{code.ToLowerInvariant()}.json";
            var el = ReadJson(Path.Combine(packDirectory, "rules", $"mdc-{code.ToLowerInvariant()}.json"));
            if (el.ValueKind != JsonValueKind.Object)
                throw new PackException($"{file}: 根节点必须是对象");
            if (el.TryGetProperty("code", out var cEl) && cEl.GetString() != code)
                throw new PackException($"{file}: 文件内 code 与 manifest.mdcOrder 不一致");

            chain.Add(CompileMdc(el, groups, file, codeSets));
        }
        return chain;
    }

    private static CompiledMdc CompileMdc(
        JsonElement mdcEl, Dictionary<string, GroupInfo> groups, string path,
        Dictionary<string, IReadOnlyList<string>> codeSets)
    {
        var code = RequireString(mdcEl, "code", path);
        if (code.Length != 1 || !char.IsAsciiLetterUpper(code[0]))
            throw new PackException($"{path}.code: 必须是单个大写字母");

        var gateEl = mdcEl.TryGetProperty("gate", out var g) ? g : throw new PackException($"{path}: 缺少 gate");
        var gate = ConditionParser.Parse(gateEl, $"{path}.gate", codeSets);

        IReadOnlyDictionary<string, FrozenSet<string>>? sites = null;
        if (mdcEl.TryGetProperty("sites", out var sitesEl))
        {
            if (sitesEl.ValueKind != JsonValueKind.Object)
                throw new PackException($"{path}.sites: 必须是对象");
            var dict = new Dictionary<string, FrozenSet<string>>();
            foreach (var site in sitesEl.EnumerateObject())
            {
                if (site.Value.ValueKind != JsonValueKind.Array)
                    throw new PackException($"{path}.sites.{site.Name}: 必须是编码数组");
                dict[site.Name] = site.Value.EnumerateArray()
                    .Select(c => c.GetString()?.Trim() ?? throw new PackException($"{path}.sites.{site.Name}: 含非字符串项"))
                    .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            }
            sites = dict.ToFrozenDictionary();
        }

        var adrgs = CompileAdrgs(mdcEl, groups, path, codeSets);
        if (adrgs.Count == 0)
            throw new PackException($"{path}: 至少需要一个 ADRG");

        return new CompiledMdc { Code = code, Gate = gate, Sites = sites, Adrgs = adrgs };
    }

    private static List<CompiledAdrg> CompileAdrgs(
        JsonElement mdcEl, Dictionary<string, GroupInfo> groups, string mdcPath,
        Dictionary<string, IReadOnlyList<string>> codeSets)
    {
        if (!mdcEl.TryGetProperty("adrgs", out var adrgsEl) || adrgsEl.ValueKind != JsonValueKind.Array)
            throw new PackException($"{mdcPath}: 缺少 adrgs 数组");

        var adrgs = new List<CompiledAdrg>();
        int idx = 0;
        foreach (var adrgEl in adrgsEl.EnumerateArray())
        {
            var apath = $"{mdcPath}.adrgs[{idx++}]";
            var adrgCode = RequireString(adrgEl, "code", apath);
            if (adrgCode.Length != 3)
                throw new PackException($"{apath}.code: 必须是 3 字符 ADRG 码");

            // v3: entry × splits（entry 任一命中即入组；splits 有序首中即落位）
            if (!adrgEl.TryGetProperty("entry", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array || !entriesEl.EnumerateArray().Any())
                throw new PackException($"{apath}: 缺少 entry 数组");
            if (!adrgEl.TryGetProperty("splits", out var splitsEl) || splitsEl.ValueKind != JsonValueKind.Array || !splitsEl.EnumerateArray().Any())
                throw new PackException($"{apath}: 缺少 splits 数组");

            var entries = new List<CompiledEntry>();
            int entryIdx = 0;
            foreach (var entryEl in entriesEl.EnumerateArray())
            {
                var epath = $"{apath}.entry[{entryIdx++}]";
                if (!entryEl.TryGetProperty("when", out var whenEl))
                    throw new PackException($"{epath}: 缺少 when");
                var when = ConditionParser.Parse(whenEl, $"{epath}.when", codeSets);
                entries.Add(new CompiledEntry(
                    when,
                    entryEl.TryGetProperty("label", out var label) ? label.GetString() ?? "" : ""));
            }

            var splits = new List<CompiledSplit>();
            int splitIdx = 0;
            foreach (var splitEl in splitsEl.EnumerateArray())
            {
                var spath = $"{apath}.splits[{splitIdx++}]";
                var splitCode = RequireString(splitEl, "code", spath);
                if (!groups.ContainsKey(splitCode))
                    throw new PackException($"{spath}.code: 落位码 \"{splitCode}\" 未在组索引中定义");
                if (!splitCode.StartsWith(adrgCode, StringComparison.Ordinal))
                    throw new PackException($"{spath}.code: 亚组码 \"{splitCode}\" 必须以 ADRG \"{adrgCode}\" 开头");

                Condition? when = null;
                if (splitEl.TryGetProperty("when", out var splitWhenEl))
                    when = ConditionParser.Parse(splitWhenEl, $"{spath}.when", codeSets);

                splits.Add(new CompiledSplit(
                    splitCode,
                    when,
                    splitEl.TryGetProperty("label", out var splitLabel) ? splitLabel.GetString() ?? "" : "")
                {
                    Origin = splitEl.TryGetProperty("origin", out var splitOrigin) ? splitOrigin.GetString()?.Trim() ?? "" : "",
                });
            }

            var procedureDriven = entries.Any(e => ContainsProcedureCondition(e.When))
                || splits.Any(s => s.When is not null && ContainsProcedureCondition(s.When));
            adrgs.Add(new CompiledAdrg
            {
                Code = adrgCode,
                Origin = adrgEl.TryGetProperty("origin", out var originEl) ? originEl.GetString()?.Trim() ?? "" : "",
                Entries = entries,
                Splits = splits,
                ProcedureDriven = procedureDriven,
            });
        }

        return adrgs;
    }

    /// <summary>
    /// 判定条件树是否"手术驱动"(含操作维度原语)。
    /// </summary>
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

    // ---------------- 具名码表 ----------------

    /// <summary>code-sets/&lt;业务名&gt;.csv:单列 code,文件名即 codesRef 引用名。</summary>
    private static Dictionary<string, IReadOnlyList<string>> ReadNamedCodeSets(string dir)
    {
        if (!Directory.Exists(dir))
            throw new PackException("数据包缺少 code-sets/ 目录");
        var sets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.csv"))
        {
            var setName = Path.GetFileNameWithoutExtension(file);
            var codes = new List<string>();
            int line = 1;
            foreach (var row in ReadRows(file, 1))
            {
                line++;
                var code = row[0].Trim();
                if (code.Length == 0)
                    throw new PackException($"code-sets/{setName}.csv: 第 {line} 行存在空白编码");
                codes.Add(code);
            }
            if (codes.Count == 0)
                throw new PackException($"code-sets/{setName}.csv: 不能为空");
            sets[setName] = codes;
        }
        return sets;
    }

    // ---------------- CSV 工具 ----------------

    /// <summary>读取 CSV 并校验表头首列,返回数据行(不含表头)。</summary>
    private static List<string[]> ReadRows(string path, int minColumns, string firstColumn = "code")
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path))
            throw new PackException($"数据包文件不存在: {name}");
        var rows = Csv.ReadRows(FileText.ReadText(path));
        if (rows.Count == 0 || rows[0].Length < minColumns || rows[0][0].Trim() != firstColumn)
            throw new PackException($"{name}: 表头必须以 \"{firstColumn}\" 开头且至少 {minColumns} 列");
        return rows.Skip(1).ToList();
    }

    /// <summary>code,value 两列表 → 名称/组号字典(OrdinalIgnoreCase,空白码与重复码 fail-fast)。</summary>
    private static FrozenDictionary<string, string> NameTable(string path)
    {
        var name = Path.GetFileName(path);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int line = 1;
        foreach (var row in ReadRows(path, 2))
        {
            line++;
            var code = row[0].Trim();
            if (code.Length == 0)
                throw new PackException($"{name}: 第 {line} 行存在空白编码");
            if (!dict.TryAdd(code, row.Length > 1 ? row[1] : ""))
                throw new PackException($"{name}: 编码重复 \"{code}\"");
        }
        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>单列 code 表 → 编码集合。</summary>
    private static FrozenSet<string> CodeSet(string path)
    {
        var name = Path.GetFileName(path);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int line = 1;
        foreach (var row in ReadRows(path, 1))
        {
            line++;
            var code = row[0].Trim();
            if (code.Length == 0)
                throw new PackException($"{name}: 第 {line} 行存在空白编码");
            if (!set.Add(code))
                throw new PackException($"{name}: 编码重复 \"{code}\"");
        }
        return set.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>code-maps.csv(kind,from,to) → 诊断/操作两张映射表。</summary>
    private static (FrozenDictionary<string, string> Diagnoses, FrozenDictionary<string, string> Procedures) KindTables(string path)
    {
        var name = Path.GetFileName(path);
        var dx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var proc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int line = 1;
        foreach (var row in ReadRows(path, 3, "kind"))
        {
            line++;
            var (table, kind) = (row[0].Trim()) switch
            {
                "diagnoses" => (dx, "diagnoses"),
                "procedures" => (proc, "procedures"),
                _ => throw new PackException($"{name}: 第 {line} 行未知 kind \"{row[0]}\""),
            };
            var from = row[1].Trim();
            if (from.Length == 0)
                throw new PackException($"{name}: 第 {line} 行存在空白编码");
            if (!table.TryAdd(from, row[2]))
                throw new PackException($"{name}: {kind} 编码重复 \"{from}\"");
        }
        return (dx.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), proc.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>blocked.csv(kind,code) → 非主诊 / 非驱动操作两个集合。</summary>
    private static (FrozenSet<string> Diagnoses, FrozenSet<string> Procedures) KindSets(string path)
    {
        var name = Path.GetFileName(path);
        var dx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var proc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int line = 1;
        foreach (var row in ReadRows(path, 2, "kind"))
        {
            line++;
            var set = (row[0].Trim()) switch
            {
                "diagnoses" => dx,
                "procedures" => proc,
                _ => throw new PackException($"{name}: 第 {line} 行未知 kind \"{row[0]}\""),
            };
            var code = row[1].Trim();
            if (code.Length == 0)
                throw new PackException($"{name}: 第 {line} 行存在空白编码");
            if (!set.Add(code))
                throw new PackException($"{name}: 编码重复 \"{code}\"");
        }
        return (dx.ToFrozenSet(StringComparer.OrdinalIgnoreCase), proc.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>groups.csv(code,name) → 组索引(DRG 码重复/空表 fail-fast)。</summary>
    private static Dictionary<string, GroupInfo> GroupTable(string path)
    {
        var name = Path.GetFileName(path);
        var groupTable = new Dictionary<string, GroupInfo>();
        int line = 1;
        foreach (var row in ReadRows(path, 2))
        {
            line++;
            var code = row[0].Trim();
            if (code.Length == 0)
                throw new PackException($"{name}: 第 {line} 行存在空白编码");
            if (!groupTable.TryAdd(code, new GroupInfo(code, row.Length > 1 ? row[1] : "")))
                throw new PackException($"{name}: DRG 码重复 \"{code}\"");
        }
        if (groupTable.Count == 0)
            throw new PackException($"{name}: 组索引为空");
        return groupTable;
    }

    // ---------------- json 工具 ----------------

    private static JsonElement ReadJson(string path)
    {
        if (!File.Exists(path))
            throw new PackException($"数据包文件不存在: {Path.GetFileName(path)}");
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path),
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }).RootElement;
        }
        catch (JsonException ex)
        {
            throw new PackException($"{Path.GetFileName(path)}: JSON 解析失败 - {ex.Message}");
        }
    }

    private static string RequireString(JsonElement element, string name, string path) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? throw new PackException($"{path}.{name}: 空字符串")
            : throw new PackException($"{path}: 缺少字符串字段 \"{name}\"");
}

/// <summary>
/// 数据包中「官方模式也需沿用」的基础侧表集合（见 <see cref="PackReader.LoadSideTables"/>）。
/// 不含分组规则与官方码表——这两类在官方轨以官方配置信息工作簿为唯一真源。
/// </summary>
internal sealed record BaseSideTables(
    ManifestInfo Manifest,
    FrozenDictionary<string, string> DiagnosisNames,
    FrozenDictionary<string, string> ProcedureNames,
    FrozenDictionary<string, string> DiagnosisMap,
    FrozenDictionary<string, string> ProcedureMap,
    FrozenSet<string> NonPrincipalDiagnoses,
    FrozenSet<string> NonGroupingProcedures,
    FrozenSet<string> RobotProcedures,
    IReadOnlyList<PrimaryGroupInfo> PrimaryGroups,
    CodeDictionaries Yibao,
    CodeDictionaries Guolin);
