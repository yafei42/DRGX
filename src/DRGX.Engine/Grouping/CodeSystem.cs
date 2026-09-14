using System.Collections.Frozen;

namespace DRGX.Engine;

/// <summary>
/// 输入编码体系（界面上的「编码版本」切换）。
///
/// <para>为什么需要显式选择，而不是"有映射就转"的隐式兜底：两版之间**存在同码不同义**
/// （实测诊断 245 条 / 操作 4 条，如 A21.200、A49.100）—— 码串一模一样但两版含义不同，
/// 而映射表里恰好给了它一条记录。无条件转换会把医保版输入误转成别的码。选医保版时
/// 必须原样保留，选国临版时才转换。</para>
///
/// <para>分组判定恒在医保版口径上执行（CHS-DRG 3.0 分组方案的 MCC/CC、排除表、ADRG、DRG
/// 全部是医保版码），本枚举只决定**输入侧**的字典与转换策略。</para>
/// </summary>
public enum CodeSystem
{
    /// <summary>国家医保版（结算清单/分组方案基准口径）。输入即分组口径，不做任何转换。</summary>
    Yibao = 0,

    /// <summary>国家临床版（病案首页口径）。输入按国临字典受理，分组前经 code-maps 转医保版。</summary>
    Guolin = 1,
}

/// <summary>某一体（诊断 / 操作）某一版本的编码字典。只用于输入侧检索、精确查码与名称回显。</summary>
public sealed class CodeDictionary
{
    /// <summary>编码 → 名称（版本目录原文）。</summary>
    public required FrozenDictionary<string, string> Names { get; init; }

    public bool Contains(string code) => Names.ContainsKey(code);

    public static readonly CodeDictionary Empty = new()
    {
        Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>一个版本下的诊断 + 操作两张字典。</summary>
public sealed record CodeDictionaries(CodeDictionary Diagnoses, CodeDictionary Procedures)
{
    public static readonly CodeDictionaries Empty = new(CodeDictionary.Empty, CodeDictionary.Empty);
}
