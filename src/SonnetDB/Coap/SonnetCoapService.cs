using System.Net;
using CoAP;
using CoAP.Channel;
using CoAP.Net;
using CoAP.Server;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;

namespace SonnetDB.Coap;

/// <summary>
/// CoAP / coaps 设备写入后台服务。
/// </summary>
internal sealed class SonnetCoapService : IHostedService, IDisposable
{
    private readonly ServerOptions _options;
    private readonly SonnetCoapMessageDeliverer _deliverer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SonnetCoapService> _logger;
    private CoapServer? _server;

    /// <summary>
    /// 创建 CoAP 设备写入后台服务。
    /// </summary>
    public SonnetCoapService(
        IOptions<ServerOptions> options,
        SonnetCoapMessageDeliverer deliverer,
        ILoggerFactory loggerFactory,
        ILogger<SonnetCoapService> logger)
    {
        _options = options.Value;
        _deliverer = deliverer;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Coap.Enabled && !_options.Coap.Dtls.Enabled)
            return Task.CompletedTask;

        ValidateOptions(_options.Coap);
        CoapLogging.LoggerFactory = _loggerFactory;

        var config = CreateConfig(_options.Coap);
        var server = new CoapServer(config)
        {
            MessageDeliverer = _deliverer,
        };

        if (_options.Coap.Enabled)
        {
            server.AddEndPoint(new CoAPEndPoint(new IPEndPoint(IPAddress.Any, _options.Coap.Port), config));
            _logger.LogInformation("CoAP 明文 UDP 监听已启用：0.0.0.0:{Port}", _options.Coap.Port);
        }

        if (_options.Coap.Dtls.Enabled)
        {
            IChannel channel = new DtlsPskChannel(
                _options.Coap.Dtls.Port,
                _options.Coap.Dtls.PskKeys,
                TimeSpan.FromSeconds(Math.Max(30, _options.Coap.Dtls.SessionIdleSeconds)));
            server.AddEndPoint(new CoAPEndPoint(channel, config));
            _logger.LogInformation("CoAP DTLS/coaps 监听已启用：0.0.0.0:{Port}", _options.Coap.Dtls.Port);
        }

        server.Start();
        _server = server;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            _server.Stop();
            _server.Dispose();
            _server = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _server?.Dispose();
    }

    private static CoapConfig CreateConfig(CoapServerOptions options)
    {
        var packetSize = Math.Clamp(options.MaxPayloadBytes + 128, 2048, 65_507);
        return new CoapConfig
        {
            DefaultPort = options.Port,
            DefaultSecurePort = options.Dtls.Port,
            MaxMessageSize = Math.Max(1024, options.MaxPayloadBytes),
            ChannelReceivePacketSize = packetSize,
        };
    }

    private static void ValidateOptions(CoapServerOptions options)
    {
        ValidatePort(options.Port, "SonnetDBServer:Coap:Port");
        ValidatePort(options.Dtls.Port, "SonnetDBServer:Coap:Dtls:Port");
        if (options.MaxPayloadBytes <= 0)
            throw new InvalidOperationException("SonnetDBServer:Coap:MaxPayloadBytes 必须大于 0。");
        if (options.Dtls.Enabled && options.Dtls.PskKeys.Count == 0)
            throw new InvalidOperationException("启用 CoAP DTLS 时必须配置 SonnetDBServer:Coap:Dtls:PskKeys。");
    }

    private static void ValidatePort(int port, string name)
    {
        if (port < 0 || port > 65_535)
            throw new InvalidOperationException($"{name} 必须位于 0..65535。");
    }
}
