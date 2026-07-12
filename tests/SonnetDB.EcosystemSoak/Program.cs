using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using SonnetDB.Backup;
using SonnetDB.Caching.Distributed;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.Engine;
using SonnetDB.EntityFrameworkCore.Extensions;
using SonnetDB.Model;
using SonnetDB.ObjectStorage;
using SonnetDB.Query;
using SonnetDB.Sql.Execution;

namespace SonnetDB.EcosystemSoak;

internal static class Program
{
    private const int CrashSeedPointCount = 128;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--crash-worker", StringComparison.Ordinal))
            return RunCrashWorker(args);

        var options = SoakOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);
        Directory.CreateDirectory(options.WorkRoot);
        var startedUtc = DateTimeOffset.UtcNow;
        var cycles = new List<SoakCycleResult>();
        string? failure = null;

        try
        {
            for (int cycle = 1; cycle <= options.Cycles; cycle++)
                cycles.Add(await RunCycleAsync(options, cycle).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }

        var report = new EcosystemSoakReport(
            options.Profile,
            startedUtc,
            DateTimeOffset.UtcNow,
            failure is null,
            failure,
            options.ToReportOptions(),
            new SoakEnvironment(
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.MachineName,
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
            cycles);
        await WriteReportAsync(report, options.OutputDirectory).ConfigureAwait(false);

        if (!options.KeepData)
            TryDelete(options.WorkRoot);

        Console.WriteLine($"report={Path.Combine(options.OutputDirectory, "report.md")}");
        return report.Succeeded ? 0 : 1;
    }

    private static async Task<SoakCycleResult> RunCycleAsync(SoakOptions options, int cycle)
    {
        string cycleRoot = Path.Combine(options.WorkRoot, $"cycle-{cycle:D4}");
        string databaseRoot = Path.Combine(cycleRoot, "database");
        Directory.CreateDirectory(databaseRoot);
        var phases = new List<SoakPhaseResult>();
        var startedUtc = DateTimeOffset.UtcNow;

        phases.Add(await MeasureAsync("ef_core_provider", async () =>
        {
            string connectionString = $"Data Source={databaseRoot}";
            var dbOptions = new DbContextOptionsBuilder<SoakDbContext>()
                .UseSonnetDB(connectionString)
                .Options;
            await using var context = new SoakDbContext(dbOptions);
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);

            var rows = Enumerable.Range(1, options.RelationalRows)
                .Select(index => new SoakProduct { Id = index, Name = $"product-{index:D8}" })
                .ToArray();
            context.Products.AddRange(rows);
            await context.SaveChangesAsync().ConfigureAwait(false);
            int count = await context.Products.AsNoTracking().CountAsync().ConfigureAwait(false);
            if (count != options.RelationalRows)
                throw new InvalidDataException($"EF Core rows mismatch: expected {options.RelationalRows}, actual {count}.");

            return PhaseData(options.RelationalRows, ("rows", count.ToString(CultureInfo.InvariantCulture)));
        }).ConfigureAwait(false));

        phases.Add(await MeasureAsync("bulk_write_many_measurements", () =>
        {
            long written = 0;
            using var db = Open(databaseRoot);
            for (int measurement = 0; measurement < options.Measurements; measurement++)
            {
                string name = $"soak_m_{measurement:D8}";
                var points = new Point[options.PointsPerMeasurement];
                for (int point = 0; point < points.Length; point++)
                {
                    points[point] = Point.Create(
                        name,
                        1_700_000_000_000L + point,
                        new Dictionary<string, string> { ["host"] = $"edge-{measurement % 32:D2}" },
                        new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromDouble(point) });
                }

                db.WriteMany(points);
                written += points.Length;
            }

            db.FlushNow();
            if (db.Measurements.Count != options.Measurements)
                throw new InvalidDataException($"Measurement count mismatch: expected {options.Measurements}, actual {db.Measurements.Count}.");

            return Task.FromResult(PhaseData(
                written,
                ("measurements", options.Measurements.ToString(CultureInfo.InvariantCulture)),
                ("points", written.ToString(CultureInfo.InvariantCulture)),
                ("segments", db.Segments.SegmentCount.ToString(CultureInfo.InvariantCulture))));
        }).ConfigureAwait(false));

        phases.Add(await MeasureAsync("kv_cache_ttl", async () =>
        {
            using var cache = new SonnetDbDistributedCache(Options.Create(new SonnetDbDistributedCacheOptions
            {
                ConnectionString = $"Data Source={databaseRoot}",
                Keyspace = "ecosystem-cache",
                Namespace = "soak",
                ExpirationScanInterval = TimeSpan.Zero,
            }));

            await cache.SetAsync(
                "durable",
                [0x2A],
                new DistributedCacheEntryOptions()).ConfigureAwait(false);
            for (int index = 0; index < options.CacheEntries; index++)
            {
                await cache.SetAsync(
                    $"ttl-{index:D8}",
                    BitConverter.GetBytes(index),
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(options.CacheTtlMilliseconds),
                    }).ConfigureAwait(false);
            }

            await Task.Delay(options.CacheTtlMilliseconds + 80).ConfigureAwait(false);
            for (int index = 0; index < options.CacheEntries; index++)
            {
                if (await cache.GetAsync($"ttl-{index:D8}").ConfigureAwait(false) is not null)
                    throw new InvalidDataException($"Expired cache key ttl-{index:D8} remained visible.");
            }

            byte[]? durable = await cache.GetAsync("durable").ConfigureAwait(false);
            if (durable is null || !durable.AsSpan().SequenceEqual([(byte)0x2A]))
                throw new InvalidDataException("Non-expiring cache key was lost.");

            return PhaseData(
                options.CacheEntries + 1L,
                ("expiredKeys", options.CacheEntries.ToString(CultureInfo.InvariantCulture)),
                ("ttlMilliseconds", options.CacheTtlMilliseconds.ToString(CultureInfo.InvariantCulture)));
        }).ConfigureAwait(false));

        phases.Add(await MeasureAsync("object_multipart", async () =>
        {
            using var objects = new SndbObjectStorageClient($"Data Source={databaseRoot}");
            const string bucket = "ecosystem-soak";
            await objects.CreateBucketAsync(bucket, SndbBucketPurpose.Artifact).ConfigureAwait(false);
            var upload = await objects.InitiateMultipartUploadAsync(bucket, "payload.bin", "application/octet-stream").ConfigureAwait(false);
            using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var partNumbers = new List<int>(options.MultipartParts);
            for (int partNumber = 1; partNumber <= options.MultipartParts; partNumber++)
            {
                byte[] bytes = CreatePart(options.MultipartPartBytes, partNumber);
                expectedHash.AppendData(bytes);
                using var partContent = new MemoryStream(bytes);
                await objects.UploadPartAsync(bucket, "payload.bin", upload.UploadId, partNumber, partContent).ConfigureAwait(false);
                partNumbers.Add(partNumber);
            }

            var completed = await objects.CompleteMultipartUploadAsync(bucket, "payload.bin", upload.UploadId, partNumbers).ConfigureAwait(false);
            var read = await objects.OpenReadAsync(bucket, "payload.bin").ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed multipart object was not found.");
            await using var content = read.Content;
            byte[] expectedDigest = expectedHash.GetHashAndReset();
            byte[] actualDigest = await SHA256.HashDataAsync(content).ConfigureAwait(false);
            if (!expectedDigest.AsSpan().SequenceEqual(actualDigest))
                throw new InvalidDataException("Multipart object content mismatch.");
            if (!string.Equals(completed.Sha256, Convert.ToHexString(actualDigest).ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidDataException("Multipart object metadata SHA-256 mismatch.");

            return PhaseData(
                completed.SizeBytes,
                ("parts", options.MultipartParts.ToString(CultureInfo.InvariantCulture)),
                ("bytes", completed.SizeBytes.ToString(CultureInfo.InvariantCulture)),
                ("sha256", completed.Sha256));
        }).ConfigureAwait(false));

        phases.Add(await MeasureAsync("migration_backup_restore_rollback", async () =>
        {
            string packageRoot = Path.Combine(cycleRoot, "migration-package");
            string restoredRoot = Path.Combine(cycleRoot, "restored");
            var migration = new MigrationService();
            MigrationExportResult exported;

            using (var source = Open(databaseRoot))
            {
                exported = migration.Export(source, new MigrationExportOptions { PackageDirectory = packageRoot });
                source.Write(Point.Create(
                    "post_upgrade_probe",
                    1_800_000_000_000L,
                    new Dictionary<string, string> { ["version"] = "next" },
                    new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromLong(1) }));
                source.FlushNow();
            }

            var dryRun = migration.ImportDryRun(new MigrationImportOptions
            {
                PackageDirectory = packageRoot,
                TargetDirectory = restoredRoot,
            });
            if (!dryRun.IsValid)
                throw new InvalidDataException("Migration dry-run failed: " + string.Join("; ", dryRun.Errors));

            var imported = migration.Import(new MigrationImportOptions
            {
                PackageDirectory = packageRoot,
                TargetDirectory = restoredRoot,
            });

            using var restored = Open(restoredRoot);
            if (restored.Measurements.Count != options.Measurements)
                throw new InvalidDataException("Restored measurement count mismatch.");
            if (restored.Catalog.Find("post_upgrade_probe", new Dictionary<string, string> { ["version"] = "next" }).Count != 0)
                throw new InvalidDataException("Rollback restore unexpectedly contains post-snapshot data.");

            var products = AssertSelect(restored, "SELECT count(*) FROM soak_products");
            if (Convert.ToInt64(products.Rows[0][0], CultureInfo.InvariantCulture) != options.RelationalRows)
                throw new InvalidDataException("Restored relational row count mismatch.");
            if (restored.Keyspaces.Open("ecosystem-cache").Count == 0)
                throw new InvalidDataException("Restored cache keyspace is empty.");
            if (new SndbObjectStore(restored).HeadObject("ecosystem-soak", "payload.bin") is null)
                throw new InvalidDataException("Restored multipart object is missing.");

            await Task.CompletedTask;
            return PhaseData(
                imported.Scan.TotalBytes,
                ("files", imported.Scan.FileCount.ToString(CultureInfo.InvariantCulture)),
                ("packageSha256", imported.Checksum.PackageSha256),
                ("databaseFormat", imported.Manifest.DatabaseFormat),
                ("rollbackProbeExcluded", "true"));
        }).ConfigureAwait(false));

        phases.Add(await MeasureAsync("process_crash_recovery", () =>
        {
            int recovered = RunCrashRecovery(Path.Combine(cycleRoot, "crash"));
            return Task.FromResult(PhaseData(
                recovered,
                ("seedPoints", CrashSeedPointCount.ToString(CultureInfo.InvariantCulture)),
                ("recoveredPoints", recovered.ToString(CultureInfo.InvariantCulture)),
                ("injection", "Process.Kill(entireProcessTree: true)")));
        }).ConfigureAwait(false));

        phases.Add(await MeasureAsync("power_loss_torn_wal_recovery", () =>
        {
            int recovered = RunTornWalRecovery(Path.Combine(cycleRoot, "power-loss"));
            return Task.FromResult(PhaseData(
                recovered,
                ("acknowledgedPoints", "1"),
                ("recoveredPoints", recovered.ToString(CultureInfo.InvariantCulture)),
                ("injection", "incomplete WAL tail")));
        }).ConfigureAwait(false));

        return new SoakCycleResult(cycle, startedUtc, DateTimeOffset.UtcNow, phases);
    }

    private static async Task<SoakPhaseResult> MeasureAsync(string name, Func<Task<PhaseMeasurement>> action)
    {
        var watch = Stopwatch.StartNew();
        var measurement = await action().ConfigureAwait(false);
        watch.Stop();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000001);
        return new SoakPhaseResult(
            name,
            watch.Elapsed.TotalMilliseconds,
            measurement.Operations,
            measurement.Operations / seconds,
            GC.GetTotalMemory(forceFullCollection: false),
            measurement.Details);
    }

    private static PhaseMeasurement PhaseData(long operations, params (string Key, string Value)[] details)
        => new(operations, details.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal));

    private static SelectExecutionResult AssertSelect(Tsdb db, string sql)
        => SqlExecutor.Execute(db, sql) as SelectExecutionResult
            ?? throw new InvalidDataException($"Expected SELECT result for: {sql}");

    private static int RunCrashRecovery(string root)
    {
        Directory.CreateDirectory(root);
        string readyFile = Path.Combine(root, "ready");
        string assemblyPath = typeof(Program).Assembly.Location;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{assemblyPath}\" --crash-worker \"{root}\" \"{readyFile}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("无法启动 EcosystemSoak 崩溃注入子进程。");

        try
        {
            WaitForReady(process, readyFile, TimeSpan.FromSeconds(20));
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
                throw new TimeoutException("崩溃注入子进程未在超时内退出。");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }

        using var recovered = Open(root);
        var series = recovered.Catalog.Find("crash_probe", new Dictionary<string, string> { ["host"] = "worker" });
        if (series.Count != 1)
            throw new InvalidDataException("Crash recovery did not restore the expected series.");
        int count = recovered.Query.Execute(new PointQuery(series[0].Id, "value", TimeRange.All)).Count();
        if (count < CrashSeedPointCount)
            throw new InvalidDataException($"Crash recovery lost acknowledged writes: expected at least {CrashSeedPointCount}, actual {count}.");
        return count;
    }

    private static int RunCrashWorker(string[] args)
    {
        if (args.Length != 3)
            return 2;

        string root = args[1];
        string readyFile = args[2];
        Directory.CreateDirectory(root);
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = root,
            SyncWalOnEveryWrite = true,
            BackgroundFlush = new() { Enabled = false },
            Compaction = new() { Enabled = false },
            FlushPolicy = new()
            {
                MaxPoints = long.MaxValue,
                MaxBytes = long.MaxValue,
                HardCapBytes = 0,
                MaxAge = TimeSpan.MaxValue,
            },
        });

        for (int index = 0; index < CrashSeedPointCount; index++)
        {
            db.Write(Point.Create(
                "crash_probe",
                2_000_000_000_000L + index,
                new Dictionary<string, string> { ["host"] = "worker" },
                new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromLong(index) }));
        }

        File.WriteAllText(readyFile, "ready");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static int RunTornWalRecovery(string root)
    {
        Directory.CreateDirectory(root);
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = root,
            SyncWalOnEveryWrite = true,
            BackgroundFlush = new() { Enabled = false },
            Compaction = new() { Enabled = false },
        }))
        {
            db.Write(Point.Create(
                "power_loss_probe",
                2_100_000_000_000L,
                new Dictionary<string, string> { ["host"] = "worker" },
                new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromLong(1) }));
        }

        string walDirectory = Path.Combine(root, "wal");
        string walPath = Directory.EnumerateFiles(walDirectory, "*.SDBWAL", SearchOption.TopDirectoryOnly).Single();
        using (var stream = new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.Write([0x42, 0x13, 0x37, 0x00, 0x7F]);

        using var recovered = Open(root);
        var series = recovered.Catalog.Find("power_loss_probe", new Dictionary<string, string> { ["host"] = "worker" });
        if (series.Count != 1)
            throw new InvalidDataException("Torn WAL recovery did not restore the expected series.");
        int count = recovered.Query.Execute(new PointQuery(series[0].Id, "value", TimeRange.All)).Count();
        if (count != 1)
            throw new InvalidDataException($"Torn WAL recovery expected one point, actual {count}.");
        return count;
    }

    private static void WaitForReady(Process process, string readyFile, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (File.Exists(readyFile))
                return;
            if (process.HasExited)
                throw new InvalidOperationException($"崩溃注入子进程提前退出：{process.StandardError.ReadToEnd()}");
            Thread.Sleep(25);
        }

        throw new TimeoutException("等待崩溃注入子进程 ready 超时。");
    }

    private static byte[] CreatePart(int size, int partNumber)
    {
        var bytes = new byte[size];
        new Random(10_000 + partNumber).NextBytes(bytes);
        return bytes;
    }

    private static Tsdb Open(string root) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        BackgroundFlush = new() { Enabled = false },
        Compaction = new() { Enabled = false },
    });

    private static async Task WriteReportAsync(EcosystemSoakReport report, string outputDirectory)
    {
        string jsonPath = Path.Combine(outputDirectory, "report.json");
        string markdownPath = Path.Combine(outputDirectory, "report.md");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, EcosystemSoakJsonContext.Default.EcosystemSoakReport)).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report)).ConfigureAwait(false);
    }

    private static string BuildMarkdown(EcosystemSoakReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# SonnetDB Ecosystem Soak Report");
        text.AppendLine();
        text.AppendLine($"- Profile: `{report.Profile}`");
        text.AppendLine($"- Result: `{(report.Succeeded ? "PASS" : "FAIL")}`");
        text.AppendLine($"- Started UTC: `{report.StartedUtc:O}`");
        text.AppendLine($"- Finished UTC: `{report.FinishedUtc:O}`");
        text.AppendLine($"- Runtime: `{report.Environment.Framework}` on `{report.Environment.Os}`");
        text.AppendLine($"- Shape: `{report.Options.Measurements}` measurements x `{report.Options.PointsPerMeasurement}` points, `{report.Options.Cycles}` cycle(s)");
        text.AppendLine();
        text.AppendLine("| Cycle | Phase | Duration ms | Operations | Operations/sec | Managed bytes |");
        text.AppendLine("| ---: | --- | ---: | ---: | ---: | ---: |");
        foreach (var cycle in report.Cycles)
        {
            foreach (var phase in cycle.Phases)
            {
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {cycle.Cycle} | {phase.Name} | {phase.DurationMilliseconds:F2} | {phase.Operations} | {phase.OperationsPerSecond:F2} | {phase.ManagedMemoryBytes} |"));
            }
        }

        text.AppendLine();
        text.AppendLine("## Phase evidence");
        text.AppendLine();
        foreach (var cycle in report.Cycles)
        {
            foreach (var phase in cycle.Phases)
            {
                string details = string.Join(", ", phase.Details.Select(static item => $"{item.Key}={item.Value}"));
                text.AppendLine($"- Cycle {cycle.Cycle} `{phase.Name}`: {details}");
            }
        }

        if (!report.Succeeded)
        {
            text.AppendLine();
            text.AppendLine("## Failure");
            text.AppendLine();
            text.AppendLine("```text");
            text.AppendLine(report.Failure);
            text.AppendLine("```");
        }

        return text.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 报告已落盘，测试数据清理失败不覆盖真实验收结果。
        }
    }
}

internal sealed class SoakOptions
{
    public required string Profile { get; init; }
    public required string WorkRoot { get; init; }
    public required string OutputDirectory { get; init; }
    public required int Cycles { get; init; }
    public required int RelationalRows { get; init; }
    public required int Measurements { get; init; }
    public required int PointsPerMeasurement { get; init; }
    public required int CacheEntries { get; init; }
    public required int CacheTtlMilliseconds { get; init; }
    public required int MultipartParts { get; init; }
    public required int MultipartPartBytes { get; init; }
    public required bool KeepData { get; init; }

    public SoakReportOptions ToReportOptions() => new(
        Cycles,
        RelationalRows,
        Measurements,
        PointsPerMeasurement,
        CacheEntries,
        CacheTtlMilliseconds,
        MultipartParts,
        MultipartPartBytes);

    public static SoakOptions Parse(string[] args)
    {
        string profile = ReadValue(args, "--profile") ?? "quick";
        var defaults = profile.ToLowerInvariant() switch
        {
            "quick" => new ProfileDefaults(1, 100, 64, 16, 100, 100, 3, 64 * 1024),
            "ci" => new ProfileDefaults(2, 1_000, 1_000, 100, 1_000, 200, 4, 1024 * 1024),
            "soak" => new ProfileDefaults(10, 10_000, 10_000, 1_000, 100_000, 500, 8, 5 * 1024 * 1024),
            _ => throw new ArgumentException("--profile 必须是 quick、ci 或 soak。"),
        };

        string workRoot = Path.GetFullPath(ReadValue(args, "--work")
            ?? Path.Combine(Path.GetTempPath(), "sonnetdb-ecosystem-soak", Guid.NewGuid().ToString("N")));
        string output = Path.GetFullPath(ReadValue(args, "--output")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "ecosystem-soak"));
        if (IsSameOrDescendant(output, workRoot))
            throw new ArgumentException("--output 不能位于 --work 内部，否则测试数据清理会删除报告。");

        return new SoakOptions
        {
            Profile = profile,
            WorkRoot = workRoot,
            OutputDirectory = output,
            Cycles = PositiveInt(args, "--cycles", defaults.Cycles),
            RelationalRows = PositiveInt(args, "--relational-rows", defaults.RelationalRows),
            Measurements = PositiveInt(args, "--measurements", defaults.Measurements),
            PointsPerMeasurement = PositiveInt(args, "--points-per-measurement", defaults.PointsPerMeasurement),
            CacheEntries = PositiveInt(args, "--cache-entries", defaults.CacheEntries),
            CacheTtlMilliseconds = PositiveInt(args, "--cache-ttl-ms", defaults.CacheTtlMilliseconds),
            MultipartParts = PositiveInt(args, "--multipart-parts", defaults.MultipartParts),
            MultipartPartBytes = PositiveInt(args, "--multipart-part-bytes", defaults.MultipartPartBytes),
            KeepData = args.Contains("--keep-data", StringComparer.Ordinal),
        };
    }

    private static int PositiveInt(string[] args, string name, int fallback)
    {
        string? value = ReadValue(args, name);
        if (value is null)
            return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"{name} 必须是正整数。");
        return parsed;
    }

    private static string? ReadValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.Ordinal));
        if (index < 0)
            return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{name} 缺少值。");
        return args[index + 1];
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        if (Path.IsPathFullyQualified(relative))
            return false;

        return string.Equals(relative, ".", StringComparison.Ordinal)
            || (!string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }
}

internal sealed class SoakDbContext(DbContextOptions<SoakDbContext> options) : DbContext(options)
{
    public DbSet<SoakProduct> Products => Set<SoakProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SoakProduct>(entity =>
        {
            entity.ToTable("soak_products");
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name).HasMaxLength(200).IsRequired();
        });
    }
}

internal sealed class SoakProduct
{
    public int Id { get; set; }
    public required string Name { get; set; }
}

internal sealed record ProfileDefaults(
    int Cycles,
    int RelationalRows,
    int Measurements,
    int PointsPerMeasurement,
    int CacheEntries,
    int CacheTtlMilliseconds,
    int MultipartParts,
    int MultipartPartBytes);

internal sealed record PhaseMeasurement(long Operations, IReadOnlyDictionary<string, string> Details);

internal sealed record SoakReportOptions(
    int Cycles,
    int RelationalRows,
    int Measurements,
    int PointsPerMeasurement,
    int CacheEntries,
    int CacheTtlMilliseconds,
    int MultipartParts,
    int MultipartPartBytes);

internal sealed record SoakEnvironment(
    string Os,
    string Framework,
    string Architecture,
    string MachineName,
    int ProcessorCount,
    long AvailableMemoryBytes);

internal sealed record SoakPhaseResult(
    string Name,
    double DurationMilliseconds,
    long Operations,
    double OperationsPerSecond,
    long ManagedMemoryBytes,
    IReadOnlyDictionary<string, string> Details);

internal sealed record SoakCycleResult(
    int Cycle,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    IReadOnlyList<SoakPhaseResult> Phases);

internal sealed record EcosystemSoakReport(
    string Profile,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    bool Succeeded,
    string? Failure,
    SoakReportOptions Options,
    SoakEnvironment Environment,
    IReadOnlyList<SoakCycleResult> Cycles);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EcosystemSoakReport))]
[JsonSerializable(typeof(SoakReportOptions))]
[JsonSerializable(typeof(SoakEnvironment))]
[JsonSerializable(typeof(SoakCycleResult))]
[JsonSerializable(typeof(SoakPhaseResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class EcosystemSoakJsonContext : JsonSerializerContext;
