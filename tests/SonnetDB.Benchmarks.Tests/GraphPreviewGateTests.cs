using System.Text.Json;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>验证 #352 不能用本地 smoke、错档数据或自报 PASS 绕过原生图发布门禁。</summary>
public sealed class GraphPreviewGateTests : IDisposable
{
    private const string TestDirectoryPrefix = "sndb-m40-preview-gate-test-";
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(), TestDirectoryPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>创建只属于本测试的临时目录。</summary>
    public GraphPreviewGateTests() => Directory.CreateDirectory(_rootDirectory);

    /// <summary>仅回收经过归属校验的测试目录。</summary>
    public void Dispose()
    {
        if (!GraphEvidenceOwnedDirectoryCleanup.TryDelete(
                Path.GetFullPath(_rootDirectory), Path.GetTempPath(), TestDirectoryPrefix,
                out string failureReason))
        {
            Console.Error.WriteLine($"preview-gate-test-temp-retained path={_rootDirectory} reason={failureReason}");
        }
    }

    /// <summary>本地检查即使全部自报通过，也不能形成任何发布门禁 PASS。</summary>
    [Fact]
    public void Evaluate_LocalSmokePass_DoesNotPromoteEitherReleaseGate()
    {
        var input = new GraphPreviewGateInput
        {
            CorrectnessRecoveryChecks =
            [
                new GraphProductionCheckEvidence
                {
                    Id = "local_recovery",
                    Status = GraphProductionEvidenceStatus.Pass,
                    Artifact = new GraphProductionArtifactEvidence { Path = "must-not-open.json" },
                },
            ],
        };

        GraphPreviewGateReport report = GraphPreviewGateEvaluator.Evaluate(input, _rootDirectory);

        Assert.Equal("m40-graph-preview-gate-v1", report.Schema);
        Assert.Equal("#352", report.Issue);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.LocalSmoke);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.NotRun, report.ReleaseDecision);
        Assert.Equal("preview_not_attempted", Assert.Single(report.Findings).Code);
    }

    /// <summary>Preview 的十二条原生旅程和 C2 检查不会要求 SQL/PGQ 或生产长稳合同。</summary>
    [Fact]
    public void WriteTemplate_NativePreviewContract_RoundTripsWithoutProductionRequirements()
    {
        string path = GraphPreviewGateRunner.WriteTemplate(_rootDirectory);
        GraphPreviewGateInput input = JsonSerializer.Deserialize(
            File.ReadAllText(path), GraphPreviewGateJsonContext.Default.GraphPreviewGateInput)!;

        Assert.Equal("m40-graph-preview-input-v1", input.Schema);
        Assert.True(input.PreviewRun);
        Assert.Equal(
            new[] { "CPL-1", "CPL-2", "CPL-3", "EVD-1", "EVD-2", "EVD-3", "SOC-1", "SOC-2", "SOC-3", "TOP-1", "TOP-2", "TOP-3" },
            input.Journeys.Select(static journey => journey.Id));
        Assert.Equal("gate", input.Dataset.Tier);
        Assert.Equal(1_000_000, input.Dataset.VertexCount);
        Assert.Equal(10_000_000, input.Dataset.EdgeCount);
        Assert.Equal("preview-small", input.PreviewSmallDataset.Tier);
        Assert.Equal(100_000, input.PreviewSmallDataset.VertexCount);
        Assert.Equal(1_000_000, input.PreviewSmallDataset.EdgeCount);
        Assert.Contains(input.PerformanceCapacityChecks, static check => check.Id == "couplet_c2");
        Assert.DoesNotContain(input.PerformanceCapacityChecks, static check => check.Id is "couplet_c4" or "native_aot" or "ldbc_snb" or "graphalytics");
        Assert.DoesNotContain(input.CorrectnessRecoveryChecks, static check => check.Id == "postgresql_comparison");
        Assert.Equal(7, input.Gaps.Count);
        Assert.Contains("PGQ-1", GraphProductionGateEvaluator.GetRequiredJourneyIds());
        Assert.Contains("CPL-4", GraphProductionGateEvaluator.GetRequiredJourneyIds());
        Assert.Equal(12, GraphProductionGateEvaluator.GetRequiredGapIds().Count);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string output = Path.Combine(_rootDirectory, "evaluated");
        GraphPreviewGateReport report = GraphPreviewGateRunner.EvaluateManifest(path, output, cancellation.Token);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.True(File.Exists(Path.Combine(output, "m40-graph-preview-gate.json")));
        Assert.False(File.Exists(Path.Combine(output, "m40-graph-production-gate.json")));
    }

    /// <summary>即使伪装为 quick，超过冻结条目数的输入也必须在访问证据前拒绝。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_ThirteenJourneys_RejectsBeforeArtifactOrGitAccess(bool previewRun)
    {
        var input = new GraphPreviewGateInput
        {
            PreviewRun = previewRun,
            Journeys = Enumerable.Range(0, 13)
                .Select(index => new GraphProductionJourneyEvidence { Id = $"unexpected-{index}" }).ToArray(),
        };

        GraphPreviewGateReport report = GraphPreviewGateEvaluator.Evaluate(
            input, Path.Combine(_rootDirectory, "does-not-exist"));

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Equal("manifest_execution_bound", Assert.Single(report.Findings).Code);
    }

    /// <summary>根对象 null 必须拒绝；嵌套 null 不能被悄悄转成可通过的证据。</summary>
    [Fact]
    public void Evaluate_NullInputOrNestedDataset_DoesNotProduceReleasePass()
    {
        Assert.Throws<ArgumentNullException>(() => GraphPreviewGateEvaluator.Evaluate(null!, _rootDirectory));

        GraphPreviewGateReport report = GraphPreviewGateEvaluator.Evaluate(
            new GraphPreviewGateInput { PreviewSmallDataset = null! }, _rootDirectory);

        Assert.NotEqual(GraphProductionEvidenceStatus.Pass, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "manifest_null");
    }

    /// <summary>Production 的输入 schema 不能借 quick 早退冒充 Preview 清单。</summary>
    [Fact]
    public void Evaluate_ProductionSchemaOnLocalInput_RejectsBeforeEarlyReturn()
    {
        GraphPreviewGateReport report = GraphPreviewGateEvaluator.Evaluate(
            new GraphPreviewGateInput { Schema = "m40-graph-production-input-v2" },
            Path.Combine(_rootDirectory, "does-not-exist"));

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "input_schema");
        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "preview_not_attempted");
    }

    /// <summary>反序列化阶段拒绝空对象和缺少独立 schema 的清单，不写入报告。</summary>
    [Theory]
    [InlineData("null", false)]
    [InlineData("{}", true)]
    public void EvaluateManifest_NullOrMissingSchema_RejectsWithoutReport(string json, bool missingSchema)
    {
        string manifest = Path.Combine(_rootDirectory, "invalid.json");
        string output = Path.Combine(_rootDirectory, "evaluated");
        File.WriteAllText(manifest, json);

        if (missingSchema)
            Assert.Throws<JsonException>(() => GraphPreviewGateRunner.EvaluateManifest(manifest, output));
        else
            Assert.Throws<InvalidDataException>(() => GraphPreviewGateRunner.EvaluateManifest(manifest, output));

        Assert.False(Directory.Exists(output));
    }

    /// <summary>超过 4 MiB 的稀疏清单必须先按字节上限拒绝，而不是尝试 JSON 解析。</summary>
    [Fact]
    public void EvaluateManifest_ExceedsByteLimit_RejectsBeforeDeserialization()
    {
        string manifest = Path.Combine(_rootDirectory, "oversized.json");
        using (FileStream stream = File.Create(manifest))
            stream.SetLength(GraphProductionGateRunner.MaximumManifestBytes + 1);
        string output = Path.Combine(_rootDirectory, "evaluated");

        Assert.Throws<InvalidDataException>(() => GraphPreviewGateRunner.EvaluateManifest(manifest, output));

        Assert.False(Directory.Exists(output));
    }

    /// <summary>预取消在打开缺失文件、启动 quick 子进程及写报告前生效。</summary>
    [Fact]
    public async Task EvaluateAsync_PreCancelled_DoesNotReadOrStartQuickOrWriteOutput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string output = Path.Combine(_rootDirectory, "evaluated");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GraphPreviewGateEvaluator.EvaluateAsync(
            new GraphPreviewGateInput { PreviewRun = true }, output, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GraphPreviewGateRunner.EvaluateManifestAsync(
            Path.Combine(_rootDirectory, "missing.json"), output, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => GraphPreviewGateRunner.RunQuick(output, cancellation.Token));

        Assert.False(Directory.Exists(output));
    }

    /// <summary>具名原始证据、双档数据和短恢复验证可以通过 Preview，且不冒称生产长稳通过。</summary>
    [Fact]
    public void Evaluate_CompleteNativeRawEvidence_PassesPreviewWithoutProductionSoak()
    {
        using var fixture = new GraphProductionGateTests();
        GraphPreviewGateInput input = CreatePassingPreviewInput(fixture);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        GraphPreviewGateReport report = GraphPreviewGateEvaluator.Evaluate(
            input, fixture.ArtifactDirectory, cancellation.Token);

        Assert.Empty(report.Findings);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Pass, report.ReleaseDecision);
        Assert.Equal("#352", report.Issue);
        Assert.Equal(12, report.Input.Journeys.Count);
        Assert.Equal(1, report.Input.Recovery.DurationHours);
        Assert.Equal(1, report.Input.Recovery.KillReopenCount);
        Assert.Equal("gate", report.Input.Dataset.Tier);
        Assert.Equal("preview-small", report.Input.PreviewSmallDataset.Tier);
        Assert.All(report.Input.Journeys, static journey => Assert.Equal(1, journey.P99Milliseconds));
    }

    /// <summary>被篡改的原始断言、错档测量、缺失旅程和较宽 Production 阈值不能冒充 Preview。</summary>
    [Fact]
    public void Evaluate_ForgedMissingOrMisboundEvidenceAndRelaxedLatency_FailsBothGates()
    {
        using var fixture = new GraphProductionGateTests();
        GraphPreviewGateInput input = CreatePassingPreviewInput(fixture);
        GraphProductionJourneyEvidence[] journeys = input.Journeys
            .Where(static journey => journey.Id != "EVD-3")
            .Select(journey => MutateJourney(fixture, journey, input.PreviewSmallDataset.OutputDigest))
            .ToArray();
        GraphProductionCheckEvidence[] correctness = input.CorrectnessRecoveryChecks.Select(check =>
        {
            if (check.Id == "backup_restore")
                return check with { Artifact = check.Artifact with { Path = "missing-artifact.json" } };
            if (check.Id is not ("neo4j_comparison" or "budget_cancel"))
                return check;
            GraphProductionCheckArtifact artifact = fixture.ReadArtifact(
                check.Artifact, GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact);
            if (check.Id == "neo4j_comparison")
            {
                File.WriteAllText(Path.Combine(fixture.ArtifactDirectory, check.Artifact.Path), "{\"status\":\"PASS\"}");
                return check with { Artifact = fixture.CreateReference(check.Artifact.Path, artifact.Run) };
            }
            artifact = artifact with { Assertions = [new GraphProductionCheckAssertion { Name = "unrelated", Expected = "1", Actual = "1" }] };
            return check with { Artifact = fixture.WriteArtifact(check.Artifact.Path, artifact,
                GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact, artifact.Run) };
        }).ToArray();
        GraphProductionSoakArtifact recovery = fixture.ReadArtifact(
            input.Recovery.Artifact, GraphProductionArtifactJsonContext.Default.GraphProductionSoakArtifact)
            with { DatasetOutputDigest = input.PreviewSmallDataset.OutputDigest };
        input = input with
        {
            Journeys = journeys,
            CorrectnessRecoveryChecks = correctness,
            PreviewSmallDataset = input.Dataset,
            Recovery = input.Recovery with
            {
                Artifact = fixture.WriteArtifact(input.Recovery.Artifact.Path, recovery,
                    GraphProductionArtifactJsonContext.Default.GraphProductionSoakArtifact, recovery.Run),
            },
            Gaps = input.Gaps.Select((gap, index) => index == 0 ? gap with { Status = "open", Blocks = string.Empty } : gap).ToArray(),
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        GraphPreviewGateReport report = GraphPreviewGateEvaluator.Evaluate(
            input, fixture.ArtifactDirectory, cancellation.Token);

        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.CorrectnessRecovery);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.PerformanceCapacity);
        Assert.Equal(GraphProductionEvidenceStatus.Fail, report.ReleaseDecision);
        Assert.Contains(report.Findings, static finding => finding.Code == "journey_missing");
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_dataset_mismatch" && finding.Message.Contains("recovery", StringComparison.Ordinal));
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_dataset_mismatch" && finding.Message.Contains("TOP-1", StringComparison.Ordinal));
        Assert.Contains(report.Findings, static finding => finding.Code == "preview_check_binding");
        Assert.Contains(report.Findings, static finding => finding.Code == "dataset_contract");
        Assert.Contains(report.Findings, static finding => finding.Code == "blocking_gap");
        Assert.Contains(report.Findings, static finding => finding.Code == "latency_slo" && finding.Message.Contains("SOC-1", StringComparison.Ordinal));
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_missing");
        Assert.Contains(report.Findings, static finding => finding.Code is "artifact_json" or "artifact_schema");
        Assert.Contains(report.Findings, static finding => finding.Code == "artifact_collection_null");
    }

    private static GraphProductionJourneyEvidence MutateJourney(
        GraphProductionGateTests fixture, GraphProductionJourneyEvidence journey, string smallDigest)
    {
        if (journey.Id is not ("SOC-1" or "TOP-1" or "TOP-2"))
            return journey;
        GraphProductionJourneyArtifact artifact = fixture.ReadArtifact(
            journey.Artifact, GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact);
        if (journey.Id == "TOP-1")
            artifact = artifact with { DatasetOutputDigest = smallDigest };
        else if (journey.Id == "TOP-2")
            artifact = artifact with { Rounds = [null!] };
        else
        {
            // 15 ms 满足 Production SOC-1 的 20/50 ms，超过 Preview 的 10/25 ms。
            artifact = artifact with
            {
                Rounds = artifact.Rounds.Select(static round => round with
                {
                    ElapsedMicroseconds = Enumerable.Repeat(15_000L, 10_000).ToArray(),
                }).ToArray(),
            };
            journey = journey with
            {
                P50Milliseconds = 15,
                P95Milliseconds = 15,
                P99Milliseconds = 15,
                MaxMilliseconds = 15,
                ThroughputPerSecond = 1_000d / 15,
            };
        }
        return journey with
        {
            Artifact = fixture.WriteArtifact(journey.Artifact.Path, artifact,
                GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact, artifact.Run),
        };
    }

    private static GraphPreviewGateInput CreatePassingPreviewInput(GraphProductionGateTests fixture)
    {
        GraphProductionGateInput production = fixture.CreatePassingInput(preview: true);
        GraphProductionDatasetArtifact gate = fixture.ReadArtifact(
            production.Dataset.Artifact, GraphProductionArtifactJsonContext.Default.GraphProductionDatasetArtifact)
            with { Tier = "gate" };
        GraphProductionDatasetEvidence gateEvidence = DatasetEvidence(fixture, "dataset.json", gate);
        GraphProductionDatasetEvidence smallEvidence = DatasetEvidence(fixture, "dataset-small.json", gate with
        {
            Tier = "preview-small", VertexCount = 100_000, EdgeCount = 1_000_000,
            OutputDigest = new string('a', 64),
        });
        DateTimeOffset finished = production.FinishedUtc;
        DateTimeOffset started = finished.AddHours(-1);
        GraphProductionSoakArtifact recovery = fixture.ReadArtifact(
            production.Soak.Artifact, GraphProductionArtifactJsonContext.Default.GraphProductionSoakArtifact);
        recovery = recovery with
        {
            DatasetOutputDigest = gate.OutputDigest,
            StartedUtc = started,
            FinishedUtc = finished,
            CheckpointsUtc = [started, started.AddMinutes(30), finished],
            KillReopenSamples = [recovery.KillReopenSamples[0] with { TimestampUtc = started.AddMinutes(30) }],
            ColdOpenMilliseconds = [500, 500, 500],
            ResourceSamples =
            [
                recovery.ResourceSamples[0] with { TimestampUtc = started },
                recovery.ResourceSamples[1] with { TimestampUtc = finished },
            ],
        };
        GraphProductionSoakEvidence recoveryEvidence = production.Soak with
        {
            DurationHours = 1,
            CheckpointCount = 3,
            KillReopenCount = 1,
            InvariantCheckCount = 1,
            Artifact = fixture.WriteArtifact(production.Soak.Artifact.Path, recovery,
                GraphProductionArtifactJsonContext.Default.GraphProductionSoakArtifact, recovery.Run),
        };
        GraphProductionJourneyEvidence[] journeys = production.Journeys.Select(journey =>
        {
            GraphProductionJourneyArtifact artifact = fixture.ReadArtifact(
                journey.Artifact, GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact);
            artifact = artifact with
            {
                DatasetOutputDigest = gate.OutputDigest,
                Rounds = artifact.Rounds.Select(round => round with
                {
                    OracleAssertions = [round.OracleAssertions[0] with { Name = journey.Id }],
                }).ToArray(),
            };
            return journey with { Artifact = fixture.WriteArtifact(journey.Artifact.Path, artifact,
                GraphProductionArtifactJsonContext.Default.GraphProductionJourneyArtifact, artifact.Run) };
        }).ToArray();
        return new GraphPreviewGateInput
        {
            PreviewRun = true,
            CommitSha = production.CommitSha,
            StartedUtc = started,
            FinishedUtc = finished,
            Dataset = gateEvidence,
            PreviewSmallDataset = smallEvidence,
            Environment = production.Environment,
            Recovery = recoveryEvidence,
            Journeys = journeys,
            CorrectnessRecoveryChecks = production.CorrectnessRecoveryChecks.Select(check => check with
            {
                Artifact = BindCheck(fixture, check.Id, check.Artifact),
            }).ToArray(),
            PerformanceCapacityChecks = production.PerformanceCapacityChecks.Select(check => check with
            {
                Artifact = BindCheck(fixture, check.Id, check.Artifact),
            }).ToArray(),
            Gaps = production.Gaps.Select(gap => gap with
            {
                Blocks = "Preview; Couplet C2",
                CloseEvidence = BindCheck(fixture, gap.Id, gap.CloseEvidence!),
            }).ToArray(),
        };
    }

    private static GraphProductionDatasetEvidence DatasetEvidence(
        GraphProductionGateTests fixture, string path, GraphProductionDatasetArtifact artifact)
        => new()
        {
            Tier = artifact.Tier,
            Generator = artifact.Generator,
            Seed = artifact.Seed,
            VertexCount = artifact.VertexCount,
            EdgeCount = artifact.EdgeCount,
            InputDigest = artifact.InputDigest,
            OutputDigest = artifact.OutputDigest,
            Artifact = fixture.WriteArtifact(path, artifact,
                GraphProductionArtifactJsonContext.Default.GraphProductionDatasetArtifact, artifact.Run),
        };

    private static GraphProductionArtifactEvidence BindCheck(
        GraphProductionGateTests fixture, string id, GraphProductionArtifactEvidence reference)
    {
        GraphProductionCheckArtifact artifact = fixture.ReadArtifact(
            reference, GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact);
        IReadOnlyList<string> names = id is "native_journey_oracle" or "neo4j_comparison"
            ? GraphPreviewGateEvaluator.GetRequiredJourneyIds() : [id];
        string gateDigest = new string('c', 64);
        string smallDigest = new string('a', 64);
        string assertionDigest = id switch
        {
            "preview_small_capacity" => smallDigest,
            "gate_capacity" => gateDigest,
            "complexity_trend" => smallDigest + ":" + gateDigest,
            _ => "expected",
        };
        artifact = artifact with
        {
            DatasetOutputDigest = id == "preview_small_capacity" ? smallDigest : gateDigest,
            ComparisonDatasetOutputDigest = id == "complexity_trend" ? smallDigest : string.Empty,
            Assertions = names.Select(name => new GraphProductionCheckAssertion
            {
                Name = name,
                Expected = assertionDigest,
                Actual = assertionDigest,
            }).ToArray(),
        };
        return fixture.WriteArtifact(reference.Path, artifact,
            GraphProductionArtifactJsonContext.Default.GraphProductionCheckArtifact, artifact.Run);
    }
}
