using System.Globalization;
using System.Security.Cryptography;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>按 #341 冻结合同严格判定 M40 #367 双门禁。</summary>
public static class GraphProductionGateEvaluator
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;
    private const long ExpectedWalBudget = 256L * MiB;
    private const long MaximumWorkingSet = 12L * GiB;
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

    /// <summary>判定证据清单；PASS artifact 必须存在且 SHA-256 匹配。</summary>
    /// <param name="input">原始证据清单。</param>
    /// <param name="artifactBaseDirectory">相对 artifact 路径的基准目录。</param>
    /// <returns>带双 gate 和 findings 的报告。</returns>
    public static GraphProductionGateReport Evaluate(
        GraphProductionGateInput input,
        string artifactBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactBaseDirectory);
        string artifactRoot = Path.GetFullPath(artifactBaseDirectory);
        var findings = new List<GraphProductionGateFinding>();
        input = NormalizeInput(input, findings);
        string localSmoke = GetLocalSmoke(input);

        if (!input.ProductionRun)
        {
            AddFinding(
                findings,
                "release",
                "production_not_attempted",
                "quick/local evidence 不能替代 #367 Production 门禁。");
            return CreateReport(
                input,
                localSmoke,
                GraphProductionEvidenceStatus.NotRun,
                GraphProductionEvidenceStatus.NotRun,
                findings);
        }

        ValidateCommon(input, findings);
        ValidateRequiredChecks(
            input.CorrectnessRecoveryChecks,
            RequiredCorrectnessChecks,
            "correctness_recovery",
            artifactRoot,
            findings);
        ValidateRequiredChecks(
            input.PerformanceCapacityChecks,
            RequiredPerformanceChecks,
            "performance_capacity",
            artifactRoot,
            findings);
        ValidateJourneys(input.Journeys, artifactRoot, findings);
        ValidateDataset(input.Dataset, findings);
        ValidateEnvironment(input.Environment, findings);
        ValidateSoak(input, findings);
        ValidateGaps(input.Gaps, artifactRoot, findings);

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
        if (!string.Equals(input.Schema, "m40-graph-production-input-v1", StringComparison.Ordinal))
            AddFinding(findings, "both", "input_schema", "证据清单 schema 必须为 m40-graph-production-input-v1。");
        if (!IsSha1(input.CommitSha))
            AddFinding(findings, "both", "commit_sha", "Production 证据必须绑定 40 位 clean commit SHA。");
        if (input.StartedUtc == default || input.FinishedUtc <= input.StartedUtc)
            AddFinding(findings, "both", "run_interval", "运行开始/结束 UTC 时间无效。");
        else if (input.FinishedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            AddFinding(findings, "both", "run_interval_future", "运行结束时间不能位于未来。");
    }

    private static void ValidateRequiredChecks(
        IReadOnlyList<GraphProductionCheckEvidence> checks,
        IReadOnlyList<string> requiredIds,
        string gate,
        string artifactRoot,
        List<GraphProductionGateFinding> findings)
    {
        IReadOnlyDictionary<string, GraphProductionCheckEvidence> byId = IndexById(
            checks,
            gate,
            findings);
        foreach (string requiredId in requiredIds)
        {
            if (!byId.TryGetValue(requiredId, out GraphProductionCheckEvidence? check))
            {
                AddFinding(findings, gate, "check_missing", $"缺少必需检查 {requiredId}。");
                continue;
            }

            if (!string.Equals(check.Status, GraphProductionEvidenceStatus.Pass, StringComparison.Ordinal))
            {
                AddFinding(
                    findings,
                    gate,
                    "check_not_pass",
                    $"检查 {requiredId} 状态为 {check.Status}，Production 提交不允许缺项或重试后模糊通过。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(check.Summary))
                AddFinding(findings, gate, "check_summary", $"检查 {requiredId} 缺少可审计摘要。");

            ValidateArtifact(check.Artifact, artifactRoot, gate, requiredId, findings);
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
        string artifactRoot,
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
            {
                AddFinding(findings, "correctness_recovery", "journey_oracle", $"{id} 逐项 oracle 未 PASS。");
            }
            else
            {
                ValidateArtifact(journey.Artifact, artifactRoot, "both", id, findings);
            }

            if (journey.WarmupCount < 1_000 || journey.Rounds < 3 || journey.SamplesPerRound < 10_000)
            {
                AddFinding(
                    findings,
                    "performance_capacity",
                    "journey_samples",
                    $"{id} 必须有 1,000 warmup、3 个独立轮次且每轮至少 10,000 个完整消费样本。");
            }

            ValidateJourneyMetrics(journey, spec, findings);
            ValidateJourneyPath(journey, spec.Path, findings);
        }
    }

    private static void ValidateJourneyMetrics(
        GraphProductionJourneyEvidence journey,
        JourneySpec spec,
        List<GraphProductionGateFinding> findings)
    {
        if (journey.QueryPeakLiveBytes < 0
            || journey.QueryPeakLiveBytes > spec.MemoryMiB * MiB)
        {
            AddFinding(
                findings,
                "performance_capacity",
                "query_memory",
                $"{journey.Id} query-owned peak 超过 {spec.MemoryMiB} MiB 或计数无效。");
        }
        if (journey.PeakWorkingSetBytes <= 0 || journey.PeakWorkingSetBytes > MaximumWorkingSet)
            AddFinding(findings, "performance_capacity", "working_set", $"{journey.Id} working set 必须在 0~12 GiB 内。");
        if (journey.AllocatedBytesP95 < 0
            || journey.LogicalReadBytes < 0
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
            || (spec.Path != JourneyPath.BoundedRelationFallback
                && (journey.Returned <= 0 || journey.ExpandedEdges <= 0)))
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
        if (journey.P95Milliseconds > spec.P95Milliseconds
            || journey.P99Milliseconds > spec.P99Milliseconds)
        {
            AddFinding(
                findings,
                "performance_capacity",
                "latency_slo",
                $"{journey.Id} 超过 Production P95/P99 {spec.P95Milliseconds}/{spec.P99Milliseconds} ms。");
        }
        double coldFirstQueryLimit = Math.Max(4 * journey.P99Milliseconds, 2_000);
        if (journey.ColdFirstQueryP99Milliseconds <= 0
            || journey.ColdFirstQueryP99Milliseconds > coldFirstQueryLimit)
        {
            AddFinding(
                findings,
                "performance_capacity",
                "cold_first_query",
                $"{journey.Id} 冷首查 P99 必须在 0~{coldFirstQueryLimit:F3} ms 内。");
        }
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
            JourneyPath.BoundedRelationFallback => string.Equals(
                path,
                "relation_scan_fallback",
                StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(journey.FallbackReason),
            _ => false,
        };
        if (!pathPass)
            AddFinding(findings, "performance_capacity", "access_path", $"{journey.Id} 实际 access path 不符合冻结合同：{path}。");
        if (expected != JourneyPath.BoundedRelationFallback
            && !string.IsNullOrWhiteSpace(journey.FallbackReason))
        {
            AddFinding(findings, "performance_capacity", "unexpected_fallback", $"{journey.Id} 出现未允许回退：{journey.FallbackReason}。");
        }
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
        bool memoryMatches = environment.PhysicalMemoryBytes >= 63L * GiB
            && environment.PhysicalMemoryBytes <= 65L * GiB;
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

    private static void ValidateGaps(
        IReadOnlyList<GraphProductionGapEvidence> gaps,
        string artifactRoot,
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

            bool blocksProduction = gap.Blocks.Contains("Production", StringComparison.OrdinalIgnoreCase)
                || gap.Blocks.Contains("Couplet C4", StringComparison.OrdinalIgnoreCase);
            if (blocksProduction
                && gap.Status is "open" or "in_progress" or "not_planned")
            {
                AddFinding(findings, GateForSeverity(gap.Severity), "blocking_gap", $"{gap.Id} 仍阻塞 {gap.Blocks}。");
            }
            else if (string.Equals(gap.Status, "closed", StringComparison.Ordinal))
            {
                if (gap.CloseEvidence is null)
                    AddFinding(findings, "both", "gap_close_evidence", $"{gap.Id} 标记 closed 但没有关闭 evidence。");
                else
                    ValidateArtifact(gap.CloseEvidence, artifactRoot, "both", gap.Id, findings);
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

    private static void ValidateArtifact(
        GraphProductionArtifactEvidence artifact,
        string artifactRoot,
        string gate,
        string owner,
        List<GraphProductionGateFinding> findings)
    {
        if (artifact is null)
        {
            AddFinding(findings, gate, "artifact_null", $"{owner} artifact 不能为 null。");
            return;
        }
        if (string.IsNullOrWhiteSpace(artifact.Path)
            || !IsSha256(artifact.Sha256)
            || string.IsNullOrWhiteSpace(artifact.Command))
        {
            AddFinding(findings, gate, "artifact_metadata", $"{owner} 缺少 path、SHA-256 或复现命令。");
            return;
        }

        string path = Path.IsPathFullyQualified(artifact.Path)
            ? Path.GetFullPath(artifact.Path)
            : Path.GetFullPath(Path.Combine(artifactRoot, artifact.Path));
        if (!File.Exists(path))
        {
            AddFinding(findings, gate, "artifact_missing", $"{owner} artifact 不存在：{artifact.Path}。");
            return;
        }

        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            AddFinding(findings, gate, "artifact_digest", $"{owner} artifact SHA-256 不匹配。");
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
