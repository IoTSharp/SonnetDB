using System.Text.Json;

namespace SonnetDB.Parity.Runner.Reporting;

/// <summary>
/// 把 <see cref="ParityReport"/> 写为 <c>report.json</c>（源生成序列化）。
/// </summary>
public static class JsonReporter
{
    /// <summary>
    /// 将报告写入 <paramref name="reportDirectory"/> 下的 <c>report.json</c>。
    /// </summary>
    /// <param name="report">待写出的报告。</param>
    /// <param name="reportDirectory">报告目录（不存在则创建）。</param>
    /// <returns>写出的文件完整路径。</returns>
    public static async Task<string> WriteAsync(ParityReport report, string reportDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);

        Directory.CreateDirectory(reportDirectory);
        var path = Path.Combine(reportDirectory, "report.json");
        var json = JsonSerializer.Serialize(report, ParityJsonContext.Default.ParityReport);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        return path;
    }
}
