namespace DRGX.Tests;

/// <summary>
/// 仓库内定位。测试要从 <c>data/packs/chs-drg-3.0</c> 读真实数据包,从
/// <c>tests/DRGX.Tests/cases</c> 读用例基线,而 <c>dotnet test</c> 的工作目录是
/// <c>bin/&lt;cfg&gt;/&lt;tfm&gt;</c> —— 因此一律从程序目录向上找仓库根,不依赖当前目录。
///
/// <para>向上找的锚点是 <c>DRGX.slnx</c>:它是仓库根的独有标记,且与数据包无关,
/// 不会因为某个数据包被改名或删掉而让定位悄悄落到别处。</para>
/// </summary>
internal static class RepoPaths
{
    /// <summary>仓库根(含 DRGX.slnx 的那一级)。</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>默认数据包目录(data/packs/chs-drg-3.0)。</summary>
    public static string PackDirectory => Path.Combine(Root, "data", "packs", "chs-drg-3.0");

    /// <summary>用例基线目录(生成器写、测试读;两者必须是同一个目录)。</summary>
    public static string CaseDirectory => Path.Combine(Root, "tests", "DRGX.Tests", "cases");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DRGX.slnx")))
                return dir.FullName;

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到 DRGX.slnx,无法定位仓库根。" +
            "测试必须在仓库内运行(dotnet test tests/DRGX.Tests)。");
    }
}
