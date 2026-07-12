using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;
using SonnetDB.Hosting;
using SonnetDB.Ingest;

namespace SonnetDB.LineProtocolUdp;

/// <summary>
/// Line Protocol UDP 后台监听服务。每个 UDP 数据报被视为一批 LP 行并写入配置绑定的数据库。
/// </summary>
internal sealed class LineProtocolUdpListenerService : BackgroundService
{
    internal const int UdpPayloadLimit = 65_507;

    private readonly TsdbRegistry _registry;
    private readonly ServerMetrics _metrics;
    private readonly LineProtocolUdpOptions _options;
    private readonly TimePrecision _precision;
    private readonly ILogger<LineProtocolUdpListenerService> _logger;

    /// <summary>
    /// 创建 Line Protocol UDP 监听服务。
    /// </summary>
    /// <param name="registry">数据库注册表。</param>
    /// <param name="metrics">服务端指标。</param>
    /// <param name="options">服务器配置。</param>
    /// <param name="logger">日志记录器。</param>
    public LineProtocolUdpListenerService(
        TsdbRegistry registry,
        ServerMetrics metrics,
        IOptions<ServerOptions> options,
        ILogger<LineProtocolUdpListenerService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry;
        _metrics = metrics;
        _options = options.Value.LineProtocolUdp;
        _logger = logger;

        if (!TryParsePrecision(_options.Precision, out _precision))
            _precision = TimePrecision.Nanoseconds;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, _options.Port));
        var local = (IPEndPoint)udp.Client.LocalEndPoint!;
        _logger.LineProtocolUdpStarted(local.Port, _options.Database);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LineProtocolUdpStopped(ex);
                break;
            }

            ProcessDatagram(datagram);
        }
    }

    /// <summary>
    /// 解析 InfluxDB 风格 timestamp 精度配置。
    /// </summary>
    /// <param name="value">配置值。</param>
    /// <param name="precision">解析得到的精度。</param>
    /// <returns>解析是否成功。</returns>
    internal static bool TryParsePrecision(string? value, out TimePrecision precision)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            precision = TimePrecision.Nanoseconds;
            return true;
        }

        precision = value.Trim() switch
        {
            "n" or "ns" => TimePrecision.Nanoseconds,
            "u" or "us" or "µs" => TimePrecision.Microseconds,
            "ms" => TimePrecision.Milliseconds,
            "s" => TimePrecision.Seconds,
            _ => TimePrecision.Nanoseconds,
        };

        return value.Trim() is "n" or "ns" or "u" or "us" or "µs" or "ms" or "s";
    }

    private void ProcessDatagram(UdpReceiveResult datagram)
    {
        var payload = datagram.Buffer.AsMemory();
        if (payload.Length > _options.MaxDatagramBytes)
        {
            _logger.LineProtocolUdpOversized(datagram.RemoteEndPoint, payload.Length, _options.MaxDatagramBytes);
            return;
        }

        if (!_registry.TryGet(_options.Database, out var tsdb))
        {
            _logger.LineProtocolUdpDatabaseMissing(_options.Database, datagram.RemoteEndPoint);
            return;
        }

        char[]? charBuffer = null;
        try
        {
            int maxChars = Encoding.UTF8.GetMaxCharCount(payload.Length);
            charBuffer = ArrayPool<char>.Shared.Rent(maxChars);
            int charCount = Encoding.UTF8.GetChars(payload.Span, charBuffer);
            var reader = new LineProtocolReader(
                new ReadOnlyMemory<char>(charBuffer, 0, charCount),
                _precision,
                measurementOverride: null);

            var result = BulkIngestor.Ingest(tsdb, reader, BulkErrorPolicy.FailFast, BulkFlushMode.None);
            _metrics.AddInsertedRows(result.Written);

            if (result.Written > 0 || result.Skipped > 0)
            {
                _logger.LineProtocolUdpIngested(datagram.RemoteEndPoint, result.Written, result.Skipped);
            }
        }
        catch (BulkIngestException ex)
        {
            _logger.LineProtocolUdpIngestFailed(ex, datagram.RemoteEndPoint, payload.Length);
        }
        catch (DecoderFallbackException ex)
        {
            _logger.LineProtocolUdpInvalidUtf8(ex, datagram.RemoteEndPoint, payload.Length);
        }
        catch (ArgumentException ex)
        {
            _logger.LineProtocolUdpInvalidArguments(ex, datagram.RemoteEndPoint, payload.Length);
        }
        catch (Exception ex)
        {
            _logger.LineProtocolUdpFailed(ex, datagram.RemoteEndPoint, payload.Length);
        }
        finally
        {
            if (charBuffer is not null)
                ArrayPool<char>.Shared.Return(charBuffer);
        }
    }
}
