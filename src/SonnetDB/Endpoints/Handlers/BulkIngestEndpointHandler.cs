using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Ingest;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// 三个批量入库端点的统一处理器：
/// <c>POST /v1/db/{db}/measurements/{m}/lp</c>（Line Protocol）、
/// <c>POST /v1/db/{db}/measurements/{m}/json</c>（JSON points）、
/// <c>POST /v1/db/{db}/measurements/{m}/bulk</c>（INSERT VALUES 快路径）。
/// 全部走 <see cref="BulkIngestor"/>，绕开 SQL 解析器。
/// </summary>
internal static class BulkIngestEndpointHandler
{
    /// <summary>批量入库目标格式。</summary>
    public enum Format
    {
        /// <summary>Line Protocol。</summary>
        LineProtocol,
        /// <summary>JSON points。</summary>
        Json,
        /// <summary>INSERT INTO ... VALUES 快路径。</summary>
        BulkValues,
    }

    /// <summary>处理一次批量入库请求。</summary>
    public static async Task HandleAsync(
        HttpContext context,
        Tsdb tsdb,
        string measurement,
        Format format,
        ServerMetrics metrics)
    {
        var sw = Stopwatch.StartNew();
        BulkErrorPolicy errorPolicy = ParseOnError(context);
        BulkFlushMode flushMode = ParseFlush(context);

        // PR #47：从 ArrayPool 租借请求体缓冲区，避免大 payload（>85KB）落入 LOH。
        // 体积通常 MB 级，仍一次性读取（无需流式），但全程零分配复用。
        var (bodyBuffer, bodyLength) = await ReadAllAsync(context).ConfigureAwait(false);
        char[]? lpCharBuffer = null;
        IPointReader? reader = null;
        try
        {
            reader = format switch
            {
                Format.LineProtocol => CreateLineProtocolReader(
                    bodyBuffer, bodyLength, measurement, out lpCharBuffer),
                Format.Json => new JsonPointsReader(
                    new ReadOnlyMemory<byte>(bodyBuffer, 0, bodyLength),
                    measurementOverride: measurement),
                Format.BulkValues => SchemaBoundBulkValuesReader.Create(
                    tsdb,
                    Encoding.UTF8.GetString(bodyBuffer, 0, bodyLength),
                    measurementOverride: measurement),
                _ => throw new InvalidOperationException($"未知格式 {format}。"),
            };

            BulkIngestResult result;
            try
            {
                result = BulkIngestor.Ingest(tsdb, reader, errorPolicy, flushMode);
            }
            catch (BulkIngestException ex)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bulk_ingest_error", ex.Message).ConfigureAwait(false);
                return;
            }

            metrics.AddInsertedRows(result.Written);
            var resp = new BulkIngestResponse(result.Written, result.Skipped, sw.Elapsed.TotalMilliseconds);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                resp,
                ServerJsonContext.Default.BulkIngestResponse,
                context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            (reader as IDisposable)?.Dispose();
            if (lpCharBuffer is not null)
                ArrayPool<char>.Shared.Return(lpCharBuffer);
            ArrayPool<byte>.Shared.Return(bodyBuffer);
        }
    }

    private static LineProtocolReader CreateLineProtocolReader(
        byte[] bodyBuffer, int bodyLength, string measurement, out char[] charBuffer)
    {
        // 解码 UTF-8 → char[]（从 ArrayPool 租借）。LineProtocolReader 仅持有 ReadOnlyMemory<char>，
        // 调用方负责在 reader 释放后归还 char[]。
        int maxChars = Encoding.UTF8.GetMaxCharCount(bodyLength);
        charBuffer = ArrayPool<char>.Shared.Rent(maxChars);
        int charCount = Encoding.UTF8.GetChars(
            new ReadOnlySpan<byte>(bodyBuffer, 0, bodyLength),
            charBuffer);
        return new LineProtocolReader(
            new ReadOnlyMemory<char>(charBuffer, 0, charCount),
            measurementOverride: measurement);
    }

    private static BulkErrorPolicy ParseOnError(HttpContext context)
    {
        if (context.Request.Query.TryGetValue("onerror", out var v)
            && string.Equals(v.ToString(), "skip", StringComparison.OrdinalIgnoreCase))
            return BulkErrorPolicy.Skip;
        return BulkErrorPolicy.FailFast;
    }

    private static BulkFlushMode ParseFlush(HttpContext context)
    {
        // PR #48：?flush 三档位
        //   缺省 / "false" / "0" / "no"   → None  （最快，仅入 MemTable + WAL）
        //   "async"                       → Async （仅 _flushWorker.Signal()，不阻塞）
        //   "true" / "1" / "sync" / "yes" → Sync  （同步 FlushNow，等待落盘）
        if (!context.Request.Query.TryGetValue("flush", out var v))
            return BulkFlushMode.None;
        var s = v.ToString();
        if (string.IsNullOrEmpty(s)) return BulkFlushMode.None;
        if (string.Equals(s, "async", StringComparison.OrdinalIgnoreCase))
            return BulkFlushMode.Async;
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "sync", StringComparison.OrdinalIgnoreCase)
            || s == "1"
            || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase))
            return BulkFlushMode.Sync;
        return BulkFlushMode.None;
    }

    private static async Task<(byte[] Buffer, int Length)> ReadAllAsync(HttpContext context)
    {
        // PR #47：所有路径均从 ArrayPool<byte>.Shared 租借（caller 在 finally 中归还），
        // 避免 100MB+ payload 直接落入 LOH。
        // 优先按 Content-Length 一次性租借精确大小。
        if (context.Request.ContentLength is long len && len > 0 && len <= int.MaxValue)
        {
            int size = (int)len;
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            int offset = 0;
            try
            {
                while (offset < size)
                {
                    int n = await context.Request.Body.ReadAsync(
                        buffer.AsMemory(offset, size - offset),
                        context.RequestAborted).ConfigureAwait(false);
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

        // 未知长度：用初始 4KB 池缓冲增量扩容（每次翻倍）。
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
                int n = await context.Request.Body.ReadAsync(
                    rented.AsMemory(total, rented.Length - total),
                    context.RequestAborted).ConfigureAwait(false);
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
