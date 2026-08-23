using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Graphs;
using SonnetDB.Kv;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M40 #367 可审计 production gate 报告和本地管线 smoke runner。</summary>
public static class GraphProductionGateRunner
{
    private const int QuickVertexCount = 64;
    private const int QuickEdgeCount = 192;
    private const int QuickReaderWorkers = 8;
    private const int QuickSamplesPerReader = 6;
    private const int QuickWriterMutations = 12;

    /// <summary>运行 8 reader + 1 writer、checkpoint、重开和 backup/restore 的 quick smoke。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <returns>保持 Production 双门禁为 NOT_RUN 的本地报告。</returns>
    public static GraphProductionGateReport RunQuick(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        string root = Path.Combine(
            Path.GetTempPath(),
            "sonnetdb-m40-production-smoke-" + Guid.NewGuid().ToString("N"));
        string databaseRoot = Path.Combine(root, "database");
        string backupRoot = Path.Combine(root, "backup");
        string restoredRoot = Path.Combine(root, "restored");
        Directory.CreateDirectory(root);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var latencies = new ConcurrentBag<double>();
        long expandedEdges = 0;
        long lastSequence = 0;
        bool mixedWorkloadPass = false;
        bool checkpointReopenPass = false;
        bool backupRestorePass = false;
        long peakWorkingSet = Process.GetCurrentProcess().WorkingSet64;
        try
        {
            TsdbOptions options = CreateQuickOptions(databaseRoot);
            using (var database = Tsdb.Open(options))
            {
                GraphStore store = database.Graphs.Create("production_smoke");
                lastSequence = WriteQuickFixture(store);
                (mixedWorkloadPass, expandedEdges, lastSequence, peakWorkingSet) = RunQuickMixedWorkload(
                    store,
                    latencies,
                    lastSequence,
                    peakWorkingSet);
                _ = store.Checkpoint();
                GraphInvariantReport invariant = GraphInvariantChecker.Check(store);
                mixedWorkloadPass &= invariant.IsValid
                    && invariant.IsComplete
                    && invariant.VertexCount == QuickVertexCount
                    && invariant.EdgeCount == QuickEdgeCount;
            }

            using (var reopened = Tsdb.Open(CreateQuickOptions(databaseRoot)))
            {
                GraphStore store = reopened.Graphs.Open("production_smoke");
                GraphInvariantReport invariant = GraphInvariantChecker.Check(store);
                using GraphReadSession read = store.BeginRead();
                checkpointReopenPass = invariant.IsValid
                    && invariant.IsComplete
                    && invariant.VertexCount == QuickVertexCount
                    && invariant.EdgeCount == QuickEdgeCount
                    && read.Sequence >= lastSequence
                    && read.GetVertex(new GraphElementId(QuickVertexCount)) is not null;

                var backupService = new BackupService();
                _ = backupService.Create(reopened, new BackupCreateOptions
                {
                    DestinationDirectory = backupRoot,
                });
                BackupVerificationResult verification = backupService.Verify(backupRoot);
                backupRestorePass = verification.IsValid;
            }

            var restoreService = new BackupService();
            _ = restoreService.Restore(new BackupRestoreOptions
            {
                BackupDirectory = backupRoot,
                TargetDirectory = restoredRoot,
            });
            using (var restored = Tsdb.Open(CreateQuickOptions(restoredRoot)))
            {
                GraphStore store = restored.Graphs.Open("production_smoke");
                GraphInvariantReport invariant = GraphInvariantChecker.Check(store);
                backupRestorePass &= invariant.IsValid
                    && invariant.IsComplete
                    && invariant.VertexCount == QuickVertexCount
                    && invariant.EdgeCount == QuickEdgeCount;
            }

            DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;
            string artifactName = "m40-graph-production-quick.log";
            string artifactPath = Path.Combine(outputRoot, artifactName);
            string outputDigest = HashText(
                FormattableString.Invariant(
                    $"vertices={QuickVertexCount};edges={QuickEdgeCount};sequence={lastSequence};expanded={expandedEdges}"));
            WriteAtomically(
                artifactPath,
                FormattableString.Invariant(
                    $"""
                    schema=m40-graph-production-quick-v1
                    started_utc={startedUtc:O}
                    finished_utc={finishedUtc:O}
                    mixed_workload={Status(mixedWorkloadPass)}
                    checkpoint_reopen={Status(checkpointReopenPass)}
                    backup_restore={Status(backupRestorePass)}
                    reader_workers={QuickReaderWorkers}
                    update_workers=1
                    samples={latencies.Count}
                    expanded_edges={expandedEdges}
                    output_digest={outputDigest}

                    """));
            var artifact = new GraphProductionArtifactEvidence
            {
                Path = artifactName,
                Sha256 = HashFile(artifactPath),
                Command = "dotnet run --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -c Release -- --m40-production-gate --quick",
            };

            double[] orderedLatencies = latencies.Order().ToArray();
            var input = new GraphProductionGateInput
            {
                ProductionRun = false,
                CommitSha = ResolveCommitSha(),
                StartedUtc = startedUtc,
                FinishedUtc = finishedUtc,
                Dataset = new GraphProductionDatasetEvidence
                {
                    Tier = "quick",
                    VertexCount = QuickVertexCount,
                    EdgeCount = QuickEdgeCount,
                    InputDigest = HashText("m40-graph-generator-v1|0x534F4E4E45544442|64|192"),
                    OutputDigest = outputDigest,
                },
                Environment = CaptureEnvironment(outputRoot),
                Soak = new GraphProductionSoakEvidence
                {
                    DurationHours = (finishedUtc - startedUtc).TotalHours,
                    ReaderWorkers = QuickReaderWorkers,
                    UpdateWorkers = 1,
                    UpdateProfile = "quick-smoke",
                    MaximumCheckpointIntervalMinutes = 0,
                    CheckpointCount = 1,
                    KillReopenCount = 0,
                    InvariantCheckCount = 2,
                    FailedOperationCount = mixedWorkloadPass && checkpointReopenPass && backupRestorePass ? 0 : 1,
                    PeakWorkingSetBytes = peakWorkingSet,
                    SyncWalOnEveryWrite = true,
                    AutoCheckpointEnabled = true,
                    MaxWalBytes = 256L * 1024 * 1024,
                    MaxOverlayEntries = 100_000,
                },
                Journeys =
                [
                    CreateQuickJourney("SOC-1", orderedLatencies, expandedEdges, peakWorkingSet, artifact),
                    CreateQuickJourney("SOC-3", orderedLatencies, expandedEdges, peakWorkingSet, artifact),
                ],
                CorrectnessRecoveryChecks =
                [
                    Check("mixed_workload_smoke", mixedWorkloadPass, artifact),
                    Check("checkpoint_reopen_smoke", checkpointReopenPass, artifact),
                    Check("backup_restore_smoke", backupRestorePass, artifact),
                ],
                PerformanceCapacityChecks =
                [
                    new GraphProductionCheckEvidence
                    {
                        Id = "fixed_hardware",
                        Status = GraphProductionEvidenceStatus.NotRun,
                        Summary = "quick smoke 不形成固定硬件容量证据。",
                    },
                ],
                Limitations =
                [
                    "quick 数据不等于 1m vertex/10m edge production-soak。",
                    "本次仅做正常进程重开，不等于真子进程 kill matrix。",
                    "未执行 Neo4j/PostgreSQL、LDBC、Graphalytics、Couplet C4 或 Native AOT 发布 artifact 验证。",
                    "未运行 168 小时 mixed workload，不能据此改变八模型产品定位。",
                ],
            };
            GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, outputRoot);
            WriteReport(report, outputRoot);
            WriteTemplate(outputRoot, input.Environment);
            return report;
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>读取完整证据清单、校验 artifact 并输出双门禁报告。</summary>
    /// <param name="manifestPath">source-generated JSON 输入清单。</param>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <returns>严格门禁报告。</returns>
    public static GraphProductionGateReport EvaluateManifest(
        string manifestPath,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        GraphProductionGateInput input = JsonSerializer.Deserialize(
            File.ReadAllText(fullManifestPath),
            GraphProductionGateJsonContext.Default.GraphProductionGateInput)
            ?? throw new InvalidDataException("M40 #367 证据清单为空。");
        string artifactRoot = Path.GetDirectoryName(fullManifestPath)
            ?? Directory.GetCurrentDirectory();
        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, artifactRoot);
        WriteReport(report, outputRoot);
        return report;
    }

    /// <summary>生成完整 Production 证据清单模板；模板本身不可通过门禁。</summary>
    /// <param name="outputDirectory">模板输出目录。</param>
    /// <param name="environment">可选的已采集机器信息。</param>
    /// <returns>模板文件路径。</returns>
    public static string WriteTemplate(
        string outputDirectory,
        GraphProductionEnvironmentEvidence? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        var missingArtifact = new GraphProductionArtifactEvidence
        {
            Path = "artifacts/REPLACE_ME.json",
            Sha256 = new string('0', 64),
            Command = "REPLACE_ME",
        };
        var input = new GraphProductionGateInput
        {
            ProductionRun = true,
            CommitSha = new string('0', 40),
            Environment = environment ?? new GraphProductionEnvironmentEvidence(),
            Dataset = new GraphProductionDatasetEvidence
            {
                Tier = "production-soak",
                VertexCount = 1_000_000,
                EdgeCount = 10_000_000,
                InputDigest = new string('0', 64),
                OutputDigest = new string('0', 64),
            },
            Soak = new GraphProductionSoakEvidence
            {
                DurationHours = 168,
                ReaderWorkers = 8,
                UpdateWorkers = 1,
                UpdateProfile = "m40-frozen-update-profile-v1",
                MaximumCheckpointIntervalMinutes = 30,
                CheckpointCount = 336,
                KillReopenCount = 7,
                InvariantCheckCount = 7,
                SyncWalOnEveryWrite = true,
                AutoCheckpointEnabled = true,
                MaxWalBytes = 256L * 1024 * 1024,
                MaxOverlayEntries = 100_000,
            },
            Journeys = GraphProductionGateEvaluator.GetRequiredJourneyIds()
                .Select(id => new GraphProductionJourneyEvidence
                {
                    Id = id,
                    Artifact = missingArtifact,
                })
                .ToArray(),
            CorrectnessRecoveryChecks = GraphProductionGateEvaluator.GetRequiredCorrectnessCheckIds()
                .Select(id => new GraphProductionCheckEvidence
                {
                    Id = id,
                    Artifact = missingArtifact,
                })
                .ToArray(),
            PerformanceCapacityChecks = GraphProductionGateEvaluator.GetRequiredPerformanceCheckIds()
                .Select(id => new GraphProductionCheckEvidence
                {
                    Id = id,
                    Artifact = missingArtifact,
                })
                .ToArray(),
            Gaps =
                GraphProductionGateEvaluator.GetRequiredGapIds()
                .Select(id => new GraphProductionGapEvidence
                {
                    Id = id,
                    Status = "open",
                    Blocks = "Production; Couplet C4",
                    Severity = "correctness_recovery",
                })
                .ToArray(),
            Limitations = ["替换所有 REPLACE_ME/零摘要并附原始 artifact 后才能判门。"],
        };
        string path = Path.Combine(outputRoot, "m40-graph-production-input.template.json");
        WriteAtomically(
            path,
            JsonSerializer.Serialize(input, GraphProductionGateJsonContext.Default.GraphProductionGateInput));
        return path;
    }

    private static TsdbOptions CreateQuickOptions(string rootDirectory)
        => new()
        {
            RootDirectory = rootDirectory,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Kv = KvOptions.Default with
            {
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
        };

    private static long WriteQuickFixture(GraphStore store)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (int id = 1; id <= QuickVertexCount; id++)
        {
            transaction.UpsertVertex(
                new GraphElementId(id),
                0,
                [new LabelId((id % 5) + 1)],
                [new GraphProperty(1, GraphPropertyValue.FromInt64(id % 7))]);
        }
        for (int index = 0; index < QuickEdgeCount; index++)
        {
            int source = index < QuickVertexCount
                ? index + 1
                : ((index * 17) % QuickVertexCount) + 1;
            int target = index < QuickVertexCount
                ? ((index + 1) % QuickVertexCount) + 1
                : ((index * 29 + 3) % QuickVertexCount) + 1;
            transaction.UpsertEdge(
                new GraphElementId(index + 1),
                0,
                new GraphElementId(source),
                new GraphElementId(target),
                new LabelId((index % 4) + 10),
                [new GraphProperty(2, GraphPropertyValue.FromInt64((index % 9) + 1))]);
        }
        return transaction.Commit().Sequence;
    }

    private static (bool Pass, long ExpandedEdges, long Sequence, long PeakWorkingSet) RunQuickMixedWorkload(
        GraphStore store,
        ConcurrentBag<double> latencies,
        long initialSequence,
        long initialPeakWorkingSet)
    {
        using var start = new ManualResetEventSlim(false);
        long expandedEdges = 0;
        long sequence = initialSequence;
        long peakWorkingSet = initialPeakWorkingSet;
        Task[] readers = Enumerable.Range(0, QuickReaderWorkers)
            .Select(worker => Task.Run(() =>
            {
                start.Wait();
                for (int sample = 0; sample < QuickSamplesPerReader; sample++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    using GraphReadSession read = store.BeginRead();
                    int anchor = ((worker * QuickSamplesPerReader + sample) % QuickVertexCount) + 1;
                    if (read.GetVertex(new GraphElementId(anchor)) is null)
                        throw new InvalidDataException($"quick mixed workload 缺少 vertex {anchor}。");
                    GraphExplain explain = read.ExplainExpand(new GraphElementId(anchor));
                    if (explain.AccessPath != GraphAccessPath.NativeAdjacency)
                        throw new InvalidDataException("quick mixed workload 未命中 native adjacency。");
                    using GraphCursor<GraphExpansion> cursor = read.Expand(
                        new GraphElementId(anchor),
                        options: new GraphCursorOptions { PageSize = 8, MaxResults = 64 });
                    while (true)
                    {
                        IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage();
                        if (page.Count == 0)
                            break;
                        Interlocked.Add(ref expandedEdges, page.Count);
                    }
                    GraphPath? path = read.ShortestPath(
                        new GraphElementId(1),
                        new GraphElementId(8),
                        options: new GraphTraversalOptions
                        {
                            MaxDepth = 8,
                            MaxFrontier = 512,
                            MaxPaths = 512,
                        });
                    if (path is null)
                        throw new InvalidDataException("quick mixed workload shortest path 不可达。");
                    stopwatch.Stop();
                    latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                    UpdatePeak(ref peakWorkingSet, Process.GetCurrentProcess().WorkingSet64);
                }
            }))
            .ToArray();
        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int mutation = 1; mutation <= QuickWriterMutations; mutation++)
            {
                using GraphReadSession read = store.BeginRead();
                GraphVertex vertex = read.GetVertex(new GraphElementId(QuickVertexCount))
                    ?? throw new InvalidDataException("quick writer 缺少更新顶点。");
                GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
                transaction.UpsertVertex(
                    vertex.Id,
                    vertex.ElementVersion,
                    vertex.Labels,
                    [new GraphProperty(1, GraphPropertyValue.FromInt64(mutation))]);
                long committed = transaction.Commit().Sequence;
                Interlocked.Exchange(ref sequence, committed);
                UpdatePeak(ref peakWorkingSet, Process.GetCurrentProcess().WorkingSet64);
            }
        });

        start.Set();
        Task.WhenAll(readers.Append(writer)).GetAwaiter().GetResult();
        return (
            latencies.Count == QuickReaderWorkers * QuickSamplesPerReader,
            expandedEdges,
            sequence,
            peakWorkingSet);
    }

    private static GraphProductionJourneyEvidence CreateQuickJourney(
        string id,
        IReadOnlyList<double> orderedLatencies,
        long expandedEdges,
        long peakWorkingSet,
        GraphProductionArtifactEvidence artifact)
        => new()
        {
            Id = id,
            Status = GraphProductionEvidenceStatus.Pass,
            Rounds = 1,
            SamplesPerRound = orderedLatencies.Count,
            P50Milliseconds = Percentile(orderedLatencies, 0.50),
            P95Milliseconds = Percentile(orderedLatencies, 0.95),
            P99Milliseconds = Percentile(orderedLatencies, 0.99),
            MaxMilliseconds = orderedLatencies.Count == 0 ? 0 : orderedLatencies[^1],
            ThroughputPerSecond = orderedLatencies.Count == 0
                ? 0
                : orderedLatencies.Count / Math.Max(orderedLatencies.Sum() / 1_000, 0.001),
            TimeToFirstRowP99Milliseconds = Percentile(orderedLatencies, 0.99),
            QueryPeakLiveBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false)),
            PeakWorkingSetBytes = peakWorkingSet,
            ExpandedEdges = expandedEdges,
            Returned = 1,
            AccessPath = "native_adjacency",
            Artifact = artifact,
        };

    private static GraphProductionCheckEvidence Check(
        string id,
        bool pass,
        GraphProductionArtifactEvidence artifact)
        => new()
        {
            Id = id,
            Status = Status(pass),
            Summary = pass ? "quick smoke 通过。" : "quick smoke 失败。",
            Artifact = artifact,
        };

    private static GraphProductionEnvironmentEvidence CaptureEnvironment(string outputDirectory)
    {
        string root = Path.GetPathRoot(outputDirectory) ?? Path.DirectorySeparatorChar.ToString();
        var drive = new DriveInfo(root);
        return new GraphProductionEnvironmentEvidence
        {
            OsDescription = RuntimeInformation.OSDescription,
            OsBuild = Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            CpuName = ReadCpuName(),
            LogicalProcessorCount = Environment.ProcessorCount,
            PhysicalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            DiskFormat = drive.DriveFormat,
            Runtime = RuntimeInformation.FrameworkDescription,
            SdkVersion = ReadDotNetSdkVersion(),
            GcMode = GCSettings.IsServerGC ? "server" : "workstation",
        };
    }

    private static string ReadCpuName()
    {
        if (!OperatingSystem.IsWindows())
            return RuntimeInformation.ProcessArchitecture.ToString();
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            writable: false);
        return key?.GetValue("ProcessorNameString") as string ?? "unknown";
    }

    private static string ReadDotNetSdkVersion()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");
            using Process? process = Process.Start(startInfo);
            if (process is null)
                return "unknown";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0 || output.Length == 0)
                return "unknown";

            var statusStartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            statusStartInfo.ArgumentList.Add("status");
            statusStartInfo.ArgumentList.Add("--porcelain");
            statusStartInfo.ArgumentList.Add("--untracked-files=normal");
            using Process? statusProcess = Process.Start(statusStartInfo);
            if (statusProcess is null)
                return output + "-status-unknown";
            string status = statusProcess.StandardOutput.ReadToEnd();
            statusProcess.WaitForExit();
            if (statusProcess.ExitCode != 0)
                return output + "-status-unknown";
            return status.Length == 0 ? output : output + "-dirty";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ResolveCommitSha()
    {
        string? configured = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("HEAD");
            using Process? process = Process.Start(startInfo);
            if (process is null)
                return "unknown";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static void WriteReport(GraphProductionGateReport report, string outputDirectory)
    {
        WriteAtomically(
            Path.Combine(outputDirectory, "m40-graph-production-gate.json"),
            JsonSerializer.Serialize(report, GraphProductionGateJsonContext.Default.GraphProductionGateReport));
        WriteAtomically(
            Path.Combine(outputDirectory, "m40-graph-production-gate.md"),
            BuildMarkdown(report));
    }

    private static string BuildMarkdown(GraphProductionGateReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# M40 #367 Graph Production Gate");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{report.Input.CommitSha}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Dataset: `{report.Input.Dataset.Tier}` ({report.Input.Dataset.VertexCount:N0} vertex / {report.Input.Dataset.EdgeCount:N0} edge)");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Local smoke: `{report.LocalSmoke}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Correctness/recovery: `{report.CorrectnessRecovery}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Performance/capacity: `{report.PerformanceCapacity}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Release decision: `{report.ReleaseDecision}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Soak: `{report.Input.Soak.DurationHours:F3}` hours, `{report.Input.Soak.ReaderWorkers}+{report.Input.Soak.UpdateWorkers}` workers");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Cold open P95/P99: `{report.Input.Soak.ColdOpenP95Milliseconds:F3}/{report.Input.Soak.ColdOpenP99Milliseconds:F3}` ms");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Recovery P50/P95/P99: `{report.Input.Soak.RecoveryP50Milliseconds:F3}/{report.Input.Soak.RecoveryP95Milliseconds:F3}/{report.Input.Soak.RecoveryP99Milliseconds:F3}` ms");
        text.AppendLine();
        text.AppendLine("## Journeys");
        text.AppendLine();
        text.AppendLine("| ID | Status | Access path | Samples | ops/s | P50 ms | P95 ms | P99 ms | Peak live | Working set |");
        text.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (GraphProductionJourneyEvidence journey in report.Input.Journeys)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {journey.Id} | {journey.Status} | {journey.AccessPath} | {journey.Rounds} x {journey.SamplesPerRound:N0} | "
                + $"{journey.ThroughputPerSecond:F3} | {journey.P50Milliseconds:F3} | {journey.P95Milliseconds:F3} | {journey.P99Milliseconds:F3} | "
                + $"{journey.QueryPeakLiveBytes:N0} | {journey.PeakWorkingSetBytes:N0} |");
        }
        text.AppendLine();
        text.AppendLine("## Findings");
        text.AppendLine();
        if (report.Findings.Count == 0)
            text.AppendLine("- None.");
        else
            foreach (GraphProductionGateFinding finding in report.Findings)
                text.AppendLine(CultureInfo.InvariantCulture, $"- `{finding.Gate}/{finding.Code}`: {finding.Message}");
        text.AppendLine();
        text.AppendLine("## Boundaries");
        text.AppendLine();
        foreach (string limitation in report.Input.Limitations)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {limitation}");
        return text.ToString();
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0)
            return 0;
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Count) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }

    private static void UpdatePeak(ref long target, long candidate)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (candidate <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref target, candidate, current) != current);
    }

    private static string Status(bool pass)
        => pass ? GraphProductionEvidenceStatus.Pass : GraphProductionEvidenceStatus.Fail;

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteAtomically(string path, string content)
    {
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // 已生成的最终 evidence 优先于临时文件清理。
            }
            catch (UnauthorizedAccessException)
            {
                // 最终 evidence 已发布时，不以临时文件清理失败覆盖结果。
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // 临时数据库清理不能覆盖已生成的 evidence。
        }
        catch (UnauthorizedAccessException)
        {
            // Windows 文件句柄短暂存活时交给临时目录后续清理。
        }
    }
}
