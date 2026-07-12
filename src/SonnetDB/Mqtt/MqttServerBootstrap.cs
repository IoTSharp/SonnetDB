using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.AspNetCore;
using MQTTnet.AspNetCore.Routing;
using SonnetDB.Configuration;
using SonnetDB.Hosting;

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

        if (brokerEnabled && mqttOptions.Sparkplug.Enabled)
        {
            if (!TsdbRegistry.IsValidName(mqttOptions.Sparkplug.Database))
                throw new InvalidOperationException("SonnetDBServer:Mqtt:Sparkplug:Database 必须为合法数据库名。");
            if (mqttOptions.Sparkplug.MaxPayloadBytes <= 0)
                throw new InvalidOperationException("SonnetDBServer:Mqtt:Sparkplug:MaxPayloadBytes 必须大于 0。");
            if (string.IsNullOrWhiteSpace(mqttOptions.Sparkplug.HostId)
                || mqttOptions.Sparkplug.HostId.Length > 255
                || mqttOptions.Sparkplug.HostId.AsSpan().IndexOfAny("/+#\n\r\t") >= 0)
            {
                throw new InvalidOperationException("SonnetDBServer:Mqtt:Sparkplug:HostId 必须为合法的单段 topic 标识。");
            }
        }

        builder.Services.AddSingleton<SonnetMqttMeasurementIngestor>();

        if (brokerEnabled)
        {
            builder.Services.AddSingleton<SparkplugAliasStore>();
            builder.Services.AddSingleton<SparkplugLifecycleStore>();
            builder.Services.AddSingleton<SparkplugIngestor>();
            builder.Services.AddSingleton<SonnetMqttBrokerBridge>();
            builder.Services.AddSingleton<SparkplugHostApplicationService>();
            // AOT 安全：用泛型控制器重载（编译期已知类型）替代程序集扫描重载。
            builder.Services.AddMqttControllers<SonnetMqttController>(options =>
                options.WithCaseSensitiveTopicMatching());
            builder.Services.AddMqttApplicationMessageSlimRouting(static routes =>
                global::MyGeneratedMqttEndpoints.Map(routes));

            builder.Services
                .AddHostedMqttServerWithServices(options => options.WithoutDefaultEndpoint())
                .AddMqttConnectionHandler()
                .AddConnections();
            // 晚于 broker 注册，保证启动时 broker 先就绪、停止时 STATE/OFFLINE 先发出。
            builder.Services.AddHostedService(static services =>
                services.GetRequiredService<SparkplugHostApplicationService>());
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
            server.WithApplicationMessageRouting(app.Services);
        });

        if (!string.IsNullOrWhiteSpace(serverOptions.Mqtt.WebSocketPath))
        {
            app.MapConnectionHandler<MqttConnectionHandler>(
                serverOptions.Mqtt.WebSocketPath,
                options => options.WebSockets.SubProtocolSelector = protocols => protocols.FirstOrDefault() ?? string.Empty);
        }
    }
}
