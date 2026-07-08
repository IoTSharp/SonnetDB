using System.Diagnostics;
using System.Net;
using CoAP;
using CoAP.Server.Routing;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Ingest;

namespace SonnetDB.Coap;

/// <summary>
/// 将已路由的 CoAP measurement 写入请求映射到 SonnetDB 批量入库通道。
/// </summary>
internal sealed class SonnetCoapMeasurementIngestor
{
    internal const string FormatQueryName = "format";
    internal const string SonnetFormatQueryName = "sndb-format";

    private readonly TsdbRegistry _registry;
    private readonly GrantsStore _grants;
    private readonly UserStore _users;
    private readonly ServerOptions _options;
    private readonly ServerMetrics _metrics;
    private readonly ILogger<SonnetCoapMeasurementIngestor> _logger;

    /// <summary>
    /// 创建 CoAP measurement 写入服务。
    /// </summary>
    /// <param name="registry">数据库注册表。</param>
    /// <param name="grants">用户授权存储。</param>
    /// <param name="users">动态用户存储。</param>
    /// <param name="options">服务器配置。</param>
    /// <param name="metrics">服务器指标。</param>
    /// <param name="logger">日志记录器。</param>
    public SonnetCoapMeasurementIngestor(
        TsdbRegistry registry,
        GrantsStore grants,
        UserStore users,
        IOptions<ServerOptions> options,
        ServerMetrics metrics,
        ILogger<SonnetCoapMeasurementIngestor> logger)
    {
        _registry = registry;
        _grants = grants;
        _users = users;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// 处理一次已命中 <c>db/{db}/m/{measurement}</c> 的 CoAP 写入。
    /// </summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="measurement">目标 measurement 名称。</param>
    /// <param name="context">当前 CoAP 路由上下文。</param>
    /// <returns>要返回给 CoAP 客户端的响应。</returns>
    public CoapRouteResult Ingest(string db, string measurement, CoapRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sw = Stopwatch.StartNew();
        try
        {
            return IngestCore(db, measurement, context, sw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoAP 写入处理失败：{Source}", context.RemoteEndPoint);
            return Text(StatusCode.InternalServerError, "CoAP 写入处理失败。");
        }
    }

    private CoapRouteResult IngestCore(
        string db,
        string measurement,
        CoapRouteContext context,
        Stopwatch sw)
    {
        if (!TsdbRegistry.IsValidName(db))
            return Text(StatusCode.BadRequest, $"非法数据库名 '{db}'。");

        if (string.IsNullOrWhiteSpace(measurement) || measurement.Length > 255)
            return Text(StatusCode.BadRequest, $"非法 measurement 名 '{measurement}'。");

        if (!TryAuthenticate(context.Queries, out var principal))
            return Text(StatusCode.Unauthorized, "缺失或无效的 SonnetDB CoAP token。");

        if (!_registry.TryGet(db, out var tsdb))
            return Text(StatusCode.NotFound, $"数据库 '{db}' 不存在。");

        if (!principal.HasPermission(_grants, db, DatabasePermission.Write))
            return Text(StatusCode.Forbidden, $"当前 CoAP 凭据对数据库 '{db}' 没有写权限。");

        if (context.Payload.Length > _options.Coap.MaxPayloadBytes)
            return Text(StatusCode.RequestEntityTooLarge, $"CoAP payload 超过 {_options.Coap.MaxPayloadBytes} 字节限制。");

        if (!TryResolveFormat(context, out var format, out var formatError))
            return Text(StatusCode.UnsupportedMediaType, formatError);

        try
        {
            var result = BulkIngestEndpointHandler.IngestPayload(
                tsdb,
                measurement,
                format,
                context.Payload,
                ParseOnError(context.Queries),
                ParseFlush(context.Queries));
            _metrics.AddInsertedRows(result.Written);
            return Text(
                StatusCode.Changed,
                $"written={result.Written};skipped={result.Skipped};elapsed_ms={sw.Elapsed.TotalMilliseconds:F3}");
        }
        catch (BulkIngestException ex)
        {
            return Text(StatusCode.BadRequest, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Text(StatusCode.BadRequest, ex.Message);
        }
    }

    private bool TryAuthenticate(IReadOnlyList<string> queries, out SonnetCoapPrincipal principal)
        => SonnetCoapAuthentication.TryAuthenticate(queries, _options, _users, out principal);

    private static bool TryResolveFormat(
        CoapRouteContext context,
        out BulkIngestEndpointHandler.Format format,
        out string error)
    {
        error = string.Empty;
        if (TryGetQueryValue(context.Queries, FormatQueryName, out var explicitFormat)
            || TryGetQueryValue(context.Queries, SonnetFormatQueryName, out explicitFormat))
        {
            return TryParseExplicitFormat(explicitFormat, out format, out error);
        }

        format = context.ContentFormat switch
        {
            MediaType.ApplicationJson => BulkIngestEndpointHandler.Format.Json,
            MediaType.TextPlain or MediaType.ApplicationOctetStream or MediaType.Undefined => SniffFormat(context.Payload.Span),
            _ => BulkIngestEndpointHandler.Format.LineProtocol,
        };

        if (context.ContentFormat is not MediaType.ApplicationJson
            and not MediaType.TextPlain
            and not MediaType.ApplicationOctetStream
            and not MediaType.Undefined)
        {
            error = $"不支持的 CoAP Content-Format：{context.ContentFormat}。";
            return false;
        }

        return true;
    }

    private static bool TryParseExplicitFormat(
        string value,
        out BulkIngestEndpointHandler.Format format,
        out string error)
    {
        format = BulkIngestEndpointHandler.Format.LineProtocol;
        error = string.Empty;
        if (value.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            format = BulkIngestEndpointHandler.Format.Json;
            return true;
        }

        if (value.Contains("bulk", StringComparison.OrdinalIgnoreCase)
            || value.Contains("values", StringComparison.OrdinalIgnoreCase))
        {
            format = BulkIngestEndpointHandler.Format.BulkValues;
            return true;
        }

        if (value.Contains("line", StringComparison.OrdinalIgnoreCase)
            || value.Contains("lp", StringComparison.OrdinalIgnoreCase)
            || value.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            format = BulkIngestEndpointHandler.Format.LineProtocol;
            return true;
        }

        error = $"不支持的 CoAP payload 格式 '{value}'。";
        return false;
    }

    private static BulkIngestEndpointHandler.Format SniffFormat(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return BulkIngestEndpointHandler.Format.LineProtocol;

        var offset = 0;
        while (offset < payload.Length && payload[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            offset++;

        if (offset >= payload.Length)
            return BulkIngestEndpointHandler.Format.LineProtocol;

        if (payload[offset] is (byte)'{' or (byte)'[')
            return BulkIngestEndpointHandler.Format.Json;

        return StartsWithAsciiIgnoreCase(payload[offset..], "insert")
            ? BulkIngestEndpointHandler.Format.BulkValues
            : BulkIngestEndpointHandler.Format.LineProtocol;
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> span, string value)
    {
        if (span.Length < value.Length)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            var expected = (byte)value[i];
            var actual = span[i];
            if (actual >= (byte)'A' && actual <= (byte)'Z')
                actual = (byte)(actual + 32);
            if (expected >= (byte)'A' && expected <= (byte)'Z')
                expected = (byte)(expected + 32);
            if (actual != expected)
                return false;
        }

        return true;
    }

    private static BulkErrorPolicy ParseOnError(IReadOnlyList<string> queries)
        => TryGetQueryValue(queries, "onerror", out var value)
            && string.Equals(value, "skip", StringComparison.OrdinalIgnoreCase)
            ? BulkErrorPolicy.Skip
            : BulkErrorPolicy.FailFast;

    private static BulkFlushMode ParseFlush(IReadOnlyList<string> queries)
    {
        if (!TryGetQueryValue(queries, "flush", out var value) || string.IsNullOrEmpty(value))
            return BulkFlushMode.None;

        if (string.Equals(value, "async", StringComparison.OrdinalIgnoreCase))
            return BulkFlushMode.Async;

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "sync", StringComparison.OrdinalIgnoreCase)
            || value == "1"
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            return BulkFlushMode.Sync;

        return BulkFlushMode.None;
    }

    private static bool TryGetQueryValue(IReadOnlyList<string> queries, string name, out string value)
    {
        value = string.Empty;
        foreach (var query in queries)
        {
            var decoded = WebUtility.UrlDecode(query);
            if (string.IsNullOrWhiteSpace(decoded))
                continue;

            var split = decoded.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
                continue;

            if (!string.Equals(decoded[..split].Trim(), name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = decoded[(split + 1)..].Trim();
            return true;
        }

        return false;
    }

    private static CoapRouteResult Text(StatusCode statusCode, string message)
        => CoapRouteResult.Text(statusCode, message);
}
