using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Benchmarks.Benchmarks;

internal static class M42SqlPreviewEvidenceRunner
{
    private const string Sql = "SELECT id, payload FROM m42_preview_rows";
    private const int BatchSize = 128;
    private const long PreviewMaxBytes = 16L * 1024 * 1024;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    internal static async Task<string> RunAsync(
        string outputDirectory,
        bool quick,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        int rowCount = quick ? 64 : 20_000;
        int payloadLength = quick ? 64 : 512;
        int previewRows = quick ? 8 : 100;
        int iterations = quick ? 2 : 5;
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        string ownedRoot = Path.Combine(
            Path.GetTempPath(), "sndb-m42-sql-preview-evidence-" + Guid.NewGuid().ToString("N"));
        string databaseRoot = Path.Combine(ownedRoot, "database");
        Directory.CreateDirectory(ownedRoot);
        try
        {
            M42CoreSample[] fullCore = new M42CoreSample[iterations];
            M42CoreSample[] previewCore = new M42CoreSample[iterations];
            using (Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = databaseRoot }))
            {
                CreateDataset(database, rowCount, payloadLength, cancellationToken);
                var statement = SqlParser.Parse(Sql);
                await MeasureCoreAsync(database, statement, rowCount, previewRows,
                    fullCore, previewCore, cancellationToken).ConfigureAwait(false);
            }

            (M42HttpSample[] fullRest, M42HttpSample[] previewRest) =
                await MeasureRestAsync(ownedRoot, databaseRoot, rowCount, previewRows,
                    iterations, cancellationToken).ConfigureAwait(false);

            using Process process = Process.GetCurrentProcess();
            var report = new M42SqlPreviewEvidenceReport(
                "m42-sql-preview-evidence-v1",
                quick ? "quick_smoke" : "local_full",
                startedUtc,
                DateTimeOffset.UtcNow,
                rowCount,
                payloadLength,
                previewRows,
                PreviewMaxBytes,
                iterations,
                Sql,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                process.Id,
                process.StartTime.ToUniversalTime(),
                Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion ?? "unknown",
                fullCore,
                previewCore,
                fullRest,
                previewRest,
                [
                    "Local synthetic comparison only; it is not fixed-hardware or production SLO evidence.",
                    "Core allocation is synchronous-thread allocation. Sampled and immediate post-execution heap deltas are observations, not retained heap or a CLR heap hard bound.",
                    "REST time to first body byte includes SQL execution and current NDJSON buffering; it does not prove row streaming.",
                    "Full and preview samples alternate after one warmup per path. The client and Kestrel share one process.",
                ]);

            Directory.CreateDirectory(outputDirectory);
            string reportPath = Path.GetFullPath(Path.Combine(outputDirectory, "m42-sql-preview-evidence.json"));
            File.WriteAllText(reportPath,
                JsonSerializer.Serialize(report, M42SqlPreviewEvidenceJsonContext.Default.M42SqlPreviewEvidenceReport),
                Utf8WithoutBom);
            File.WriteAllText(Path.Combine(outputDirectory, "m42-sql-preview-evidence.md"),
                BuildMarkdown(report), Utf8WithoutBom);
            return reportPath;
        }
        finally
        {
            DeleteOwnedRoot(ownedRoot);
        }
    }

    private static void CreateDataset(Tsdb database, int rowCount, int payloadLength,
        CancellationToken cancellationToken)
    {
        var options = new SqlExecutionOptions { CancellationToken = cancellationToken };
        SqlExecutor.Execute(database, null,
            "CREATE TABLE m42_preview_rows (id INT, payload STRING, PRIMARY KEY (id))",
            null, null, options);
        string payload = new('x', payloadLength);
        for (int start = 1; start <= rowCount; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(rowCount, start + BatchSize - 1);
            var sql = new StringBuilder(BatchSize * (payloadLength + 32));
            sql.Append("INSERT INTO m42_preview_rows (id, payload) VALUES ");
            for (int id = start; id <= end; id++)
            {
                if (id > start)
                    sql.Append(',');
                sql.Append('(').Append(id).Append(",'").Append(payload).Append("')");
            }
            SqlExecutor.Execute(database, null, sql.ToString(), null, null, options);
            if (end == rowCount || end % (BatchSize * 16) == 0)
                Console.WriteLine($"m42-sql-preview dataset={end}/{rowCount}");
        }
    }

    private static async Task MeasureCoreAsync(
        Tsdb database,
        SqlStatement statement,
        int rowCount,
        int previewRows,
        M42CoreSample[] full,
        M42CoreSample[] preview,
        CancellationToken cancellationToken)
    {
        var fullOptions = new SqlExecutionOptions { CancellationToken = cancellationToken };
        var previewOptions = fullOptions with
        {
            PreviewMaxRows = previewRows,
            PreviewMaxBytes = PreviewMaxBytes,
        };
        ValidateCore(ExecuteCore(database, statement, fullOptions), rowCount, false);
        ValidateCore(ExecuteCore(database, statement, previewOptions), previewRows, true);
        for (int index = 0; index < full.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 2 == 0)
            {
                full[index] = await MeasureCoreOneAsync(database, statement, fullOptions,
                    rowCount, false, cancellationToken).ConfigureAwait(false);
                preview[index] = await MeasureCoreOneAsync(database, statement, previewOptions,
                    previewRows, true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                preview[index] = await MeasureCoreOneAsync(database, statement, previewOptions,
                    previewRows, true, cancellationToken).ConfigureAwait(false);
                full[index] = await MeasureCoreOneAsync(database, statement, fullOptions,
                    rowCount, false, cancellationToken).ConfigureAwait(false);
            }
            Console.WriteLine($"m42-sql-preview core={index + 1}/{full.Length}");
        }
    }

    private static async Task<M42CoreSample> MeasureCoreOneAsync(
        Tsdb database,
        SqlStatement statement,
        SqlExecutionOptions options,
        int expectedRows,
        bool expectedTruncated,
        CancellationToken cancellationToken)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long heapBefore = GC.GetTotalMemory(false);
        long sampledPeak = heapBefore;
        using var stopSampling = new CancellationTokenSource();
        using var samplingStarted = new ManualResetEventSlim(false);
        Task sampler = Task.Run(() =>
        {
            samplingStarted.Set();
            for (int poll = 0; poll < 150_000 && !stopSampling.IsCancellationRequested; poll++)
            {
                sampledPeak = Math.Max(sampledPeak, GC.GetTotalMemory(false));
                stopSampling.Token.WaitHandle.WaitOne(2);
            }
        });
        long allocatedBefore = 0;
        long allocated = 0;
        var stopwatch = new Stopwatch();
        SelectExecutionResult result;
        try
        {
            if (!samplingStarted.Wait(TimeSpan.FromSeconds(2), cancellationToken))
                throw new TimeoutException("M42 heap sampler did not start within two seconds.");
            allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            stopwatch.Start();
            result = ExecuteCore(database, statement, options);
            stopwatch.Stop();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }
        finally
        {
            stopSampling.Cancel();
            await sampler.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        long heapAfter = GC.GetTotalMemory(false);
        ValidateCore(result, expectedRows, expectedTruncated);
        return new M42CoreSample(
            stopwatch.Elapsed.TotalMilliseconds,
            allocated,
            Math.Max(0, Math.Max(sampledPeak, heapAfter) - heapBefore),
            heapAfter - heapBefore,
            result.Rows.Count,
            result.Truncated);
    }

    private static SelectExecutionResult ExecuteCore(Tsdb database, SqlStatement statement,
        SqlExecutionOptions options)
        => SqlExecutor.ExecuteStatement(database, "m42_evidence", statement, null, null, options)
            as SelectExecutionResult
            ?? throw new InvalidDataException("M42 Core query did not return SELECT rows.");

    private static void ValidateCore(SelectExecutionResult result, int expectedRows,
        bool expectedTruncated)
    {
        if (result.Rows.Count != expectedRows || result.Truncated != expectedTruncated
            || result.Rows.Count == 0 || result.Rows[0][0] is not long firstId || firstId != 1)
        {
            throw new InvalidDataException("M42 Core result count, prefix, or truncation marker differs from the fixture.");
        }
    }

    private static async Task<(M42HttpSample[] Full, M42HttpSample[] Preview)> MeasureRestAsync(
        string ownedRoot,
        string databaseRoot,
        int rowCount,
        int previewRows,
        int iterations,
        CancellationToken cancellationToken)
    {
        string contentRoot = Path.Combine(ownedRoot, "content");
        Directory.CreateDirectory(contentRoot);
        string[] args =
        [
            "--contentRoot", contentRoot,
            "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0",
            "--SonnetDBServer:DataRoot=" + Path.Combine(ownedRoot, "server"),
            "--SonnetDBServer:MountedDatabasePath=" + databaseRoot,
            "--SonnetDBServer:MountedDatabaseName=m42_evidence",
            "--SonnetDBServer:SqlExecution:MaxResultRows=" + previewRows,
            "--SonnetDBServer:SqlExecution:MaxPreviewBytes=" + PreviewMaxBytes,
            "--SonnetDBServer:Tokens:m42-evidence=admin",
        ];
        await using WebApplication app = SonnetDB.Program.BuildApp(args);
        bool started = false;
        try
        {
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(30));
                await app.StartAsync(startup.Token).ConfigureAwait(false);
                started = true;
            }
            string address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("M42 Kestrel did not publish a bound address.");
            using var client = new HttpClient
            {
                BaseAddress = new Uri(address),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "m42-evidence");
            await MeasureHttpOneAsync(client, rowCount, null, cancellationToken).ConfigureAwait(false);
            await MeasureHttpOneAsync(client, previewRows, previewRows, cancellationToken)
                .ConfigureAwait(false);
            var full = new M42HttpSample[iterations];
            var preview = new M42HttpSample[iterations];
            for (int index = 0; index < iterations; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index % 2 == 0)
                {
                    full[index] = await MeasureHttpOneAsync(client, rowCount, null,
                        cancellationToken).ConfigureAwait(false);
                    preview[index] = await MeasureHttpOneAsync(client, previewRows, previewRows,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    preview[index] = await MeasureHttpOneAsync(client, previewRows, previewRows,
                        cancellationToken).ConfigureAwait(false);
                    full[index] = await MeasureHttpOneAsync(client, rowCount, null,
                        cancellationToken).ConfigureAwait(false);
                }
                Console.WriteLine($"m42-sql-preview rest={index + 1}/{iterations}");
            }
            return (full, preview);
        }
        finally
        {
            if (started)
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await app.StopAsync(shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<M42HttpSample> MeasureHttpOneAsync(HttpClient client,
        int expectedRows, int? previewRows, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/db/m42_evidence/sql")
        {
            Content = JsonContent.Create(new SqlRequest(Sql) { PreviewMaxRows = previewRows },
                ServerJsonContext.Default.SqlRequest),
        };
        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(requestTimeout.Token)
            .ConfigureAwait(false);
        byte[] buffer = new byte[64 * 1024];
        byte[] lastLine = new byte[4096];
        int lineLength = 0;
        int lastLineLength = 0;
        long bytes = 0;
        long lines = 0;
        int read = await body.ReadAsync(buffer, requestTimeout.Token).ConfigureAwait(false);
        if (read == 0)
            throw new InvalidDataException("M42 REST response had no body.");
        double firstByteMs = stopwatch.Elapsed.TotalMilliseconds;
        int maxChunks = Math.Max(4096, expectedRows * 2);
        long maxBodyBytes = 8192L + expectedRows * 4096L;
        for (int chunk = 0; chunk < maxChunks && read > 0; chunk++)
        {
            bytes += read;
            if (bytes > maxBodyBytes)
                throw new InvalidDataException("M42 REST body exceeded the evidence reader limit.");
            for (int index = 0; index < read; index++)
            {
                byte value = buffer[index];
                if (value == (byte)'\n')
                {
                    lastLineLength = lineLength;
                    lineLength = 0;
                    lines++;
                }
                else
                {
                    if (lineLength == lastLine.Length)
                        throw new InvalidDataException("M42 REST NDJSON line exceeded the evidence reader limit.");
                    lastLine[lineLength++] = value;
                }
            }
            read = await body.ReadAsync(buffer, requestTimeout.Token).ConfigureAwait(false);
        }
        stopwatch.Stop();
        if (read != 0 || lineLength != 0 || lines != expectedRows + 2)
            throw new InvalidDataException("M42 REST row count or terminal newline differs from the fixture.");
        using JsonDocument end = JsonDocument.Parse(lastLine.AsMemory(0, lastLineLength));
        if (end.RootElement.GetProperty("type").GetString() != "end"
            || end.RootElement.GetProperty("rowCount").GetInt64() != expectedRows
            || end.RootElement.GetProperty("truncated").GetBoolean() != (previewRows is not null))
        {
            throw new InvalidDataException("M42 REST end marker differs from the fixture.");
        }
        return new M42HttpSample(firstByteMs, stopwatch.Elapsed.TotalMilliseconds,
            bytes, expectedRows, previewRows is not null);
    }

    private static string BuildMarkdown(M42SqlPreviewEvidenceReport report)
    {
        static double P50(IEnumerable<double> values)
        {
            double[] ordered = values.Order().ToArray();
            return ordered[(ordered.Length - 1) / 2];
        }

        var output = new StringBuilder();
        output.AppendLine("# M42 SQL preview local evidence")
            .AppendLine()
            .AppendLine($"Mode: `{report.Mode}`; rows: {report.RowCount}; payload characters: {report.PayloadLength}; preview rows: {report.PreviewRows}; iterations: {report.Iterations}.")
            .AppendLine($"Started UTC: {report.StartedUtc:O}; runtime: {report.Framework}; OS: {report.Os}; process: {report.ProcessId}.")
            .AppendLine()
            .AppendLine("| Path | Core P50 ms | Core P50 allocated bytes | Core P50 sampled heap delta bytes | REST P50 first body byte ms | REST P50 complete ms |")
            .AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        AppendRow("Full", report.FullCore, report.FullRest);
        AppendRow("Preview", report.PreviewCore, report.PreviewRest);
        output.AppendLine()
            .AppendLine("The JSON artifact contains every sample, response byte count, data shape, process identity, and environment details.")
            .AppendLine();
        foreach (string caveat in report.Caveats)
            output.Append("- ").AppendLine(caveat);
        return output.ToString();

        void AppendRow(string name, M42CoreSample[] core, M42HttpSample[] rest)
        {
            output.Append("| ").Append(name)
                .Append(" | ").Append(P50(core.Select(static value => value.ElapsedMs)).ToString("F3", CultureInfo.InvariantCulture))
                .Append(" | ").Append(P50(core.Select(static value => (double)value.AllocatedBytes)).ToString("F0", CultureInfo.InvariantCulture))
                .Append(" | ").Append(P50(core.Select(static value => (double)value.SampledPeakHeapDeltaBytes)).ToString("F0", CultureInfo.InvariantCulture))
                .Append(" | ").Append(P50(rest.Select(static value => value.FirstBodyByteMs)).ToString("F3", CultureInfo.InvariantCulture))
                .Append(" | ").Append(P50(rest.Select(static value => value.CompleteMs)).ToString("F3", CultureInfo.InvariantCulture))
                .AppendLine(" |");
        }
    }

    private static void DeleteOwnedRoot(string ownedRoot)
    {
        string target = Path.GetFullPath(ownedRoot);
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(target), tempRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !Path.GetFileName(target).StartsWith("sndb-m42-sql-preview-evidence-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("M42 evidence cleanup target is outside its owned temp root.");
        }
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

internal sealed record M42SqlPreviewEvidenceReport(
    string Schema,
    string Mode,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    int RowCount,
    int PayloadLength,
    int PreviewRows,
    long PreviewMaxBytes,
    int Iterations,
    string Sql,
    string Framework,
    string Os,
    string Architecture,
    int ProcessorCount,
    int ProcessId,
    DateTime ProcessStartedUtc,
    string BuildVersion,
    M42CoreSample[] FullCore,
    M42CoreSample[] PreviewCore,
    M42HttpSample[] FullRest,
    M42HttpSample[] PreviewRest,
    string[] Caveats);

internal sealed record M42CoreSample(
    double ElapsedMs,
    long AllocatedBytes,
    long SampledPeakHeapDeltaBytes,
    long PostExecutionHeapDeltaBytes,
    int ReturnedRows,
    bool Truncated);

internal sealed record M42HttpSample(
    double FirstBodyByteMs,
    double CompleteMs,
    long BodyBytes,
    int ReturnedRows,
    bool Truncated);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(M42SqlPreviewEvidenceReport))]
internal sealed partial class M42SqlPreviewEvidenceJsonContext : JsonSerializerContext;
