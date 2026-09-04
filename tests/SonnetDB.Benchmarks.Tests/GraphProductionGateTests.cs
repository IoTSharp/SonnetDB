using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>M40 #367 production gate 证据和防误报合同测试。</summary>
public sealed class GraphProductionGateTests : IDisposable
{
    private const string TestDirectoryPrefix = "sndb-m40-production-gate-test-";
    private const int SampleCount = 10_000;
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        TestDirectoryPrefix + Guid.NewGuid().ToString("N"));
    private readonly string _artifactDirectory;
    private readonly string _commitSha;

    /// <summary>创建带 ignored evidence 目录的独立 clean Git worktree。</summary>
    public GraphProductionGateTests()
    {
        Directory.CreateDirectory(_rootDirectory);
        _artifactDirectory = Path.Combine(_rootDirectory, "artifacts");
        Directory.CreateDirectory(_artifactDirectory);
        _commitSha = InitializeRepository();
    }

    /// <summary>清理测试 evidence。</summary>
    public void Dispose()
    {
        string fullPath = Path.GetFullPath(_rootDirectory);
        if (!GraphEvidenceOwnedDirectoryCleanup.TryDelete(
            fullPath,
            Path.GetTempPath(),
            TestDirectoryPrefix,
            out string failureReason))
        {
            Console.Error.WriteLine(
                $"production-gate-test-temp-retained path={fullPath} reason={failureReason}");
        }
    }

    /// <summary>验证清理器拒绝仅伪装前缀、但不含规范 GUID 的目录。</summary>
    [Fact]
    public void OwnedDirectoryCleanup_NameWithoutGuid_RejectsOwnership()
    {
        string invalidPath = Path.Combine(Path.GetTempPath(), TestDirectoryPrefix + "not-a-guid");

        bool deleted = GraphEvidenceOwnedDirectoryCleanup.TryDelete(
            invalidPath,
            Path.GetTempPath(),
            TestDirectoryPrefix,
            out string failureReason);

        Assert.False(deleted);
        Assert.Equal("ownership", failureReason);
    }

    /// <summary>验证清理器不跟随目录符号链接进入不属于当前 fixture 的目标。</summary>
    [Fact]
    public void OwnedDirectoryCleanup_NestedDirectoryLink_DeletesLinkWithoutFollowingTarget()
    {
        string externalDirectory = Path.Combine(
            Path.GetTempPath(),
            TestDirectoryPrefix + Guid.NewGuid().ToString("N"));
        string sentinelPath = Path.Combine(externalDirectory, "sentinel.txt");
        string linkPath = Path.Combine(_rootDirectory, "external-link");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(sentinelPath, "retained");
        try
        {
            try
            {
                _ = Directory.CreateSymbolicLink(linkPath, externalDirectory);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            Assert.True(GraphEvidenceOwnedDirectoryCleanup.TryDelete(
                _rootDirectory,
                Path.GetTempPath(),
                TestDirectoryPrefix,
                out string failureReason), failureReason);
            Assert.True(File.Exists(sentinelPath));
        }
        finally
        {
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath, recursive: false);
            if (!GraphEvidenceOwnedDirectoryCleanup.TryDelete(
                externalDirectory,
                Path.GetTempPath(),
                TestDirectoryPrefix,
                out string failureReason))
            {
                Console.Error.WriteLine(
                    $"production-gate-link-target-retained path={externalDirectory} reason={failureReason}");
            }
        }
    }

    /// <summary>验证清理器拒绝把自有根目录替换成指向外部目标的符号链接。</summary>
    [Fact]
    public void OwnedDirectoryCleanup_RootDirectoryLink_RejectsDeletion()
    {
        string externalDirectory = Path.Combine(
            Path.GetTempPath(),
            TestDirectoryPrefix + Guid.NewGuid().ToString("N"));
        string ownedLink = Path.Combine(
            Path.GetTempPath(),
            TestDirectoryPrefix + Guid.NewGuid().ToString("N"));
        string sentinelPath = Path.Combine(externalDirectory, "sentinel.txt");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(sentinelPath, "retained");
        try
        {
            try
            {
                _ = Directory.CreateSymbolicLink(ownedLink, externalDirectory);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            bool deleted = GraphEvidenceOwnedDirectoryCleanup.TryDelete(
                ownedLink,
                Path.GetTempPath(),
                TestDirectoryPrefix,
                out string failureReason);

            Assert.False(deleted);
            Assert.Equal("root-reparse-point", failureReason);
            Assert.True(File.Exists(sentinelPath));
        }
        finally
        {
            try
            {
                Directory.Delete(ownedLink, recursive: false);
            }
            catch (DirectoryNotFoundException)
            {
            }

            if (!GraphEvidenceOwnedDirectoryCleanup.TryDelete(
                externalDirectory,
                Path.GetTempPath(),
                TestDirectoryPrefix,
                out string failureReason))
            {
                Console.Error.WriteLine(
                    $"production-gate-root-link-target-retained path={externalDirectory} reason={failureReason}");
            }
        }
    }

    /// <summary>验证 quick 会真实读写/重开/恢复，但绝不形成 Production PASS。</summary>
    [Fact]
    public void RunQuick_MixedWorkloadAndRecoveryPass_ProductionRemainsNotRun()
    {
        GraphProductionGateReport report = GraphProductionGateRunner.RunQuick(_artifactDirectory);

        Assert.Equal("m40-graph-production-gate-v2", report.Schema);
        Assert.Equal("#367", report.Issue);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.LocalSmoke);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.ReleaseDecision);
        Assert.False(report.Input.ProductionRun);
        Assert.Equal(8, report.Input.Soak.ReaderWorkers);
        Assert.Equal(1, report.Input.Soak.UpdateWorkers);
        Assert.Equal(1, report.Input.Soak.KillReopenCount);
        Assert.Equal(64, report.Input.Dataset.VertexCount);
        Assert.Equal(192, report.Input.Dataset.EdgeCount);
        Assert.All(report.Input.CorrectnessRecoveryChecks, static check =>
            Assert.Equal(GraphProductionEvidenceStatus.Pass, check.Status));
        Assert.Contains(report.Findings, static finding => finding.Code == "production_not_attempted");
        Assert.True(File.Exists(Path.Combine(_artifactDirectory, "m40-graph-production-gate.json")));
        Assert.True(File.Exists(Path.Combine(_artifactDirectory, "m40-graph-production-gate.md")));
        Assert.True(File.Exists(Path.Combine(_artifactDirectory, "m40-graph-production-input.template.json")));
        string quickLog = File.ReadAllText(Path.Combine(_artifactDirectory, "m40-graph-production-quick.log"));
        Assert.Contains("kill_reopen_tree_tracking_reliable=True", quickLog, StringComparison.Ordinal);
        Assert.Contains("kill_reopen_cleanup_confirmed=True", quickLog, StringComparison.Ordinal);
        Assert.Contains("kill_reopen_output_drained=True", quickLog, StringComparison.Ordinal);
        Assert.Contains("kill_reopen_output_drain_stopped=True", quickLog, StringComparison.Ordinal);
    }

    /// <summary>验证完整原始 evidence 经独立重算、Git 检查和命令回放后才能形成 PASS。</summary>
    [Fact]
    public void Evaluate_AllSchemaAwareRawEvidencePasses_ReturnsReleasePass()
    {
        GraphProductionGateInput input = CreatePassingInput();

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Empty(report.Findings);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.ReleaseDecision);
        Assert.All(report.Input.Journeys, static journey =>
        {
            Assert.Equal(1, journey.P99Milliseconds);
            Assert.Equal(1, journey.AllocatedBytesP95);
            Assert.Equal(0, journey.Gen2Collections);
            Assert.Equal(0.1, journey.GcPauseP99Milliseconds, precision: 3);
        });
    }

    /// <summary>验证伪 status、缺逐样本值、失败回放和 allocation/GC 超限均不能绕过门禁。</summary>
    [Fact]
    public void Evaluate_ForgedRawEvidenceAndResourceRegression_FailsBothGates()
    {
        GraphProductionGateInput input = CreatePassingInput();
        GraphProductionCheckEvidence[] correctness = input.CorrectnessRecoveryChecks.ToArray();
        GraphProductionJourneyEvidence[] journeys = input.Journeys.ToArray();

        GraphProductionArtifactRun normalRun = CreateRun();
        string forgedPath = Path.Combine(_artifactDirectory, "forged-status.json");
        File.WriteAllText(forgedPath, "{\"status\":\"PASS\"}");
        correctness[0] = correctness[0] with
        {
            Artifact = CreateReference("forged-status.json", normalRun),
        };

        GraphProductionJourneyArtifact missingSamples = ReadArtifact(
            journeys[0].Artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact);
        GraphProductionJourneyRoundArtifact[] missingRounds = missingSamples.Rounds.ToArray();
        missingRounds[0] = missingRounds[0] with { ElapsedMicroseconds = [1_000] };
        missingSamples = missingSamples with { Rounds = missingRounds };
        journeys[0] = journeys[0] with
        {
            Artifact = WriteArtifact(
                journeys[0].Artifact.Path,
                missingSamples,
                GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact,
                missingSamples.Run),
        };

        GraphProductionJourneyArtifact resourceRegression = ReadArtifact(
            journeys[1].Artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact);
        GraphProductionJourneyRoundArtifact[] resourceRounds = resourceRegression.Rounds
            .Select(static round => round with
            {
                AllocatedBytes = Repeat(512L * 1024 * 1024),
                Gen2Collections = Repeat(1),
                GcPauseMicroseconds = Repeat(100_000),
            })
            .ToArray();
        resourceRegression = resourceRegression with { Rounds = resourceRounds };
        journeys[1] = journeys[1] with
        {
            Artifact = WriteArtifact(
                journeys[1].Artifact.Path,
                resourceRegression,
                GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact,
                resourceRegression.Run),
        };

        GraphProductionDatasetArtifact datasetArtifact = ReadArtifact(
            input.Dataset.Artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionDatasetArtifact);
        GraphProductionArtifactRun failingRun = datasetArtifact.Run with
        {
            Command = "dotnet",
            Arguments = ["run", "--project", "missing-evidence-harness.csproj", "--", "{artifact}"],
            ExitCode = 0,
        };
        datasetArtifact = datasetArtifact with { Run = failingRun };
        GraphProductionDatasetEvidence dataset = input.Dataset with
        {
            Artifact = WriteArtifact(
                input.Dataset.Artifact.Path,
                datasetArtifact,
                GraphProductionArtifactJsonContext.Default.GraphProductionDatasetArtifact,
                failingRun),
        };

        input = input with
        {
            Dataset = dataset,
            Journeys = journeys,
            CorrectnessRecoveryChecks = correctness,
        };
        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_schema");
        Assert.Contains(report.Findings, static finding => finding.Code == "journey_raw_samples");
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_reproduction");
        Assert.Contains(report.Findings, static finding => finding.Code == "journey_summary_mismatch");
        Assert.Contains(report.Findings, static finding => finding.Code == "allocation");
        Assert.Contains(report.Findings, static finding => finding.Code == "gc_rate");
        Assert.Contains(report.Findings, static finding => finding.Code == "gc_pause");
    }

    /// <summary>验证无效 commit 与 dirty worktree 即使 manifest 自报 Production 也稳定失败。</summary>
    [Fact]
    public void Evaluate_InvalidCommitAndDirtyWorktree_FailsGitGate()
    {
        File.WriteAllText(Path.Combine(_rootDirectory, "dirty.txt"), "dirty");
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow.AddHours(-2);
        var input = new GraphProductionGateInput
        {
            ProductionRun = true,
            CommitSha = new string('a', 40),
            StartedUtc = startedUtc,
            FinishedUtc = startedUtc.AddHours(1),
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "git_commit_missing");
        Assert.Contains(report.Findings, static finding => finding.Code == "git_head_mismatch");
        Assert.Contains(report.Findings, static finding => finding.Code == "git_worktree_dirty");
    }

    /// <summary>验证超量清单在启动 Git 或 artifact 复现进程前即失败。</summary>
    [Fact]
    public void Evaluate_ManifestCollectionsExceedFrozenBound_FailsBeforeReplay()
    {
        int overLimit = GraphProductionGateEvaluator.GetRequiredJourneyIds().Count + 1;
        var input = new GraphProductionGateInput
        {
            ProductionRun = true,
            Journeys = Enumerable.Range(0, overLimit)
                .Select(static index => new GraphProductionJourneyEvidence { Id = $"extra-{index}" })
                .ToArray(),
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "manifest_execution_bound");
    }

    /// <summary>验证非 Production 清单也不能借早退绕过集合上限。</summary>
    [Fact]
    public void Evaluate_NonProductionManifestCollectionsExceedFrozenBound_FailsBeforeEarlyReturn()
    {
        int overLimit = GraphProductionGateEvaluator.GetRequiredJourneyIds().Count + 1;
        var input = new GraphProductionGateInput
        {
            ProductionRun = false,
            Journeys = Enumerable.Range(0, overLimit)
                .Select(static index => new GraphProductionJourneyEvidence { Id = $"extra-{index}" })
                .ToArray(),
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "manifest_execution_bound");
        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "production_not_attempted");
    }

    /// <summary>验证超出字节上限的清单会在 JSON 读取和输出目录创建前失败。</summary>
    [Fact]
    public void EvaluateManifest_FileExceedsByteLimit_FailsBeforeDeserialization()
    {
        string manifestPath = Path.Combine(_artifactDirectory, "oversized-manifest.json");
        string outputDirectory = Path.Combine(_rootDirectory, "oversized-output");
        using (FileStream stream = File.Create(manifestPath))
            stream.SetLength(GraphProductionGateRunner.MaximumManifestBytes + 1);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            GraphProductionGateRunner.EvaluateManifest(manifestPath, outputDirectory));

        Assert.Contains(
            GraphProductionGateRunner.MaximumManifestBytes.ToString(),
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>验证预取消在打开清单文件前传播。</summary>
    [Fact]
    public void EvaluateManifest_WithPreCancelledToken_DoesNotOpenManifest()
    {
        string missingManifestPath = Path.Combine(_artifactDirectory, "missing-pre-cancelled.json");
        string outputDirectory = Path.Combine(_rootDirectory, "pre-cancelled-output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = Assert.Throws<OperationCanceledException>(() =>
            GraphProductionGateRunner.EvaluateManifest(
                missingManifestPath,
                outputDirectory,
                cancellation.Token));

        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>验证单个 artifact 超过字节上限时在哈希、JSON 解析和回放前失败。</summary>
    [Fact]
    public void Evaluate_ArtifactExceedsByteLimit_FailsClosed()
    {
        string fileName = "oversized-artifact.json";
        string path = Path.Combine(_artifactDirectory, fileName);
        using (FileStream stream = File.Create(path))
            stream.SetLength(GraphProductionGateEvaluator.MaximumArtifactBytes + 1);
        GraphProductionArtifactRun run = CreateRun();
        GraphProductionArtifactEvidence reference = CreateUncheckedReference(fileName, run);
        GraphProductionGateInput input = CreateMinimalProductionInput() with
        {
            Dataset = new GraphProductionDatasetEvidence { Artifact = reference },
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_size_bound");
    }

    /// <summary>验证 soak 的 checkpoint、kill、cold-open 和资源集合在聚合前统一受限。</summary>
    [Fact]
    public void Evaluate_SoakArtifactCollectionsExceedBounds_FailsBeforeAggregation()
    {
        GraphProductionArtifactRun run = CreateRun();
        var artifact = new GraphProductionSoakArtifact
        {
            Run = run,
            CheckpointsUtc = Enumerable.Repeat(DateTimeOffset.UtcNow, GraphProductionGateEvaluator.MaximumSoakCheckpointSamples + 1).ToArray(),
            KillReopenSamples = Enumerable.Repeat(new GraphProductionKillReopenSample(), GraphProductionGateEvaluator.MaximumSoakKillReopenSamples + 1).ToArray(),
            ColdOpenMilliseconds = Enumerable.Repeat(1d, GraphProductionGateEvaluator.MaximumSoakColdOpenSamples + 1).ToArray(),
            ResourceSamples = Enumerable.Repeat(new GraphProductionSoakResourceSample(), GraphProductionGateEvaluator.MaximumSoakResourceSamples + 1).ToArray(),
        };
        GraphProductionArtifactEvidence reference = WriteArtifact(
            "overbound-soak.json",
            artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionSoakArtifact,
            run);
        GraphProductionGateInput input = CreateMinimalProductionInput() with
        {
            Soak = new GraphProductionSoakEvidence { Artifact = reference },
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Equal(4, report.Findings.Count(static finding => finding.Code == "artifact_collection_bound"));
    }

    /// <summary>验证 journey 轮次在逐轮遍历前受限。</summary>
    [Fact]
    public void Evaluate_JourneyRoundsExceedBound_FailsBeforeRoundTraversal()
    {
        GraphProductionArtifactRun run = CreateRun();
        var artifact = new GraphProductionJourneyArtifact
        {
            Run = run,
            JourneyId = "SOC-1",
            Rounds = Enumerable.Range(1, GraphProductionGateEvaluator.MaximumJourneyRounds + 1)
                .Select(static round => new GraphProductionJourneyRoundArtifact { Round = round })
                .ToArray(),
        };
        GraphProductionArtifactEvidence reference = WriteArtifact(
            "overbound-journey-rounds.json",
            artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact,
            run);
        GraphProductionGateInput input = CreateMinimalProductionInput() with
        {
            Journeys = [new GraphProductionJourneyEvidence { Id = "SOC-1", Artifact = reference }],
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_collection_bound");
    }

    /// <summary>验证 journey 样本列和 oracle 在排序与分组前受限。</summary>
    [Fact]
    public void Evaluate_JourneySamplesAndOraclesExceedBounds_FailsBeforeAggregation()
    {
        GraphProductionArtifactRun run = CreateRun();
        var artifact = new GraphProductionJourneyArtifact
        {
            Run = run,
            JourneyId = "SOC-1",
            Rounds =
            [
                new GraphProductionJourneyRoundArtifact
                {
                    Round = 1,
                    ElapsedMicroseconds = Enumerable.Repeat(1L, GraphProductionGateEvaluator.MaximumJourneySamplesPerColumn + 1).ToArray(),
                    OracleAssertions = Enumerable.Repeat(new GraphProductionOracleAssertion(), GraphProductionGateEvaluator.MaximumJourneyOracleAssertions + 1).ToArray(),
                },
            ],
        };
        GraphProductionArtifactEvidence reference = WriteArtifact(
            "overbound-journey-samples.json",
            artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact,
            run);
        GraphProductionGateInput input = CreateMinimalProductionInput() with
        {
            Journeys = [new GraphProductionJourneyEvidence { Id = "SOC-1", Artifact = reference }],
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Equal(2, report.Findings.Count(static finding => finding.Code == "artifact_collection_bound"));
    }

    /// <summary>验证 check assertions 在遍历和去重前受限。</summary>
    [Fact]
    public void Evaluate_CheckAssertionsExceedBound_FailsBeforeTraversal()
    {
        GraphProductionArtifactRun run = CreateRun();
        var artifact = new GraphProductionCheckArtifact
        {
            Run = run,
            CheckId = "five_journey_oracle",
            Assertions = Enumerable.Repeat(new GraphProductionCheckAssertion(), GraphProductionGateEvaluator.MaximumCheckAssertions + 1).ToArray(),
        };
        GraphProductionArtifactEvidence reference = WriteArtifact(
            "overbound-check.json",
            artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact,
            run);
        GraphProductionGateInput input = CreateMinimalProductionInput() with
        {
            CorrectnessRecoveryChecks =
            [
                new GraphProductionCheckEvidence
                {
                    Id = "five_journey_oracle",
                    Artifact = reference,
                },
            ],
        };

        GraphProductionGateReport report = GraphProductionGateEvaluator.Evaluate(input, _artifactDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_collection_bound");
    }

    /// <summary>验证 evaluator 的预取消在任何 Git 探测或 artifact 文件读取前传播。</summary>
    [Fact]
    public async Task EvaluateAsync_WithPreCancelledToken_DoesNotReadArtifact()
    {
        GraphProductionArtifactRun run = CreateRun();
        var artifact = new GraphProductionDatasetArtifact { Run = run };
        GraphProductionArtifactEvidence reference = WriteArtifact(
            "pre-cancelled-artifact.json",
            artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionDatasetArtifact,
            run);
        GraphProductionGateInput input = CreateMinimalProductionInput() with
        {
            Dataset = new GraphProductionDatasetEvidence { Artifact = reference },
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GraphProductionGateEvaluator.EvaluateAsync(input, _artifactDirectory, cancellation.Token));
    }

    /// <summary>验证模板可由 source-generated JSON 路径读取，且所有占位证据稳定失败。</summary>
    [Fact]
    public void EvaluateManifest_TemplateWithPlaceholders_FailsAndWritesReport()
    {
        string manifestPath = GraphProductionGateRunner.WriteTemplate(_artifactDirectory);
        string outputDirectory = Path.Combine(_rootDirectory, "evaluated");

        GraphProductionGateReport report = GraphProductionGateRunner.EvaluateManifest(manifestPath, outputDirectory);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_metadata");
        Assert.Contains(report.Findings, static finding => finding.Code == "check_not_pass");
        Assert.True(File.Exists(Path.Combine(outputDirectory, "m40-graph-production-gate.json")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "m40-graph-production-gate.md")));
    }

    private GraphProductionGateInput CreateMinimalProductionInput()
    {
        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        return new GraphProductionGateInput
        {
            ProductionRun = true,
            CommitSha = _commitSha,
            StartedUtc = finishedUtc.AddHours(-1),
            FinishedUtc = finishedUtc,
        };
    }

    private static GraphProductionArtifactEvidence CreateUncheckedReference(
        string fileName,
        GraphProductionArtifactRun run)
        => new()
        {
            Path = fileName,
            Sha256 = new string('a', 64),
            Command = run.Command,
            Arguments = run.Arguments,
            WorkingDirectory = run.WorkingDirectory,
            ExpectedExitCode = run.ExitCode,
            TimeoutSeconds = 60,
        };

    private GraphProductionGateInput CreatePassingInput()
    {
        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        DateTimeOffset startedUtc = finishedUtc.AddHours(-168);
        GraphProductionArtifactRun run = CreateRun();

        var datasetArtifact = new GraphProductionDatasetArtifact
        {
            Run = run,
            Tier = "production-soak",
            Generator = "m40-graph-generator-v1",
            Seed = "0x534F4E4E45544442",
            VertexCount = 1_000_000,
            EdgeCount = 10_000_000,
            InputDigest = new string('b', 64),
            OutputDigest = new string('c', 64),
        };
        GraphProductionArtifactEvidence datasetReference = WriteArtifact(
            "dataset.json",
            datasetArtifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionDatasetArtifact,
            run);
        var dataset = new GraphProductionDatasetEvidence
        {
            Tier = datasetArtifact.Tier,
            Generator = datasetArtifact.Generator,
            Seed = datasetArtifact.Seed,
            VertexCount = datasetArtifact.VertexCount,
            EdgeCount = datasetArtifact.EdgeCount,
            InputDigest = datasetArtifact.InputDigest,
            OutputDigest = datasetArtifact.OutputDigest,
            Artifact = datasetReference,
        };

        var environmentSnapshot = new GraphProductionEnvironmentSnapshot
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
        };
        var environmentArtifact = new GraphProductionEnvironmentArtifact
        {
            Run = run,
            Environment = environmentSnapshot,
        };
        GraphProductionArtifactEvidence environmentReference = WriteArtifact(
            "environment.json",
            environmentArtifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionEnvironmentArtifact,
            run);
        var environment = new GraphProductionEnvironmentEvidence
        {
            OsDescription = environmentSnapshot.OsDescription,
            OsBuild = environmentSnapshot.OsBuild,
            Architecture = environmentSnapshot.Architecture,
            CpuName = environmentSnapshot.CpuName,
            PhysicalCoreCount = environmentSnapshot.PhysicalCoreCount,
            LogicalProcessorCount = environmentSnapshot.LogicalProcessorCount,
            PhysicalMemoryBytes = environmentSnapshot.PhysicalMemoryBytes,
            DiskName = environmentSnapshot.DiskName,
            DiskFormat = environmentSnapshot.DiskFormat,
            Runtime = environmentSnapshot.Runtime,
            SdkVersion = environmentSnapshot.SdkVersion,
            GcMode = environmentSnapshot.GcMode,
            PowerProfile = environmentSnapshot.PowerProfile,
            Artifact = environmentReference,
        };

        DateTimeOffset[] checkpoints = Enumerable.Range(0, 337)
            .Select(index => startedUtc.AddMinutes(index * 30d))
            .ToArray();
        GraphProductionKillReopenSample[] kills = Enumerable.Range(1, 7)
            .Select(index => new GraphProductionKillReopenSample
            {
                TimestampUtc = startedUtc.AddHours(index * 24d),
                ProcessKilled = true,
                Reopened = true,
                InvariantPassed = true,
                RecoveryMilliseconds = 100,
            })
            .ToArray();
        var soakArtifact = new GraphProductionSoakArtifact
        {
            Run = run,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            ReaderWorkers = 8,
            UpdateWorkers = 1,
            UpdateProfile = "m40-frozen-update-profile-v1",
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = true,
            MaxWalBytes = 256L * 1024 * 1024,
            MaxOverlayEntries = 100_000,
            CheckpointsUtc = checkpoints,
            KillReopenSamples = kills,
            ColdOpenMilliseconds = [500, 500, 500, 500, 500, 500, 500],
            ResourceSamples =
            [
                new GraphProductionSoakResourceSample
                {
                    TimestampUtc = startedUtc,
                    WorkingSetBytes = 2L * 1024 * 1024 * 1024,
                    WalBytes = 1,
                },
                new GraphProductionSoakResourceSample
                {
                    TimestampUtc = finishedUtc,
                    WorkingSetBytes = 2L * 1024 * 1024 * 1024,
                    WalBytes = 1_024,
                },
            ],
        };
        GraphProductionArtifactEvidence soakReference = WriteArtifact(
            "soak.json",
            soakArtifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionSoakArtifact,
            run);
        var soak = new GraphProductionSoakEvidence
        {
            DurationHours = 168,
            ReaderWorkers = 8,
            UpdateWorkers = 1,
            UpdateProfile = "m40-frozen-update-profile-v1",
            MaximumCheckpointIntervalMinutes = 30,
            CheckpointCount = 337,
            KillReopenCount = 7,
            InvariantCheckCount = 7,
            PeakWorkingSetBytes = 2L * 1024 * 1024 * 1024,
            WalBytes = 1_024,
            ColdOpenP95Milliseconds = 500,
            ColdOpenP99Milliseconds = 500,
            RecoveryP50Milliseconds = 100,
            RecoveryP95Milliseconds = 100,
            RecoveryP99Milliseconds = 100,
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = true,
            MaxWalBytes = 256L * 1024 * 1024,
            MaxOverlayEntries = 100_000,
            Artifact = soakReference,
        };

        GraphProductionJourneyEvidence[] journeys = GraphProductionGateEvaluator.GetRequiredJourneyIds()
            .Select(id => CreatePassingJourney(id, run))
            .ToArray();
        GraphProductionCheckEvidence[] correctness = GraphProductionGateEvaluator.GetRequiredCorrectnessCheckIds()
            .Select(id => CreatePassingCheck(id, "correctness", run))
            .ToArray();
        GraphProductionCheckEvidence[] performance = GraphProductionGateEvaluator.GetRequiredPerformanceCheckIds()
            .Select(id => CreatePassingCheck(id, "performance", run))
            .ToArray();
        GraphProductionGapEvidence[] gaps = GraphProductionGateEvaluator.GetRequiredGapIds()
            .Select(id => new GraphProductionGapEvidence
            {
                Id = id,
                Status = "closed",
                Blocks = "Production; Couplet C4",
                Severity = "capacity_latency",
                CloseEvidence = CreatePassingCheckArtifact(id, "gap", run),
            })
            .ToArray();

        return new GraphProductionGateInput
        {
            ProductionRun = true,
            CommitSha = _commitSha,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            Dataset = dataset,
            Environment = environment,
            Soak = soak,
            Journeys = journeys,
            CorrectnessRecoveryChecks = correctness,
            PerformanceCapacityChecks = performance,
            Gaps = gaps,
        };
    }

    private GraphProductionJourneyEvidence CreatePassingJourney(
        string id,
        GraphProductionArtifactRun run)
    {
        string accessPath = id switch
        {
            "CPL-4" => "hybrid_candidates+native_adjacency",
            "PGQ-1" or "PGQ-2" => "relation_index_seek",
            "PGQ-3" => "relation_scan_fallback",
            _ => "native_adjacency",
        };
        string? fallbackReason = id == "PGQ-3" ? "bounded_missing_index" : null;
        string oracleDigest = new('d', 64);
        IReadOnlyList<GraphProductionJourneyRoundArtifact> rounds = Enumerable.Range(1, 3)
            .Select(round => new GraphProductionJourneyRoundArtifact
            {
                Round = round,
                WarmupCount = 1_000,
                ElapsedMicroseconds = Repeat(1_000),
                TimeToFirstRowMicroseconds = Repeat(500),
                ColdFirstQueryMicroseconds = [4_000],
                AllocatedBytes = Repeat(1),
                QueryPeakLiveBytes = Repeat(1_024),
                WorkingSetBytes = Repeat(2L * 1024 * 1024 * 1024),
                LogicalReadBytes = Repeat(1),
                PhysicalReadBytes = Repeat(0),
                WalBytes = Repeat(0),
                Candidates = Repeat(1),
                Examined = Repeat(1),
                Returned = Repeat(1),
                ExpandedEdges = Repeat(1),
                FrontierPeak = Repeat(1),
                Gen0Collections = Repeat(0),
                Gen1Collections = Repeat(0),
                Gen2Collections = Repeat(0),
                GcPauseMicroseconds = Repeat(100),
                AccessPath = accessPath,
                FallbackReason = fallbackReason,
                OracleAssertions =
                [
                    new GraphProductionOracleAssertion
                    {
                        Name = "id_property_path_oracle",
                        ExpectedDigest = oracleDigest,
                        ActualDigest = oracleDigest,
                    },
                ],
            })
            .ToArray();
        var artifact = new GraphProductionJourneyArtifact
        {
            Run = run,
            JourneyId = id,
            Rounds = rounds,
        };
        GraphProductionArtifactEvidence reference = WriteArtifact(
            "journey-" + id.ToLowerInvariant() + ".json",
            artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact,
            run);
        return new GraphProductionJourneyEvidence
        {
            Id = id,
            Status = GraphProductionEvidenceStatus.Pass,
            WarmupCount = 1_000,
            Rounds = 3,
            SamplesPerRound = SampleCount,
            P50Milliseconds = 1,
            P95Milliseconds = 1,
            P99Milliseconds = 1,
            MaxMilliseconds = 1,
            ThroughputPerSecond = 1_000,
            TimeToFirstRowP99Milliseconds = 0.5,
            ColdFirstQueryP99Milliseconds = 4,
            QueryPeakLiveBytes = 1_024,
            PeakWorkingSetBytes = 2L * 1024 * 1024 * 1024,
            AllocatedBytesP95 = 1,
            GcPauseP99Milliseconds = 0.1,
            LogicalReadBytes = 1,
            Candidates = 1,
            Examined = 1,
            Returned = 1,
            ExpandedEdges = 1,
            FrontierPeak = 1,
            AccessPath = accessPath,
            FallbackReason = fallbackReason,
            Artifact = reference,
        };
    }

    private GraphProductionCheckEvidence CreatePassingCheck(
        string id,
        string prefix,
        GraphProductionArtifactRun run)
    {
        GraphProductionArtifactEvidence reference = CreatePassingCheckArtifact(id, prefix, run);
        return new GraphProductionCheckEvidence
        {
            Id = id,
            Status = GraphProductionEvidenceStatus.Pass,
            Summary = "raw assertions matched",
            Artifact = reference,
        };
    }

    private GraphProductionArtifactEvidence CreatePassingCheckArtifact(
        string id,
        string prefix,
        GraphProductionArtifactRun run)
    {
        var artifact = new GraphProductionCheckArtifact
        {
            Run = run,
            CheckId = id,
            Summary = "raw assertions matched",
            Assertions =
            [
                new GraphProductionCheckAssertion
                {
                    Name = "result_digest",
                    Expected = "expected",
                    Actual = "expected",
                },
            ],
        };
        return WriteArtifact(
            prefix + "-" + id.ToLowerInvariant() + ".json",
            artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact,
            run);
    }

    private GraphProductionArtifactRun CreateRun()
        => new()
        {
            CommitSha = _commitSha,
            Command = "dotnet",
            Arguments =
            [
                "exec",
                typeof(GraphProductionGateRunner).Assembly.Location,
                "--m40-verify-artifact",
                "{artifact}",
            ],
            WorkingDirectory = ".",
            ExitCode = 0,
        };

    private GraphProductionArtifactEvidence WriteArtifact<T>(
        string fileName,
        T artifact,
        JsonTypeInfo<T> typeInfo,
        GraphProductionArtifactRun run)
    {
        string path = Path.Combine(_artifactDirectory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(artifact, typeInfo));
        return CreateReference(fileName, run);
    }

    private GraphProductionArtifactEvidence CreateReference(
        string fileName,
        GraphProductionArtifactRun run)
    {
        string path = Path.Combine(_artifactDirectory, fileName);
        using FileStream stream = File.OpenRead(path);
        string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new GraphProductionArtifactEvidence
        {
            Path = fileName,
            Sha256 = digest,
            Command = run.Command,
            Arguments = run.Arguments,
            WorkingDirectory = run.WorkingDirectory,
            ExpectedExitCode = run.ExitCode,
            TimeoutSeconds = 60,
        };
    }

    private T ReadArtifact<T>(GraphProductionArtifactEvidence reference, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        using FileStream stream = File.OpenRead(Path.Combine(_artifactDirectory, reference.Path));
        return JsonSerializer.Deserialize(stream, typeInfo)
            ?? throw new InvalidDataException("测试 artifact 反序列化为空。");
    }

    private string InitializeRepository()
    {
        File.WriteAllText(Path.Combine(_rootDirectory, ".gitignore"), "artifacts/\nevaluated/\n");
        _ = RunGit("init");
        _ = RunGit("add", ".gitignore");
        _ = RunGit(
            "-c",
            "user.name=SonnetDB Tests",
            "-c",
            "user.email=tests@sonnetdb.invalid",
            "-c",
            "commit.gpgsign=false",
            "commit",
            "-m",
            "test fixture");
        return RunGit("rev-parse", "HEAD").Trim();
    }

    private string RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _rootDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        GraphEvidenceProcessResult result = GraphEvidenceProcessRunner.Run(
            startInfo,
            TimeSpan.FromSeconds(30),
            captureOutput: true);
        if (!result.Completed || result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} 失败：{result.Diagnostic} stderr={result.StandardError}");
        }
        return result.StandardOutput;
    }

    private static long[] Repeat(long value)
        => Enumerable.Repeat(value, SampleCount).ToArray();
}
