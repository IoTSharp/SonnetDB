using System.Collections.Concurrent;
using System.ComponentModel;
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
    internal const long MaximumManifestBytes = 4L * 1024 * 1024;
    private const int QuickVertexCount = 64;
    private const int QuickEdgeCount = 192;
    private const int QuickReaderWorkers = 8;
    private const int QuickSamplesPerReader = 6;
    private const int QuickWriterMutations = 12;
    private const int QuickCursorPageSize = 8;
    private const int QuickCursorMaximumResults = 64;
    private const int QuickCursorMaximumPageReads =
        (QuickCursorMaximumResults + QuickCursorPageSize - 1) / QuickCursorPageSize;
    private const int QuickMaximumPeakUpdateAttempts =
        (QuickReaderWorkers * QuickSamplesPerReader) + QuickWriterMutations;
    private const string CrashReadyFileName = "m40-crash-ready";
    private const int CrashMarkerMaximumPollCount = 1_200;
    private const int CrashChildMaximumPollCount = 600;
    private const int CrashChildWatchdogExitCode = 124;
    private static readonly TimeSpan CrashMarkerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CrashMarkerPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan CrashChildMaximumLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CrashChildWatchdogJoinTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MetadataProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuickMixedWorkloadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan QuickMixedWorkloadCancellationJoinTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuickExecutionTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ManifestEvaluationTimeout = TimeSpan.FromHours(12);

    /// <summary>按冻结 source-generated schema 验证单个 M40 #367 原始 artifact。</summary>
    /// <param name="artifactPath">待验证 artifact 路径。</param>
    public static void VerifyArtifact(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        string fullPath = Path.GetFullPath(artifactPath);
        using FileStream documentStream = File.OpenRead(fullPath);
        using JsonDocument document = JsonDocument.Parse(documentStream);
        if (!document.RootElement.TryGetProperty("schema", out JsonElement schemaElement))
            throw new InvalidDataException("M40 #367 原始 artifact 缺少 schema。");
        string? schema = schemaElement.GetString();
        using FileStream stream = File.OpenRead(fullPath);
        object? artifact = schema switch
        {
            "m40-graph-dataset-evidence-v1" => JsonSerializer.Deserialize(
                stream,
                GraphProductionArtifactJsonContext.Default.GraphProductionDatasetArtifact),
            "m40-graph-environment-evidence-v1" => JsonSerializer.Deserialize(
                stream,
                GraphProductionArtifactJsonContext.Default.GraphProductionEnvironmentArtifact),
            "m40-graph-soak-evidence-v1" => JsonSerializer.Deserialize(
                stream,
                GraphProductionArtifactJsonContext.Default.GraphProductionSoakArtifact),
            "m40-graph-journey-evidence-v1" => JsonSerializer.Deserialize(
                stream,
                GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact),
            "m40-graph-check-evidence-v1" => JsonSerializer.Deserialize(
                stream,
                GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact),
            _ => throw new InvalidDataException($"未知 M40 #367 原始 artifact schema：{schema}。"),
        };
        if (artifact is null)
            throw new InvalidDataException("M40 #367 原始 artifact 为空。");
    }

    /// <summary>运行 8 reader + 1 writer、checkpoint、重开和 backup/restore 的 quick smoke。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="cancellationToken">取消令牌；取消后先回收已启动的子进程。</param>
    /// <returns>保持 Production 双门禁为 NOT_RUN 的本地报告。</returns>
    public static GraphProductionGateReport RunQuick(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionCancellation.CancelAfter(QuickExecutionTimeout);
        CancellationToken executionToken = executionCancellation.Token;
        executionToken.ThrowIfCancellationRequested();
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
        bool killReopenPass = false;
        bool killReopenAttempted = false;
        GraphEvidenceProcessResult? killReopenProcess = null;
        int killReopenCount = 0;
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
                    peakWorkingSet,
                    executionToken);
                _ = store.Checkpoint();
                GraphInvariantReport invariant = GraphInvariantChecker.Check(store);
                mixedWorkloadPass &= invariant.IsValid
                    && invariant.IsComplete
                    && invariant.VertexCount == QuickVertexCount
                    && invariant.EdgeCount == QuickEdgeCount;
            }
            executionToken.ThrowIfCancellationRequested();

            killReopenAttempted = true;
            killReopenPass = RunRealKillReopen(root, executionToken, out killReopenProcess);
            GraphEvidenceProcessResult killReopenEvidence = killReopenProcess
                ?? throw new InvalidOperationException("M40 crash/reopen process evidence 缺失。");
            killReopenCount = killReopenPass ? 1 : 0;
            executionToken.ThrowIfCancellationRequested();

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
            executionToken.ThrowIfCancellationRequested();

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
            executionToken.ThrowIfCancellationRequested();

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
                    kill_reopen={Status(killReopenPass)}
                    kill_reopen_pid={killReopenEvidence.Identity.ProcessId}
                    kill_reopen_started_utc={killReopenEvidence.Identity.StartedUtc:O}
                    kill_reopen_parent_pid={killReopenEvidence.Identity.ParentProcessId}
                    kill_reopen_parent_started_utc={killReopenEvidence.Identity.ParentStartedUtc:O}
                    kill_reopen_containment={killReopenEvidence.ContainmentKind}
                    kill_reopen_tree_tracking_reliable={killReopenEvidence.TreeTrackingReliable}
                    kill_reopen_cleanup_confirmed={killReopenEvidence.CleanupConfirmed}
                    kill_reopen_output_drained={killReopenEvidence.OutputDrained}
                    kill_reopen_output_drain_stopped={killReopenEvidence.OutputDrainStopped}
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
                Command = "dotnet",
                Arguments =
                [
                    "run",
                    "--project",
                    "tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj",
                    "-c",
                    "Release",
                    "--",
                    "--m40-production-gate",
                    "--quick",
                ],
            };

            double[] orderedLatencies = latencies.Order().ToArray();
            var input = new GraphProductionGateInput
            {
                ProductionRun = false,
                CommitSha = ResolveCommitSha(executionToken),
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
                Environment = CaptureEnvironment(outputRoot, executionToken),
                Soak = new GraphProductionSoakEvidence
                {
                    DurationHours = (finishedUtc - startedUtc).TotalHours,
                    ReaderWorkers = QuickReaderWorkers,
                    UpdateWorkers = 1,
                    UpdateProfile = "quick-smoke",
                    MaximumCheckpointIntervalMinutes = 0,
                    CheckpointCount = 1,
                    KillReopenCount = killReopenCount,
                    InvariantCheckCount = 2 + killReopenCount,
                    FailedOperationCount = mixedWorkloadPass && checkpointReopenPass && backupRestorePass && killReopenPass ? 0 : 1,
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
                    Check("kill_reopen_smoke", killReopenPass, artifact),
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
                    "本次只覆盖单次 quick 真子进程 kill/reopen，不等于 Production 的每日 kill matrix。",
                    "未执行 Neo4j/PostgreSQL、LDBC、Graphalytics、Couplet C4 或 Native AOT 发布 artifact 验证。",
                    "未运行 168 小时 mixed workload，不能据此宣称 Graph Production 门禁通过。",
                ],
            };
            executionToken.ThrowIfCancellationRequested();
            GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(
                input,
                outputRoot,
                executionToken);
            executionToken.ThrowIfCancellationRequested();
            WriteReport(report, outputRoot);
            executionToken.ThrowIfCancellationRequested();
            WriteTemplate(outputRoot, input.Environment);
            executionToken.ThrowIfCancellationRequested();
            return report;
        }
        finally
        {
            bool cleanupSafe = !killReopenAttempted
                || killReopenProcess is { CleanupConfirmed: true };
            if (cleanupSafe)
            {
                TryDeleteDirectory(root);
            }
            else
            {
                Console.Error.WriteLine(
                    $"m40-temp-retained-unconfirmed-process-tree path={Path.GetFullPath(root)}");
            }
        }
    }

    /// <summary>供 quick 恢复 harness 使用的子进程入口；父进程会在 marker 持久化后主动终止它。</summary>
    /// <param name="databaseRoot">子进程数据库根目录。</param>
    /// <param name="parentProcessId">启动本 harness 的父进程 ID。</param>
    /// <param name="parentIdentityToken">父进程稳定启动标识，用于拒绝 PID 重用。</param>
    public static void RunCrashReopenChild(
        string databaseRoot,
        int parentProcessId,
        string parentIdentityToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentIdentityToken);
        var lifetime = Stopwatch.StartNew();
        using var completed = new ManualResetEventSlim(false);
        Task watchdog = Task.Run(
            () => WatchCrashChildParent(
                parentProcessId,
                parentIdentityToken,
                lifetime,
                completed));
        try
        {
            if (!GraphEvidenceProcessIdentityToken.IsExpectedProcessAlive(parentProcessId, parentIdentityToken))
                return;

            Directory.CreateDirectory(databaseRoot);
            using Tsdb database = Tsdb.Open(CreateQuickOptions(databaseRoot));
            GraphStore store = database.Graphs.Create("production_crash");
            long sequence = WriteQuickFixture(store);
            string marker = Path.Combine(databaseRoot, CrashReadyFileName);
            File.WriteAllText(marker, sequence.ToString(CultureInfo.InvariantCulture));
            using var wait = new ManualResetEventSlim(false);
            for (int attempt = 0;
                attempt < CrashChildMaximumPollCount && lifetime.Elapsed < CrashChildMaximumLifetime;
                attempt++)
            {
                if (!GraphEvidenceProcessIdentityToken.IsExpectedProcessAlive(parentProcessId, parentIdentityToken))
                    return;
                _ = wait.Wait(TimeSpan.FromMilliseconds(100));
                if ((attempt + 1) % 300 == 0)
                {
                    Console.Error.WriteLine(FormattableString.Invariant(
                        $"m40-crash-child-wait pid={Environment.ProcessId} parent_pid={parentProcessId} polls={attempt + 1} elapsed_seconds={lifetime.Elapsed.TotalSeconds:F3}"));
                }
            }

            Console.Error.WriteLine(FormattableString.Invariant(
                $"m40-crash-child-watchdog pid={Environment.ProcessId} parent_pid={parentProcessId} elapsed_seconds={lifetime.Elapsed.TotalSeconds:F3}"));
        }
        finally
        {
            completed.Set();
            if (!watchdog.Wait(CrashChildWatchdogJoinTimeout))
            {
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"m40-crash-child-watchdog-join-timeout pid={Environment.ProcessId} parent_pid={parentProcessId}"));
            }
        }
    }

    private static void WatchCrashChildParent(
        int parentProcessId,
        string parentIdentityToken,
        Stopwatch lifetime,
        ManualResetEventSlim completed)
    {
        for (int attempt = 0;
            attempt < CrashChildMaximumPollCount && lifetime.Elapsed < CrashChildMaximumLifetime;
            attempt++)
        {
            if (!GraphEvidenceProcessIdentityToken.IsExpectedProcessAlive(parentProcessId, parentIdentityToken))
            {
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"m40-crash-child-parent-lost pid={Environment.ProcessId} parent_pid={parentProcessId} elapsed_seconds={lifetime.Elapsed.TotalSeconds:F3}"));
                Environment.Exit(CrashChildWatchdogExitCode);
                return;
            }
            if (completed.Wait(TimeSpan.FromMilliseconds(100)))
                return;
        }

        if (!completed.IsSet)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"m40-crash-child-hard-deadline pid={Environment.ProcessId} parent_pid={parentProcessId} elapsed_seconds={lifetime.Elapsed.TotalSeconds:F3}"));
            Environment.Exit(CrashChildWatchdogExitCode);
        }
    }

    /// <summary>读取完整证据清单、校验 artifact 并输出双门禁报告。</summary>
    /// <param name="manifestPath">source-generated JSON 输入清单。</param>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="cancellationToken">取消令牌；取消后先回收正在执行的复现进程。</param>
    /// <returns>严格门禁报告。</returns>
    public static GraphProductionGateReport EvaluateManifest(
        string manifestPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => EvaluateManifestAsync(manifestPath, outputDirectory, cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    /// <summary>异步读取完整证据清单、校验 artifact 并输出双门禁报告。</summary>
    /// <param name="manifestPath">source-generated JSON 输入清单。</param>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="cancellationToken">取消令牌；取消后先回收正在执行的复现进程。</param>
    /// <returns>严格门禁报告。</returns>
    public static async Task<GraphProductionGateReport> EvaluateManifestAsync(
        string manifestPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionCancellation.CancelAfter(ManifestEvaluationTimeout);
        CancellationToken executionToken = executionCancellation.Token;
        executionToken.ThrowIfCancellationRequested();
        string fullManifestPath = Path.GetFullPath(manifestPath);
        using var manifestStream = new FileStream(
            fullManifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (manifestStream.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"M40 #367 证据清单不能超过 {MaximumManifestBytes} 字节。");
        }
        executionToken.ThrowIfCancellationRequested();
        GraphProductionGateInput input = await JsonSerializer.DeserializeAsync(
            manifestStream,
            GraphProductionGateJsonContext.Default.GraphProductionGateInput,
            executionToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("M40 #367 证据清单为空。");
        string artifactRoot = Path.GetDirectoryName(fullManifestPath)
            ?? Directory.GetCurrentDirectory();
        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        GraphProductionGateReport report = await GraphProductionGateEvaluator.EvaluateAsync(
            input,
            artifactRoot,
            executionToken).ConfigureAwait(false);
        executionToken.ThrowIfCancellationRequested();
        WriteReport(report, outputRoot);
        executionToken.ThrowIfCancellationRequested();
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
            Arguments = ["REPLACE_ME", "{artifact}"],
        };
        GraphProductionEnvironmentEvidence templateEnvironment = (environment
            ?? new GraphProductionEnvironmentEvidence()) with
        {
            Artifact = missingArtifact,
        };
        var input = new GraphProductionGateInput
        {
            ProductionRun = true,
            CommitSha = new string('0', 40),
            Environment = templateEnvironment,
            Dataset = new GraphProductionDatasetEvidence
            {
                Tier = "production-soak",
                VertexCount = 1_000_000,
                EdgeCount = 10_000_000,
                InputDigest = new string('0', 64),
                OutputDigest = new string('0', 64),
                Artifact = missingArtifact,
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
                Artifact = missingArtifact,
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

    private static bool RunRealKillReopen(
        string parentRoot,
        CancellationToken cancellationToken,
        out GraphEvidenceProcessResult? processResult)
    {
        processResult = null;
        string childRoot = Path.Combine(parentRoot, "crash-reopen");
        string marker = Path.Combine(childRoot, CrashReadyFileName);
        Directory.CreateDirectory(childRoot);
        ProcessStartInfo startInfo = CreateSelfStartInfo(childRoot);
        GraphEvidenceProcessResult child = GraphEvidenceProcessRunner.RunUntilFileExists(
            startInfo,
            marker,
            CrashMarkerTimeout,
            CrashMarkerPollInterval,
            CrashMarkerMaximumPollCount,
            captureOutput: true,
            cancellationToken: cancellationToken);
        processResult = child;
        cancellationToken.ThrowIfCancellationRequested();
        if (child.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException("M40 crash/reopen child 已取消并完成回收。");
        }
        if (!child.Completed
            || !child.ConditionSatisfied
            || !child.TerminationRequested
            || !child.RunnerTerminationConfirmed
            || child.TargetCompletionObserved)
        {
            throw new InvalidOperationException(
                "M40 crash/reopen child 未在 deadline 内发布 marker，或其完整进程树未被确认回收；"
                + $"{child.Diagnostic} stdout={child.StandardOutput} stderr={child.StandardError}");
        }

        using Tsdb reopened = Tsdb.Open(CreateQuickOptions(childRoot));
        GraphStore store = reopened.Graphs.Open("production_crash");
        GraphInvariantReport invariant = GraphInvariantChecker.Check(store);
        using GraphReadSession read = store.BeginRead();
        bool pass = invariant.IsComplete
            && invariant.IsValid
            && invariant.VertexCount == QuickVertexCount
            && invariant.EdgeCount == QuickEdgeCount
            && read.GetVertex(new GraphElementId(QuickVertexCount)) is not null;
        return pass;
    }

    private static ProcessStartInfo CreateSelfStartInfo(string databaseRoot)
    {
        string assemblyPath = Path.Combine(AppContext.BaseDirectory, "SonnetDB.Benchmarks.dll");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("M40 crash/reopen child benchmark assembly 不存在。", assemblyPath);
        using Process parent = Process.GetCurrentProcess();
        string parentIdentityToken = GraphEvidenceProcessIdentityToken.Create(parent);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--m40-production-crash-child");
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(databaseRoot);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--parent-identity-token");
        startInfo.ArgumentList.Add(parentIdentityToken);
        return startInfo;
    }

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
        long initialPeakWorkingSet,
        CancellationToken cancellationToken)
    {
        using var workloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        workloadCancellation.CancelAfter(QuickMixedWorkloadTimeout);
        CancellationToken workloadToken = workloadCancellation.Token;
        using var start = new ManualResetEventSlim(false);
        long expandedEdges = 0;
        long sequence = initialSequence;
        long peakWorkingSet = initialPeakWorkingSet;
        Task[] readers = Enumerable.Range(0, QuickReaderWorkers)
            .Select(worker => Task.Run(() =>
            {
                start.Wait(workloadToken);
                for (int sample = 0; sample < QuickSamplesPerReader; sample++)
                {
                    workloadToken.ThrowIfCancellationRequested();
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
                        options: new GraphCursorOptions
                        {
                            PageSize = QuickCursorPageSize,
                            MaxResults = QuickCursorMaximumResults,
                        });
                    for (int pageRead = 0;
                        pageRead < QuickCursorMaximumPageReads && !cursor.IsExhausted;
                        pageRead++)
                    {
                        workloadToken.ThrowIfCancellationRequested();
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
            start.Wait(workloadToken);
            for (int mutation = 1; mutation <= QuickWriterMutations; mutation++)
            {
                workloadToken.ThrowIfCancellationRequested();
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
        Task workload = Task.WhenAll(readers.Append(writer));
        try
        {
            workload.WaitAsync(QuickMixedWorkloadTimeout, cancellationToken)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            workloadCancellation.Cancel();
            if (!workload.IsCompleted)
            {
                try
                {
                    _ = workload.Wait(QuickMixedWorkloadCancellationJoinTimeout);
                }
                catch (AggregateException) when (workload.IsCompleted)
                {
                    // WaitAsync 已传播主失败；这里只确认协作任务已在取消后停止。
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
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

    private static GraphProductionEnvironmentEvidence CaptureEnvironment(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            SdkVersion = ReadDotNetSdkVersion(cancellationToken),
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

    private static string ReadDotNetSdkVersion(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateCapturedStartInfo("dotnet", Directory.GetCurrentDirectory(), ["--version"]);
        GraphEvidenceProcessResult version = GraphEvidenceProcessRunner.Run(
            startInfo,
            MetadataProcessTimeout,
            captureOutput: true,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string output = version.StandardOutput.Trim();
        if (!version.Completed || version.ExitCode != 0 || output.Length == 0)
            return "unknown";

        ProcessStartInfo statusStartInfo = CreateCapturedStartInfo(
            "git",
            Directory.GetCurrentDirectory(),
            ["status", "--porcelain", "--untracked-files=normal"]);
        GraphEvidenceProcessResult status = GraphEvidenceProcessRunner.Run(
            statusStartInfo,
            MetadataProcessTimeout,
            captureOutput: true,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!status.Completed || status.ExitCode != 0)
            return output + "-status-unknown";
        return status.StandardOutput.Length == 0 ? output : output + "-dirty";
    }

    private static string ResolveCommitSha(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? configured = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        ProcessStartInfo startInfo = CreateCapturedStartInfo(
            "git",
            Directory.GetCurrentDirectory(),
            ["rev-parse", "HEAD"]);
        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
            startInfo,
            MetadataProcessTimeout,
            captureOutput: true,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string output = result.StandardOutput.Trim();
        return result.Completed && result.ExitCode == 0 && output.Length > 0 ? output : "unknown";
    }

    private static ProcessStartInfo CreateCapturedStartInfo(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
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
        text.AppendLine("| ID | Status | Access path | Samples | ops/s | P50 ms | P95 ms | P99 ms | Alloc P95 | Gen0/1/2 | GC P99 ms | Peak live | Working set |");
        text.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (GraphProductionJourneyEvidence journey in report.Input.Journeys)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {journey.Id} | {journey.Status} | {journey.AccessPath} | {journey.Rounds} x {journey.SamplesPerRound:N0} | "
                + $"{journey.ThroughputPerSecond:F3} | {journey.P50Milliseconds:F3} | {journey.P95Milliseconds:F3} | {journey.P99Milliseconds:F3} | "
                + $"{journey.AllocatedBytesP95:N0} | {journey.Gen0Collections}/{journey.Gen1Collections}/{journey.Gen2Collections} | {journey.GcPauseP99Milliseconds:F3} | "
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
        for (int attempt = 0; attempt < QuickMaximumPeakUpdateAttempts; attempt++)
        {
            long current = Volatile.Read(ref target);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
                return;
            Thread.Yield();
        }

        throw new InvalidOperationException("quick working-set peak update exceeded its retry budget.");
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
        string fullPath = Path.GetFullPath(path);
        string expectedPrefix = "sonnetdb-m40-production-smoke-";
        if (!GraphEvidenceOwnedDirectoryCleanup.TryDelete(
            fullPath,
            Path.GetTempPath(),
            expectedPrefix,
            out string failureReason))
        {
            Console.Error.WriteLine(
                $"m40-temp-cleanup-failed path={fullPath} reason={failureReason}");
        }
    }

}
