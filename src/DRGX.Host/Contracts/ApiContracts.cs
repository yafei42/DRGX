using DRGX.Engine;

namespace DRGX.Host;

// ============================================================================
// API 请求/响应契约(端点 DTO):字典检索、分组、数据包元数据。
// ============================================================================

/// <summary>字典条目:编码 + 名称 + 分组相关标记(诊断 Valid 为 null,序列化时省略;Robot 仅手术码使用)。
///
/// <para><c>Code</c> 恒为**所选版本的原始录入码**(国临版模式下就是国临码),前端提交它、
/// 由引擎负责转换;<c>MappedTo</c> 只是给下拉/结果看的"会转成谁"。</para></summary>
internal sealed record DictEntry(
    string Code,
    string Name,
    bool Blocked,
    bool? Valid,
    string? Comp = null,
    string? MappedTo = null,
    string? MappedToName = null,
    bool? Robot = null,
    /// <summary>该码经版本转换后没有医保版出口(国临独有且无映射)。前端据此在芯片上给"无医保对应"告警。</summary>
    bool? Unmapped = null);

internal sealed record NamesRequest(IReadOnlyList<string>? Codes);

internal sealed record GroupRequest(
    string? Gender,
    int? Age,
    int? AgeDay,
    int? Weight,
    string? MainDiagnosis,
    IReadOnlyList<string>? OtherDiagnoses,
    string? MainProcedure,
    IReadOnlyList<string>? OtherProcedures,
    bool? Verbose,
    /// <summary>输入编码版本:"guolin"(默认) | "yibao"。取代旧的 UseCodeMap 开关。</summary>
    string? Version = null,
    string? Region = null,
    /// <summary>已废弃:老客户端仍传。false 等价 version=yibao(不转换),true/缺省走默认版本。</summary>
    bool? UseCodeMap = null);

/// <summary>ADRG 原始条件描述(来自官方 xlsx 提取的记录)。</summary>
internal sealed record AdrgInfo(string Name, string MdcCode, string MdcName, string Cond, int Dx, int Proc);

/// <summary>
/// 「编码版本」形参解析:界面上是一个国临版/医保版二选一,落到 API 上统一走 <c>version</c>。
///
/// <para>旧参数 <c>useCodeMap</c> 保留兼容(老客户端与 HIS 插件仍在传):
/// <c>false</c> 的原语义是"不转换",恰好等价于医保版;<c>true</c>/缺省按默认版本走。
/// 两者同时出现时以 <c>version</c> 为准。</para>
/// </summary>
internal static class CodeSystemParam
{
    /// <summary>界面默认版本。与历史默认「转换医保版编码 = 勾选」等价,换成医保版会让
    /// 4,418 个国临独有码突然检索不到,是行为回退。</summary>
    public const CodeSystem Default = CodeSystem.Guolin;

    public static CodeSystem Parse(string? version, bool? legacyUseCodeMap = null)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version.Trim().ToLowerInvariant() switch
            {
                "guolin" or "gl" or "clinical" or "国临" or "国临版" => CodeSystem.Guolin,
                "yibao" or "yb" or "insurance" or "医保" or "医保版" => CodeSystem.Yibao,
                _ => Default,
            };
        }
        return legacyUseCodeMap is false ? CodeSystem.Yibao : Default;
    }
}
