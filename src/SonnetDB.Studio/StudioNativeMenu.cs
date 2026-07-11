using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using NativeWebHost;

namespace SonnetDB.Studio;

/// <summary>
/// 将 Studio 动作目录映射为 Win32 窗口菜单，并把命令转发到 WebView。
/// </summary>
internal sealed class StudioNativeMenu : IDisposable
{
    private const uint MenuString = 0x0000;
    private const uint MenuPopup = 0x0010;
    private const uint MenuSeparator = 0x0800;
    private const uint WindowMessageCommand = 0x0111;
    private const uint WindowMessageClose = 0x0010;
    private const int WindowProcedureIndex = -4;

    private readonly nint _windowHandle;
    private readonly nint _menuHandle;
    private readonly IJsBridge _jsBridge;
    private readonly Dictionary<int, StudioDesktopActionDefinition> _actions;
    private readonly WindowProcedureCallback _windowProcedureCallback;
    private readonly nint _windowProcedureCallbackPointer;
    private readonly nint _previousWindowProcedure;
    private bool _disposed;

    private StudioNativeMenu(nint windowHandle, IJsBridge jsBridge)
    {
        _windowHandle = windowHandle;
        _jsBridge = jsBridge;
        _actions = StudioDesktopActions.Items.ToDictionary(item => item.NativeId);
        _windowProcedureCallback = HandleWindowMessage;
        _windowProcedureCallbackPointer = Marshal.GetFunctionPointerForDelegate(_windowProcedureCallback);
        _menuHandle = CreateStudioMenu();

        Marshal.SetLastPInvokeError(0);
        _previousWindowProcedure = SetWindowProcedure(
            _windowHandle,
            _windowProcedureCallbackPointer);
        if (_previousWindowProcedure == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            DestroyMenu(_menuHandle);
            throw new Win32Exception(error, "无法安装 Studio 窗口菜单回调。");
        }

        if (SetMenu(_windowHandle, _menuHandle) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            SetWindowProcedure(_windowHandle, _previousWindowProcedure);
            DestroyMenu(_menuHandle);
            throw new Win32Exception(error, "无法将 Studio 菜单附加到主窗口。");
        }

        DrawMenuBar(_windowHandle);
    }

    /// <summary>
    /// 等待 NativeWebHost 主窗口就绪并附加原生菜单。
    /// </summary>
    /// <param name="windowTitle">宿主窗口标题。</param>
    /// <param name="jsBridge">NativeWebHost JavaScript bridge。</param>
    /// <returns>已附加到主窗口的菜单实例。</returns>
    public static StudioNativeMenu Attach(
        string windowTitle,
        IJsBridge jsBridge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowTitle);
        ArgumentNullException.ThrowIfNull(jsBridge);

        var windowHandle = FindCurrentProcessWindow();
        if (windowHandle != 0)
            return new StudioNativeMenu(windowHandle, jsBridge);

        throw new InvalidOperationException($"未找到 Studio 主窗口：{windowTitle}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        SetWindowProcedure(_windowHandle, _previousWindowProcedure);
        SetMenu(_windowHandle, 0);
        DrawMenuBar(_windowHandle);
        DestroyMenu(_menuHandle);
        GC.KeepAlive(_windowProcedureCallback);
    }

    private nint HandleWindowMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam)
    {
        try
        {
            if (message == WindowMessageCommand)
            {
                var commandId = unchecked((ushort)(wParam & 0xffff));
                if (_actions.TryGetValue(commandId, out var action))
                {
                    if (action.Id == "app.exit")
                    {
                        PostMessageW(windowHandle, WindowMessageClose, 0, 0);
                    }
                    else
                    {
                        _ = DispatchActionAsync(action.Id);
                    }
                    return 0;
                }
            }
        }
        catch
        {
            // 异常不能越过原生窗口回调边界；未知消息仍交回默认窗口过程。
        }

        return CallWindowProcW(_previousWindowProcedure, windowHandle, message, wParam, lParam);
    }

    private async Task DispatchActionAsync(string actionId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new StudioDesktopActionMessage(actionId),
                StudioBridgeJsonContext.Default.StudioDesktopActionMessage);
            await _jsBridge.PostMessageAsync(StudioDesktopActions.EventName, payload).ConfigureAwait(false);
        }
        catch
        {
            // 页面导航或退出期间 WebView 可能已释放，菜单动作可安全忽略。
        }
    }

    private static nint CreateStudioMenu()
    {
        var rootMenu = CreateMenu();
        if (rootMenu == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法创建 Studio 菜单栏。");

        try
        {
            foreach (var group in StudioDesktopActions.Items.GroupBy(item => item.Group))
            {
                var popupMenu = CreatePopupMenu();
                if (popupMenu == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"无法创建 {group.Key} 菜单。");

                if (AppendMenuW(rootMenu, MenuPopup, unchecked((nuint)popupMenu), $"&{group.Key}") == 0)
                {
                    DestroyMenu(popupMenu);
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"无法添加顶级菜单：{group.Key}");
                }

                foreach (var item in group)
                {
                    if (item.SeparatorBefore)
                        EnsureMenuResult(AppendMenuW(popupMenu, MenuSeparator, 0, null), "无法添加菜单分隔线。");
                    var label = item.Shortcut is null ? item.Label : $"{item.Label}\t{item.Shortcut}";
                    EnsureMenuResult(
                        AppendMenuW(popupMenu, MenuString, unchecked((nuint)item.NativeId), label),
                        $"无法添加菜单项：{item.Label}");
                }
            }

            return rootMenu;
        }
        catch
        {
            DestroyMenu(rootMenu);
            throw;
        }
    }

    private static void EnsureMenuResult(int result, string message)
    {
        if (result == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), message);
    }

    private static nint FindCurrentProcessWindow()
    {
        // 启动回调期间窗口线程可能正在等待当前任务，不能用 GetWindowText 发送同步消息。
        nint found = 0;
        WindowEnumerationCallback callback = (windowHandle, _) =>
        {
            GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId != Environment.ProcessId)
                return 1;

            found = windowHandle;
            return 0;
        };

        EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return found;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedureCallback(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WindowEnumerationCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll", EntryPoint = "CreateMenu", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateMenu();

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport(
        "user32.dll",
        EntryPoint = "AppendMenuW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern int AppendMenuW(
        nint menuHandle,
        uint flags,
        nuint itemId,
        string? text);

    [DllImport("user32.dll", EntryPoint = "SetMenu", ExactSpelling = true, SetLastError = true)]
    private static extern int SetMenu(nint windowHandle, nint menuHandle);

    [DllImport("user32.dll", EntryPoint = "DrawMenuBar", ExactSpelling = true, SetLastError = true)]
    private static extern int DrawMenuBar(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "DestroyMenu", ExactSpelling = true, SetLastError = true)]
    private static extern int DestroyMenu(nint menuHandle);

    [DllImport("user32.dll", EntryPoint = "EnumWindows", ExactSpelling = true, SetLastError = true)]
    private static extern int EnumWindows(WindowEnumerationCallback callback, nint parameter);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    private static extern int PostMessageW(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", ExactSpelling = true)]
    private static extern nint CallWindowProcW(
        nint previousWindowProcedure,
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowLongPtrW(
        nint windowHandle,
        int index,
        nint newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true, SetLastError = true)]
    private static extern int SetWindowLongW(
        nint windowHandle,
        int index,
        int newValue);

    private static nint SetWindowProcedure(nint windowHandle, nint procedure)
        => nint.Size == 8
            ? SetWindowLongPtrW(windowHandle, WindowProcedureIndex, procedure)
            : SetWindowLongW(windowHandle, WindowProcedureIndex, procedure.ToInt32());
}
