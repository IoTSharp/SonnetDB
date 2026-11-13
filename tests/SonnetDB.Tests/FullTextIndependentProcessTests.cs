using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Tests;

/// <summary>实际 Server 和源码候选消费者分别作为独立进程运行，不复用进程内测试宿主。</summary>
public sealed class FullTextIndependentProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task IndependentServerAndCandidateConsumerCommunicateAndRecoverFromEveryBudget()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-fulltext-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string repository = FindRepository();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).FullName.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "Release" : "Debug";
        string probe = Path.Combine(repository, "tests", "SonnetDB.FullTextProcessProbe", "bin", configuration, "net10.0", "SonnetDB.FullTextProcessProbe.dll");
        string server = ResolveServerAssembly(repository, configuration);
        string dotnet = OperatingSystem.IsWindows() ? @"C:\Program Files\dotnet\dotnet.exe" : "dotnet";
        string dataRoot = Path.Combine(root, "server");
        string embeddedRoot = Path.Combine(root, "embedded");
        string report = Path.Combine(root, "consumer.json");
        Process? serverProcess = null;
        Task<string>? serverOutput = null;
        Task<string>? serverError = null;
        try
        {
            Assert.True(File.Exists(probe), $"Missing source consumer assembly: {probe}");
            await RunAsync(dotnet, [probe, "seed", Path.Combine(dataRoot, "fulltextprobe"), embeddedRoot], root, deadline.Token);
            int port;
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            string address = $"http://127.0.0.1:{port}";
            string contentRoot = Path.Combine(root, "content");
            Directory.CreateDirectory(contentRoot);
            string[] arguments = [server, "--contentRoot", contentRoot, "--hostBuilder:reloadConfigOnChange=false",
                "--Kestrel:Endpoints:Http:Url=" + address, "--SonnetDBServer:DataRoot=" + dataRoot,
                "--SonnetDBServer:Tokens:fulltext-process-token=admin", "--SonnetDBServer:AllowAnonymousProbes=true"];
            serverProcess = Start(dotnet, arguments, root);
            serverOutput = CaptureAsync(serverProcess.StandardOutput, deadline.Token);
            serverError = CaptureAsync(serverProcess.StandardError, deadline.Token);
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(1) };
            bool ready = false;
            for (int attempt = 0; attempt < 100 && !ready; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.False(serverProcess.HasExited, "Independent Server exited before readiness.");
                try
                {
                    using HttpResponseMessage response = await http.GetAsync("/healthz/ready", deadline.Token);
                    ready = response.IsSuccessStatusCode;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
                if (!ready)
                    await Task.Delay(50, deadline.Token);
            }
            Assert.True(ready, "Independent Server did not become ready within the bounded startup window.");
            await RunAsync(dotnet, [probe, "consume", embeddedRoot, address, report], root, deadline.Token);
            using JsonDocument result = JsonDocument.Parse(await File.ReadAllBytesAsync(report, deadline.Token));
            Assert.Equal(10_003, result.RootElement.GetProperty("CorpusCount").GetInt32());
            Assert.Equal(9, result.RootElement.GetProperty("ComparedResponses").GetInt32());
            Assert.Equal(14, result.RootElement.GetProperty("BudgetFailures").GetInt32());
            Assert.True(result.RootElement.GetProperty("RecoveryPassed").GetBoolean());
            Assert.NotEqual(Environment.ProcessId, result.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.NotEqual(serverProcess.Id, result.RootElement.GetProperty("ProcessId").GetInt32());
            output.WriteLine(result.RootElement.GetRawText());
        }
        finally
        {
            try
            {
                StopOwned(serverProcess);
            }
            finally
            {
                try
                {
                    if (serverOutput is not null)
                        output.WriteLine(await serverOutput);
                    if (serverError is not null)
                        output.WriteLine(await serverError);
                }
                finally
                {
                    serverProcess?.Dispose();
                    DeleteOwned(root);
                }
            }
        }
    }

    private Process Start(string program, IReadOnlyList<string> args, string cwd)
    {
        var info = new ProcessStartInfo(program)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        foreach (string argument in args)
            info.ArgumentList.Add(argument);
        Process process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start owned process.");
        output.WriteLine($"Owned PID={process.Id}; parent={Environment.ProcessId}; startedUtc={process.StartTime.ToUniversalTime():O}; program={program}; argv={string.Join(" | ", args)}");
        return process;
    }

    private async Task RunAsync(string program, IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
    {
        using Process process = Start(program, args, cwd);
        Task<string> stdout = CaptureAsync(process.StandardOutput, cancellationToken);
        Task<string> stderr = CaptureAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            output.WriteLine(await stdout);
            output.WriteLine(await stderr);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            StopOwned(process);
        }
    }

    private static async Task<string> CaptureAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4_096];
        var result = new StringBuilder();
        for (int readIndex = 0; readIndex < 256; readIndex++)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return result.ToString();
            result.Append(buffer, 0, read);
        }
        throw new InvalidOperationException("Owned process exceeded the 1 MiB captured log budget.");
    }

    private static void StopOwned(Process? process)
    {
        if (process is null || process.HasExited)
            return;
        // 此 Process 对象仅来自本测试的直接 Process.Start；记录的身份不按名称重新发现。
        int pid = process.Id;
        DateTime started = process.StartTime;
        using Process observed = Process.GetProcessById(pid);
        if (observed.StartTime != started)
            throw new InvalidOperationException("Owned PID identity changed; refusing termination.");
        process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(10_000))
            throw new TimeoutException("Owned process tree did not exit after termination.");
    }

    private static string FindRepository()
    {
        var clock = Stopwatch.StartNew();
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int ancestor = 0; ancestor < 10 && directory is not null && clock.Elapsed < TimeSpan.FromSeconds(2); ancestor++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SonnetDB.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found within ten ancestors/two seconds.");
    }

    private static string ResolveServerAssembly(string repository, string configuration)
    {
        string outputRoot = Path.Combine(repository, "src", "SonnetDB", "bin", configuration, "net10.0");
        string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SonnetDB.Program).Assembly.Location)));
        string[] candidates = [Path.Combine(outputRoot, "SonnetDB.dll"),
            Path.Combine(outputRoot, RuntimeInformation.RuntimeIdentifier, "SonnetDB.dll"),
            Path.Combine(AppContext.BaseDirectory, "SonnetDB.dll")];
        foreach (string path in candidates)
            if (File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json"))
                && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == expected)
                return path;
        throw new FileNotFoundException("No runnable Server output matches this test's referenced source assembly.");
    }

    private static void DeleteOwned(string root)
    {
        string full = Path.GetFullPath(root);
        var directory = new DirectoryInfo(full);
        string prefix = "sonnetdb-fulltext-process-";
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(directory.Parent?.FullName, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), comparison)
            || !directory.Name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(directory.Name[prefix.Length..], "N", out _)
            || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing cleanup outside this test's owned temporary directory.");
        if (directory.Exists)
            directory.Delete(recursive: true);
    }
}
