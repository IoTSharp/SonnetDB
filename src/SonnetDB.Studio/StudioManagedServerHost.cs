using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace SonnetDB.Studio;

/// <summary>
/// 管理 Studio 启动的本地 SonnetDB Server 子进程。
/// </summary>
internal sealed class StudioManagedServerHost : IAsyncDisposable
{
    private readonly string? _configuredServerExecutable;
    private readonly bool _keepRunningOnExit;
    private readonly HttpClient _http;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Process? _process;
    private string? _managedDataRoot;
    private string? _managedUrl;
    private string? _managedMountedDatabasePath;
    private string? _managedMountedDatabaseName;
    private string? _lastError;
    private string _lastDataRoot = string.Empty;
    private string _lastUrl = "http://127.0.0.1:5080";
    private string? _logPath;
    private readonly StringBuilder _stderrTail = new();

    /// <summary>
    /// 创建本地托管 server 控制器。
    /// </summary>
    /// <param name="configuredServerExecutable">显式指定的 SonnetDB Server 可执行文件。</param>
    /// <param name="keepRunningOnExit">Studio 退出后是否保留子进程。</param>
    public StudioManagedServerHost(string? configuredServerExecutable, bool keepRunningOnExit)
    {
        _configuredServerExecutable = configuredServerExecutable;
        _keepRunningOnExit = keepRunningOnExit;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    /// <summary>
    /// 启动本地托管 server；若目标 URL 已健康，则视为外部已有实例。
    /// </summary>
    /// <param name="dataRoot">数据库根目录。</param>
    /// <param name="url">HTTP 监听地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<StudioManagedServerStatus> StartAsync(
        string dataRoot,
        string url,
        CancellationToken cancellationToken)
        => await StartCoreAsync(dataRoot, url, null, null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 使用独立控制目录挂载并打开一个已有嵌入式数据库。
    /// </summary>
    /// <param name="databasePath">已有嵌入式数据库目录。</param>
    /// <param name="controlRoot">Studio 的 Server 控制面目录。</param>
    /// <param name="url">本地 Server 地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<StudioManagedServerStatus> StartEmbeddedAsync(
        string databasePath,
        string controlRoot,
        string url,
        CancellationToken cancellationToken)
    {
        var path = NormalizePath(databasePath);
        if (!Directory.Exists(path)
            || (!File.Exists(Path.Combine(path, "catalog.SDBCAT"))
                && !File.Exists(Path.Combine(path, "wal", "active.SDBWAL"))))
        {
            var status = await GetStatusAsync(controlRoot, url, cancellationToken).ConfigureAwait(false);
            return status with { Error = "所选目录不是已有的 SonnetDB 嵌入式数据库。" };
        }

        var relativeControlRoot = Path.GetRelativePath(path, NormalizePath(controlRoot));
        if (relativeControlRoot is "." || (!Path.IsPathRooted(relativeControlRoot)
            && relativeControlRoot != ".."
            && !relativeControlRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var status = await GetStatusAsync(controlRoot, url, cancellationToken).ConfigureAwait(false);
            return status with { Error = "Studio 控制目录不能位于所选数据库目录内。" };
        }

        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var name = leaf.Length is > 0 and <= 64 && leaf.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
            ? leaf
            : "embedded";
        StudioServerTarget? previous;
        lock (_sync)
        {
            previous = _process is { HasExited: false } && _managedDataRoot is not null && _managedUrl is not null
                ? new StudioServerTarget(_managedDataRoot, _managedUrl, _managedMountedDatabasePath, _managedMountedDatabaseName)
                : null;
        }

        var opened = await StartCoreAsync(controlRoot, url, path, name, cancellationToken).ConfigureAwait(false);
        if (opened.Healthy && opened.StartedByStudio
            && string.Equals(opened.MountedDatabasePath, path, StringComparison.OrdinalIgnoreCase))
            return opened;

        if (previous is not null)
        {
            bool previousStillRunning;
            lock (_sync)
                previousStillRunning = IsManagedProcessFor(previous.DataRoot, previous.Url);
            if (!previousStillRunning)
            {
                var restored = await StartCoreAsync(previous.DataRoot, previous.Url,
                    previous.MountedDatabasePath, previous.MountedDatabaseName, cancellationToken).ConfigureAwait(false);
                return restored with
                {
                    Error = restored.Healthy
                        ? opened.Error ?? "所选嵌入式数据库未能打开；已恢复先前的本地 Server。"
                        : $"{opened.Error ?? "所选数据库未能打开"}；恢复先前的本地 Server 也失败：{restored.Error}",
                };
            }
        }
        return opened with { Error = opened.Error ?? "所选嵌入式数据库未能由 Studio 本地 Server 打开。" };
    }

    private async Task<StudioManagedServerStatus> StartCoreAsync(
        string dataRoot,
        string url,
        string? mountedDatabasePath,
        string? mountedDatabaseName,
        CancellationToken cancellationToken)
    {
        dataRoot = NormalizePath(dataRoot);
        url = NormalizeUrl(url);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _lastDataRoot = dataRoot;
                _lastUrl = url;
            }

            bool replaceAtSameUrl;
            lock (_sync)
            {
                replaceAtSameUrl = _process is { HasExited: false }
                    && string.Equals(_managedUrl, url, StringComparison.OrdinalIgnoreCase)
                    && (!IsManagedProcessFor(dataRoot, url)
                        || !string.Equals(_managedMountedDatabasePath, mountedDatabasePath, StringComparison.OrdinalIgnoreCase));
            }
            if (replaceAtSameUrl)
                StopProcess();

            if (await IsHealthyAsync(url, cancellationToken).ConfigureAwait(false))
                return await GetStatusAsync(dataRoot, url, cancellationToken).ConfigureAwait(false) with { Error = null };

            bool targetChanged;
            lock (_sync)
            {
                targetChanged = _process is { HasExited: false } && !IsManagedProcessFor(dataRoot, url);
            }

            // A healthy external target was returned above and must not affect the
            // existing managed process. For an unhealthy new target, however, the
            // single managed-process slot must be released before launching it.
            if (targetChanged)
                StopProcess();

            bool exitedProcess;
            lock (_sync)
                exitedProcess = _process is { HasExited: true };
            if (exitedProcess)
                StopProcess();

            lock (_sync)
            {
                if (_process is { HasExited: false })
                    return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
            }

            var target = ResolveLaunchTarget();
            if (target is null)
            {
                SetError("SonnetDB Server executable was not found. Pass --server-exe to Studio or build src/SonnetDB first.");
                return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
            }

            Directory.CreateDirectory(dataRoot);
            ConfigureLogPath(dataRoot);
            var startInfo = CreateStartInfo(target, dataRoot, url, mountedDatabasePath, mountedDatabaseName);
            try
            {
                var process = Process.Start(startInfo);
                if (process is null)
                {
                    SetError("Failed to start SonnetDB Server process.");
                    return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
                }

                process.EnableRaisingEvents = true;
                process.OutputDataReceived += (_, args) => AppendProcessLog("stdout", args.Data);
                process.ErrorDataReceived += (_, args) => AppendProcessLog("stderr", args.Data);
                process.Exited += (_, _) =>
                {
                    lock (_sync)
                    {
                        // A replaced process may deliver Exited after a new target
                        // has been installed; its failure must not overwrite the
                        // current host state.
                        if (!ReferenceEquals(_process, process))
                            return;
                    }
                    if (process.ExitCode != 0)
                        SetError($"SonnetDB Server exited before becoming healthy (exit code {process.ExitCode}).");
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                lock (_sync)
                {
                    _process = process;
                    _managedDataRoot = dataRoot;
                    _managedUrl = url;
                    _managedMountedDatabasePath = mountedDatabasePath;
                    _managedMountedDatabaseName = mountedDatabaseName;
                    _lastError = null;
                    _stderrTail.Clear();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                SetError(ex.Message);
                return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
            }

            for (var i = 0; i < 40; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsHealthyAsync(url, cancellationToken).ConfigureAwait(false))
                    return BuildStatus(dataRoot, url, healthy: true, error: null);

                lock (_sync)
                {
                    if (_process is { HasExited: true })
                    {
                        SetError(_lastError ?? "SonnetDB Server exited before becoming healthy.");
                        return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
                    }
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            SetError("SonnetDB Server process started, but /healthz did not become healthy in time.");
            StopProcess();
            return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopProcess();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 停止 Studio 启动的本地 server；外部已有实例不会被停止。
    /// </summary>
    /// <param name="dataRoot">数据库根目录。</param>
    /// <param name="url">HTTP 监听地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<StudioManagedServerStatus> StopAsync(
        string dataRoot,
        string url,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            dataRoot = NormalizePath(dataRoot);
            url = NormalizeUrl(url);
            lock (_sync)
            {
                if (dataRoot.Length > 0)
                    _lastDataRoot = dataRoot;
                _lastUrl = url;
            }
            StopProcess(dataRoot, url);
            return await GetStatusAsync(dataRoot, url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 查询当前本地 server 状态。
    /// </summary>
    /// <param name="dataRoot">数据库根目录。</param>
    /// <param name="url">HTTP 监听地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<StudioManagedServerStatus> GetStatusAsync(
        string dataRoot,
        string url,
        CancellationToken cancellationToken)
    {
        dataRoot = NormalizePath(dataRoot);
        url = NormalizeUrl(url);
        var healthy = await IsHealthyAsync(url, cancellationToken).ConfigureAwait(false);
        return BuildStatus(dataRoot, url, healthy, healthy ? null : _lastError);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_keepRunningOnExit)
            {
                await _lifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    StopProcess();
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
        }
        finally
        {
            _http.Dispose();
            _lifecycleGate.Dispose();
        }
    }

    private ProcessStartInfo CreateStartInfo(
        StudioServerLaunchTarget target, string dataRoot, string url,
        string? mountedDatabasePath, string? mountedDatabaseName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = target.WorkingDirectory,
        };

        foreach (var argument in target.Arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["SONNETDB_Kestrel__Endpoints__Http__Url"] = url;
        var frameUrl = TryBuildFrameUrl(url);
        if (frameUrl is not null)
            startInfo.Environment["SONNETDB_Kestrel__Endpoints__FrameH2__Url"] = frameUrl;
        startInfo.Environment["SONNETDB_SonnetDBServer__DataRoot"] = dataRoot;
        if (mountedDatabasePath is not null)
        {
            startInfo.Environment["SONNETDB_SonnetDBServer__MountedDatabasePath"] = mountedDatabasePath;
            startInfo.Environment["SONNETDB_SonnetDBServer__MountedDatabaseName"] = mountedDatabaseName;
            startInfo.Environment["SONNETDB_SonnetDBServer__AutoLoadExistingDatabases"] = "false";
        }
        startInfo.Environment["SONNETDB_SonnetDBServer__Mqtt__Enabled"] = "false";
        startInfo.Environment["SONNETDB_SonnetDBServer__Coap__Enabled"] = "false";
        startInfo.Environment["SONNETDB_SonnetDBServer__LineProtocolUdp__Enabled"] = "false";
        return startInfo;
    }

    private StudioServerLaunchTarget? ResolveLaunchTarget()
    {
        if (!string.IsNullOrWhiteSpace(_configuredServerExecutable))
            return CreateTargetFromPath(Path.GetFullPath(_configuredServerExecutable));

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in EnumerateServerCandidates(baseDir))
        {
            var target = CreateTargetFromPath(candidate);
            if (target is not null)
                return target;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateServerCandidates(string baseDir)
    {
        yield return Path.Combine(baseDir, "SonnetDB.exe");
        yield return Path.Combine(baseDir, "SonnetDB.dll");
        yield return Path.Combine(baseDir, "server", "SonnetDB.exe");
        yield return Path.Combine(baseDir, "server", "SonnetDB.dll");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "SonnetDB", "bin", "Debug", "net10.0", "SonnetDB.exe"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "SonnetDB", "bin", "Release", "net10.0", "SonnetDB.exe"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "SonnetDB", "SonnetDB.csproj"));
    }

    private static StudioServerLaunchTarget? CreateTargetFromPath(string path)
    {
        if (!File.Exists(path))
            return null;

        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return new StudioServerLaunchTarget(
                "dotnet",
                ["run", "--project", path, "--no-build", "--no-launch-profile"],
                directory);
        }

        if (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
            return new StudioServerLaunchTarget("dotnet", [path], directory);

        return new StudioServerLaunchTarget(path, [], directory);
    }

    private async Task<bool> IsHealthyAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(NormalizeUrl(url) + "/healthz", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private StudioManagedServerStatus BuildStatus(string dataRoot, string url, bool healthy, string? error)
    {
        lock (_sync)
        {
            var runningProcess = IsManagedProcessFor(dataRoot, url) ? _process : null;
            var running = healthy || runningProcess is not null;
            return new StudioManagedServerStatus(
                running,
                runningProcess is not null,
                healthy,
                runningProcess?.Id,
                NormalizeUrl(url),
                NormalizePath(dataRoot),
                error,
                runningProcess is null ? null : _managedMountedDatabasePath,
                runningProcess is null ? null : _managedMountedDatabaseName);
        }
    }

    private void StopProcess(string? dataRoot = null, string? url = null)
    {
        Process? process;
        lock (_sync)
        {
            if (dataRoot is not null && url is not null && !IsManagedProcessFor(dataRoot, url))
                return;

            process = _process;
            _process = null;
            _managedDataRoot = null;
            _managedUrl = null;
            _managedMountedDatabasePath = null;
            _managedMountedDatabaseName = null;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 进程在停止窗口内已退出。
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ConfigureLogPath(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            return;

        try
        {
            var logDirectory = Path.Combine(dataRoot, ".studio");
            Directory.CreateDirectory(logDirectory);
            _logPath = Path.Combine(logDirectory, "managed-server.log");
            File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O} [studio] starting managed server{Environment.NewLine}");
        }
        catch (IOException)
        {
            _logPath = null;
        }
        catch (UnauthorizedAccessException)
        {
            _logPath = null;
        }
    }

    private void AppendProcessLog(string stream, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_sync)
        {
            if (stream == "stderr")
            {
                if (_stderrTail.Length > 2048)
                    _stderrTail.Clear();
                _stderrTail.AppendLine(line);
            }

            if (_logPath is null)
                return;

            try
            {
                File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O} [{stream}] {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
                _logPath = null;
            }
            catch (UnauthorizedAccessException)
            {
                _logPath = null;
            }
        }
    }

    private void SetError(string message)
    {
        lock (_sync)
        {
            _lastError = _stderrTail.Length == 0
                ? message
                : $"{message} stderr: {_stderrTail.ToString().Trim()}";
        }
    }

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private bool IsManagedProcessFor(string dataRoot, string url)
        => _process is { HasExited: false }
            && string.Equals(_managedDataRoot, NormalizePath(dataRoot), StringComparison.OrdinalIgnoreCase)
            && string.Equals(_managedUrl, NormalizeUrl(url), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUrl(string url)
        => string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:5080" : url.Trim().TrimEnd('/');

    private static string? TryBuildFrameUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Port <= 0)
            return null;

        var builder = new UriBuilder(uri)
        {
            Port = uri.Port + 1,
            Path = string.Empty,
            Query = string.Empty,
        };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private sealed record StudioServerLaunchTarget(string FileName, string[] Arguments, string WorkingDirectory);
    private sealed record StudioServerTarget(string DataRoot, string Url, string? MountedDatabasePath, string? MountedDatabaseName);
}
