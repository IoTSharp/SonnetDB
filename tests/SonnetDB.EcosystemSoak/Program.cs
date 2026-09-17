using System.Diagnostics;
using System.Globalization;
using System.Runtime;
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
        if (args.Length > 0 && string.Equals(args[0], "--maintenance-chaos-worker", StringComparison.Ordinal))
            return SpecializedSoakRunner.RunMaintenanceChaosWorker(args);

        var options = SoakOptions.Parse(args);
        // Capture repository provenance before the runner creates an output directory
        // under a checked-out workspace, otherwise its own artifacts make the tree dirty.
        SoakSourceRevision sourceRevision = ResolveSourceRevision();
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

        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;
        SoakEnvironment environment = SoakEnvironment.Capture(options.WorkRoot, sourceRevision.Value);
        var report = new EcosystemSoakReport(
            SchemaVersion: 2,
            Evidence: SoakEvidenceMetadata.Create(options, startedUtc, finishedUtc, sourceRevision),
            Profile: options.Profile,
            StartedUtc: startedUtc,
            FinishedUtc: finishedUtc,
            Succeeded: failure is null,
            Failure: failure,
            Options: options.ToReportOptions(),
            Environment: environment,
            TargetHardware: SoakTargetHardware.Capture(),
            EffectiveConfiguration: BuildEffectiveConfiguration(options, cycles),
            Cycles: cycles,
            Summary: SoakReportSummary.Create(options, cycles));
        await WriteReportAsync(report, options.OutputDirectory).ConfigureAwait(false);

        if (!options.KeepData)
            TryDelete(options.WorkRoot);

        Console.WriteLine($"report={Path.Combine(options.OutputDirectory, "report.md")}");
        return report.Succeeded ? 0 : 1;
    }

    private static async Task<SoakCycleResult> RunCycleAsync(SoakOptions options, int cycle)
    {
        if (options.IsSpecializedProfile)
            return await SpecializedSoakRunner.RunCycleAsync(options, cycle).ConfigureAwait(false);

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

    /// <summary>测量阶段耗时、吞吐和进程资源峰值，并合并专项证据。</summary>
    internal static async Task<SoakPhaseResult> MeasureAsync(string name, Func<Task<PhaseMeasurement>> action)
    {
        using var resources = new PhaseResourceMonitor();
        var watch = Stopwatch.StartNew();
        var measurement = await action().ConfigureAwait(false);
        watch.Stop();
        resources.Stop();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000001);
        return new SoakPhaseResult(
            name,
            watch.Elapsed.TotalMilliseconds,
            measurement.Operations,
            measurement.Operations / seconds,
            GC.GetTotalMemory(forceFullCollection: false),
            resources.PeakManagedMemoryBytes,
            resources.PeakWorkingSetBytes,
            resources.ManagedMemoryDeltaBytes,
            resources.AllocatedBytes,
            resources.Gen0Collections,
            resources.Gen1Collections,
            resources.Gen2Collections,
            resources.ProcessResources.PeakPrivateMemoryBytes,
            resources.ProcessResources.WorkingSetDeltaBytes,
            resources.ProcessResources.PrivateMemoryDeltaBytes,
            resources.ProcessResources.CpuTimeMilliseconds,
            resources.ProcessResources.CpuUtilizationPercent,
            resources.ProcessResources.ReadOperationDelta,
            resources.ProcessResources.WriteOperationDelta,
            resources.ProcessResources.ReadTransferBytesDelta,
            resources.ProcessResources.WriteTransferBytesDelta,
            measurement.Integrity,
            measurement.RecoveryLatencySamplesMilliseconds,
            measurement.QueryLatencySamplesMilliseconds,
            measurement.Details,
            measurement.EffectiveConfiguration,
            measurement.ProcessResourceContributions,
            measurement.MaintenanceChaosReservations);
    }

    /// <summary>为不包含专项分位数或完整性摘要的既有阶段创建测量结果。</summary>
    internal static PhaseMeasurement PhaseData(long operations, params (string Key, string Value)[] details)
        => new(
            operations,
            details.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal),
            null,
            [],
            []);

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
        using var db = Tsdb.Open(CreateCrashWorkerOptions(root));

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
        using (var db = Tsdb.Open(CreateTornWalRecoveryOptions(root)))
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

    private static Tsdb Open(string root) => Tsdb.Open(CreateStandardOptions(root));

    private static TsdbOptions CreateStandardOptions(string root) => new()
    {
        RootDirectory = root,
        BackgroundFlush = new() { Enabled = false },
        Compaction = new() { Enabled = false },
    };

    private static TsdbOptions CreateCrashWorkerOptions(string root) => new()
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
    };

    private static TsdbOptions CreateTornWalRecoveryOptions(string root) => new()
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = true,
        BackgroundFlush = new() { Enabled = false },
        Compaction = new() { Enabled = false },
    };

    private static IReadOnlyList<SoakConfigurationEvidence> BuildEffectiveConfiguration(
        SoakOptions options,
        IReadOnlyList<SoakCycleResult> cycles)
    {
        var evidence = new List<SoakConfigurationEvidence>
        {
            SoakConfigurationEvidence.CreateInvocation(options),
        };

        if (!options.IsSpecializedProfile)
        {
            evidence.Add(SoakConfigurationEvidence.FromTsdbOptions(
                "standard-embedded",
                "Program.Open",
                CreateStandardOptions("<report-root>"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["coverage"] = "Direct Tsdb opens from Program.Open; provider and client-owned opens are reported by their own components.",
                }));
            evidence.Add(SoakConfigurationEvidence.FromTsdbOptions(
                "process-crash-worker",
                "Program.CreateCrashWorkerOptions",
                CreateCrashWorkerOptions("<report-root>"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["coverage"] = "Child process used by process_crash_recovery.",
                }));
            evidence.Add(SoakConfigurationEvidence.FromTsdbOptions(
                "torn-wal-recovery",
                "Program.CreateTornWalRecoveryOptions",
                CreateTornWalRecoveryOptions("<report-root>"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["coverage"] = "Direct Tsdb open used by power_loss_torn_wal_recovery.",
                }));
        }

        foreach (SoakConfigurationEvidence configuration in cycles
            .SelectMany(static cycle => cycle.Phases)
            .SelectMany(static phase => phase.EffectiveConfiguration))
        {
            if (!evidence.Any(existing => string.Equals(
                existing.StableKey,
                configuration.StableKey,
                StringComparison.Ordinal)))
            {
                evidence.Add(configuration);
            }
        }

        return evidence;
    }

    private static async Task WriteReportAsync(EcosystemSoakReport report, string outputDirectory)
    {
        string jsonPath = Path.Combine(outputDirectory, "report.json");
        string markdownPath = Path.Combine(outputDirectory, "report.md");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, EcosystemSoakJsonContext.Default.EcosystemSoakReport)).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report)).ConfigureAwait(false);
    }

    private static SoakSourceRevision ResolveSourceRevision()
    {
        string workingTreeState = ResolveWorkingTreeState();
        foreach (string variable in new[] { "GITHUB_SHA", "BUILD_SOURCEVERSION" })
        {
            string? configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured))
                return new SoakSourceRevision(configured.Trim(), "environment:" + variable, workingTreeState);
        }

        string? revision = TryRunGit("rev-parse", "--verify", "HEAD");
        return string.IsNullOrWhiteSpace(revision)
            ? new SoakSourceRevision("UNAVAILABLE", "unavailable", workingTreeState)
            : new SoakSourceRevision(revision, "git:rev-parse", workingTreeState);
    }

    private static string ResolveWorkingTreeState()
    {
        string? status = TryRunGit("status", "--porcelain=v1", "--untracked-files=all");
        if (status is null)
            return "UNAVAILABLE";

        return status.Length == 0 ? "CLEAN" : "DIRTY";
    }

    private static string? TryRunGit(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return null;

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return null;
            }

            _ = error.GetAwaiter().GetResult();
            string result = output.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 ? result : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string BuildMarkdown(EcosystemSoakReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# SonnetDB Ecosystem Soak Report");
        text.AppendLine();
        text.AppendLine($"- Evidence schema: `{report.SchemaVersion}` (`{report.Evidence.Schema}`, contract `{report.Evidence.Contract}`)");
        text.AppendLine($"- Evidence run: `{report.Evidence.RunId}`, generated `{report.Evidence.GeneratedUtc:O}`");
        text.AppendLine($"- Profile: `{report.Profile}`");
        text.AppendLine($"- Result: `{(report.Succeeded ? "PASS" : "FAIL")}`");
        text.AppendLine($"- Started UTC: `{report.StartedUtc:O}`");
        text.AppendLine($"- Finished UTC: `{report.FinishedUtc:O}`");
        text.AppendLine($"- Runtime: `{report.Environment.Framework}` on `{report.Environment.Os}`");
        text.AppendLine($"- Commit: `{report.Environment.CommitSha}` (`{report.Evidence.SourceRevisionSource}`, working tree `{report.Evidence.WorkingTreeState}`)");
        text.AppendLine($"- Hardware: `{report.Environment.Hardware.ProcessorModel}` (`{report.Environment.Hardware.ProcessorModelSource}`), `{report.Environment.Architecture}` process / `{report.Environment.Hardware.OperatingSystemArchitecture}` OS, `{report.Environment.ProcessorCount}` logical CPU, `{report.Environment.AvailableMemoryBytes}` bytes available memory");
        text.AppendLine($"- GC: server `{report.Environment.Hardware.IsServerGarbageCollector}`, latency `{report.Environment.Hardware.GcLatencyMode}`, page `{report.Environment.Hardware.SystemPageSizeBytes}` bytes");
        text.AppendLine($"- Process provenance: `{report.Environment.Provenance.ProcessExecutable}` pid `{report.Environment.Provenance.ProcessId}`, runtime `{report.Environment.Provenance.RuntimeIdentifier}`, container `{report.Environment.Provenance.ContainerState}`");
        text.AppendLine($"- Disk: `{report.Environment.Disk.Root}` ({report.Environment.Disk.FileSystem}, `{report.Environment.Disk.DriveType}`), `{report.Environment.Disk.TotalBytes}` bytes total / `{report.Environment.Disk.AvailableBytes}` bytes available, device `{report.Environment.Disk.DeviceModel}` (`{report.Environment.Disk.DeviceModelSource}`)");
        text.AppendLine($"- Target hardware evidence: `{report.TargetHardware.Status}` (id `{report.TargetHardware.Id}`, contract `{report.TargetHardware.Contract}`, source `{report.TargetHardware.DeclarationSource}`)");
        text.AppendLine($"- Shape: `{report.Options.Measurements}` measurements x `{report.Options.PointsPerMeasurement}` points, `{report.Options.Cycles}` cycle(s)");
        text.AppendLine($"- Peak working set: `{report.Summary.PeakWorkingSetBytes}` bytes");
        text.AppendLine($"- Peak managed memory: `{report.Summary.PeakManagedMemoryBytes}` bytes");
        text.AppendLine($"- Total allocation / Gen0 / Gen1 / Gen2: `{report.Summary.Resources.TotalAllocatedBytes}` bytes / `{report.Summary.Resources.TotalGen0Collections}` / `{report.Summary.Resources.TotalGen1Collections}` / `{report.Summary.Resources.TotalGen2Collections}`");
        text.AppendLine($"- Parent CPU: `{report.Summary.Resources.TotalCpuTimeMilliseconds:F2}` ms across `{report.Summary.Resources.TotalMeasuredDurationMilliseconds:F2}` ms measured; average normalized CPU `{FormatNullable(report.Summary.Resources.AverageCpuUtilizationPercent, "F2")}`%");
        text.AppendLine($"- Parent I/O deltas: read/write operations `{FormatNullable(report.Summary.Resources.TotalReadOperationDelta)}` / `{FormatNullable(report.Summary.Resources.TotalWriteOperationDelta)}`; read/write transfer `{FormatNullable(report.Summary.Resources.TotalReadTransferBytesDelta)}` / `{FormatNullable(report.Summary.Resources.TotalWriteTransferBytesDelta)}` bytes");
        if (report.Summary.ExternalProcessResources.ProcessCount > 0)
        {
            text.AppendLine($"- External processes: `{report.Summary.ExternalProcessResources.ProcessCount}` observed, peak RSS/private `{report.Summary.ExternalProcessResources.PeakWorkingSetBytes}` / `{report.Summary.ExternalProcessResources.PeakPrivateMemoryBytes}` bytes, CPU `{report.Summary.ExternalProcessResources.TotalCpuTimeMilliseconds:F2}` ms, read/write transfer `{FormatNullable(report.Summary.ExternalProcessResources.TotalReadTransferBytesDelta)}` / `{FormatNullable(report.Summary.ExternalProcessResources.TotalWriteTransferBytesDelta)}` bytes");
        }
        text.AppendLine();
        text.AppendLine("| Cycle | Phase | Duration ms | Operations | Operations/sec | Allocated bytes | Gen0/1/2 | Peak RSS | Peak private | CPU ms | CPU % | Read/write transfer bytes |");
        text.AppendLine("| ---: | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | --- |");
        foreach (var cycle in report.Cycles)
        {
            foreach (var phase in cycle.Phases)
            {
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {cycle.Cycle} | {phase.Name} | {phase.DurationMilliseconds:F2} | {phase.Operations} | {phase.OperationsPerSecond:F2} | {phase.AllocatedBytes} | {phase.Gen0Collections}/{phase.Gen1Collections}/{phase.Gen2Collections} | {phase.PeakWorkingSetBytes} | {FormatNullable(phase.PeakPrivateMemoryBytes)} | {FormatNullable(phase.CpuTimeMilliseconds, "F2")} | {FormatNullable(phase.CpuUtilizationPercent, "F2")} | {FormatNullable(phase.ReadTransferBytesDelta)} / {FormatNullable(phase.WriteTransferBytesDelta)} |"));
            }
        }

        AppendLatencySummary(text, "Recovery latency", report.Summary.RecoveryLatency);
        AppendLatencySummary(text, "Query latency", report.Summary.QueryLatency);

        if (report.Summary.Integrity is { } integrity)
        {
            text.AppendLine();
            text.AppendLine("## Integrity summary");
            text.AppendLine();
            text.AppendLine($"- Scope: `{integrity.Scope}`");
            text.AppendLine($"- Expected / observed: `{integrity.ExpectedPoints}` / `{integrity.ObservedPoints}`");
            text.AppendLine($"- Missing / duplicate / unexpected / value mismatch: `{integrity.MissingPoints}` / `{integrity.DuplicatePoints}` / `{integrity.UnexpectedPoints}` / `{integrity.ValueMismatches}`");
            text.AppendLine($"- Digest match: `{integrity.DigestMatches}`");
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
                text.AppendLine($"  - Parent resource delta: managed `{phase.ManagedMemoryDeltaBytes}` bytes, RSS `{FormatNullable(phase.WorkingSetDeltaBytes)}` bytes, private `{FormatNullable(phase.PrivateMemoryDeltaBytes)}` bytes, read/write operations `{FormatNullable(phase.ReadOperationDelta)}` / `{FormatNullable(phase.WriteOperationDelta)}`, read/write transfer `{FormatNullable(phase.ReadTransferBytesDelta)}` / `{FormatNullable(phase.WriteTransferBytesDelta)}` bytes");
                foreach (SoakProcessResourceContribution contribution in phase.ProcessResourceContributions)
                {
                    text.AppendLine($"  - Child `{contribution.Scope}`: samples `{contribution.SampleCount}`, peak RSS/private `{FormatNullable(contribution.PeakWorkingSetBytes)}` / `{FormatNullable(contribution.PeakPrivateMemoryBytes)}` bytes, CPU `{FormatNullable(contribution.CpuTimeMilliseconds, "F2")}` ms, read/write transfer `{FormatNullable(contribution.ReadTransferBytesDelta)}` / `{FormatNullable(contribution.WriteTransferBytesDelta)}` bytes, exited `{contribution.ProcessExited}`");
                }
                foreach (MaintenanceChaosReservationEvidence reservation in phase.MaintenanceChaosReservations)
                {
                    text.AppendLine($"  - Restart reservation `{reservation.Restart}`: `{reservation.StartInclusive}`..`{reservation.EndInclusive}`, acknowledged through `{reservation.AcknowledgedThroughInclusive}`");
                }
            }
        }

        text.AppendLine();
        text.AppendLine("## Effective configuration");
        text.AppendLine();
        foreach (SoakConfigurationEvidence configuration in report.EffectiveConfiguration)
        {
            string settings = string.Join(", ", configuration.Settings.Select(static item => $"{item.Key}={item.Value}"));
            text.AppendLine($"- `{configuration.Scope}` from `{configuration.Source}`: durability `{configuration.DurabilityMode}`, WAL sync `{FormatNullable(configuration.SyncWalOnEveryWrite)}`, WAL OS flush `{FormatNullable(configuration.FlushWalToOsOnWrite)}`, segment fsync `{FormatNullable(configuration.SegmentFsyncOnCommit)}`, background flush `{FormatNullable(configuration.BackgroundFlushEnabled)}`, compaction `{FormatNullable(configuration.CompactionEnabled)}`, retention `{FormatNullable(configuration.RetentionEnabled)}`; {settings}");
        }

        text.AppendLine();
        text.AppendLine("## Capacity boundary");
        text.AppendLine();
        text.AppendLine("### What this profile improves or validates");
        text.AppendLine();
        foreach (string item in report.Summary.CapacityBoundary.Validates)
            text.AppendLine($"- {item}");
        text.AppendLine();
        text.AppendLine("### What this profile does not improve or prove");
        text.AppendLine();
        foreach (string item in report.Summary.CapacityBoundary.DoesNotProve)
            text.AppendLine($"- {item}");

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

    private static void AppendLatencySummary(
        StringBuilder text,
        string title,
        SoakLatencySummary? latency)
    {
        if (latency is null)
            return;

        text.AppendLine();
        text.AppendLine($"## {title}");
        text.AppendLine();
        text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Samples: `{latency.Samples}`, min/P50/P95/P99/max: `{latency.MinimumMilliseconds:F2}` / `{latency.P50Milliseconds:F2}` / `{latency.P95Milliseconds:F2}` / `{latency.P99Milliseconds:F2}` / `{latency.MaximumMilliseconds:F2}` ms"));
    }

    private static string FormatNullable(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";

    private static string FormatNullable(bool? value) => value?.ToString() ?? "unavailable";

    private static string FormatNullable(double? value, string format)
        => value?.ToString(format, CultureInfo.InvariantCulture) ?? "unavailable";

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
    public required int Series { get; init; }
    public required int TargetSegments { get; init; }
    public required int PointsPerSegment { get; init; }
    public required int RestartCount { get; init; }
    public required int RecoverySamples { get; init; }
    public required int QuerySamples { get; init; }
    public required int MaintenanceBatches { get; init; }
    public required int PointsPerBatch { get; init; }
    public required int DropMeasurements { get; init; }
    public required int RandomSeed { get; init; }
    public required bool KeepData { get; init; }

    public bool IsSpecializedProfile => Profile is
        "high-cardinality" or "small-segments" or "maintenance-chaos" or "many-measurements";

    public SoakReportOptions ToReportOptions() => new(
        Cycles,
        RelationalRows,
        Measurements,
        PointsPerMeasurement,
        CacheEntries,
        CacheTtlMilliseconds,
        MultipartParts,
        MultipartPartBytes,
        Series,
        TargetSegments,
        PointsPerSegment,
        RestartCount,
        RecoverySamples,
        QuerySamples,
        MaintenanceBatches,
        PointsPerBatch,
        DropMeasurements,
        RandomSeed);

    public static SoakOptions Parse(string[] args)
    {
        string profile = (ReadValue(args, "--profile") ?? "quick").ToLowerInvariant();
        ProfileDefaults defaults = profile switch
        {
            "quick" => new ProfileDefaults(1, 100, 64, 16, 100, 100, 3, 64 * 1024),
            "ci" => new ProfileDefaults(2, 1_000, 1_000, 100, 1_000, 200, 4, 1024 * 1024),
            "soak" => new ProfileDefaults(10, 10_000, 10_000, 1_000, 100_000, 500, 8, 5 * 1024 * 1024),
            "high-cardinality" => new ProfileDefaults(1, 1, 1, 1, 1, 1, 1, 1),
            "small-segments" => new ProfileDefaults(1, 1, 1, 1, 1, 1, 1, 1),
            "maintenance-chaos" => new ProfileDefaults(1, 1, 1, 1, 1, 1, 1, 1),
            "many-measurements" => new ProfileDefaults(1, 1, 10_000, 1, 1, 1, 1, 1),
            _ => throw new ArgumentException(
                "--profile 必须是 quick、ci、soak、high-cardinality、small-segments、maintenance-chaos 或 many-measurements。"),
        };
        SpecializedProfileDefaults specialized = SpecializedProfileDefaults.For(profile);

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
            Series = PositiveInt(args, "--series", specialized.Series),
            TargetSegments = PositiveInt(args, "--target-segments", specialized.TargetSegments),
            PointsPerSegment = PositiveInt(args, "--points-per-segment", specialized.PointsPerSegment),
            RestartCount = PositiveInt(args, "--restart-count", specialized.RestartCount),
            RecoverySamples = PositiveInt(args, "--recovery-samples", specialized.RecoverySamples),
            QuerySamples = PositiveInt(args, "--query-samples", specialized.QuerySamples),
            MaintenanceBatches = PositiveInt(args, "--maintenance-batches", specialized.MaintenanceBatches),
            PointsPerBatch = PositiveInt(args, "--points-per-batch", specialized.PointsPerBatch),
            DropMeasurements = PositiveInt(args, "--drop-measurements", specialized.DropMeasurements),
            RandomSeed = PositiveInt(args, "--random-seed", specialized.RandomSeed),
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

internal sealed record SpecializedProfileDefaults(
    int Series,
    int TargetSegments,
    int PointsPerSegment,
    int RestartCount,
    int RecoverySamples,
    int QuerySamples,
    int MaintenanceBatches,
    int PointsPerBatch,
    int DropMeasurements,
    int RandomSeed)
{
    public static SpecializedProfileDefaults For(string profile) => profile switch
    {
        "high-cardinality" => new(1_000_000, 1, 1, 1, 5, 100, 1, 4_096, 1, 125),
        "small-segments" => new(32, 10_000, 1, 1, 5, 100, 1, 1, 1, 125),
        "maintenance-chaos" => new(64, 1, 1, 20, 20, 100, 4, 64, 1, 125),
        "many-measurements" => new(1, 100, 1, 1, 5, 100, 1, 100, 100, 125),
        _ => new(1, 1, 1, 1, 1, 1, 1, 1, 1, 125),
    };
}

internal sealed record PhaseMeasurement(
    long Operations,
    IReadOnlyDictionary<string, string> Details,
    SoakIntegritySummary? Integrity,
    IReadOnlyList<double> RecoveryLatencySamplesMilliseconds,
    IReadOnlyList<double> QueryLatencySamplesMilliseconds)
{
    /// <summary>执行阶段实际使用的引擎配置；未采集时为空而非推断默认值。</summary>
    public IReadOnlyList<SoakConfigurationEvidence> EffectiveConfiguration { get; init; } = [];

    /// <summary>由阶段创建的外部子进程资源快照；不与当前进程指标混合。</summary>
    public IReadOnlyList<SoakProcessResourceContribution> ProcessResourceContributions { get; init; } = [];

    /// <summary>维护混沌阶段每次重启的已保留序列范围；非该阶段为空。</summary>
    public IReadOnlyList<MaintenanceChaosReservationEvidence> MaintenanceChaosReservations { get; init; } = [];
}

internal sealed record SoakReportOptions(
    int Cycles,
    int RelationalRows,
    int Measurements,
    int PointsPerMeasurement,
    int CacheEntries,
    int CacheTtlMilliseconds,
    int MultipartParts,
    int MultipartPartBytes,
    int Series,
    int TargetSegments,
    int PointsPerSegment,
    int RestartCount,
    int RecoverySamples,
    int QuerySamples,
    int MaintenanceBatches,
    int PointsPerBatch,
    int DropMeasurements,
    int RandomSeed);

internal sealed record SoakEnvironment(
    string Os,
    string Framework,
    string Architecture,
    string MachineName,
    int ProcessorCount,
    long AvailableMemoryBytes,
    string CommitSha,
    SoakDiskSnapshot Disk,
    SoakHostHardware Hardware,
    SoakRuntimeProvenance Provenance)
{
    /// <summary>采集跨平台可用的运行环境、硬件和进程来源信息。</summary>
    public static SoakEnvironment Capture(string workRoot, string commitSha)
        => new(
            SoakEvidenceText.Normalize(RuntimeInformation.OSDescription),
            SoakEvidenceText.Normalize(RuntimeInformation.FrameworkDescription),
            RuntimeInformation.ProcessArchitecture.ToString(),
            SoakEvidenceText.Normalize(Environment.MachineName),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            commitSha,
            SoakDiskSnapshot.Capture(workRoot),
            SoakHostHardware.Capture(),
            SoakRuntimeProvenance.Capture());
}

internal sealed record SoakHostHardware(
    string ProcessorModel,
    string ProcessorModelSource,
    string OperatingSystemArchitecture,
    bool Is64BitOperatingSystem,
    int LogicalProcessorCount,
    int SystemPageSizeBytes,
    long GcTotalAvailableMemoryBytes,
    bool IsServerGarbageCollector,
    string GcLatencyMode)
{
    /// <summary>收集无需特权即可读取的主机硬件和 GC 限额信息。</summary>
    public static SoakHostHardware Capture()
    {
        (string processorModel, string processorModelSource) = ResolveProcessorModel();
        return new SoakHostHardware(
            processorModel,
            processorModelSource,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.Is64BitOperatingSystem,
            Environment.ProcessorCount,
            Environment.SystemPageSize,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString());
    }

    private static (string Model, string Source) ResolveProcessorModel()
    {
        if (OperatingSystem.IsWindows())
        {
            string? processorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(processorIdentifier))
                return (SoakEvidenceText.Normalize(processorIdentifier), "environment:PROCESSOR_IDENTIFIER");
        }

        if (OperatingSystem.IsLinux())
        {
            string? processorModel = TryReadLinuxProcessorModel();
            if (!string.IsNullOrWhiteSpace(processorModel))
                return (processorModel, "/proc/cpuinfo");
        }

        if (OperatingSystem.IsMacOS())
        {
            string? processorModel = TryReadMacProcessorModel();
            if (!string.IsNullOrWhiteSpace(processorModel))
                return (processorModel, "sysctl:machdep.cpu.brand_string");
        }

        return (RuntimeInformation.ProcessArchitecture.ToString(), "runtime:process-architecture");
    }

    private static string? TryReadLinuxProcessorModel()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo").Take(256))
            {
                int separator = line.IndexOf(':');
                if (separator <= 0)
                    continue;

                string key = line[..separator].Trim();
                if (key is "model name" or "Hardware" or "Processor" or "cpu model")
                    return SoakEvidenceText.Normalize(line[(separator + 1)..]);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return null;
    }

    private static string? TryReadMacProcessorModel()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "sysctl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("machdep.cpu.brand_string");

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return null;

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(2).TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return null;
            }

            _ = error.GetAwaiter().GetResult();
            return process.ExitCode == 0
                ? SoakEvidenceText.Normalize(output.GetAwaiter().GetResult())
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

internal sealed record SoakRuntimeProvenance(
    string RuntimeIdentifier,
    string ProcessExecutable,
    int ProcessId,
    DateTimeOffset? ProcessStartedUtc,
    bool Is64BitProcess,
    string ContainerState)
{
    /// <summary>采集进程来源和容器探测结果；<c>none</c> 仅表示未发现已知容器标记，不构成宿主机证明。</summary>
    public static SoakRuntimeProvenance Capture()
    {
        DateTimeOffset? processStartedUtc = null;
        try
        {
            using Process process = Process.GetCurrentProcess();
            processStartedUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }

        string processPath = Environment.ProcessPath ?? string.Empty;
        string processExecutable = string.IsNullOrWhiteSpace(processPath)
            ? "UNAVAILABLE"
            : SoakEvidenceText.Normalize(Path.GetFileName(processPath));
        return new SoakRuntimeProvenance(
            SoakEvidenceText.Normalize(RuntimeInformation.RuntimeIdentifier),
            processExecutable,
            Environment.ProcessId,
            processStartedUtc,
            Environment.Is64BitProcess,
            DetectContainerState());
    }

    private static string DetectContainerState()
    {
        foreach (string variable in new[] { "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_RUNNING_IN_CONTAINERS" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.Ordinal))
            {
                return "DECLARED_BY_" + variable;
            }
        }

        if (!OperatingSystem.IsLinux())
            return "none";

        if (File.Exists("/.dockerenv"))
            return "DETECTED_BY_DOCKERENV";
        if (File.Exists("/run/.containerenv"))
            return "DETECTED_BY_CONTAINERENV";

        try
        {
            string cgroup = File.ReadAllText("/proc/1/cgroup");
            if (cgroup.Contains("docker", StringComparison.OrdinalIgnoreCase)
                || cgroup.Contains("containerd", StringComparison.OrdinalIgnoreCase)
                || cgroup.Contains("kubepods", StringComparison.OrdinalIgnoreCase))
            {
                return "DETECTED_BY_CGROUP";
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }

        // 固定硬件 verifier 将 `none` 定义为没有发现容器标记的唯一可发布状态。
        // 该值是探测结果，不替代受保护 runner 与环境审批的硬件真实性证明。
        return "none";
    }
}

internal sealed record SoakDiskSnapshot(
    string Root,
    string FileSystem,
    long TotalBytes,
    long AvailableBytes,
    string DriveType,
    string VolumeLabel,
    string DeviceModel,
    string DeviceModelSource)
{
    /// <summary>工作目录实际所在的文件系统挂载点；Linux 上由 findmnt 解析。</summary>
    public string MountPoint { get; init; } = "UNAVAILABLE";

    /// <summary>工作目录挂载的 source（例如设备、LVM 或网络源）。</summary>
    public string MountSource { get; init; } = "UNAVAILABLE";

    /// <summary>挂载设备的 major:minor 标识；无法解析时为 UNAVAILABLE。</summary>
    public string MountDeviceId { get; init; } = "UNAVAILABLE";

    /// <summary>由 findmnt、DriveInfo 等实际解析路径的来源。</summary>
    public string ResolutionSource { get; init; } = "unavailable";

    /// <summary>从工作目录所在卷读取总容量和可用空间快照。</summary>
    public static SoakDiskSnapshot Capture(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return Unavailable("UNAVAILABLE");
        }

        (string deviceModel, string deviceModelSource) = ResolveDeviceModel();
        SoakMountResolution mount = SoakMountResolution.Resolve(fullPath);
        string root = mount.MountPoint;
        try
        {
            var drive = new DriveInfo(root);
            string volumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? (OperatingSystem.IsWindows() ? "UNAVAILABLE" : "NOT_APPLICABLE")
                : SoakEvidenceText.Normalize(drive.VolumeLabel);
            return new SoakDiskSnapshot(
                root,
                SoakEvidenceText.Normalize(drive.DriveFormat),
                drive.TotalSize,
                drive.AvailableFreeSpace,
                drive.DriveType.ToString(),
                volumeLabel,
                deviceModel,
                deviceModelSource)
            {
                MountPoint = mount.MountPoint,
                MountSource = mount.MountSource,
                MountDeviceId = mount.DeviceId,
                ResolutionSource = mount.Source,
            };
        }
        catch (IOException)
        {
            return Unavailable(root, deviceModel, deviceModelSource, mount);
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(root, deviceModel, deviceModelSource, mount);
        }
        catch (NotSupportedException)
        {
            return Unavailable(root, deviceModel, deviceModelSource, mount);
        }
        catch (System.Security.SecurityException)
        {
            return Unavailable(root, deviceModel, deviceModelSource, mount);
        }
        catch (ArgumentException)
        {
            return Unavailable(root, deviceModel, deviceModelSource, mount);
        }
    }

    private static SoakDiskSnapshot Unavailable(
        string root,
        string deviceModel = "UNAVAILABLE",
        string deviceModelSource = "unavailable",
        SoakMountResolution? mount = null)
        => new(root, "UNAVAILABLE", -1, -1, "UNAVAILABLE", "UNAVAILABLE", deviceModel, deviceModelSource)
        {
            MountPoint = mount?.MountPoint ?? root,
            MountSource = mount?.MountSource ?? "UNAVAILABLE",
            MountDeviceId = mount?.DeviceId ?? "UNAVAILABLE",
            ResolutionSource = mount?.Source ?? "unavailable",
        };

    private static (string DeviceModel, string Source) ResolveDeviceModel()
    {
        foreach (string variable in new[] { "SONNETDB_M19_STORAGE_MODEL", "SONNETDB_M19_DISK_MODEL" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                return (SoakEvidenceText.Normalize(value), "environment:" + variable);
        }

        return ("UNDECLARED", "unconfigured");
    }
}

/// <summary>工作目录所在挂载点的最小可审计身份。</summary>
internal sealed record SoakMountResolution(
    string MountPoint,
    string MountSource,
    string DeviceId,
    string Source)
{
    /// <summary>解析工作路径所在 mount；Linux 优先使用 findmnt，其他平台使用驱动器根。</summary>
    public static SoakMountResolution Resolve(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (OperatingSystem.IsLinux())
        {
            string? output = RunCommand(
                "findmnt",
                "--noheadings",
                "--raw",
                "--target",
                fullPath,
                "--output",
                "TARGET,SOURCE,FSTYPE,MAJ:MIN");
            if (!string.IsNullOrWhiteSpace(output))
            {
                string[] fields = output.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 4)
                {
                    string mountPoint = DecodeMountField(fields[0]);
                    string source = DecodeMountField(fields[1]);
                    string deviceId = DecodeMountField(fields[3]);
                    if (!string.IsNullOrWhiteSpace(mountPoint)
                        && !string.IsNullOrWhiteSpace(source)
                        && !string.IsNullOrWhiteSpace(deviceId))
                    {
                        return new SoakMountResolution(mountPoint, source, deviceId, "findmnt");
                    }
                }
            }

            return new SoakMountResolution("UNAVAILABLE", "UNAVAILABLE", "UNAVAILABLE", "findmnt-unavailable");
        }

        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return new SoakMountResolution("UNAVAILABLE", "UNAVAILABLE", "UNAVAILABLE", "path-root-unavailable");

        return new SoakMountResolution(root, root, "UNAVAILABLE", "driveinfo-path-root");
    }

    private static string DecodeMountField(string value)
        => value.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);

    private static string? RunCommand(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return null;

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(2).TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return null;
            }

            return process.ExitCode == 0
                ? outputTask.GetAwaiter().GetResult()
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

internal sealed record SoakPhaseResult(
    string Name,
    double DurationMilliseconds,
    long Operations,
    double OperationsPerSecond,
    long ManagedMemoryBytes,
    long PeakManagedMemoryBytes,
    long PeakWorkingSetBytes,
    long ManagedMemoryDeltaBytes,
    long AllocatedBytes,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections,
    long? PeakPrivateMemoryBytes,
    long? WorkingSetDeltaBytes,
    long? PrivateMemoryDeltaBytes,
    double? CpuTimeMilliseconds,
    double? CpuUtilizationPercent,
    long? ReadOperationDelta,
    long? WriteOperationDelta,
    long? ReadTransferBytesDelta,
    long? WriteTransferBytesDelta,
    SoakIntegritySummary? Integrity,
    IReadOnlyList<double> RecoveryLatencySamplesMilliseconds,
    IReadOnlyList<double> QueryLatencySamplesMilliseconds,
    IReadOnlyDictionary<string, string> Details,
    IReadOnlyList<SoakConfigurationEvidence> EffectiveConfiguration,
    IReadOnlyList<SoakProcessResourceContribution> ProcessResourceContributions,
    IReadOnlyList<MaintenanceChaosReservationEvidence> MaintenanceChaosReservations);

/// <summary>maintenance-chaos 单次 worker 重启保留的连续序列租约及其 progress 确认上界。</summary>
internal sealed record MaintenanceChaosReservationEvidence(
    int Restart,
    long StartInclusive,
    long EndInclusive,
    long AcknowledgedThroughInclusive);

internal sealed record SoakCycleResult(
    int Cycle,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    IReadOnlyList<SoakPhaseResult> Phases);

internal sealed record EcosystemSoakReport(
    int SchemaVersion,
    SoakEvidenceMetadata Evidence,
    string Profile,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    bool Succeeded,
    string? Failure,
    SoakReportOptions Options,
    SoakEnvironment Environment,
    SoakTargetHardware TargetHardware,
    IReadOnlyList<SoakConfigurationEvidence> EffectiveConfiguration,
    IReadOnlyList<SoakCycleResult> Cycles,
    SoakReportSummary Summary);

internal sealed record SoakTargetHardware(
    string Status,
    string Id,
    string Contract,
    string DeclarationSource)
{
    /// <summary>读取外部固定目标机声明；未显式声明时保持 NOT_READY。</summary>
    public static SoakTargetHardware Capture()
    {
        string? status = Environment.GetEnvironmentVariable("SONNETDB_M19_TARGET_HARDWARE_STATUS");
        string? id = Environment.GetEnvironmentVariable("SONNETDB_M19_TARGET_HARDWARE_ID");
        string? contract = Environment.GetEnvironmentVariable("SONNETDB_M19_TARGET_HARDWARE_CONTRACT");
        bool configured = !string.IsNullOrWhiteSpace(status)
            || !string.IsNullOrWhiteSpace(id)
            || !string.IsNullOrWhiteSpace(contract);
        return new SoakTargetHardware(
            Read(status, "NOT_READY"),
            Read(id, "UNDECLARED"),
            Read(contract, "M19-#125-frozen-target-v1"),
            configured ? "environment" : "unconfigured");
    }

    private static string Read(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value)
            ? fallback
            : SoakEvidenceText.Normalize(value);
}

internal sealed record SoakSourceRevision(
    string Value,
    string Source,
    string WorkingTreeState);

internal sealed record SoakEvidenceMetadata(
    string Schema,
    string Contract,
    string RunId,
    string Generator,
    string GeneratorVersion,
    string SourceRevision,
    string SourceRevisionSource,
    string WorkingTreeState,
    DateTimeOffset GeneratedUtc,
    string WorkloadFingerprint,
    string ExecutionEnvironment,
    string? CiRunId,
    string? CiAttempt)
{
    /// <summary>建立可关联报告、源码版本和固定输入形状的版本化证据元数据。</summary>
    public static SoakEvidenceMetadata Create(
        SoakOptions options,
        DateTimeOffset startedUtc,
        DateTimeOffset finishedUtc,
        SoakSourceRevision sourceRevision)
    {
        string executionEnvironment = ResolveExecutionEnvironment();
        return new SoakEvidenceMetadata(
            "sonnetdb.ecosystem-soak.report",
            "M19-#125-capacity-evidence-v2",
            CreateRunId(startedUtc, finishedUtc),
            typeof(Program).Assembly.GetName().Name ?? "SonnetDB.EcosystemSoak",
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "UNAVAILABLE",
            sourceRevision.Value,
            sourceRevision.Source,
            sourceRevision.WorkingTreeState,
            DateTimeOffset.UtcNow,
            CreateWorkloadFingerprint(options),
            executionEnvironment,
            ReadOptional("GITHUB_RUN_ID", "BUILD_BUILDID"),
            ReadOptional("GITHUB_RUN_ATTEMPT", "SYSTEM_JOBATTEMPT"));
    }

    private static string CreateRunId(DateTimeOffset startedUtc, DateTimeOffset finishedUtc)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{startedUtc:yyyyMMddTHHmmss.fffffffZ}-{finishedUtc:HHmmss.fffffffZ}-{Guid.NewGuid():N}");

    private static string CreateWorkloadFingerprint(SoakOptions options)
    {
        string[] fields =
        [
            "profile=" + options.Profile,
            "cycles=" + options.Cycles.ToString(CultureInfo.InvariantCulture),
            "relationalRows=" + options.RelationalRows.ToString(CultureInfo.InvariantCulture),
            "measurements=" + options.Measurements.ToString(CultureInfo.InvariantCulture),
            "pointsPerMeasurement=" + options.PointsPerMeasurement.ToString(CultureInfo.InvariantCulture),
            "cacheEntries=" + options.CacheEntries.ToString(CultureInfo.InvariantCulture),
            "cacheTtlMilliseconds=" + options.CacheTtlMilliseconds.ToString(CultureInfo.InvariantCulture),
            "multipartParts=" + options.MultipartParts.ToString(CultureInfo.InvariantCulture),
            "multipartPartBytes=" + options.MultipartPartBytes.ToString(CultureInfo.InvariantCulture),
            "series=" + options.Series.ToString(CultureInfo.InvariantCulture),
            "targetSegments=" + options.TargetSegments.ToString(CultureInfo.InvariantCulture),
            "pointsPerSegment=" + options.PointsPerSegment.ToString(CultureInfo.InvariantCulture),
            "restartCount=" + options.RestartCount.ToString(CultureInfo.InvariantCulture),
            "recoverySamples=" + options.RecoverySamples.ToString(CultureInfo.InvariantCulture),
            "querySamples=" + options.QuerySamples.ToString(CultureInfo.InvariantCulture),
            "maintenanceBatches=" + options.MaintenanceBatches.ToString(CultureInfo.InvariantCulture),
            "pointsPerBatch=" + options.PointsPerBatch.ToString(CultureInfo.InvariantCulture),
            "dropMeasurements=" + options.DropMeasurements.ToString(CultureInfo.InvariantCulture),
            "randomSeed=" + options.RandomSeed.ToString(CultureInfo.InvariantCulture),
        ];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', fields)))).ToLowerInvariant();
    }

    private static string ResolveExecutionEnvironment()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return "github-actions";
        if (string.Equals(Environment.GetEnvironmentVariable("TF_BUILD"), "true", StringComparison.OrdinalIgnoreCase))
            return "azure-pipelines";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI")))
            return "generic-ci";
        return "local-or-unknown";
    }

    private static string? ReadOptional(params string[] names)
    {
        foreach (string name in names)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return SoakEvidenceText.Normalize(value);
        }

        return null;
    }
}

internal sealed record SoakConfigurationEvidence(
    string Scope,
    string Source,
    string DurabilityMode,
    bool? SyncWalOnEveryWrite,
    bool? FlushWalToOsOnWrite,
    bool? SegmentFsyncOnCommit,
    bool? BackgroundFlushEnabled,
    bool? CompactionEnabled,
    bool? RetentionEnabled,
    IReadOnlyDictionary<string, string> Settings)
{
    /// <summary>供报告去重使用的稳定键，不属于 JSON evidence 合同。</summary>
    [JsonIgnore]
    public string StableKey => string.Join(
        '\u001F',
        Scope,
        Source,
        DurabilityMode,
        SyncWalOnEveryWrite?.ToString(CultureInfo.InvariantCulture) ?? "null",
        FlushWalToOsOnWrite?.ToString(CultureInfo.InvariantCulture) ?? "null",
        SegmentFsyncOnCommit?.ToString(CultureInfo.InvariantCulture) ?? "null",
        BackgroundFlushEnabled?.ToString(CultureInfo.InvariantCulture) ?? "null",
        CompactionEnabled?.ToString(CultureInfo.InvariantCulture) ?? "null",
        RetentionEnabled?.ToString(CultureInfo.InvariantCulture) ?? "null",
        string.Join('\u001E', Settings.OrderBy(static item => item.Key, StringComparer.Ordinal).Select(static item => item.Key + "=" + item.Value)));

    /// <summary>从实际 <see cref="TsdbOptions"/> 投影持久性和后台维护的有效配置。</summary>
    public static SoakConfigurationEvidence FromTsdbOptions(
        string scope,
        string source,
        TsdbOptions options,
        IReadOnlyDictionary<string, string>? additionalSettings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(options);

        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["walBufferBytes"] = options.WalBufferSize.ToString(CultureInfo.InvariantCulture),
            ["walGroupCommitEnabled"] = options.WalGroupCommit.Enabled.ToString(CultureInfo.InvariantCulture),
            ["walGroupCommitFlushWindowTicks"] = options.WalGroupCommit.FlushWindow.Ticks.ToString(CultureInfo.InvariantCulture),
            ["flushMaxPoints"] = options.FlushPolicy.MaxPoints.ToString(CultureInfo.InvariantCulture),
            ["flushMaxBytes"] = options.FlushPolicy.MaxBytes.ToString(CultureInfo.InvariantCulture),
            ["flushHardCapBytes"] = options.FlushPolicy.ResolveHardCapBytes().ToString(CultureInfo.InvariantCulture),
            ["flushMaxAgeTicks"] = options.FlushPolicy.MaxAge.Ticks.ToString(CultureInfo.InvariantCulture),
            ["backgroundFlushPollIntervalTicks"] = options.BackgroundFlush.PollInterval.Ticks.ToString(CultureInfo.InvariantCulture),
            ["compactionMinTierSize"] = options.Compaction.MinTierSize.ToString(CultureInfo.InvariantCulture),
            ["compactionTierSizeRatio"] = options.Compaction.TierSizeRatio.ToString(CultureInfo.InvariantCulture),
            ["compactionFirstTierMaxBytes"] = options.Compaction.FirstTierMaxBytes.ToString(CultureInfo.InvariantCulture),
            ["compactionPollIntervalTicks"] = options.Compaction.PollInterval.Ticks.ToString(CultureInfo.InvariantCulture),
            ["retentionTtlTicks"] = options.Retention.Ttl.Ticks.ToString(CultureInfo.InvariantCulture),
            ["retentionTtlTimestampUnits"] = options.Retention.TtlInTimestampUnits?.ToString(CultureInfo.InvariantCulture) ?? "auto-milliseconds",
            ["retentionPollIntervalTicks"] = options.Retention.PollInterval.Ticks.ToString(CultureInfo.InvariantCulture),
            ["retentionMaxTombstonesPerRound"] = options.Retention.MaxTombstonesPerRound.ToString(CultureInfo.InvariantCulture),
            ["segmentBufferBytes"] = options.SegmentWriterOptions.BufferSize.ToString(CultureInfo.InvariantCulture),
            ["segmentTimestampEncoding"] = options.SegmentWriterOptions.TimestampEncoding.ToString(),
            ["segmentValueEncoding"] = options.SegmentWriterOptions.ValueEncoding.ToString(),
        };
        if (additionalSettings is not null)
        {
            foreach ((string key, string value) in additionalSettings)
                settings[key] = value;
        }

        return new SoakConfigurationEvidence(
            scope,
            source,
            ResolveDurabilityMode(options),
            options.SyncWalOnEveryWrite,
            options.FlushWalToOsOnWrite,
            options.SegmentWriterOptions.FsyncOnCommit,
            options.BackgroundFlush.Enabled,
            options.Compaction.Enabled,
            options.Retention.Enabled,
            settings);
    }

    /// <summary>记录运行参数而不把未由当前阶段采集的引擎默认值伪装为有效配置。</summary>
    public static SoakConfigurationEvidence CreateInvocation(SoakOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile"] = options.Profile,
            ["cycles"] = options.Cycles.ToString(CultureInfo.InvariantCulture),
            ["series"] = options.Series.ToString(CultureInfo.InvariantCulture),
            ["measurements"] = options.Measurements.ToString(CultureInfo.InvariantCulture),
            ["pointsPerMeasurement"] = options.PointsPerMeasurement.ToString(CultureInfo.InvariantCulture),
            ["targetSegments"] = options.TargetSegments.ToString(CultureInfo.InvariantCulture),
            ["pointsPerSegment"] = options.PointsPerSegment.ToString(CultureInfo.InvariantCulture),
            ["restartCount"] = options.RestartCount.ToString(CultureInfo.InvariantCulture),
            ["recoverySamples"] = options.RecoverySamples.ToString(CultureInfo.InvariantCulture),
            ["querySamples"] = options.QuerySamples.ToString(CultureInfo.InvariantCulture),
            ["maintenanceBatches"] = options.MaintenanceBatches.ToString(CultureInfo.InvariantCulture),
            ["pointsPerBatch"] = options.PointsPerBatch.ToString(CultureInfo.InvariantCulture),
            ["dropMeasurements"] = options.DropMeasurements.ToString(CultureInfo.InvariantCulture),
            ["randomSeed"] = options.RandomSeed.ToString(CultureInfo.InvariantCulture),
        };
        return new SoakConfigurationEvidence(
            "runner-invocation",
            "SoakOptions.Parse",
            "not-an-engine-durability-assertion",
            null,
            null,
            null,
            null,
            null,
            null,
            settings);
    }

    private static string ResolveDurabilityMode(TsdbOptions options)
    {
        if (options.SyncWalOnEveryWrite)
            return "fsync-on-every-write";
        if (options.FlushWalToOsOnWrite)
            return "flush-to-os-on-every-write";
        return "buffered-until-flush-or-dispose";
    }
}

internal static class SoakEvidenceText
{
    /// <summary>压缩环境来源文本并限制长度，避免换行或不受控输入破坏报告结构。</summary>
    public static string Normalize(string? value, string fallback = "UNAVAILABLE")
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }
}

internal sealed record SoakIntegritySummary(
    string Scope,
    long ExpectedPoints,
    long ObservedPoints,
    long MissingPoints,
    long DuplicatePoints,
    long UnexpectedPoints,
    long ValueMismatches,
    bool DigestMatches);

internal sealed record SoakLatencySummary(
    int Samples,
    double MinimumMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds)
{
    /// <summary>按 nearest-rank 规则汇总延迟样本。</summary>
    public static SoakLatencySummary? Create(IEnumerable<double> samples)
    {
        double[] sorted = samples.Order().ToArray();
        if (sorted.Length == 0)
            return null;

        return new SoakLatencySummary(
            sorted.Length,
            sorted[0],
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1]);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}

internal sealed record SoakCapacityBoundary(
    IReadOnlyList<string> Validates,
    IReadOnlyList<string> DoesNotProve);

internal sealed record SoakReportSummary(
    long PeakWorkingSetBytes,
    long PeakManagedMemoryBytes,
    SoakResourceSummary Resources,
    SoakExternalProcessResourceSummary ExternalProcessResources,
    SoakIntegritySummary? Integrity,
    SoakLatencySummary? RecoveryLatency,
    SoakLatencySummary? QueryLatency,
    SoakCapacityBoundary CapacityBoundary)
{
    /// <summary>从全部阶段聚合资源峰值、完整性、分位数和容量边界。</summary>
    public static SoakReportSummary Create(
        SoakOptions options,
        IReadOnlyList<SoakCycleResult> cycles)
    {
        SoakPhaseResult[] phases = cycles.SelectMany(static cycle => cycle.Phases).ToArray();
        SoakIntegritySummary[] integrity = phases
            .Select(static phase => phase.Integrity)
            .OfType<SoakIntegritySummary>()
            .ToArray();

        SoakIntegritySummary? aggregate = integrity.Length == 0
            ? null
            : new SoakIntegritySummary(
                string.Join(",", integrity.Select(static item => item.Scope).Distinct(StringComparer.Ordinal)),
                integrity.Sum(static item => item.ExpectedPoints),
                integrity.Sum(static item => item.ObservedPoints),
                integrity.Sum(static item => item.MissingPoints),
                integrity.Sum(static item => item.DuplicatePoints),
                integrity.Sum(static item => item.UnexpectedPoints),
                integrity.Sum(static item => item.ValueMismatches),
                integrity.All(static item => item.DigestMatches));

        return new SoakReportSummary(
            phases.Select(static phase => phase.PeakWorkingSetBytes).DefaultIfEmpty().Max(),
            phases.Select(static phase => phase.PeakManagedMemoryBytes).DefaultIfEmpty().Max(),
            SoakResourceSummary.Create(phases),
            SoakExternalProcessResourceSummary.Create(phases.SelectMany(static phase => phase.ProcessResourceContributions)),
            aggregate,
            SoakLatencySummary.Create(phases.SelectMany(static phase => phase.RecoveryLatencySamplesMilliseconds)),
            SoakLatencySummary.Create(phases.SelectMany(static phase => phase.QueryLatencySamplesMilliseconds)),
            BuildCapacityBoundary(options.Profile));
    }

    private static SoakCapacityBoundary BuildCapacityBoundary(string profile) => profile switch
    {
        "high-cardinality" => new(
            [
                "验证大量 tag 组合下 catalog、倒排 tag index、目录持久化与 reopen 成本。",
                "用确定性抽样核对 series、timestamp 与 value，并报告查询和恢复分位数。",
            ],
            [
                "不验证海量 segment、后台 compaction I/O 或服务端多租户并发。",
                "抽样点校验不能替代对百万 series 全量逐点扫描。",
            ]),
        "small-segments" => new(
            [
                "验证主动多次 flush 后大量小 segment 的发布、枚举、查询、完整性与 reopen 成本。",
                "量化 #124 增量发布之后仍然存在的段数量、解码与恢复成本。",
            ],
            [
                "#124 不会减少 segment 文件数量，也不会消除 compaction I/O。",
                "禁用 compaction 的专项结果不能代表启用后台维护后的稳态段数量。",
            ]),
        "maintenance-chaos" => new(
            [
                "验证后台 flush、compaction、retention 并发期间的确定性随机 kill/reopen。",
                "按已确认序列核对缺失、重复、额外点和值，并报告多轮恢复分位数。",
            ],
            [
                "Process.Kill 不是内核崩溃或整机掉电模型。",
                "固定种子与给定规模的通过结果不是无限运行稳定性证明。",
            ]),
        "many-measurements" => new(
            [
                "验证大量 measurement 下目录枚举、备份扫描、drop、retention 与 reopen。",
                "报告 measurement/segment/备份文件规模及恢复分位数。",
            ],
            [
                "每个 measurement 的低 series 数不能代表单 measurement 高基数。",
                "嵌入式结果不包含 HTTP、认证、租户隔离或远程客户端开销。",
            ]),
        _ => new(
            ["验证 EF、时序、KV、对象、迁移、崩溃恢复组合路径在给定规模下可重复通过。"],
            ["quick/ci 数字不是生产容量、服务端 SLA 或长期稳定性结论。"]),
    };
}

internal sealed record SoakResourceSummary(
    long TotalAllocatedBytes,
    long TotalGen0Collections,
    long TotalGen1Collections,
    long TotalGen2Collections,
    long PeakPrivateMemoryBytes,
    long? MaximumWorkingSetDeltaBytes,
    long? MaximumPrivateMemoryDeltaBytes,
    double TotalCpuTimeMilliseconds,
    double TotalMeasuredDurationMilliseconds,
    double? AverageCpuUtilizationPercent,
    long? TotalReadOperationDelta,
    long? TotalWriteOperationDelta,
    long? TotalReadTransferBytesDelta,
    long? TotalWriteTransferBytesDelta)
{
    /// <summary>从父进程阶段采样聚合分配、GC、内存和 CPU 指标。</summary>
    public static SoakResourceSummary Create(IEnumerable<SoakPhaseResult> phases)
    {
        SoakPhaseResult[] materialized = phases.ToArray();
        long? maximumWorkingSetDelta = MaxNullable(materialized.Select(static phase => phase.WorkingSetDeltaBytes));
        long? maximumPrivateMemoryDelta = MaxNullable(materialized.Select(static phase => phase.PrivateMemoryDeltaBytes));
        double totalCpuMilliseconds = materialized.Sum(static phase => phase.CpuTimeMilliseconds ?? 0d);
        double totalDurationMilliseconds = materialized.Sum(static phase => phase.DurationMilliseconds);
        double? averageCpuUtilization = totalDurationMilliseconds > 0d && Environment.ProcessorCount > 0
            ? totalCpuMilliseconds / (totalDurationMilliseconds * Environment.ProcessorCount) * 100d
            : null;
        return new SoakResourceSummary(
            materialized.Sum(static phase => phase.AllocatedBytes),
            materialized.Sum(static phase => phase.Gen0Collections),
            materialized.Sum(static phase => phase.Gen1Collections),
            materialized.Sum(static phase => phase.Gen2Collections),
            materialized.Select(static phase => phase.PeakPrivateMemoryBytes ?? 0).DefaultIfEmpty().Max(),
            maximumWorkingSetDelta,
            maximumPrivateMemoryDelta,
            totalCpuMilliseconds,
            totalDurationMilliseconds,
            averageCpuUtilization,
            SumAllOrNull(materialized.Select(static phase => phase.ReadOperationDelta)),
            SumAllOrNull(materialized.Select(static phase => phase.WriteOperationDelta)),
            SumAllOrNull(materialized.Select(static phase => phase.ReadTransferBytesDelta)),
            SumAllOrNull(materialized.Select(static phase => phase.WriteTransferBytesDelta)));
    }

    private static long? MaxNullable(IEnumerable<long?> values)
    {
        long? maximum = null;
        foreach (long? value in values)
        {
            if (value.HasValue && (!maximum.HasValue || value.Value > maximum.Value))
                maximum = value;
        }

        return maximum;
    }

    private static long? SumAllOrNull(IEnumerable<long?> values)
    {
        long total = 0;
        bool any = false;
        foreach (long? value in values)
        {
            if (!value.HasValue)
                return null;

            total = checked(total + value.Value);
            any = true;
        }

        return any ? total : null;
    }

}

internal sealed record SoakExternalProcessResourceSummary(
    int ProcessCount,
    long PeakWorkingSetBytes,
    long PeakPrivateMemoryBytes,
    double TotalCpuTimeMilliseconds,
    double TotalObservedElapsedMilliseconds,
    int TotalSamples,
    int ExitedProcessCount,
    long? TotalReadOperationDelta,
    long? TotalWriteOperationDelta,
    long? TotalReadTransferBytesDelta,
    long? TotalWriteTransferBytesDelta)
{
    /// <summary>聚合阶段记录的子进程资源快照；CPU 时间和观察时长可能因并发进程重叠。</summary>
    public static SoakExternalProcessResourceSummary Create(IEnumerable<SoakProcessResourceContribution> contributions)
    {
        SoakProcessResourceContribution[] materialized = contributions.ToArray();
        return new SoakExternalProcessResourceSummary(
            materialized.Length,
            materialized.Select(static item => item.PeakWorkingSetBytes ?? 0).DefaultIfEmpty().Max(),
            materialized.Select(static item => item.PeakPrivateMemoryBytes ?? 0).DefaultIfEmpty().Max(),
            materialized.Sum(static item => item.CpuTimeMilliseconds ?? 0d),
            materialized.Sum(static item => item.ElapsedMilliseconds),
            materialized.Sum(static item => item.SampleCount),
            materialized.Count(static item => item.ProcessExited),
            SumAllOrNull(materialized.Select(static item => item.ReadOperationDelta)),
            SumAllOrNull(materialized.Select(static item => item.WriteOperationDelta)),
            SumAllOrNull(materialized.Select(static item => item.ReadTransferBytesDelta)),
            SumAllOrNull(materialized.Select(static item => item.WriteTransferBytesDelta)));
    }

    private static long? SumAllOrNull(IEnumerable<long?> values)
    {
        long total = 0;
        bool any = false;
        foreach (long? value in values)
        {
            if (!value.HasValue)
                return null;

            total = checked(total + value.Value);
            any = true;
        }

        return any ? total : null;
    }
}

internal sealed record ProcessResourceSnapshot(
    long? InitialWorkingSetBytes,
    long? FinalObservedWorkingSetBytes,
    long? PeakWorkingSetBytes,
    long? WorkingSetDeltaBytes,
    long? InitialPrivateMemoryBytes,
    long? FinalObservedPrivateMemoryBytes,
    long? PeakPrivateMemoryBytes,
    long? PrivateMemoryDeltaBytes,
    double? CpuTimeMilliseconds,
    double ElapsedMilliseconds,
    double? CpuUtilizationPercent,
    long? ReadOperationDelta,
    long? WriteOperationDelta,
    long? ReadTransferBytesDelta,
    long? WriteTransferBytesDelta,
    int SampleCount,
    bool ProcessExited);

internal sealed record SoakProcessResourceContribution(
    string Scope,
    long? InitialWorkingSetBytes,
    long? FinalObservedWorkingSetBytes,
    long? PeakWorkingSetBytes,
    long? WorkingSetDeltaBytes,
    long? InitialPrivateMemoryBytes,
    long? FinalObservedPrivateMemoryBytes,
    long? PeakPrivateMemoryBytes,
    long? PrivateMemoryDeltaBytes,
    double? CpuTimeMilliseconds,
    double ElapsedMilliseconds,
    double? CpuUtilizationPercent,
    long? ReadOperationDelta,
    long? WriteOperationDelta,
    long? ReadTransferBytesDelta,
    long? WriteTransferBytesDelta,
    int SampleCount,
    bool ProcessExited)
{
    /// <summary>把外部进程监控快照附加到所属阶段，不与父进程资源计数混合。</summary>
    public static SoakProcessResourceContribution FromSnapshot(string scope, ProcessResourceSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SoakProcessResourceContribution(
            scope,
            snapshot.InitialWorkingSetBytes,
            snapshot.FinalObservedWorkingSetBytes,
            snapshot.PeakWorkingSetBytes,
            snapshot.WorkingSetDeltaBytes,
            snapshot.InitialPrivateMemoryBytes,
            snapshot.FinalObservedPrivateMemoryBytes,
            snapshot.PeakPrivateMemoryBytes,
            snapshot.PrivateMemoryDeltaBytes,
            snapshot.CpuTimeMilliseconds,
            snapshot.ElapsedMilliseconds,
            snapshot.CpuUtilizationPercent,
            snapshot.ReadOperationDelta,
            snapshot.WriteOperationDelta,
            snapshot.ReadTransferBytesDelta,
            snapshot.WriteTransferBytesDelta,
            snapshot.SampleCount,
            snapshot.ProcessExited);
    }
}

/// <summary>以低频轮询收集任意进程的 CPU、RSS 和 private-memory 快照。</summary>
internal sealed class ProcessResourceMonitor : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(25);
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Process _process;
    private readonly bool _ownsProcess;
    private readonly Thread _thread;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private ProcessResourceReading _initial;
    private ProcessResourceReading _latest;
    private long? _peakWorkingSetBytes;
    private long? _peakPrivateMemoryBytes;
    private int _sampleCount;
    private bool _processExited;
    private int _stopped;

    /// <summary>开始监控已启动或当前正在运行的进程。</summary>
    /// <param name="process">要监控的进程。</param>
    /// <param name="ownsProcess">是否由监控器在释放时一并释放 <paramref name="process"/> 对象。</param>
    public ProcessResourceMonitor(Process process, bool ownsProcess = false)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
        _ownsProcess = ownsProcess;
        _initial = CaptureReading();
        _latest = _initial;
        RecordReading(_initial);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SonnetDB-EcosystemSoak-ProcessResourceMonitor",
        };
        _thread.Start();
    }

    /// <summary>返回最后可读取的资源快照；进程已经退出时保留退出前的最后一次成功采样。</summary>
    public ProcessResourceSnapshot Snapshot
    {
        get
        {
            if (Volatile.Read(ref _stopped) == 0)
                Sample();

            lock (_gate)
            {
                double elapsedMilliseconds = _elapsed.Elapsed.TotalMilliseconds;
                double? cpuTimeMilliseconds = DifferenceMilliseconds(_initial.TotalProcessorTime, _latest.TotalProcessorTime);
                double? cpuUtilizationPercent = cpuTimeMilliseconds.HasValue && elapsedMilliseconds > 0 && Environment.ProcessorCount > 0
                    ? cpuTimeMilliseconds.Value / (elapsedMilliseconds * Environment.ProcessorCount) * 100d
                    : null;
                return new ProcessResourceSnapshot(
                    _initial.WorkingSetBytes,
                    _latest.WorkingSetBytes,
                    _peakWorkingSetBytes,
                    Difference(_initial.WorkingSetBytes, _latest.WorkingSetBytes),
                    _initial.PrivateMemoryBytes,
                    _latest.PrivateMemoryBytes,
                    _peakPrivateMemoryBytes,
                    Difference(_initial.PrivateMemoryBytes, _latest.PrivateMemoryBytes),
                    cpuTimeMilliseconds,
                    elapsedMilliseconds,
                    cpuUtilizationPercent,
                    Difference(_initial.ReadOperationCount, _latest.ReadOperationCount),
                    Difference(_initial.WriteOperationCount, _latest.WriteOperationCount),
                    Difference(_initial.ReadTransferBytes, _latest.ReadTransferBytes),
                    Difference(_initial.WriteTransferBytes, _latest.WriteTransferBytes),
                    _sampleCount,
                    _processExited);
            }
        }
    }

    /// <summary>停止轮询并获取最终可读取的进程资源样本。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _stop.Set();
        _thread.Join();
        Sample();
        _elapsed.Stop();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        if (_ownsProcess)
            _process.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        while (!_stop.Wait(SampleInterval))
            Sample();
    }

    private void Sample()
    {
        ProcessResourceReading reading = CaptureReading();
        lock (_gate)
            RecordReading(reading);
    }

    private ProcessResourceReading CaptureReading()
    {
        bool exited = TryReadProcessExited();
        if (!exited)
            TryRefresh();

        ProcessIoCounters? ioCounters = TryReadProcessIoCounters();

        return new ProcessResourceReading(
            TryReadLong(static process => process.WorkingSet64, _process),
            TryReadLong(static process => process.PrivateMemorySize64, _process),
            TryReadTimeSpan(static process => process.TotalProcessorTime, _process),
            ioCounters?.ReadOperationCount,
            ioCounters?.WriteOperationCount,
            ioCounters?.ReadTransferBytes,
            ioCounters?.WriteTransferBytes,
            exited);
    }

    private ProcessIoCounters? TryReadProcessIoCounters()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            string path = $"/proc/{_process.Id.ToString(CultureInfo.InvariantCulture)}/io";
            long? readOperations = null;
            long? writeOperations = null;
            long? readTransferBytes = null;
            long? writeTransferBytes = null;
            foreach (string line in File.ReadLines(path))
            {
                int separator = line.IndexOf(':');
                if (separator <= 0
                    || !long.TryParse(
                        line[(separator + 1)..].Trim(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out long value))
                {
                    continue;
                }

                switch (line[..separator])
                {
                    case "syscr":
                        readOperations = value;
                        break;
                    case "syscw":
                        writeOperations = value;
                        break;
                    case "rchar":
                        readTransferBytes = value;
                        break;
                    case "wchar":
                        writeTransferBytes = value;
                        break;
                }
            }

            return readOperations.HasValue
                && writeOperations.HasValue
                && readTransferBytes.HasValue
                && writeTransferBytes.HasValue
                ? new ProcessIoCounters(
                    readOperations.Value,
                    writeOperations.Value,
                    readTransferBytes.Value,
                    writeTransferBytes.Value)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private void RecordReading(ProcessResourceReading reading)
    {
        _processExited |= reading.ProcessExited;
        if (!reading.HasMetrics)
            return;

        // A just-started Linux child can expose /proc/<pid>/io a moment after its
        // Process object becomes observable. Use the first readable value as the
        // baseline for each independent counter; otherwise a transient startup
        // miss would make the entire phase permanently report unknown I/O.
        _initial = _initial with
        {
            WorkingSetBytes = _initial.WorkingSetBytes ?? reading.WorkingSetBytes,
            PrivateMemoryBytes = _initial.PrivateMemoryBytes ?? reading.PrivateMemoryBytes,
            TotalProcessorTime = _initial.TotalProcessorTime ?? reading.TotalProcessorTime,
            ReadOperationCount = _initial.ReadOperationCount ?? reading.ReadOperationCount,
            WriteOperationCount = _initial.WriteOperationCount ?? reading.WriteOperationCount,
            ReadTransferBytes = _initial.ReadTransferBytes ?? reading.ReadTransferBytes,
            WriteTransferBytes = _initial.WriteTransferBytes ?? reading.WriteTransferBytes,
        };

        // A process can remain queryable while /proc/<pid>/io is momentarily
        // unavailable. Preserve the last usable value for each independent
        // counter instead of turning a valid phase delta into an unknown one.
        _latest = _latest with
        {
            WorkingSetBytes = reading.WorkingSetBytes ?? _latest.WorkingSetBytes,
            PrivateMemoryBytes = reading.PrivateMemoryBytes ?? _latest.PrivateMemoryBytes,
            TotalProcessorTime = reading.TotalProcessorTime ?? _latest.TotalProcessorTime,
            ReadOperationCount = reading.ReadOperationCount ?? _latest.ReadOperationCount,
            WriteOperationCount = reading.WriteOperationCount ?? _latest.WriteOperationCount,
            ReadTransferBytes = reading.ReadTransferBytes ?? _latest.ReadTransferBytes,
            WriteTransferBytes = reading.WriteTransferBytes ?? _latest.WriteTransferBytes,
            ProcessExited = reading.ProcessExited,
        };
        _sampleCount++;
        UpdateMaximum(ref _peakWorkingSetBytes, reading.WorkingSetBytes);
        UpdateMaximum(ref _peakPrivateMemoryBytes, reading.PrivateMemoryBytes);
    }

    private bool TryReadProcessExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private void TryRefresh()
    {
        try
        {
            _process.Refresh();
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static long? TryReadLong(Func<Process, long> read, Process process)
    {
        try
        {
            return read(process);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static TimeSpan? TryReadTimeSpan(Func<Process, TimeSpan> read, Process process)
    {
        try
        {
            return read(process);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static long? Difference(long? start, long? end)
        => start.HasValue && end.HasValue ? end.Value - start.Value : null;

    private static double? DifferenceMilliseconds(TimeSpan? start, TimeSpan? end)
        => start.HasValue && end.HasValue
            ? Math.Max(0d, (end.Value - start.Value).TotalMilliseconds)
            : null;

    private static void UpdateMaximum(ref long? maximum, long? value)
    {
        if (value.HasValue && (!maximum.HasValue || value.Value > maximum.Value))
            maximum = value;
    }

    private sealed record ProcessResourceReading(
        long? WorkingSetBytes,
        long? PrivateMemoryBytes,
        TimeSpan? TotalProcessorTime,
        long? ReadOperationCount,
        long? WriteOperationCount,
        long? ReadTransferBytes,
        long? WriteTransferBytes,
        bool ProcessExited)
    {
        public bool HasMetrics => WorkingSetBytes.HasValue
            || PrivateMemoryBytes.HasValue
            || TotalProcessorTime.HasValue
            || ReadOperationCount.HasValue
            || WriteOperationCount.HasValue
            || ReadTransferBytes.HasValue
            || WriteTransferBytes.HasValue;
    }

    private sealed record ProcessIoCounters(
        long ReadOperationCount,
        long WriteOperationCount,
        long ReadTransferBytes,
        long WriteTransferBytes);
}

internal sealed class PhaseResourceMonitor : IDisposable
{
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly ProcessResourceMonitor _processMonitor;
    private readonly Thread _thread;
    private readonly long _startingManagedMemoryBytes;
    private readonly long _startingAllocatedBytes;
    private readonly long _startingGen0Collections;
    private readonly long _startingGen1Collections;
    private readonly long _startingGen2Collections;
    private int _stopped;
    private long _managedMemoryBytes;
    private long _peakManagedMemoryBytes;
    private long _allocatedBytes;
    private long _gen0Collections;
    private long _gen1Collections;
    private long _gen2Collections;

    public PhaseResourceMonitor()
    {
        _processMonitor = new ProcessResourceMonitor(Process.GetCurrentProcess(), ownsProcess: true);
        _startingManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
        _startingAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _startingGen0Collections = GC.CollectionCount(0);
        _startingGen1Collections = GC.CollectionCount(1);
        _startingGen2Collections = GC.CollectionCount(2);
        _managedMemoryBytes = _startingManagedMemoryBytes;
        _peakManagedMemoryBytes = _startingManagedMemoryBytes;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SonnetDB-EcosystemSoak-ManagedResourceMonitor",
        };
        _thread.Start();
    }

    public long ManagedMemoryBytes => Interlocked.Read(ref _managedMemoryBytes);

    public long PeakManagedMemoryBytes => Interlocked.Read(ref _peakManagedMemoryBytes);

    public long PeakWorkingSetBytes => ProcessResources.PeakWorkingSetBytes ?? 0;

    public long ManagedMemoryDeltaBytes => ManagedMemoryBytes - _startingManagedMemoryBytes;

    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

    public long Gen0Collections => Interlocked.Read(ref _gen0Collections);

    public long Gen1Collections => Interlocked.Read(ref _gen1Collections);

    public long Gen2Collections => Interlocked.Read(ref _gen2Collections);

    public ProcessResourceSnapshot ProcessResources => _processMonitor.Snapshot;

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _stop.Set();
        _thread.Join();
        SampleManagedMemory();
        Interlocked.Exchange(ref _allocatedBytes, Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _startingAllocatedBytes));
        Interlocked.Exchange(ref _gen0Collections, Math.Max(0, (long)GC.CollectionCount(0) - _startingGen0Collections));
        Interlocked.Exchange(ref _gen1Collections, Math.Max(0, (long)GC.CollectionCount(1) - _startingGen1Collections));
        Interlocked.Exchange(ref _gen2Collections, Math.Max(0, (long)GC.CollectionCount(2) - _startingGen2Collections));
        _processMonitor.Stop();
    }

    public void Dispose()
    {
        Stop();
        _processMonitor.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        while (!_stop.Wait(TimeSpan.FromMilliseconds(25)))
            SampleManagedMemory();
    }

    private void SampleManagedMemory()
    {
        long managedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
        Interlocked.Exchange(ref _managedMemoryBytes, managedMemoryBytes);
        UpdateMaximum(ref _peakManagedMemoryBytes, managedMemoryBytes);
    }

    private static void UpdateMaximum(ref long location, long value)
    {
        long current = Interlocked.Read(ref location);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EcosystemSoakReport))]
[JsonSerializable(typeof(SoakEvidenceMetadata))]
[JsonSerializable(typeof(SoakReportOptions))]
[JsonSerializable(typeof(SoakEnvironment))]
[JsonSerializable(typeof(SoakHostHardware))]
[JsonSerializable(typeof(SoakRuntimeProvenance))]
[JsonSerializable(typeof(SoakDiskSnapshot))]
[JsonSerializable(typeof(SoakTargetHardware))]
[JsonSerializable(typeof(SoakConfigurationEvidence))]
[JsonSerializable(typeof(SoakCycleResult))]
[JsonSerializable(typeof(SoakPhaseResult))]
[JsonSerializable(typeof(MaintenanceChaosReservationEvidence))]
[JsonSerializable(typeof(SoakProcessResourceContribution))]
[JsonSerializable(typeof(SoakIntegritySummary))]
[JsonSerializable(typeof(SoakLatencySummary))]
[JsonSerializable(typeof(SoakCapacityBoundary))]
[JsonSerializable(typeof(SoakReportSummary))]
[JsonSerializable(typeof(SoakResourceSummary))]
[JsonSerializable(typeof(SoakExternalProcessResourceSummary))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<double>))]
internal sealed partial class EcosystemSoakJsonContext : JsonSerializerContext;
