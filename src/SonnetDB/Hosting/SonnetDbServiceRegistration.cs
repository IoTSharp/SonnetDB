using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Coap;
using SonnetDB.Configuration;
using SonnetDB.Copilot;
using SonnetDB.Diagnostics;
using SonnetDB.Json;
using SonnetDB.LineProtocolUdp;
using SonnetDB.Mcp;
using SonnetDB.Mqtt;
using SonnetMQ;

namespace SonnetDB.Hosting;

/// <summary>
/// 注册 SonnetDB Server 的应用服务、后台任务与协议接入组件。
/// </summary>
internal static class SonnetDbServiceRegistration
{
    /// <summary>
    /// 按服务器配置注册运行期所需的服务集合。
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    /// <param name="serverOptions">启动期已绑定的服务器配置。</param>
    public static void Configure(WebApplicationBuilder builder, ServerOptions serverOptions)
    {
        OpenTelemetryBootstrap.Configure(builder, serverOptions);

        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.Converters.Add(new GeoJsonConverter());
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonContext.Default);
        });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ServerMetrics>();
        builder.Services.AddSingleton<EventBroadcaster>();
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Observability.SlowQueryLog;
            return new SlowQueryRing(Math.Clamp(options.Capacity, 16, 4096));
        });
        builder.Services.AddSingleton<SlowQueryDiagnostics>();
        builder.Services.AddSingleton<SonnetDbMcpContextAccessor>();
        builder.Services.AddSingleton<SonnetDbMcpSchemaCache>();
        builder.Services.AddSingleton<SonnetDbMcpExplainSqlService>();
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
            var registry = new TsdbRegistry(options.DataRoot, sp.GetRequiredService<EventBroadcaster>());
            if (options.AutoLoadExistingDatabases)
                registry.LoadExisting();
            return registry;
        });

        // PR #34c：周期性指标快照后台服务。
        builder.Services.AddHostedService<MetricsTickService>();

        // PR #34a：用户 / 权限 / 控制面存储全局只实例。文件位于 <DataRoot>/.system/。
        builder.Services.AddSingleton(sp =>
        {
            var systemDirectory = GetSystemDirectory(sp);
            return SonnetMqStore.Open(new SonnetMqOptions
            {
                Path = Path.Combine(systemDirectory, "mq"),
                FlushOnPublish = true,
            });
        });
        builder.Services.AddSingleton(sp => new UserStore(GetSystemDirectory(sp)));
        builder.Services.AddSingleton(sp => new GrantsStore(GetSystemDirectory(sp)));
        builder.Services.AddSingleton(sp => new InstallationStore(GetSystemDirectory(sp)));
        builder.Services.AddSingleton(sp =>
        {
            var systemDirectory = GetSystemDirectory(sp);
            var store = new AiConfigStore(systemDirectory);
            // M16/M2：启动时把已持久化的 sonnetdb.com Cloud Token
            // 同步到 CopilotChatOptions，让 /v1/copilot/chat 直接就绪。
            var options = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
            AiCopilotBridge.Apply(store.Get(), options.Copilot.Chat, options.Copilot.Embedding);
            return store;
        });
        builder.Services.AddSingleton<CopilotReadiness>();
        builder.Services.AddHttpClient();
        builder.Services.AddHealthChecks()
            .AddCheck<SegmentStoreWritableHealthCheck>(
                "segment_store_writable",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "storage"])
            .AddCheck<WalWritableHealthCheck>(
                "wal_writable",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "storage"])
            .AddCheck<CopilotChatProviderHealthCheck>(
                "copilot_provider_reachable",
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "provider"])
            .AddCheck<CopilotEmbeddingProviderHealthCheck>(
                "copilot_embedding_provider_reachable",
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "provider"]);
        builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Copilot.Embedding;
            if (string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase))
                return new OpenAICompatibleEmbeddingProvider(options, sp.GetRequiredService<IHttpClientFactory>());
            if (string.Equals(options.Provider, "local", StringComparison.OrdinalIgnoreCase))
            {
                // 本地 ONNX 骨架还未接入 tokenizer；若模型文件不存在则自动降级到 builtin，
                // 避免首次部署者后 Copilot 在运行时装载报错。
                if (!string.IsNullOrWhiteSpace(options.LocalModelPath) && File.Exists(options.LocalModelPath))
                    return new LocalOnnxEmbeddingProvider(options);
                return new BuiltinHashEmbeddingProvider(options);
            }
            if (string.Equals(options.Provider, "builtin", StringComparison.OrdinalIgnoreCase))
                return new BuiltinHashEmbeddingProvider(options);

            throw new InvalidOperationException($"Unsupported copilot embedding provider '{options.Provider}'.");
        });
        builder.Services.AddSingleton<IChatProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Copilot.Chat;
            if (string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase))
                return new OpenAICompatibleChatProvider(options, sp.GetRequiredService<IHttpClientFactory>());

            throw new InvalidOperationException($"Unsupported copilot chat provider '{options.Provider}'.");
        });

        // Copilot 云端运行时：本地仅提供上下文摘要与受权限保护的工具执行。
        builder.Services.AddSingleton<ICopilotCloudGatewayClient, CopilotCloudGatewayClient>();
        builder.Services.AddSingleton<CopilotLocalToolExecutor>();
        builder.Services.AddSingleton<CopilotStateStore>();

        // PR #64：文档摄入与检索（Knowledge 库 __copilot__）。
        // 当前在线 Copilot 流程已切到 ai.sonnetdb.com，下面的本地索引服务仅保留为兼容/手动诊断能力。
        builder.Services.AddSingleton<DocsSourceScanner>();
        builder.Services.AddSingleton<DocsChunker>();
        builder.Services.AddSingleton<DocsIngestor>();
        builder.Services.AddSingleton<DocsSearchService>();
        builder.Services.AddHostedService<CopilotDocsIngestionService>();

        // PR #65：技能库 __copilot__.skills + 技能路由。
        builder.Services.AddSingleton<SkillSourceScanner>();
        builder.Services.AddSingleton<SkillRegistry>();
        builder.Services.AddSingleton<SkillSearchService>();
        builder.Services.AddSingleton<CopilotAgent>();
        builder.Services.AddHostedService<CopilotSkillsIngestionService>();

        builder.Services.AddSingleton<SonnetDB.Sql.Execution.IControlPlane>(sp =>
            new ControlPlane(
                sp.GetRequiredService<UserStore>(),
                sp.GetRequiredService<GrantsStore>(),
                sp.GetRequiredService<TsdbRegistry>()));

        builder.Services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = static (context, mcpServerOptions, _) =>
                {
                    if (context.Items.TryGetValue(SonnetDbMcpContextAccessor.DatabaseNameItemKey, out var value)
                        && value is string databaseName)
                    {
                        mcpServerOptions.ServerInstructions =
                            $"SonnetDB MCP endpoint for database '{databaseName}'. " +
                            "Only read-only tools and resources are exposed. " +
                            "Prefer bounded queries via SQL LIMIT / FETCH or the maxRows tool parameter.";
                    }

                    return Task.CompletedTask;
                };
            })
            .WithTools<SonnetDbMcpTools>()
            .WithResources<SonnetDbMcpResources>();

        // 在应用关闭时优雅释放所有 Tsdb 实例。
        builder.Services.AddSingleton<IHostedService>(sp => new RegistryShutdownHook(sp.GetRequiredService<TsdbRegistry>()));

        MqttServerBootstrap.ConfigureServices(builder, serverOptions.Mqtt);
        CoapServerBootstrap.ConfigureServices(builder, serverOptions.Coap);
        LineProtocolUdpBootstrap.ConfigureServices(builder, serverOptions.LineProtocolUdp);
    }

    private static string GetSystemDirectory(IServiceProvider services)
    {
        var serverOptions = services.GetRequiredService<IOptions<ServerOptions>>().Value;
        var systemDirectory = Path.Combine(serverOptions.DataRoot, ".system");
        Directory.CreateDirectory(systemDirectory);
        return systemDirectory;
    }
}
