using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Diagnostics;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.Mcp;
using SonnetMQ;

namespace SonnetDB;

/// <summary>
/// AOT-friendly Minimal API 入口。
/// </summary>
public static class Program
{
    /// <summary>
    /// 构建并运行 SonnetDB Server。
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var app = BuildApp(args);
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// 构造但不启动 <see cref="WebApplication"/>。供测试代码注入自定义配置。
    /// </summary>
    /// <param name="args">命令行参数（透传给 <see cref="WebApplication.CreateSlimBuilder(string[])"/>）。</param>
    /// <param name="configureServices">测试或宿主可选的附加 DI 覆盖。</param>
    public static WebApplication BuildApp(
        string[] args,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        // 配置：appsettings.json / appsettings.{Environment}.json。
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddWindowsService(options => options.ServiceName = "SonnetDB");
        }

        builder.Configuration.AddEnvironmentVariables(prefix: "SONNETDB_");
        builder.Services.Configure<ServerOptions>(options => options.Copilot.Docs.Roots.Clear());
        builder.Services.Configure<ServerOptions>(
            builder.Configuration.GetSection("SonnetDBServer"));
        builder.Services.PostConfigure<ServerOptions>(options =>
        {
            if (options.Copilot.Docs.Roots.Count == 0)
                options.Copilot.Docs.Roots.AddRange(DefaultCopilotDocsRoots);
        });

        Configure(builder);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        var serverOptions = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        ConfigureMiddleware(app, serverOptions);
        app.MapSonnetDbEndpoints(serverOptions);
        return app;
    }

    private static readonly string[] DefaultCopilotDocsRoots =
    [
        "./docs",
        "./web/help",
        "./src/SonnetDB/wwwroot/help",
    ];

    private static void Configure(WebApplicationBuilder builder)
    {
        ConfigureOpenTelemetry(builder);

        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.Converters.Add(new GeoJsonConverter());
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonContext.Default);
        });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ServerMetrics>();
        builder.Services.AddSingleton<EventBroadcaster>();
        builder.Services.AddSingleton<SonnetDbMcpContextAccessor>();
        builder.Services.AddSingleton<SonnetDbMcpSchemaCache>();
        builder.Services.AddSingleton<SonnetDbMcpExplainSqlService>();
        builder.Services.AddSingleton(sp =>
        {
            var serverOptions = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
            var registry = new TsdbRegistry(serverOptions.DataRoot, sp.GetRequiredService<EventBroadcaster>());
            if (serverOptions.AutoLoadExistingDatabases)
                registry.LoadExisting();
            return registry;
        });

        // PR #34c：周期性指标快照后台服务
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
            var serverOptions = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
            AiCopilotBridge.Apply(store.Get(), serverOptions.Copilot.Chat, serverOptions.Copilot.Embedding);
            return store;
        });
        builder.Services.AddSingleton<CopilotReadiness>();
        builder.Services.AddHttpClient();
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

        // PR #64：文档摄入与检索（Knowledge 库 __copilot__）
        // 当前在线 Copilot 流程已切到 ai.sonnetdb.com，下面的本地索引服务仅保留为兼容/手动诊断能力。
        builder.Services.AddSingleton<DocsSourceScanner>();
        builder.Services.AddSingleton<DocsChunker>();
        builder.Services.AddSingleton<DocsIngestor>();
        builder.Services.AddSingleton<DocsSearchService>();
        builder.Services.AddHostedService<CopilotDocsIngestionService>();

        // PR #65：技能库 __copilot__.skills + 技能路由
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
                options.ConfigureSessionOptions = static (context, serverOptions, _) =>
                {
                    if (context.Items.TryGetValue(SonnetDbMcpContextAccessor.DatabaseNameItemKey, out var value)
                        && value is string databaseName)
                    {
                        serverOptions.ServerInstructions =
                            $"SonnetDB MCP endpoint for database '{databaseName}'. " +
                            "Only read-only tools and resources are exposed. " +
                            "Prefer bounded queries via SQL LIMIT / FETCH or the maxRows tool parameter.";
                    }

                    return Task.CompletedTask;
                };
            })
            .WithTools<SonnetDbMcpTools>()
            .WithResources<SonnetDbMcpResources>();

        // 在应用关闭时优雅释放所有 Tsdb 实例
        builder.Services.AddSingleton<IHostedService>(sp => new RegistryShutdownHook(sp.GetRequiredService<TsdbRegistry>()));
    }

    private static string GetSystemDirectory(IServiceProvider services)
    {
        var serverOptions = services.GetRequiredService<IOptions<ServerOptions>>().Value;
        var systemDirectory = Path.Combine(serverOptions.DataRoot, ".system");
        Directory.CreateDirectory(systemDirectory);
        return systemDirectory;
    }

    /// <summary>
    /// M17 #90：OpenTelemetry 引导。指标 / 追踪默认开启（订阅 Core 的 BCL Meter / ActivitySource）；
    /// OTLP 导出走标准 <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> 环境变量（未设置则不导出）；
    /// Console exporter 仅 Development 环境启用。Resource 自动携带
    /// <c>service.name=sonnetdb</c> / <c>service.version</c> / <c>service.instance.id</c> / <c>host.name</c>。
    /// M17 #91：<c>Observability:Prometheus:Enabled=true</c> 时追加 Prometheus exporter，
    /// <c>/metrics</c> 由 <c>MapPrometheusScrapingEndpoint</c> 接管（见 HealthEndpoints）。
    /// </summary>
    private static void ConfigureOpenTelemetry(WebApplicationBuilder builder)
    {
        string serviceVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        bool hasOtlpEndpoint = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
        bool prometheusEnabled = builder.Configuration
            .GetValue<bool>("SonnetDBServer:Observability:Prometheus:Enabled");

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: "sonnetdb",
                    serviceVersion: serviceVersion,
                    serviceInstanceId: Environment.MachineName + ":" + Environment.ProcessId)
                .AddAttributes([new KeyValuePair<string, object>("host.name", Environment.MachineName)]))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(SonnetDbMeter.MeterName)
                    .AddMeter("SonnetDB.Server")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (hasOtlpEndpoint)
                    metrics.AddOtlpExporter();
                if (prometheusEnabled)
                    metrics.AddPrometheusExporter();
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(SonnetDbActivitySource.SourceName)
                    .AddSource("SonnetDB.Copilot")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (hasOtlpEndpoint)
                    tracing.AddOtlpExporter();
                if (builder.Environment.IsDevelopment())
                    tracing.AddConsoleExporter();
            });
    }

    private static void ConfigureMiddleware(WebApplication app, ServerOptions serverOptions)
    {
        var userStore = app.Services.GetRequiredService<UserStore>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        // Bearer 认证（在所有 endpoint 之前）
        app.Use(async (context, next) =>
        {
            var status = BearerAuthMiddleware.Authenticate(context, serverOptions, userStore);
            if (status is not null)
            {
                context.Response.StatusCode = status.Value;
                context.Response.ContentType = "application/json; charset=utf-8";
                var err = new ErrorResponse(status.Value == 401 ? "unauthorized" : "forbidden",
                    status.Value == 401 ? "缺失或无效的 Bearer token。" : "权限不足。");
                await JsonSerializer.SerializeAsync(context.Response.Body, err, ServerJsonContext.Default.ErrorResponse).ConfigureAwait(false);
                return;
            }
            await next(context).ConfigureAwait(false);
        });

        app.Use(async (context, next) =>
        {
            if (await SonnetDbEndpoints.TryBindMcpDatabaseAsync(context, registry, grants).ConfigureAwait(false))
                return;
            await next(context).ConfigureAwait(false);
        });
    }
}

internal sealed class RegistryShutdownHook(TsdbRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.Dispose();
        return Task.CompletedTask;
    }
}
