using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>按 #341 冻结合同严格判定 M40 #367 双门禁。</summary>
public static class GraphProductionGateEvaluator
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;
    private const long ExpectedWalBudget = 256L * MiB;
    private const long MaximumWorkingSet = 12L * GiB;
    private const double MaximumGcPauseP99Milliseconds = 50;
    private const int SamplesPerGcRateUnit = 1_000;
    private const int MaximumUniqueReplayCount = 64;
    internal const long MaximumArtifactBytes = 16L * MiB;
    internal const int MaximumArtifactArguments = 256;
    internal const int MaximumSoakCheckpointSamples = 20_000;
    internal const int MaximumSoakKillReopenSamples = 1_024;
    internal const int MaximumSoakColdOpenSamples = 20_000;
    internal const int MaximumSoakResourceSamples = 25_000;
    internal const int MaximumJourneyRounds = 16;
    internal const int MaximumJourneySamplesPerColumn = 20_000;
    internal const int MaximumJourneyOracleAssertions = 64;
    internal const int MaximumCheckAssertions = 256;
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromHours(12);
    private const string ArtifactArgumentPlaceholder = "{artifact}";
    private static readonly string[] RequiredCorrectnessChecks =
    [
        "five_journey_oracle",
        "neo4j_comparison",
        "postgresql_comparison",
        "edge_atomicity",
        "concurrency_idempotency",
        "kill_reopen_matrix",
        "backup_restore",
        "invariant_corruption_detection",
        "format_compatibility",
        "budget_cancel",
    ];
    private static readonly string[] RequiredPerformanceChecks =
    [
        "fixed_hardware",
        "preview_small_capacity",
        "gate_capacity",
        "ldbc_snb",
        "graphalytics",
        "couplet_c4",
        "native_aot",
        "cold_open",
        "complexity_trend",
        "access_path",
    ];
    private static readonly string[] RequiredGapIds = Enumerable.Range(1, 12)
        .Select(static number => "M40-GAP-" + number.ToString("D3", CultureInfo.InvariantCulture))
        .ToArray();
    private static readonly IReadOnlyDictionary<string, JourneySpec> JourneySpecs =
        new Dictionary<string, JourneySpec>(StringComparer.Ordinal)
        {
            ["SOC-1"] = new(20, 50, 32, JourneyPath.Native),
            ["SOC-2"] = new(200, 600, 128, JourneyPath.Native),
            ["SOC-3"] = new(600, 1_800, 256, JourneyPath.Native),
            ["TOP-1"] = new(150, 450, 96, JourneyPath.Native),
            ["TOP-2"] = new(400, 1_200, 192, JourneyPath.Native),
            ["TOP-3"] = new(700, 2_000, 256, JourneyPath.Native),
            ["EVD-1"] = new(200, 600, 128, JourneyPath.Native),
            ["EVD-2"] = new(100, 300, 64, JourneyPath.Native),
            ["EVD-3"] = new(500, 1_500, 192, JourneyPath.Native),
            ["CPL-1"] = new(30, 80, 32, JourneyPath.Native),
            ["CPL-2"] = new(200, 600, 128, JourneyPath.Native),
            ["CPL-3"] = new(1_000, 3_000, 256, JourneyPath.Native),
            ["CPL-4"] = new(300, 900, 128, JourneyPath.HybridNative),
            ["PGQ-1"] = new(60, 180, 64, JourneyPath.RelationIndex),
            ["PGQ-2"] = new(500, 1_500, 192, JourneyPath.RelationIndex),
            ["PGQ-3"] = new(0, 0, 64, JourneyPath.BoundedRelationFallback),
        };

    /// <summary>判定证据清单；Production PASS 只使用 schema-aware 原始 artifact 重算值。</summary>
    /// <param name="input">原始证据清单。</param>
    /// <param name="artifactBaseDirectory">相对 artifact 路径的基准目录。</param>
    /// <param name="cancellationToken">取消令牌；取消后先回收正在执行的复现进程。</param>
    /// <returns>带双 gate 和 findings 的报告。</returns>
    public static GraphProductionGateReport Evaluate(
        GraphProductionGateInput input,
        string artifactBaseDirectory,
        CancellationToken cancellationToken = default)
        => EvaluateAsync(input, artifactBaseDirectory, cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    /// <summary>异步判定证据清单；artifact 哈希与 JSON 读取支持取消。</summary>
    /// <param name="input">原始证据清单。</param>
    /// <param name="artifactBaseDirectory">相对 artifact 路径的基准目录。</param>
    /// <param name="cancellationToken">取消令牌；取消后先回收正在执行的复现进程。</param>
    /// <returns>带双 gate 和 findings 的报告。</returns>
    public static async Task<GraphProductionGateReport> EvaluateAsync(
        GraphProductionGateInput input,
        string artifactBaseDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactBaseDirectory);
        using var evaluationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        evaluationCancellation.CancelAfter(EvaluationTimeout);
        CancellationToken evaluationToken = evaluationCancellation.Token;
        evaluationToken.ThrowIfCancellationRequested();
        string artifactRoot = Path.GetFullPath(artifactBaseDirectory);
        var findings = new List<GraphProductionGateFinding>();
        input = NormalizeInput(input, findings);

        if (!ValidateExecutionBounds(input, findings))
        {
            return CreateReport(
                input,
                GraphProductionEvidenceStatus.Fail,
                GraphProductionEvidenceStatus.Fail,
                GraphProductionEvidenceStatus.Fail,
                findings);
        }

        if (!input.ProductionRun)
        {
            string quickLocalSmoke = GetLocalSmoke(input);
            AddFinding(findings, "release", "production_not_attempted", "quick/local evidence 不能替代 #367 Production 门禁。");
            return CreateReport(
                input,
                quickLocalSmoke,
                GraphProductionEvidenceStatus.NotRun,
                GraphProductionEvidenceStatus.NotRun,
                findings);
        }

        ValidateCommon(input, findings);
        string localSmoke = GetLocalSmoke(input);

        string repositoryRoot = ValidateRepository(
            input.CommitSha,
            artifactRoot,
            findings,
            evaluationToken);
        var context = new EvaluationContext(
            artifactRoot,
            repositoryRoot,
            input.CommitSha,
            findings,
            evaluationToken);
        input = await RebuildInputFromArtifactsAsync(input, context).ConfigureAwait(false);
        evaluationToken.ThrowIfCancellationRequested();

        ValidateRequiredChecks(input.CorrectnessRecoveryChecks, RequiredCorrectnessChecks, "correctness_recovery", findings);
        ValidateRequiredChecks(input.PerformanceCapacityChecks, RequiredPerformanceChecks, "performance_capacity", findings);
        ValidateJourneys(input.Journeys, findings);
        ValidateDataset(input.Dataset, findings);
        ValidateEnvironment(input.Environment, findings);
        ValidateSoak(input, findings);
        await ValidateGapsAsync(input.Gaps, context, findings).ConfigureAwait(false);
        ValidateRepositoryClean(
            repositoryRoot,
            findings,
            "git_worktree_dirty_after_replay",
            evaluationToken);
        evaluationToken.ThrowIfCancellationRequested();

        bool correctnessPass = !findings.Any(static finding =>
            string.Equals(finding.Gate, "correctness_recovery", StringComparison.Ordinal)
            || string.Equals(finding.Gate, "both", StringComparison.Ordinal));
        bool performancePass = !findings.Any(static finding =>
            string.Equals(finding.Gate, "performance_capacity", StringComparison.Ordinal)
            || string.Equals(finding.Gate, "both", StringComparison.Ordinal));

        return CreateReport(
            input,
            localSmoke,
            correctnessPass ? GraphProductionEvidenceStatus.Pass : GraphProductionEvidenceStatus.Fail,
            performancePass ? GraphProductionEvidenceStatus.Pass : GraphProductionEvidenceStatus.Fail,
            findings);
    }

    /// <summary>返回 #367 所有冻结 journey ID。</summary>
    /// <returns>按 ordinal 排序的查询 ID。</returns>
    public static IReadOnlyList<string> GetRequiredJourneyIds()
        => JourneySpecs.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>返回 Gate A 所需稳定检查 ID。</summary>
    /// <returns>检查 ID。</returns>
    public static IReadOnlyList<string> GetRequiredCorrectnessCheckIds()
        => RequiredCorrectnessChecks.ToArray();

    /// <summary>返回 Gate B 所需稳定检查 ID。</summary>
    /// <returns>检查 ID。</returns>
    public static IReadOnlyList<string> GetRequiredPerformanceCheckIds()
        => RequiredPerformanceChecks.ToArray();

    /// <summary>返回 #341 冻结 capability gap ID。</summary>
    /// <returns>完整 gap ID。</returns>
    public static IReadOnlyList<string> GetRequiredGapIds()
        => RequiredGapIds.ToArray();

    private static GraphProductionGateReport CreateReport(
        GraphProductionGateInput input,
        string localSmoke,
        string correctnessRecovery,
        string performanceCapacity,
        IReadOnlyList<GraphProductionGateFinding> findings)
    {
        string releaseDecision = correctnessRecovery == GraphProductionEvidenceStatus.Pass
            && performanceCapacity == GraphProductionEvidenceStatus.Pass
                ? GraphProductionEvidenceStatus.Pass
                : correctnessRecovery == GraphProductionEvidenceStatus.NotRun
                    || performanceCapacity == GraphProductionEvidenceStatus.NotRun
                        ? GraphProductionEvidenceStatus.NotRun
                        : GraphProductionEvidenceStatus.Fail;
        return new GraphProductionGateReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            LocalSmoke = localSmoke,
            CorrectnessRecovery = correctnessRecovery,
            PerformanceCapacity = performanceCapacity,
            ReleaseDecision = releaseDecision,
            Input = input,
            Findings = findings,
        };
    }

    private static GraphProductionGateInput NormalizeInput(
        GraphProductionGateInput input,
        List<GraphProductionGateFinding> findings)
    {
        bool invalid = input.Dataset is null
            || input.Environment is null
            || input.Soak is null
            || input.Journeys is null
            || input.CorrectnessRecoveryChecks is null
            || input.PerformanceCapacityChecks is null
            || input.Gaps is null
            || input.Limitations is null;
        if (invalid)
            AddFinding(findings, "both", "manifest_null", "证据清单的对象和集合字段不能为 null。");
        return input with
        {
            Dataset = input.Dataset ?? new GraphProductionDatasetEvidence(),
            Environment = input.Environment ?? new GraphProductionEnvironmentEvidence(),
            Soak = input.Soak ?? new GraphProductionSoakEvidence(),
            Journeys = input.Journeys ?? [],
            CorrectnessRecoveryChecks = input.CorrectnessRecoveryChecks ?? [],
            PerformanceCapacityChecks = input.PerformanceCapacityChecks ?? [],
            Gaps = input.Gaps ?? [],
            Limitations = input.Limitations ?? [],
        };
    }

    private static string GetLocalSmoke(GraphProductionGateInput input)
    {
        if (input.CorrectnessRecoveryChecks.Count == 0)
            return GraphProductionEvidenceStatus.NotRun;
        return input.CorrectnessRecoveryChecks.All(static check =>
            check is not null
            && string.Equals(check.Status, GraphProductionEvidenceStatus.Pass, StringComparison.Ordinal))
                ? GraphProductionEvidenceStatus.Pass
                : GraphProductionEvidenceStatus.Fail;
    }

    private static void ValidateCommon(
        GraphProductionGateInput input,
        List<GraphProductionGateFinding> findings)
    {
        if (!string.Equals(input.Schema, "m40-graph-production-input-v2", StringComparison.Ordinal))
            AddFinding(findings, "both", "input_schema", "证据清单 schema 必须为 m40-graph-production-input-v2。");
        if (!IsSha1(input.CommitSha))
            AddFinding(findings, "both", "commit_sha", "Production 证据必须绑定 40 位 clean commit SHA。");
        if (input.StartedUtc == default || input.FinishedUtc <= input.StartedUtc)
            AddFinding(findings, "both", "run_interval", "运行开始/结束 UTC 时间无效。");
        else if (input.FinishedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            AddFinding(findings, "both", "run_interval_future", "运行结束时间不能位于未来。");
    }

    private static bool ValidateExecutionBounds(
        GraphProductionGateInput input,
        List<GraphProductionGateFinding> findings)
    {
        bool valid = true;
        valid &= ValidateCollectionBound(
            input.Journeys.Count,
            JourneySpecs.Count,
            "journeys",
            findings);
        valid &= ValidateCollectionBound(
            input.CorrectnessRecoveryChecks.Count,
            RequiredCorrectnessChecks.Length,
            "correctness checks",
            findings);
        valid &= ValidateCollectionBound(
            input.PerformanceCapacityChecks.Count,
            RequiredPerformanceChecks.Length,
            "performance checks",
            findings);
        valid &= ValidateCollectionBound(
            input.Gaps.Count,
            RequiredGapIds.Length,
            "gaps",
            findings);
        return valid;
    }

    private static bool ValidateCollectionBound(
        int actual,
        int maximum,
        string collection,
        List<GraphProductionGateFinding> findings)
    {
        if (actual <= maximum)
            return true;
        AddFinding(
            findings,
            "both",
            "manifest_execution_bound",
            $"{collection} 条目数 {actual} 超过冻结上限 {maximum}；未启动任何复现进程。");
        return false;
    }

    private static string ValidateRepository(
        string commitSha,
        string artifactRoot,
        List<GraphProductionGateFinding> findings,
        CancellationToken cancellationToken)
    {
        GraphEvidenceProcessResult rootResult = RunCapturedProcess(
            "git",
            ["-C", artifactRoot, "rev-parse", "--show-toplevel"],
            artifactRoot,
            30,
            cancellationToken);
        if (!rootResult.Completed || rootResult.ExitCode != 0)
        {
            AddFinding(findings, "both", "git_repository", "artifact 必须位于可验证的 Git worktree 内。");
            return artifactRoot;
        }

        string repositoryRoot = Path.GetFullPath(rootResult.StandardOutput.Trim());
        if (IsSha1(commitSha))
        {
            GraphEvidenceProcessResult objectResult = RunCapturedProcess(
                "git",
                ["cat-file", "-e", commitSha + "^{commit}"],
                repositoryRoot,
                30,
                cancellationToken);
            if (!objectResult.Completed || objectResult.ExitCode != 0)
                AddFinding(findings, "both", "git_commit_missing", $"Git commit 不存在或不是 commit 对象：{commitSha}。");

            GraphEvidenceProcessResult headResult = RunCapturedProcess(
                "git",
                ["rev-parse", "HEAD"],
                repositoryRoot,
                30,
                cancellationToken);
            if (!headResult.Completed
                || headResult.ExitCode != 0
                || !string.Equals(headResult.StandardOutput.Trim(), commitSha, StringComparison.OrdinalIgnoreCase))
            {
                AddFinding(findings, "both", "git_head_mismatch", "被测 commit 必须与 evidence worktree 的 HEAD 完全一致。");
            }
        }

        ValidateRepositoryClean(repositoryRoot, findings, "git_worktree_dirty", cancellationToken);
        return repositoryRoot;
    }

    private static void ValidateRepositoryClean(
        string repositoryRoot,
        List<GraphProductionGateFinding> findings,
        string findingCode,
        CancellationToken cancellationToken)
    {
        GraphEvidenceProcessResult statusResult = RunCapturedProcess(
            "git",
            ["status", "--porcelain=v1", "--untracked-files=all"],
            repositoryRoot,
            30,
            cancellationToken);
        if (!statusResult.Completed || statusResult.ExitCode != 0)
        {
            AddFinding(findings, "both", "git_status", "无法验证 evidence worktree 状态。");
            return;
        }
        if (!string.IsNullOrWhiteSpace(statusResult.StandardOutput))
            AddFinding(findings, "both", findingCode, "Production evidence 要求被测 Git worktree 干净。");
    }

    private static async Task<GraphProductionGateInput> RebuildInputFromArtifactsAsync(
        GraphProductionGateInput input,
        EvaluationContext context)
    {
        GraphProductionDatasetEvidence dataset = await RebuildDatasetAsync(input.Dataset, context).ConfigureAwait(false);
        GraphProductionEnvironmentEvidence environment = await RebuildEnvironmentAsync(input.Environment, context).ConfigureAwait(false);
        (GraphProductionSoakEvidence soak, DateTimeOffset startedUtc, DateTimeOffset finishedUtc) =
            await RebuildSoakAsync(input.Soak, input.StartedUtc, input.FinishedUtc, context).ConfigureAwait(false);
        var journeys = new List<GraphProductionJourneyEvidence>(input.Journeys.Count);
        foreach (GraphProductionJourneyEvidence? journey in input.Journeys)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (journey is not null)
                journeys.Add(await RebuildJourneyAsync(journey, context).ConfigureAwait(false));
        }
        var correctnessChecks = new List<GraphProductionCheckEvidence>(input.CorrectnessRecoveryChecks.Count);
        foreach (GraphProductionCheckEvidence? check in input.CorrectnessRecoveryChecks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (check is not null)
            {
                correctnessChecks.Add(
                    await RebuildCheckAsync(check, "correctness_recovery", context).ConfigureAwait(false));
            }
        }
        var performanceChecks = new List<GraphProductionCheckEvidence>(input.PerformanceCapacityChecks.Count);
        foreach (GraphProductionCheckEvidence? check in input.PerformanceCapacityChecks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (check is not null)
            {
                performanceChecks.Add(
                    await RebuildCheckAsync(check, "performance_capacity", context).ConfigureAwait(false));
            }
        }

        return input with
        {
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            Dataset = dataset,
            Environment = environment,
            Soak = soak,
            Journeys = journeys,
            CorrectnessRecoveryChecks = correctnessChecks,
            PerformanceCapacityChecks = performanceChecks,
        };
    }

    private static async Task<GraphProductionDatasetEvidence> RebuildDatasetAsync(
        GraphProductionDatasetEvidence summary,
        EvaluationContext context)
    {
        GraphProductionDatasetArtifact? artifact = await LoadArtifactAsync(
            summary.Artifact,
            "m40-graph-dataset-evidence-v1",
            "both",
            "dataset",
            GraphProductionArtifactJsonContext.Default.GraphProductionDatasetArtifact,
            static value => value.Schema,
            static value => value.Run,
            context).ConfigureAwait(false);
        if (artifact is null)
            return summary;

        var rebuilt = new GraphProductionDatasetEvidence
        {
            Tier = artifact.Tier,
            Generator = artifact.Generator,
            Seed = artifact.Seed,
            VertexCount = artifact.VertexCount,
            EdgeCount = artifact.EdgeCount,
            InputDigest = artifact.InputDigest,
            OutputDigest = artifact.OutputDigest,
            Artifact = summary.Artifact,
        };
        if (summary with { Artifact = new GraphProductionArtifactEvidence() }
            != rebuilt with { Artifact = new GraphProductionArtifactEvidence() })
        {
            AddFinding(context.Findings, "both", "dataset_summary_mismatch", "dataset manifest 摘要与原始 artifact 不一致。");
        }
        return rebuilt;
    }

    private static async Task<GraphProductionEnvironmentEvidence> RebuildEnvironmentAsync(
        GraphProductionEnvironmentEvidence summary,
        EvaluationContext context)
    {
        GraphProductionEnvironmentArtifact? artifact = await LoadArtifactAsync(
            summary.Artifact,
            "m40-graph-environment-evidence-v1",
            "performance_capacity",
            "environment",
            GraphProductionArtifactJsonContext.Default.GraphProductionEnvironmentArtifact,
            static value => value.Schema,
            static value => value.Run,
            context).ConfigureAwait(false);
        if (artifact is null || artifact.Environment is null)
        {
            if (artifact is not null)
                AddFinding(context.Findings, "performance_capacity", "environment_raw_missing", "环境 artifact 缺少原始快照。");
            return summary;
        }

        GraphProductionEnvironmentSnapshot raw = artifact.Environment;
        var rebuilt = new GraphProductionEnvironmentEvidence
        {
            OsDescription = raw.OsDescription,
            OsBuild = raw.OsBuild,
            Architecture = raw.Architecture,
            CpuName = raw.CpuName,
            PhysicalCoreCount = raw.PhysicalCoreCount,
            LogicalProcessorCount = raw.LogicalProcessorCount,
            PhysicalMemoryBytes = raw.PhysicalMemoryBytes,
            DiskName = raw.DiskName,
            DiskFormat = raw.DiskFormat,
            Runtime = raw.Runtime,
            SdkVersion = raw.SdkVersion,
            GcMode = raw.GcMode,
            PowerProfile = raw.PowerProfile,
            Artifact = summary.Artifact,
        };
        if (summary with { Artifact = new GraphProductionArtifactEvidence() }
            != rebuilt with { Artifact = new GraphProductionArtifactEvidence() })
        {
            AddFinding(context.Findings, "performance_capacity", "environment_summary_mismatch", "environment manifest 摘要与原始 artifact 不一致。");
        }
        return rebuilt;
    }

    private static async Task<(GraphProductionSoakEvidence Soak, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc)> RebuildSoakAsync(
        GraphProductionSoakEvidence summary,
        DateTimeOffset manifestStartedUtc,
        DateTimeOffset manifestFinishedUtc,
        EvaluationContext context)
    {
        GraphProductionSoakArtifact? artifact = await LoadArtifactAsync(
            summary.Artifact,
            "m40-graph-soak-evidence-v1",
            "both",
            "soak",
            GraphProductionArtifactJsonContext.Default.GraphProductionSoakArtifact,
            static value => value.Schema,
            static value => value.Run,
            context,
            value => ValidateSoakArtifactBounds(value, context)).ConfigureAwait(false);
        if (artifact is null)
            return (summary, manifestStartedUtc, manifestFinishedUtc);

        IReadOnlyList<DateTimeOffset> checkpoints = artifact.CheckpointsUtc ?? [];
        IReadOnlyList<GraphProductionKillReopenSample> kills = artifact.KillReopenSamples ?? [];
        IReadOnlyList<double> coldOpen = artifact.ColdOpenMilliseconds ?? [];
        IReadOnlyList<GraphProductionSoakResourceSample> resources = artifact.ResourceSamples ?? [];
        ValidateSoakRawArtifact(artifact, checkpoints, kills, coldOpen, resources, context.Findings);

        double maximumCheckpointInterval = ComputeMaximumIntervalMinutes(
            artifact.StartedUtc,
            artifact.FinishedUtc,
            checkpoints);
        double[] orderedColdOpen = coldOpen.Order().ToArray();
        double[] orderedRecovery = kills
            .Where(static sample => sample is not null)
            .Select(static sample => sample.RecoveryMilliseconds)
            .Order()
            .ToArray();
        var rebuilt = new GraphProductionSoakEvidence
        {
            DurationHours = (artifact.FinishedUtc - artifact.StartedUtc).TotalHours,
            ReaderWorkers = artifact.ReaderWorkers,
            UpdateWorkers = artifact.UpdateWorkers,
            UpdateProfile = artifact.UpdateProfile,
            MaximumCheckpointIntervalMinutes = maximumCheckpointInterval,
            CheckpointCount = checkpoints.Count,
            KillReopenCount = kills.Count(static sample => sample is not null && sample.ProcessKilled && sample.Reopened),
            InvariantCheckCount = kills.Count(static sample => sample is not null && sample.InvariantPassed),
            FailedOperationCount = artifact.FailedOperationCount,
            UnexpectedRestartCount = artifact.UnexpectedRestartCount,
            PeakWorkingSetBytes = resources.Count == 0 ? 0 : resources.Max(static sample => sample.WorkingSetBytes),
            WalBytes = resources.Count == 0 ? 0 : resources.Max(static sample => sample.WalBytes),
            ColdOpenP95Milliseconds = Percentile(orderedColdOpen, 0.95),
            ColdOpenP99Milliseconds = Percentile(orderedColdOpen, 0.99),
            RecoveryP50Milliseconds = Percentile(orderedRecovery, 0.50),
            RecoveryP95Milliseconds = Percentile(orderedRecovery, 0.95),
            RecoveryP99Milliseconds = Percentile(orderedRecovery, 0.99),
            SyncWalOnEveryWrite = artifact.SyncWalOnEveryWrite,
            AutoCheckpointEnabled = artifact.AutoCheckpointEnabled,
            MaxWalBytes = artifact.MaxWalBytes,
            MaxOverlayEntries = artifact.MaxOverlayEntries,
            Artifact = summary.Artifact,
        };
        if (summary with { Artifact = new GraphProductionArtifactEvidence() }
            != rebuilt with { Artifact = new GraphProductionArtifactEvidence() }
            || manifestStartedUtc != artifact.StartedUtc
            || manifestFinishedUtc != artifact.FinishedUtc)
        {
            AddFinding(context.Findings, "both", "soak_summary_mismatch", "soak manifest 摘要或运行区间与原始 artifact 不一致。");
        }
        return (rebuilt, artifact.StartedUtc, artifact.FinishedUtc);
    }

    private static void ValidateSoakRawArtifact(
        GraphProductionSoakArtifact artifact,
        IReadOnlyList<DateTimeOffset> checkpoints,
        IReadOnlyList<GraphProductionKillReopenSample> kills,
        IReadOnlyList<double> coldOpen,
        IReadOnlyList<GraphProductionSoakResourceSample> resources,
        List<GraphProductionGateFinding> findings)
    {
        bool intervalValid = artifact.StartedUtc != default && artifact.FinishedUtc > artifact.StartedUtc;
        if (!intervalValid)
            AddFinding(findings, "both", "soak_raw_interval", "soak artifact 的开始/结束时间无效。");
        if (checkpoints.Count == 0
            || checkpoints.Any(value => value < artifact.StartedUtc || value > artifact.FinishedUtc)
            || !IsStrictlyIncreasing(checkpoints))
        {
            AddFinding(findings, "correctness_recovery", "soak_raw_checkpoints", "soak artifact 缺少有序、区间内的 checkpoint 原始时间戳。");
        }
        DateTimeOffset[] killTimestamps = kills
            .Where(static sample => sample is not null)
            .Select(static sample => sample.TimestampUtc)
            .ToArray();
        if (kills.Count < 7
            || kills.Any(static sample => sample is null
                || sample.TimestampUtc == default
                || !sample.ProcessKilled
                || !sample.Reopened
                || !sample.InvariantPassed
                || !IsFinitePositive(sample.RecoveryMilliseconds))
            || killTimestamps.Any(value => value < artifact.StartedUtc || value > artifact.FinishedUtc)
            || !IsStrictlyIncreasing(killTimestamps)
            || ComputeMaximumIntervalHours(artifact.StartedUtc, artifact.FinishedUtc, killTimestamps) > 24)
        {
            AddFinding(findings, "correctness_recovery", "soak_raw_kill_reopen", "soak artifact 必须含按 24 小时分布的真 kill/reopen/invariant 原始样本。");
        }
        if (coldOpen.Count < 7 || coldOpen.Any(static value => !IsFinitePositive(value)))
            AddFinding(findings, "performance_capacity", "soak_raw_cold_open", "soak artifact 必须含至少 7 个有效 cold-open 原始样本。");
        if (resources.Count == 0
            || resources.Any(sample => sample is null
                || sample.TimestampUtc < artifact.StartedUtc
                || sample.TimestampUtc > artifact.FinishedUtc
                || sample.WorkingSetBytes <= 0
                || sample.WalBytes < 0))
        {
            AddFinding(findings, "performance_capacity", "soak_raw_resources", "soak artifact 缺少区间内的 working-set/WAL 原始样本。");
        }
    }

    private static async Task<GraphProductionJourneyEvidence> RebuildJourneyAsync(
        GraphProductionJourneyEvidence summary,
        EvaluationContext context)
    {
        GraphProductionJourneyArtifact? artifact = await LoadArtifactAsync(
            summary.Artifact,
            "m40-graph-journey-evidence-v1",
            "both",
            summary.Id,
            GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact,
            static value => value.Schema,
            static value => value.Run,
            context,
            value => ValidateJourneyArtifactBounds(value, summary.Id, context)).ConfigureAwait(false);
        if (artifact is null)
            return summary;
        if (!string.Equals(artifact.JourneyId, summary.Id, StringComparison.Ordinal))
            AddFinding(context.Findings, "both", "journey_artifact_id", $"{summary.Id} artifact journey_id 不匹配。");

        IReadOnlyList<GraphProductionJourneyRoundArtifact> rounds = artifact.Rounds ?? [];
        bool rawValid = ValidateJourneyRounds(summary.Id, rounds, context.Findings);
        GraphProductionJourneyEvidence rebuilt = SummarizeJourney(summary, rounds, rawValid, context.Findings);
        if (!JourneySummariesEqual(summary, rebuilt))
            AddFinding(context.Findings, "both", "journey_summary_mismatch", $"{summary.Id} manifest 摘要与逐轮原始样本重算结果不一致。");
        return rebuilt;
    }

    private static bool ValidateJourneyRounds(
        string journeyId,
        IReadOnlyList<GraphProductionJourneyRoundArtifact> rounds,
        List<GraphProductionGateFinding> findings)
    {
        bool valid = true;
        if (rounds.Count < 3)
        {
            AddFinding(findings, "performance_capacity", "journey_raw_rounds", $"{journeyId} 必须提供至少 3 个原始轮次。");
            valid = false;
        }
        var roundNumbers = new HashSet<int>();
        foreach (GraphProductionJourneyRoundArtifact round in rounds)
        {
            if (round is null || round.Round <= 0 || !roundNumbers.Add(round.Round))
            {
                AddFinding(findings, "both", "journey_raw_round_id", $"{journeyId} 原始轮次号不能为空、非正数或重复。");
                valid = false;
                continue;
            }

            int count = round.ElapsedMicroseconds?.Count ?? 0;
            if (round.WarmupCount < 1_000 || count < 10_000 || !AllSampleColumnsHaveCount(round, count))
            {
                AddFinding(findings, "performance_capacity", "journey_raw_samples", $"{journeyId} 第 {round.Round} 轮缺少 1,000 warmup、10,000 个逐样本值或样本列长度不一致。");
                valid = false;
            }
            if (!AllSampleColumnsNonNegative(round))
            {
                AddFinding(findings, "performance_capacity", "journey_raw_values", $"{journeyId} 第 {round.Round} 轮包含负数资源或时间样本。");
                valid = false;
            }
            IReadOnlyList<GraphProductionOracleAssertion> assertions = round.OracleAssertions ?? [];
            if (assertions.Count == 0
                || assertions.Any(static assertion => assertion is null
                    || string.IsNullOrWhiteSpace(assertion.Name)
                    || !IsSha256(assertion.ExpectedDigest)
                    || !IsSha256(assertion.ActualDigest)))
            {
                AddFinding(findings, "correctness_recovery", "journey_raw_oracle", $"{journeyId} 第 {round.Round} 轮缺少逐 ID/property/path oracle 摘要。");
                valid = false;
            }
            if (assertions
                .Where(static assertion => assertion is not null)
                .GroupBy(static assertion => assertion.Name, StringComparer.Ordinal)
                .Any(static group => group.Count() != 1))
            {
                AddFinding(findings, "correctness_recovery", "journey_raw_oracle_id", $"{journeyId} 第 {round.Round} 轮 oracle 名称重复。");
                valid = false;
            }
        }
        return valid;
    }

    private static GraphProductionJourneyEvidence SummarizeJourney(
        GraphProductionJourneyEvidence manifest,
        IReadOnlyList<GraphProductionJourneyRoundArtifact> rounds,
        bool rawValid,
        List<GraphProductionGateFinding> findings)
    {
        if (rounds.Count == 0)
            return manifest with { Status = GraphProductionEvidenceStatus.Fail };

        bool oraclePass = rawValid && rounds.All(static round =>
            (round.OracleAssertions ?? []).Count > 0
            && (round.OracleAssertions ?? []).All(static assertion =>
                assertion is not null
                && string.Equals(assertion.ExpectedDigest, assertion.ActualDigest, StringComparison.OrdinalIgnoreCase)));
        string[] paths = rounds.Select(static round => round.AccessPath).Distinct(StringComparer.Ordinal).ToArray();
        string?[] fallbacks = rounds.Select(static round => round.FallbackReason).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length != 1 || fallbacks.Length != 1)
            AddFinding(findings, "performance_capacity", "journey_raw_path_drift", $"{manifest.Id} 的 access path/fallback 在独立轮次间不稳定。");

        return new GraphProductionJourneyEvidence
        {
            Id = manifest.Id,
            Status = oraclePass ? GraphProductionEvidenceStatus.Pass : GraphProductionEvidenceStatus.Fail,
            WarmupCount = rounds.Min(static round => round.WarmupCount),
            Rounds = rounds.Count,
            SamplesPerRound = rounds.Min(static round => round.ElapsedMicroseconds?.Count ?? 0),
            P50Milliseconds = WorstRoundPercentile(rounds, static round => round.ElapsedMicroseconds, 0.50) / 1_000d,
            P95Milliseconds = WorstRoundPercentile(rounds, static round => round.ElapsedMicroseconds, 0.95) / 1_000d,
            P99Milliseconds = WorstRoundPercentile(rounds, static round => round.ElapsedMicroseconds, 0.99) / 1_000d,
            MaxMilliseconds = rounds.Max(static round => Maximum(round.ElapsedMicroseconds)) / 1_000d,
            ThroughputPerSecond = rounds.Min(static round => Throughput(round.ElapsedMicroseconds)),
            TimeToFirstRowP99Milliseconds = WorstRoundPercentile(rounds, static round => round.TimeToFirstRowMicroseconds, 0.99) / 1_000d,
            ColdFirstQueryP99Milliseconds = WorstRoundPercentile(rounds, static round => round.ColdFirstQueryMicroseconds, 0.99) / 1_000d,
            QueryPeakLiveBytes = rounds.Max(static round => Maximum(round.QueryPeakLiveBytes)),
            PeakWorkingSetBytes = rounds.Max(static round => Maximum(round.WorkingSetBytes)),
            AllocatedBytesP95 = (long)WorstRoundPercentile(rounds, static round => round.AllocatedBytes, 0.95),
            Gen0Collections = rounds.Sum(static round => Sum(round.Gen0Collections)),
            Gen1Collections = rounds.Sum(static round => Sum(round.Gen1Collections)),
            Gen2Collections = rounds.Sum(static round => Sum(round.Gen2Collections)),
            GcPauseP99Milliseconds = WorstRoundPercentile(rounds, static round => round.GcPauseMicroseconds, 0.99) / 1_000d,
            LogicalReadBytes = rounds.Max(static round => Maximum(round.LogicalReadBytes)),
            PhysicalReadBytes = rounds.Max(static round => Maximum(round.PhysicalReadBytes)),
            WalBytes = rounds.Max(static round => Maximum(round.WalBytes)),
            Candidates = rounds.Max(static round => Maximum(round.Candidates)),
            Examined = rounds.Max(static round => Maximum(round.Examined)),
            Returned = rounds.Max(static round => Maximum(round.Returned)),
            ExpandedEdges = rounds.Max(static round => Maximum(round.ExpandedEdges)),
            FrontierPeak = rounds.Max(static round => Maximum(round.FrontierPeak)),
            AccessPath = paths.Length == 1 ? paths[0] : string.Empty,
            FallbackReason = fallbacks.Length == 1 ? fallbacks[0] : null,
            Artifact = manifest.Artifact,
        };
    }

    private static async Task<GraphProductionCheckEvidence> RebuildCheckAsync(
        GraphProductionCheckEvidence summary,
        string gate,
        EvaluationContext context)
    {
        GraphProductionCheckArtifact? artifact = await LoadArtifactAsync(
            summary.Artifact,
            "m40-graph-check-evidence-v1",
            gate,
            summary.Id,
            GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact,
            static value => value.Schema,
            static value => value.Run,
            context,
            value => ValidateCheckArtifactBounds(value, gate, summary.Id, context)).ConfigureAwait(false);
        if (artifact is null)
            return summary;
        if (!string.Equals(artifact.CheckId, summary.Id, StringComparison.Ordinal))
            AddFinding(context.Findings, gate, "check_artifact_id", $"{summary.Id} artifact check_id 不匹配。");
        bool assertionsPass = ValidateCheckAssertions(artifact.Assertions, gate, summary.Id, context.Findings);
        var rebuilt = new GraphProductionCheckEvidence
        {
            Id = summary.Id,
            Status = assertionsPass ? GraphProductionEvidenceStatus.Pass : GraphProductionEvidenceStatus.Fail,
            Summary = artifact.Summary,
            Artifact = summary.Artifact,
        };
        if (!string.Equals(summary.Status, rebuilt.Status, StringComparison.Ordinal)
            || !string.Equals(summary.Summary, rebuilt.Summary, StringComparison.Ordinal))
        {
            AddFinding(context.Findings, gate, "check_summary_mismatch", $"{summary.Id} manifest 摘要与原始 assertion 重算结果不一致。");
        }
        return rebuilt;
    }

    private static bool ValidateCheckAssertions(
        IReadOnlyList<GraphProductionCheckAssertion>? assertions,
        string gate,
        string owner,
        List<GraphProductionGateFinding> findings)
    {
        assertions ??= [];
        bool pass = assertions.Count > 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphProductionCheckAssertion assertion in assertions)
        {
            if (assertion is null
                || string.IsNullOrWhiteSpace(assertion.Name)
                || !names.Add(assertion.Name)
                || string.IsNullOrWhiteSpace(assertion.Expected)
                || string.IsNullOrWhiteSpace(assertion.Actual))
            {
                pass = false;
                continue;
            }
            pass &= string.Equals(assertion.Expected, assertion.Actual, StringComparison.Ordinal);
        }
        if (!pass)
            AddFinding(findings, gate, "check_raw_assertions", $"{owner} 缺少非重复原始断言或期望/实际值不一致。");
        return pass;
    }

    private static bool ValidateSoakArtifactBounds(
        GraphProductionSoakArtifact artifact,
        EvaluationContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        bool valid = true;
        valid &= ValidateArtifactCollectionBound(artifact.CheckpointsUtc?.Count ?? 0, MaximumSoakCheckpointSamples, "both", "soak checkpoints", context.Findings);
        valid &= ValidateArtifactCollectionBound(artifact.KillReopenSamples?.Count ?? 0, MaximumSoakKillReopenSamples, "both", "soak kill/reopen samples", context.Findings);
        valid &= ValidateArtifactCollectionBound(artifact.ColdOpenMilliseconds?.Count ?? 0, MaximumSoakColdOpenSamples, "performance_capacity", "soak cold-open samples", context.Findings);
        valid &= ValidateArtifactCollectionBound(artifact.ResourceSamples?.Count ?? 0, MaximumSoakResourceSamples, "performance_capacity", "soak resource samples", context.Findings);
        return valid;
    }

    private static bool ValidateJourneyArtifactBounds(
        GraphProductionJourneyArtifact artifact,
        string owner,
        EvaluationContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<GraphProductionJourneyRoundArtifact> rounds = artifact.Rounds ?? [];
        if (!ValidateArtifactCollectionBound(rounds.Count, MaximumJourneyRounds, "both", $"{owner} journey rounds", context.Findings))
            return false;

        bool valid = true;
        foreach (GraphProductionJourneyRoundArtifact? round in rounds)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (round is null)
                continue;
            valid &= ValidateJourneySampleColumnBounds(round, owner, context.Findings);
            valid &= ValidateArtifactCollectionBound(
                round.OracleAssertions?.Count ?? 0,
                MaximumJourneyOracleAssertions,
                "correctness_recovery",
                $"{owner} round {round.Round} oracle assertions",
                context.Findings);
        }
        return valid;
    }

    private static bool ValidateJourneySampleColumnBounds(
        GraphProductionJourneyRoundArtifact round,
        string owner,
        List<GraphProductionGateFinding> findings)
    {
        (string Name, int Count)[] columns =
        [
            ("elapsed", round.ElapsedMicroseconds?.Count ?? 0),
            ("time-to-first-row", round.TimeToFirstRowMicroseconds?.Count ?? 0),
            ("cold-first-query", round.ColdFirstQueryMicroseconds?.Count ?? 0),
            ("allocated-bytes", round.AllocatedBytes?.Count ?? 0),
            ("query-peak-live-bytes", round.QueryPeakLiveBytes?.Count ?? 0),
            ("working-set-bytes", round.WorkingSetBytes?.Count ?? 0),
            ("logical-read-bytes", round.LogicalReadBytes?.Count ?? 0),
            ("physical-read-bytes", round.PhysicalReadBytes?.Count ?? 0),
            ("wal-bytes", round.WalBytes?.Count ?? 0),
            ("candidates", round.Candidates?.Count ?? 0),
            ("examined", round.Examined?.Count ?? 0),
            ("returned", round.Returned?.Count ?? 0),
            ("expanded-edges", round.ExpandedEdges?.Count ?? 0),
            ("frontier-peak", round.FrontierPeak?.Count ?? 0),
            ("gen0", round.Gen0Collections?.Count ?? 0),
            ("gen1", round.Gen1Collections?.Count ?? 0),
            ("gen2", round.Gen2Collections?.Count ?? 0),
            ("gc-pause", round.GcPauseMicroseconds?.Count ?? 0),
        ];
        bool valid = true;
        foreach ((string name, int count) in columns)
        {
            valid &= ValidateArtifactCollectionBound(
                count,
                MaximumJourneySamplesPerColumn,
                "both",
                $"{owner} round {round.Round} {name} samples",
                findings);
        }
        return valid;
    }

    private static bool ValidateCheckArtifactBounds(
        GraphProductionCheckArtifact artifact,
        string gate,
        string owner,
        EvaluationContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        return ValidateArtifactCollectionBound(
            artifact.Assertions?.Count ?? 0,
            MaximumCheckAssertions,
            gate,
            $"{owner} check assertions",
            context.Findings);
    }

    private static bool ValidateArtifactCollectionBound(
        int actual,
        int maximum,
        string gate,
        string collection,
        List<GraphProductionGateFinding> findings)
    {
        if (actual <= maximum)
            return true;
        AddFinding(findings, gate, "artifact_collection_bound", $"{collection} 条目数 {actual} 超过上限 {maximum}。");
        return false;
    }

    private static async Task<T?> LoadArtifactAsync<T>(
        GraphProductionArtifactEvidence reference,
        string expectedSchema,
        string gate,
        string owner,
        JsonTypeInfo<T> typeInfo,
        Func<T, string> schemaSelector,
        Func<T, GraphProductionArtifactRun> runSelector,
        EvaluationContext context,
        Func<T, bool>? boundsValidator = null)
        where T : class
    {
        string? path = ValidateArtifactReference(reference, gate, owner, context);
        if (path is null)
            return null;

        T? artifact;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumArtifactBytes)
            {
                AddFinding(
                    context.Findings,
                    gate,
                    "artifact_size_bound",
                    $"{owner} artifact 大小 {stream.Length} 字节超过上限 {MaximumArtifactBytes} 字节。");
                return null;
            }
            byte[] digest = await SHA256.HashDataAsync(stream, context.CancellationToken).ConfigureAwait(false);
            string actualDigest = Convert.ToHexString(digest).ToLowerInvariant();
            if (!string.Equals(actualDigest, reference.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                AddFinding(context.Findings, gate, "artifact_digest", $"{owner} artifact SHA-256 不匹配。");
                return null;
            }
            stream.Position = 0;
            artifact = await JsonSerializer.DeserializeAsync(
                stream,
                typeInfo,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            AddFinding(context.Findings, gate, "artifact_schema", $"{owner} artifact 无法按 {expectedSchema} 解析：{exception.Message}");
            return null;
        }
        if (artifact is null || !string.Equals(schemaSelector(artifact), expectedSchema, StringComparison.Ordinal))
        {
            AddFinding(context.Findings, gate, "artifact_schema", $"{owner} artifact schema 必须为 {expectedSchema}。");
            return null;
        }

        GraphProductionArtifactRun? run = runSelector(artifact);
        if (run?.Arguments is { Count: > MaximumArtifactArguments })
        {
            AddFinding(
                context.Findings,
                gate,
                "artifact_collection_bound",
                $"{owner} run arguments 条目数 {run.Arguments.Count} 超过上限 {MaximumArtifactArguments}。");
            return null;
        }
        if (run is null
            || run.Arguments is null
            || !string.Equals(run.CommitSha, context.CommitSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(run.Command, reference.Command, StringComparison.Ordinal)
            || !run.Arguments.SequenceEqual(reference.Arguments ?? [], StringComparer.Ordinal)
            || !string.Equals(run.WorkingDirectory, reference.WorkingDirectory, StringComparison.Ordinal)
            || run.ExitCode != reference.ExpectedExitCode)
        {
            AddFinding(context.Findings, gate, "artifact_run_metadata", $"{owner} artifact 的 commit、命令、参数、工作目录或退出码与 manifest 不一致。");
            return null;
        }
        if (boundsValidator is not null && !boundsValidator(artifact))
            return null;

        await ReplayArtifactAsync(reference, path, gate, owner, context).ConfigureAwait(false);
        return artifact;
    }

    private static string? ValidateArtifactReference(
        GraphProductionArtifactEvidence reference,
        string gate,
        string owner,
        EvaluationContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (reference?.Arguments is { Count: > MaximumArtifactArguments })
        {
            AddFinding(
                context.Findings,
                gate,
                "artifact_collection_bound",
                $"{owner} manifest arguments 条目数 {reference.Arguments.Count} 超过上限 {MaximumArtifactArguments}。");
            return null;
        }
        if (reference is null
            || reference.Arguments is null
            || reference.Arguments.Any(static argument => string.IsNullOrWhiteSpace(argument))
            || string.IsNullOrWhiteSpace(reference.Path)
            || !IsSha256(reference.Sha256)
            || string.IsNullOrWhiteSpace(reference.Command)
            || string.IsNullOrWhiteSpace(reference.WorkingDirectory)
            || reference.ExpectedExitCode != 0
            || reference.TimeoutSeconds is < 1 or > 3_600
            || reference.Arguments.Count(static argument =>
                string.Equals(argument, ArtifactArgumentPlaceholder, StringComparison.Ordinal)) != 1
            || !IsAllowedReplayCommand(reference.Command, reference.Arguments, context.RepositoryRoot)
            || Path.IsPathFullyQualified(reference.Path)
            || Path.IsPathFullyQualified(reference.WorkingDirectory))
        {
            AddFinding(context.Findings, gate, "artifact_metadata", $"{owner} 缺少可移植 path、SHA-256 或结构化复现命令元数据。");
            return null;
        }

        if (!TryResolveWithin(context.ArtifactRoot, reference.Path, out string path))
        {
            AddFinding(context.Findings, gate, "artifact_path", $"{owner} artifact 路径越过 manifest 目录。");
            return null;
        }
        if (!File.Exists(path))
        {
            AddFinding(context.Findings, gate, "artifact_missing", $"{owner} artifact 不存在：{reference.Path}。");
            return null;
        }
        if (!TryResolveWithin(context.RepositoryRoot, reference.WorkingDirectory, out string workingDirectory)
            || !Directory.Exists(workingDirectory))
        {
            AddFinding(context.Findings, gate, "artifact_working_directory", $"{owner} 复现工作目录不存在或越过仓库根目录。");
            return null;
        }
        return path;
    }

    private static async Task ReplayArtifactAsync(
        GraphProductionArtifactEvidence reference,
        string artifactPath,
        string gate,
        string owner,
        EvaluationContext context)
    {
        string workingDirectory = Path.GetFullPath(Path.Combine(context.RepositoryRoot, reference.WorkingDirectory));
        string cacheKey = string.Join('\u001f',
            reference.Command,
            workingDirectory,
            artifactPath,
            reference.ExpectedExitCode.ToString(CultureInfo.InvariantCulture),
            reference.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            string.Join('\u001e', reference.Arguments));
        if (!context.Replays.TryGetValue(cacheKey, out GraphEvidenceProcessResult? result))
        {
            if (context.Replays.Count >= MaximumUniqueReplayCount)
            {
                AddFinding(
                    context.Findings,
                    "both",
                    "artifact_replay_bound",
                    $"唯一 artifact 复现命令超过 {MaximumUniqueReplayCount} 项，后续命令未启动。");
                return;
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            string[] arguments = reference.Arguments
                .Select(argument => string.Equals(argument, ArtifactArgumentPlaceholder, StringComparison.Ordinal)
                    ? artifactPath
                    : argument)
                .ToArray();
            result = RunReplayProcess(
                reference.Command,
                arguments,
                workingDirectory,
                reference.TimeoutSeconds,
                context.CancellationToken);
            context.Replays.Add(cacheKey, result);
        }
        if (!result.Completed || result.ExitCode != reference.ExpectedExitCode)
        {
            AddFinding(
                context.Findings,
                gate,
                "artifact_reproduction",
                $"{owner} 复现命令失败、超时、取消、回收不完整或退出码不匹配"
                + $"（expected={reference.ExpectedExitCode}, actual={result.ExitCode}; {result.Diagnostic}）。");
        }
        if (!await DigestMatchesAsync(
            artifactPath,
            reference.Sha256,
            context.CancellationToken).ConfigureAwait(false))
            AddFinding(context.Findings, gate, "artifact_changed_by_replay", $"{owner} artifact 在复现命令执行后发生变化。");
    }

    private static void ValidateRequiredChecks(
        IReadOnlyList<GraphProductionCheckEvidence> checks,
        IReadOnlyList<string> requiredIds,
        string gate,
        List<GraphProductionGateFinding> findings)
    {
        IReadOnlyDictionary<string, GraphProductionCheckEvidence> byId = IndexById(checks, gate, findings);
        foreach (string requiredId in requiredIds)
        {
            if (!byId.TryGetValue(requiredId, out GraphProductionCheckEvidence? check))
            {
                AddFinding(findings, gate, "check_missing", $"缺少必需检查 {requiredId}。");
                continue;
            }
            if (!string.Equals(check.Status, GraphProductionEvidenceStatus.Pass, StringComparison.Ordinal))
                AddFinding(findings, gate, "check_not_pass", $"检查 {requiredId} 的原始 assertions 未 PASS。");
            if (string.IsNullOrWhiteSpace(check.Summary))
                AddFinding(findings, gate, "check_summary", $"检查 {requiredId} 缺少可审计摘要。");
        }
        foreach (string actualId in byId.Keys)
        {
            if (!requiredIds.Contains(actualId, StringComparer.Ordinal))
                AddFinding(findings, gate, "check_unknown", $"Production 清单包含未冻结检查 {actualId}。");
        }
    }

    private static IReadOnlyDictionary<string, GraphProductionCheckEvidence> IndexById(
        IReadOnlyList<GraphProductionCheckEvidence> checks,
        string gate,
        List<GraphProductionGateFinding> findings)
    {
        var result = new Dictionary<string, GraphProductionCheckEvidence>(StringComparer.Ordinal);
        foreach (GraphProductionCheckEvidence check in checks)
        {
            if (check is null)
            {
                AddFinding(findings, gate, "check_null", "检查项不能为 null。");
                continue;
            }
            if (string.IsNullOrWhiteSpace(check.Id) || !result.TryAdd(check.Id, check))
                AddFinding(findings, gate, "check_id", "检查 ID 不能为空或重复。");
        }
        return result;
    }

    private static void ValidateJourneys(
        IReadOnlyList<GraphProductionJourneyEvidence> journeys,
        List<GraphProductionGateFinding> findings)
    {
        var byId = new Dictionary<string, GraphProductionJourneyEvidence>(StringComparer.Ordinal);
        foreach (GraphProductionJourneyEvidence journey in journeys)
        {
            if (journey is null)
            {
                AddFinding(findings, "both", "journey_null", "journey 不能为 null。");
                continue;
            }
            if (string.IsNullOrWhiteSpace(journey.Id) || !byId.TryAdd(journey.Id, journey))
                AddFinding(findings, "both", "journey_id", "journey ID 不能为空或重复。");
        }

        foreach ((string id, JourneySpec spec) in JourneySpecs)
        {
            if (!byId.TryGetValue(id, out GraphProductionJourneyEvidence? journey))
            {
                AddFinding(findings, "both", "journey_missing", $"缺少冻结 journey {id}。");
                continue;
            }
            if (!string.Equals(journey.Status, GraphProductionEvidenceStatus.Pass, StringComparison.Ordinal))
                AddFinding(findings, "correctness_recovery", "journey_oracle", $"{id} 原始逐项 oracle 未 PASS。");
            if (journey.WarmupCount < 1_000 || journey.Rounds < 3 || journey.SamplesPerRound < 10_000)
            {
                AddFinding(findings, "performance_capacity", "journey_samples", $"{id} 必须有 1,000 warmup、3 个独立轮次且每轮至少 10,000 个完整消费样本。");
            }
            ValidateJourneyMetrics(journey, spec, findings);
            ValidateJourneyPath(journey, spec.Path, findings);
        }
        foreach (string actualId in byId.Keys)
        {
            if (!JourneySpecs.ContainsKey(actualId))
                AddFinding(findings, "both", "journey_unknown", $"Production 清单包含未冻结 journey {actualId}。");
        }
    }

    private static void ValidateJourneyMetrics(
        GraphProductionJourneyEvidence journey,
        JourneySpec spec,
        List<GraphProductionGateFinding> findings)
    {
        if (journey.QueryPeakLiveBytes < 0 || journey.QueryPeakLiveBytes > spec.MemoryMiB * MiB)
            AddFinding(findings, "performance_capacity", "query_memory", $"{journey.Id} query-owned peak 超过 {spec.MemoryMiB} MiB 或计数无效。");
        if (journey.PeakWorkingSetBytes <= 0 || journey.PeakWorkingSetBytes > MaximumWorkingSet)
            AddFinding(findings, "performance_capacity", "working_set", $"{journey.Id} working set 必须在 0~12 GiB 内。");
        if (journey.AllocatedBytesP95 < 0 || journey.AllocatedBytesP95 > spec.MemoryMiB * MiB)
            AddFinding(findings, "performance_capacity", "allocation", $"{journey.Id} 每查询 allocation P95 必须在 0~{spec.MemoryMiB} MiB 内。");

        long sampleUnits = Math.Max(1, (long)Math.Ceiling(
            journey.Rounds * (double)journey.SamplesPerRound / SamplesPerGcRateUnit));
        if (journey.Gen0Collections < 0
            || journey.Gen1Collections < 0
            || journey.Gen2Collections < 0
            || journey.Gen0Collections > 100 * sampleUnits
            || journey.Gen1Collections > 10 * sampleUnits
            || journey.Gen2Collections > sampleUnits)
        {
            AddFinding(findings, "performance_capacity", "gc_rate", $"{journey.Id} 超过每 1,000 样本 Gen0/Gen1/Gen2 100/10/1 次阈值。");
        }
        if (!double.IsFinite(journey.GcPauseP99Milliseconds)
            || journey.GcPauseP99Milliseconds < 0
            || journey.GcPauseP99Milliseconds > MaximumGcPauseP99Milliseconds)
        {
            AddFinding(findings, "performance_capacity", "gc_pause", $"{journey.Id} GC pause P99 必须在 0~{MaximumGcPauseP99Milliseconds} ms 内。");
        }
        if (journey.LogicalReadBytes < 0
            || journey.PhysicalReadBytes < 0
            || journey.WalBytes < 0
            || journey.Candidates < 0
            || journey.Examined < 0
            || journey.Returned < 0
            || journey.ExpandedEdges < 0
            || journey.FrontierPeak < 0)
        {
            AddFinding(findings, "performance_capacity", "journey_counters", $"{journey.Id} 缺少非负资源/访问计数器。");
        }
        if (journey.ThroughputPerSecond <= 0
            || (spec.Path != JourneyPath.BoundedRelationFallback && (journey.Returned <= 0 || journey.ExpandedEdges <= 0)))
        {
            AddFinding(findings, "performance_capacity", "journey_work", $"{journey.Id} 缺少有效吞吐或完整消费工作量计数。");
        }

        if (spec.Path == JourneyPath.BoundedRelationFallback)
            return;
        if (journey.P50Milliseconds < 0
            || journey.P50Milliseconds > journey.P95Milliseconds
            || journey.P95Milliseconds > journey.P99Milliseconds
            || journey.P99Milliseconds > journey.MaxMilliseconds)
        {
            AddFinding(findings, "performance_capacity", "latency_order", $"{journey.Id} 延迟分位数顺序无效。");
        }
        if (journey.P95Milliseconds > spec.P95Milliseconds || journey.P99Milliseconds > spec.P99Milliseconds)
            AddFinding(findings, "performance_capacity", "latency_slo", $"{journey.Id} 超过 Production P95/P99 {spec.P95Milliseconds}/{spec.P99Milliseconds} ms。");
        double coldFirstQueryLimit = Math.Max(4 * journey.P99Milliseconds, 2_000);
        if (journey.ColdFirstQueryP99Milliseconds <= 0 || journey.ColdFirstQueryP99Milliseconds > coldFirstQueryLimit)
            AddFinding(findings, "performance_capacity", "cold_first_query", $"{journey.Id} 冷首查 P99 必须在 0~{coldFirstQueryLimit:F3} ms 内。");
    }

    private static void ValidateJourneyPath(
        GraphProductionJourneyEvidence journey,
        JourneyPath expected,
        List<GraphProductionGateFinding> findings)
    {
        string path = journey.AccessPath;
        bool pathPass = expected switch
        {
            JourneyPath.Native => path.Contains("native_", StringComparison.Ordinal)
                && !path.Contains("relation_", StringComparison.Ordinal)
                && !path.Contains("table_join", StringComparison.Ordinal)
                && !path.Contains("full_scan", StringComparison.Ordinal),
            JourneyPath.HybridNative => path.Contains("hybrid", StringComparison.Ordinal)
                && path.Contains("native_adjacency", StringComparison.Ordinal)
                && !path.Contains("full_scan", StringComparison.Ordinal),
            JourneyPath.RelationIndex => path.Contains("relation_index_seek", StringComparison.Ordinal)
                && !path.Contains("relation_scan", StringComparison.Ordinal),
            JourneyPath.BoundedRelationFallback => string.Equals(path, "relation_scan_fallback", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(journey.FallbackReason),
            _ => false,
        };
        if (!pathPass)
            AddFinding(findings, "performance_capacity", "access_path", $"{journey.Id} 实际 access path 不符合冻结合同：{path}。");
        if (expected != JourneyPath.BoundedRelationFallback && !string.IsNullOrWhiteSpace(journey.FallbackReason))
            AddFinding(findings, "performance_capacity", "unexpected_fallback", $"{journey.Id} 出现未允许回退：{journey.FallbackReason}。");
    }

    private static void ValidateDataset(
        GraphProductionDatasetEvidence dataset,
        List<GraphProductionGateFinding> findings)
    {
        if (!string.Equals(dataset.Tier, "production-soak", StringComparison.Ordinal)
            || !string.Equals(dataset.Generator, "m40-graph-generator-v1", StringComparison.Ordinal)
            || !string.Equals(dataset.Seed, "0x534F4E4E45544442", StringComparison.Ordinal)
            || dataset.VertexCount != 1_000_000
            || dataset.EdgeCount != 10_000_000)
        {
            AddFinding(findings, "performance_capacity", "dataset_contract", "Production 必须使用冻结的 1m vertex/10m edge production-soak 数据集。");
        }
        if (!IsSha256(dataset.InputDigest) || !IsSha256(dataset.OutputDigest))
            AddFinding(findings, "both", "dataset_digest", "生成器输入和输出必须记录 SHA-256。");
    }

    private static void ValidateEnvironment(
        GraphProductionEnvironmentEvidence environment,
        List<GraphProductionGateFinding> findings)
    {
        bool memoryMatches = environment.PhysicalMemoryBytes >= 63L * GiB && environment.PhysicalMemoryBytes <= 65L * GiB;
        bool matches = environment.OsDescription.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(environment.OsBuild)
            && string.Equals(environment.Architecture, "X64", StringComparison.OrdinalIgnoreCase)
            && environment.CpuName.Contains("Ultra 9 185H", StringComparison.OrdinalIgnoreCase)
            && environment.PhysicalCoreCount == 16
            && environment.LogicalProcessorCount == 22
            && memoryMatches
            && environment.DiskName.Contains("SN8000S", StringComparison.OrdinalIgnoreCase)
            && string.Equals(environment.DiskFormat, "NTFS", StringComparison.OrdinalIgnoreCase)
            && environment.Runtime.Contains(".NET 10", StringComparison.OrdinalIgnoreCase)
            && environment.SdkVersion.StartsWith("10.", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(environment.GcMode)
            && environment.PowerProfile.Contains("Best performance", StringComparison.OrdinalIgnoreCase);
        if (!matches)
            AddFinding(findings, "performance_capacity", "fixed_hardware", "运行环境不匹配 #341 冻结的 Windows x64 固定目标机合同。");
    }

    private static void ValidateSoak(
        GraphProductionGateInput input,
        List<GraphProductionGateFinding> findings)
    {
        GraphProductionSoakEvidence soak = input.Soak;
        double wallClockHours = (input.FinishedUtc - input.StartedUtc).TotalHours;
        if (soak.DurationHours < 168 || wallClockHours < 168)
            AddFinding(findings, "performance_capacity", "soak_duration", "Production mixed workload 必须连续运行至少 168 小时。");
        if (soak.ReaderWorkers != 8 || soak.UpdateWorkers != 1)
            AddFinding(findings, "performance_capacity", "soak_workers", "mixed workload 必须使用 8 reader + 1 update worker。");
        if (!string.Equals(soak.UpdateProfile, "m40-frozen-update-profile-v1", StringComparison.Ordinal))
            AddFinding(findings, "performance_capacity", "update_profile", "更新速率必须使用 #341 冻结 profile。");
        int minimumCheckpoints = (int)Math.Floor(soak.DurationHours * 2);
        if (soak.MaximumCheckpointIntervalMinutes <= 0
            || soak.MaximumCheckpointIntervalMinutes > 30
            || soak.CheckpointCount < minimumCheckpoints)
        {
            AddFinding(findings, "correctness_recovery", "checkpoint_schedule", "soak 必须每 30 分钟 checkpoint 且不得缺失周期。");
        }
        if (soak.KillReopenCount < 7 || soak.InvariantCheckCount < soak.KillReopenCount)
            AddFinding(findings, "correctness_recovery", "kill_reopen_schedule", "soak 必须每 24 小时真进程 kill/reopen 并完成 invariant check。");
        if (soak.FailedOperationCount != 0 || soak.UnexpectedRestartCount != 0)
            AddFinding(findings, "correctness_recovery", "soak_failures", "soak 存在失败操作或非计划重启。");
        if (!soak.SyncWalOnEveryWrite
            || !soak.AutoCheckpointEnabled
            || soak.MaxWalBytes != ExpectedWalBudget
            || soak.MaxOverlayEntries != 100_000)
        {
            AddFinding(findings, "both", "durability_options", "soak 必须保持默认 fsync/checkpoint/WAL/overlay 耐久配置。");
        }
        if (soak.PeakWorkingSetBytes <= 0 || soak.PeakWorkingSetBytes > MaximumWorkingSet || soak.WalBytes <= 0)
            AddFinding(findings, "performance_capacity", "soak_resources", "soak working set/WAL 资源计数无效或超过 12 GiB。");
        if (soak.ColdOpenP95Milliseconds <= 0
            || soak.ColdOpenP95Milliseconds > 2_000
            || soak.ColdOpenP99Milliseconds < soak.ColdOpenP95Milliseconds
            || soak.ColdOpenP99Milliseconds > 5_000)
        {
            AddFinding(findings, "performance_capacity", "cold_open", "冷启动 open P95/P99 必须在 0~2,000/5,000 ms 内且分位数有序。");
        }
        if (soak.RecoveryP50Milliseconds <= 0
            || soak.RecoveryP50Milliseconds > soak.RecoveryP95Milliseconds
            || soak.RecoveryP95Milliseconds > soak.RecoveryP99Milliseconds)
        {
            AddFinding(findings, "correctness_recovery", "recovery_latency", "kill/reopen 恢复 P50/P95/P99 必须存在且分位数有序。");
        }
    }

    private static async Task ValidateGapsAsync(
        IReadOnlyList<GraphProductionGapEvidence> gaps,
        EvaluationContext context,
        List<GraphProductionGateFinding> findings)
    {
        if (gaps.Count == 0)
        {
            AddFinding(findings, "both", "gap_catalog_missing", "Production 报告必须包含 #341 capability gap catalog 快照。");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphProductionGapEvidence gap in gaps)
        {
            if (gap is null)
            {
                AddFinding(findings, "both", "gap_null", "gap 不能为 null。");
                continue;
            }
            if (string.IsNullOrWhiteSpace(gap.Id) || !ids.Add(gap.Id))
            {
                AddFinding(findings, "both", "gap_id", "gap ID 不能为空或重复。");
                continue;
            }
            if (!RequiredGapIds.Contains(gap.Id, StringComparer.Ordinal))
                AddFinding(findings, "both", "gap_unknown", $"capability gap catalog 包含未冻结 ID {gap.Id}。");

            bool blocksProduction = gap.Blocks.Contains("Production", StringComparison.OrdinalIgnoreCase)
                || gap.Blocks.Contains("Couplet C4", StringComparison.OrdinalIgnoreCase);
            if (blocksProduction && gap.Status is "open" or "in_progress" or "not_planned")
            {
                AddFinding(findings, GateForSeverity(gap.Severity), "blocking_gap", $"{gap.Id} 仍阻塞 {gap.Blocks}。");
            }
            else if (string.Equals(gap.Status, "closed", StringComparison.Ordinal))
            {
                if (gap.CloseEvidence is null)
                {
                    AddFinding(findings, "both", "gap_close_evidence", $"{gap.Id} 标记 closed 但没有关闭 evidence。");
                }
                else
                {
                    GraphProductionCheckArtifact? artifact = await LoadArtifactAsync(
                        gap.CloseEvidence,
                        "m40-graph-check-evidence-v1",
                        "both",
                        gap.Id,
                        GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact,
                        static value => value.Schema,
                        static value => value.Run,
                        context,
                        value => ValidateCheckArtifactBounds(value, "both", gap.Id, context)).ConfigureAwait(false);
                    if (artifact is not null)
                    {
                        if (!string.Equals(artifact.CheckId, gap.Id, StringComparison.Ordinal))
                            AddFinding(findings, "both", "gap_artifact_id", $"{gap.Id} 关闭 artifact ID 不匹配。");
                        _ = ValidateCheckAssertions(artifact.Assertions, "both", gap.Id, findings);
                    }
                }
            }
            else if (gap.Status is not ("open" or "in_progress" or "not_planned"))
            {
                AddFinding(findings, "both", "gap_status", $"{gap.Id} 的 gap status 无效：{gap.Status}。");
            }
        }
        foreach (string requiredId in RequiredGapIds)
        {
            if (!ids.Contains(requiredId))
                AddFinding(findings, "both", "gap_missing", $"capability gap catalog 缺少 {requiredId}。");
        }
    }

    private static string GateForSeverity(string severity)
        => severity switch
        {
            "correctness_recovery" => "correctness_recovery",
            "capacity_latency" or "api_product" => "performance_capacity",
            _ => "both",
        };

    private static bool JourneySummariesEqual(
        GraphProductionJourneyEvidence left,
        GraphProductionJourneyEvidence right)
        => left with { Artifact = new GraphProductionArtifactEvidence() }
            == right with { Artifact = new GraphProductionArtifactEvidence() };

    private static bool AllSampleColumnsHaveCount(GraphProductionJourneyRoundArtifact round, int count)
        => round.TimeToFirstRowMicroseconds?.Count == count
            && round.ColdFirstQueryMicroseconds is { Count: > 0 }
            && round.AllocatedBytes?.Count == count
            && round.QueryPeakLiveBytes?.Count == count
            && round.WorkingSetBytes?.Count == count
            && round.LogicalReadBytes?.Count == count
            && round.PhysicalReadBytes?.Count == count
            && round.WalBytes?.Count == count
            && round.Candidates?.Count == count
            && round.Examined?.Count == count
            && round.Returned?.Count == count
            && round.ExpandedEdges?.Count == count
            && round.FrontierPeak?.Count == count
            && round.Gen0Collections?.Count == count
            && round.Gen1Collections?.Count == count
            && round.Gen2Collections?.Count == count
            && round.GcPauseMicroseconds?.Count == count;

    private static bool AllSampleColumnsNonNegative(GraphProductionJourneyRoundArtifact round)
        => AllNonNegative(round.ElapsedMicroseconds)
            && AllNonNegative(round.TimeToFirstRowMicroseconds)
            && AllNonNegative(round.ColdFirstQueryMicroseconds)
            && AllNonNegative(round.AllocatedBytes)
            && AllNonNegative(round.QueryPeakLiveBytes)
            && AllNonNegative(round.WorkingSetBytes)
            && AllNonNegative(round.LogicalReadBytes)
            && AllNonNegative(round.PhysicalReadBytes)
            && AllNonNegative(round.WalBytes)
            && AllNonNegative(round.Candidates)
            && AllNonNegative(round.Examined)
            && AllNonNegative(round.Returned)
            && AllNonNegative(round.ExpandedEdges)
            && AllNonNegative(round.FrontierPeak)
            && AllNonNegative(round.Gen0Collections)
            && AllNonNegative(round.Gen1Collections)
            && AllNonNegative(round.Gen2Collections)
            && AllNonNegative(round.GcPauseMicroseconds);

    private static bool AllNonNegative(IReadOnlyList<long>? values)
        => values is not null && values.All(static value => value >= 0);

    private static bool IsStrictlyIncreasing(IReadOnlyList<DateTimeOffset> values)
    {
        for (int index = 1; index < values.Count; index++)
        {
            if (values[index] <= values[index - 1])
                return false;
        }
        return true;
    }

    private static double ComputeMaximumIntervalMinutes(
        DateTimeOffset startedUtc,
        DateTimeOffset finishedUtc,
        IReadOnlyList<DateTimeOffset> checkpoints)
    {
        if (checkpoints.Count == 0 || finishedUtc <= startedUtc)
            return 0;
        double maximum = (checkpoints[0] - startedUtc).TotalMinutes;
        for (int index = 1; index < checkpoints.Count; index++)
            maximum = Math.Max(maximum, (checkpoints[index] - checkpoints[index - 1]).TotalMinutes);
        return Math.Max(maximum, (finishedUtc - checkpoints[^1]).TotalMinutes);
    }

    private static double ComputeMaximumIntervalHours(
        DateTimeOffset startedUtc,
        DateTimeOffset finishedUtc,
        IReadOnlyList<DateTimeOffset> samples)
    {
        if (samples.Count == 0 || finishedUtc <= startedUtc)
            return double.PositiveInfinity;
        double maximum = (samples[0] - startedUtc).TotalHours;
        for (int index = 1; index < samples.Count; index++)
            maximum = Math.Max(maximum, (samples[index] - samples[index - 1]).TotalHours);
        return Math.Max(maximum, (finishedUtc - samples[^1]).TotalHours);
    }

    private static double WorstRoundPercentile(
        IReadOnlyList<GraphProductionJourneyRoundArtifact> rounds,
        Func<GraphProductionJourneyRoundArtifact, IReadOnlyList<long>?> selector,
        double percentile)
        => rounds.Max(round => Percentile((selector(round) ?? []).Order().ToArray(), percentile));

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0)
            return 0;
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Count) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }

    private static long Percentile(IReadOnlyList<long> ordered, double percentile)
    {
        if (ordered.Count == 0)
            return 0;
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Count) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }

    private static long Maximum(IReadOnlyList<long>? values)
        => values is null || values.Count == 0 ? 0 : values.Max();

    private static long Sum(IReadOnlyList<long>? values)
        => values is null ? 0 : values.Sum();

    private static double Throughput(IReadOnlyList<long>? elapsedMicroseconds)
    {
        if (elapsedMicroseconds is null || elapsedMicroseconds.Count == 0)
            return 0;
        long total = elapsedMicroseconds.Sum();
        return total <= 0 ? 0 : elapsedMicroseconds.Count * 1_000_000d / total;
    }

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    private static bool IsWithin(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathFullyQualified(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsAllowedReplayCommand(
        string command,
        IReadOnlyList<string> arguments,
        string repositoryRoot)
    {
        if (!string.Equals(command, "dotnet", StringComparison.OrdinalIgnoreCase) || arguments.Count == 0)
            return false;
        if (string.Equals(arguments[0], "exec", StringComparison.Ordinal))
        {
            return arguments.Count == 4
                && PathsEqual(arguments[1], typeof(GraphProductionGateEvaluator).Assembly.Location)
                && string.Equals(arguments[2], "--m40-verify-artifact", StringComparison.Ordinal)
                && string.Equals(arguments[3], ArtifactArgumentPlaceholder, StringComparison.Ordinal);
        }
        if (string.Equals(arguments[0], "run", StringComparison.Ordinal))
        {
            int projectIndex = -1;
            for (int index = 1; index < arguments.Count; index++)
            {
                if (string.Equals(arguments[index], "--project", StringComparison.Ordinal))
                {
                    projectIndex = index;
                    break;
                }
            }
            if (projectIndex < 0 || projectIndex + 1 >= arguments.Count)
                return false;
            return TryResolveWithin(repositoryRoot, arguments[projectIndex + 1], out _);
        }
        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool TryResolveWithin(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            return IsWithin(fullPath, root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static async Task<bool> DigestMatchesAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumArtifactBytes)
                return false;
            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexString(digest).ToLowerInvariant();
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static GraphEvidenceProcessResult RunCapturedProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken)
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
        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
            startInfo,
            TimeSpan.FromSeconds(timeoutSeconds),
            captureOutput: true,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static GraphEvidenceProcessResult RunReplayProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken)
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
        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
            startInfo,
            TimeSpan.FromSeconds(timeoutSeconds),
            captureOutput: false,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool IsSha1(string value)
        => value.Length == 40
            && value.All(static character => Uri.IsHexDigit(character))
            && value.Any(static character => character != '0');

    private static bool IsSha256(string value)
        => value.Length == 64
            && value.All(static character => Uri.IsHexDigit(character))
            && value.Any(static character => character != '0');

    private static void AddFinding(
        List<GraphProductionGateFinding> findings,
        string gate,
        string code,
        string message)
        => findings.Add(new GraphProductionGateFinding
        {
            Gate = gate,
            Code = code,
            Message = message,
        });

    private sealed record EvaluationContext(
        string ArtifactRoot,
        string RepositoryRoot,
        string CommitSha,
        List<GraphProductionGateFinding> Findings,
        CancellationToken CancellationToken)
    {
        public Dictionary<string, GraphEvidenceProcessResult> Replays { get; } = new(StringComparer.Ordinal);
    }

    private sealed record JourneySpec(
        double P95Milliseconds,
        double P99Milliseconds,
        int MemoryMiB,
        JourneyPath Path);

    private enum JourneyPath : byte
    {
        Native = 1,
        HybridNative = 2,
        RelationIndex = 3,
        BoundedRelationFallback = 4,
    }
}
