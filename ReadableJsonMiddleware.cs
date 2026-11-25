using System.Text.Encodings.Web;
using System.Text.Json;

namespace AuthBaseXiHan;

/// <summary>
/// JSON 响应中文直出中间件：把响应体里的 \uXXXX 转义还原为 UTF-8 明文
/// </summary>
/// <remarks>
/// 背景：XiHan 框架（rc.6 起，rc.7 仍如此）写 JSON（ProblemDetails 用 Utf8JsonWriter、操作响应用源生成上下文）
/// 都走默认 Encoder，非 ASCII 字符一律转义成 \uXXXX——这是合法 JSON，客户端 JSON.parse
/// 后内容完全正确，只是调试视图（Scalar、curl）里不可读；框架未暴露 Encoder 配置口，
/// 故在响应边缘统一做一次「JSON 解析 + 宽松编码器重写」。
///
/// 实现：用包装流接管 Response.Body，写入先进缓冲；请求走完后按响应内容类型决定——
/// JSON（application/json 或 *+json）经 JsonDocument 解析后用 UnsafeRelaxedJsonEscaping
/// 重写（该编码器只转义 HTML 敏感字符，中文直接输出；由解析器保证还原正确性，含代理
/// 对与字符串里的字面 \u），其余内容原样透传。
///
/// 关键点：框架用 Response.HasStarted 判断端点是否已写过响应（成功响应只写一次、失败
/// 才写 ProblemDetails）。缓冲会让 HasStarted 恒为 false、框架把成功响应写两遍，因此
/// 首次写入缓冲时必须先清掉 Content-Length 并调用 StartAsync，与不缓冲时的语义对齐。
/// </remarks>
public sealed class ReadableJsonMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        // 换上包装流：内核管道与端点都写进缓冲
        var originalBody = context.Response.Body;
        await using var buffering = new BufferingStream(context.Response, originalBody);
        context.Response.Body = buffering;
        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var buffer = buffering.Content;
        if (buffer.Length == 0 || !IsJson(context.Response.ContentType))
        {
            // 空响应（204 等）或非 JSON 内容：原样透传
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
            return;
        }

        // 解析再重写：默认编码器写出的 \uXXXX 是合法 JSON，解析后用宽松编码器直出中文
        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: context.RequestAborted);
        await using (var writer = new Utf8JsonWriter(
            originalBody,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            document.WriteTo(writer);
        }

        await originalBody.FlushAsync(context.RequestAborted);
    }

    /// <summary>内容类型是否 JSON（application/json 或 +json 结尾的子类型，如 application/problem+json）</summary>
    private static bool IsJson(string? contentType) =>
        contentType is not null &&
        (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
         || contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 响应缓冲流：写入先进内存缓冲，首写与 flush 时把响应头真正发出去
    /// </summary>
    /// <remarks>
    /// 框架用 Response.HasStarted 判定端点是否已写过响应；HttpResponse.StartAsync 内部是
    /// flush 响应流触发发送响应头，而缓冲流的 flush 不会到达 Kestrel，HasStarted 会恒为
    /// false、成功响应被框架写两遍。因此首写时直接 flush 原始流（空 flush 只发响应头），
    /// 与不缓冲时的启动语义对齐。
    /// </remarks>
    private sealed class BufferingStream(HttpResponse response, Stream originalBody) : Stream
    {
        private readonly MemoryStream _buffer = new();
        private bool _started;

        /// <summary>缓冲的内容，请求管道走完后按内容类型决定透传或重写</summary>
        public MemoryStream Content => _buffer;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            if (!_started)
            {
                await StartResponseAsync(cancellationToken);
            }

            await _buffer.WriteAsync(source, cancellationToken);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!_started)
            {
                await StartResponseAsync(cancellationToken);
            }
        }

        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>发送响应头：显式 Content-Length 是按转义后的字节数算的，重写后长度会变，先清掉改用分块</summary>
        private async Task StartResponseAsync(CancellationToken cancellationToken)
        {
            _started = true;
            response.Headers.ContentLength = null;
            // 空 flush 只发送响应头（正文仍在缓冲里），Kestrel 据此置起 HasStarted
            await originalBody.FlushAsync(cancellationToken);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
