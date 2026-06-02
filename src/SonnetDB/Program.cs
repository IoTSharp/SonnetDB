using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Mcp;

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
    /// <param name="overrideOptions">可选覆盖配置（如自定义 DataRoot / Tokens）。</param>
    /// <param name="configureServices">测试或宿主可选的附加 DI 覆盖。</param>
    public static WebApplication BuildApp(
        string[] args,
        ServerOptions? overrideOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        // 配置：appsettings + 环境变量 SONNETDB_*
        builder.Configuration.AddEnvironmentVariables(prefix: "SONNETDB_");
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddWindowsService(options => options.ServiceName = "SonnetDB");
        }

        var serverOptions = overrideOptions ?? LoadServerOptions(builder.Configuration);

        Configure(builder, serverOptions);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        ConfigureMiddleware(app, serverOptions);
        MapEndpoints(app, serverOptions);
        return app;
    }

    private static void BindNonAot(Microsoft.Extensions.Configuration.BinderOptions options)
    {
        options.BindNonPublicProperties = false;
    }

    private static ServerOptions LoadServerOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("SonnetDBServer");
        var options = new ServerOptions();

        var dataRoot = section["DataRoot"];
        if (!string.IsNullOrWhiteSpace(dataRoot))
            options.DataRoot = dataRoot;

        var autoLoad = section["AutoLoadExistingDatabases"];
        if (bool.TryParse(autoLoad, out var auto))
            options.AutoLoadExistingDatabases = auto;

        var allowAnon = section["AllowAnonymousProbes"];
        if (bool.TryParse(allowAnon, out var anon))
            options.AllowAnonymousProbes = anon;

        var helpDocsRoot = section["HelpDocsRoot"];
        if (!string.IsNullOrWhiteSpace(helpDocsRoot))
            options.HelpDocsRoot = helpDocsRoot;

        var copilot = section.GetSection("Copilot");
        var copilotEnabled = copilot["Enabled"];
        if (bool.TryParse(copilotEnabled, out var enabled))
            options.Copilot.Enabled = enabled;

        var embedding = copilot.GetSection("Embedding");
        var embeddingProvider = embedding["Provider"];
        if (!string.IsNullOrWhiteSpace(embeddingProvider))
            options.Copilot.Embedding.Provider = embeddingProvider;
        var localModelPath = embedding["LocalModelPath"];
        if (!string.IsNullOrWhiteSpace(localModelPath))
            options.Copilot.Embedding.LocalModelPath = localModelPath;
        var embeddingEndpoint = embedding["Endpoint"];
        if (!string.IsNullOrWhiteSpace(embeddingEndpoint))
            options.Copilot.Embedding.Endpoint = embeddingEndpoint;
        var embeddingApiKey = embedding["ApiKey"];
        if (!string.IsNullOrWhiteSpace(embeddingApiKey))
            options.Copilot.Embedding.ApiKey = embeddingApiKey;
        var embeddingModel = embedding["Model"];
        if (!string.IsNullOrWhiteSpace(embeddingModel))
            options.Copilot.Embedding.Model = embeddingModel;
        var embeddingTimeoutSeconds = embedding["TimeoutSeconds"];
        if (int.TryParse(embeddingTimeoutSeconds, out var embeddingTimeout))
            options.Copilot.Embedding.TimeoutSeconds = embeddingTimeout;

        var chat = copilot.GetSection("Chat");
        var chatProvider = chat["Provider"];
        if (!string.IsNullOrWhiteSpace(chatProvider))
            options.Copilot.Chat.Provider = chatProvider;
        var chatEndpoint = chat["Endpoint"];
        if (!string.IsNullOrWhiteSpace(chatEndpoint))
            options.Copilot.Chat.Endpoint = chatEndpoint;
        var chatApiKey = chat["ApiKey"];
        if (!string.IsNullOrWhiteSpace(chatApiKey))
            options.Copilot.Chat.ApiKey = chatApiKey;
        var chatModel = chat["Model"];
        if (!string.IsNullOrWhiteSpace(chatModel))
            options.Copilot.Chat.Model = chatModel;
        var chatTimeoutSeconds = chat["TimeoutSeconds"];
        if (int.TryParse(chatTimeoutSeconds, out var chatTimeout))
            options.Copilot.Chat.TimeoutSeconds = chatTimeout;

        LoadCopilotDocsOptions(copilot.GetSection("Docs"), options.Copilot.Docs);

        var tokens = section.GetSection("Tokens");
        foreach (var child in tokens.GetChildren())
        {
            if (!string.IsNullOrEmpty(child.Key) && !string.IsNullOrEmpty(child.Value))
                options.Tokens[child.Key] = child.Value;
        }
        return options;
    }

    private static void LoadCopilotDocsOptions(IConfigurationSection docs, CopilotDocsOptions options)
    {
        var autoIngestOnStartup = docs["AutoIngestOnStartup"];
        if (bool.TryParse(autoIngestOnStartup, out var autoIngest))
            options.AutoIngestOnStartup = autoIngest;

        var chunkSize = docs["ChunkSize"];
        if (int.TryParse(chunkSize, out var parsedChunkSize))
            options.ChunkSize = parsedChunkSize;

        var chunkOverlap = docs["ChunkOverlap"];
        if (int.TryParse(chunkOverlap, out var parsedChunkOverlap))
            options.ChunkOverlap = parsedChunkOverlap;

        var roots = docs.GetSection("Roots");
        var parsedRoots = new List<string>();
        foreach (var child in roots.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
                parsedRoots.Add(child.Value);
        }

        if (parsedRoots.Count > 0)
            options.Roots = parsedRoots;
    }

    private static void Configure(WebApplicationBuilder builder, ServerOptions serverOptions)
    {
        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.Converters.Add(new GeoJsonConverter());
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonContext.Default);
        });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton<ServerMetrics>();
        builder.Services.AddSingleton<EventBroadcaster>();
        builder.Services.AddSingleton<SonnetDbMcpContextAccessor>();
        builder.Services.AddSingleton<SonnetDbMcpSchemaCache>();
        builder.Services.AddSingleton<SonnetDbMcpExplainSqlService>();
        builder.Services.AddSingleton(sp =>
        {
            var registry = new TsdbRegistry(serverOptions.DataRoot, sp.GetRequiredService<EventBroadcaster>());
            if (serverOptions.AutoLoadExistingDatabases)
                registry.LoadExisting();
            return registry;
        });

        // PR #34c：周期性指标快照后台服务
        builder.Services.AddHostedService<MetricsTickService>();

        // PR #34a：用户 / 权限 / 控制面存储全局只实例。文件位于 <DataRoot>/.system/。
        var systemDirectory = Path.Combine(serverOptions.DataRoot, ".system");
        Directory.CreateDirectory(systemDirectory);
        builder.Services.AddSingleton(_ => new UserStore(systemDirectory));
        builder.Services.AddSingleton(_ => new GrantsStore(systemDirectory));
        builder.Services.AddSingleton(_ => new InstallationStore(systemDirectory));
        builder.Services.AddSingleton(_ =>
        {
            var store = new AiConfigStore(systemDirectory);
            // M16/M2：启动时把已持久化的 sonnetdb.com Cloud Token
            // 同步到 CopilotChatOptions，让 /v1/copilot/chat 直接就绪。
            AiCopilotBridge.Apply(store.Get(), serverOptions.Copilot.Chat, serverOptions.Copilot.Embedding);
            return store;
        });
        builder.Services.AddSingleton(serverOptions.Copilot);
        builder.Services.AddSingleton(serverOptions.Copilot.Embedding);
        builder.Services.AddSingleton(serverOptions.Copilot.Chat);
        builder.Services.AddSingleton(serverOptions.Copilot.Docs);
        builder.Services.AddSingleton<CopilotReadiness>();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var options = sp.GetRequiredService<CopilotEmbeddingOptions>();
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
            var options = sp.GetRequiredService<CopilotChatOptions>();
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
            if (await TryBindMcpDatabaseAsync(context, registry, grants).ConfigureAwait(false))
                return;
            await next(context).ConfigureAwait(false);
        });
    }

    private static void MapEndpoints(WebApplication app, ServerOptions serverOptions)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var users = app.Services.GetRequiredService<UserStore>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var metrics = app.Services.GetRequiredService<ServerMetrics>();
        var installation = app.Services.GetRequiredService<InstallationStore>();
        var copilotReadiness = app.Services.GetRequiredService<CopilotReadiness>();

        // ---- Vue SPA：根 / 承载产品官网，/admin/* 承载管理后台 ----
        app.MapSpa();
        app.MapHelpDocs(serverOptions);

        // ---- 健康 / 指标 ----
        app.MapGet("/healthz", () =>
        {
            var readiness = copilotReadiness.Evaluate();
            var resp = new HealthResponse("ok", registry.Count, metrics.UptimeSeconds, readiness.Enabled, readiness.Ready);
            return Results.Json(resp, ServerJsonContext.Default.HealthResponse);
        });

        app.MapGet("/metrics", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            return ctx.Response.WriteAsync(PrometheusFormatter.Render(metrics, registry));
        });

        // ---- 首次安装 ----
        app.MapGet("/v1/setup/status", () =>
        {
            var users = app.Services.GetRequiredService<UserStore>();
            var visibleDatabaseCount = registry.ListDatabases()
                .Count(static database => !DatabaseAccessEvaluator.IsSystemDatabase(database));
            var status = installation.GetStatus(users.Count, visibleDatabaseCount);
            var resp = new SetupStatusResponse(
                status.NeedsSetup,
                status.SuggestedServerId,
                status.ServerId,
                status.Organization,
                status.UserCount,
                status.DatabaseCount);
            return Results.Json(resp, ServerJsonContext.Default.SetupStatusResponse);
        });

        app.MapMethods("/v1/setup/initialize", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            var users = app.Services.GetRequiredService<UserStore>();
            var visibleDatabaseCount = registry.ListDatabases()
                .Count(static database => !DatabaseAccessEvaluator.IsSystemDatabase(database));
            var status = installation.GetStatus(users.Count, visibleDatabaseCount);
            if (!status.NeedsSetup)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "already_initialized", "SonnetDB Server 已完成首次安装。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SetupInitializeRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不能为空。").ConfigureAwait(false);
                return;
            }

            try
            {
                users.CreateUser(req.Username, req.Password, isSuperuser: true);
                var tokenId = users.ImportToken(req.Username, req.BearerToken);
                var bootstrap = installation.CompleteInitialization(
                    req.ServerId,
                    req.Organization,
                    req.Username,
                    tokenId,
                    users.Count,
                    registry.Count);

                var resp = new SetupInitializeResponse(
                    bootstrap.ServerId,
                    bootstrap.Organization,
                    bootstrap.AdminUserName,
                    req.BearerToken.Trim(),
                    bootstrap.InitialTokenId,
                    IsSuperuser: true);

                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.SetupInitializeResponse).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "setup_conflict", ex.Message).ConfigureAwait(false);
            }
        }));

        // ---- 数据库管理 ----
        app.MapGet("/v1/db", (HttpContext ctx) =>
        {
            var visibleDatabases = DatabaseAccessEvaluator.GetVisibleDatabases(ctx, grants, registry.ListDatabases());
            var resp = new DatabaseListResponse(visibleDatabases);
            return Results.Json(resp, ServerJsonContext.Default.DatabaseListResponse);
        });

        app.MapPost("/v1/db", async (HttpContext ctx) =>
        {
            var role = BearerAuthMiddleware.GetRole(ctx);
            if (!BearerAuthMiddleware.IsAdmin(role))
                return ForbiddenResult("仅 admin 可创建数据库。");
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CreateDatabaseRequest).ConfigureAwait(false);
            if (req is null)
                return BadRequestResult("请求体不可为空。");
            if (!TsdbRegistry.IsValidName(req.Name))
                return BadRequestResult($"非法数据库名 '{req.Name}'。");
            var created = registry.TryCreate(req.Name, out _);
            return Results.Json(new DatabaseOperationResponse(req.Name, created ? "created" : "exists"),
                ServerJsonContext.Default.DatabaseOperationResponse,
                statusCode: created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        app.MapDelete("/v1/db/{db}", (HttpContext ctx, string db) =>
        {
            var role = BearerAuthMiddleware.GetRole(ctx);
            if (!BearerAuthMiddleware.IsAdmin(role))
                return ForbiddenResult("仅 admin 可删除数据库。");
            if (!TsdbRegistry.IsValidName(db))
                return BadRequestResult($"非法数据库名 '{db}'。");
            var dropped = registry.Drop(db);
            return Results.Json(new DatabaseOperationResponse(db, dropped ? "dropped" : "not_found"),
                ServerJsonContext.Default.DatabaseOperationResponse,
                statusCode: dropped ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        });

        // ---- SQL ----
        var controlPlane = app.Services.GetRequiredService<SonnetDB.Sql.Execution.IControlPlane>();
        app.MapPost("/v1/db/{db}/sql", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }
            var scopedControlPlane = CreateScopedControlPlane(ctx, controlPlane, users, grants, registry);
            await SqlEndpointHandler.HandleSingleAsync(ctx, tsdb, db, req, metrics,
                DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write),
                DatabaseAccessEvaluator.IsServerAdmin(ctx),
                scopedControlPlane).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/sql/batch", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlBatchRequest).ConfigureAwait(false);
            if (req is null || req.Statements.Count == 0)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体或 statements 不可为空。").ConfigureAwait(false);
                return;
            }
            var scopedControlPlane = CreateScopedControlPlane(ctx, controlPlane, users, grants, registry);
            await SqlEndpointHandler.HandleBatchAsync(ctx, tsdb, db, req, metrics,
                DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write),
                DatabaseAccessEvaluator.IsServerAdmin(ctx),
                scopedControlPlane).ConfigureAwait(false);
        });

        app.MapGet("/v1/db/{db}/geo/{measurement}/trajectory", async (HttpContext ctx, string db, string measurement) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            await GeoEndpointHandler.HandleTrajectoryAsync(ctx, tsdb, measurement).ConfigureAwait(false);
        });

        // ---- PR #44：批量入库快路径三端点（绕开 SQL parser）----
        // PR #47：批量端点 payload 可达数百 MB，移除 Kestrel 默认 30MB 上限。
        app.MapPost("/v1/db/{db}/measurements/{m}/lp", async (HttpContext ctx, string db, string m) =>
            await HandleBulkAsync(ctx, registry, grants, metrics, db, m, BulkIngestEndpointHandler.Format.LineProtocol).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapPost("/v1/db/{db}/measurements/{m}/json", async (HttpContext ctx, string db, string m) =>
            await HandleBulkAsync(ctx, registry, grants, metrics, db, m, BulkIngestEndpointHandler.Format.Json).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapPost("/v1/db/{db}/measurements/{m}/bulk", async (HttpContext ctx, string db, string m) =>
            await HandleBulkAsync(ctx, registry, grants, metrics, db, m, BulkIngestEndpointHandler.Format.BulkValues).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        // ---- InfluxDB 兼容写入端点：让 Telegraf / EMQX / influx CLI 等生态工具可直接对接 ----
        // v1: POST /write?db=<db>&precision=<n|u|ms|s>
        // v2: POST /api/v2/write?bucket=<db>&org=<ignored>&precision=<ns|us|ms|s>
        // 与 /v1/db/{db}/measurements/{m}/lp 的关键差别：measurement 来自每行 LP，而非 URL。
        app.MapPost("/write", async (HttpContext ctx) =>
            await InfluxLineProtocolEndpointHandler.HandleAsync(
                ctx, registry, grants, metrics, InfluxLineProtocolEndpointHandler.ApiVersion.V1).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapPost("/api/v2/write", async (HttpContext ctx) =>
            await InfluxLineProtocolEndpointHandler.HandleAsync(
                ctx, registry, grants, metrics, InfluxLineProtocolEndpointHandler.ApiVersion.V2).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        // ---- Prometheus Remote Write v1 兼容入站端点 ----
        // POST /api/v1/prom/write?db=<name>，body = snappy(block) + protobuf(prometheus.WriteRequest)
        // 让 Prometheus / VictoriaMetrics agent / Grafana Alloy / OpenTelemetry Collector 直接对接 SonnetDB。
        app.MapPost("/api/v1/prom/write", async (HttpContext ctx) =>
            await PrometheusRemoteWriteEndpointHandler.HandleAsync(ctx, registry, grants, metrics).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        // ---- 控制面 SQL（无 db 路径；admin 全量、动态用户仅自服务）----
        app.MapMethods("/v1/sql", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            var isAdmin = DatabaseAccessEvaluator.IsServerAdmin(ctx);
            if (!isAdmin && BearerAuthMiddleware.GetUser(ctx) is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden",
                    "/v1/sql 仅 admin 或动态用户 token 可调用。").ConfigureAwait(false);
                return;
            }
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrEmpty(req.Sql))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 sql。").ConfigureAwait(false);
                return;
            }
            var scopedControlPlane = CreateScopedControlPlane(ctx, controlPlane, users, grants, registry);
            await SqlEndpointHandler.HandleControlPlaneAsync(ctx, req, metrics, isAdmin, scopedControlPlane).ConfigureAwait(false);
        }));

        // ---- 认证 ----
        app.MapMethods("/v1/auth/login", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.LoginRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 username 与 password。").ConfigureAwait(false);
                return;
            }
            if (!users.VerifyPassword(req.Username, req.Password))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status401Unauthorized, "unauthorized", "用户名或密码错误。").ConfigureAwait(false);
                return;
            }
            var (token, tokenId) = users.IssueToken(req.Username);
            var resp = new LoginResponse(req.Username, token, tokenId, users.IsSuperuser(req.Username));
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.LoginResponse).ConfigureAwait(false);
        }));

        // ---- SSE：实时事件流（指标 / 慢查询 / 数据库事件）----
        var broadcaster = app.Services.GetRequiredService<EventBroadcaster>();
        app.MapGet("/v1/events", async (HttpContext ctx) =>
        {
            await SseEndpointHandler.HandleAsync(ctx, broadcaster, grants).ConfigureAwait(false);
        });

        // ---- Schema API ----
        app.MapGet("/v1/db/{db}/schema", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            await SchemaEndpointHandler.Handle(db, tsdb).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        // ---- Maintenance API ----
        app.MapPost("/v1/db/{db}/maintenance", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.MaintenanceRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            var operation = MaintenanceEndpointHandler.NormalizeOperation(req.Operation);
            if (operation is "backup_verify" or "restore_dry_run" && !DatabaseAccessEvaluator.IsServerAdmin(ctx))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden",
                    $"{operation} 需要 server admin 权限。").ConfigureAwait(false);
                return;
            }

            var requiredPermission = operation == "health_check"
                ? DatabasePermission.Read
                : DatabasePermission.Admin;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, requiredPermission).ConfigureAwait(false))
                return;

            await MaintenanceEndpointHandler.Handle(tsdb, req).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        // ---- AI 助手 ----
        var aiConfigStore = app.Services.GetRequiredService<AiConfigStore>();
        var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
        AiEndpointHandler.Map(
            app,
            aiConfigStore,
            grants,
            registry,
            httpClientFactory,
            serverOptions.Copilot.Chat,
            serverOptions.Copilot.Embedding);
        CopilotChatEndpointHandler.Map(
            app,
            aiConfigStore,
            app.Services.GetRequiredService<ICopilotCloudGatewayClient>(),
            app.Services.GetRequiredService<CopilotLocalToolExecutor>(),
            grants,
            registry);

        // ---- Copilot 文档摄入 / 检索（PR #64）----
        var copilotOptions = serverOptions.Copilot;
        app.MapMethods("/v1/copilot/docs/ingest", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", "/v1/copilot/docs/ingest 仅 admin 可调用。").ConfigureAwait(false);
                return;
            }
            var readiness = copilotReadiness.Evaluate();
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            CopilotIngestRequest? req = null;
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Content-Type"))
                req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotIngestRequest).ConfigureAwait(false);
            req ??= new CopilotIngestRequest();

            var roots = (req.Roots is { Count: > 0 } ? req.Roots : copilotOptions.Docs.Roots).ToArray();
            var ingestor = app.Services.GetRequiredService<DocsIngestor>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var stats = await ingestor.IngestAsync(roots, req.Force, req.DryRun, ctx.RequestAborted).ConfigureAwait(false);
                var resp = new CopilotIngestResponse(
                    stats.ScannedFiles,
                    stats.IndexedFiles,
                    stats.SkippedFiles,
                    stats.DeletedFiles,
                    stats.WrittenChunks,
                    stats.DryRun,
                    sw.Elapsed.TotalMilliseconds);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotIngestResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "ingest_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/docs/search", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            var readiness = copilotReadiness.Evaluate();
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotSearchRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Query))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 query。").ConfigureAwait(false);
                return;
            }

            var requested = req.K is null or <= 0 ? 5 : Math.Min(req.K.Value, 50);
            var search = app.Services.GetRequiredService<DocsSearchService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var hits = await search.SearchAsync(req.Query, requested, ctx.RequestAborted).ConfigureAwait(false);
                var resp = new CopilotSearchResponse(
                    Query: req.Query,
                    Requested: requested,
                    Hits: hits.Select(h => new CopilotSearchHit(h.Source, h.Title, h.Section, h.Content, h.Score)).ToArray(),
                    ElapsedMilliseconds: sw.Elapsed.TotalMilliseconds);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSearchResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "search_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        // ---- Copilot 技能库（PR #65）----
        app.MapMethods("/v1/copilot/skills/reload", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", "/v1/copilot/skills/reload 仅 admin 可调用。").ConfigureAwait(false);
                return;
            }
            var readiness = copilotReadiness.Evaluate();
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            CopilotSkillsIngestRequest? req = null;
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Content-Type"))
                req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotSkillsIngestRequest).ConfigureAwait(false);
            req ??= new CopilotSkillsIngestRequest();

            var root = string.IsNullOrWhiteSpace(req.Root) ? copilotOptions.Skills.Root : req.Root!;
            var registry2 = app.Services.GetRequiredService<SkillRegistry>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var stats = await registry2.IngestAsync(root, req.Force, req.DryRun, ctx.RequestAborted).ConfigureAwait(false);
                var resp = new CopilotSkillsIngestResponse(
                    stats.ScannedSkills,
                    stats.IndexedSkills,
                    stats.SkippedSkills,
                    stats.DeletedSkills,
                    stats.DryRun,
                    sw.Elapsed.TotalMilliseconds);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSkillsIngestResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "skills_reload_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/skills/search", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            var readiness = copilotReadiness.Evaluate();
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotSkillsSearchRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Query))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 query。").ConfigureAwait(false);
                return;
            }

            var requested = req.K is null or <= 0 ? 5 : Math.Min(req.K.Value, 50);
            var skillSearch = app.Services.GetRequiredService<SkillSearchService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var hits = await skillSearch.SearchAsync(req.Query, requested, ctx.RequestAborted).ConfigureAwait(false);
                var resp = new CopilotSkillsSearchResponse(
                    Query: req.Query,
                    Requested: requested,
                    Hits: hits.Select(h => new CopilotSkillsSearchHit(h.Name, h.Description, h.Triggers, h.RequiresTools, h.Score)).ToArray(),
                    ElapsedMilliseconds: sw.Elapsed.TotalMilliseconds);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSkillsSearchResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "skills_search_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/skills/list", new[] { "GET" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }

            var skillRegistry = app.Services.GetRequiredService<SkillRegistry>();
            try
            {
                var items = skillRegistry.List();
                var resp = new CopilotSkillsListResponse(
                    items.Select(h => new CopilotSkillsSearchHit(h.Name, h.Description, h.Triggers, h.RequiresTools, h.Score)).ToArray());
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSkillsListResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "skills_list_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/skills/{name}", new[] { "GET" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }

            var name = ctx.Request.RouteValues["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "缺少 name 路径参数。").ConfigureAwait(false);
                return;
            }

            var skillRegistry = app.Services.GetRequiredService<SkillRegistry>();
            try
            {
                var skill = skillRegistry.Load(name);
                if (skill is null)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "skill_not_found", $"未找到技能 '{name}'。").ConfigureAwait(false);
                    return;
                }

                var resp = new CopilotSkillLoadResponse(
                    skill.Name,
                    skill.Description,
                    skill.Triggers,
                    skill.RequiresTools,
                    skill.Body,
                    skill.Source);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSkillLoadResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "skills_load_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        // ---- Copilot 知识库可视化（M1.5）：只读 status ----
        app.MapMethods("/v1/copilot/knowledge/status", new[] { "GET" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }

            try
            {
                var ingestor = app.Services.GetRequiredService<DocsIngestor>();
                var skillRegistry = app.Services.GetRequiredService<SkillRegistry>();
                var indexState = await ingestor.GetIndexStateAsync(ctx.RequestAborted).ConfigureAwait(false);
                var skillCount = skillRegistry.List().Count;

                var embeddingProvider = app.Services.GetRequiredService<IEmbeddingProvider>();
                var providerName = copilotOptions.Embedding.Provider ?? "builtin";
                var fallback = embeddingProvider is BuiltinHashEmbeddingProvider builtin && builtin.IsFallback;

                var docsRoots = copilotOptions.Docs.Roots
                    .Where(static root => !string.IsNullOrWhiteSpace(root))
                    .Select(static root => Path.IsPathRooted(root) ? Path.GetFullPath(root) : Path.GetFullPath(root, Directory.GetCurrentDirectory()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var resp = new CopilotKnowledgeStatusResponse(
                    Enabled: true,
                    EmbeddingProvider: providerName,
                    EmbeddingFallback: fallback,
                    VectorDimension: BuiltinHashEmbeddingProvider.VectorDimension,
                    DocsRoots: docsRoots,
                    IndexedFiles: indexState.IndexedFiles,
                    IndexedChunks: indexState.IndexedChunks,
                    LastIngestedUtc: indexState.LastIngestedUtc,
                    SkillCount: skillCount);

                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotKnowledgeStatusResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "knowledge_status_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        // ---- MCP：按数据库绑定的 Streamable HTTP 端点 ----
        app.MapMcp("/mcp/{db}");
    }

    private static async Task HandleBulkAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        ServerMetrics metrics,
        string db,
        string measurement,
        BulkIngestEndpointHandler.Format format)
    {
        if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
            return;
        var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
        if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Write).ConfigureAwait(false))
            return;
        if (string.IsNullOrWhiteSpace(measurement) || measurement.Length > 255)
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法 measurement 名 '{measurement}'。").ConfigureAwait(false);
            return;
        }
        await BulkIngestEndpointHandler.HandleAsync(ctx, tsdb, measurement, format, metrics).ConfigureAwait(false);
    }

    private static bool TryResolveDatabase(HttpContext ctx, TsdbRegistry registry, string db, out Engine.Tsdb tsdb)
    {
        if (!TsdbRegistry.IsValidName(db))
        {
            _ = WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", $"非法数据库名 '{db}'。");
            tsdb = null!;
            return false;
        }
        if (!registry.TryGet(db, out tsdb))
        {
            _ = WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found", $"数据库 '{db}' 不存在。");
            return false;
        }
        return true;
    }

    private static async Task WriteSimpleErrorAsync(HttpContext ctx, int statusCode, string code, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (ctx.Request.ContentLength == 0)
            return null;
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> TryBindMcpDatabaseAsync(HttpContext ctx, TsdbRegistry registry, GrantsStore grants)
    {
        if (!ctx.Request.Path.StartsWithSegments("/mcp", out var remaining))
            return false;

        var tail = remaining.Value;
        if (string.IsNullOrWhiteSpace(tail))
            return false;

        var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var databaseName = segments[0];
        if (!TsdbRegistry.IsValidName(databaseName))
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法数据库名 '{databaseName}'。").ConfigureAwait(false);
            return true;
        }

        if (!registry.TryGet(databaseName, out var tsdb))
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found",
                $"数据库 '{databaseName}' 不存在。").ConfigureAwait(false);
            return true;
        }

        var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, databaseName);
        if (!await TryRequireDatabasePermissionAsync(ctx, databaseName, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
            return true;

        ctx.Items[SonnetDbMcpContextAccessor.DatabaseNameItemKey] = databaseName;
        ctx.Items[SonnetDbMcpContextAccessor.TsdbItemKey] = tsdb;
        return false;
    }

    private static SonnetDB.Sql.Execution.IControlPlane CreateScopedControlPlane(
        HttpContext ctx,
        SonnetDB.Sql.Execution.IControlPlane controlPlane,
        UserStore users,
        GrantsStore grants,
        TsdbRegistry registry)
        => new ScopedDatabaseListControlPlane(
            controlPlane,
            users,
            () => DatabaseAccessEvaluator.GetVisibleDatabases(ctx, grants, registry.ListDatabases()),
            BearerAuthMiddleware.GetUser(ctx));

    private static async Task<bool> TryRequireDatabasePermissionAsync(
        HttpContext ctx,
        string db,
        DatabasePermission actualPermission,
        DatabasePermission requiredPermission)
    {
        if (DatabaseAccessEvaluator.HasPermission(actualPermission, requiredPermission))
            return true;

        await WriteSimpleErrorAsync(
            ctx,
            StatusCodes.Status403Forbidden,
            "forbidden",
            $"当前凭据对数据库 '{db}' 没有 {requiredPermission.ToString().ToLowerInvariant()} 权限。").ConfigureAwait(false);
        return false;
    }

    private static IResult ForbiddenResult(string message)
        => Results.Json(new ErrorResponse("forbidden", message),
            ServerJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

    private static IResult BadRequestResult(string message)
        => Results.Json(new ErrorResponse("bad_request", message),
            ServerJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status400BadRequest);
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
