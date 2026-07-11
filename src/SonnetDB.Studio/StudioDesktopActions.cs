namespace SonnetDB.Studio;

/// <summary>
/// Studio 原生菜单与 Web 工作台共享的动作目录。
/// </summary>
internal static class StudioDesktopActions
{
    /// <summary>
    /// NativeWebHost 向 WebView 派发桌面动作时使用的事件名。
    /// </summary>
    public const string EventName = "studio.desktop-action";

    /// <summary>
    /// 原生菜单动作定义；原生编号限定在 Studio 自有命令区间内。
    /// </summary>
    public static readonly StudioDesktopActionDefinition[] Items =
    [
        new(41001, "query.new", "New Query", "query.new", "File", "Ctrl+N"),
        new(41002, "file.open", "Open SQL...", "dialogs.openFile", "File", "Ctrl+O"),
        new(41003, "file.save", "Save SQL As...", "dialogs.saveFile", "File", "Ctrl+S"),
        new(41004, "app.exit", "Exit", "window.close", "File", null, true),
        new(41101, "view.results", "Results", "view.results", "View", "Ctrl+Shift+R"),
        new(41102, "view.history", "History", "view.history", "View", "Ctrl+H"),
        new(41201, "server.start", "Start Local Server", "server.start", "Local Server", null),
        new(41202, "server.stop", "Stop Local Server", "server.stop", "Local Server", null),
        new(41203, "server.health", "Check Health", "server.status", "Local Server", null),
    ];

    /// <summary>
    /// bridge manifest 使用的动作清单。
    /// </summary>
    public static StudioMenuItem[] ManifestItems { get; } = Items
        .Select(item => new StudioMenuItem(
            item.Id,
            item.Label,
            item.Command,
            item.Group,
            item.Shortcut))
        .ToArray();
}

/// <summary>
/// Studio 原生菜单动作定义。
/// </summary>
/// <param name="NativeId">Win32 菜单命令编号。</param>
/// <param name="Id">稳定动作标识。</param>
/// <param name="Label">显示文本。</param>
/// <param name="Command">动作对应的能力命令。</param>
/// <param name="Group">顶级菜单分组。</param>
/// <param name="Shortcut">WebView 内对应的快捷键。</param>
/// <param name="SeparatorBefore">是否在此项之前显示分隔线。</param>
internal sealed record StudioDesktopActionDefinition(
    int NativeId,
    string Id,
    string Label,
    string Command,
    string Group,
    string? Shortcut,
    bool SeparatorBefore = false);
