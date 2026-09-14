namespace DRGX.Host;

/// <summary>
/// Web 宿主路径解析(此前 ResolvePackPath/ResolveFeePath/ResolveHisPath 三份近似实现收敛于此)。
///
/// <para><b>为什么需要多个候选根:</b> <c>dotnet run</c> 会把子进程的**工作目录设成工程目录**
/// (不是调用者的 CWD),而 <c>start-web.bat</c> / README 里的手动命令都用 <c>dotnet run</c>。
/// 此时 <c>--pack data\packs\chs-drg-3.0</c>、默认的 <c>plugins</c> 与 <c>data/regions</c>
/// 这些相对路径若只按 CWD 解释,会落到 <c>src\DRGX.Host\</c> 下 —— 于是数据包/插件/地区包
/// 全部找不到,而报错只表现为「未加载任何插件」「地区包为空」,极难联想到是工作目录问题。</para>
/// </summary>
internal static class WebPaths
{
    /// <summary>仓库根标记:向上逐级查找该文件以定位仓库根(开发态)。</summary>
    private const string RepoMarker = "DRGX.slnx";

    /// <summary>上溯层数上限,防止在异常路径下无界循环。</summary>
    private const int MaxUpLevels = 8;

    /// <summary>
    /// 相对路径解析:按候选根依次尝试,命中即返回。
    /// isFile=false 找目录,=true 找文件;全部落空回退 CWD 相对解释,由后续读取自行报错。
    /// </summary>
    public static string Resolve(string path, string? packPath = null, bool isFile = false)
    {
        if (Path.IsPathRooted(path)) return path;
        foreach (var root in CandidateRoots(packPath))
        {
            var candidate = Path.GetFullPath(Path.Combine(root, path));
            if (isFile ? File.Exists(candidate) : Directory.Exists(candidate)) return candidate;
        }
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// 逐个尝试一组相对路径,返回第一个**真实存在**的目录;都不存在时回退第一个的解析结果。
    ///
    /// <para>用途是让同一份代码兼容两种发布形态:发布包把插件放在 exe 同级的
    /// <c>plugins</c>(没有 artifacts 这一层),开发态则由插件工程直接输出到
    /// <c>artifacts/plugins</c>。只认其中一个,另一种形态下就会静默加载 0 个插件。</para>
    /// </summary>
    public static string ResolveAny(IEnumerable<string> paths, string? packPath = null)
    {
        var first = string.Empty;
        foreach (var path in paths)
        {
            var resolved = Resolve(path, packPath);
            if (first.Length == 0) first = resolved;
            if (Directory.Exists(resolved)) return resolved;
        }
        return first;
    }

    /// <summary>
    /// 候选根目录,就近优先:
    /// <list type="number">
    ///   <item>进程当前目录(<c>dotnet run</c> 下是工程目录);</item>
    ///   <item>程序集所在目录(单文件发布:数据/插件与 exe 同级);</item>
    ///   <item><b>数据包目录及其各级上级</b> —— 开发态仓库根就在其中。<b>刻意不写死「上两级」</b>:
    ///         目录布局调整过(如 <c>packs/</c> 挪到 <c>data/packs/</c>)之后,
    ///         按固定层数推导会静默失效,而症状只是"东西没加载",很难定位;</item>
    ///   <item>从当前目录与程序集目录向上找到的仓库根(以 <see cref="RepoMarker"/> 为标记)
    ///         —— 覆盖「<c>--pack</c> 本身也是相对路径」的情形。</item>
    /// </list>
    /// </summary>
    private static IEnumerable<string> CandidateRoots(string? packPath)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string full;
            try { full = Path.GetFullPath(dir); } catch { return; }
            if (seen.Add(full)) roots.Add(full);
        }

        Add(Directory.GetCurrentDirectory());
        Add(AppContext.BaseDirectory);

        // 数据包目录及其上级(逐级上溯,不假设固定层级)
        if (!string.IsNullOrWhiteSpace(packPath))
        {
            DirectoryInfo? d = null;
            try { d = new DirectoryInfo(Path.GetFullPath(packPath)); } catch { /* 路径非法则跳过 */ }
            for (var i = 0; d is not null && i < MaxUpLevels; i++, d = d.Parent)
                Add(d.FullName);
        }

        // 仓库根标记上溯
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? d = null;
            try { d = new DirectoryInfo(start); } catch { continue; }
            for (var i = 0; d is not null && i < MaxUpLevels; i++, d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, RepoMarker))) { Add(d.FullName); break; }
            }
        }

        return roots;
    }

    /// <summary>静态资源内容根:发布态在 exe 同级;开发态依次回退 CWD → exe 同级 → 源工程目录,保证页面可访问。</summary>
    public static string ResolveContentRoot()
    {
        var cwd = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(cwd, "wwwroot"))) return cwd;
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            // bin/{cfg}/{tfm}/ → 上溯 3 级即工程目录(bin→Debug→工程)
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot")),
        })
        {
            if (Directory.Exists(candidate))
                return Path.GetFullPath(Path.Combine(candidate, ".."));
        }
        return cwd;
    }
}
