using System.Security.Cryptography;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>M40 #367 production gate 证据和防误报合同测试。</summary>
public sealed class GraphProductionGateTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "sndb-m40-production-gate-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建独立 evidence 目录。</summary>
    public GraphProductionGateTests() => Directory.CreateDirectory(_rootDirectory);

    /// <summary>清理测试 evidence。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理不能覆盖门禁断言。
        }
        catch (UnauthorizedAccessException)
        {
            // Windows 文件句柄短暂存活时交给临时目录后续清理。
        }
    }

    /// <summary>验证 quick 会真实读写/重开/恢复，但绝不形成 Production PASS。</summary>
    [Fact]
    public void RunQuick_MixedWorkloadAndRecoveryPass_ProductionRemainsNotRun()
    {
        GraphProductionGateReport report = GraphProductionGateRunner.RunQuick(_rootDirectory);

        Assert.Equal("m40-graph-production-gate-v1", report.Schema);
        Assert.Equal("#367", report.Issue);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.LocalSmoke);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.ReleaseDecision);
        Assert.False(report.Input.ProductionRun);
        Assert.Equal(8, report.Input.Soak.ReaderWorkers);
        Assert.Equal(1, report.Input.Soak.UpdateWorkers);
        Assert.Equal(64, report.Input.Dataset.VertexCount);
        Assert.Equal(192, report.Input.Dataset.EdgeCount);
        Assert.All(report.Input.CorrectnessRecoveryChecks, static check =>
            Assert.Equal(GraphProductionEvidenceStatus.Pass, check.Status));
        Assert.Contains(report.Findings, static finding => finding.Code == "production_not_attempted");
        Assert.True(File.Exists(Path.Combine(_rootDirectory, "m40-graph-production-gate.json")));
        Assert.True(File.Exists(Path.Combine(_rootDirectory, "m40-graph-production-gate.md")));
        Assert.True(File.Exists(Path.Combine(_rootDirectory, "m40-graph-production-input.template.json")));
    }

    /// <summary>验证完整冻结 evidence 可得双 PASS，release 只由双 gate 合取得出。</summary>
    [Fact]
    public void Evaluate_AllFrozenEvidencePasses_ReturnsReleasePass()
    {
        GraphProductionGateInput input = CreatePassingInput();

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _rootDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.ReleaseDecision);
        Assert.Empty(report.Findings);
    }

    /// <summary>验证短 soak、缺外部检查、错误路径和 blocking gap 均不能被误报为 PASS。</summary>
    [Fact]
    public void Evaluate_IncompleteProductionEvidence_FailsBothGates()
    {
        GraphProductionGateInput passing = CreatePassingInput();
        GraphProductionGateInput incomplete = passing with
        {
            Soak = passing.Soak with { DurationHours = 24, KillReopenCount = 1 },
            PerformanceCapacityChecks = passing.PerformanceCapacityChecks
                .Where(static check => check.Id != "native_aot")
                .ToArray(),
            Journeys = passing.Journeys
                .Select(static journey => journey.Id == "PGQ-1"
                    ? journey with { AccessPath = "relation_scan_fallback", FallbackReason = "missing_index" }
                    : journey)
                .ToArray(),
            Gaps =
            [
                new GraphProductionGapEvidence
                {
                    Id = "M40-GAP-009",
                    Status = "in_progress",
                    Blocks = "Production; Couplet C4",
                    Severity = "correctness_recovery",
                },
            ],
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(
            incomplete,
            _rootDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "soak_duration");
        Assert.Contains(report.Findings, static finding => finding.Code == "check_missing");
        Assert.Contains(report.Findings, static finding => finding.Code == "access_path");
        Assert.Contains(report.Findings, static finding => finding.Code == "blocking_gap");
    }

    /// <summary>验证模板可由 source-generated JSON 路径读取，且所有占位证据稳定失败。</summary>
    [Fact]
    public void EvaluateManifest_TemplateWithPlaceholders_FailsAndWritesReport()
    {
        string manifestPath = GraphProductionGateRunner.WriteTemplate(_rootDirectory);
        string outputDirectory = Path.Combine(_rootDirectory, "evaluated");

        GraphProductionGateReport report = GraphProductionGateRunner.EvaluateManifest(
            manifestPath,
            outputDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "check_not_pass");
        Assert.True(File.Exists(Path.Combine(outputDirectory, "m40-graph-production-gate.json")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "m40-graph-production-gate.md")));
    }

    private GraphProductionGateInput CreatePassingInput()
    {
        string artifactPath = Path.Combine(_rootDirectory, "evidence.json");
        File.WriteAllText(artifactPath, "{\"status\":\"PASS\"}");
        using FileStream stream = File.OpenRead(artifactPath);
        string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var artifact = new GraphProductionArtifactEvidence
        {
            Path = Path.GetFileName(artifactPath),
            Sha256 = digest,
            Command = "dotnet test --filter M40Production",
        };
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow.AddHours(-168);
        return new GraphProductionGateInput
        {
            ProductionRun = true,
            CommitSha = new string('a', 40),
            StartedUtc = startedUtc,
            FinishedUtc = startedUtc.AddHours(168),
            Dataset = new GraphProductionDatasetEvidence
            {
                Tier = "production-soak",
                VertexCount = 1_000_000,
                EdgeCount = 10_000_000,
                InputDigest = new string('b', 64),
                OutputDigest = new string('c', 64),
            },
            Environment = new GraphProductionEnvironmentEvidence
            {
                OsDescription = "Microsoft Windows 11 25H2 x64",
                OsBuild = "26200",
                Architecture = "X64",
                CpuName = "Intel(R) Core(TM) Ultra 9 185H",
                PhysicalCoreCount = 16,
                LogicalProcessorCount = 22,
                PhysicalMemoryBytes = 64L * 1024 * 1024 * 1024,
                DiskName = "NVMe PC SN8000S WD 2048GB",
                DiskFormat = "NTFS",
                Runtime = ".NET 10.0.0",
                SdkVersion = "10.0.100",
                GcMode = "server",
                PowerProfile = "Windows Best performance",
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
                PeakWorkingSetBytes = 2L * 1024 * 1024 * 1024,
                WalBytes = 1,
                ColdOpenP95Milliseconds = 500,
                ColdOpenP99Milliseconds = 1_000,
                RecoveryP50Milliseconds = 100,
                RecoveryP95Milliseconds = 200,
                RecoveryP99Milliseconds = 300,
                SyncWalOnEveryWrite = true,
                AutoCheckpointEnabled = true,
                MaxWalBytes = 256L * 1024 * 1024,
                MaxOverlayEntries = 100_000,
            },
            Journeys = GraphProductionGateEvaluator.GetRequiredJourneyIds()
                .Select(id => CreatePassingJourney(id, artifact))
                .ToArray(),
            CorrectnessRecoveryChecks = GraphProductionGateEvaluator.GetRequiredCorrectnessCheckIds()
                .Select(id => PassingCheck(id, artifact))
                .ToArray(),
            PerformanceCapacityChecks = GraphProductionGateEvaluator.GetRequiredPerformanceCheckIds()
                .Select(id => PassingCheck(id, artifact))
                .ToArray(),
            Gaps = GraphProductionGateEvaluator.GetRequiredGapIds()
                .Select(id => new GraphProductionGapEvidence
                {
                    Id = id,
                    Status = "closed",
                    Blocks = "Production; Couplet C4",
                    Severity = "capacity_latency",
                    CloseEvidence = artifact,
                })
                .ToArray(),
        };
    }

    private static GraphProductionJourneyEvidence CreatePassingJourney(
        string id,
        GraphProductionArtifactEvidence artifact)
    {
        string accessPath = id switch
        {
            "CPL-4" => "hybrid_candidates+native_adjacency",
            "PGQ-1" or "PGQ-2" => "relation_index_seek",
            "PGQ-3" => "relation_scan_fallback",
            _ => "native_adjacency",
        };
        return new GraphProductionJourneyEvidence
        {
            Id = id,
            Status = GraphProductionEvidenceStatus.Pass,
            WarmupCount = 1_000,
            Rounds = 3,
            SamplesPerRound = 10_000,
            P50Milliseconds = 1,
            P95Milliseconds = 2,
            P99Milliseconds = 3,
            MaxMilliseconds = 4,
            ThroughputPerSecond = 100,
            TimeToFirstRowP99Milliseconds = 1,
            ColdFirstQueryP99Milliseconds = 4,
            QueryPeakLiveBytes = 1024,
            PeakWorkingSetBytes = 2L * 1024 * 1024 * 1024,
            AllocatedBytesP95 = 1,
            LogicalReadBytes = 1,
            PhysicalReadBytes = 0,
            WalBytes = 0,
            Candidates = 1,
            Examined = 1,
            Returned = 1,
            ExpandedEdges = 1,
            FrontierPeak = 1,
            AccessPath = accessPath,
            FallbackReason = id == "PGQ-3" ? "scan_budget_exceeded" : null,
            Artifact = artifact,
        };
    }

    private static GraphProductionCheckEvidence PassingCheck(
        string id,
        GraphProductionArtifactEvidence artifact)
        => new()
        {
            Id = id,
            Status = GraphProductionEvidenceStatus.Pass,
            Summary = "PASS",
            Artifact = artifact,
        };
}
