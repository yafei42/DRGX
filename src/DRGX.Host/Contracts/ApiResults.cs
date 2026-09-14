namespace DRGX.Host;

/// <summary>
/// 错误响应统一出口。全站契约:
///   * 业务/校验错误(4xx) → { "error": "..." }(前端统一读 error 字段,见 js/dom.js 的 readJson);
///   * 基础设施故障(5xx:HIS 连接器未加载/提取失败、CSV 处理异常、地区费用包未加载)
///     → Results.Problem(title + detail),与业务错误区分开。
/// </summary>
internal static class ApiResults
{
    public static IResult BadRequest(string message) => Results.BadRequest(new { error = message });

    public static IResult NotFound(string message) => Results.NotFound(new { error = message });

    /// <summary>413:请求体/文件超过上限。与 400 分开,便于前端提示"文件太大"而不是"格式错误"。</summary>
    public static IResult TooLarge(string message) =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "内容过大", detail: message);
}
