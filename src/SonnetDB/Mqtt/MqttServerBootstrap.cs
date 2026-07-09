using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.AspNetCore;
using MQTTnet.AspNetCore.Routing;
using SonnetDB.Configuration;

namespace SonnetDB.Mqtt;

/// <summary>
/// 配置 SonnetDB 内置 MQTT Broker 与外部 MQTT 客户端接入。
/// </summary>
internal static class MqttServerBootstrap
{
    /// <summary>
    /// 为 MQTT 原生 TCP 端口注册 Kestrel 监听。
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    /// <param name="mqttOptions">MQTT 配置。</param>
    public static void ConfigureKestrel(WebApplicationBuilder builder, MqttBrokerOptions mqttOptions)
    {
        if (!mqttOptions.Enabled)
            return;

        int port = mqttOptions.Port;
        if (port < 0 || port > 65535)
            throw new InvalidOperationException("SonnetDBServer:Mqtt:Port 必须位于 0..65535。");

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listenOptions => listenOptions.UseMqtt());
        });
    }

    /// <summary>
    /// 注册 MQTT Broker、路由控制器和外部客户端后台服务。
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    /// <param name="mqttOptions">MQTT 配置。</param>
    public static void ConfigureServices(WebApplicationBuilder builder, MqttBrokerOptions mqttOptions)
    {
        bool brokerEnabled = mqttOptions.Enabled;
        bool externalClientEnabled = mqttOptions.ExternalClient.Enabled;
        if (!brokerEnabled && !externalClientEnabled)
            return;

        builder.Services.AddSingleton<SonnetMqttMeasurementIngestor>();

        if (brokerEnabled)
        {
            builder.Services.AddSingleton<SonnetMqttBrokerBridge>();
            // AOT 安全：用泛型控制器重载（编译期已知类型）替代程序集扫描重载。
            builder.Services.AddMqttControllers<SonnetMqttController>(options =>
                options.WithCaseSensitiveTopicMatching());

            builder.Services
                .AddHostedMqttServerWithServices(options => options.WithoutDefaultEndpoint())
                .AddMqttConnectionHandler()
                .AddConnections();
        }

        if (externalClientEnabled)
            builder.Services.AddHostedService<SonnetMqttExternalClientService>();
    }

    /// <summary>
    /// 在请求管线中挂载 MQTT Broker 与 WebSocket 连接处理器。
    /// </summary>
    /// <param name="app">已构建的 Web 应用。</param>
    /// <param name="serverOptions">运行期服务器配置。</param>
    public static void ConfigureMiddleware(WebApplication app, ServerOptions serverOptions)
    {
        if (!serverOptions.Mqtt.Enabled)
            return;

        var bridge = app.Services.GetRequiredService<SonnetMqttBrokerBridge>();
        app.UseMqttServer(server =>
        {
            bridge.Configure(server);
            server.WithAttributeRouting(app.Services, allowUnmatchedRoutes: false);
        });

        if (!string.IsNullOrWhiteSpace(serverOptions.Mqtt.WebSocketPath))
        {
            app.MapConnectionHandler<MqttConnectionHandler>(
                serverOptions.Mqtt.WebSocketPath,
                options => options.WebSockets.SubProtocolSelector = protocols => protocols.FirstOrDefault() ?? string.Empty);
        }
    }
}
