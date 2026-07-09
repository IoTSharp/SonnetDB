using System.Text;
using System.Windows.Forms;

namespace SonnetDB.Studio;

/// <summary>
/// 提供 Studio 桌面壳可用的原生打开 / 保存文件对话框。
/// </summary>
internal sealed class StudioFileDialogService
{
    private const long DefaultMaxOpenBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 使用原生文件对话框选择并读取一个文本文件。
    /// </summary>
    /// <param name="request">打开文件请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<StudioOpenFileResult> OpenTextFileAsync(
        StudioOpenFileRequest request,
        CancellationToken cancellationToken)
    {
        var selected = StaDialogRunner.Run(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? "Open file" : request.Title,
                Filter = BuildFilter(request.Filters),
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
            };

            return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
        });

        if (selected is null)
            return new StudioOpenFileResult(true, null, null, null);

        var info = new FileInfo(selected);
        var maxBytes = request.MaxBytes is > 0 ? request.MaxBytes.Value : DefaultMaxOpenBytes;
        if (info.Length > maxBytes)
        {
            return new StudioOpenFileResult(
                false,
                info.Name,
                null,
                $"File is larger than the Studio text import limit ({maxBytes} bytes).");
        }

        var content = await File.ReadAllTextAsync(selected, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return new StudioOpenFileResult(false, info.Name, content, null);
    }

    /// <summary>
    /// 使用原生文件对话框选择路径并写入文本内容。
    /// </summary>
    /// <param name="request">保存文件请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<StudioSaveFileResult> SaveTextFileAsync(
        StudioSaveFileRequest request,
        CancellationToken cancellationToken)
    {
        var selected = StaDialogRunner.Run(() =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? "Save file" : request.Title,
                FileName = string.IsNullOrWhiteSpace(request.SuggestedName) ? "sonnetdb-export.txt" : request.SuggestedName,
                Filter = BuildFilter(request.Filters),
                OverwritePrompt = true,
                RestoreDirectory = true,
            };

            return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
        });

        if (selected is null)
            return new StudioSaveFileResult(true, null, null);

        await File.WriteAllTextAsync(selected, request.Content ?? string.Empty, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return new StudioSaveFileResult(false, Path.GetFileName(selected), null);
    }

    private static string BuildFilter(StudioFileDialogFilter[]? filters)
    {
        if (filters is null || filters.Length == 0)
            return "All files (*.*)|*.*";

        var parts = new List<string>();
        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Name) || filter.Extensions.Length == 0)
                continue;

            var patterns = filter.Extensions
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Select(extension => extension.Trim().TrimStart('.'))
                .Where(extension => extension.Length > 0)
                .Select(extension => "*." + extension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (patterns.Length == 0)
                continue;

            parts.Add($"{filter.Name} ({string.Join("; ", patterns)})|{string.Join(";", patterns)}");
        }

        parts.Add("All files (*.*)|*.*");
        return string.Join("|", parts);
    }
}

/// <summary>
/// 在 STA 线程中执行需要 Windows 桌面 apartment 的对话框代码。
/// </summary>
internal static class StaDialogRunner
{
    /// <summary>
    /// 运行指定委托并把返回值或异常传回调用线程。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="action">需要在 STA 线程执行的操作。</param>
    public static T Run<T>(Func<T> action)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (error is not null)
            throw error;

        return result!;
    }
}
