using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SonnetDB.Studio;

/// <summary>
/// Studio 桌面壳旁路的本地 HTTP bridge，仅监听 loopback 并要求启动期 token。
/// </summary>
internal sealed class StudioBridgeHost : IAsyncDisposable
{
    private const string TokenHeader = "X-SonnetDB-Studio-Bridge-Token";
    private readonly StudioHostOptions _options;
    private readonly StudioConnectionLibrary _connections;
    private readonly StudioManagedServerHost _managedServer;
    private string _selectedDataRoot;
    private string _selectedManagedServerUrl;
    private WebApplication? _app;

    /// <summary>
    /// 创建 Studio bridge。
    /// </summary>
    /// <param name="options">Studio 启动参数。</param>
    public StudioBridgeHost(StudioHostOptions options)
    {
        _options = options;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        BaseUrl = $"http://127.0.0.1:{options.BridgePort}";
        _connections = new StudioConnectionLibrary(options.ConnectionLibraryPath, options.ManagedServerUrl);
        _managedServer = new StudioManagedServerHost(options.ServerExecutable, options.KeepManagedServer);
        _selectedDataRoot = options.DataRoot;
        _selectedManagedServerUrl = options.ManagedServerUrl;
    }

    /// <summary>
    /// bridge 根地址。
    /// </summary>
    public string BaseUrl { get; }

    /// <summary>
    /// bridge API 根地址。
    /// </summary>
    public string EndpointUrl => BaseUrl + "/studio-bridge";

    /// <summary>
    /// 当前进程启动期访问 token。
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// 启动本地 bridge HTTP 服务。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = Assembly.GetExecutingAssembly().GetName().Name,
        });
        builder.WebHost.UseUrls(BaseUrl);
        builder.Logging.ClearProviders();
        builder.Services.AddCors();

        var app = builder.Build();
        app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        app.Use(AuthorizeBridgeRequestAsync);

        var group = app.MapGroup("/studio-bridge");
        group.MapGet("/manifest", WriteManifestAsync);
        group.MapGet("/connections", WriteConnectionsAsync);
        group.MapPut("/connections", SaveConnectionsAsync);
        group.MapPost("/dialogs/open-file", OpenFileAsync);
        group.MapPost("/dialogs/save-file", SaveFileAsync);
        group.MapPost("/dialogs/open-binary", OpenBinaryFileAsync);
        group.MapPost("/dialogs/save-binary", SaveBinaryFileAsync);
        group.MapPost("/dialogs/select-directory", SelectDirectoryAsync);
        group.MapGet("/server/status", WriteServerStatusAsync);
        group.MapPost("/server/start", StartServerAsync);
        group.MapPost("/server/stop", StopServerAsync);

        _app = app;
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动默认托管本地 server。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<StudioManagedServerStatus> StartManagedServerAsync(CancellationToken cancellationToken)
        => _managedServer.StartAsync(_selectedDataRoot, _selectedManagedServerUrl, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        await _managedServer.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AuthorizeBridgeRequestAsync(HttpContext context, RequestDelegate next)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!IsAuthorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Studio bridge token is missing or invalid.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpContext context)
    {
        var provided = context.Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrWhiteSpace(provided))
            provided = context.Request.Query["token"].ToString();
        if (string.IsNullOrWhiteSpace(provided))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(Token);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private async Task WriteManifestAsync(HttpContext context)
    {
        var status = await _managedServer.GetStatusAsync(
            _selectedDataRoot,
            _selectedManagedServerUrl,
            context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, BuildManifest(status), StudioBridgeJsonContext.Default.StudioBridgeManifest).ConfigureAwait(false);
    }

    private async Task WriteConnectionsAsync(HttpContext context)
    {
        var snapshot = await _connections.LoadAsync(context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, snapshot, StudioBridgeJsonContext.Default.StudioConnectionLibrarySnapshot).ConfigureAwait(false);
    }

    private async Task SaveConnectionsAsync(HttpContext context)
    {
        var snapshot = await ReadJsonAsync(context, StudioBridgeJsonContext.Default.StudioConnectionLibrarySnapshot).ConfigureAwait(false);
        if (snapshot is null)
        {
            await BadRequestAsync(context, "Connection library payload is required.").ConfigureAwait(false);
            return;
        }

        await _connections.SaveAsync(snapshot, context.RequestAborted).ConfigureAwait(false);
        var saved = await _connections.LoadAsync(context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, saved, StudioBridgeJsonContext.Default.StudioConnectionLibrarySnapshot).ConfigureAwait(false);
    }

    private async Task OpenFileAsync(HttpContext context)
    {
        var request = await ReadJsonAsync(context, StudioBridgeJsonContext.Default.StudioOpenFileRequest).ConfigureAwait(false)
            ?? new StudioOpenFileRequest(null, null, null);
        var dialogs = new StudioFileDialogService();
        var result = await dialogs.OpenTextFileAsync(request, context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, result, StudioBridgeJsonContext.Default.StudioOpenFileResult).ConfigureAwait(false);
    }

    private async Task SaveFileAsync(HttpContext context)
    {
        var request = await ReadJsonAsync(context, StudioBridgeJsonContext.Default.StudioSaveFileRequest).ConfigureAwait(false);
        if (request is null)
        {
            await BadRequestAsync(context, "Save file payload is required.").ConfigureAwait(false);
            return;
        }

        var dialogs = new StudioFileDialogService();
        var result = await dialogs.SaveTextFileAsync(request, context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, result, StudioBridgeJsonContext.Default.StudioSaveFileResult).ConfigureAwait(false);
    }

    private async Task OpenBinaryFileAsync(HttpContext context)
    {
        var request = await ReadJsonAsync(context, StudioBridgeJsonContext.Default.StudioOpenBinaryFileRequest).ConfigureAwait(false)
            ?? new StudioOpenBinaryFileRequest(null, null);
        var file = new StudioFileDialogService().OpenBinaryFile(request);
        if (file is null)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = file.Length;
        context.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(file.Name)}";
        await context.Response.SendFileAsync(file.FullName, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task SaveBinaryFileAsync(HttpContext context)
    {
        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is not null && !bodySizeFeature.IsReadOnly)
            bodySizeFeature.MaxRequestBodySize = null;
        var title = context.Request.Query["title"].ToString();
        var suggestedName = context.Request.Query["suggestedName"].ToString();
        var result = await new StudioFileDialogService().SaveBinaryFileAsync(
            title,
            suggestedName,
            context.Request.Body,
            context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, result, StudioBridgeJsonContext.Default.StudioSaveFileResult).ConfigureAwait(false);
    }

    private async Task SelectDirectoryAsync(HttpContext context)
    {
        var request = await ReadJsonAsync(context, StudioBridgeJsonContext.Default.StudioSelectDirectoryRequest).ConfigureAwait(false)
            ?? new StudioSelectDirectoryRequest(null, null);
        var result = new StudioFileDialogService().SelectDirectory(request);
        await WriteJsonAsync(context, result, StudioBridgeJsonContext.Default.StudioSelectDirectoryResult).ConfigureAwait(false);
    }

    private async Task WriteServerStatusAsync(HttpContext context)
    {
        var status = await _managedServer.GetStatusAsync(
            _selectedDataRoot,
            _selectedManagedServerUrl,
            context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, status, StudioBridgeJsonContext.Default.StudioManagedServerStatus).ConfigureAwait(false);
    }

    private async Task StartServerAsync(HttpContext context)
    {
        var request = await ReadJsonAsync(context, StudioBridgeJsonContext.Default.StudioManagedServerRequest).ConfigureAwait(false)
            ?? new StudioManagedServerRequest(null, null);
        _selectedDataRoot = string.IsNullOrWhiteSpace(request.DataRoot) ? _selectedDataRoot : Path.GetFullPath(request.DataRoot);
        _selectedManagedServerUrl = string.IsNullOrWhiteSpace(request.Url) ? _selectedManagedServerUrl : request.Url.Trim().TrimEnd('/');
        var status = await _managedServer.StartAsync(
            _selectedDataRoot,
            _selectedManagedServerUrl,
            context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, status, StudioBridgeJsonContext.Default.StudioManagedServerStatus).ConfigureAwait(false);
    }

    private async Task StopServerAsync(HttpContext context)
    {
        var request = await ReadJsonAsync(context, StudioBridgeJsonContext.Default.StudioManagedServerRequest).ConfigureAwait(false)
            ?? new StudioManagedServerRequest(null, null);
        var status = await _managedServer.StopAsync(
            string.IsNullOrWhiteSpace(request.DataRoot) ? _selectedDataRoot : request.DataRoot,
            string.IsNullOrWhiteSpace(request.Url) ? _selectedManagedServerUrl : request.Url,
            context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, status, StudioBridgeJsonContext.Default.StudioManagedServerStatus).ConfigureAwait(false);
    }

    private StudioBridgeManifest BuildManifest(StudioManagedServerStatus status)
        => new(
            "studio-desktop",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            _options.ServerUrl,
            _selectedManagedServerUrl,
            _selectedDataRoot,
            [
                "dialogs.openFile",
                "dialogs.saveFile",
                "dialogs.openBinary",
                "dialogs.saveBinary",
                "dialogs.selectDirectory",
                "connections.diskLibrary",
                "server.managedLocal",
                "menu.desktopActions",
                "menu.native",
            ],
            StudioDesktopActions.ManifestItems,
            status);

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                typeInfo,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, T value, JsonTypeInfo<T> typeInfo)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            typeInfo,
            context.RequestAborted).ConfigureAwait(false);
    }

    private static Task BadRequestAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsync(message, context.RequestAborted);
    }
}
