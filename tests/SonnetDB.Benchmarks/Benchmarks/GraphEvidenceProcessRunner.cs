using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SonnetDB.Benchmarks.Benchmarks;

internal static class GraphEvidenceProcessRunner
{
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan OutputDrainCancellationTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan TerminationDecisionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ExitProgressInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupProgressInterval = TimeSpan.FromSeconds(5);
    private const int MaximumCapturedOutputBytes = 64 * 1024;
    private const int MaximumArgumentCount = 256;
    private const int MaximumArgumentLength = 32 * 1024;
    private const int MaximumEnvironmentVariableCount = 4_096;

    internal static GraphEvidenceProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan executionTimeout,
        bool captureOutput,
        CancellationToken cancellationToken = default,
        GraphEvidenceLauncherTestMode launcherTestMode = GraphEvidenceLauncherTestMode.None)
    {
        ValidateStartInfo(startInfo);
        ValidateTimeout(executionTimeout, nameof(executionTimeout));
        cancellationToken.ThrowIfCancellationRequested();

        GraphEvidenceOwnedProcess? process = null;
        try
        {
            process = GraphEvidenceOwnedProcess.Start(
                startInfo,
                captureOutput,
                executionTimeout,
                launcherTestMode);
            GraphEvidenceWaitOutcome outcome = process.WaitForExit(
                executionTimeout,
                cancellationToken,
                ExitPollInterval,
                ExitProgressInterval,
                "exit");
            return Complete(
                process,
                timedOut: outcome == GraphEvidenceWaitOutcome.TimedOut,
                cancelled: outcome == GraphEvidenceWaitOutcome.Cancelled,
                conditionSatisfied: false,
                terminate: outcome != GraphEvidenceWaitOutcome.Exited,
                completionRequired: true,
                runnerTerminationConfirmed: false,
                failure: null);
        }
        catch (GraphEvidenceProcessStartException exception)
        {
            return StartedButInitializationFailed(exception, cancellationToken.IsCancellationRequested);
        }
        catch (Exception exception) when (exception is Win32Exception
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException)
        {
            return process is null
                ? NotStarted(startInfo, exception)
                : Complete(
                    process,
                    timedOut: false,
                    cancelled: cancellationToken.IsCancellationRequested,
                    conditionSatisfied: false,
                    terminate: true,
                    completionRequired: true,
                    runnerTerminationConfirmed: false,
                    failure: exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static GraphEvidenceProcessResult RunUntilFileExists(
        ProcessStartInfo startInfo,
        string markerPath,
        TimeSpan executionTimeout,
        TimeSpan pollInterval,
        int maximumPollCount,
        bool captureOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        ValidateStartInfo(startInfo);
        ValidateTimeout(executionTimeout, nameof(executionTimeout));
        ValidateTimeout(pollInterval, nameof(pollInterval));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPollCount);
        cancellationToken.ThrowIfCancellationRequested();
        string fullMarkerPath = Path.GetFullPath(markerPath);

        GraphEvidenceOwnedProcess? process = null;
        try
        {
            process = GraphEvidenceOwnedProcess.Start(
                startInfo,
                captureOutput,
                executionTimeout,
                GraphEvidenceLauncherTestMode.None);
            var stopwatch = Stopwatch.StartNew();
            TimeSpan nextProgress = TimeSpan.FromSeconds(5);
            bool conditionSatisfied = false;
            bool cancelled = false;
            bool exited = false;
            bool runnerTerminationConfirmed = false;
            bool terminationDecisionTimedOut = false;
            int completedPolls = 0;

            for (int attempt = 0;
                attempt < maximumPollCount && stopwatch.Elapsed < executionTimeout;
                attempt++)
            {
                completedPolls = attempt + 1;
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                if (File.Exists(fullMarkerPath))
                {
                    conditionSatisfied = true;
                    GraphEvidenceTerminationDecision decision =
                        process.RequestTargetTerminationAndWait(
                            TerminationDecisionTimeout,
                            cancellationToken,
                            ExitPollInterval,
                            TimeSpan.FromSeconds(1));
                    exited = decision is GraphEvidenceTerminationDecision.TargetCompleted
                        or GraphEvidenceTerminationDecision.LauncherExitedWithoutDecision;
                    runnerTerminationConfirmed =
                        decision == GraphEvidenceTerminationDecision.TargetTerminationAcknowledged;
                    cancelled = decision == GraphEvidenceTerminationDecision.Cancelled;
                    terminationDecisionTimedOut =
                        decision == GraphEvidenceTerminationDecision.TimedOut;
                    break;
                }
                if (process.HasExited)
                {
                    exited = true;
                    break;
                }

                TimeSpan remaining = executionTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                TimeSpan delay = remaining < pollInterval ? remaining : pollInterval;
                if (cancellationToken.WaitHandle.WaitOne(delay))
                {
                    cancelled = true;
                    break;
                }
                if (stopwatch.Elapsed >= nextProgress)
                {
                    WriteProgress(process.Identity, "condition", completedPolls, stopwatch.Elapsed);
                    nextProgress += TimeSpan.FromSeconds(5);
                }
            }

            if (!conditionSatisfied && !exited)
                exited = process.HasExited;
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                conditionSatisfied = false;
            }
            bool timedOut = terminationDecisionTimedOut
                || (!conditionSatisfied && !cancelled && !exited);
            string? failure = terminationDecisionTimedOut
                ? "trusted launcher did not publish a termination decision before the decision deadline"
                : timedOut
                    ? FormattableString.Invariant(
                        $"condition was not satisfied after {completedPolls} polls and {stopwatch.Elapsed.TotalSeconds:F3}s")
                    : null;
            return Complete(
                process,
                timedOut,
                cancelled,
                conditionSatisfied,
                terminate: runnerTerminationConfirmed || !exited,
                completionRequired: !runnerTerminationConfirmed,
                runnerTerminationConfirmed,
                failure: failure);
        }
        catch (GraphEvidenceProcessStartException exception)
        {
            return StartedButInitializationFailed(exception, cancellationToken.IsCancellationRequested);
        }
        catch (Exception exception) when (exception is Win32Exception
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException)
        {
            return process is null
                ? NotStarted(startInfo, exception)
                : Complete(
                    process,
                    timedOut: false,
                    cancelled: cancellationToken.IsCancellationRequested,
                    conditionSatisfied: false,
                    terminate: true,
                    completionRequired: true,
                    runnerTerminationConfirmed: false,
                    failure: exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static GraphEvidenceProcessResult Complete(
        GraphEvidenceOwnedProcess process,
        bool timedOut,
        bool cancelled,
        bool conditionSatisfied,
        bool terminate,
        bool completionRequired,
        bool runnerTerminationConfirmed,
        string? failure)
    {
        bool hasContainedProcesses = process.TryHasActiveContainedProcesses(out bool containmentQuerySucceeded);
        bool terminationRequested = terminate || hasContainedProcesses || !containmentQuerySucceeded;
        bool cleanupConfirmed = terminationRequested
            ? process.TerminateAndWait(CleanupTimeout)
            : process.ConfirmExited();
        if (!cleanupConfirmed)
            failure = AppendFailure(failure, "process tree exit was not confirmed before the cleanup deadline");

        (bool launcherOutputDrained, bool outputDrainStopped) = process.FinishOutputDrain(
            OutputDrainTimeout,
            OutputDrainCancellationTimeout);
        bool targetCompletionObserved = process.TargetCompletionObserved;
        bool targetOutputDrained = !targetCompletionObserved || process.TargetOutputDrained;
        bool outputDrained = launcherOutputDrained && targetOutputDrained;
        if (!targetOutputDrained)
            failure = AppendFailure(failure, "target stdout/stderr drain failed inside the trusted launcher");
        if (!outputDrained)
            failure = AppendFailure(failure, "stdout/stderr drain did not complete before the output deadline");
        if (!outputDrainStopped)
            failure = AppendFailure(failure, "stdout/stderr drain task did not stop before the cancellation deadline");

        if (completionRequired && !targetCompletionObserved)
        {
            failure = AppendFailure(
                failure,
                "authenticated target completion was not observed before cleanup completed");
        }
        if (!completionRequired && !runnerTerminationConfirmed)
        {
            failure = AppendFailure(
                failure,
                "target completion exemption lacked an authenticated runner termination acknowledgement");
        }
        if (runnerTerminationConfirmed && targetCompletionObserved)
        {
            failure = AppendFailure(
                failure,
                "trusted launcher published conflicting completion and termination dispositions");
        }

        var result = new GraphEvidenceProcessResult(
            Started: true,
            TimedOut: timedOut,
            Cancelled: cancelled,
            ConditionSatisfied: conditionSatisfied,
            TerminationRequested: terminationRequested,
            CleanupConfirmed: cleanupConfirmed,
            OutputDrained: outputDrained,
            OutputDrainStopped: outputDrainStopped,
            CompletionRequired: completionRequired,
            TargetCompletionObserved: targetCompletionObserved,
            RunnerTerminationConfirmed: runnerTerminationConfirmed,
            TreeTrackingReliable: process.TreeTrackingReliable,
            ContainmentKind: process.ContainmentKind,
            AuthenticatedExitCode: cleanupConfirmed && targetCompletionObserved ? process.TargetExitCode : -1,
            StandardOutput: process.StandardOutput,
            StandardError: process.StandardError,
            Identity: process.Identity,
            Failure: failure);
        WriteCompletion(result);
        return result;
    }

    private static GraphEvidenceProcessResult StartedButInitializationFailed(
        GraphEvidenceProcessStartException exception,
        bool cancelled)
    {
        string failure = exception.CleanupConfirmed
            ? exception.InnerException?.Message ?? exception.Message
            : AppendFailure(
                exception.InnerException?.Message ?? exception.Message,
                "process tree exit was not confirmed after process startup failed");
        var result = new GraphEvidenceProcessResult(
            Started: true,
            TimedOut: false,
            Cancelled: cancelled,
            ConditionSatisfied: false,
            TerminationRequested: true,
            CleanupConfirmed: exception.CleanupConfirmed,
            OutputDrained: false,
            OutputDrainStopped: exception.OutputDrainStopped,
            CompletionRequired: true,
            TargetCompletionObserved: false,
            RunnerTerminationConfirmed: false,
            TreeTrackingReliable: exception.TreeTrackingReliable,
            ContainmentKind: exception.ContainmentKind,
            AuthenticatedExitCode: -1,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            Identity: exception.Identity,
            Failure: failure);
        WriteCompletion(result);
        return result;
    }

    private static GraphEvidenceProcessResult NotStarted(ProcessStartInfo startInfo, Exception exception)
    {
        GraphEvidenceProcessIdentity identity = GraphEvidenceProcessIdentity.CreateNotStarted(startInfo);
        var result = new GraphEvidenceProcessResult(
            Started: false,
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
            TreeTrackingReliable: false,
            ContainmentKind: "not-started",
            AuthenticatedExitCode: -1,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            Identity: identity,
            Failure: exception.Message);
        WriteCompletion(result);
        return result;
    }

    private static string AppendFailure(string? current, string additional)
        => string.IsNullOrWhiteSpace(current) ? additional : current + "; " + additional;

    private static void ValidateStartInfo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.FileName);
        if (startInfo.UseShellExecute)
            throw new ArgumentException("Evidence process 不允许 shell execution。", nameof(startInfo));
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
            throw new ArgumentException("Evidence process 必须重定向 stdout 和 stderr。", nameof(startInfo));
        if (startInfo.RedirectStandardInput)
            throw new ArgumentException("Evidence process 不接受 stdin 重定向。", nameof(startInfo));
        if (!string.IsNullOrEmpty(startInfo.Arguments))
            throw new ArgumentException("Evidence process 必须使用结构化 ArgumentList。", nameof(startInfo));
        if (startInfo.ArgumentList.Count > MaximumArgumentCount)
            throw new ArgumentException($"Evidence process 参数不能超过 {MaximumArgumentCount} 项。", nameof(startInfo));
        if (startInfo.ArgumentList.Any(static argument => argument.Length > MaximumArgumentLength))
        {
            throw new ArgumentException(
                $"Evidence process 单个参数不能超过 {MaximumArgumentLength} 个字符。",
                nameof(startInfo));
        }
        if (startInfo.Environment.Count > MaximumEnvironmentVariableCount)
        {
            throw new ArgumentException(
                $"Evidence process 环境变量不能超过 {MaximumEnvironmentVariableCount} 项。",
                nameof(startInfo));
        }
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "超时必须为可由 Process API 表示的正时间段。");
    }

    private static void WriteProgress(
        GraphEvidenceProcessIdentity identity,
        string phase,
        int polls,
        TimeSpan elapsed)
        => TryWriteDiagnostic(FormattableString.Invariant(
            $"m40-process-progress phase={phase} pid={identity.ProcessId} parent_pid={identity.ParentProcessId} polls={polls} elapsed_seconds={elapsed.TotalSeconds:F3}"));

    private static void WriteCompletion(GraphEvidenceProcessResult result)
        => TryWriteDiagnostic(FormattableString.Invariant(
            $"m40-process-complete pid={result.Identity.ProcessId} parent_pid={result.Identity.ParentProcessId} started_utc={result.Identity.StartedUtc:O} containment={result.ContainmentKind} tree_tracking_reliable={result.TreeTrackingReliable} timed_out={result.TimedOut} cancelled={result.Cancelled} cleanup_confirmed={result.CleanupConfirmed} output_drained={result.OutputDrained} output_drain_stopped={result.OutputDrainStopped} completion_required={result.CompletionRequired} target_completion_observed={result.TargetCompletionObserved} runner_termination_confirmed={result.RunnerTerminationConfirmed} exit_code={result.ExitCode} command={result.Identity.CommandDisplay}"));

    private static void TryWriteDiagnostic(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // 诊断输出不可反向破坏已启动进程的监督与回收。
        }
    }

    private enum GraphEvidenceWaitOutcome : byte
    {
        Exited = 1,
        TimedOut = 2,
        Cancelled = 3,
    }

    private enum GraphEvidenceTerminationDecision : byte
    {
        TargetCompleted = 1,
        TargetTerminationAcknowledged = 2,
        LauncherExitedWithoutDecision = 3,
        TimedOut = 4,
        Cancelled = 5,
    }

    private sealed class GraphEvidenceProcessStartException : Exception
    {
        internal GraphEvidenceProcessStartException(
            GraphEvidenceProcessIdentity identity,
            string containmentKind,
            bool treeTrackingReliable,
            bool cleanupConfirmed,
            bool outputDrainStopped,
            Exception innerException)
            : base("Evidence process 启动后的监督初始化失败。", innerException)
        {
            Identity = identity;
            ContainmentKind = containmentKind;
            TreeTrackingReliable = treeTrackingReliable;
            CleanupConfirmed = cleanupConfirmed;
            OutputDrainStopped = outputDrainStopped;
        }

        internal GraphEvidenceProcessIdentity Identity { get; }

        internal string ContainmentKind { get; }

        internal bool TreeTrackingReliable { get; }

        internal bool CleanupConfirmed { get; }

        internal bool OutputDrainStopped { get; }
    }

    private sealed class GraphEvidenceOwnedProcess : IDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _outputCancellation = new();
        private readonly BoundedCaptureStream _standardOutput;
        private readonly BoundedCaptureStream _standardError;
        private readonly Task _outputDrain;
        private readonly GraphEvidenceProcessContainment _containment;
        private readonly GraphEvidenceProcessControl _control;
        private GraphEvidenceLauncherCompletion? _targetCompletion;
        private bool _cleanupConfirmed;
        private bool _cleanupFinalized;
        private bool _outputFinalized;
        private bool _disposed;

        private GraphEvidenceOwnedProcess(
            Process process,
            GraphEvidenceProcessIdentity identity,
            bool captureOutput,
            GraphEvidenceProcessContainment containment,
            GraphEvidenceProcessControl control)
        {
            _process = process;
            Identity = identity;
            _containment = containment;
            _control = control;
            int capacity = captureOutput ? MaximumCapturedOutputBytes : 0;
            _standardOutput = new BoundedCaptureStream(capacity);
            _standardError = new BoundedCaptureStream(capacity);
            Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(
                _standardOutput,
                bufferSize: 81_920,
                _outputCancellation.Token);
            Task errorTask = process.StandardError.BaseStream.CopyToAsync(
                _standardError,
                bufferSize: 81_920,
                _outputCancellation.Token);
            _outputDrain = Task.WhenAll(outputTask, errorTask);
            _ = _outputDrain.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal GraphEvidenceProcessIdentity Identity { get; }

        internal bool TreeTrackingReliable => _containment.TreeTrackingReliable;

        internal string ContainmentKind => _containment.Kind;

        internal bool HasExited
        {
            get
            {
                if (TryRefreshTargetCompletion())
                    return true;
                return HasExitedSafely(_process);
            }
        }

        internal int TargetExitCode => _targetCompletion?.ExitCode ?? -1;

        internal bool TargetOutputDrained => _targetCompletion?.OutputDrained ?? true;

        internal bool TargetCompletionObserved => TryRefreshTargetCompletion();

        internal string StandardOutput => _standardOutput.GetText("stdout");

        internal string StandardError => _standardError.GetText("stderr");

        internal static GraphEvidenceOwnedProcess Start(
            ProcessStartInfo startInfo,
            bool captureOutput,
            TimeSpan executionTimeout,
            GraphEvidenceLauncherTestMode launcherTestMode)
        {
            DateTimeOffset launchUtc = DateTimeOffset.UtcNow;
            using Process parent = Process.GetCurrentProcess();
            DateTimeOffset parentStartedUtc = ReadStartTime(parent, launchUtc);
            string parentIdentityToken = GraphEvidenceProcessIdentityToken.Create(parent);
            string handshakeToken = Guid.NewGuid().ToString("N");
            GraphEvidenceProcessControl control = GraphEvidenceProcessControl.Create(
                handshakeToken,
                startInfo.Environment);
            TimeSpan launcherLifetime = executionTimeout
                + CleanupTimeout
                + OutputDrainTimeout
                + OutputDrainCancellationTimeout
                + TimeSpan.FromSeconds(30);
            ProcessStartInfo effectiveStartInfo;
            bool expectsUnixProcessGroup;
            try
            {
                ProcessStartInfo launcherStartInfo = GraphEvidenceProcessLauncher.CreateStartInfo(
                    startInfo,
                    parent.Id,
                    parentIdentityToken,
                    launcherLifetime,
                    handshakeToken,
                    control.CompletionPath,
                    launcherTestMode);
                effectiveStartInfo = GraphEvidenceProcessContainment.PrepareStartInfo(
                    launcherStartInfo,
                    out expectsUnixProcessGroup);
            }
            catch
            {
                control.Dispose();
                throw;
            }
            var process = new Process { StartInfo = effectiveStartInfo };
            GraphEvidenceProcessContainment? containment = null;
            GraphEvidenceProcessIdentity? identity = null;
            GraphEvidenceOwnedProcess? ownedProcess = null;
            bool started = false;
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("无法启动 M40 evidence process。");
                started = true;
                DateTimeOffset startedUtc = ReadStartTime(process, launchUtc);
                identity = GraphEvidenceProcessIdentity.Create(
                    process.Id,
                    startedUtc,
                    parent.Id,
                    parentStartedUtc,
                    startInfo);
                containment = GraphEvidenceProcessContainment.Attach(process, expectsUnixProcessGroup);
                if (!containment.TreeTrackingReliable)
                {
                    throw new InvalidOperationException(
                        "无法在启动真实命令前确认可靠的 evidence process containment。");
                }
                ownedProcess = new GraphEvidenceOwnedProcess(
                    process,
                    identity,
                    captureOutput,
                    containment,
                    control);
                ownedProcess.ReleaseLauncher(handshakeToken);
                TryWriteDiagnostic(FormattableString.Invariant(
                    $"m40-process-start pid={identity.ProcessId} parent_pid={identity.ParentProcessId} started_utc={identity.StartedUtc:O} parent_started_utc={identity.ParentStartedUtc:O} containment={containment.Kind} tree_tracking_reliable={containment.TreeTrackingReliable} working_directory={identity.WorkingDirectory} command={identity.CommandDisplay}"));
                return ownedProcess;
            }
            catch (Exception exception)
            {
                if (!started)
                {
                    containment?.Dispose();
                    process.Dispose();
                    control.Dispose();
                    throw;
                }

                identity ??= GraphEvidenceProcessIdentity.Create(
                    process.Id,
                    ReadStartTime(process, launchUtc),
                    parent.Id,
                    parentStartedUtc,
                    startInfo);
                string containmentKind = containment?.Kind ?? "containment-setup-failed";
                bool treeTrackingReliable = containment?.TreeTrackingReliable == true;
                bool cleanupConfirmed = false;
                bool outputDrainStopped = false;
                try
                {
                    if (ownedProcess is not null)
                    {
                        cleanupConfirmed = ownedProcess.TerminateAndWait(CleanupTimeout);
                        (_, outputDrainStopped) = ownedProcess.FinishOutputDrain(
                            OutputDrainTimeout,
                            OutputDrainCancellationTimeout);
                    }
                    else
                    {
                        cleanupConfirmed = TerminateUnreleasedLauncherAndWait(
                            process,
                            containment,
                            identity,
                            CleanupTimeout);
                        _ = CloseRedirectedStreams(process);
                    }
                }
                catch (Exception cleanupException) when (cleanupException is Win32Exception
                    or IOException
                    or InvalidOperationException
                    or NotSupportedException
                    or DllNotFoundException
                    or EntryPointNotFoundException
                    or AggregateException)
                {
                    cleanupConfirmed = false;
                    outputDrainStopped = false;
                    TryWriteDiagnostic(
                        $"m40-process-startup-cleanup-failed pid={identity.ProcessId} error={cleanupException.Message}");
                }
                finally
                {
                    try
                    {
                        if (ownedProcess is not null)
                        {
                            ownedProcess.Dispose();
                        }
                        else
                        {
                            try
                            {
                                containment?.Dispose();
                            }
                            finally
                            {
                                process.Dispose();
                            }
                        }
                        if (cleanupConfirmed)
                            control.Dispose();
                        else
                            TryWriteDiagnostic($"m40-process-control-temp-retained path={control.DirectoryPath}");
                    }
                    catch (Exception disposeException) when (disposeException is InvalidOperationException
                        or IOException
                        or ObjectDisposedException)
                    {
                        cleanupConfirmed = false;
                        outputDrainStopped = false;
                        TryWriteDiagnostic(
                            $"m40-process-startup-dispose-failed pid={identity.ProcessId} error={disposeException.Message}");
                    }
                }
                throw new GraphEvidenceProcessStartException(
                    identity,
                    containmentKind,
                    treeTrackingReliable,
                    cleanupConfirmed,
                    outputDrainStopped,
                    exception);
            }
        }

        internal void ReleaseLauncher(string handshakeToken)
        {
            string handshake = _containment.CreateLauncherHandshake(_process, handshakeToken);
            _process.StandardInput.WriteLine(handshake);
            _process.StandardInput.Flush();
            _process.StandardInput.Dispose();
        }

        internal GraphEvidenceWaitOutcome WaitForExit(
            TimeSpan timeout,
            CancellationToken cancellationToken,
            TimeSpan pollInterval,
            TimeSpan progressInterval,
            string phase)
        {
            var stopwatch = Stopwatch.StartNew();
            int maximumPollCount = checked((int)Math.Ceiling(timeout.TotalMilliseconds / pollInterval.TotalMilliseconds) + 1);
            TimeSpan nextProgress = progressInterval;
            for (int attempt = 0;
                attempt < maximumPollCount && stopwatch.Elapsed < timeout;
                attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return GraphEvidenceWaitOutcome.Cancelled;
                if (HasExited)
                    return GraphEvidenceWaitOutcome.Exited;

                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                int waitMilliseconds = Math.Max(
                    1,
                    (int)Math.Ceiling(Math.Min(remaining.TotalMilliseconds, pollInterval.TotalMilliseconds)));
                if (_process.WaitForExit(waitMilliseconds))
                    return GraphEvidenceWaitOutcome.Exited;
                if (stopwatch.Elapsed >= nextProgress)
                {
                    WriteProgress(Identity, phase, attempt + 1, stopwatch.Elapsed);
                    nextProgress += progressInterval;
                }
            }
            return HasExited ? GraphEvidenceWaitOutcome.Exited : GraphEvidenceWaitOutcome.TimedOut;
        }

        internal GraphEvidenceTerminationDecision RequestTargetTerminationAndWait(
            TimeSpan timeout,
            CancellationToken cancellationToken,
            TimeSpan pollInterval,
            TimeSpan progressInterval)
        {
            ValidateTimeout(timeout, nameof(timeout));
            ValidateTimeout(pollInterval, nameof(pollInterval));
            ValidateTimeout(progressInterval, nameof(progressInterval));
            if (cancellationToken.IsCancellationRequested)
                return GraphEvidenceTerminationDecision.Cancelled;

            GraphEvidenceTerminationDecision? initial = TryReadTerminationDecision();
            if (initial is not null)
                return initial.Value;
            _control.PublishTerminationRequest();

            var stopwatch = Stopwatch.StartNew();
            int maximumPollCount = checked(
                (int)Math.Ceiling(timeout.TotalMilliseconds / pollInterval.TotalMilliseconds) + 1);
            TimeSpan nextProgress = progressInterval;
            for (int attempt = 0;
                attempt < maximumPollCount && stopwatch.Elapsed < timeout;
                attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return GraphEvidenceTerminationDecision.Cancelled;
                GraphEvidenceTerminationDecision? decision = TryReadTerminationDecision();
                if (decision is not null)
                    return decision.Value;
                if (HasExitedSafely(_process))
                {
                    decision = TryReadTerminationDecision();
                    return decision ?? GraphEvidenceTerminationDecision.LauncherExitedWithoutDecision;
                }

                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                TimeSpan delay = remaining < pollInterval ? remaining : pollInterval;
                if (cancellationToken.WaitHandle.WaitOne(delay))
                    return GraphEvidenceTerminationDecision.Cancelled;
                if (stopwatch.Elapsed >= nextProgress)
                {
                    WriteProgress(Identity, "termination-decision", attempt + 1, stopwatch.Elapsed);
                    nextProgress += progressInterval;
                }
            }

            GraphEvidenceTerminationDecision? final = TryReadTerminationDecision();
            if (final is not null)
                return final.Value;
            return HasExitedSafely(_process)
                ? GraphEvidenceTerminationDecision.LauncherExitedWithoutDecision
                : GraphEvidenceTerminationDecision.TimedOut;
        }

        internal bool TryHasActiveContainedProcesses(out bool querySucceeded)
        {
            querySucceeded = TryReadContainmentState(_containment, out bool active);
            return active;
        }

        internal bool ConfirmExited()
        {
            if (!HasExitedSafely(_process))
                return false;
            if (!_containment.TreeTrackingReliable)
                return false;
            return TryReadContainmentState(_containment, out bool active) && !active;
        }

        internal bool TerminateAndWait(TimeSpan timeout)
        {
            if (_cleanupFinalized)
                return _cleanupConfirmed;
            _cleanupFinalized = true;
            _cleanupConfirmed = TerminateAndWaitCore(timeout);
            return _cleanupConfirmed;
        }

        private bool TerminateAndWaitCore(TimeSpan timeout)
            => TerminateAndWaitCore(_process, _containment, Identity, timeout);

        private static bool TerminateUnreleasedLauncherAndWait(
            Process process,
            GraphEvidenceProcessContainment? containment,
            GraphEvidenceProcessIdentity identity,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            if (containment is not null)
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
                    TryWriteDiagnostic(
                        $"m40-process-launcher-termination-request-failed pid={identity.ProcessId} error={exception.Message}");
                }
            }
            try
            {
                if (!HasExitedSafely(process))
                    process.Kill(entireProcessTree: false);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or Win32Exception
                or NotSupportedException)
            {
                TryWriteDiagnostic(
                    $"m40-process-launcher-root-termination-failed pid={identity.ProcessId} error={exception.Message}");
            }

            int maximumPollCount = checked(
                (int)Math.Ceiling(timeout.TotalMilliseconds / ExitPollInterval.TotalMilliseconds) + 1);
            for (int attempt = 0;
                attempt < maximumPollCount && stopwatch.Elapsed < timeout;
                attempt++)
            {
                if (HasExitedSafely(process))
                    return true;
                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                int waitMilliseconds = Math.Max(
                    1,
                    (int)Math.Ceiling(Math.Min(
                        remaining.TotalMilliseconds,
                        ExitPollInterval.TotalMilliseconds)));
                if (process.WaitForExit(waitMilliseconds))
                    return true;
            }
            return HasExitedSafely(process);
        }

        private static bool TerminateAndWaitCore(
            Process process,
            GraphEvidenceProcessContainment containment,
            GraphEvidenceProcessIdentity identity,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
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
                TryWriteDiagnostic(
                    $"m40-process-termination-request-failed pid={identity.ProcessId} error={exception.Message}");
            }
            try
            {
                if (!HasExitedSafely(process))
                    process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException) when (HasExitedSafely(process))
            {
            }
            catch (Exception exception) when (exception is Win32Exception or NotSupportedException)
            {
                TryWriteDiagnostic(
                    $"m40-process-root-termination-failed pid={identity.ProcessId} error={exception.Message}");
            }

            int maximumPollCount = checked(
                (int)Math.Ceiling(timeout.TotalMilliseconds / ExitPollInterval.TotalMilliseconds) + 1);
            TimeSpan nextProgress = CleanupProgressInterval;
            for (int attempt = 0;
                attempt < maximumPollCount && stopwatch.Elapsed < timeout;
                attempt++)
            {
                bool rootExited = HasExitedSafely(process);
                bool containmentKnown = TryReadContainmentState(containment, out bool active);
                bool containmentEmpty = containmentKnown && !active;
                if (rootExited && !containment.TreeTrackingReliable)
                    return false;
                if (rootExited && containmentEmpty)
                    return true;

                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                int waitMilliseconds = Math.Max(
                    1,
                    (int)Math.Ceiling(Math.Min(
                        remaining.TotalMilliseconds,
                        ExitPollInterval.TotalMilliseconds)));
                if (!rootExited)
                    _ = process.WaitForExit(waitMilliseconds);
                else
                    Thread.Sleep(waitMilliseconds);
                if (stopwatch.Elapsed >= nextProgress)
                {
                    WriteProgress(identity, "cleanup", attempt + 1, stopwatch.Elapsed);
                    nextProgress += CleanupProgressInterval;
                }
            }

            bool finalRootExited = HasExitedSafely(process);
            bool finalContainmentKnown = TryReadContainmentState(containment, out bool finalActive);
            bool finalContainmentEmpty = finalContainmentKnown && !finalActive;
            return finalRootExited
                && finalContainmentKnown
                && finalContainmentEmpty
                && containment.TreeTrackingReliable;
        }

        private static bool TryReadContainmentState(
            GraphEvidenceProcessContainment containment,
            out bool active)
        {
            try
            {
                return containment.TryHasActiveProcesses(out active);
            }
            catch (Exception exception) when (exception is Win32Exception
                or InvalidOperationException
                or NotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException)
            {
                active = true;
                return false;
            }
        }

        private static bool CloseRedirectedStreams(Process process)
        {
            bool closed = true;
            try
            {
                process.StandardOutput.Dispose();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                closed = false;
            }
            try
            {
                process.StandardError.Dispose();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                closed = false;
            }
            return closed;
        }

        internal (bool Drained, bool Stopped) FinishOutputDrain(
            TimeSpan drainTimeout,
            TimeSpan cancellationTimeout)
        {
            if (_outputFinalized)
                return (_outputDrain.Status == TaskStatus.RanToCompletion, _outputDrain.IsCompleted);
            _outputFinalized = true;
            try
            {
                if (_outputDrain.Wait(drainTimeout))
                    return (_outputDrain.Status == TaskStatus.RanToCompletion, true);
            }
            catch (AggregateException)
            {
                return (false, true);
            }

            _outputCancellation.Cancel();
            _ = CloseRedirectedStreams(_process);

            try
            {
                _ = _outputDrain.Wait(cancellationTimeout);
            }
            catch (AggregateException)
            {
                // 已取消或 pipe 已关闭都表示 drain task 已停止。
            }
            return (false, _outputDrain.IsCompleted);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            bool cleanupConfirmed = ConfirmExited() || TerminateAndWait(CleanupTimeout);
            if (!cleanupConfirmed)
            {
                TryWriteDiagnostic(
                    $"m40-process-cleanup-failed pid={Identity.ProcessId} command={Identity.CommandDisplay}");
            }
            if (!_outputFinalized)
                _ = FinishOutputDrain(OutputDrainTimeout, OutputDrainCancellationTimeout);
            _outputCancellation.Cancel();
            _containment.Dispose();
            _process.Dispose();
            if (cleanupConfirmed)
                _control.Dispose();
            else
                TryWriteDiagnostic($"m40-process-control-temp-retained path={_control.DirectoryPath}");
            if (_outputDrain.IsCompleted)
            {
                _outputCancellation.Dispose();
            }
            else
            {
                _ = _outputDrain.ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    _outputCancellation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

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

        private bool TryRefreshTargetCompletion()
        {
            if (_targetCompletion is not null)
                return true;
            if (!_control.TryReadCompletion(out GraphEvidenceLauncherCompletion completion))
                return false;
            _targetCompletion = completion;
            return true;
        }

        private GraphEvidenceTerminationDecision? TryReadTerminationDecision()
        {
            bool targetCompleted = TryRefreshTargetCompletion();
            bool terminationAcknowledged = _control.TryReadTerminationAcknowledgement();
            if (targetCompleted && terminationAcknowledged)
                throw new InvalidDataException("Trusted launcher 发布了冲突的 completion 与 termination 状态。");
            if (targetCompleted)
                return GraphEvidenceTerminationDecision.TargetCompleted;
            if (terminationAcknowledged)
                return GraphEvidenceTerminationDecision.TargetTerminationAcknowledged;
            return null;
        }

        private static DateTimeOffset ReadStartTime(Process process, DateTimeOffset fallback)
        {
            try
            {
                return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return fallback;
            }
        }
    }

    private sealed class BoundedCaptureStream(int capacity) : Stream
    {
        private readonly object _sync = new();
        private readonly MemoryStream _captured = new(Math.Max(0, capacity));
        private long _totalBytes;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Capture(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
            => Capture(buffer);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Capture(buffer.Span);
            return ValueTask.CompletedTask;
        }

        internal string GetText(string streamName)
        {
            lock (_sync)
            {
                string text = Encoding.UTF8.GetString(_captured.GetBuffer(), 0, checked((int)_captured.Length));
                return _totalBytes <= capacity
                    ? text
                    : text + FormattableString.Invariant(
                        $"\n...[{streamName} truncated; captured={capacity} total>={_totalBytes}]...");
            }
        }

        private void Capture(ReadOnlySpan<byte> buffer)
        {
            lock (_sync)
            {
                _totalBytes = _totalBytes > long.MaxValue - buffer.Length
                    ? long.MaxValue
                    : _totalBytes + buffer.Length;
                int remaining = capacity - checked((int)_captured.Length);
                if (remaining > 0)
                    _captured.Write(buffer[..Math.Min(remaining, buffer.Length)]);
            }
        }
    }
}

internal sealed record GraphEvidenceProcessIdentity(
    int ProcessId,
    DateTimeOffset StartedUtc,
    int ParentProcessId,
    DateTimeOffset ParentStartedUtc,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    internal string CommandDisplay => FileName + (Arguments.Count == 0
        ? string.Empty
        : " " + string.Join(' ', Arguments.Take(64).Select(EscapeArgument))
            + (Arguments.Count > 64 ? " ...[arguments truncated]" : string.Empty));

    internal static GraphEvidenceProcessIdentity Create(
        int processId,
        DateTimeOffset startedUtc,
        int parentProcessId,
        DateTimeOffset parentStartedUtc,
        ProcessStartInfo startInfo)
        => new(
            processId,
            startedUtc,
            parentProcessId,
            parentStartedUtc,
            startInfo.FileName,
            startInfo.ArgumentList.ToArray(),
            ResolveWorkingDirectory(startInfo.WorkingDirectory));

    internal static GraphEvidenceProcessIdentity CreateNotStarted(ProcessStartInfo startInfo)
    {
        using Process parent = Process.GetCurrentProcess();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset parentStartedUtc;
        try
        {
            parentStartedUtc = new DateTimeOffset(parent.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            parentStartedUtc = now;
        }
        return Create(-1, now, parent.Id, parentStartedUtc, startInfo);
    }

    private static string ResolveWorkingDirectory(string workingDirectory)
        => string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workingDirectory);

    private static string EscapeArgument(string argument)
    {
        string bounded = argument.Length <= 512 ? argument : argument[..512] + "...[truncated]";
        return '"' + bounded
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + '"';
    }
}

internal sealed record GraphEvidenceProcessResult(
    bool Started,
    bool TimedOut,
    bool Cancelled,
    bool ConditionSatisfied,
    bool TerminationRequested,
    bool CleanupConfirmed,
    bool OutputDrained,
    bool OutputDrainStopped,
    bool CompletionRequired,
    bool TargetCompletionObserved,
    bool RunnerTerminationConfirmed,
    bool TreeTrackingReliable,
    string ContainmentKind,
    int AuthenticatedExitCode,
    string StandardOutput,
    string StandardError,
    GraphEvidenceProcessIdentity Identity,
    string? Failure)
{
    internal int ExitCode => TargetCompletionObserved ? AuthenticatedExitCode : -1;

    internal bool Completed => Started
        && !TimedOut
        && !Cancelled
        && CleanupConfirmed
        && TreeTrackingReliable
        && OutputDrained
        && OutputDrainStopped
        && (TargetCompletionObserved
            || (!CompletionRequired
                && ConditionSatisfied
                && TerminationRequested
                && RunnerTerminationConfirmed))
        && Failure is null;

    internal string Diagnostic => FormattableString.Invariant(
        $"pid={Identity.ProcessId} parent_pid={Identity.ParentProcessId} started_utc={Identity.StartedUtc:O} containment={ContainmentKind} tree_tracking_reliable={TreeTrackingReliable} completion_required={CompletionRequired} target_completion_observed={TargetCompletionObserved} runner_termination_confirmed={RunnerTerminationConfirmed} working_directory={Identity.WorkingDirectory} command={Identity.CommandDisplay} failure={Failure ?? "<none>"}");
}
