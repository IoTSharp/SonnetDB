using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SonnetDB.Auth;

internal static class HostServerIdGenerator
{
    private const int HashByteLength = 6;
    private const int MaximumServerIdLength = 64;
    private const string ServerIdPrefix = "sndb-";
    private static readonly Lazy<string> CurrentHostServerId = new(
        CreateForCurrentHost,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string GetSuggestedServerId()
    {
        return CurrentHostServerId.Value;
    }

    internal static string CreateSuggestedServerId(
        string machineName,
        IEnumerable<KeyValuePair<string, string?>> fingerprintParts)
    {
        ArgumentNullException.ThrowIfNull(machineName);
        ArgumentNullException.ThrowIfNull(fingerprintParts);

        var normalizedMachineName = NormalizeMachineName(machineName);
        var normalizedFingerprint = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host.name"] = machineName.Trim(),
        };

        foreach (var part in fingerprintParts)
        {
            if (!string.IsNullOrWhiteSpace(part.Key) && !string.IsNullOrWhiteSpace(part.Value))
            {
                normalizedFingerprint[part.Key.Trim()] = part.Value.Trim();
            }
        }

        var canonicalFingerprint = new StringBuilder("sonnetdb-server-id-v1\n");
        foreach (var part in normalizedFingerprint)
        {
            canonicalFingerprint.Append(part.Key)
                .Append('=')
                .Append(part.Value)
                .Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalFingerprint.ToString()));
        var suffix = Convert.ToHexStringLower(hash.AsSpan(0, HashByteLength));
        var maximumMachineNameLength = MaximumServerIdLength - ServerIdPrefix.Length - 1 - suffix.Length;
        if (normalizedMachineName.Length > maximumMachineNameLength)
        {
            normalizedMachineName = normalizedMachineName[..maximumMachineNameLength].TrimEnd('-');
        }

        return $"{ServerIdPrefix}{normalizedMachineName}-{suffix}";
    }

    private static string CreateForCurrentHost()
    {
        return CreateSuggestedServerId(Environment.MachineName, CollectFingerprintParts());
    }

    private static IReadOnlyDictionary<string, string?> CollectFingerprintParts()
    {
        var parts = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["cpu.architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["cpu.logicalCount"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["os.platform"] = GetOperatingSystemName(),
        };

        var hasStableMachineIdentifier = false;
        if (OperatingSystem.IsWindows())
        {
            hasStableMachineIdentifier = AddWindowsFingerprintParts(parts);
        }
        else if (OperatingSystem.IsLinux())
        {
            hasStableMachineIdentifier = AddLinuxFingerprintParts(parts);
        }

        if (!hasStableMachineIdentifier)
        {
            AddNetworkFingerprintParts(parts);
        }

        return parts;
    }

    [SupportedOSPlatform("windows")]
    private static bool AddWindowsFingerprintParts(IDictionary<string, string?> parts)
    {
        var machineGuid = ReadRegistryValue(
            Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Cryptography",
            "MachineGuid");
        parts["machine.id"] = machineGuid;

        const string biosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
        parts["board.manufacturer"] = ReadRegistryValue(Registry.LocalMachine, biosKey, "BaseBoardManufacturer");
        parts["board.product"] = ReadRegistryValue(Registry.LocalMachine, biosKey, "BaseBoardProduct");
        parts["board.version"] = ReadRegistryValue(Registry.LocalMachine, biosKey, "BaseBoardVersion");
        parts["bios.vendor"] = ReadRegistryValue(Registry.LocalMachine, biosKey, "BIOSVendor");
        parts["bios.version"] = ReadRegistryValue(Registry.LocalMachine, biosKey, "BIOSVersion");
        parts["system.manufacturer"] = ReadRegistryValue(Registry.LocalMachine, biosKey, "SystemManufacturer");
        parts["system.product"] = ReadRegistryValue(Registry.LocalMachine, biosKey, "SystemProductName");

        const string cpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        parts["cpu.identifier"] = ReadRegistryValue(Registry.LocalMachine, cpuKey, "Identifier");
        parts["cpu.name"] = ReadRegistryValue(Registry.LocalMachine, cpuKey, "ProcessorNameString");
        parts["cpu.vendor"] = ReadRegistryValue(Registry.LocalMachine, cpuKey, "VendorIdentifier");
        return !string.IsNullOrWhiteSpace(machineGuid);
    }

    private static bool AddLinuxFingerprintParts(IDictionary<string, string?> parts)
    {
        var machineId = ReadFirstFileValue("/etc/machine-id", "/var/lib/dbus/machine-id");
        var productUuid = ReadFirstFileValue("/sys/class/dmi/id/product_uuid");
        var boardSerial = ReadFirstFileValue("/sys/class/dmi/id/board_serial");

        parts["machine.id"] = machineId;
        parts["system.uuid"] = productUuid;
        parts["board.serial"] = boardSerial;
        parts["board.manufacturer"] = ReadFirstFileValue("/sys/class/dmi/id/board_vendor");
        parts["board.product"] = ReadFirstFileValue("/sys/class/dmi/id/board_name");
        parts["board.version"] = ReadFirstFileValue("/sys/class/dmi/id/board_version");
        parts["bios.vendor"] = ReadFirstFileValue("/sys/class/dmi/id/bios_vendor");
        parts["bios.version"] = ReadFirstFileValue("/sys/class/dmi/id/bios_version");
        parts["system.manufacturer"] = ReadFirstFileValue("/sys/class/dmi/id/sys_vendor");
        parts["system.product"] = ReadFirstFileValue("/sys/class/dmi/id/product_name");
        parts["cpu.vendor"] = ReadCpuInfoValue("vendor_id", "CPU implementer");
        parts["cpu.name"] = ReadCpuInfoValue("model name", "Hardware", "Processor");

        return !string.IsNullOrWhiteSpace(machineId)
            || !string.IsNullOrWhiteSpace(productUuid)
            || !string.IsNullOrWhiteSpace(boardSerial);
    }

    private static void AddNetworkFingerprintParts(IDictionary<string, string?> parts)
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(networkInterface => networkInterface.GetPhysicalAddress().ToString())
                .Where(address => address.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            for (var index = 0; index < addresses.Length; index++)
            {
                parts[$"network.address.{index}"] = addresses[index];
            }
        }
        catch (NetworkInformationException)
        {
            // 网络接口仅用于缺少机器标识时的尽力回退。
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryValue(RegistryKey root, string keyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) switch
            {
                string value => value.Trim(),
                string[] values => string.Join('|', values.Select(value => value.Trim())),
                object value => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(),
                _ => null,
            };
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ReadFirstFileValue(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var value = File.ReadAllText(path).Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 继续尝试下一项机器信息来源。
            }
            catch (IOException)
            {
                // 继续尝试下一项机器信息来源。
            }
        }

        return null;
    }

    private static string? ReadCpuInfoValue(params string[] keys)
    {
        const string cpuInfoPath = "/proc/cpuinfo";
        try
        {
            if (!File.Exists(cpuInfoPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(cpuInfoPath))
            {
                var separatorIndex = line.IndexOf(':');
                if (separatorIndex < 0)
                {
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                if (!keys.Any(candidate => key.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var value = line[(separatorIndex + 1)..].Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    private static string NormalizeMachineName(string machineName)
    {
        var normalized = new StringBuilder(machineName.Length);
        foreach (var ch in machineName.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                normalized.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (normalized.Length > 0 && normalized[^1] != '-')
            {
                normalized.Append('-');
            }
        }

        var value = normalized.ToString().Trim('-');
        return value.Length == 0 ? "server" : value;
    }

    private static string GetOperatingSystemName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        if (OperatingSystem.IsFreeBSD())
        {
            return "freebsd";
        }

        return "unknown";
    }
}
