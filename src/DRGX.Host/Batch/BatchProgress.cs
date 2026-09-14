
using System.Text.Json;

namespace DRGX.Host;

/// <summary>批量分组进度的流式上报(opt-in)。
///
/// <para><b>为什么是"流式"而不是轮询作业</b>:耗时的两端都在同一个请求里 ——
/// 上传(大件 108MB 要几秒) 与 服务端逐行分组(10 万行实测 8~15 秒)。
/// 只要这个请求还没返回,浏览器就没有任何反馈;而把它改成"作业化(202 + 轮询)"
/// 要引入作业存储、临时文件、TTL 清理(见设计稿 §8 未决项),代价远大于收益。
/// 用 NDJSON 逐行写进度则不需要任何存储:请求照旧是一次 POST,只是响应体是分块到达的。</para>
///
/// <para><b>协议</b>:请求带 <c>progress=1</c> 时,响应 Content-Type 为
/// <c>application/x-ndjson</c>,逐行写:
/// <c>{"phase":"group","done":12000,"total":100000}</c> … 最后一行是
/// <c>{"done":true,"payload":{…与普通模式完全相同的 JSON…}}</c>。
/// 不带 <c>progress</c> 时<b>一切照旧</b>(单次 JSON),脚本与回归不受影响。</para>
/// </summary>
internal static class BatchProgress
{
    private static readonly JsonSerializerOptions Opts = ApiJson.Create();

    /// <summary>流式写完响应之后要返回的"空结果"。
    ///
    /// <para><b>不能用 <c>Results.Empty</c></b>:它会再设一次 <c>StatusCode = 200</c>,
    /// 而那时响应体已经写出去、响应已开始,于是抛
    /// <c>InvalidOperationException: StatusCode cannot be set because the response has already started</c>
    /// —— 表现为"进度行都到了,但最后的结果行永远不到"(实测踩到)。</para>
    /// </summary>
    public static readonly IResult Written = new WrittenResult();

    private sealed class WrittenResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
    }

    /// <summary>逐行进度上报。节流到"每 1% 或每 2000 行"一次:
    /// 10 万行若每行都写,就是 10 万次 flush —— 进度条顺滑度不会再提高,却把响应切成 10 万块。
    /// 写入失败(客户端断开)时静默忽略:进度写不出去不该让整批计算失败。</summary>
    public static async Task ReportAsync(HttpRequest req, bool enabled, int done, int total)
    {
        if (!enabled) return;
        var step = Math.Max(2000, total / 100);
        if (done % step != 0) return;
        await WriteLineAsync(req, new { phase = "group", done, total });
    }

    /// <summary>收尾:把最终结果作为最后一行写出。</summary>
    public static async Task FinalAsync(HttpRequest req, object payload) =>
        await WriteLineAsync(req, new { done = true, payload });

    /// <summary>开始流式响应前设头:必须显式去掉 Content-Length,否则 Kestrel 会缓冲整个响应
    /// 再发出 —— 那样进度行会"攒到最后一起到",进度条就成了摆设。
    ///
    /// <para><b>必须带 <c>HasStarted</c> 守卫</b>:响应体一开始写,响应头就变只读,再设任何头会抛
    /// <c>HeadersReadOnlyException</c>(表现为"进度行都到了,最后的结果行永远不到")。
    /// 本方法在读完表单后调用一次;写成幂等是为了防止日后有人顺手在收尾处再调一次。</para>
    /// </summary>
    public static void ApplyHeaders(HttpRequest req)
    {
        var res = req.HttpContext.Response;
        if (res.HasStarted) return;
        // 用 text/plain 而不是 application/x-ndjson:后者下浏览器不保证增量暴露 responseText,
        // 进度行会"攒到最后一起到"(NDJSON over text/plain 本就是通行做法)。
        res.ContentType = "text/plain; charset=utf-8";
        res.Headers.ContentLength = null;
    }

    private static async Task WriteLineAsync(HttpRequest req, object line)
    {
        var res = req.HttpContext.Response;
        var json = JsonSerializer.Serialize(line, Opts) + "\n";
        try
        {
            await res.WriteAsync(json);
            await res.Body.FlushAsync();
        }
        catch (Exception) when (req.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // 用户中途关掉页面/取消:进度写不出去是预期行为,不该冒泡成 500
        }
    }
}
