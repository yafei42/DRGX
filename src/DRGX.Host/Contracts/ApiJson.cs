using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DRGX.Host;

/// <summary>
/// API JSON 序列化统一口径(单一事实源):camelCase、null 省略、中文直出、枚举名称化。
/// HttpJsonOptions(端点参数绑定/Results.Ok)与本地序列化(/api/group 结果组装)必须同参,
/// 此前 Program.cs 里两份手写配置已收敛到此处。
///
/// 【裁剪(self-contained + PublishTrimmed)契约 —— 改这里前必读】
/// 端点大量返回匿名类型(Result.Ok(new { ... })),匿名类型**无法**用 [JsonSerializable] 标注,
/// 所以本项目走不了"全量 source-gen"路线,AOT 也一并排除。裁剪态下必须显式挂上
/// <see cref="DefaultJsonTypeInfoResolver"/>:否则 HttpJsonOptions 的解析链为空
/// (异常原文 <c>JsonTypeInfo metadata for type '...' was not provided by TypeInfoResolver of type '[]'</c>),
/// 而该异常抛在 <c>CompositeEndpointDataSource</c> 的端点**构建**阶段 —— 表现是**任意一个**
/// 请求(连 / 和 /style.css 也是)全部 500,不是只有带 JSON 的那个接口坏。
/// 代价:只能 <c>TrimMode=partial</c>(框架被裁、宿主程序集不裁),不能用 full。
/// </summary>
internal static class ApiJson
{
    /// <summary>把统一口径应用到既有选项(HttpJsonOptions 的 SerializerOptions 是只读实例,只能逐项设置)。</summary>
    public static void Apply(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        // 裁剪态下 HttpJsonOptions 默认的解析链是空的(见类型注释),显式挂回去。
        // 必须用 TypeInfoResolverChain.**Add**,不能写 `o.TypeInfoResolver ??= ...`:
        // 裁剪态下该属性非 null,而是"空组合器",`??=` 会整条跳过,症状与没改一样。
        // 反射式 JSON 与 partial 裁剪共存:宿主程序集不裁,属性元数据因此完整。
        o.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());

        o.Converters.Add(new JsonStringEnumConverter()); // status/reason 以名称输出,便于人读
    }

    /// <summary>按统一口径新建选项(本地序列化用,如 /api/group 的结果节点组装)。</summary>
    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions();
        Apply(o);
        return o;
    }
}
