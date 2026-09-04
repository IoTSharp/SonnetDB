using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SonnetDB.Benchmarks.Benchmarks;

internal static class GraphEvidenceProcessLauncher
{
    internal const string Command = "--m40-evidence-process-launcher";
    private const int MaximumArgumentCount = 256;
    private const int MaximumHandshakePollCount = 100;
    private const int MaximumTargetTerminationPollCount = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TargetTerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputDrainCancellationTimeout = TimeSpan.FromSeconds(2);

    internal static ProcessStartInfo CreateStartInfo(
        ProcessStartInfo target,
        int parentProcessId,
        string parentIdentityToken,
        TimeSpan maximumLifetime,
        string handshakeToken,
        string completionPath,
        GraphEvidenceLauncherTestMode testMode = GraphEvidenceLauncherTestMode.None)
    {
        string assemblyPath = typeof(GraphEvidenceProcessLauncher).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            throw new FileNotFoundException("M40 evidence launcher assembly 不存在。", assemblyPath);

        string dotnetHost = ResolveTrustedDotNetHost();
        string workingDirectory = string.IsNullOrWhiteSpace(target.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(target.WorkingDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (target.StandardOutputEncoding is not null)
            startInfo.StandardOutputEncoding = target.StandardOutputEncoding;
        if (target.StandardErrorEncoding is not null)
            startInfo.StandardErrorEncoding = target.StandardErrorEncoding;

        RemoveRuntimeInjectionVariables(startInfo);

        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(Command);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--parent-identity-token");
        startInfo.ArgumentList.Add(parentIdentityToken);
        startInfo.ArgumentList.Add("--maximum-lifetime-ms");
        startInfo.ArgumentList.Add(
            checked((long)Math.Ceiling(Math.Min(maximumLifetime.TotalMilliseconds, int.MaxValue)))
                .ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--handshake-token");
        startInfo.ArgumentList.Add(handshakeToken);
        startInfo.ArgumentList.Add("--working-directory");
        startInfo.ArgumentList.Add(workingDirectory);
        startInfo.ArgumentList.Add("--completion-path");
        startInfo.ArgumentList.Add(completionPath);
        startInfo.ArgumentList.Add("--executable");
        startInfo.ArgumentList.Add(target.FileName);
        if (testMode != GraphEvidenceLauncherTestMode.None)
        {
            startInfo.ArgumentList.Add("--test-mode");
            startInfo.ArgumentList.Add(testMode switch
            {
                GraphEvidenceLauncherTestMode.ExitWithoutCompletion => "exit-without-completion",
                _ => throw new ArgumentOutOfRangeException(nameof(testMode)),
            });
        }
        startInfo.ArgumentList.Add("--");
        foreach (string argument in target.ArgumentList)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out LaunchOptions? options, out string error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            return 2;
        }
        LaunchOptions launchOptions = options!;

        var lifetime = Stopwatch.StartNew();
        Task<string?> handshake = Console.In.ReadLineAsync();
        bool handshakeReceived = false;
        for (int attempt = 0;
            attempt < MaximumHandshakePollCount && lifetime.Elapsed < HandshakeTimeout;
            attempt++)
        {
            if (!GraphEvidenceProcessIdentityToken.IsExpectedProcessAlive(
                    launchOptions.ParentProcessId,
                    launchOptions.ParentIdentityToken))
                return 124;
            if (handshake.Wait(PollInterval))
            {
                handshakeReceived = true;
                break;
            }
        }
        if (!handshakeReceived
            || !GraphEvidenceProcessIdentityToken.IsExpectedProcessAlive(
                launchOptions.ParentProcessId,
                launchOptions.ParentIdentityToken)
            || !GraphEvidenceProcessContainment.TryOpenLauncherTerminationLease(
                handshake.GetAwaiter().GetResult(),
                launchOptions.HandshakeToken,
                out GraphEvidenceProcessContainment? terminationLease))
        {
            return 124;
        }

        using (terminationLease)
        {
            using var watchdogCancellation = new CancellationTokenSource();
            Task watchdog = Task.Run(
                () => WatchParentAndLifetime(
                    launchOptions,
                    lifetime,
                    terminationLease,
                    watchdogCancellation.Token));
            try
            {
                IReadOnlyDictionary<string, string?> targetEnvironment =
                    GraphEvidenceProcessControl.ReadTargetEnvironment(
                        launchOptions.CompletionPath,
                        launchOptions.HandshakeToken);
                GraphEvidenceProcessControl.TryDeleteTargetEnvironment(launchOptions.CompletionPath);
                if (launchOptions.TestMode == GraphEvidenceLauncherTestMode.ExitWithoutCompletion)
                    return 125;

                return await RunTargetAsync(
                        launchOptions,
                        targetEnvironment,
                        lifetime,
                        terminationLease)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is Win32Exception
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException
                or AggregateException)
            {
                try
                {
                    await Console.Error.WriteLineAsync(
                            $"m40-process-launcher-failed pid={Environment.ProcessId} error={exception.Message}")
                        .ConfigureAwait(false);
                }
                catch (Exception diagnosticException) when (diagnosticException is IOException or ObjectDisposedException)
                {
                }
                TerminateOwnedTree(launchOptions, terminationLease, "launcher-failure");
                return 125;
            }
            finally
            {
                watchdogCancellation.Cancel();
                await watchdog.ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> RunTargetAsync(
        LaunchOptions launchOptions,
        IReadOnlyDictionary<string, string?> targetEnvironment,
        Stopwatch lifetime,
        GraphEvidenceProcessContainment? terminationLease)
    {
        using var target = new Process
        {
            StartInfo = CreateTargetStartInfo(launchOptions, targetEnvironment),
        };
        try
        {
            if (!target.Start())
                return 126;
        }
        catch (Exception exception) when (exception is Win32Exception
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(
                $"m40-process-target-start-failed parent_pid={Environment.ProcessId} error={exception.Message}")
                .ConfigureAwait(false);
            PublishCompletionOrTerminate(launchOptions, terminationLease, exitCode: 126, outputDrained: true);
            return 126;
        }

        DateTimeOffset targetStartedUtc = ReadStartTime(target);
        await Console.Error.WriteLineAsync(FormattableString.Invariant(
                $"m40-process-target-start pid={target.Id} parent_pid={Environment.ProcessId} started_utc={targetStartedUtc:O} command={launchOptions.Executable}"))
            .ConfigureAwait(false);

        Task stdoutDrain = target.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        Task stderrDrain = target.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        Task outputDrain = Task.WhenAll(stdoutDrain, stderrDrain);
        _ = outputDrain.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        bool exited = false;
        int maximumLifetimePollCount = checked(
            (int)Math.Ceiling(launchOptions.MaximumLifetime.TotalMilliseconds / PollInterval.TotalMilliseconds) + 1);
        for (int attempt = 0;
            attempt < maximumLifetimePollCount && lifetime.Elapsed < launchOptions.MaximumLifetime;
            attempt++)
        {
            if (!GraphEvidenceProcessIdentityToken.IsExpectedProcessAlive(
                    launchOptions.ParentProcessId,
                    launchOptions.ParentIdentityToken))
            {
                TerminateOwnedTree(launchOptions, terminationLease, "parent-lost");
                return 124;
            }
            if (target.WaitForExit(0))
            {
                exited = true;
                break;
            }
            if (GraphEvidenceProcessControl.TryReadTerminationRequest(
                    launchOptions.CompletionPath,
                    launchOptions.HandshakeToken))
            {
                if (target.WaitForExit(0))
                {
                    exited = true;
                    break;
                }

                bool targetTerminationRequested = false;
                try
                {
                    target.Kill(entireProcessTree: false);
                    targetTerminationRequested = true;
                }
                catch (InvalidOperationException) when (target.HasExited)
                {
                    exited = true;
                    break;
                }
                catch (Win32Exception) when (target.HasExited)
                {
                    exited = true;
                    break;
                }

                if (!targetTerminationRequested || !WaitForTargetExitAfterTermination(target))
                {
                    TerminateOwnedTree(launchOptions, terminationLease, "target-termination-unconfirmed");
                    return 125;
                }

                GraphEvidenceProcessControl.PublishTerminationAcknowledgement(
                    launchOptions.CompletionPath,
                    launchOptions.HandshakeToken);
                return 0;
            }
            TimeSpan remaining = launchOptions.MaximumLifetime - lifetime.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            int waitMilliseconds = Math.Max(
                1,
                (int)Math.Ceiling(Math.Min(remaining.TotalMilliseconds, PollInterval.TotalMilliseconds)));
            if (target.WaitForExit(waitMilliseconds))
            {
                exited = true;
                break;
            }
            if ((attempt + 1) % 300 == 0)
            {
                await Console.Error.WriteLineAsync(FormattableString.Invariant(
                        $"m40-process-launcher-progress pid={Environment.ProcessId} target_pid={target.Id} polls={attempt + 1} elapsed_seconds={lifetime.Elapsed.TotalSeconds:F3}"))
                    .ConfigureAwait(false);
            }
        }

        if (!exited)
        {
            TerminateOwnedTree(launchOptions, terminationLease, "hard-lifetime");
            return 125;
        }

        int exitCode = target.ExitCode;
        LauncherDrainOutcome drainOutcome = WaitForOutputDrain(outputDrain, target);
        if (drainOutcome == LauncherDrainOutcome.TimedOut)
        {
            await Console.Error.WriteLineAsync(
                    $"m40-process-target-drain-timeout pid={target.Id} parent_pid={Environment.ProcessId}")
                .ConfigureAwait(false);
        }
        int completionExitCode = drainOutcome == LauncherDrainOutcome.Drained ? exitCode : 125;
        PublishCompletionOrTerminate(
            launchOptions,
            terminationLease,
            completionExitCode,
            outputDrained: drainOutcome == LauncherDrainOutcome.Drained);
        return completionExitCode;
    }

    private static void WatchParentAndLifetime(
        LaunchOptions launchOptions,
        Stopwatch lifetime,
        GraphEvidenceProcessContainment? terminationLease,
        CancellationToken cancellationToken)
    {
        int maximumPollCount = checked(
            (int)Math.Ceiling(launchOptions.MaximumLifetime.TotalMilliseconds / PollInterval.TotalMilliseconds) + 1);
        for (int attempt = 0;
            attempt < maximumPollCount
                && lifetime.Elapsed < launchOptions.MaximumLifetime
                && !cancellationToken.IsCancellationRequested;
            attempt++)
        {
            if (!GraphEvidenceProcessIdentityToken.IsExpectedProcessAlive(
                    launchOptions.ParentProcessId,
                    launchOptions.ParentIdentityToken))
            {
                TerminateOwnedTree(launchOptions, terminationLease, "parent-lost-watchdog");
                return;
            }

            TimeSpan remaining = launchOptions.MaximumLifetime - lifetime.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            TimeSpan delay = remaining < PollInterval ? remaining : PollInterval;
            if (cancellationToken.WaitHandle.WaitOne(delay))
                return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;
        TerminateOwnedTree(launchOptions, terminationLease, "hard-lifetime-watchdog");
    }

    private static void PublishCompletionOrTerminate(
        LaunchOptions launchOptions,
        GraphEvidenceProcessContainment? terminationLease,
        int exitCode,
        bool outputDrained)
    {
        try
        {
            GraphEvidenceProcessControl.PublishCompletion(
                launchOptions.CompletionPath,
                launchOptions.HandshakeToken,
                exitCode,
                outputDrained);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            try
            {
                Console.Error.WriteLine(
                    $"m40-process-completion-publish-failed pid={Environment.ProcessId} error={exception.Message}");
            }
            catch (Exception diagnosticException) when (diagnosticException is IOException or ObjectDisposedException)
            {
            }
            TerminateOwnedTree(launchOptions, terminationLease, "completion-publish-failed");
        }
    }

    private static void TerminateOwnedTree(
        LaunchOptions launchOptions,
        GraphEvidenceProcessContainment? terminationLease,
        string reason)
    {
        GraphEvidenceProcessControl.TryDeleteForAbandonedLauncher(launchOptions.CompletionPath);
        if (OperatingSystem.IsLinux())
        {
            GraphEvidenceProcessContainment.TerminateCurrentUnixProcessGroup();
            Environment.FailFast($"Unable to terminate the M40 evidence process group ({reason}).");
        }

        if (OperatingSystem.IsWindows() && terminationLease is not null)
        {
            try
            {
                _ = terminationLease.RequestTermination();
            }
            catch (Exception exception) when (exception is Win32Exception
                or InvalidOperationException
                or NotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException)
            {
                try
                {
                    Console.Error.WriteLine(
                        $"m40-process-launcher-job-termination-failed pid={Environment.ProcessId} error={exception.Message}");
                }
                catch (Exception diagnosticException) when (diagnosticException is IOException or ObjectDisposedException)
                {
                }
            }
            finally
            {
                Environment.FailFast($"Unable to terminate the M40 evidence Job ({reason}).");
            }
        }

        Environment.FailFast($"M40 evidence launcher lacks reliable termination ({reason}).");
    }

    private static ProcessStartInfo CreateTargetStartInfo(
        LaunchOptions options,
        IReadOnlyDictionary<string, string?> targetEnvironment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Executable,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment.Clear();
        foreach ((string key, string? value) in targetEnvironment)
            startInfo.Environment.Add(key, value);
        foreach (string argument in options.Arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static bool WaitForTargetExitAfterTermination(Process target)
    {
        var stopwatch = Stopwatch.StartNew();
        for (int attempt = 0;
            attempt < MaximumTargetTerminationPollCount
                && stopwatch.Elapsed < TargetTerminationTimeout;
            attempt++)
        {
            if (target.HasExited)
                return true;
            TimeSpan remaining = TargetTerminationTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            int waitMilliseconds = Math.Max(
                1,
                (int)Math.Ceiling(Math.Min(remaining.TotalMilliseconds, PollInterval.TotalMilliseconds)));
            if (target.WaitForExit(waitMilliseconds))
                return true;
        }
        return target.HasExited;
    }

    private static void RemoveRuntimeInjectionVariables(ProcessStartInfo startInfo)
    {
        string[] names = startInfo.Environment.Keys
            .Where(IsRuntimeInjectionVariable)
            .ToArray();
        foreach (string name in names)
            _ = startInfo.Environment.Remove(name);
    }

    private static string ResolveTrustedDotNetHost()
    {
        string? processPath = Environment.ProcessPath;
        if (IsDotNetHost(processPath))
            return Path.GetFullPath(processPath!);

        string runtimeDirectory = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory());
        var runtimeVersionDirectory = new DirectoryInfo(runtimeDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        DirectoryInfo? frameworkDirectory = runtimeVersionDirectory.Parent;
        DirectoryInfo? sharedDirectory = frameworkDirectory?.Parent;
        DirectoryInfo? dotNetRoot = sharedDirectory?.Parent;
        string hostName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? registeredHost = dotNetRoot is null
            ? null
            : Path.Combine(dotNetRoot.FullName, hostName);
        if (IsDotNetHost(registeredHost))
            return Path.GetFullPath(registeredHost!);

        throw new FileNotFoundException(
            "无法从当前受信 .NET runtime 定位 evidence launcher host。",
            registeredHost);
    }

    private static bool IsDotNetHost(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && string.Equals(
                Path.GetFileName(path),
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);

    private static bool IsRuntimeInjectionVariable(string name)
        => name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MONO_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "LD_PRELOAD", StringComparison.Ordinal)
            || string.Equals(name, "LD_AUDIT", StringComparison.Ordinal)
            || string.Equals(name, "LD_LIBRARY_PATH", StringComparison.Ordinal);

    private static LauncherDrainOutcome WaitForOutputDrain(Task outputDrain, Process target)
    {
        try
        {
            if (outputDrain.Wait(OutputDrainTimeout))
            {
                return outputDrain.Status == TaskStatus.RanToCompletion
                    ? LauncherDrainOutcome.Drained
                    : LauncherDrainOutcome.Faulted;
            }
        }
        catch (AggregateException)
        {
            return LauncherDrainOutcome.Faulted;
        }

        CloseStream(target.StandardOutput);
        CloseStream(target.StandardError);
        try
        {
            _ = outputDrain.Wait(OutputDrainCancellationTimeout);
        }
        catch (AggregateException)
        {
            // 关闭 pipe 后的 fault 仍表示 drain task 已停止。
        }
        return LauncherDrainOutcome.TimedOut;
    }

    private static void CloseStream(StreamReader stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // 两路 pipe 独立关闭，避免一路失败跳过另一路。
        }
    }

    private static bool TryParseArguments(
        string[] args,
        out LaunchOptions? options,
        out string error)
    {
        options = null;
        error = "M40 evidence launcher 参数无效。";
        if (args.Length < 16 || args.Length > MaximumArgumentCount + 18
            || !string.Equals(args[0], Command, StringComparison.Ordinal))
        {
            return false;
        }

        int separator = Array.IndexOf(args, "--");
        if (separator < 15 || separator >= args.Length)
            return false;
        string? parentPidText = ReadOption(args, separator, "--parent-pid");
        string? parentIdentityToken = ReadOption(args, separator, "--parent-identity-token");
        string? maximumLifetimeText = ReadOption(args, separator, "--maximum-lifetime-ms");
        string? handshakeToken = ReadOption(args, separator, "--handshake-token");
        string? workingDirectory = ReadOption(args, separator, "--working-directory");
        string? completionPath = ReadOption(args, separator, "--completion-path");
        string? executable = ReadOption(args, separator, "--executable");
        string? testModeText = ReadOption(args, separator, "--test-mode");
        GraphEvidenceLauncherTestMode testMode = testModeText switch
        {
            null => GraphEvidenceLauncherTestMode.None,
            "exit-without-completion" => GraphEvidenceLauncherTestMode.ExitWithoutCompletion,
            _ => (GraphEvidenceLauncherTestMode)byte.MaxValue,
        };
        if (!int.TryParse(parentPidText, NumberStyles.None, CultureInfo.InvariantCulture, out int parentProcessId)
            || parentProcessId <= 0
            || string.IsNullOrWhiteSpace(parentIdentityToken)
            || parentIdentityToken.Length > 128
            || !long.TryParse(maximumLifetimeText, NumberStyles.None, CultureInfo.InvariantCulture, out long maximumLifetimeMilliseconds)
            || maximumLifetimeMilliseconds <= 0
            || maximumLifetimeMilliseconds > int.MaxValue
            || string.IsNullOrWhiteSpace(handshakeToken)
            || string.IsNullOrWhiteSpace(workingDirectory)
            || string.IsNullOrWhiteSpace(completionPath)
            || string.IsNullOrWhiteSpace(executable)
            || !Enum.IsDefined(testMode)
            || separator != (testMode == GraphEvidenceLauncherTestMode.None ? 15 : 17))
        {
            return false;
        }

        try
        {
            string fullWorkingDirectory = Path.GetFullPath(workingDirectory);
            string fullCompletionPath = Path.GetFullPath(completionPath);
            if (!Directory.Exists(fullWorkingDirectory)
                || !GraphEvidenceProcessControl.IsValidCompletionPath(fullCompletionPath))
                return false;
            options = new LaunchOptions(
                parentProcessId,
                parentIdentityToken,
                TimeSpan.FromMilliseconds(maximumLifetimeMilliseconds),
                handshakeToken,
                fullWorkingDirectory,
                fullCompletionPath,
                executable,
                testMode,
                args[(separator + 1)..]);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ReadOption(string[] args, int endExclusive, string option)
    {
        string? value = null;
        for (int index = 1; index < endExclusive; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.Ordinal))
                continue;
            if (value is not null || index + 1 >= endExclusive)
                return null;
            value = args[++index];
        }
        return value;
    }

    private static DateTimeOffset ReadStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private sealed record LaunchOptions(
        int ParentProcessId,
        string ParentIdentityToken,
        TimeSpan MaximumLifetime,
        string HandshakeToken,
        string WorkingDirectory,
        string CompletionPath,
        string Executable,
        GraphEvidenceLauncherTestMode TestMode,
        string[] Arguments);

    private enum LauncherDrainOutcome : byte
    {
        Drained = 1,
        TimedOut = 2,
        Faulted = 3,
    }
}

internal enum GraphEvidenceLauncherTestMode : byte
{
    None = 0,
    ExitWithoutCompletion = 1,
}
