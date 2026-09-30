namespace DRGX.Tests;

/// <summary>
/// 用例基线的进程内索引。
///
/// <para><b>为什么不在每个测试里各自读盘。</b>基线文件加起来三千多行,而 xunit 会为
/// 理论数据的每一行建一个测试实例;若每个实例都重新解析整个文件,光是 JSON 解析就会成为
/// 全套测试的墙钟主导项。这里读一次、建索引,后续按 Id 取。</para>
///
/// <para><b>为什么理论数据只传 Id(字符串)而不是整个用例对象。</b>xunit 2.x 会把理论数据行
/// 序列化进测试用例标识;非 <see cref="IXunitSerializable"/> 的复杂对象会让它在发现期
/// 选择"运行时再枚举",于是 3200 条用例会塌成 1 条 —— 报告里就看不到"每个组一条"了。
/// 传字符串则每行都是独立、可筛选、可定位的测试用例。</para>
/// </summary>
internal static class CaseStore
{
    private static readonly Lazy<IReadOnlyList<TestCase>> WitnessesLazy = new(CaseLibrary.LoadWitnesses);
    private static readonly Lazy<IReadOnlyList<TestCase>> BoundaryLazy = new(CaseLibrary.LoadBoundary);

    public static IReadOnlyList<TestCase> Witnesses => WitnessesLazy.Value;
    public static IReadOnlyList<TestCase> Boundary => BoundaryLazy.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, TestCase>> WitnessIndex =
        new(() => Witnesses.ToDictionary(c => c.Id, StringComparer.Ordinal));

    private static readonly Lazy<IReadOnlyDictionary<string, TestCase>> BoundaryIndex =
        new(() => Boundary.ToDictionary(c => c.Id, StringComparer.Ordinal));

    /// <summary>理论数据:witness 用例 Id(已排序,保证测试名顺序稳定)。</summary>
    public static IEnumerable<object[]> WitnessIds =>
        Witnesses.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).Select(id => new object[] { id });

    /// <summary>理论数据:边界探针 Id(已排序)。</summary>
    public static IEnumerable<object[]> BoundaryIds =>
        Boundary.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).Select(id => new object[] { id });

    public static TestCase Witness(string id) =>
        WitnessIndex.Value.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"witness 用例不存在: {id}");

    public static TestCase BoundaryCase(string id) =>
        BoundaryIndex.Value.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"边界用例不存在: {id}");
}
