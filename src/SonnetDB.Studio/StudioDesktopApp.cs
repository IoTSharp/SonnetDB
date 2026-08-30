using System.Text.Json;
using NativeWebHost;

namespace SonnetDB.Studio;

/// <summary>
/// 在 NativeWebHost 主窗口创建后安装并维护 Studio 原生菜单。
/// </summary>
internal sealed class StudioDesktopApp : IDesktopApp, IWindowAwareDesktopApp
{
    internal const string BridgeBootstrapRequestHandler = "studio.bridge.bootstrap.request";
    internal const string BridgeBootstrapEvent = "studio.bridge.bootstrap";

    private readonly string _windowTitle;
    private readonly string? _bridgeBootstrapJson;
    private StudioNativeMenu? _mainMenu;

    /// <summary>
    /// 创建 Studio 桌面生命周期适配器。
    /// </summary>
    /// <param name="windowTitle">主窗口标题。</param>
    /// <param name="bridgeBootstrap">仅注入当前 WebView 内存的 bridge 启动配置。</param>
    public StudioDesktopApp(string windowTitle, StudioBridgeBootstrap? bridgeBootstrap = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowTitle);
        _windowTitle = windowTitle;
        _bridgeBootstrapJson = bridgeBootstrap is null
            ? null
            : JsonSerializer.Serialize(
                bridgeBootstrap,
                StudioBridgeJsonContext.Default.StudioBridgeBootstrap);
    }

    /// <inheritdoc />
    public Task OnStartAsync(IWebViewAdapter adapter, CancellationToken cancellationToken)
    {
        ConfigureAdapter(_windowTitle, adapter.JsBridge);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnClosingAsync(CancellationToken cancellationToken)
    {
        _mainMenu?.Dispose();
        _mainMenu = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnWindowStartAsync(
        NativeWebWindowContext context,
        CancellationToken cancellationToken)
    {
        if (!context.IsMainWindow)
            return Task.CompletedTask;

        ConfigureAdapter(
            context.Options.Title,
            context.Adapter.JsBridge);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnWindowClosingAsync(
        NativeWebWindowContext context,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void ConfigureAdapter(string windowTitle, IJsBridge jsBridge)
    {
        if (_bridgeBootstrapJson is not null)
        {
            jsBridge.RegisterHandler(
                BridgeBootstrapRequestHandler,
                async _ =>
                {
                    await jsBridge.PostMessageAsync(
                        BridgeBootstrapEvent,
                        _bridgeBootstrapJson).ConfigureAwait(false);
                    return "null";
                });
        }

        EnsureMainMenu(windowTitle, jsBridge);
    }

    private void EnsureMainMenu(string windowTitle, IJsBridge jsBridge)
    {
        if (_mainMenu is not null)
            return;

        try
        {
            _mainMenu = StudioNativeMenu.Attach(windowTitle, jsBridge);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Studio 原生菜单安装失败：{exception.Message}");
        }
    }
}
