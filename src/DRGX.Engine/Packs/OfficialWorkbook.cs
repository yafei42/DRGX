using System.Globalization;
using System.Text;
using System.Text.Json;
using DRGX.Text;

namespace DRGX.Engine;

// ============================================================================
// OfficialWorkbook — 官方配置信息工作簿(xlsx) → 7 张内存表
//
// 【为什么有这个文件】
// 改造前,分组规则与码表是「官方 xlsx → official/*.csv」由**外部 Python 工具链**
// (chs-drg-v2/tools/import-official-pack.py)转换出来的,而那个工具**不随本仓库发布**。
// 后果是拿到本程序的人即使拿到新版官方 Excel 也无法更新数据 —— 数据源与「官方原件」
// 之间隔着一只别人的手。本类把那次转换**移进程序内部并在装载期完成**,官方工作簿成为
// 数据源本身,包内不再有任何转换产物。
//
// 【对拍基线】
// 本类的输出必须与旧 CSV **逐字节等价**(同样的表头、同样的行序、同样的取值)。
// 移植来源就是那个 Python 导入器,逐函数对应:
//     OfficialWorkbook.Transform   ← main()
//     ResolveColumns / Layout      ← official_layout.py 的 LAYOUT / HEADER_ALIASES / resolve_columns
//     Clean                        ← official_layout.py 的 clean()
//     Renumber                     ← main() 的 renumber()
//     Amend                        ← main() 的 insert_sorted / apply_name_corrections
// 验收方式:把本类产出的 7 张表按旧 CSV 格式落盘,与原 CSV `cmp` —— 实测 7/7 一致。
//
// 【为什么转换逻辑不能简化】
// 下面每一处都是**语义修正**,不是格式偏好,删掉任何一条都会让分组结果错:
//   1. 按表头文字定位列 —— 官方 2026-09-09 版把 ADRG sheet 的首列空列删了,整表左移一位;
//      按固定序号读会把「ADRG名称」当编码读,**不报错**,于是全部 ADRG 都查不到。
//   2. 兜底组排序重排 —— 官方把 MDCO 的 O00 排在 18,而 OT1/OZ1 在 19/20;O00 规则为空
//      (恒真),留在中间会把整个 MDCO 吞掉。故空规则兜底组一律挪到所属作用域末尾。
//   3. 排除表补录 —— 官方工作簿「排除表」sheet 只有 21,036 行,比正式版 PDF 的 148 张
//      「表 6-5-N」成员清单少 7 条亚目码(它们的扩展码兄弟已在表内)。缺这 7 条会让对应
//      主诊断丢失排除能力,第四位码系统性偏高。补录清单登记在 official/amendments.json。
//   4. clean() 的空白折叠 —— Excel 把单元格内换行存成字面量 `_x000D_`,不展开会让 DSL
//      变成多行表达式而编译失败。全角空格/NBSP/零宽空格同理。
// ============================================================================

/// <summary>一张已解析的官方表:表头 + 定长行。列名即历史 CSV 表头名,下游(OfficialPackReader、
/// 「数据一览」页、两棵层级树)因此可以完全不感知数据源已由 CSV 换成工作簿。</summary>
public sealed class OfficialTableData
{
    public required string[] Header { get; init; }
    public required string[][] Rows { get; init; }

    /// <summary>列名(大小写不敏感)→ 列下标;不存在返回 -1。</summary>
    public int IndexOf(string column)
    {
        for (var i = 0; i < Header.Length; i++)
            if (string.Equals(Header[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>行 → 列名键字典(响应序列化与按名取值用)。</summary>
    public Dictionary<string, string> ToObject(string[] row)
    {
        var d = new Dictionary<string, string>(Header.Length, StringComparer.Ordinal);
        for (var i = 0; i < Header.Length; i++) d[Header[i]] = i < row.Length ? row[i] : "";
        return d;
    }

    /// <summary>按列名取值(列缺失或行短时给空串):官方表按列名取值的唯一入口,
    /// 免得到处重复 IndexOf + 越界判断(两棵层级树曾各抄一份 private Cell)。</summary>
    public string Cell(string[] row, string column)
    {
        var i = IndexOf(column);
        return i >= 0 && i < row.Length ? row[i] : "";
    }
}

/// <summary>官方配置信息工作簿。装载一次,供分组引擎与「数据一览」页共用同一份实例。</summary>
public sealed class OfficialWorkbook
{
    // ---- 逻辑表 id(与 OfficialTable 的表定义 id 一致) ----
    public const string MdcTable = "mdc";
    public const string AdrgTable = "adrg";
    public const string DrgTable = "drg";
    public const string CcTable = "cc";
    public const string ExclusionsTable = "exclusions";
    public const string CodeSetsTable = "codesets";
    public const string CodeSetIndexTable = "csindex";

    /// <summary>取值宽度:官方表最宽 8 列,留一倍余量。超宽只会多几个空引用,不影响取值。</summary>
    private const int MaxColumns = 16;

    private const int SortLast = 1 << 30;

    private readonly Dictionary<string, OfficialTableData> _tables;
    private readonly Dictionary<string, string> _sheetOf;

    public string Path { get; }
    public string FileName { get; }
    public long Size { get; }
    public IReadOnlyList<string> SheetNames { get; }

    /// <summary>装载报告:逐行记录「读到了什么、修了什么」,与旧 IMPORT-REPORT.md 的导入过程同风格。
    /// 这是数据包从「可逐行 diff 的 CSV」换成「二进制工作簿」之后,留给信息科的唯一可核对痕迹。</summary>
    public IReadOnlyList<string> Log { get; }

    private OfficialWorkbook(string path, long size, IReadOnlyList<string> sheets,
        Dictionary<string, OfficialTableData> tables, Dictionary<string, string> sheetOf, List<string> log)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Size = size;
        SheetNames = sheets;
        _tables = tables;
        _sheetOf = sheetOf;
        Log = log;
    }

    public OfficialTableData Table(string id) =>
        _tables.TryGetValue(id, out var t)
            ? t
            : throw new PackException($"官方工作簿没有表 \"{id}\"(可用: {string.Join(" / ", _tables.Keys)})");

    /// <summary>该表的工作簿溯源说明(工作簿文件名 · sheet 名),用于界面上的「表源」标注。</summary>
    public string SourceOf(string id) =>
        _sheetOf.TryGetValue(id, out var sheet) ? $"{FileName} · {sheet}" : FileName;

    // ------------------------------ 装载与缓存 ------------------------------

    private static readonly Dictionary<string, OfficialWorkbook> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long Ticks, long Size, string Amendment)> Stamp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>该目录内是否存在官方工作簿(排除 Excel 打开中的 ~$ 临时文件)。用于数据包选轨。</summary>
    public static bool HasWorkbook(string officialDir) =>
        Directory.Exists(officialDir) && Directory
            .EnumerateFiles(officialDir, "*.xlsx", SearchOption.TopDirectoryOnly)
            .Any(p => !System.IO.Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal));

    /// <summary>装载 <c>&lt;pack&gt;/official/</c> 下的官方工作簿。工作簿或登记文件变化即重读。</summary>
    public static OfficialWorkbook Load(string officialDir)
    {
        if (!Directory.Exists(officialDir))
            throw new PackException($"官方配置目录不存在: {officialDir}(期望 &lt;数据包&gt;/official/ 内含官方配置信息工作簿 .xlsx)");

        var path = FindWorkbook(officialDir);
        var amendmentsPath = System.IO.Path.Combine(officialDir, "amendments.json");
        var info = new FileInfo(path);
        var stamp = (info.LastWriteTimeUtc.Ticks, info.Length,
            File.Exists(amendmentsPath) ? $"{File.GetLastWriteTimeUtc(amendmentsPath).Ticks}:{new FileInfo(amendmentsPath).Length}" : "-");

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var hit) && Stamp.TryGetValue(path, out var old) && old == stamp)
                return hit;
            var loaded = Build(path, amendmentsPath);
            Cache[path] = loaded;
            Stamp[path] = stamp;
            return loaded;
        }
    }

    /// <summary>
    /// 定位官方工作簿。**按扩展名识别、不写死文件名** —— 目录约定是统一命名 <c>official-workbook.xlsx</c>
    /// (官方原名带「1.」序号与日期后缀,如 `1.按病组（DRG）付费3.0版分组方案配置信息_20260909更新.xlsx`,
    /// 原始出处登记在包 manifest 的 sourceFile;仍按扩展名识别以便覆盖未按约定改名的包)。
    /// Excel 打开中的临时文件(`~$xxx.xlsx`)必须排除,否则会读到半截文件。
    /// </summary>
    private static string FindWorkbook(string officialDir)
    {
        var found = Directory.EnumerateFiles(officialDir, "*.xlsx", SearchOption.TopDirectoryOnly)
            .Where(p => !System.IO.Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new PackException(
                $"官方配置目录内没有工作簿: {officialDir}\n" +
                "  本程序的数据源是国家医保局发布的分组方案配置信息 Excel(.xlsx),请把官方文件放入该目录" +
                "并统一命名为 official-workbook.xlsx(官方原名可另记于包 manifest 的 sourceFile)。"),
            _ => throw new PackException(
                $"官方配置目录内有 {found.Count} 个工作簿,无法确定用哪一份: {officialDir}\n" +
                string.Join("\n", found.Select(p => "  - " + System.IO.Path.GetFileName(p))) +
                "\n  请只保留当前生效的那一份(旧版本请移出该目录)。"),
        };
    }

    private static OfficialWorkbook Build(string path, string amendmentsPath)
    {
        var log = new List<string>();
        using var stream = File.OpenRead(path);
        var book = XlsxWorkbook.Open(stream);
        log.Add($"官方工作簿: {System.IO.Path.GetFileName(path)} ({new FileInfo(path).Length:N0} B, " +
                $"{book.SheetNames.Count} 个 sheet: {string.Join(" / ", book.SheetNames)})");

        var sheetOf = new Dictionary<string, string>(StringComparer.Ordinal) { [MdcTable] = "MDC" };
        var tables = new Dictionary<string, OfficialTableData>(StringComparer.Ordinal);
        var layout = new List<string>();

        // ---- MDC / ADRG / DRG:层级主干,官方 sort 列决定求值顺序 ----
        var mdc = ReadNodes(book, Layout.Mdc, "mdc", log, layout);
        var adrg = ReadNodes(book, Layout.Adrg, "adrg", log, layout);
        var drg = ReadNodes(book, Layout.Drg, "drg", log, layout);

        Renumber(mdc, "MDC", log);                // 作用域 = 全表
        Renumber(adrg, "ADRG", log);              // 作用域 = 所属 MDC
        Renumber(drg, "DRG", log);                // 作用域 = 所属 ADRG

        log.Add($"MDC: {mdc.Count} 行");
        log.Add($"ADRG: {adrg.Count} 行");
        log.Add($"DRG: {drg.Count} 行");

        tables[MdcTable] = NodeTable(
            ["code", "name", "dsl", "sort", "is_fallback"],
            mdc.OrderBy(n => n.Sort),
            (n, c) => c switch
            {
                0 => n.Code, 1 => n.Name, 2 => n.Dsl,
                3 => n.Sort.ToString(CultureInfo.InvariantCulture),
                _ => n.Fallback ? "1" : "0",
            });

        tables[AdrgTable] = NodeTable(
            ["code", "name", "dsl", "mdc", "sort", "is_fallback"],
            adrg.OrderBy(n => n.Scope, StringComparer.Ordinal).ThenBy(n => n.Sort),
            (n, c) => c switch
            {
                0 => n.Code, 1 => n.Name, 2 => n.Dsl, 3 => n.Scope,
                4 => n.Sort.ToString(CultureInfo.InvariantCulture),
                _ => n.Fallback ? "1" : "0",
            });
        sheetOf[AdrgTable] = "ADRG";
        sheetOf[DrgTable] = "DRG";

        tables[DrgTable] = NodeTable(
            ["code", "name", "dsl", "adrg", "mdc", "sort", "is_fallback"],
            drg.OrderBy(n => n.Scope, StringComparer.Ordinal).ThenBy(n => n.Sort),
            (n, c) => c switch
            {
                0 => n.Code, 1 => n.Name, 2 => n.Dsl, 3 => n.Scope, 4 => n.Mdc,
                5 => n.Sort.ToString(CultureInfo.InvariantCulture),
                _ => n.Fallback ? "1" : "0",
            });

        // ---- 集合:成员明细(code-sets)+ 集合索引(member_count,派生) ----
        var (setRows, setIndexRows) = ReadCodeSets(book, log, layout);
        sheetOf[CodeSetsTable] = "集合";
        sheetOf[CodeSetIndexTable] = "集合";

        // ---- CC / 排除表 ----
        var cc = ReadCc(book, log, layout);
        var excl = ReadExclusions(book, log, layout);
        sheetOf[CcTable] = "CC";
        sheetOf[ExclusionsTable] = "排除表";

        // ---- 登记差异(官方工作簿 ↔ 权威 PDF) ----
        // ⚠ 必须在本段结束后才 ToArray() 固化各表:补录/更正改的是 List<string[]>,
        // 一旦先用 ToArray() 建好 OfficialTableData,数组就与 List 脱钩,
        // 补录会**静默丢失**(表行数少 7、名称仍旧错)而界面上完全看不出来。
        var amend = Amended(amendmentsPath);
        var added = InsertSupplements(excl, amend.Supplements);
        log.Add($"排除表补充: +{added} 行 → {excl.Count} 行(登记在 official/amendments.json,取自分组方案正式版 PDF 表 6-5-N)");
        var corrected = 0;
        corrected += ApplyNameCorrections(setRows, 1, 2, amend.Names);
        corrected += ApplyNameCorrections(cc, 0, 1, amend.Names);
        corrected += ApplyNameCorrections(excl, 1, 2, amend.Names);
        log.Add($"名称更正: {corrected} 处(登记 {amend.Names.Count} 项)");

        tables[CodeSetsTable] = new OfficialTableData
        {
            Header = ["set_id", "icd_code", "icd_name", "type"],
            Rows = setRows.ToArray(),
        };
        tables[CodeSetIndexTable] = new OfficialTableData
        {
            Header = ["set_id", "type", "member_count"],
            Rows = setIndexRows.ToArray(),
        };
        tables[CcTable] = new OfficialTableData
        {
            Header = ["icd_code", "name", "exclusion_table", "type"],
            Rows = cc.ToArray(),
        };
        tables[ExclusionsTable] = new OfficialTableData
        {
            Header = ["set_id", "icd_code", "icd_name"],
            Rows = excl.ToArray(),
        };

        if (layout.Count > 0) log.InsertRange(1, layout);
        return new OfficialWorkbook(path, new FileInfo(path).Length, book.SheetNames, tables, sheetOf, log);
    }

    // ------------------------------ 列结构(移植自 tools/official_layout.py) ------------------------------

    /// <summary>一处逻辑列:旧版列索引(兜底) + 表头文字候选(首选,首个命中即用)。</summary>
    private sealed record Column(string Name, int Fallback, params string[] Aliases);

    /// <summary>一张 sheet 的读取规格:表头所在行 + 各逻辑列的定位依据。</summary>
    private sealed record SheetSpec(string Sheet, int HeaderRow, params Column[] Columns);

    /// <summary>
    /// 列结构定义。⚠ 与 Python 侧 <c>tools/official_layout.py</c> 必须保持一致 —— 官方改版时
    /// 两边会一起位移,按表头定位是唯一能自动跟上的做法。
    /// 列索引只是**兜底**:表头识别不到时回退并在装载报告里告警。
    /// </summary>
    private static class Layout
    {
        // MDC sheet 第一行是整表标题(`按病组（DRG）付费3.0版分组方案配置信息`),表头在第 2 行
        public static readonly SheetSpec Mdc = new("MDC", 2,
            new Column("code", 0, "MDC编码"),
            new Column("name", 1, "MDC名称"),
            new Column("dsl", 2, "MDC规则"),
            new Column("sort", 3, "排序"));

        public static readonly SheetSpec Adrg = new("ADRG", 1,
            new Column("code", 1, "ADRG编码"),
            new Column("name", 2, "ADRG名称"),
            new Column("dsl", 3, "ADRG规则"),
            new Column("mdc", 4, "所属MDC编码"),
            new Column("sort", 5, "MDC内排序", "排序"));

        public static readonly SheetSpec Drg = new("DRG", 1,
            new Column("code", 0, "DRG", "DRG编码"),
            new Column("name", 1, "DRG名称"),
            new Column("dsl", 2, "DRG规则"),
            new Column("adrg", 3, "所属ADRG编码"),
            new Column("mdc", 4, "所属MDC编码"),
            new Column("sort", 5, "排序"));

        public static readonly SheetSpec CodeSets = new("集合", 1,
            new Column("set", 0, "集合编号"),
            new Column("icd", 1, "ICD编码"),
            new Column("icd_name", 2, "ICD名称"),
            new Column("type", 3, "类型编号", "类型"));

        public static readonly SheetSpec Cc = new("CC", 1,
            new Column("icd", 0, "疾病编码", "ICD编码"),
            new Column("name", 1, "疾病名称", "ICD名称"),
            new Column("exclude", 2, "排除表"),
            new Column("type", 3, "类型", "类型编号"));

        public static readonly SheetSpec Exclusions = new("排除表", 1,
            new Column("set", 0, "集合编号"),
            new Column("icd", 1, "ICD编码"),
            new Column("icd_name", 2, "ICD名称"));
    }

    /// <summary>
    /// 按表头文字定位「逻辑名 → 列下标」。找不到就回退旧索引并记入报告 —— 官方改版最常见的是
    /// 「删掉首列空列导致整表左移」,那种错位**不会报错**,只会静默读错列,所以位移必须留痕。
    /// </summary>
    private static Dictionary<string, int> ResolveColumns(XlsxRow header, SheetSpec spec, List<string> layoutLog)
    {
        var cols = new Dictionary<string, int>(StringComparer.Ordinal);
        var shifted = new List<string>();
        var missing = new List<string>();

        foreach (var col in spec.Columns)
        {
            var hit = -1;
            for (var i = 0; i < header.Cells.Length; i++)
                if (col.Aliases.Contains(Clean(header.Cells[i]), StringComparer.Ordinal)) { hit = i; break; }

            if (hit >= 0)
            {
                cols[col.Name] = hit;
                if (hit != col.Fallback) shifted.Add($"{col.Name} {col.Fallback}→{hit}");
            }
            else
            {
                cols[col.Name] = col.Fallback;
                missing.Add($"{col.Name}(回退 {col.Fallback})");
            }
        }

        if (shifted.Count > 0)
            layoutLog.Add($"{spec.Sheet} 列定位: 按表头识别,相对旧索引位移 —— {string.Join(", ", shifted)}");
        if (missing.Count > 0)
            layoutLog.Add($"{spec.Sheet} 列定位: 表头未识别,回退旧索引 —— {string.Join(", ", missing)}");
        return cols;
    }

    // ------------------------------ 逐表读取 ------------------------------

    /// <summary>MDC / ADRG / DRG 共用的节点行。Scope 为排序/重排作用域(MDC 表恒 "MDC",
    /// ADRG 表为所属 MDC,DRG 表为所属 ADRG),Mdc 仅 DRG 表使用。</summary>
    private sealed class Node
    {
        public required string Code { get; init; }
        public required string Name { get; init; }
        public required string Dsl { get; init; }
        public required string Scope { get; init; }
        public string Mdc { get; init; } = "";
        public required int Row { get; init; }
        public required int SortRaw { get; init; }
        public required bool Fallback { get; init; }
        public int Sort { get; set; }
    }

    private static List<Node> ReadNodes(XlsxWorkbook book, SheetSpec spec, string kind, List<string> log, List<string> layout)
    {
        var sheet = Read(book, spec, log, layout);
        var nodes = new List<Node>(sheet.Count);
        foreach (var (rowNumber, cells, cols) in sheet)
        {
            var code = Cell(cells, cols, "code").ToUpperInvariant();
            var dsl = Cell(cells, cols, "dsl");
            var scope = kind switch
            {
                "mdc" => "MDC",
                "adrg" => Cell(cells, cols, "mdc").ToUpperInvariant(),
                _ => Cell(cells, cols, "adrg").ToUpperInvariant(),
            };
            nodes.Add(new Node
            {
                Code = code,
                Name = Cell(cells, cols, "name"),
                Dsl = dsl,
                Scope = scope,
                Mdc = cols.ContainsKey("mdc") ? Cell(cells, cols, "mdc").ToUpperInvariant() : "",
                Row = rowNumber,
                SortRaw = SortKey(Cell(cells, cols, "sort")),
                Fallback = IsFallback(code, dsl, kind),
            });
        }
        return nodes;
    }

    /// <summary>
    /// 兜底组(000 / 0000 / XX00 / XX000 / ADRG·DRG 空规则)必须排在所属作用域**末尾**。
    /// 官方把 MDCO 的 O00 排在 18、OT1/OZ1 在 19/20 —— 而 O00 规则为空(恒真),
    /// 留在中间会把整个 MDCO 吞掉。只挪排错的那些,其余保留官方原值,CSV/表因此仍与官方可逐行对照。
    /// <paramref name="what"/> 只用于装载报告里的作用域标签(MDC / ADRG / DRG)。
    /// </summary>
    private static void Renumber(List<Node> nodes, string what, List<string> log)
    {
        var moved = new List<string>();
        foreach (var scope in nodes.Select(n => n.Scope).Distinct(StringComparer.Ordinal))
        {
            var items = nodes.Where(n => string.Equals(n.Scope, scope, StringComparison.Ordinal)).ToList();
            var normal = items.Where(n => !n.Fallback).OrderBy(n => n.SortRaw).ThenBy(n => n.Row).ToList();
            var tails = items.Where(n => n.Fallback).OrderBy(n => n.SortRaw).ThenBy(n => n.Row).ToList();

            var dup = normal.GroupBy(n => n.SortRaw).Where(g => g.Count() > 1)
                .Select(g => g.Key).OrderBy(v => v).ToList();
            if (dup.Count > 0)
                log.Add($"  - 作用域 {scope}: 官方排序值重复 {string.Join(", ", dup)}(按源表行序定序)");

            foreach (var n in normal) n.Sort = n.SortRaw;

            var maxNormal = normal.Count > 0 ? normal.Max(n => n.SortRaw) : 0;
            var kept = tails.Where(t => t.SortRaw >= maxNormal).ToList();
            var misplaced = tails.Where(t => t.SortRaw < maxNormal).ToList();
            foreach (var t in kept) t.Sort = t.SortRaw;

            if (misplaced.Count == 0) continue;
            var next = (normal.Count > 0 ? normal.Max(n => n.Sort) : 0);
            if (kept.Count > 0) next = Math.Max(next, kept.Max(n => n.Sort));
            next += 1;
            foreach (var t in misplaced)
            {
                moved.Add($"{t.Code}({t.SortRaw}→{next})");
                t.Sort = next++;
            }
        }
        log.Add(moved.Count == 0
            ? $"{what} 兜底组排序重排: 0 处(官方排序已正确)"
            : $"{what} 兜底组排序重排: {moved.Count} 处 —— {string.Join(", ", moved)}");
    }

    /// <summary>集合 sheet → (成员明细行, 集合索引行)。索引的 member_count 为**行数**(与官方表逐行一致)。</summary>
    private static (List<string[]> Members, List<string[]> Index) ReadCodeSets(
        XlsxWorkbook book, List<string> log, List<string> layout)
    {
        var sheet = Read(book, Layout.CodeSets, log, layout);
        var members = new List<string[]>(sheet.Count);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, cells, cols) in sheet)
        {
            var sid = Cell(cells, cols, "set").ToUpperInvariant();
            var icd = Cell(cells, cols, "icd");
            if (sid.Length == 0 || icd.Length == 0) continue;
            var type = Cell(cells, cols, "type").ToUpperInvariant();
            members.Add([sid, icd, Cell(cells, cols, "icd_name"), type]);
            types[sid] = type;
            counts[sid] = counts.GetValueOrDefault(sid) + 1;
        }
        var index = counts.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(s => new[] { s, types[s], counts[s].ToString(CultureInfo.InvariantCulture) })
            .ToList();
        log.Add($"集合: {members.Count} 行 / {counts.Count} 个集合");
        return (members, index);
    }

    private static List<string[]> ReadCc(XlsxWorkbook book, List<string> log, List<string> layout)
    {
        var sheet = Read(book, Layout.Cc, log, layout);
        var rows = new List<string[]>(sheet.Count);
        foreach (var (_, cells, cols) in sheet)
        {
            var icd = Cell(cells, cols, "icd");
            if (icd.Length == 0) continue;
            rows.Add([icd, Cell(cells, cols, "name"),
                Cell(cells, cols, "exclude").ToUpperInvariant(),
                Cell(cells, cols, "type").ToUpperInvariant()]);
        }
        log.Add($"CC: {rows.Count} 行");
        return rows;
    }

    private static List<string[]> ReadExclusions(XlsxWorkbook book, List<string> log, List<string> layout)
    {
        var sheet = Read(book, Layout.Exclusions, log, layout);
        var rows = new List<string[]>(sheet.Count);
        foreach (var (_, cells, cols) in sheet)
        {
            var icd = Cell(cells, cols, "icd");
            if (icd.Length == 0) continue;
            rows.Add([Cell(cells, cols, "set").ToUpperInvariant(), icd, Cell(cells, cols, "icd_name")]);
        }
        log.Add($"排除表: {rows.Count} 行");
        return rows;
    }

    /// <summary>读一张 sheet 并按表头定列;跳过「分表头之前的行」与「整行皆空的行」。</summary>
    private static List<(int Row, string[] Cells, Dictionary<string, int> Cols)> Read(
        XlsxWorkbook book, SheetSpec spec, List<string> log, List<string> layout)
    {
        var all = book.ReadSheet(spec.Sheet, MaxColumns);
        if (all.Count < spec.HeaderRow)
            throw new PackException($"官方工作簿 sheet「{spec.Sheet}」只有 {all.Count} 行,不足表头行 {spec.HeaderRow}");

        var cols = ResolveColumns(all[spec.HeaderRow - 1], spec, layout);
        var width = cols.Values.Max() + 1;
        var rows = new List<(int, string[], Dictionary<string, int>)>(Math.Max(0, all.Count - spec.HeaderRow));
        for (var i = spec.HeaderRow; i < all.Count; i++)
        {
            var cells = all[i].Cells;
            var empty = true;
            for (var c = 0; c < width && c < cells.Length; c++)
                if (!string.IsNullOrWhiteSpace(cells[c])) { empty = false; break; }
            if (empty) continue;
            rows.Add((all[i].Number, cells, cols));
        }
        return rows;
    }

    private static string Cell(string[] cells, Dictionary<string, int> cols, string name)
    {
        if (!cols.TryGetValue(name, out var i)) return "";
        return i >= 0 && i < cells.Length ? Clean(cells[i]) : "";
    }

    // ------------------------------ 文本与排序口径(移植自 official_layout.clean / import 的 sort_key) ------------------------------

    /// <summary>
    /// Trim 并折叠空白(含 NBSP U+00A0 / 全角空格 U+3000 / 零宽空格 U+200B)为单空格。
    /// Excel 把单元格内的 CR/LF/TAB 存成**字面量转义** `_x000D_` / `_x000A_` / `_x0009_`
    /// (ADRG:RN4 是真实案例),这里一并展开成空格,否则 DSL 会变成多行表达式而编译失败。
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value;
        if (text.Contains("_x000", StringComparison.OrdinalIgnoreCase))
            text = text.Replace("_x000D_", " ", StringComparison.OrdinalIgnoreCase)
                       .Replace("_x000A_", " ", StringComparison.OrdinalIgnoreCase)
                       .Replace("_x0009_", " ", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder(text.Length);
        var pending = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch is '\u3000' or '\u00a0' or '\u200b')
            {
                if (sb.Length > 0) pending = true;   // 行首空白直接丢弃(等价 Python 的 strip)
                continue;
            }
            if (pending) { sb.Append(' '); pending = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>排序值 → int。空/非数值排最后但保持稳定(与 Python 侧 <c>sort_key</c> 同口径)。</summary>
    private static int SortKey(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : SortLast;

    /// <summary>
    /// 兜底组判定。注意 MDC 特殊:MDCA(先期分组)规则为空表示**恒真**而非兜底 ——
    /// 把它排到最后会破坏先期分组优先级;MDC 只有 0000 是真的兜底行。
    /// </summary>
    private static bool IsFallback(string code, string dsl, string kind)
    {
        var c = code.ToUpperInvariant();
        if (kind == "mdc") return c == "0000";
        if (dsl.Length == 0) return true;
        if (c is "000" or "0000") return true;
        return (c.Length == 3 && c.EndsWith("00", StringComparison.Ordinal))
            || (c.Length == 4 && c.EndsWith("000", StringComparison.Ordinal));
    }

    private static OfficialTableData NodeTable(string[] header, IEnumerable<Node> nodes, Func<Node, int, string> cell)
    {
        var rows = nodes.Select(n => Enumerable.Range(0, header.Length).Select(c => cell(n, c)).ToArray()).ToArray();
        return new OfficialTableData { Header = header, Rows = rows };
    }

    // ------------------------------ 登记差异(amendments.json) ------------------------------

    private sealed record Amendments(List<(string SetId, string Code, string Name)> Supplements,
        Dictionary<string, string> Names);

    /// <summary>
    /// 读登记文件。**缺失即 fail-fast** —— 它不是可选项:少了排除表补录,对应主诊断会丢掉
    /// 排除能力,第四位码系统性偏高,而界面上完全看不出来。宁可启动失败,也不要静默算错。
    /// </summary>
    private static Amendments Amended(string path)
    {
        if (!File.Exists(path))
            throw new PackException(
                $"缺少 official/amendments.json: {path}\n" +
                "  该文件登记官方工作簿与正式版 PDF 的已知差异(排除表缺 7 条亚目码、上游名称错别字)。\n" +
                "  缺它会让排除表少 7 条码而分组结果悄悄偏移,故不予加载。");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var supplements = new List<(string, string, string)>();
        if (root.TryGetProperty("exclusionSupplements", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                supplements.Add((
                    item.TryGetProperty("setId", out var s) ? s.GetString() ?? "" : "",
                    item.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                    item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        if (supplements.Count == 0)
            throw new PackException($"official/amendments.json 缺少 exclusionSupplements 条目: {path}");

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("nameCorrections", out var corrections) && corrections.ValueKind == JsonValueKind.Object)
            foreach (var prop in corrections.EnumerateObject())
                if (prop.Value.GetString() is { Length: > 0 } good) names[prop.Name] = good;

        return new Amendments(supplements, names);
    }

    /// <summary>
    /// 把补充行按「作用域, 编码」升序插进目标表(**只插不重排**,既有行序不动,
    /// 避免重新装载时产生无谓的全表 diff)。目标作用域内已有同码则跳过 —— 官方日后补齐时自然幂等。
    /// </summary>
    private static int InsertSupplements(List<string[]> rows, List<(string SetId, string Code, string Name)> supplements)
    {
        var added = 0;
        foreach (var (setId, code, name) in supplements)
        {
            var sameScope = new List<int>();
            for (var i = 0; i < rows.Count; i++)
                if (string.Equals(rows[i][0], setId, StringComparison.OrdinalIgnoreCase)) sameScope.Add(i);
            if (sameScope.Count == 0)
                throw new PackException($"official/amendments.json: 补充项 {setId}/{code} 的目标集合在排除表中不存在");
            if (sameScope.Any(i => string.Equals(rows[i][1], code, StringComparison.OrdinalIgnoreCase)))
                continue;

            var pos = sameScope[^1] + 1;
            foreach (var i in sameScope)
                if (string.CompareOrdinal(rows[i][1], code) > 0) { pos = i; break; }
            rows.Insert(pos, [setId, code, name]);
            added++;
        }
        return added;
    }

    private static int ApplyNameCorrections(List<string[]> rows, int codeIndex, int nameIndex,
        Dictionary<string, string> corrections)
    {
        if (corrections.Count == 0) return 0;
        var n = 0;
        foreach (var row in rows)
        {
            if (codeIndex >= row.Length || nameIndex >= row.Length) continue;
            if (corrections.TryGetValue(row[codeIndex], out var good)
                && !string.Equals(row[nameIndex], good, StringComparison.Ordinal))
            {
                row[nameIndex] = good;
                n++;
            }
        }
        return n;
    }
}
