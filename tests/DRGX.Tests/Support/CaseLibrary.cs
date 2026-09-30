using System.Text;

namespace DRGX.Tests;

/// <summary>
/// 用例基线的读写。三个文件,各司其职:
///
/// <list type="bullet">
/// <item><c>witnesses.jsonl</c> —— 正例:每个可达 DRG 组(及 21 个 QY 歧义组)一条经引擎验证的病案。</item>
/// <item><c>boundary.jsonl</c>  —— 边界探针:由 witness 派生,冻结"阈值两侧/去掉关键事实"后的落点。</item>
/// <item><c>unreachable.txt</c> —— 造不出 witness 的组 + 原因,门禁据此把它们从可达目标里剔除
/// (剔除是**声明式**的:每条都必须写清为什么,不接受"反推失败"这种含糊理由)。</item>
/// </list>
///
/// <para>写入一律 LF、UTF-8 无 BOM、按 Id 排序 —— 基线文件是要提交进仓库的,
/// 顺序不稳定会让 diff 变成整文件重写,审阅时看不出真正改了什么。</para>
/// </summary>
internal static class CaseLibrary
{
    public const string WitnessFile = "witnesses.jsonl";
    public const string BoundaryFile = "boundary.jsonl";
    public const string UnreachableFile = "unreachable.txt";

    public static IReadOnlyList<TestCase> LoadWitnesses() =>
        ReadCases(Path.Combine(RepoPaths.CaseDirectory, WitnessFile));

    public static IReadOnlyList<TestCase> LoadBoundary() =>
        ReadCases(Path.Combine(RepoPaths.CaseDirectory, BoundaryFile));

    public static IReadOnlyList<(string Code, string Reason)> LoadUnreachable()
    {
        var path = Path.Combine(RepoPaths.CaseDirectory, UnreachableFile);
        if (!File.Exists(path)) return [];

        var rows = new List<(string, string)>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[1].Trim().Length == 0)
                throw new FormatException($"{UnreachableFile}: 每行须为「组码 <TAB> 原因」,实际为 \"{line}\"");
            rows.Add((parts[0].Trim(), parts[1].Trim()));
        }
        return rows;
    }

    public static IReadOnlyList<TestCase> ReadCases(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"用例基线缺失: {path}。重新生成: DRGX_WRITE_CASES=1 dotnet test tests/DRGX.Tests --filter Category=Regen",
                path);

        var name = Path.GetFileName(path);
        var cases = new List<TestCase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int lineNo = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (line.Trim().Length == 0) continue;
            var c = CaseJson.Deserialize(line, name, lineNo);
            if (!seen.Add(c.Id))
                throw new FormatException($"{name}:{lineNo}: 用例 Id 重复 \"{c.Id}\"");
            cases.Add(c);
        }
        return cases;
    }

    public static void WriteWitnesses(IEnumerable<TestCase> cases) =>
        WriteCases(Path.Combine(RepoPaths.CaseDirectory, WitnessFile), cases);

    public static void WriteBoundary(IEnumerable<TestCase> cases) =>
        WriteCases(Path.Combine(RepoPaths.CaseDirectory, BoundaryFile), cases);

    public static void WriteUnreachable(IEnumerable<(string Code, string Reason)> rows)
    {
        Directory.CreateDirectory(RepoPaths.CaseDirectory);
        var sb = new StringBuilder();
        sb.Append("# 造不出 witness 的组:门禁把它们从「可达目标」剔除。格式:组码 <TAB> 原因\n");
        sb.Append("# 原因是给人读的证据(哪条规则与哪条规则矛盾、被哪个兄弟组截获),不是占位符。\n");
        foreach (var (code, reason) in rows.OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase))
            sb.Append(code).Append('\t').Append(reason).Append('\n');
        WriteUtf8Lf(Path.Combine(RepoPaths.CaseDirectory, UnreachableFile), sb.ToString());
    }

    private static void WriteCases(string path, IEnumerable<TestCase> cases)
    {
        Directory.CreateDirectory(RepoPaths.CaseDirectory);
        var sb = new StringBuilder();
        foreach (var c in cases.OrderBy(c => c.Id, StringComparer.Ordinal))
            sb.Append(CaseJson.Serialize(c)).Append('\n');
        WriteUtf8Lf(path, sb.ToString());
    }

    private static void WriteUtf8Lf(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
