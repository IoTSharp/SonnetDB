using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>#367 evidence 子进程的 deadline、双流排空和整树回收测试。</summary>
public sealed class GraphEvidenceProcessRunnerTests : IDisposable
{
    private const string TestDirectoryPrefix = "sndb-m40-process-runner-test-";
    private const int MaximumExitPollCount = 50;
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConditionPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ExitPollTimeout = TimeSpan.FromSeconds(5);
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        TestDirectoryPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>创建独立 process probe 状态目录。</summary>
    public GraphEvidenceProcessRunnerTests()
    {
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>只清理当前测试创建且已验证命名的临时目录。</summary>
    public void Dispose()
    {
        string fullPath = Path.GetFullPath(_rootDirectory);
        if (!GraphEvidenceOwnedDirectoryCleanup.TryValidateRoot(
            fullPath,
            Path.GetTempPath(),
            TestDirectoryPrefix,
            out bool exists,
            out string validationFailureReason))
        {
            Console.Error.WriteLine(
                $"process-runner-test-temp-retained path={fullPath} reason={validationFailureReason}");
            return;
        }
        if (!exists)
            return;

        bool parentReclaimed = ReclaimRecordedProcess("parent");
        bool leafReclaimed = ReclaimRecordedProcess("leaf");
        if (!parentReclaimed || !leafReclaimed)
        {
            Console.Error.WriteLine($"process-runner-test-temp-retained path={fullPath}");
            return;
        }

        if (!GraphEvidenceOwnedDirectoryCleanup.TryDelete(
            fullPath,
            Path.GetTempPath(),
            TestDirectoryPrefix,
            out string failureReason))
        {
            Console.Error.WriteLine(
                $"process-runner-test-temp-retained path={fullPath} reason={failureReason}");
        }
    }

    /// <summary>验证调用前已取消时不会启动 probe 或产生子进程状态文件。</summary>
    [Fact]
    public void Run_WithPreCancelledToken_DoesNotStartProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = Assert.Throws<OperationCanceledException>(() => GraphEvidenceProcessRunner.Run(
            CreateProbeStartInfo(),
            ExecutionTimeout,
            captureOutput: true,
            cancellationToken: cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_rootDirectory, "parent.pid")));
        Assert.False(File.Exists(Path.Combine(_rootDirectory, "leaf.pid")));
        Assert.False(File.Exists(Path.Combine(_rootDirectory, "ready")));
    }

    /// <summary>验证缺少可信 target completion 时，结果不能完成或暴露 launcher 退出码。</summary>
    [Fact]
    public void Completed_RequiredTargetCompletionMissing_FailsClosed()
    {
        var result = new GraphEvidenceProcessResult(
            Started: true,
            TimedOut: false,
            Cancelled: false,
            ConditionSatisfied: false,
            TerminationRequested: false,
            CleanupConfirmed: true,
            OutputDrained: true,
            OutputDrainStopped: true,
            CompletionRequired: true,
            TargetCompletionObserved: false,
            RunnerTerminationConfirmed: false,
            TreeTrackingReliable: true,
            ContainmentKind: "test",
            AuthenticatedExitCode: 125,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            Identity: GraphEvidenceProcessIdentity.CreateNotStarted(CreateProbeStartInfo()),
            Failure: null);

        Assert.False(result.Completed);
        Assert.Equal(-1, result.ExitCode);

        GraphEvidenceProcessResult conditionWithoutTermination = result with
        {
            ConditionSatisfied = true,
            CompletionRequired = false,
        };
        Assert.False(conditionWithoutTermination.Completed);

        GraphEvidenceProcessResult conditionTermination = result with
        {
            ConditionSatisfied = true,
            TerminationRequested = true,
            CompletionRequired = false,
        };
        Assert.False(conditionTermination.Completed);

        GraphEvidenceProcessResult acknowledgedTermination = conditionTermination with
        {
            RunnerTerminationConfirmed = true,
        };
        Assert.True(acknowledgedTermination.Completed);
        Assert.Equal(-1, acknowledgedTermination.ExitCode);
    }

    /// <summary>验证 launcher 在无 completion 情况下提前退出时，普通 Run 不会采信 launcher 退出码。</summary>
    [Fact]
    public void Run_LauncherExitsWithoutCompletion_FailsClosedAndConfirmsCleanup()
    {
        ProcessStartInfo startInfo = CreateProbeStartInfo();
        var stopwatch = Stopwatch.StartNew();

        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
            startInfo,
            TimeSpan.FromSeconds(10),
            captureOutput: true,
            launcherTestMode: GraphEvidenceLauncherTestMode.ExitWithoutCompletion);

        Assert.True(result.Started, result.Diagnostic);
        Assert.False(result.TimedOut, result.Diagnostic);
        Assert.False(result.Cancelled, result.Diagnostic);
        Assert.False(result.Completed, result.Diagnostic);
        Assert.True(result.CleanupConfirmed, result.Diagnostic);
        Assert.True(result.OutputDrained, result.Diagnostic);
        Assert.True(result.OutputDrainStopped, result.Diagnostic);
        Assert.True(result.CompletionRequired, result.Diagnostic);
        Assert.False(result.TargetCompletionObserved, result.Diagnostic);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("authenticated target completion", result.Failure, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"elapsed={stopwatch.Elapsed}");
        Assert.False(File.Exists(Path.Combine(_rootDirectory, "parent.pid")));
        Assert.False(File.Exists(Path.Combine(_rootDirectory, "leaf.pid")));
    }

    /// <summary>验证 target 的 runtime 注入环境不会在 containment attach 前作用于受信 launcher。</summary>
    [Fact]
    public void Run_TargetStartupHookEnvironment_IsolatedFromTrustedLauncher()
    {
        ProcessStartInfo startInfo = CreateProbeStartInfo();
        startInfo.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(
            AppContext.BaseDirectory,
            "SonnetDB.Benchmarks.Tests.ProcessProbe.dll");
        startInfo.Environment["SONNETDB_M40_TEST_FAIL_LAUNCHER_STARTUP"] = "1";

        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
            startInfo,
            TimeSpan.FromSeconds(10),
            captureOutput: true);

        Assert.True(result.Started, result.Diagnostic);
        Assert.True(result.Completed, result.Diagnostic);
        Assert.True(result.TargetCompletionObserved, result.Diagnostic);
        Assert.True(result.CompletionRequired, result.Diagnostic);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Intentional evidence target startup-hook failure", result.StandardError);
        Assert.False(File.Exists(Path.Combine(_rootDirectory, "parent.pid")));
        Assert.False(File.Exists(Path.Combine(_rootDirectory, "leaf.pid")));
    }

    /// <summary>验证极短命令在握手完成后才启动，不因先于 containment attach 退出而偶发失败。</summary>
    [Fact]
    public void Run_FastCommandRepeatedly_CompletesWithReliableContainment()
    {
        const int maximumAttempts = 20;
        var stopwatch = Stopwatch.StartNew();
        int completed = 0;
        for (int attempt = 0;
            attempt < maximumAttempts && stopwatch.Elapsed < TimeSpan.FromSeconds(45);
            attempt++)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkingDirectory = _rootDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");

            GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
                startInfo,
                TimeSpan.FromSeconds(10),
                captureOutput: true);

            Assert.True(result.Completed, result.Diagnostic);
            Assert.True(result.TreeTrackingReliable, result.Diagnostic);
            Assert.True(result.CompletionRequired, result.Diagnostic);
            Assert.True(result.TargetCompletionObserved, result.Diagnostic);
            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
            completed++;
        }

        Assert.Equal(maximumAttempts, completed);
    }

    /// <summary>验证根进程先退出但后代持有 pipe 时，drain 失败不会被伪报为成功且整树仍被回收。</summary>
    [Fact]
    public void Run_RootExitsBeforeDetachedDescendant_ReportsDrainFailureAndReapsContainedTree()
    {
        var stopwatch = Stopwatch.StartNew();

        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
            CreateProbeStartInfo("detach"),
            ExecutionTimeout,
            captureOutput: true);

        ProcessIdentity parent = ReadIdentity("parent");
        ProcessIdentity leaf = ReadIdentity("leaf");
        Assert.True(result.Started, result.Diagnostic);
        Assert.False(result.Completed, result.Diagnostic);
        Assert.True(result.TreeTrackingReliable, result.Diagnostic);
        Assert.True(result.TerminationRequested, result.Diagnostic);
        Assert.True(result.CleanupConfirmed, result.Diagnostic);
        Assert.False(result.OutputDrained, result.Diagnostic);
        Assert.True(result.OutputDrainStopped, result.Diagnostic);
        Assert.Equal(125, result.ExitCode);
        Assert.Contains("target stdout/stderr drain failed", result.Failure, StringComparison.Ordinal);
        Assert.NotEqual(parent.ProcessId, result.Identity.ProcessId);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"elapsed={stopwatch.Elapsed}");
        Assert.True(WaitUntilExited(parent), $"parent PID {parent.ProcessId} 仍存活。");
        Assert.True(WaitUntilExited(leaf), $"leaf PID {leaf.ProcessId} 仍存活。");
    }

    /// <summary>验证父进程仍存活时，launcher hard lifetime 仍会主动终止完整 Job/PGID。</summary>
    [Fact]
    public async Task Launcher_HardLifetimeReached_ReapsContainedTreeWhileParentRemainsAlive()
    {
        const int maximumExitPollCount = 100;
        string handshakeToken = Guid.NewGuid().ToString("N");
        using GraphEvidenceProcessControl control = GraphEvidenceProcessControl.Create(handshakeToken);
        using Process parent = Process.GetCurrentProcess();
        ProcessStartInfo launcherStartInfo = GraphEvidenceProcessLauncher.CreateStartInfo(
            CreateProbeStartInfo(),
            parent.Id,
            GraphEvidenceProcessIdentityToken.Create(parent),
            TimeSpan.FromSeconds(3),
            handshakeToken,
            control.CompletionPath);
        ProcessStartInfo effectiveStartInfo = GraphEvidenceProcessContainment.PrepareStartInfo(
            launcherStartInfo,
            out bool expectsUnixProcessGroup);
        using var launcher = new Process { StartInfo = effectiveStartInfo };
        GraphEvidenceProcessContainment? containment = null;
        Task stdoutDrain = Task.CompletedTask;
        Task stderrDrain = Task.CompletedTask;
        bool cleanupConfirmed = false;
        try
        {
            Assert.True(launcher.Start());
            stdoutDrain = launcher.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            stderrDrain = launcher.StandardError.BaseStream.CopyToAsync(Stream.Null);
            containment = GraphEvidenceProcessContainment.Attach(launcher, expectsUnixProcessGroup);
            Assert.True(containment.TreeTrackingReliable);
            launcher.StandardInput.WriteLine(
                containment.CreateLauncherHandshake(launcher, handshakeToken));
            launcher.StandardInput.Flush();
            launcher.StandardInput.Dispose();

            Assert.True(WaitForIdentity("parent", TimeSpan.FromSeconds(2)));
            Assert.True(WaitForIdentity("leaf", TimeSpan.FromSeconds(2)));
            ProcessIdentity targetParent = ReadIdentity("parent");
            ProcessIdentity targetLeaf = ReadIdentity("leaf");
            var stopwatch = Stopwatch.StartNew();
            for (int attempt = 0;
                attempt < maximumExitPollCount && stopwatch.Elapsed < TimeSpan.FromSeconds(10);
                attempt++)
            {
                bool launcherExited = launcher.HasExited || launcher.WaitForExit(100);
                bool stateKnown = containment.TryHasActiveProcesses(out bool active);
                if (launcherExited && stateKnown && !active)
                {
                    cleanupConfirmed = true;
                    break;
                }
            }

            Assert.True(cleanupConfirmed, "launcher hard lifetime 后 containment 未确认清空。");
            Assert.True(WaitUntilExited(targetParent), $"parent PID {targetParent.ProcessId} 仍存活。");
            Assert.True(WaitUntilExited(targetLeaf), $"leaf PID {targetLeaf.ProcessId} 仍存活。");
        }
        finally
        {
            if (containment is not null && !cleanupConfirmed)
            {
                _ = containment.RequestTermination();
                var cleanup = Stopwatch.StartNew();
                for (int attempt = 0;
                    attempt < maximumExitPollCount && cleanup.Elapsed < TimeSpan.FromSeconds(10);
                    attempt++)
                {
                    bool launcherExited = launcher.HasExited || launcher.WaitForExit(100);
                    bool stateKnown = containment.TryHasActiveProcesses(out bool active);
                    if (launcherExited && stateKnown && !active)
                    {
                        cleanupConfirmed = true;
                        break;
                    }
                }
            }

            containment?.Dispose();
            try
            {
                await Task.WhenAll(stdoutDrain, stderrDrain)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is IOException
                or OperationCanceledException
                or TimeoutException)
            {
            }
        }
    }

    /// <summary>验证被观察父进程消失时，launcher 会在父侧 Job handle 仍打开期间主动回收完整 Job/PGID。</summary>
    [Fact]
    public async Task Launcher_ObservedParentExits_ReapsContainedTreeBeforeHardLifetime()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return;

        const int maximumExitPollCount = 50;
        TimeSpan parentDeathCleanupLimit = TimeSpan.FromSeconds(5);
        TimeSpan launcherHardLifetime = TimeSpan.FromSeconds(60);
        string watchedParentDirectory = Path.Combine(_rootDirectory, "watched-parent");
        Directory.CreateDirectory(watchedParentDirectory);
        ProcessStartInfo watchedParentStartInfo = CreateProbeStartInfo("leaf", watchedParentDirectory);
        watchedParentStartInfo.RedirectStandardOutput = false;
        watchedParentStartInfo.RedirectStandardError = false;
        using var watchedParent = new Process { StartInfo = watchedParentStartInfo };
        ProcessIdentity? watchedParentIdentity = null;
        GraphEvidenceProcessControl? control = null;
        GraphEvidenceProcessContainment? containment = null;
        Process? launcher = null;
        ProcessIdentity? launcherIdentity = null;
        ProcessIdentity? targetParentIdentity = null;
        ProcessIdentity? targetLeafIdentity = null;
        Task stdoutDrain = Task.CompletedTask;
        Task stderrDrain = Task.CompletedTask;
        bool launcherStarted = false;
        bool handshakeSent = false;
        bool cleanupConfirmed = false;

        try
        {
            Assert.True(watchedParent.Start());
            watchedParentIdentity = GetIdentity(watchedParent);
            WriteTestProcessStart("watched-parent", watchedParent, watchedParentStartInfo, watchedParentIdentity.Value);

            string handshakeToken = Guid.NewGuid().ToString("N");
            control = GraphEvidenceProcessControl.Create(handshakeToken);
            ProcessStartInfo launcherStartInfo = GraphEvidenceProcessLauncher.CreateStartInfo(
                CreateProbeStartInfo(),
                watchedParentIdentity.Value.ProcessId,
                watchedParentIdentity.Value.IdentityToken,
                launcherHardLifetime,
                handshakeToken,
                control.CompletionPath);
            ProcessStartInfo effectiveStartInfo = GraphEvidenceProcessContainment.PrepareStartInfo(
                launcherStartInfo,
                out bool expectsUnixProcessGroup);
            launcher = new Process { StartInfo = effectiveStartInfo };
            var launcherLifetime = Stopwatch.StartNew();
            launcherStarted = launcher.Start();
            Assert.True(launcherStarted);
            launcherIdentity = GetIdentity(launcher);
            WriteTestProcessStart("launcher", launcher, effectiveStartInfo, launcherIdentity.Value);
            stdoutDrain = launcher.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            stderrDrain = launcher.StandardError.BaseStream.CopyToAsync(Stream.Null);
            containment = GraphEvidenceProcessContainment.Attach(launcher, expectsUnixProcessGroup);
            Assert.True(containment.TreeTrackingReliable);
            Assert.Equal(
                OperatingSystem.IsWindows() ? "windows-job" : "linux-process-group",
                containment.Kind);
            launcher.StandardInput.WriteLine(
                containment.CreateLauncherHandshake(launcher, handshakeToken));
            launcher.StandardInput.Flush();
            launcher.StandardInput.Dispose();
            handshakeSent = true;

            Assert.True(WaitForIdentity("parent", TimeSpan.FromSeconds(5)));
            Assert.True(WaitForIdentity("leaf", TimeSpan.FromSeconds(5)));
            Assert.True(WaitForFile(Path.Combine(_rootDirectory, "ready"), TimeSpan.FromSeconds(5)));
            targetParentIdentity = ReadIdentity("parent");
            targetLeafIdentity = ReadIdentity("leaf");
            Assert.True(IsProcessAlive(launcherIdentity.Value));
            Assert.True(IsProcessAlive(targetParentIdentity.Value));
            Assert.True(IsProcessAlive(targetLeafIdentity.Value));
            Assert.True(IsProcessAlive(watchedParentIdentity.Value));
            TimeSpan hardLifetimeRemaining = launcherHardLifetime - launcherLifetime.Elapsed;
            Assert.True(
                hardLifetimeRemaining > parentDeathCleanupLimit + TimeSpan.FromSeconds(10),
                $"hard-lifetime remaining={hardLifetimeRemaining}");

            var parentDeath = Stopwatch.StartNew();
            Assert.True(RequestOwnedProcessTermination(watchedParent, watchedParentIdentity.Value));
            for (int attempt = 0;
                attempt < maximumExitPollCount && parentDeath.Elapsed < parentDeathCleanupLimit;
                attempt++)
            {
                bool watchedParentExited = !IsProcessAlive(watchedParentIdentity.Value);
                bool launcherExited = launcher.HasExited || launcher.WaitForExit(100);
                bool stateKnown = containment.TryHasActiveProcesses(out bool active);
                if (watchedParentExited && launcherExited && stateKnown && !active)
                {
                    cleanupConfirmed = true;
                    break;
                }
                if ((attempt + 1) % 25 == 0)
                    Console.Error.WriteLine($"parent-death-cleanup-progress polls={attempt + 1} elapsed={parentDeath.Elapsed}");
            }

            Assert.True(cleanupConfirmed, "被观察父进程退出后 containment 未在 5 秒内清空。");
            Assert.True(parentDeath.Elapsed < parentDeathCleanupLimit, $"elapsed={parentDeath.Elapsed}");
            Assert.True(WaitUntilExited(launcherIdentity.Value), $"launcher PID {launcherIdentity.Value.ProcessId} 仍存活。");
            Assert.True(WaitUntilExited(targetParentIdentity.Value), $"parent PID {targetParentIdentity.Value.ProcessId} 仍存活。");
            Assert.True(WaitUntilExited(targetLeafIdentity.Value), $"leaf PID {targetLeafIdentity.Value.ProcessId} 仍存活。");
            Assert.True(containment.TryHasActiveProcesses(out bool activeAfterCleanup));
            Assert.False(activeAfterCleanup);
        }
        finally
        {
            if (watchedParentIdentity is ProcessIdentity ownedParent)
                _ = RequestOwnedProcessTermination(watchedParent, ownedParent);
            if (targetParentIdentity is null && TryReadIdentity("parent", out ProcessIdentity recordedParent))
                targetParentIdentity = recordedParent;
            if (targetLeafIdentity is null && TryReadIdentity("leaf", out ProcessIdentity recordedLeaf))
                targetLeafIdentity = recordedLeaf;

            bool containmentEmpty = cleanupConfirmed;
            if (containment is not null && !containmentEmpty)
            {
                try
                {
                    _ = containment.RequestTermination();
                }
                catch (Exception exception) when (exception is Win32Exception
                    or InvalidOperationException
                    or NotSupportedException
                    or DllNotFoundException
                    or EntryPointNotFoundException)
                {
                    Console.Error.WriteLine($"parent-death-containment-termination-failed error={exception.Message}");
                }

                var cleanup = Stopwatch.StartNew();
                for (int attempt = 0;
                    attempt < 100 && cleanup.Elapsed < TimeSpan.FromSeconds(10);
                    attempt++)
                {
                    try
                    {
                        bool launcherExited = launcher is null || !launcherStarted
                            || launcher.HasExited || launcher.WaitForExit(100);
                        bool stateKnown = containment.TryHasActiveProcesses(out bool active);
                        if (launcherExited && stateKnown && !active)
                        {
                            containmentEmpty = true;
                            break;
                        }
                    }
                    catch (Exception exception) when (exception is Win32Exception
                        or InvalidOperationException
                        or NotSupportedException)
                    {
                        Console.Error.WriteLine($"parent-death-containment-query-failed error={exception.Message}");
                        break;
                    }
                    if ((attempt + 1) % 50 == 0)
                        Console.Error.WriteLine($"parent-death-fallback-progress polls={attempt + 1} elapsed={cleanup.Elapsed}");
                }
            }

            var recordedProcesses = new List<ProcessIdentity>(3);
            if (launcherIdentity is ProcessIdentity ownedLauncher)
                recordedProcesses.Add(ownedLauncher);
            if (targetParentIdentity is ProcessIdentity ownedTargetParent)
                recordedProcesses.Add(ownedTargetParent);
            if (targetLeafIdentity is ProcessIdentity ownedTargetLeaf)
                recordedProcesses.Add(ownedTargetLeaf);
            foreach (ProcessIdentity identity in recordedProcesses)
                _ = RequestOwnedProcessTermination(identity);
            bool recordedProcessesExited = WaitUntilAllExited(
                recordedProcesses,
                TimeSpan.FromSeconds(5));

            try
            {
                containment?.Dispose();
            }
            catch (Exception exception) when (exception is Win32Exception
                or InvalidOperationException
                or NotSupportedException)
            {
                Console.Error.WriteLine($"parent-death-containment-dispose-failed error={exception.Message}");
            }

            bool expectedIdentitiesKnown = launcherIdentity is not null
                && (!handshakeSent || (targetParentIdentity is not null && targetLeafIdentity is not null));
            bool cleanupSafe = containmentEmpty
                || (!launcherStarted)
                || (!handshakeSent && (launcherIdentity is null || !IsProcessAlive(launcherIdentity.Value)))
                || (expectedIdentitiesKnown && recordedProcessesExited);
            try
            {
                await Task.WhenAll(stdoutDrain, stderrDrain)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is IOException
                or OperationCanceledException
                or TimeoutException)
            {
                Console.Error.WriteLine($"parent-death-output-drain-failed error={exception.Message}");
            }

            if (control is not null)
            {
                if (cleanupSafe || !Directory.Exists(control.DirectoryPath))
                    control.Dispose();
                else
                    Console.Error.WriteLine($"m40-process-control-temp-retained path={control.DirectoryPath}");
            }
            launcher?.Dispose();
        }
    }

    /// <summary>验证生产 replay 使用的 Run + zero-capacity drain 在超时后仍回收完整子树。</summary>
    [Fact]
    public void Run_ZeroCapacityOutputTimesOut_ReapsTreeAndStopsDrainTasks()
    {
        var stopwatch = Stopwatch.StartNew();

        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
            CreateProbeStartInfo(),
            TimeSpan.FromSeconds(5),
            captureOutput: false);

        ProcessIdentity parent = ReadIdentity("parent");
        ProcessIdentity leaf = ReadIdentity("leaf");
        Assert.True(result.Started, result.Diagnostic);
        Assert.True(result.TimedOut, result.Diagnostic);
        Assert.True(result.TreeTrackingReliable, result.Diagnostic);
        Assert.True(result.TerminationRequested, result.Diagnostic);
        Assert.True(result.CleanupConfirmed, result.Diagnostic);
        Assert.True(result.OutputDrained, result.Diagnostic);
        Assert.True(result.OutputDrainStopped, result.Diagnostic);
        Assert.True(result.CompletionRequired, result.Diagnostic);
        Assert.False(result.TargetCompletionObserved, result.Diagnostic);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("authenticated target completion", result.Failure, StringComparison.Ordinal);
        Assert.Contains("captured=0", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("captured=0", result.StandardError, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"elapsed={stopwatch.Elapsed}");
        Assert.True(WaitUntilExited(parent), $"parent PID {parent.ProcessId} 仍存活。");
        Assert.True(WaitUntilExited(leaf), $"leaf PID {leaf.ProcessId} 仍存活。");
    }

    /// <summary>验证双流超过 pipe 容量后仍能抵达 marker，并确认父子进程均被回收。</summary>
    [Fact]
    public void RunUntilFileExists_LargeDualOutputThenReady_KillsEntireOwnedTree()
    {
        string readyPath = Path.Combine(_rootDirectory, "ready");
        var stopwatch = Stopwatch.StartNew();

        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.RunUntilFileExists(
            CreateProbeStartInfo(),
            readyPath,
            ExecutionTimeout,
            ConditionPollInterval,
            maximumPollCount: 300,
            captureOutput: true);

        ProcessIdentity parent = ReadIdentity("parent");
        ProcessIdentity leaf = ReadIdentity("leaf");
        Assert.True(result.Started, result.Diagnostic);
        Assert.True(result.ConditionSatisfied, result.Diagnostic);
        Assert.True(result.TerminationRequested, result.Diagnostic);
        Assert.True(result.CleanupConfirmed, result.Diagnostic);
        Assert.True(result.OutputDrained, result.Diagnostic);
        Assert.False(result.CompletionRequired, result.Diagnostic);
        Assert.False(result.TargetCompletionObserved, result.Diagnostic);
        Assert.True(result.RunnerTerminationConfirmed, result.Diagnostic);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut, result.Diagnostic);
        Assert.False(result.Cancelled, result.Diagnostic);
        Assert.NotEqual(parent.ProcessId, result.Identity.ProcessId);
        Assert.Contains("[stdout truncated", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[stderr truncated", result.StandardError, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"elapsed={stopwatch.Elapsed}");
        Assert.True(WaitUntilExited(parent), $"parent PID {parent.ProcessId} 仍存活。");
        Assert.True(WaitUntilExited(leaf), $"leaf PID {leaf.ProcessId} 仍存活。");
    }

    /// <summary>验证 marker 与自然退出均已可见时，必须使用可信 target completion。</summary>
    [Fact]
    public void RunUntilFileExists_MarkerThenNaturalExit_RequiresTargetCompletion()
    {
        string readyPath = Path.Combine(_rootDirectory, "ready");

        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.RunUntilFileExists(
            CreateProbeStartInfo("ready-exit"),
            readyPath,
            TimeSpan.FromSeconds(10),
            ConditionPollInterval,
            maximumPollCount: 200,
            captureOutput: true);

        ProcessIdentity parent = ReadIdentity("parent");
        Assert.True(result.Started, result.Diagnostic);
        Assert.True(result.ConditionSatisfied, result.Diagnostic);
        Assert.False(result.TimedOut, result.Diagnostic);
        Assert.False(result.Cancelled, result.Diagnostic);
        Assert.True(result.Completed, result.Diagnostic);
        Assert.True(result.CleanupConfirmed, result.Diagnostic);
        Assert.True(result.OutputDrained, result.Diagnostic);
        Assert.True(result.OutputDrainStopped, result.Diagnostic);
        Assert.True(result.CompletionRequired, result.Diagnostic);
        Assert.True(result.TargetCompletionObserved, result.Diagnostic);
        Assert.False(result.RunnerTerminationConfirmed, result.Diagnostic);
        Assert.Equal(23, result.ExitCode);
        Assert.True(WaitUntilExited(parent), $"parent PID {parent.ProcessId} 仍存活。");
    }

    /// <summary>验证外部取消只在完整回收已启动子树后返回。</summary>
    [Fact]
    public async Task RunUntilFileExists_AfterReadyCancellation_ReapsTreeBeforeReturning()
    {
        string readyPath = Path.Combine(_rootDirectory, "ready");
        using var cancellation = new CancellationTokenSource();
        Task<GraphEvidenceProcessResult> runner = Task.Run(() =>
            GraphEvidenceProcessRunner.RunUntilFileExists(
                CreateProbeStartInfo(),
                Path.Combine(_rootDirectory, "never-ready"),
                ExecutionTimeout,
                ConditionPollInterval,
                maximumPollCount: 300,
                captureOutput: true,
                cancellationToken: cancellation.Token));
        bool cancellationIssued = await CancelWhenFileExists(readyPath, cancellation);
        GraphEvidenceProcessResult result = await runner.WaitAsync(TimeSpan.FromSeconds(20));

        ProcessIdentity parent = ReadIdentity("parent");
        ProcessIdentity leaf = ReadIdentity("leaf");
        Assert.True(cancellationIssued);
        Assert.True(result.Cancelled, result.Diagnostic);
        Assert.False(result.TimedOut, result.Diagnostic);
        Assert.True(result.TerminationRequested, result.Diagnostic);
        Assert.True(result.CleanupConfirmed, result.Diagnostic);
        Assert.True(result.OutputDrained, result.Diagnostic);
        Assert.True(WaitUntilExited(parent), $"parent PID {parent.ProcessId} 仍存活。");
        Assert.True(WaitUntilExited(leaf), $"leaf PID {leaf.ProcessId} 仍存活。");
    }

    /// <summary>验证 marker 永不满足时，execution timeout 后仍会有界回收完整子树。</summary>
    [Fact]
    public void RunUntilFileExists_MissingMarker_TimesOutAndReapsTree()
    {
        var stopwatch = Stopwatch.StartNew();

        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.RunUntilFileExists(
            CreateProbeStartInfo(),
            Path.Combine(_rootDirectory, "never-ready"),
            TimeSpan.FromSeconds(5),
            ConditionPollInterval,
            maximumPollCount: 100,
            captureOutput: true);

        ProcessIdentity parent = ReadIdentity("parent");
        ProcessIdentity leaf = ReadIdentity("leaf");
        Assert.True(result.Started, result.Diagnostic);
        Assert.True(result.TimedOut, result.Diagnostic);
        Assert.False(result.Cancelled, result.Diagnostic);
        Assert.False(result.ConditionSatisfied, result.Diagnostic);
        Assert.True(result.TerminationRequested, result.Diagnostic);
        Assert.True(result.CleanupConfirmed, result.Diagnostic);
        Assert.True(result.OutputDrained, result.Diagnostic);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"elapsed={stopwatch.Elapsed}");
        Assert.True(WaitUntilExited(parent), $"parent PID {parent.ProcessId} 仍存活。");
        Assert.True(WaitUntilExited(leaf), $"leaf PID {leaf.ProcessId} 仍存活。");
    }

    private ProcessStartInfo CreateProbeStartInfo(
        string? mode = null,
        string? stateDirectory = null)
    {
        string probePath = Path.Combine(
            AppContext.BaseDirectory,
            "SonnetDB.Benchmarks.Tests.ProcessProbe.dll");
        if (!File.Exists(probePath))
            throw new FileNotFoundException("Process probe assembly 不存在。", probePath);
        string dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = _rootDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(probePath);
        startInfo.ArgumentList.Add(stateDirectory ?? _rootDirectory);
        if (mode is not null)
            startInfo.ArgumentList.Add(mode);
        return startInfo;
    }

    private static ProcessIdentity GetIdentity(Process process)
        => new(process.Id, GraphEvidenceProcessIdentityToken.Create(process));

    private static void WriteTestProcessStart(
        string role,
        Process process,
        ProcessStartInfo startInfo,
        ProcessIdentity identity)
    {
        using Process parent = Process.GetCurrentProcess();
        DateTimeOffset startedUtc = new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        DateTimeOffset parentStartedUtc = new(parent.StartTime.ToUniversalTime(), TimeSpan.Zero);
        GraphEvidenceProcessIdentity diagnosticIdentity = GraphEvidenceProcessIdentity.Create(
            process.Id,
            startedUtc,
            parent.Id,
            parentStartedUtc,
            startInfo);
        Console.Error.WriteLine(
            $"m40-process-test-start role={role} pid={identity.ProcessId} parent_pid={parent.Id} "
            + $"started_utc={startedUtc:O} identity_token={identity.IdentityToken} "
            + $"command={diagnosticIdentity.CommandDisplay}");
    }

    private static bool RequestOwnedProcessTermination(Process process, ProcessIdentity identity)
    {
        if (process.Id != identity.ProcessId || !IsProcessAlive(identity))
            return !IsProcessAlive(identity);

        try
        {
            process.Kill(entireProcessTree: false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return !IsProcessAlive(identity);
        }
    }

    private static bool RequestOwnedProcessTermination(ProcessIdentity identity)
    {
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            return RequestOwnedProcessTermination(process, identity);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return !IsProcessAlive(identity);
        }
    }

    private static bool WaitUntilAllExited(
        IReadOnlyList<ProcessIdentity> identities,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        int maximumPollCount = checked((int)Math.Ceiling(timeout.TotalMilliseconds / 100) + 1);
        for (int attempt = 0;
            attempt < maximumPollCount && stopwatch.Elapsed < timeout;
            attempt++)
        {
            if (identities.All(static identity => !IsProcessAlive(identity)))
                return true;
            if ((attempt + 1) % 25 == 0)
                Console.Error.WriteLine($"owned-process-cleanup-progress polls={attempt + 1} elapsed={stopwatch.Elapsed}");
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }
        return identities.All(static identity => !IsProcessAlive(identity));
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        int maximumPollCount = checked((int)Math.Ceiling(timeout.TotalMilliseconds / 50) + 1);
        for (int attempt = 0;
            attempt < maximumPollCount && stopwatch.Elapsed < timeout;
            attempt++)
        {
            if (File.Exists(path))
                return true;
            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }
        return File.Exists(path);
    }

    private bool ReclaimRecordedProcess(string prefix)
    {
        string pidPath = Path.Combine(_rootDirectory, prefix + ".pid");
        string identityPath = Path.Combine(_rootDirectory, prefix + ".identity-token");
        if (!File.Exists(pidPath) && !File.Exists(identityPath))
            return true;
        if (!TryReadIdentity(prefix, out ProcessIdentity identity))
            return false;
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited
                || !string.Equals(
                    GraphEvidenceProcessIdentityToken.Create(process),
                    identity.IdentityToken,
                    StringComparison.Ordinal))
                return true;
            process.Kill(entireProcessTree: false);
            return WaitUntilExited(identity);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return !IsProcessAlive(identity);
        }
    }

    private bool WaitForIdentity(string prefix, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        int maximumPollCount = checked((int)Math.Ceiling(timeout.TotalMilliseconds / 50) + 1);
        for (int attempt = 0;
            attempt < maximumPollCount && stopwatch.Elapsed < timeout;
            attempt++)
        {
            if (TryReadIdentity(prefix, out _))
                return true;
            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }
        return TryReadIdentity(prefix, out _);
    }

    private static async Task<bool> CancelWhenFileExists(
        string path,
        CancellationTokenSource cancellation)
    {
        const int maximumPollCount = 200;
        var stopwatch = Stopwatch.StartNew();
        for (int attempt = 0;
            attempt < maximumPollCount && stopwatch.Elapsed < TimeSpan.FromSeconds(10);
            attempt++)
        {
            if (File.Exists(path))
            {
                cancellation.Cancel();
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        return false;
    }

    private bool TryReadIdentity(string prefix, out ProcessIdentity identity)
    {
        try
        {
            identity = ReadIdentity(prefix);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or FormatException
            or OverflowException)
        {
            identity = default;
            return false;
        }
    }

    private ProcessIdentity ReadIdentity(string prefix)
    {
        int processId = int.Parse(
            File.ReadAllText(Path.Combine(_rootDirectory, prefix + ".pid")),
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        string identityToken = File.ReadAllText(Path.Combine(_rootDirectory, prefix + ".identity-token"));
        if (string.IsNullOrWhiteSpace(identityToken))
            throw new FormatException("Process identity token 不能为空。");
        return new ProcessIdentity(processId, identityToken);
    }

    private static bool WaitUntilExited(ProcessIdentity identity)
    {
        var stopwatch = Stopwatch.StartNew();
        for (int attempt = 0;
            attempt < MaximumExitPollCount && stopwatch.Elapsed < ExitPollTimeout;
            attempt++)
        {
            if (!IsProcessAlive(identity))
                return true;
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }
        return !IsProcessAlive(identity);
    }

    private static bool IsProcessAlive(ProcessIdentity identity)
    {
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited
                && string.Equals(
                    GraphEvidenceProcessIdentityToken.Create(process),
                    identity.IdentityToken,
                    StringComparison.Ordinal);
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

    private readonly record struct ProcessIdentity(int ProcessId, string IdentityToken);
}
