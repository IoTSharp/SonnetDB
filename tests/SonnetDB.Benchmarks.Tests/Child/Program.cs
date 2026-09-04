using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using SonnetDB.Benchmarks.Benchmarks;

namespace SonnetDB.Benchmarks.Tests.ProcessProbe;

internal static class Program
{
    private const int IdentityWaitAttempts = 100;
    private const int LifetimeWaitAttempts = 600;
    private const int OutputWriteIterations = 8;
    private const int OutputChunkCharacters = 32 * 1024;
    private const int ProgressInterval = 50;

    private static readonly TimeSpan IdentityWaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LifetimeWaitLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OutputWriteLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CleanupWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadyExitDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReadyExitPipeHoldDelay = TimeSpan.FromSeconds(1);

    private static async Task<int> Main(string[] args)
    {
        bool standardMode = args.Length is 1 or 2
            && (args.Length == 1
                || string.Equals(args[1], "leaf", StringComparison.Ordinal)
                || string.Equals(args[1], "detach", StringComparison.Ordinal)
                || string.Equals(args[1], "ready-exit", StringComparison.Ordinal));
        int readyParentProcessId = 0;
        bool readyHelperMode = args.Length == 4
            && string.Equals(args[1], "ready-after-parent-exit", StringComparison.Ordinal)
            && int.TryParse(
                args[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out readyParentProcessId)
            && readyParentProcessId > 0
            && !string.IsNullOrWhiteSpace(args[3]);
        if (!standardMode && !readyHelperMode)
        {
            Console.Error.WriteLine(
                "Usage: dotnet SonnetDB.Benchmarks.Tests.ProcessProbe.dll <state-directory> "
                + "[leaf|detach|ready-exit|ready-after-parent-exit <parent-pid> <parent-token>]");
            return 2;
        }

        string stateDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[0]));
        Directory.CreateDirectory(stateDirectory);

        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (args.Length == 2 && string.Equals(args[1], "leaf", StringComparison.Ordinal))
                return await RunLeafAsync(stateDirectory, cancellationSource.Token).ConfigureAwait(false);
            if (readyHelperMode)
            {
                return await RunReadyAfterParentExitAsync(
                        stateDirectory,
                        readyParentProcessId,
                        args[3],
                        cancellationSource.Token)
                    .ConfigureAwait(false);
            }
            if (args.Length == 2 && string.Equals(args[1], "ready-exit", StringComparison.Ordinal))
                return await RunReadyAndExitAsync(stateDirectory, cancellationSource.Token).ConfigureAwait(false);
            return await RunParentAsync(
                    stateDirectory,
                    detachLeaf: args.Length == 2,
                    cancellationSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            Console.Error.WriteLine("Process probe cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunReadyAndExitAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        ProcessIdentity parentIdentity = GetCurrentIdentity();
        await RecordIdentityAsync(stateDirectory, "parent", parentIdentity, cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(ReadyExitDelay, cancellationToken).ConfigureAwait(false);

        string dotnetPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to resolve the current dotnet host path.");
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetPath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(stateDirectory);
        startInfo.ArgumentList.Add("ready-after-parent-exit");
        startInfo.ArgumentList.Add(parentIdentity.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(parentIdentity.IdentityToken);

        using var helper = new Process { StartInfo = startInfo };
        if (!helper.Start())
            throw new InvalidOperationException("Unable to start the ready-exit helper.");
        ProcessIdentity helperIdentity = GetIdentity(helper);
        await WaitForLeafIdentityAsync(stateDirectory, helperIdentity, cancellationToken)
            .ConfigureAwait(false);
        return 23;
    }

    private static async Task<int> RunReadyAfterParentExitAsync(
        string stateDirectory,
        int parentProcessId,
        string parentIdentityToken,
        CancellationToken cancellationToken)
    {
        await RecordIdentityAsync(stateDirectory, "leaf", GetCurrentIdentity(), cancellationToken)
            .ConfigureAwait(false);

        var wait = Stopwatch.StartNew();
        bool parentExited = false;
        for (int attempt = 0;
            attempt < IdentityWaitAttempts && wait.Elapsed < IdentityWaitLimit;
            attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GraphEvidenceProcessIdentityToken.IsExpectedProcessAlive(
                    parentProcessId,
                    parentIdentityToken))
            {
                parentExited = true;
                break;
            }
            if (attempt > 0 && attempt % ProgressInterval == 0)
                Console.Error.WriteLine($"ready-parent-exit-wait-attempt={attempt}");
            await DelayWithinLimitAsync(wait, IdentityWaitLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        if (!parentExited)
            throw new TimeoutException("The ready-exit helper did not observe its parent exit in time.");

        await WriteStateFileAsync(stateDirectory, "ready", "ready-exit", cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(ReadyExitPipeHoldDelay, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunParentAsync(
        string stateDirectory,
        bool detachLeaf,
        CancellationToken cancellationToken)
    {
        ProcessIdentity parentIdentity = GetCurrentIdentity();
        await RecordIdentityAsync(stateDirectory, "parent", parentIdentity, cancellationToken)
            .ConfigureAwait(false);

        string dotnetPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to resolve the current dotnet host path.");
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetPath,
            UseShellExecute = false,
            RedirectStandardOutput = !detachLeaf,
            RedirectStandardError = !detachLeaf,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(stateDirectory);
        startInfo.ArgumentList.Add("leaf");

        using var leafProcess = new Process { StartInfo = startInfo };
        ProcessIdentity? launchedIdentity = null;
        Task? stdoutDrain = null;
        Task? stderrDrain = null;
        using var drainCancellationSource = new CancellationTokenSource(
            LifetimeWaitLimit + IdentityWaitLimit + OutputWriteLimit + CleanupWaitLimit);

        try
        {
            if (!leafProcess.Start())
                throw new InvalidOperationException("Unable to start the process probe leaf.");

            if (!detachLeaf)
            {
                stdoutDrain = leafProcess.StandardOutput.BaseStream.CopyToAsync(
                    Stream.Null,
                    drainCancellationSource.Token);
                stderrDrain = leafProcess.StandardError.BaseStream.CopyToAsync(
                    Stream.Null,
                    drainCancellationSource.Token);
            }

            launchedIdentity = GetIdentity(leafProcess);
            await RecordLaunchAsync(
                    stateDirectory,
                    dotnetPath,
                    [assemblyPath, stateDirectory, "leaf"],
                    parentIdentity,
                    launchedIdentity.Value,
                    cancellationToken)
                .ConfigureAwait(false);

            await WaitForLeafIdentityAsync(
                    stateDirectory,
                    launchedIdentity.Value,
                    cancellationToken)
                .ConfigureAwait(false);

            if (detachLeaf)
            {
                await WriteStateFileAsync(stateDirectory, "ready", "detached", cancellationToken)
                    .ConfigureAwait(false);
                return 0;
            }

            await WriteProbeOutputAsync(cancellationToken).ConfigureAwait(false);
            await WriteStateFileAsync(stateDirectory, "ready", "ready", cancellationToken)
                .ConfigureAwait(false);

            await WaitForLeafExitAsync(leafProcess, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await ReclaimOwnedLeafAsync(
                    leafProcess,
                    launchedIdentity,
                    stdoutDrain,
                    stderrDrain,
                    drainCancellationSource,
                    terminateLeaf: !detachLeaf)
                .ConfigureAwait(false);
        }
    }

    private static async Task<int> RunLeafAsync(string stateDirectory, CancellationToken cancellationToken)
    {
        var lifetime = Stopwatch.StartNew();
        await RecordIdentityAsync(stateDirectory, "leaf", GetCurrentIdentity(), cancellationToken)
            .ConfigureAwait(false);

        for (int attempt = 0;
             attempt < LifetimeWaitAttempts && lifetime.Elapsed < LifetimeWaitLimit;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0 && attempt % ProgressInterval == 0)
                Console.Error.WriteLine($"leaf-wait-attempt={attempt}");

            await DelayWithinLimitAsync(lifetime, LifetimeWaitLimit, cancellationToken)
                .ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task WaitForLeafIdentityAsync(
        string stateDirectory,
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        var wait = Stopwatch.StartNew();
        for (int attempt = 0;
             attempt < IdentityWaitAttempts && wait.Elapsed < IdentityWaitLimit;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadIdentity(stateDirectory, "leaf", out ProcessIdentity identity) &&
                identity == expectedIdentity)
            {
                return;
            }

            if (attempt > 0 && attempt % ProgressInterval == 0)
                Console.Error.WriteLine($"leaf-identity-wait-attempt={attempt}");

            await DelayWithinLimitAsync(wait, IdentityWaitLimit, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The process probe leaf did not publish a matching identity in time.");
    }

    private static async Task WriteProbeOutputAsync(CancellationToken cancellationToken)
    {
        using var outputCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        outputCancellationSource.CancelAfter(OutputWriteLimit);

        Task stdout = WriteRepeatedAsync(
            Console.Out,
            new string('O', OutputChunkCharacters),
            outputCancellationSource.Token);
        Task stderr = WriteRepeatedAsync(
            Console.Error,
            new string('E', OutputChunkCharacters),
            outputCancellationSource.Token);

        try
        {
            await Task.WhenAll(stdout, stderr)
                .WaitAsync(OutputWriteLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            outputCancellationSource.Cancel();
        }
    }

    private static async Task WriteRepeatedAsync(
        TextWriter writer,
        string chunk,
        CancellationToken cancellationToken)
    {
        var writeTime = Stopwatch.StartNew();
        int completedIterations = 0;
        for (int iteration = 0;
             iteration < OutputWriteIterations && writeTime.Elapsed < OutputWriteLimit;
             iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = OutputWriteLimit - writeTime.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("The process probe output deadline elapsed.");

            await writer.WriteAsync(chunk.AsMemory(), cancellationToken)
                .WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            completedIterations++;
        }

        if (completedIterations != OutputWriteIterations)
            throw new TimeoutException("The process probe did not finish writing its output in time.");

        TimeSpan flushRemaining = OutputWriteLimit - writeTime.Elapsed;
        if (flushRemaining <= TimeSpan.Zero)
            throw new TimeoutException("The process probe output flush deadline elapsed.");

        await writer.FlushAsync(cancellationToken)
            .WaitAsync(flushRemaining, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WaitForLeafExitAsync(Process leafProcess, CancellationToken cancellationToken)
    {
        var wait = Stopwatch.StartNew();
        for (int attempt = 0;
             attempt < LifetimeWaitAttempts && wait.Elapsed < LifetimeWaitLimit;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (leafProcess.HasExited)
                return;

            if (attempt > 0 && attempt % ProgressInterval == 0)
                Console.Error.WriteLine($"parent-leaf-wait-attempt={attempt}");

            await DelayWithinLimitAsync(wait, LifetimeWaitLimit, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ReclaimOwnedLeafAsync(
        Process leafProcess,
        ProcessIdentity? launchedIdentity,
        Task? stdoutDrain,
        Task? stderrDrain,
        CancellationTokenSource drainCancellationSource,
        bool terminateLeaf)
    {
        var cleanup = Stopwatch.StartNew();
        try
        {
            if (terminateLeaf &&
                launchedIdentity is ProcessIdentity expected &&
                TryGetIdentity(leafProcess, out ProcessIdentity current) &&
                current == expected &&
                !leafProcess.HasExited)
            {
                leafProcess.Kill(entireProcessTree: false);
                TimeSpan remaining = CleanupWaitLimit - cleanup.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    try
                    {
                        await leafProcess.WaitForExitAsync()
                            .WaitAsync(remaining)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        Console.Error.WriteLine("Timed out waiting for the owned leaf process to exit.");
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the identity check and termination.
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Console.Error.WriteLine($"Unable to terminate the owned leaf process: {exception.Message}");
        }
        finally
        {
            drainCancellationSource.Cancel();
            Task[] drains = [stdoutDrain ?? Task.CompletedTask, stderrDrain ?? Task.CompletedTask];
            TimeSpan remaining = CleanupWaitLimit - cleanup.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.WhenAll(drains).WaitAsync(remaining).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation closes the bounded drain window.
                }
                catch (TimeoutException)
                {
                    Console.Error.WriteLine("Timed out waiting for leaf output drains to stop.");
                }
            }
        }
    }

    private static async Task DelayWithinLimitAsync(
        Stopwatch stopwatch,
        TimeSpan limit,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = limit - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return;

        TimeSpan delay = remaining < PollInterval ? remaining : PollInterval;
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static ProcessIdentity GetCurrentIdentity()
    {
        using Process current = Process.GetCurrentProcess();
        return GetIdentity(current);
    }

    private static ProcessIdentity GetIdentity(Process process)
        => new(process.Id, GraphEvidenceProcessIdentityToken.Create(process));

    private static bool TryGetIdentity(Process process, out ProcessIdentity identity)
    {
        try
        {
            identity = GetIdentity(process);
            return true;
        }
        catch (ArgumentException)
        {
            identity = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            identity = default;
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            identity = default;
            return false;
        }
        catch (IOException)
        {
            identity = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            identity = default;
            return false;
        }
        catch (NotSupportedException)
        {
            identity = default;
            return false;
        }
    }

    private static Task RecordIdentityAsync(
        string stateDirectory,
        string prefix,
        ProcessIdentity identity,
        CancellationToken cancellationToken)
        => Task.WhenAll(
            WriteStateFileAsync(
                stateDirectory,
                $"{prefix}.pid",
                identity.ProcessId.ToString(CultureInfo.InvariantCulture),
                cancellationToken),
            WriteStateFileAsync(
                stateDirectory,
                $"{prefix}.identity-token",
                identity.IdentityToken,
                cancellationToken));

    private static async Task RecordLaunchAsync(
        string stateDirectory,
        string executable,
        IReadOnlyList<string> arguments,
        ProcessIdentity parentIdentity,
        ProcessIdentity leafIdentity,
        CancellationToken cancellationToken)
    {
        string[] lines =
        [
            $"launched-utc-ticks={DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)}",
            $"parent-pid={parentIdentity.ProcessId.ToString(CultureInfo.InvariantCulture)}",
            $"parent-identity-token={parentIdentity.IdentityToken}",
            $"leaf-pid={leafIdentity.ProcessId.ToString(CultureInfo.InvariantCulture)}",
            $"leaf-identity-token={leafIdentity.IdentityToken}",
            $"executable={executable}",
            $"argument-count={arguments.Count.ToString(CultureInfo.InvariantCulture)}",
            .. arguments.Select((argument, index) => $"argument[{index}]={argument}"),
        ];

        await WriteStateFileAsync(
                stateDirectory,
                "leaf.launch",
                string.Join(Environment.NewLine, lines),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryReadIdentity(
        string stateDirectory,
        string prefix,
        out ProcessIdentity identity)
    {
        string pidPath = Path.Combine(stateDirectory, $"{prefix}.pid");
        string identityPath = Path.Combine(stateDirectory, $"{prefix}.identity-token");
        try
        {
            bool parsed = int.TryParse(
                File.ReadAllText(pidPath),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId);
            string identityToken = File.ReadAllText(identityPath);
            parsed = parsed && !string.IsNullOrWhiteSpace(identityToken);
            identity = parsed ? new ProcessIdentity(processId, identityToken) : default;
            return parsed;
        }
        catch (IOException)
        {
            identity = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            identity = default;
            return false;
        }
    }

    private static async Task WriteStateFileAsync(
        string stateDirectory,
        string fileName,
        string contents,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            stateDirectory,
            $".process-probe-{fileName}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.tmp");
        string destinationPath = Path.Combine(stateDirectory, fileName);

        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            string fullTemporaryPath = Path.GetFullPath(temporaryPath);
            if (string.Equals(
                    Path.GetDirectoryName(fullTemporaryPath),
                    stateDirectory,
                    StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullTemporaryPath).StartsWith(".process-probe-", StringComparison.Ordinal) &&
                File.Exists(fullTemporaryPath))
            {
                File.Delete(fullTemporaryPath);
            }
        }
    }

    private readonly record struct ProcessIdentity(int ProcessId, string IdentityToken);
}
