namespace DRGX.Text;

/// <summary>
/// 读文本文件的**唯一入口**:读字节 → 交给 <see cref="TextDecoder"/> 解码。
///
/// <para>为什么需要它:解码链(UTF-16/UTF-8 BOM → 严格 UTF-8 → GB18030/GBK)曾经只被
/// <c>DRGX.Engine</c> 与 <c>DRGX.Host</c> 使用,而 <c>DRGX.Fee.RegionPack</c> 有 4 处直接
/// <c>Csv.Read(File.ReadAllText(path))</c> —— 地区费用包是各地医保局导出的 CSV,GBK/GB18030
/// 极其常见,那 4 处在遇到非 UTF-8 时会乱码或抛异常,而同样的数据在 Engine 侧却完全正常。
/// 这是"公共代码只靠约定、没有结构上强制"的代价。</para>
///
/// <para>因此把「读文件 + 解码」收成唯一入口:绕过它的成本高于遵守它的成本。
/// 新增任何读文本文件的代码,一律走 <see cref="ReadText"/>,不要直接 <c>File.ReadAllText</c>。</para>
/// </summary>
public static class FileText
{
    /// <summary>读文件并按 <see cref="TextDecoder"/> 的回退链解码(UTF-16/UTF-8 BOM → 严格 UTF-8 → GB18030/GBK)。</summary>
    public static string ReadText(string path) => TextDecoder.Decode(File.ReadAllBytes(path));
}
