using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Contracts;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// 入站端点共享小工具：请求体读入 <see cref="ArrayPool{T}"/>、按 Content-Encoding 解压、
/// 以及 InfluxDB 风格 <c>{ "error", "message" }</c> JSON 错误体回写。
/// 由 <see cref="InfluxLineProtocolEndpointHandler"/> 与
/// <see cref="PrometheusRemoteWriteEndpointHandler"/> 共用，避免逻辑漂移。
/// </summary>
internal static class EndpointIngestUtils
{
    /// <summary>初始 ArrayPool 缓冲大小。</summary>
    private const int _initialBufferSize = 4096;

    /// <summary>
    /// 把请求体读到 <see cref="ArrayPool{T}"/> 租借的字节缓冲。调用方在 finally 中归还。
    /// 如果 <paramref name="decompressGzip"/>=true 且请求带 <c>Content-Encoding: gzip</c>，
    /// 则解压后再返回；按 <c>Content-Length</c> 精确租借（如有）。
    /// </summary>
    /// <param name="ctx">HTTP 上下文。</param>
    /// <param name="decompressGzip">是否在 <c>Content-Encoding: gzip</c> 时进行解压。</param>
    /// <returns>租借的缓冲与有效字节数。空 body 返回 (rented, 0)。</returns>
    public static async Task<(byte[] Buffer, int Length)> ReadBodyAsync(HttpContext ctx, bool decompressGzip)
    {
        var stream = ctx.Request.Body;
        if (decompressGzip)
        {
            var encoding = ctx.Request.Headers.ContentEncoding.ToString();
            if (!string.IsNullOrEmpty(encoding)
                && encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
                return await ReadAllToPoolAsync(gz, ctx.RequestAborted).ConfigureAwait(false);
            }
        }

        if (ctx.Request.ContentLength is long len && len > 0 && len <= int.MaxValue)
        {
            int size = (int)len;
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            int offset = 0;
            try
            {
                while (offset < size)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(offset, size - offset), ctx.RequestAborted)
                        .ConfigureAwait(false);
                    if (n == 0) break;
                    offset += n;
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
            return (buffer, offset);
        }

        return await ReadAllToPoolAsync(stream, ctx.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// 从未知长度的 <see cref="Stream"/> 中读完所有字节到 <see cref="ArrayPool{T}"/> 租借的缓冲。
    /// 起始 4KB，按需翻倍。调用方负责在 finally 中归还。
    /// </summary>
    private static async Task<(byte[] Buffer, int Length)> ReadAllToPoolAsync(Stream stream, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(_initialBufferSize);
        int total = 0;
        try
        {
            while (true)
            {
                if (total == rented.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(rented.Length * 2);
                    Buffer.BlockCopy(rented, 0, bigger, 0, total);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = bigger;
                }
                int n = await stream.ReadAsync(rented.AsMemory(total, rented.Length - total), ct)
                    .ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
        return (rented, total);
    }

    /// <summary>
    /// 以 <c>application/json</c> 写出 <see cref="ErrorResponse"/>（<c>{ "error", "message" }</c>）。
    /// 若响应已经开始（<see cref="HttpResponse.HasStarted"/>），则跳过。
    /// </summary>
    public static async Task WriteJsonErrorAsync(HttpContext ctx, int statusCode, string code, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse, ctx.RequestAborted).ConfigureAwait(false);
    }
}
