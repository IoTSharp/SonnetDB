using System.Diagnostics;
using System.Net.Http;

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
    private Process? _process;
    private string? _lastError;

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
    {
        dataRoot = NormalizePath(dataRoot);
        url = NormalizeUrl(url);

        if (await IsHealthyAsync(url, cancellationToken).ConfigureAwait(false))
            return await GetStatusAsync(dataRoot, url, cancellationToken).ConfigureAwait(false) with { Error = null };

        lock (_sync)
        {
            if (_process is { HasExited: false })
                return BuildStatus(dataRoot, url, healthy: false, error: null);
        }

        var target = ResolveLaunchTarget();
        if (target is null)
        {
            _lastError = "SonnetDB Server executable was not found. Pass --server-exe to Studio or build src/SonnetDB first.";
            return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
        }

        Directory.CreateDirectory(dataRoot);
        var startInfo = CreateStartInfo(target, dataRoot, url);
        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                _lastError = "Failed to start SonnetDB Server process.";
                return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
            }

            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_sync)
            {
                _process = process;
                _lastError = null;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _lastError = ex.Message;
            return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
        }

        for (var i = 0; i < 40; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsHealthyAsync(url, cancellationToken).ConfigureAwait(false))
                return BuildStatus(dataRoot, url, healthy: true, error: null);

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        _lastError = "SonnetDB Server process started, but /healthz did not become healthy in time.";
        return BuildStatus(dataRoot, url, healthy: false, error: _lastError);
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
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is { HasExited: false })
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(1500))
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已退出，无需处理。
            }
            finally
            {
                process.Dispose();
            }
        }

        return await GetStatusAsync(dataRoot, url, cancellationToken).ConfigureAwait(false);
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
        if (!_keepRunningOnExit)
            await StopAsync(string.Empty, "http://127.0.0.1:5080", CancellationToken.None).ConfigureAwait(false);

        _http.Dispose();
    }

    private ProcessStartInfo CreateStartInfo(StudioServerLaunchTarget target, string dataRoot, string url)
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
            var runningProcess = _process is { HasExited: false } ? _process : null;
            var running = healthy || runningProcess is not null;
            return new StudioManagedServerStatus(
                running,
                runningProcess is not null,
                healthy,
                runningProcess?.Id,
                NormalizeUrl(url),
                NormalizePath(dataRoot),
                error);
        }
    }

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

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
}
