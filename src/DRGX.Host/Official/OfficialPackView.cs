using DRGX.Engine;

namespace DRGX.Host;

/// <summary>
/// 一次请求对**官方配置信息工作簿**的只读视图 —— 它握有那本工作簿(首次用到时才装载),
/// 外加"官方表怎么筛、怎么排、怎么分页"这条知识的唯一落点。
///
/// <para><b>它收口的是什么。</b>「数据一览」页有五个浏览端点(表清单 / 层级树 / 集合成员 /
/// 集合检索 / 表数据)。原先每个都自己写一遍:定位数据包内的 <c>official</c> 目录 → 确认它确实是
/// 官方轨 → 把工作簿读成表。于是"不走官方轨"的 404 文案有三份措辞、"表读不出来"的异常翻译有两份、
/// 两张集合表的成对加载还有两份;分页则有三份(默认值与上限各不相同,且各写一次
/// <c>Math.Clamp</c>)。这些副本里任何一处改了,另外几处就悄悄不一致 —— 而症状只是
/// "同一个工作簿,这个页签能看、那个页签报错",很难指到根因。</para>
///
/// <para><b>为什么不返回 <c>IResult</c>。</b>404/400 是端点层的口径,官方表读取层不该认识 HTTP。
/// 本类只回答"视图有没有打开、表在不在、这一页有多少行",翻译成状态码留给端点 ——
/// 因此本类不引用任何 ASP.NET 类型。</para>
/// </summary>
internal sealed class OfficialPackView
{
    private OfficialWorkbook? _book;

    private OfficialPackView(string dir) => Dir = dir;

    /// <summary>数据包内的 official 目录(内含官方工作簿)。</summary>
    public string Dir { get; }

    /// <summary>
    /// 工作簿本身,**首次用到时才装载**。
    ///
    /// <para>为什么不在 <see cref="Open"/> 里装:端点都是先 <c>Open</c> 再 <c>try</c>
    /// (先判"走不走官方轨"再进读取段),而装载会抛 <see cref="PackException"/> ——
    /// 目录或工作簿损坏时。若在 Open 里装,异常就绕过了端点那段 catch,
    /// 损坏的工作簿会 500 而不是 404。装载解析本身由 <see cref="OfficialWorkbook.Load"/> 缓存,
    /// 所以"持有实例"并不省下重复解析。</para>
    /// </summary>
    private OfficialWorkbook Book => _book ??= OfficialWorkbook.Load(Dir);

    /// <summary>
    /// 打开视图。<b>该数据包不走官方轨时返回 null</b>(目录内没有官方工作簿)——
    /// 这是"数据包形态不同",不是读取失败,故不用异常表达;端点据此决定是回空清单还是 404。
    /// </summary>
    public static OfficialPackView? Open(string packPath)
    {
        var dir = OfficialTable.DirOf(packPath);
        return OfficialWorkbook.HasWorkbook(dir) ? new OfficialPackView(dir) : null;
    }

    /// <summary>官方工作簿文件名(界面「表源」标注的前缀)。</summary>
    public string FileName => Book.FileName;

    /// <summary>该表的来源说明(工作簿文件名 · sheet 名),界面「表源」标注用。</summary>
    public string SourceOf(string id) => Book.SourceOf(id);

    /// <summary>按表 id 取表。工作簿缺失/损坏时抛 <see cref="PackException"/>(端点转 404)。</summary>
    public OfficialTableData Table(string id) =>
        OfficialTable.ById(id) is null
            ? throw new PackException($"未登记的官方数据表: {id}")
            : Book.Table(id);

    /// <summary>
    /// 编码集合的**索引表 + 成员表**。两者总是一起用:集合检索要同时覆盖"集合号/类型"与
    /// "成员编码/名称"两侧,集合成员清单要先经索引确认集合号存在。成对给出,
    /// 免得每处再各写一遍两个 id 字符串与两次 <c>!</c> 断言。
    /// </summary>
    public (OfficialTableData Index, OfficialTableData Members) CodeSets() =>
        (Table(OfficialWorkbook.CodeSetIndexTable), Table(OfficialWorkbook.CodeSetsTable));

    /// <summary>官方集合编号集合(dsl 列的下钻判定依据)。</summary>
    public IReadOnlyCollection<string> SetIds()
    {
        var table = Table(OfficialWorkbook.CodeSetIndexTable);
        var ci = table.IndexOf("set_id");
        if (ci < 0) return Array.Empty<string>();
        return table.Rows.Select(r => r[ci]).Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    /// <summary>分组主干树(MDC → ADRG → DRG)。</summary>
    public OfficialTreeResult Tree() => OfficialTree.Build(Book);

    /// <summary>编码集合树(类型 → 集合号;成员体积太大,由 /api/official/setmembers 按需分页给)。</summary>
    public OfficialSetTreeResult SetTree() => OfficialSetTree.Build(Book);

    // ============================ 筛选与分页 ============================
    // 纯函数,与具体视图无关 —— 它们是"官方表怎么查"这条知识的唯一落点。

    /// <summary>整行全文匹配:任一列包含关键字即命中(大小写不敏感)。</summary>
    public static bool RowContains(string[] row, string key) =>
        row.Any(c => c.Contains(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>指定列包含关键字(列下标为 -1 或行短时恒不命中)。</summary>
    public static bool CellContains(string[] row, int col, string key) =>
        col >= 0 && col < row.Length && row[col].Contains(key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 单元格比较:数值列两侧都可解析时按数值比(否则 <c>10</c> 会排在 <c>9</c> 前面);
    /// 空值/非数值**恒沉底**,不随升降序翻到顶部。
    ///
    /// <para>它是「官方表怎么排序」这条知识本身,故与筛选/分页同处一地 ——
    /// 原先私有在 <c>OfficialEndpoints</c> 里,排序口径无法脱离 HTTP 端点单独断言。</para>
    /// </summary>
    public static int CompareCell(string? a, string? b, bool numeric, bool desc)
    {
        if (numeric)
        {
            var okA = double.TryParse(a, out var da);
            var okB = double.TryParse(b, out var db);
            if (okA && okB) return desc ? db.CompareTo(da) : da.CompareTo(db);
            if (okA != okB) return okA ? -1 : 1;
        }
        var r = string.CompareOrdinal(a ?? "", b ?? "");
        return desc ? -r : r;
    }

    /// <summary>过滤 → 分页 → 投影。三个浏览端点共用同一套响应形状(总数/偏移/上限/行)。</summary>
    public static PageResult Page(OfficialTableData table, IReadOnlyList<string[]> rows,
        int? offset, int? limit, PageSpec spec)
    {
        var off = Math.Max(0, offset ?? 0);
        var take = spec.Clamp(limit);
        return new PageResult(rows.Count, off, take,
            rows.Skip(off).Take(take).Select(table.ToObject).ToArray());
    }
}

/// <summary>
/// 一页数据的响应形状。做成具名类型而非每处现搓匿名对象,是因为三个端点的
/// <c>total/offset/limit/rows</c> 必须同形 —— 前端分页控件按这四个字段读。
/// </summary>
internal sealed record PageResult(int Total, int Offset, int Limit,
    IReadOnlyList<Dictionary<string, string>> Rows);

/// <summary>
/// 分页预算:默认取多少行、最多给多少行。**按表而定**,不是全局常量 ——
/// 集合成员是窄表(4 列)可以一次给几百行,表数据是宽表且要留给排序/下钻,
/// 一次只给几十行。两个数原先散在各端点里,改一个容易漏掉另一个。
/// </summary>
internal readonly record struct PageSpec(int DefaultLimit, int MaxLimit)
{
    /// <summary>集合成员 / 集合检索:窄表,一屏给 300 行,上限 1000。</summary>
    public static readonly PageSpec Members = new(300, 1000);

    /// <summary>表数据:宽表,一屏给 50 行,上限 500。</summary>
    public static readonly PageSpec Rows = new(50, 500);

    /// <summary>夹取请求给的 limit:缺失取默认,越界夹到 [1, MaxLimit]。</summary>
    public int Clamp(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}
