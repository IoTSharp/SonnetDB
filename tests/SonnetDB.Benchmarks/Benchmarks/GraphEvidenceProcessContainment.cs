using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.Benchmarks.Benchmarks;

internal sealed class GraphEvidenceProcessContainment : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint DuplicateSameAccess = 0x00000002;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int UnixNoSuchProcess = 3;
    private const int UnixOperationNotPermitted = 1;
    private const int UnixSignalKill = 9;
    private const int MaximumEnvironmentVariableCount = 4_096;
    private const int UnixGroupProbeCount = 50;
    private static readonly TimeSpan UnixGroupProbeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan UnixGroupProbeInterval = TimeSpan.FromMilliseconds(10);

    private readonly SafeJobHandle? _jobHandle;
    private readonly int _unixProcessGroupId;
    private bool _disposed;

    private GraphEvidenceProcessContainment(
        string kind,
        bool treeTrackingReliable,
        SafeJobHandle? jobHandle = null,
        int unixProcessGroupId = 0)
    {
        Kind = kind;
        TreeTrackingReliable = treeTrackingReliable;
        _jobHandle = jobHandle;
        _unixProcessGroupId = unixProcessGroupId;
    }

    internal string Kind { get; }

    internal bool TreeTrackingReliable { get; }

    internal static ProcessStartInfo PrepareStartInfo(
        ProcessStartInfo startInfo,
        out bool expectsUnixProcessGroup)
    {
        expectsUnixProcessGroup = false;
        if (!OperatingSystem.IsLinux())
            return startInfo;

        string? setSidPath = File.Exists("/usr/bin/setsid")
            ? "/usr/bin/setsid"
            : File.Exists("/bin/setsid")
                ? "/bin/setsid"
                : null;
        if (setSidPath is null)
            return startInfo;

        if (startInfo.Environment.Count > MaximumEnvironmentVariableCount)
        {
            throw new InvalidOperationException(
                $"Evidence process 环境变量超过 {MaximumEnvironmentVariableCount} 项上限。");
        }

        var wrapped = new ProcessStartInfo
        {
            FileName = setSidPath,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = startInfo.RedirectStandardInput,
            RedirectStandardOutput = startInfo.RedirectStandardOutput,
            RedirectStandardError = startInfo.RedirectStandardError,
            CreateNoWindow = startInfo.CreateNoWindow,
        };
        if (startInfo.StandardInputEncoding is not null)
            wrapped.StandardInputEncoding = startInfo.StandardInputEncoding;
        if (startInfo.StandardOutputEncoding is not null)
            wrapped.StandardOutputEncoding = startInfo.StandardOutputEncoding;
        if (startInfo.StandardErrorEncoding is not null)
            wrapped.StandardErrorEncoding = startInfo.StandardErrorEncoding;

        wrapped.Environment.Clear();
        foreach ((string key, string? value) in startInfo.Environment)
            wrapped.Environment.Add(key, value);
        wrapped.ArgumentList.Add("--");
        wrapped.ArgumentList.Add(startInfo.FileName);
        foreach (string argument in startInfo.ArgumentList)
            wrapped.ArgumentList.Add(argument);
        expectsUnixProcessGroup = true;
        return wrapped;
    }

    internal static GraphEvidenceProcessContainment Attach(
        Process process,
        bool expectsUnixProcessGroup)
    {
        if (OperatingSystem.IsWindows())
            return TryAttachWindowsJob(process);
        if (expectsUnixProcessGroup)
            return TryAttachUnixProcessGroup(process);
        return RootOnly("root-only-unsupported-platform");
    }

    internal bool TryHasActiveProcesses(out bool hasActiveProcesses)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_jobHandle is not null)
        {
            var accounting = new JobObjectBasicAccountingInformationNative();
            int succeeded = QueryInformationJobObject(
                _jobHandle,
                JobObjectBasicAccountingInformation,
                out accounting,
                checked((uint)Marshal.SizeOf<JobObjectBasicAccountingInformationNative>()),
                IntPtr.Zero);
            hasActiveProcesses = succeeded != 0 && accounting.ActiveProcesses != 0;
            return succeeded != 0;
        }

        if (_unixProcessGroupId > 0)
        {
            int result = KillUnixProcess(-_unixProcessGroupId, 0);
            if (result == 0)
            {
                hasActiveProcesses = true;
                return true;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == UnixNoSuchProcess)
            {
                hasActiveProcesses = false;
                return true;
            }
            if (error == UnixOperationNotPermitted)
            {
                hasActiveProcesses = true;
                return true;
            }

            hasActiveProcesses = true;
            return false;
        }

        hasActiveProcesses = true;
        return false;
    }

    internal bool RequestTermination()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_jobHandle is not null)
        {
            if (TryHasActiveProcesses(out bool active) && !active)
                return true;
            return TerminateJobObject(_jobHandle, unchecked((uint)-1)) != 0
                || (TryHasActiveProcesses(out active) && !active);
        }

        if (_unixProcessGroupId > 0)
        {
            int result = KillUnixProcess(-_unixProcessGroupId, UnixSignalKill);
            if (result == 0)
                return true;
            int error = Marshal.GetLastPInvokeError();
            return error == UnixNoSuchProcess;
        }

        return false;
    }

    internal string CreateLauncherHandshake(Process launcher, string handshakeToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeToken);
        if (!TreeTrackingReliable)
            throw new InvalidOperationException("无法为不可靠 containment 放行 evidence launcher。");

        if (_jobHandle is not null)
        {
            using Process current = Process.GetCurrentProcess();
            int duplicated = DuplicateHandle(
                current.Handle,
                _jobHandle,
                launcher.Handle,
                out IntPtr launcherJobHandle,
                0,
                inheritHandle: 0,
                DuplicateSameAccess);
            if (duplicated == 0 || launcherJobHandle == IntPtr.Zero || launcherJobHandle == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法向 evidence launcher 复制 Job handle。");

            return handshakeToken + "|" + unchecked((ulong)launcherJobHandle.ToInt64()).ToString("X", CultureInfo.InvariantCulture);
        }

        if (_unixProcessGroupId > 0)
            return handshakeToken + "|-";

        throw new InvalidOperationException("Evidence launcher containment 缺少可用的终止凭据。");
    }

    internal static bool TryOpenLauncherTerminationLease(
        string? handshake,
        string expectedToken,
        out GraphEvidenceProcessContainment? containment)
    {
        containment = null;
        if (string.IsNullOrWhiteSpace(handshake) || string.IsNullOrWhiteSpace(expectedToken))
            return false;

        int separator = handshake.IndexOf('|');
        if (separator <= 0
            || separator != handshake.LastIndexOf('|')
            || !string.Equals(handshake[..separator], expectedToken, StringComparison.Ordinal))
        {
            return false;
        }

        string payload = handshake[(separator + 1)..];
        if (OperatingSystem.IsLinux())
            return string.Equals(payload, "-", StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()
            || !ulong.TryParse(payload, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong rawHandle)
            || rawHandle == 0
            || rawHandle == ulong.MaxValue)
        {
            return false;
        }

        var jobHandle = new SafeJobHandle(new IntPtr(unchecked((long)rawHandle)));
        if (jobHandle.IsInvalid)
        {
            jobHandle.Dispose();
            return false;
        }

        containment = new GraphEvidenceProcessContainment(
            "windows-launcher-job-lease",
            treeTrackingReliable: true,
            jobHandle: jobHandle);
        return true;
    }

    internal static void TerminateCurrentUnixProcessGroup()
    {
        if (!OperatingSystem.IsLinux())
            return;
        _ = KillUnixProcess(0, UnixSignalKill);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _jobHandle?.Dispose();
    }

    private static GraphEvidenceProcessContainment TryAttachWindowsJob(Process process)
    {
        SafeJobHandle jobHandle = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (jobHandle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            jobHandle.Dispose();
            WriteContainmentFallback(process.Id, "windows-job-create", error);
            return RootOnly("root-only-windows-job-create-failed");
        }

        var limits = new JobObjectExtendedLimitInformationNative
        {
            BasicLimitInformation = new JobObjectBasicLimitInformationNative
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        int configured = SetInformationJobObject(
            jobHandle,
            JobObjectExtendedLimitInformation,
            in limits,
            checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>()));
        if (configured == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            jobHandle.Dispose();
            WriteContainmentFallback(process.Id, "windows-job-configure", error);
            return RootOnly("root-only-windows-job-configure-failed");
        }

        int assigned;
        try
        {
            assigned = AssignProcessToJobObject(jobHandle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            assigned = 0;
        }
        if (assigned == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            jobHandle.Dispose();
            WriteContainmentFallback(process.Id, "windows-job-assign", error);
            return RootOnly("root-only-windows-job-assign-failed");
        }

        return new GraphEvidenceProcessContainment(
            "windows-job",
            treeTrackingReliable: true,
            jobHandle: jobHandle);
    }

    private static GraphEvidenceProcessContainment TryAttachUnixProcessGroup(Process process)
    {
        var stopwatch = Stopwatch.StartNew();
        for (int attempt = 0;
            attempt < UnixGroupProbeCount && stopwatch.Elapsed < UnixGroupProbeTimeout;
            attempt++)
        {
            int processGroupId = GetUnixProcessGroupId(process.Id);
            if (processGroupId == process.Id)
            {
                return new GraphEvidenceProcessContainment(
                    "linux-process-group",
                    treeTrackingReliable: true,
                    unixProcessGroupId: processGroupId);
            }
            if (HasExitedSafely(process))
                break;
            Thread.Sleep(UnixGroupProbeInterval);
        }

        WriteContainmentFallback(process.Id, "linux-process-group", Marshal.GetLastPInvokeError());
        return RootOnly("root-only-linux-group-unconfirmed");
    }

    private static GraphEvidenceProcessContainment RootOnly(string kind)
        => new(kind, treeTrackingReliable: false);

    private static bool HasExitedSafely(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void WriteContainmentFallback(int processId, string phase, int nativeError)
    {
        try
        {
            Console.Error.WriteLine(
                $"m40-process-containment-fallback pid={processId} phase={phase} native_error={nativeError}");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // 诊断输出不可反向破坏已启动进程的监督与回收。
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformationNative
    {
        internal long TotalUserTime;
        internal long TotalKernelTime;
        internal long ThisPeriodTotalUserTime;
        internal long ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount;
        internal uint TotalProcesses;
        internal uint ActiveProcesses;
        internal uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationNative
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersNative
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        internal JobObjectBasicLimitInformationNative BasicLimitInformation;
        internal IoCountersNative IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        internal SafeJobHandle(IntPtr existingHandle)
            : base(ownsHandle: true)
        {
            SetHandle(existingHandle);
        }

        protected override bool ReleaseHandle()
            => CloseHandle(handle) != 0;
    }

    // LibraryImport 生成代码要求启用 unsafe；benchmark 边界沿用仓库的安全 DllImport 模式。
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", ExactSpelling = true, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, IntPtr name);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", ExactSpelling = true, SetLastError = true)]
    private static extern int SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        in JobObjectExtendedLimitInformationNative information,
        uint informationLength);

    [DllImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", ExactSpelling = true, SetLastError = true)]
    private static extern int AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", ExactSpelling = true, SetLastError = true)]
    private static extern int DuplicateHandle(
        IntPtr sourceProcess,
        SafeJobHandle sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint desiredAccess,
        int inheritHandle,
        uint options);

    [DllImport("kernel32.dll", EntryPoint = "TerminateJobObject", ExactSpelling = true, SetLastError = true)]
    private static extern int TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", ExactSpelling = true, SetLastError = true)]
    private static extern int QueryInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        out JobObjectBasicAccountingInformationNative information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", ExactSpelling = true, SetLastError = true)]
    private static extern int CloseHandle(IntPtr handle);

    [DllImport("libc", EntryPoint = "getpgid", ExactSpelling = true, SetLastError = true)]
    private static extern int GetUnixProcessGroupId(int processId);

    [DllImport("libc", EntryPoint = "kill", ExactSpelling = true, SetLastError = true)]
    private static extern int KillUnixProcess(int processId, int signal);
}
