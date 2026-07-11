using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Backup;
using SonnetDB.Documents;
using SonnetDB.Engine;

namespace SonnetDB.DocumentSoak;

internal static class Program
{
    private const string CollectionName = "capacity_documents";
    private const string SiteIndexName = "idx_site";
    private const int CrashWriteCount = 32;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--crash-worker", StringComparison.Ordinal))
            return RunCrashWorker(args);
        if (args.Length > 0 && string.Equals(args[0], "--verify-worker", StringComparison.Ordinal))
            return RunVerifyWorker(args);

        var options = SoakOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);
        Directory.CreateDirectory(options.DataRoot);
        var startedAt = DateTimeOffset.UtcNow;
        var phases = new List<SoakPhase>();
        var memory = new List<MemorySample>();
        bool succeeded = false;
        string? failure = null;

        try
        {
            await RunProfileAsync(options, phases, memory).ConfigureAwait(false);
            succeeded = true;
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }

        var report = new DocumentSoakReport(
            options.Profile,
            options.DocumentCount,
            options.BatchSize,
            startedAt,
            DateTimeOffset.UtcNow,
            succeeded,
            failure,
            new SoakEnvironment(
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.MachineName,
                Environment.ProcessorCount,
                GCSettings(),
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
            phases,
            memory);
        await WriteReportAsync(report, options.OutputDirectory).ConfigureAwait(false);

        if (!options.KeepData)
            TryDelete(options.WorkRoot);
        return succeeded ? 0 : 1;
    }

    private static async Task RunProfileAsync(
        SoakOptions options,
        List<SoakPhase> phases,
        List<MemorySample> memory)
    {
        string backupRoot = Path.Combine(options.WorkRoot, "backup");
        string restoreRoot = Path.Combine(options.WorkRoot, "restore");
        using (var database = Open(options.DataRoot))
        {
            database.Documents.Drop(CollectionName);
            database.Documents.Create(DocumentCollectionSchema.Create(CollectionName));
            var store = database.Documents.Open(CollectionName);

            var writeWatch = Stopwatch.StartNew();
            int sampleEvery = Math.Max(options.BatchSize, options.DocumentCount / 20);
            int nextSample = sampleEvery;
            for (int offset = 0; offset < options.DocumentCount; offset += options.BatchSize)
            {
                int count = Math.Min(options.BatchSize, options.DocumentCount - offset);
                var batch = new DocumentWriteRequest[count];
                for (int i = 0; i < count; i++)
                {
                    int ordinal = offset + i;
                    batch[i] = new DocumentWriteRequest(
                        "doc-" + ordinal.ToString("D10", CultureInfo.InvariantCulture),
                        BuildDocument(ordinal, DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds()));
                }
                var result = store.InsertMany(batch);
                if (result.HasErrors || result.Inserted != count)
                    throw new InvalidOperationException($"Batch at {offset} inserted {result.Inserted}/{count}: {string.Join("; ", result.Errors.Select(static error => error.Message))}");
                if (offset + count >= nextSample || offset + count == options.DocumentCount)
                {
                    memory.Add(CaptureMemory("write", offset + count));
                    nextSample += sampleEvery;
                }
            }
            writeWatch.Stop();
            phases.Add(Phase("write", writeWatch, options.DocumentCount));

            phases.Add(Measure("index_create", options.DocumentCount, () =>
                database.Documents.CreateIndex(CollectionName, new DocumentPathIndexDefinition(SiteIndexName, "$.site"))));

            var queryWatch = Stopwatch.StartNew();
            var siteIndex = store.Schema.TryGetIndex(SiteIndexName)
                ?? throw new InvalidOperationException("Site index was not created.");
            long queryRows = 0;
            for (int i = 0; i < options.QueryIterations; i++)
                queryRows += store.GetByIndex(siteIndex, "site-03", limit: 100).Count;
            queryWatch.Stop();
            phases.Add(Phase("indexed_query", queryWatch, options.QueryIterations, new Dictionary<string, string>
            {
                ["rows_observed"] = queryRows.ToString(CultureInfo.InvariantCulture),
            }));

            phases.Add(Measure("index_rebuild", options.DocumentCount, () =>
                database.Documents.RebuildIndex(CollectionName, SiteIndexName)));

            phases.Add(Measure("ttl_index_create", options.DocumentCount, () =>
                database.Documents.CreateIndex(CollectionName, new DocumentPathIndexDefinition(
                    "ttl_created_at",
                    "$.createdAt",
                    TtlPath: "$.createdAt",
                    TtlSeconds: 60))));
            var expired = Enumerable.Range(0, 100).Select(i => new DocumentWriteRequest(
                "expired-" + i.ToString("D4", CultureInfo.InvariantCulture),
                BuildDocument(options.DocumentCount + i, DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()))).ToArray();
            store.InsertMany(expired);
            phases.Add(Measure("ttl_cleanup", expired.Length, () =>
            {
                int count = store.Count();
                if (count != options.DocumentCount)
                    throw new InvalidOperationException($"TTL cleanup expected {options.DocumentCount} documents, got {count}.");
            }));

            phases.Add(Measure("backup", options.DocumentCount, () =>
                new BackupService().Create(database, new BackupCreateOptions { DestinationDirectory = backupRoot })));
            memory.Add(CaptureMemory("hot_end", options.DocumentCount));
        }

        phases.Add(Measure("hot_reopen", options.DocumentCount, () =>
        {
            using var reopened = Open(options.DataRoot);
            var report = reopened.Documents.Open(CollectionName).VerifyIndexConsistency();
            if (!report.IsConsistent || report.DocumentCount != options.DocumentCount)
                throw new InvalidOperationException("Hot reopen index consistency validation failed.");
        }));

        var coldStartWatch = Stopwatch.StartNew();
        int verifyExitCode = await RunWorkerProcessAsync("--verify-worker", options.DataRoot, options.DocumentCount).ConfigureAwait(false);
        coldStartWatch.Stop();
        if (verifyExitCode != 0)
            throw new InvalidOperationException($"Cold-process verification worker returned exit code {verifyExitCode}.");
        phases.Add(Phase("cold_process_start", coldStartWatch, options.DocumentCount, new Dictionary<string, string>
        {
            ["os_page_cache"] = "not_flushed",
        }));

        var crashWatch = Stopwatch.StartNew();
        int exitCode = await RunWorkerProcessAsync("--crash-worker", options.DataRoot).ConfigureAwait(false);
        if (exitCode != 23)
            throw new InvalidOperationException($"Crash worker returned unexpected exit code {exitCode}.");
        using (var recovered = Open(options.DataRoot))
        {
            var report = recovered.Documents.Open(CollectionName).VerifyIndexConsistency();
            if (!report.IsConsistent || report.DocumentCount != options.DocumentCount + CrashWriteCount)
                throw new InvalidOperationException($"Crash recovery expected {options.DocumentCount + CrashWriteCount} consistent documents, got {report.DocumentCount}.");
        }
        crashWatch.Stop();
        phases.Add(Phase("crash_recovery", crashWatch, CrashWriteCount));

        phases.Add(Measure("backup_restore", options.DocumentCount + CrashWriteCount, () =>
        {
            new BackupService().Restore(new BackupRestoreOptions
            {
                BackupDirectory = backupRoot,
                TargetDirectory = restoreRoot,
            });
            using var restored = Open(restoreRoot);
            var report = restored.Documents.Open(CollectionName).VerifyIndexConsistency();
            if (!report.IsConsistent || report.DocumentCount != options.DocumentCount)
                throw new InvalidOperationException($"Restored backup expected {options.DocumentCount} documents, got {report.DocumentCount}.");
        }));
        memory.Add(CaptureMemory("completed", options.DocumentCount + CrashWriteCount));
    }

    private static int RunCrashWorker(string[] args)
    {
        if (args.Length != 2)
            return 2;
        var database = Open(args[1]);
        var store = database.Documents.Open(CollectionName);
        long future = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        for (int i = 0; i < CrashWriteCount; i++)
        {
            store.Insert(
                "crash-" + i.ToString("D4", CultureInfo.InvariantCulture),
                BuildDocument(1_000_000_000 + i, future));
        }
        Environment.Exit(23);
        return 23;
    }

    private static int RunVerifyWorker(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int expectedCount))
            return 2;
        using var database = Open(args[1]);
        var report = database.Documents.Open(CollectionName).VerifyIndexConsistency();
        return report.IsConsistent && report.DocumentCount == expectedCount ? 0 : 3;
    }

    private static async Task<int> RunWorkerProcessAsync(string mode, string dataRoot, int? expectedCount = null)
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current process path is unavailable.");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(dataRoot);
        if (expectedCount is { } count)
            start.ArgumentList.Add(count.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start crash worker.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static Tsdb Open(string root) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        FlushWalToOsOnWrite = true,
    });

    private static string BuildDocument(int ordinal, long createdAt)
        => $$"""{"serial":"serial-{{ordinal.ToString("D10", CultureInfo.InvariantCulture)}}","site":"site-{{ordinal % 16:D2}}","value":{{ordinal % 1000}},"createdAt":{{createdAt}},"payload":"document-store-capacity-profile"}""";

    private static SoakPhase Measure(string name, long operations, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        return Phase(name, watch, operations);
    }

    private static SoakPhase Phase(
        string name,
        Stopwatch watch,
        long operations,
        IReadOnlyDictionary<string, string>? details = null)
    {
        var result = new SoakPhase(
            name,
            watch.Elapsed.TotalMilliseconds,
            operations,
            watch.Elapsed.TotalSeconds <= 0 ? 0 : operations / watch.Elapsed.TotalSeconds,
            details ?? new Dictionary<string, string>());
        Console.WriteLine(
            $"[{DateTimeOffset.UtcNow:O}] {name}: {result.DurationMilliseconds:F2} ms, {result.OperationsPerSecond:F2} ops/s");
        return result;
    }

    private static MemorySample CaptureMemory(string phase, long documents)
    {
        using var process = Process.GetCurrentProcess();
        var sample = new MemorySample(
            DateTimeOffset.UtcNow,
            phase,
            documents,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false));
        Console.WriteLine(
            $"[{sample.TimestampUtc:O}] memory/{phase}: documents={documents}, working_set={sample.WorkingSetBytes}, managed={sample.ManagedBytes}");
        return sample;
    }

    private static string GCSettings()
        => $"server={System.Runtime.GCSettings.IsServerGC}; latency={System.Runtime.GCSettings.LatencyMode}";

    private static async Task WriteReportAsync(DocumentSoakReport report, string outputDirectory)
    {
        string jsonPath = Path.Combine(outputDirectory, "report.json");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, DocumentSoakJsonContext.Default.DocumentSoakReport)).ConfigureAwait(false);

        var markdown = new StringBuilder();
        markdown.Append("# Document Store Capacity Profile\n\n")
            .Append("- Profile: `").Append(report.Profile).Append("`\n")
            .Append("- Documents: ").Append(report.DocumentCount.ToString("N0", CultureInfo.InvariantCulture)).Append("\n")
            .Append("- Started: ").Append(report.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append("\n")
            .Append("- Result: **").Append(report.Succeeded ? "PASS" : "FAIL").Append("**\n\n")
            .Append("| Phase | Duration ms | Operations | Ops/s |\n|---|---:|---:|---:|\n");
        foreach (var phase in report.Phases)
        {
            markdown.Append("| ").Append(phase.Name)
                .Append(" | ").Append(phase.DurationMilliseconds.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" | ").Append(phase.Operations.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(phase.OperationsPerSecond.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" |\n");
        }
        markdown.Append("\n## Memory curve\n\n| Phase | Documents | Working set bytes | Private bytes | Managed bytes |\n|---|---:|---:|---:|---:|\n");
        foreach (var sample in report.MemorySamples)
        {
            markdown.Append("| ").Append(sample.Phase)
                .Append(" | ").Append(sample.Documents)
                .Append(" | ").Append(sample.WorkingSetBytes)
                .Append(" | ").Append(sample.PrivateBytes)
                .Append(" | ").Append(sample.ManagedBytes)
                .Append(" |\n");
        }
        if (!string.IsNullOrWhiteSpace(report.Failure))
            markdown.Append("\n## Failure\n\n```text\n").Append(report.Failure).Append("\n```\n");
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.md"), markdown.ToString()).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record SoakOptions(
    string Profile,
    int DocumentCount,
    int BatchSize,
    int QueryIterations,
    string OutputDirectory,
    string WorkRoot,
    string DataRoot,
    bool KeepData)
{
    public static SoakOptions Parse(string[] args)
    {
        string profile = Value(args, "--profile") ?? "quick";
        int documentCount = profile switch
        {
            "quick" => 10_000,
            "million" => 1_000_000,
            "ten-million" => 10_000_000,
            _ => throw new ArgumentException("--profile must be quick, million, or ten-million."),
        };
        if (int.TryParse(Value(args, "--documents"), out int overridden) && overridden > 0)
            documentCount = overridden;
        string runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + profile;
        string output = Path.GetFullPath(Value(args, "--output") ?? Path.Combine("artifacts", "document-soak", runId));
        string work = Path.GetFullPath(Value(args, "--work-root") ?? Path.Combine(Path.GetTempPath(), "sonnetdb-document-soak-" + Guid.NewGuid().ToString("N")));
        return new SoakOptions(
            profile,
            documentCount,
            BatchSize: profile == "ten-million" ? 5_000 : 1_000,
            QueryIterations: profile == "quick" ? 20 : 100,
            output,
            work,
            Path.Combine(work, "database"),
            args.Contains("--keep-data", StringComparer.Ordinal));
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed record DocumentSoakReport(
    string Profile,
    int DocumentCount,
    int BatchSize,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Succeeded,
    string? Failure,
    SoakEnvironment Environment,
    IReadOnlyList<SoakPhase> Phases,
    IReadOnlyList<MemorySample> MemorySamples);

internal sealed record SoakEnvironment(
    string OperatingSystem,
    string Framework,
    string ProcessArchitecture,
    string MachineName,
    int ProcessorCount,
    string GarbageCollector,
    long TotalAvailableMemoryBytes);

internal sealed record SoakPhase(
    string Name,
    double DurationMilliseconds,
    long Operations,
    double OperationsPerSecond,
    IReadOnlyDictionary<string, string> Details);

internal sealed record MemorySample(
    DateTimeOffset TimestampUtc,
    string Phase,
    long Documents,
    long WorkingSetBytes,
    long PrivateBytes,
    long ManagedBytes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DocumentSoakReport))]
[JsonSerializable(typeof(SoakEnvironment))]
[JsonSerializable(typeof(SoakPhase))]
[JsonSerializable(typeof(MemorySample))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class DocumentSoakJsonContext : JsonSerializerContext;
