using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace SonnetDB.Benchmarks.Benchmarks;

internal static class GraphEvidenceProcessIdentityToken
{
    private const int MaximumProcStatCharacters = 4_096;

    internal static string Create(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (OperatingSystem.IsLinux())
        {
            string startTime = ReadLinuxStartTime(process.Id);
            return "linux-proc-start:" + startTime;
        }

        return "utc-start:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool IsExpectedProcessAlive(int processId, string identityToken)
    {
        if (processId <= 0 || string.IsNullOrWhiteSpace(identityToken))
            return false;

        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited
                && string.Equals(Create(process), identityToken, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static string ReadLinuxStartTime(int processId)
    {
        string stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
        if (stat.Length is 0 or > MaximumProcStatCharacters)
            throw new InvalidDataException("Linux process stat 长度无效。");

        int commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0 || commandEnd + 1 >= stat.Length)
            throw new InvalidDataException("Linux process stat 缺少 command 终止符。");

        string[] fields = stat[(commandEnd + 1)..].Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        const int startTimeIndexAfterCommand = 19;
        if (fields.Length <= startTimeIndexAfterCommand
            || !ulong.TryParse(
                fields[startTimeIndexAfterCommand],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong startTime))
        {
            throw new InvalidDataException("Linux process stat 缺少有效 starttime 字段。");
        }

        return startTime.ToString(CultureInfo.InvariantCulture);
    }
}
