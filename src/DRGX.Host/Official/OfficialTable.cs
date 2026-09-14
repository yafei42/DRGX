using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// OfficialTable — 官方配置表的可浏览清单与只读取值
//
// 「数据一览」页的数据源是官方发布的**分组方案配置信息工作簿**(<pack>/official/*.xlsx),
// 由 DRGX.Engine 的 OfficialWorkbook 在装载期解析成 7 张表。本模块只做两件事:
//   1. 描述每张表的**列口径**(中文列头、格式、外键下钻目标) —— 这是界面层的知识;
//   2. 把表 id 映射到已解析的内存表,不重复解析、不二次派生。
//
// 取值口径与官方工作簿逐格对应:列名即工作簿列头规范化后的键,行序即工作簿行序(主干表另按
// 官方「排序」列),不补区域费用 join、不重命名、不重排。列数即工作簿该 sheet 的列数。
//
// 列之间的关系只表达"外键可下钻":某列的值等于另一张表某列的值时给出 Link,点开
// 即按该值过滤目标表(详见 wwwroot/js/data.js 的下钻逻辑)。
// ============================================================================

/// <summary>单列定义。<paramref name="Fmt"/> 为前端渲染口径:text 原文 / flag 是-否 / num 千分位。</summary>
internal sealed record OfficialColumn(
    string Key,                 // 列键(与官方工作簿该 sheet 规范化后的列名一致),同时是后端返回行的键
    string Label,               // 中文列头
    string Format = "text",
    string? LinkTable = null,   // 下钻目标表 id
    string? LinkColumn = null,  // 下钻目标表的排序列(即该列值所属的目标列)
    bool Mono = false,          // 编码类列:等宽字体
    bool Dsl = false,           // 官方 DSL 表达式:串内的集合编号渲染为下钻链接
    string? Hint = null);

internal sealed record OfficialTableDef(
    string Id,
    string Label,
    string Desc,
    OfficialColumn[] Columns);

internal static class OfficialTable
{
    /// <summary>官方配置表目录(数据包内的 official 子目录,内含官方工作簿)。</summary>
    public static string DirOf(string packPath) => Path.Combine(packPath, "official");

    /// <summary>该数据包是否走官方轨(目录内含官方工作簿)。</summary>
    public static bool Available(string dir) => OfficialWorkbook.HasWorkbook(dir);

    // 表顺序即"数据一览"子标签顺序:MDC → ADRG → DRG 主干,后接合并症/排除/集合三张明细表
    private static readonly OfficialTableDef[] All =
    [
        new OfficialTableDef("mdc", "MDC 大类", "主要诊断大类及其进入条件的官方原文",
        [
            new OfficialColumn("code", "MDC 编码", Mono: true, LinkTable: "adrg", LinkColumn: "mdc", Hint: "该 MDC 下的全部 ADRG"),
            new OfficialColumn("name", "大类名称"),
            new OfficialColumn("dsl", "入组条件(DSL)", Dsl: true),
            new OfficialColumn("sort", "方案顺序", "num"),
            new OfficialColumn("is_fallback", "兜底大类", "flag"),
        ]),
        new OfficialTableDef("adrg", "ADRG 核心组", "入核心组的官方条件原文(dsl 列为官方原始表达式)",
        [
            new OfficialColumn("code", "ADRG 编码", Mono: true, LinkTable: "drg", LinkColumn: "adrg", Hint: "该核心组下的全部 DRG"),
            new OfficialColumn("name", "核心组名称"),
            new OfficialColumn("dsl", "入组条件(DSL)", Dsl: true),
            new OfficialColumn("mdc", "所属 MDC", Mono: true, LinkTable: "mdc", LinkColumn: "code"),
            new OfficialColumn("sort", "方案顺序", "num"),
            new OfficialColumn("is_fallback", "兜底组", "flag"),
        ]),
        new OfficialTableDef("drg", "DRG 细分组", "细分到具体付费组的官方条件原文",
        [
            new OfficialColumn("code", "DRG 编码", Mono: true),
            new OfficialColumn("name", "细分组名称"),
            new OfficialColumn("dsl", "入组条件(DSL)", Dsl: true),
            new OfficialColumn("adrg", "所属 ADRG", Mono: true, LinkTable: "adrg", LinkColumn: "code"),
            new OfficialColumn("mdc", "所属 MDC", Mono: true, LinkTable: "mdc", LinkColumn: "code"),
            new OfficialColumn("sort", "方案顺序", "num"),
            new OfficialColumn("is_fallback", "兜底组", "flag"),
        ]),
        new OfficialTableDef("cc", "MCC / CC", "严重并发症与合并症清单(type 列区分 MCC/CC)",
        [
            new OfficialColumn("icd_code", "ICD 编码", Mono: true),
            new OfficialColumn("name", "诊断名称"),
            new OfficialColumn("exclusion_table", "排除表", Mono: true, LinkTable: "exclusions", LinkColumn: "set_id", Hint: "该编码同表的互斥清单"),
            new OfficialColumn("type", "类型"),
        ]),
        new OfficialTableDef("exclusions", "排除表", "互斥诊断清单:同表内编码互相排除,只计一次并发症",
        [
            new OfficialColumn("set_id", "排除表号", Mono: true, LinkTable: "cc", LinkColumn: "exclusion_table", Hint: "引用该表的 MCC/CC 行"),
            new OfficialColumn("icd_code", "ICD 编码", Mono: true),
            new OfficialColumn("icd_name", "诊断名称"),
        ]),
        // 集合两表:平铺时是「索引 + 明细」两张靠 set_id 外键互跳的宽表,读不出「类型 → 集合 → 成员」
        // 的层级,故与主干一样改由「编码集合树」呈现(见 OfficialSetTree,成员走 /api/official/setmembers)。
        // 定义在此保留:列口径、成员数与成员行的取值都还靠它。
        new OfficialTableDef("csindex", "集合索引", "官方使用的全部编码集合及其成员数(member_count 即成员条数)",
        [
            new OfficialColumn("set_id", "集合号", Mono: true, LinkTable: "codesets", LinkColumn: "set_id", Hint: "该集合的成员清单"),
            new OfficialColumn("type", "类型"),
            new OfficialColumn("member_count", "成员数", "num"),
        ]),
        new OfficialTableDef("codesets", "集合成员", "各编码集合的成员明细(逐行对应官方工作簿「集合」sheet)",
        [
            new OfficialColumn("set_id", "集合号", Mono: true, LinkTable: "csindex", LinkColumn: "set_id"),
            new OfficialColumn("icd_code", "ICD 编码", Mono: true),
            new OfficialColumn("icd_name", "编码名称"),
            new OfficialColumn("type", "类型"),
        ]),
    ];

    public static IReadOnlyList<OfficialTableDef> Definitions => All;

    /// <summary>
    /// 由「数据一览 → 分组主干树」统一呈现的表:MDC → ADRG → DRG 三张表彼此是父子关系,
    /// 平铺成三个子标签读不出层级,故不再各自成页(见 OfficialTree 与 /api/official/tree)。
    /// 它们在此仍保留定义 —— 列口径、外键关系、按列名取值都还靠它。
    /// </summary>
    public static readonly string[] TreeMemberIds = ["mdc", "adrg", "drg"];

    public static OfficialTableDef? ById(string id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    // ------------------------------ 取值 ------------------------------

    /// <summary>
    /// 取一张官方表。解析已由 <see cref="OfficialWorkbook"/> 在装载期完成并缓存,此处只做 id → 表的映射
    /// （工作簿或登记文件变更时缓存自动失效,重读后无需重启服务）。
    /// </summary>
    public static OfficialTableData Load(string dir, OfficialTableDef def) => OfficialWorkbook.Load(dir).Table(def.Id);

    /// <summary>该表的来源说明(官方工作簿文件名 · sheet 名),界面「表源」标注用。</summary>
    public static string SourceOf(string dir, string id) => OfficialWorkbook.Load(dir).SourceOf(id);

    /// <summary>官方工作簿文件名(表源说明的前缀)。</summary>
    public static string WorkbookName(string dir) => OfficialWorkbook.Load(dir).FileName;

    /// <summary>官方集合编号集合(dsl 列的下钻判定依据)。</summary>
    public static IReadOnlyCollection<string> SetIds(string dir)
    {
        var table = Load(dir, ById("csindex")!);
        var ci = table.IndexOf("set_id");
        if (ci < 0) return Array.Empty<string>();
        return table.Rows.Select(r => r[ci]).Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }
}
