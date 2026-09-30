using DRGX.Engine;

namespace DRGX.Tests;

/// <summary>
/// 官方配置信息工作簿这一层的回归闸门(<see cref="OfficialWorkbook"/> 与
/// <see cref="OfficialTableData"/>)。
///
/// <para><b>为什么值得钉住。</b>这一层是「数据一览」页与两条装载轨的共同底座:37,722 条诊断、
/// 14,330 条操作、871 个 DRG 组的可达性,全看它把工作簿读成了什么形状。但它此前**一条测试都没有** ——
/// 规则层有 witness / boundary 双基线护着,这一层全靠人肉点页面。</para>
///
/// <para><b>两段式。</b>前半段用**手搓的内存表**当 adapter,不碰 xlsx —— 取值口径(按列名、
/// 大小写不敏感、短行补空)是纯函数,不该为了测它去开一个 3.4MB 的工作簿。后半段才读真实工作簿,
/// 钉行数与"读不出来"的异常口径。</para>
/// </summary>
public class OfficialWorkbookTests
{
    // ==========================================================================
    //  一、用内存表当 adapter:取值口径(无需真实 xlsx)
    // ==========================================================================

    /// <summary>列名定位大小写不敏感;不存在的列给 -1 而不是抛。</summary>
    [Fact]
    public void IndexOfIsCaseInsensitiveAndReportsMissingColumnsAsMinusOne()
    {
        var table = Table(["code", "name", "dsl"], [["AA1", "组名", "true"]]);

        Assert.Equal(0, table.IndexOf("code"));
        Assert.Equal(0, table.IndexOf("CODE"));
        Assert.Equal(2, table.IndexOf("Dsl"));
        Assert.Equal(-1, table.IndexOf("__NO_SUCH_COL__"));
        Assert.Equal(-1, table.IndexOf(""));
    }

    /// <summary>行 → 列名键字典:每一列都要有键,短行补空串(不是漏键)。</summary>
    [Fact]
    public void ToObjectGivesEveryHeaderAKeyAndPadsShortRows()
    {
        var table = Table(["code", "name", "dsl"], []);

        var full = table.ToObject(["AA1", "组名", "true"]);
        Assert.Equal(3, full.Count);
        Assert.Equal("AA1", full["code"]);
        Assert.Equal("组名", full["name"]);
        Assert.Equal("true", full["dsl"]);

        // 行比表头短:缺的列给空串,键仍在 —— 前端按列名读,漏键会变成 undefined
        var short_ = table.ToObject(["AA1"]);
        Assert.Equal(3, short_.Count);
        Assert.Equal("AA1", short_["code"]);
        Assert.Equal("", short_["name"]);
        Assert.Equal("", short_["dsl"]);
    }

    /// <summary>按列名取值:列不存在或行短都给空串(唯一入口,免得到处重复 IndexOf + 越界判断)。</summary>
    [Fact]
    public void CellReturnsEmptyStringForMissingColumnOrShortRow()
    {
        var table = Table(["code", "name"], []);

        Assert.Equal("AA1", table.Cell(["AA1", "组名"], "code"));
        Assert.Equal("组名", table.Cell(["AA1", "组名"], "NAME"));      // 大小写不敏感
        Assert.Equal("", table.Cell(["AA1", "组名"], "__NO_SUCH_COL__"));
        Assert.Equal("", table.Cell(["AA1"], "name"));                  // 行短
        Assert.Equal("", table.Cell([], "code"));
    }

    // ==========================================================================
    //  二、读真实工作簿:形状与行数
    // ==========================================================================

    /// <summary>六张 sheet 一张不少 —— sheet 名同时是界面「表源」标注的来源。</summary>
    [Fact]
    public void RealWorkbookExposesAllSixSheets()
    {
        var book = LoadRealWorkbook();

        Assert.Equal(
            ["MDC", "ADRG", "DRG", "集合", "CC", "排除表"],
            book.SheetNames);
    }

    /// <summary>
    /// 行数与官方工作簿逐格一致。**这些数字是这一层的核心断言** ——
    /// 少一行就少一个组/一条码,而分组本身不会报错,只会安静地落兜底。
    ///
    /// <para>排除表 21,043 = 工作簿 21,036 + amendments.json 补录 7 条(官方 PDF 表 6-5-N 有、
    /// 工作簿缺)。这个差值写死在断言里,是为了让"补录被删掉"这件事有信号。</para>
    /// </summary>
    [Fact]
    public void RealWorkbookRowCountsMatchThePublishedWorkbook()
    {
        var book = LoadRealWorkbook();

        Assert.Equal(27, book.Table(OfficialWorkbook.MdcTable).Rows.Length);
        Assert.Equal(538, book.Table(OfficialWorkbook.AdrgTable).Rows.Length);
        Assert.Equal(871, book.Table(OfficialWorkbook.DrgTable).Rows.Length);
        Assert.Equal(634, book.Table(OfficialWorkbook.CodeSetIndexTable).Rows.Length);
        Assert.Equal(106_837, book.Table(OfficialWorkbook.CodeSetsTable).Rows.Length);
        Assert.Equal(7_918, book.Table(OfficialWorkbook.CcTable).Rows.Length);
        Assert.Equal(21_043, book.Table(OfficialWorkbook.ExclusionsTable).Rows.Length);
    }

    /// <summary>
    /// 主干三表必须能按列名取到值 —— 官方 2026-09-09 版删了 ADRG sheet 的首列空列、整表左移一位,
    /// 按固定序号读会把「ADRG名称」当编码读**且不报错**。按表头定位是那条修复的回归保护:
    /// 若哪天退回按序号读,这里取到的会是空串或中文名。
    /// </summary>
    [Fact]
    public void RealWorkbookMainSpineIsAddressableByColumnName()
    {
        var book = LoadRealWorkbook();

        foreach (var (table, code, parent) in new[]
                 {
                     (OfficialWorkbook.MdcTable, "code", ""),
                     (OfficialWorkbook.AdrgTable, "code", "mdc"),
                     (OfficialWorkbook.DrgTable, "code", "adrg"),
                 })
        {
            var data = book.Table(table);
            Assert.True(data.IndexOf(code) >= 0, $"{table} 缺列 {code}");
            if (parent.Length > 0) Assert.True(data.IndexOf(parent) >= 0, $"{table} 缺列 {parent}");

            var first = data.Rows[0];
            var cell = data.Cell(first, code);
            Assert.False(string.IsNullOrWhiteSpace(cell), $"{table}.{code} 首行为空 —— 列定位很可能退回了按序号");
            Assert.False(cell.Contains('（') || cell.Contains('('),
                $"{table}.{code} 读到了名称而不是编码: {cell}");
        }
    }

    /// <summary>表源说明 = 工作簿文件名 · sheet 名;未知表退回文件名,不抛。</summary>
    [Fact]
    public void SourceOfPrefixesTheWorkbookFileName()
    {
        var book = LoadRealWorkbook();

        Assert.Equal("official-workbook.xlsx · DRG", book.SourceOf(OfficialWorkbook.DrgTable));
        Assert.Equal(book.FileName, book.SourceOf("__NO_SUCH_TABLE__"));
        Assert.Equal("official-workbook.xlsx", book.FileName);
    }

    /// <summary>未知表 id 抛 <see cref="PackException"/>,消息里带上可用的表名(便于定位笔误)。</summary>
    [Fact]
    public void UnknownTableIdThrowsPackExceptionListingAvailableTables()
    {
        var book = LoadRealWorkbook();

        var ex = Assert.Throws<PackException>(() => book.Table("__NO_SUCH_TABLE__"));
        Assert.Contains("__NO_SUCH_TABLE__", ex.Message);
        Assert.Contains(OfficialWorkbook.DrgTable, ex.Message);
    }

    // ==========================================================================
    //  三、读不出来时:异常口径
    // ==========================================================================

    /// <summary>
    /// **工作簿损坏必须抛 <see cref="PackException"/>,不能是 <c>InvalidDataException</c>。**
    ///
    /// <para>解析失败在 <c>DRGX.Text</c> 层是 <c>InvalidDataException</c> —— 那一层不该认识"包"
    /// 这个概念,所以翻译由 <see cref="OfficialWorkbook.Load"/> 负责。这条契约有实际后果:
    /// 端点按 <c>catch (PackException)</c> 把"读不出工作簿"转成 404,启动路径按它给出友好报错。
    /// 翻译一旦丢了,5 个浏览端点会一起变成 500(无响应体),启动则直接抛未处理异常、打印一屏调用栈。</para>
    /// </summary>
    [Fact]
    public void CorruptWorkbookSurfacesAsPackExceptionNotInvalidDataException()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "official-workbook.xlsx");
        File.WriteAllText(path, "this is not a valid xlsx file");

        // Assert.Throws<T> 要求**精确**类型:若翻译丢了(抛 InvalidDataException),这里会红。
        var ex = Assert.Throws<PackException>(() => OfficialWorkbook.Load(tmp.Path));
        Assert.Contains("无法解析", ex.Message);
        Assert.Contains("official-workbook.xlsx", ex.Message);
    }

    /// <summary>目录不存在也是 <see cref="PackException"/>,且消息里给出期望的目录形状。</summary>
    [Fact]
    public void MissingDirectoryThrowsPackException()
    {
        var missing = Path.Combine(Path.GetTempPath(), "drgx-no-such-dir-" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<PackException>(() => OfficialWorkbook.Load(missing));
        Assert.Contains(missing, ex.Message);
    }

    /// <summary>目录里有两个工作簿时拒绝猜 —— 报出全部候选而不是随便挑一个。</summary>
    [Fact]
    public void TwoWorkbooksAreRefusedRatherThanGuessed()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "a.xlsx"), "x");
        File.WriteAllText(Path.Combine(tmp.Path, "b.xlsx"), "x");

        var ex = Assert.Throws<PackException>(() => OfficialWorkbook.Load(tmp.Path));
        Assert.Contains("a.xlsx", ex.Message);
        Assert.Contains("b.xlsx", ex.Message);
    }

    /// <summary>
    /// <see cref="OfficialWorkbook.HasWorkbook"/> 是"选不选官方轨"的判据,必须排除 Excel
    /// 打开中的 <c>~$xxx.xlsx</c> 临时文件 —— 否则正开着工作簿时会被判成"有官方工作簿",
    /// 随后读到半截文件。
    /// </summary>
    [Fact]
    public void HasWorkbookIgnoresExcelLockFilesAndEmptyDirectories()
    {
        using var empty = new TempDir();
        Assert.False(OfficialWorkbook.HasWorkbook(empty.Path));

        using var onlyLock = new TempDir();
        File.WriteAllText(Path.Combine(onlyLock.Path, "~$official-workbook.xlsx"), "x");
        Assert.False(OfficialWorkbook.HasWorkbook(onlyLock.Path));

        using var real = new TempDir();
        File.WriteAllText(Path.Combine(real.Path, "official-workbook.xlsx"), "x");
        Assert.True(OfficialWorkbook.HasWorkbook(real.Path));

        Assert.False(OfficialWorkbook.HasWorkbook(Path.Combine(empty.Path, "no-such-subdir")));
    }

    // --------------------------------------------------------------------------

    /// <summary>
    /// 真实工作簿只装载一次(缓存按 path + mtime + size + amendments 指纹失效),
    /// 故多个测试共享同一实例,不必各自付解析成本。
    /// </summary>
    private static OfficialWorkbook LoadRealWorkbook() =>
        OfficialWorkbook.Load(Path.Combine(RepoPaths.PackDirectory, "official"));

    private static OfficialTableData Table(string[] header, string[][] rows) =>
        new() { Header = header, Rows = rows };

    /// <summary>一次性临时目录,测试结束即删(内含工作簿的用例不必污染仓库)。</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "drgx-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* 临时目录清理失败不该让测试红 */ }
        }
    }
}
