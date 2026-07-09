using System.Net;
using CoAP;
using CoAP.Channel;
using CoAP.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SonnetDB.Configuration;

namespace SonnetDB.Coap;

/// <summary>
/// 配置 SonnetDB CoAP / CoAP DTLS 接入与资源映射。
/// </summary>
internal static class CoapServerBootstrap
{
    /// <summary>
    /// 注册 CoAP 明文、DTLS 监听端点和资源工厂。
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    /// <param name="coapOptions">CoAP 配置。</param>
    public static void ConfigureServices(WebApplicationBuilder builder, CoapServerOptions coapOptions)
    {
        bool coapEnabled = coapOptions.Enabled;
        bool dtlsEnabled = coapOptions.Dtls.Enabled;
        if (!coapEnabled && !dtlsEnabled)
            return;

        ValidateOptions(coapOptions);

        builder.Services.AddSingleton<SonnetCoapMeasurementIngestor>();
        builder.Services.AddSingleton<SonnetCoapMqObserveManager>();
        builder.Services.AddCoapServer(options =>
        {
            options.Config = CreateConfig(coapOptions);

            if (coapOptions.Enabled)
            {
                options.UseEndPoint((services, config) =>
                {
                    InitializeLogging(services);
                    services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("SonnetDB.Coap")
                        .LogInformation("CoAP 明文 UDP 监听已启用：0.0.0.0:{Port}", coapOptions.Port);
                    return new CoAPEndPoint(new IPEndPoint(IPAddress.Any, coapOptions.Port), config);
                });
            }

            if (coapOptions.Dtls.Enabled)
            {
                options.UseEndPoint((services, config) =>
                {
                    InitializeLogging(services);
                    var channel = new DtlsPskChannel(
                        coapOptions.Dtls.Port,
                        coapOptions.Dtls.PskKeys,
                        TimeSpan.FromSeconds(Math.Max(30, coapOptions.Dtls.SessionIdleSeconds)));
                    services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("SonnetDB.Coap")
                        .LogInformation("CoAP DTLS/coaps 监听已启用：0.0.0.0:{Port}", coapOptions.Dtls.Port);
                    return new CoAPEndPoint(channel, config);
                });
            }
        });
        builder.Services.AddCoapResources(static options =>
            options.AddEndpointFactory(global::MyGeneratedCoapEndpoints.Create));
    }

    /// <summary>
    /// 在请求管线中映射 CoAP 资源。
    /// </summary>
    /// <param name="app">已构建的 Web 应用。</param>
    /// <param name="serverOptions">运行期服务器配置。</param>
    public static void ConfigureMiddleware(WebApplication app, ServerOptions serverOptions)
    {
        if (!serverOptions.Coap.Enabled && !serverOptions.Coap.Dtls.Enabled)
            return;

        InitializeLogging(app.Services);
        app.MapCoapResources();
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

    private static void InitializeLogging(IServiceProvider services)
        => CoapLogging.LoggerFactory = services.GetRequiredService<ILoggerFactory>();
}
