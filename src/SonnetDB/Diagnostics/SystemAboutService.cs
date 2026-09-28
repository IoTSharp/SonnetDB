using System.Reflection;
using System.Runtime.InteropServices;
using SonnetDB.Contracts;

namespace SonnetDB.Diagnostics;

/// <summary>
/// 采集管理后台“关于”页面需要的只读主机信息。
/// </summary>
internal sealed class SystemAboutService
{
    private const string Unknown = "未检测到";

    /// <summary>
    /// 采集当前服务进程所在主机的信息。
    /// </summary>
    public SystemAboutResponse Capture()
    {
        var memory = ReadMemory();
        return new SystemAboutResponse(
            GetServerVersion(),
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            Environment.OSVersion.VersionString,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            new CpuAboutInfo(ReadCpuName(), Environment.ProcessorCount, ReadCpuSpeedMHz()),
            memory,
            ReadDisks(),
            ReadGpus(),
            DateTimeOffset.UtcNow);
    }

    private static string GetServerVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SystemAboutService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0-dev";
    }

    private static string ReadCpuName()
    {
        var procInfo = ReadLines("/proc/cpuinfo");
        foreach (var key in new[] { "model name", "Model", "Hardware", "Processor" })
        {
            var line = procInfo.FirstOrDefault(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return string.IsNullOrWhiteSpace(identifier) ? Unknown : identifier.Trim();
    }

    private static double? ReadCpuSpeedMHz()
    {
        var cpuInfo = ReadLines("/proc/cpuinfo");
        foreach (var line in cpuInfo)
        {
            if (!line.StartsWith("cpu MHz:", StringComparison.OrdinalIgnoreCase))
                continue;
            var raw = line[(line.IndexOf(':') + 1)..].Trim();
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mhz)
                && mhz > 0)
                return Math.Round(mhz, 2);
        }

        var frequencyPath = "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_cur_freq";
        if (double.TryParse(ReadText(frequencyPath), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var khz)
            && khz > 0)
            return Math.Round(khz / 1000d, 2);

        return null;
    }

    private static MemoryAboutInfo ReadMemory()
    {
        var total = 0L;
        var available = 0L;
        foreach (var line in ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                total = ParseMemInfoBytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                available = ParseMemInfoBytes(line);
        }

        if (total > 0)
            return new MemoryAboutInfo(total, available, "操作系统");

        var runtimeAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new MemoryAboutInfo(0, runtimeAvailable > 0 ? runtimeAvailable : 0, ".NET 运行时");
    }

    private static long ParseMemInfoBytes(string line)
    {
        var value = line[(line.IndexOf(':') + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return long.TryParse(value, out var kib) ? kib * 1024L : 0;
    }

    private static IReadOnlyList<DiskAboutInfo> ReadDisks()
    {
        var disks = new List<DiskAboutInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0)
                    continue;
                disks.Add(new DiskAboutInfo(drive.Name, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
                // 某些挂载点在枚举期间可能被卸载，忽略该项即可。
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限的挂载点不影响其余磁盘信息。
            }
        }

        return disks;
    }

    private static IReadOnlyList<GpuAboutInfo> ReadGpus()
    {
        if (!Directory.Exists("/sys/class/drm"))
            return [];

        var gpus = new List<GpuAboutInfo>();
        foreach (var cardPath in Directory.GetDirectories("/sys/class/drm", "card*"))
        {
            var uevent = ReadLines(Path.Combine(cardPath, "device", "uevent"));
            var driver = ReadKey(uevent, "DRIVER");
            var pciId = ReadKey(uevent, "PCI_ID");
            var product = ReadText(Path.Combine(cardPath, "device", "product_name"));
            var name = string.IsNullOrWhiteSpace(product) ? driver : product;
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(pciId))
                continue;
            gpus.Add(new GpuAboutInfo(string.IsNullOrWhiteSpace(name) ? "图形设备" : name, driver, pciId));
        }

        return gpus.DistinctBy(static gpu => $"{gpu.Name}|{gpu.Driver}|{gpu.PciId}").ToList();
    }

    private static string? ReadKey(IReadOnlyList<string> lines, string key)
    {
        var line = lines.FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[(line.IndexOf('=') + 1)..].Trim();
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
