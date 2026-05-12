using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Ingest;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// InfluxDB 兼容的 Line Protocol 写入端点处理器。
/// <list type="bullet">
/// <item><c>POST /write</c>（InfluxDB v1）：参数 <c>db</c>、<c>precision</c>。</item>
/// <item><c>POST /api/v2/write</c>（InfluxDB v2）：参数 <c>bucket</c>、<c>org</c>、<c>precision</c>。</item>
/// </list>
/// 与 <see cref="BulkIngestEndpointHandler"/> 路径下的 <c>/v1/db/{db}/measurements/{m}/lp</c> 不同，
/// 本端点 <b>不</b> 强制 measurement，<see cref="LineProtocolReader"/> 会从每行 LP 自行解析 measurement，
/// 与 InfluxDB 协议语义一致，可直接对接 Telegraf / EMQX / Prometheus influxdb_v2 output 等生态工具。
/// </summary>
internal static class InfluxLineProtocolEndpointHandler
{
    /// <summary>InfluxDB 写端点的协议版本。</summary>
    public enum ApiVersion
    {
        /// <summary>v1：<c>POST /write?db=&amp;precision=</c>。</summary>
        V1,

        /// <summary>v2：<c>POST /api/v2/write?bucket=&amp;org=&amp;precision=</c>。</summary>
        V2,
    }

    /// <summary>
    /// 处理一次 InfluxDB Line Protocol 写入请求。
    /// 成功返回 <c>204 No Content</c>（InfluxDB 约定），错误以 <c>{"error","message"}</c> 形式返回。
    /// </summary>
    /// <param name="ctx">HTTP 上下文。</param>
    /// <param name="registry">Tsdb 注册表。</param>
    /// <param name="grants">授权存储。</param>
    /// <param name="metrics">服务端度量计数器。</param>
    /// <param name="version">协议版本（v1 或 v2）。</param>
    public static async Task HandleAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        ServerMetrics metrics,
        ApiVersion version)
    {
        // 1) 数据库名解析：v1 → ?db；v2 → ?bucket
        var dbParam = version == ApiVersion.V1 ? "db" : "bucket";
        var dbName = ctx.Request.Query[dbParam].ToString();
        if (string.IsNullOrWhiteSpace(dbName))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"缺少查询参数 '{dbParam}'。").ConfigureAwait(false);
            return;
        }
        if (!TsdbRegistry.IsValidName(dbName))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法数据库名 '{dbName}'。").ConfigureAwait(false);
            return;
        }
        if (!registry.TryGet(dbName, out var tsdb))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found",
                $"数据库 '{dbName}' 不存在。").ConfigureAwait(false);
            return;
        }

        // 2) 写权限校验
        var perm = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, dbName);
        if (!DatabaseAccessEvaluator.HasPermission(perm, DatabasePermission.Write))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden",
                $"当前凭据对数据库 '{dbName}' 没有 write 权限。").ConfigureAwait(false);
            return;
        }

        // 3) precision 解析（InfluxDB 默认 ns；接受 n / ns / u / us / µs / ms / s）
        var precision = ParsePrecision(ctx.Request.Query["precision"].ToString());

        // 4) 读取请求体（支持 Content-Encoding: gzip），ArrayPool 复用以避免 LOH。
        byte[]? bodyBuffer = null;
        int bodyLength = 0;
        char[]? charBuffer = null;
        try
        {
            (bodyBuffer, bodyLength) = await ReadBodyAsync(ctx).ConfigureAwait(false);

            // 空 body：InfluxDB 仍返回 204（与官方 /write 行为一致）。
            if (bodyLength == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // 5) UTF-8 → char[]（ArrayPool）
            int maxChars = Encoding.UTF8.GetMaxCharCount(bodyLength);
            charBuffer = ArrayPool<char>.Shared.Rent(maxChars);
            int charCount = Encoding.UTF8.GetChars(
                new ReadOnlySpan<byte>(bodyBuffer, 0, bodyLength),
                charBuffer);

            // 6) 走通用 BulkIngestor。measurementOverride: null → 由 LineProtocolReader 解析每行 measurement。
            var reader = new LineProtocolReader(
                new ReadOnlyMemory<char>(charBuffer, 0, charCount),
                precision: precision,
                measurementOverride: null);

            BulkIngestResult result;
            try
            {
                result = BulkIngestor.Ingest(tsdb, reader, BulkErrorPolicy.FailFast, BulkFlushMode.None);
            }
            catch (BulkIngestException ex)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bulk_ingest_error", ex.Message).ConfigureAwait(false);
                return;
            }

            metrics.AddInsertedRows(result.Written);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        finally
        {
            if (charBuffer is not null)
                ArrayPool<char>.Shared.Return(charBuffer);
            if (bodyBuffer is not null)
                ArrayPool<byte>.Shared.Return(bodyBuffer);
        }
    }

    /// <summary>
    /// 解析 InfluxDB 风格的 <c>precision</c> 查询参数。
    /// </summary>
    /// <param name="value">原始查询字符串。</param>
    /// <returns>对应的 <see cref="TimePrecision"/>；缺省返回 <see cref="TimePrecision.Nanoseconds"/>（InfluxDB 默认）。</returns>
    private static TimePrecision ParsePrecision(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return TimePrecision.Nanoseconds;
        // InfluxDB v1 使用 "n" 作为纳秒别名；v2 使用 "ns"。两者兼容。
        return value switch
        {
            "ns" or "n" => TimePrecision.Nanoseconds,
            "us" or "u" or "µs" => TimePrecision.Microseconds,
            "ms" => TimePrecision.Milliseconds,
            "s" => TimePrecision.Seconds,
            _ => TimePrecision.Nanoseconds,
        };
    }

    /// <summary>
    /// 读取请求体到 <see cref="ArrayPool{T}"/> 租借的缓冲区。
    /// 自动处理 <c>Content-Encoding: gzip</c>。返回的 byte[] 由调用方在 finally 中归还。
    /// </summary>
    private static async Task<(byte[] Buffer, int Length)> ReadBodyAsync(HttpContext ctx)
    {
        var stream = ctx.Request.Body;
        var encoding = ctx.Request.Headers.ContentEncoding.ToString();
        bool gzip = !string.IsNullOrEmpty(encoding)
            && encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);

        if (gzip)
        {
            // gzip 解压后长度未知，使用 4KB 起步、按需翻倍的池缓冲读完整段流。
            using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            return await ReadAllToPoolAsync(gz, ctx.RequestAborted).ConfigureAwait(false);
        }

        // 非压缩：优先按 Content-Length 一次性精确租借。
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

    /// <summary>从未知长度的流中读完所有字节到 ArrayPool 租借的缓冲区，按需翻倍扩容。</summary>
    private static async Task<(byte[] Buffer, int Length)> ReadAllToPoolAsync(Stream stream, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(4096);
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

    private static async Task WriteErrorAsync(HttpContext ctx, int statusCode, string code, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse, ctx.RequestAborted).ConfigureAwait(false);
    }
}
