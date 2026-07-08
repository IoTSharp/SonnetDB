using System.Diagnostics;
using System.Net;
using CoAP;
using CoAP.Net;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Ingest;

namespace SonnetDB.Coap;

/// <summary>
/// 将 CoAP 写入请求映射到 SonnetDB 批量入库通道。
/// </summary>
internal sealed class SonnetCoapMessageDeliverer : IMessageDeliverer
{
    internal const string FormatQueryName = "format";
    internal const string SonnetFormatQueryName = "sndb-format";

    private readonly TsdbRegistry _registry;
    private readonly GrantsStore _grants;
    private readonly UserStore _users;
    private readonly ServerOptions _options;
    private readonly ServerMetrics _metrics;
    private readonly ILogger<SonnetCoapMessageDeliverer> _logger;

    /// <summary>
    /// 创建 CoAP 消息投递器。
    /// </summary>
    public SonnetCoapMessageDeliverer(
        TsdbRegistry registry,
        GrantsStore grants,
        UserStore users,
        IOptions<ServerOptions> options,
        ServerMetrics metrics,
        ILogger<SonnetCoapMessageDeliverer> logger)
    {
        _registry = registry;
        _grants = grants;
        _users = users;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public void DeliverRequest(Exchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        var sw = Stopwatch.StartNew();
        var request = exchange.Request;
        if (request is null)
        {
            Send(exchange, StatusCode.BadRequest, "缺失 CoAP request。");
            return;
        }

        try
        {
            HandleRequest(exchange, request, sw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoAP 写入处理失败：{Source}", request.Source);
            Send(exchange, StatusCode.InternalServerError, "CoAP 写入处理失败。");
        }
    }

    /// <inheritdoc />
    public void DeliverResponse(Exchange exchange, Response response)
    {
        // Server 侧不主动发起 CoAP 请求，因此无需处理入站响应。
    }

    private void HandleRequest(Exchange exchange, Request request, Stopwatch sw)
    {
        if (request.Method is not Method.POST and not Method.PUT)
        {
            Send(exchange, StatusCode.MethodNotAllowed, "仅支持 POST / PUT。");
            return;
        }

        if (!TryParseMeasurementRoute(request, out string db, out string measurement, out string routeError))
        {
            Send(exchange, StatusCode.BadRequest, routeError);
            return;
        }

        if (!TryAuthenticate(request, out var principal))
        {
            Send(exchange, StatusCode.Unauthorized, "缺失或无效的 SonnetDB CoAP token。");
            return;
        }

        if (!_registry.TryGet(db, out var tsdb))
        {
            Send(exchange, StatusCode.NotFound, $"数据库 '{db}' 不存在。");
            return;
        }

        if (!principal.HasPermission(_grants, db, DatabasePermission.Write))
        {
            Send(exchange, StatusCode.Forbidden, $"当前 CoAP 凭据对数据库 '{db}' 没有写权限。");
            return;
        }

        if (request.PayloadSize > _options.Coap.MaxPayloadBytes)
        {
            Send(exchange, StatusCode.RequestEntityTooLarge, $"CoAP payload 超过 {_options.Coap.MaxPayloadBytes} 字节限制。");
            return;
        }

        if (!TryResolveFormat(request, out var format, out string formatError))
        {
            Send(exchange, StatusCode.UnsupportedMediaType, formatError);
            return;
        }

        try
        {
            var result = BulkIngestEndpointHandler.IngestPayload(
                tsdb,
                measurement,
                format,
                request.Payload ?? [],
                ParseOnError(request),
                ParseFlush(request));
            _metrics.AddInsertedRows(result.Written);
            Send(exchange, StatusCode.Changed, $"written={result.Written};skipped={result.Skipped};elapsed_ms={sw.Elapsed.TotalMilliseconds:F3}");
        }
        catch (BulkIngestException ex)
        {
            Send(exchange, StatusCode.BadRequest, ex.Message);
        }
        catch (ArgumentException ex)
        {
            Send(exchange, StatusCode.BadRequest, ex.Message);
        }
    }

    private bool TryAuthenticate(Request request, out SonnetCoapPrincipal principal)
    {
        principal = null!;
        if (!TryGetToken(request, out string token))
            return false;

        if (_options.Tokens.TryGetValue(token, out string? role))
        {
            principal = SonnetCoapPrincipal.ForRole(role);
            return true;
        }

        if (_users.TryAuthenticate(token, out var user))
        {
            principal = SonnetCoapPrincipal.ForUser(user);
            return true;
        }

        return false;
    }

    private static bool TryGetToken(Request request, out string token)
    {
        token = string.Empty;
        foreach (var query in request.UriQueries)
        {
            var decoded = WebUtility.UrlDecode(query);
            if (string.IsNullOrWhiteSpace(decoded))
                continue;

            var split = decoded.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
                continue;

            var name = decoded[..split].Trim();
            var value = decoded[(split + 1)..].Trim();
            if (string.IsNullOrEmpty(value))
                continue;

            if (string.Equals(name, "token", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "access_token", StringComparison.OrdinalIgnoreCase))
            {
                token = value;
                return true;
            }

            if (string.Equals(name, "authorization", StringComparison.OrdinalIgnoreCase))
            {
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = value["Bearer ".Length..].Trim();
                    return !string.IsNullOrEmpty(token);
                }

                if (value.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
                {
                    token = value["Token ".Length..].Trim();
                    return !string.IsNullOrEmpty(token);
                }
            }
        }

        return false;
    }

    private static bool TryParseMeasurementRoute(
        Request request,
        out string db,
        out string measurement,
        out string error)
    {
        db = string.Empty;
        measurement = string.Empty;
        error = string.Empty;

        var paths = request.UriPaths.ToArray();
        if (paths.Length != 4
            || !string.Equals(paths[0], "db", StringComparison.Ordinal)
            || !string.Equals(paths[2], "m", StringComparison.Ordinal))
        {
            error = "CoAP 路径需匹配 db/{db}/m/{measurement}。";
            return false;
        }

        db = paths[1];
        measurement = paths[3];
        if (!TsdbRegistry.IsValidName(db))
        {
            error = $"非法数据库名 '{db}'。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(measurement) || measurement.Length > 255)
        {
            error = $"非法 measurement 名 '{measurement}'。";
            return false;
        }

        return true;
    }

    private static bool TryResolveFormat(
        Request request,
        out BulkIngestEndpointHandler.Format format,
        out string error)
    {
        error = string.Empty;
        if (TryGetQueryValue(request, FormatQueryName, out string explicitFormat)
            || TryGetQueryValue(request, SonnetFormatQueryName, out explicitFormat))
        {
            return TryParseExplicitFormat(explicitFormat, out format, out error);
        }

        format = request.ContentFormat switch
        {
            MediaType.ApplicationJson => BulkIngestEndpointHandler.Format.Json,
            MediaType.TextPlain or MediaType.ApplicationOctetStream or MediaType.Undefined => SniffFormat(request.Payload),
            _ => BulkIngestEndpointHandler.Format.LineProtocol,
        };

        if (request.ContentFormat is not MediaType.ApplicationJson
            and not MediaType.TextPlain
            and not MediaType.ApplicationOctetStream
            and not MediaType.Undefined)
        {
            error = $"不支持的 CoAP Content-Format：{request.ContentFormat}。";
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

    private static BulkIngestEndpointHandler.Format SniffFormat(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
            return BulkIngestEndpointHandler.Format.LineProtocol;

        var span = payload.AsSpan();
        var offset = 0;
        while (offset < span.Length && span[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            offset++;

        if (offset >= span.Length)
            return BulkIngestEndpointHandler.Format.LineProtocol;

        if (span[offset] is (byte)'{' or (byte)'[')
            return BulkIngestEndpointHandler.Format.Json;

        return StartsWithAsciiIgnoreCase(span[offset..], "insert")
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

    private static BulkErrorPolicy ParseOnError(Request request)
        => TryGetQueryValue(request, "onerror", out var value)
            && string.Equals(value, "skip", StringComparison.OrdinalIgnoreCase)
            ? BulkErrorPolicy.Skip
            : BulkErrorPolicy.FailFast;

    private static BulkFlushMode ParseFlush(Request request)
    {
        if (!TryGetQueryValue(request, "flush", out var value) || string.IsNullOrEmpty(value))
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

    private static bool TryGetQueryValue(Request request, string name, out string value)
    {
        value = string.Empty;
        foreach (var query in request.UriQueries)
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

    private static void Send(Exchange exchange, StatusCode code, string message)
    {
        var response = Response.CreateResponse(exchange.Request, code);
        if (!string.IsNullOrEmpty(message))
            response.SetPayload(message, MediaType.TextPlain);
        exchange.SendResponse(response);
        exchange.Complete = true;
    }
}
