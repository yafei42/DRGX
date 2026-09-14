using System.Text;

namespace DRGX.Text;

/// <summary>
/// RFC4180 风格 CSV 读取:引号字段、"" 转义、CRLF/LF/CR 行尾、字段内换行。
/// 纯函数、无 IO、线程安全;全仓库唯一实现。
/// </summary>
public static class Csv
{
    /// <summary>解析为「行 → 字段」列表。空文本返回 0 行;末尾分隔符按规范表示一个空字段。</summary>
    public static List<List<string>> Read(string text, char delimiter = ',')
    {
        var rows = new List<List<string>>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            var fields = new List<string>();
            while (true)
            {
                fields.Add(ReadField(text, ref i, delimiter, out bool rowEnded));
                if (rowEnded) break;
            }
            rows.Add(fields);
        }
        return rows;
    }

    /// <summary>同 <see cref="Read"/>,但每行以数组返回(便于按列索引取值)。</summary>
    public static List<string[]> ReadRows(string text, char delimiter = ',') =>
        Read(text, delimiter).Select(r => r.ToArray()).ToList();

    private static string ReadField(string s, ref int i, char delim, out bool rowEnded)
    {
        rowEnded = false;
        int n = s.Length;
        if (i >= n) { rowEnded = true; return ""; }

        var sb = new StringBuilder();
        if (s[i] == '"')
        {
            i++; // 跳过开引号
            while (i < n)
            {
                char c = s[i];
                if (c == '"')
                {
                    if (i + 1 < n && s[i + 1] == '"') { sb.Append('"'); i += 2; }
                    else { i++; break; }
                }
                else { sb.Append(c); i++; }
            }
            if (i < n && s[i] == delim) i++;
            else if (i < n && s[i] is '\n' or '\r') { SkipNewline(s, ref i); rowEnded = true; }
            else if (i >= n) rowEnded = true;
            return sb.ToString();
        }

        var consumedDelim = false;
        while (i < n)
        {
            char c = s[i];
            if (c == delim) { i++; consumedDelim = true; break; }
            if (c is '\n' or '\r') { SkipNewline(s, ref i); rowEnded = true; break; }
            sb.Append(c);
            i++;
        }
        // 消费过分隔符时行未结束(即便已到文末):末尾分隔符按 RFC4180 语义表示一个空字段
        if (!consumedDelim && i >= n) rowEnded = true;
        return sb.ToString();
    }

    /// <summary>跳过 CRLF / LFCR 这对换行符(两种顺序都吞掉,避免多出一个空行)。</summary>
    private static void SkipNewline(string s, ref int i)
    {
        char first = s[i];
        i++;
        if (i < s.Length && s[i] != first && s[i] is '\n' or '\r') i++;
    }
}
