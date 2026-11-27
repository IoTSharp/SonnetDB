using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.Sql.Execution;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Tests;

/// <summary>持有真实外排 run 的独立 Server 被强制终止后，重开数据库时回收运行目录。</summary>
public sealed class StreamingOrderByProcessRecoveryTests(ITestOutputHelper output)
{
    private const string RootPrefix = "sndb-order-recovery-";
    private const string Database = "ordered_recovery";
    private const string Token = "ordered-recovery-admin";
    private const int Rows = 4_096;
    private const int PayloadChars = 2_048;
    private const string SqlPath = "/v1/db/" + Database + "/sql";

    /// <summary>强制终止独立 Server 后核对遗留外排、无归属目录及重开后完整结果。</summary>
    [OrderRecoveryFact]
    public async Task KilledServer_ReopenCleansOwnedRunsAndKeepsDurableRows()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        string root = Path.Combine(Path.GetTempPath(), RootPrefix + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "server");
        string databaseRoot = Path.Combine(dataRoot, Database);
        string spillRoot = Path.Combine(databaseRoot, "sql-spill");
        string controlDirectory = Path.Combine(spillRoot, "query-unowned");
        string keepFile = Path.Combine(controlDirectory, "keep.txt");
        Directory.CreateDirectory(root);

        try
        {
            SeedDatabase(databaseRoot, deadline.Token);
            Directory.CreateDirectory(controlDirectory);
            await File.WriteAllTextAsync(keepFile, "keep", deadline.Token);

            string serverAssembly = ResolveServerAssembly();
            string program = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                ?? (OperatingSystem.IsWindows() ? @"C:\Program Files\dotnet\dotnet.exe" : "dotnet");
            string firstContentRoot = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            using (Process server = StartServer(program, serverAssembly, firstContentRoot, dataRoot, out Uri address))
            {
                Task captureOutput = CaptureAsync(server.StandardOutput, deadline.Token);
                Task captureError = CaptureAsync(server.StandardError, deadline.Token);
                try
                {
                    using var client = CreateClient(address, smallReceiveBuffer: true);
                    await WaitUntilReadyAsync(client, server, deadline.Token);
                    using var request = Query("SELECT id, payload FROM records ORDER BY sort_key");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    response.EnsureSuccessStatusCode();
                    Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-SonnetDB-Sql-Streaming")));
                    using var body = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
                    using var meta = JsonDocument.Parse(Assert.IsType<string>(await body.ReadLineAsync(deadline.Token)));
                    Assert.Equal("meta", meta.RootElement.GetProperty("type").GetString());
                    Assert.StartsWith("[", await body.ReadLineAsync(deadline.Token));
                    Assert.True(HasOwnedWorkspace(spillRoot, requireRun: true), "Streaming output should retain live external sort runs.");

                    StopOwned(server);
                    Assert.NotEqual(0, server.ExitCode);
                    Assert.True(HasOwnedWorkspace(spillRoot, requireRun: true), "Forced process termination should leave recovery input.");
                }
                finally
                {
                    StopOwned(server);
                    await Task.WhenAll(captureOutput, captureError).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }

            string reopenedContentRoot = Directory.CreateDirectory(Path.Combine(root, "reopened")).FullName;
            using Process reopened = StartServer(program, serverAssembly, reopenedContentRoot, dataRoot, out Uri reopenedAddress);
            Task reopenedOutput = CaptureAsync(reopened.StandardOutput, deadline.Token);
            Task reopenedError = CaptureAsync(reopened.StandardError, deadline.Token);
            try
            {
                using var client = CreateClient(reopenedAddress);
                await WaitUntilReadyAsync(client, reopened, deadline.Token);
                Assert.False(HasOwnedWorkspace(spillRoot, requireRun: false));
                Assert.Equal("keep", await File.ReadAllTextAsync(keepFile, deadline.Token));

                using var count = Query("SELECT COUNT(*) AS total FROM records");
                using var response = await client.SendAsync(count, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                using var body = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
                using var meta = JsonDocument.Parse(Assert.IsType<string>(await body.ReadLineAsync(deadline.Token)));
                Assert.Equal("meta", meta.RootElement.GetProperty("type").GetString());
                using var row = JsonDocument.Parse(Assert.IsType<string>(await body.ReadLineAsync(deadline.Token)));
                Assert.Equal(Rows, row.RootElement[0].GetInt64());
                using var end = JsonDocument.Parse(Assert.IsType<string>(await body.ReadLineAsync(deadline.Token)));
                Assert.Equal("end", end.RootElement.GetProperty("type").GetString());
                Assert.Null(await body.ReadLineAsync(deadline.Token));
                output.WriteLine($"Recovered {Rows} durable rows and removed marked spill workspaces.");
            }
            finally
            {
                StopOwned(reopened);
                await Task.WhenAll(reopenedOutput, reopenedError).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            DeleteOwned(root);
        }
    }

    private static void SeedDatabase(string databaseRoot, CancellationToken cancellationToken)
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = databaseRoot });
        SqlExecutor.Execute(database, "CREATE TABLE records (id INT, sort_key INT, payload STRING, PRIMARY KEY (id))");
        var store = database.Tables.Open("records");
        const int batchSize = 256;
        const int seed = 440;
        string payload = new('x', PayloadChars);
        for (int position = 0; position < Rows; position += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new IReadOnlyList<object?>[batchSize];
            for (int offset = 0; offset < batch.Length; offset++)
            {
                int id = position + offset;
                batch[offset] = [(long)id, ((long)id * 31 + seed) % 1_009, payload];
            }
            Assert.Equal(batch.Length, store.InsertMany(batch));
        }
    }

    private Process StartServer(string program, string assembly, string cwd, string dataRoot, out Uri address)
    {
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        }

        string[] arguments =
        [
            assembly,
            "--contentRoot", cwd,
            "--hostBuilder:reloadConfigOnChange=false",
            "--Kestrel:Endpoints:Http:Url=" + address,
            "--SonnetDBServer:DataRoot=" + dataRoot,
            "--SonnetDBServer:Tokens:" + Token + "=admin",
            "--SonnetDBServer:AllowAnonymousProbes=true",
            "--SonnetDBServer:AutoLoadExistingDatabases=true",
            "--SonnetDBServer:SqlExecution:QueryLimitBytes=1048576",
        ];
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        Process process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned SonnetDB Server.");
        output.WriteLine($"Owned PID={process.Id}, parent={Environment.ProcessId}, startedUtc={process.StartTime.ToUniversalTime():O}, program={program}, argv={string.Join(" | ", arguments)}");
        return process;
    }

    private async Task CaptureAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4_096];
        for (int readIndex = 0; readIndex < 128; readIndex++)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
                return;
            if (readIndex < 4)
                output.WriteLine(new string(buffer, 0, count));
        }
        throw new InvalidOperationException("Server log exceeded the 512 KiB capture budget.");
    }

    private static async Task WaitUntilReadyAsync(HttpClient client, Process server, CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(12));
        for (int attempt = 0; attempt < 100; attempt++)
        {
            startup.Token.ThrowIfCancellationRequested();
            Assert.False(server.HasExited, "Server exited before readiness.");
            try
            {
                using var response = await client.GetAsync("/healthz/ready", startup.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(75), startup.Token);
        }
        Assert.Fail("Independent Server did not become ready.");
    }

    private static HttpClient CreateClient(Uri address, bool smallReceiveBuffer = false)
    {
        var handler = new SocketsHttpHandler { MaxResponseDrainSize = 0 };
        if (smallReceiveBuffer)
        {
            handler.ConnectCallback = async (connection, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 1_024 };
                try
                {
                    await socket.ConnectAsync(connection.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        var client = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static HttpRequestMessage Query(string sql) => new(HttpMethod.Post, SqlPath)
    {
        Content = JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest),
    };

    private static bool HasOwnedWorkspace(string spillRoot, bool requireRun)
    {
        const string marker = ".sonnetdb-sql-spill";
        return Directory.EnumerateDirectories(spillRoot, "query-*")
            .Take(16)
            .Any(directory => File.Exists(Path.Combine(directory, marker))
                && (!requireRun || Directory.EnumerateFiles(directory, "*.bin").Any()));
    }

    private static string ResolveServerAssembly()
    {
        var anchor = typeof(SonnetDB.Program).Assembly;
        var framework = anchor.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName;
        string targetFramework = framework.Replace(".NETCoreApp,Version=v", "net", StringComparison.Ordinal);
        var clock = Stopwatch.StartNew();
        DirectoryInfo? parent = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 10 && parent is not null && clock.Elapsed < TimeSpan.FromSeconds(2); depth++, parent = parent.Parent)
        {
            if (!File.Exists(Path.Combine(parent.FullName, "SonnetDB.slnx")))
                continue;

            string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                ? "Release"
                : "Debug";
            string outputRoot = Path.Combine(parent.FullName, "src", "SonnetDB", "bin", configuration, targetFramework);
            string[] candidates =
            [
                Path.Combine(outputRoot, "SonnetDB.dll"),
                Path.Combine(outputRoot, RuntimeInformation.RuntimeIdentifier, "SonnetDB.dll"),
                Path.Combine(AppContext.BaseDirectory, "SonnetDB.dll"),
            ];
            byte[] expected = SHA256.HashData(File.ReadAllBytes(anchor.Location));
            foreach (string path in candidates)
            {
                if (File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json"))
                    && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(expected))
                    return path;
            }

            break;
        }

        throw new FileNotFoundException("Unable to find a runnable Server output matching the assembly under test.");
    }

    private static void StopOwned(Process process)
    {
        if (process.HasExited)
            return;

        // 仅处理本测试 Process.Start 返回的句柄，用 PID 和启动时间重新核对。
        using Process observed = Process.GetProcessById(process.Id);
        Assert.Equal(process.StartTime, observed.StartTime);
        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(10_000), $"Owned Server PID {process.Id} did not exit.");
    }

    private static void DeleteOwned(string root)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        if (!directory.Exists)
            return;

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), directory.Parent!.FullName);
        Assert.StartsWith(RootPrefix, directory.Name);
        Assert.True(Guid.TryParseExact(directory.Name[RootPrefix.Length..], "N", out _));
        Assert.False((directory.Attributes & FileAttributes.ReparsePoint) != 0);
        directory.Delete(recursive: true);
    }

    private sealed class OrderRecoveryFactAttribute : FactAttribute
    {
        public OrderRecoveryFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("SONNETDB_ORDER_BY_RECOVERY"), "1", StringComparison.Ordinal))
                Skip = "Set SONNETDB_ORDER_BY_RECOVERY=1 to run the independent Server crash and reopen test.";
        }
    }
}
