using NativeWebHost;

namespace SonnetDB.Studio;

/// <summary>
/// 在 NativeWebHost 主窗口创建后安装并维护 Studio 原生菜单。
/// </summary>
internal sealed class StudioDesktopApp : IDesktopApp, IWindowAwareDesktopApp
{
    private readonly string _windowTitle;
    private StudioNativeMenu? _mainMenu;

    /// <summary>
    /// 创建 Studio 桌面生命周期适配器。
    /// </summary>
    /// <param name="windowTitle">主窗口标题。</param>
    public StudioDesktopApp(string windowTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowTitle);
        _windowTitle = windowTitle;
    }

    /// <inheritdoc />
    public Task OnStartAsync(IWebViewAdapter adapter, CancellationToken cancellationToken)
    {
        EnsureMainMenu(_windowTitle, adapter.JsBridge);
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

        EnsureMainMenu(
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
