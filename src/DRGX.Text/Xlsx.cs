using Sylvan.Data.Excel;

namespace DRGX.Text;

// ============================================================================
// XlsxWorkbook — 只读 xlsx 工作簿解析(Sylvan.Data.Excel,MIT)
//
// 为什么这里用 Sylvan 而不是自研 OOXML 解析:
//   * 全仓库的 xlsx 只应有<b>一处</b>口径。曾经是「Host 批量导入用 Sylvan +
//     DRGX.Text 自研 ZipArchive/XmlReader」两套并存,292 行手写解析只有官方工作簿一个调用点,
//     两套实现各自维护、各自踩坑(列位移、共享字符串、关系解析);
//   * Sylvan 前向逐行读,不把工作表建成对象模型,内存量级与手写流式实现相当;
//   * 它已是 Host 的既有依赖(Sylvan.Data.Excel 0.5.8),统一后发布物不新增文件。
//
// 边界约定:本类只做「字节流 → 结构化文本」,不做任何文件 IO(与 DRGX.Text 的定位一致)。
// 打开文件由调用方负责 —— 见 DRGX.Engine 的 OfficialWorkbook。
// ============================================================================

/// <summary>工作簿里的一行。<paramref name="Number"/> 是 Excel 行号(1 起,与界面上看到的一致)。</summary>
public readonly record struct XlsxRow(int Number, string[] Cells);

/// <summary>只读工作簿。持有整份字节(见 <see cref="Open"/>),按 sheet 名随机读。</summary>
public sealed class XlsxWorkbook
{
    private readonly byte[] _bytes;

    public IReadOnlyList<string> SheetNames { get; }

    private XlsxWorkbook(byte[] bytes, IReadOnlyList<string> names)
    {
        _bytes = bytes;
        SheetNames = names;
    }

    /// <summary>打开一个 xlsx 字节流,由调用方负责关闭 <paramref name="stream"/>。</summary>
    public static XlsxWorkbook Open(Stream stream)
    {
        // 一次性读入内存:Sylvan 是前向流、不能回退,而调用方要按 sheet 名随机读多张表。
        // 官方工作簿 3MB 量级,比反复解压划算;批量导入路径另有 GuardXlsx 限流,不走这里。
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var names = new List<string>();
        using (var edr = Create(bytes))
        {
            do { names.Add(edr.WorksheetName ?? ""); } while (edr.NextResult());
        }
        if (names.Count == 0) throw new InvalidDataException("xlsx: 工作簿里没有任何 sheet");
        return new XlsxWorkbook(bytes, names);
    }

    /// <summary>
    /// 读整张 sheet。<paramref name="columns"/> 为取值宽度(超出部分丢弃,不足的行补空串)——
    /// 由调用方按自己的列结构给定,读表方无需关心稀疏单元格。
    /// </summary>
    public List<XlsxRow> ReadSheet(string name, int columns)
    {
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        using var edr = Create(_bytes);
        do
        {
            if (string.Equals(edr.WorksheetName, name, StringComparison.Ordinal)) return ReadRows(edr, columns);
        } while (edr.NextResult());

        throw new InvalidDataException($"xlsx: 找不到 sheet \"{name}\"(实有: {string.Join(" / ", SheetNames)})");
    }

    /// <summary>
    /// 读当前工作表。<b>下标 0 必须是表头行</b>:Sylvan 默认把首行当列名吃掉
    /// (<c>Read()</c> 从 RowNumber=2 开始,首行文本改由 <c>GetName(i)</c> 提供),
    /// 这里显式把首行还原成 rows[0] —— 官方工作簿是按表头<b>文字</b>定位列的,
    /// 少了首行会把第一行数据当表头,整表列全部映射不上。
    /// </summary>
    private static List<XlsxRow> ReadRows(ExcelDataReader edr, int columns)
    {
        var rows = new List<XlsxRow>();
        var width = Math.Min(edr.FieldCount, columns);
        if (width > 0)
        {
            var header = new string[columns];
            for (var i = 0; i < width; i++) header[i] = edr.GetName(i) ?? "";
            rows.Add(new XlsxRow(1, header));
        }
        while (edr.Read())
        {
            var cells = new string[columns];
            var n = Math.Min(edr.RowFieldCount, columns);
            for (var i = 0; i < n; i++) cells[i] = edr.GetString(i) ?? "";
            rows.Add(new XlsxRow(edr.RowNumber, cells));
        }
        return rows;
    }

    private static ExcelDataReader Create(byte[] bytes) =>
        ExcelDataReader.Create(new MemoryStream(bytes, writable: false), ExcelWorkbookType.ExcelXml);
}
