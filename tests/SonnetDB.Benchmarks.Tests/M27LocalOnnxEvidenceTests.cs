using System.Text.Json;
using System.Text.Json.Nodes;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

public sealed class M27LocalOnnxEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-m27-local-onnx-evidence-" + Guid.NewGuid().ToString("N"));

    public M27LocalOnnxEvidenceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Run_MissingModel_WritesReplayableNotReadyReport()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        M27LocalOnnxEvidenceReport report = await M27LocalOnnxEvidenceRunner.RunAsync(
            inputs.ToOptions(Path.Combine(_root, "missing-output")));

        Assert.Equal("NOT_READY", report.Status);
        Assert.Equal("m27-local-onnx-evidence-v2", report.Schema);
        Assert.False(report.Model.Exists);
        Assert.False(report.TargetModelLoaded);
        Assert.False(report.TargetModelEvidence);
        Assert.False(string.IsNullOrWhiteSpace(report.Tokenizer.Sha256));
        Assert.False(string.IsNullOrWhiteSpace(report.ProfileArtifact.Sha256));
        Assert.False(string.IsNullOrWhiteSpace(report.CorpusArtifact.Sha256));
        Assert.Equal("https://example.invalid/model-card", report.Provenance.Source);
        Assert.Equal("contract-fixture-v1", report.Provenance.Version);
        Assert.Equal("Apache-2.0", report.Provenance.License);
        Assert.True(report.Environment.PeakWorkingSetBytes >= report.Environment.WorkingSetBeforeBytes);
        Assert.True(report.Environment.PeakManagedMemoryBytes >= report.Environment.ManagedMemoryBeforeBytes);
        Assert.Equal("contract-test", report.Environment.Name);
        Assert.Equal(2, report.Environment.Threads.RequestedIntraOpThreads);
        Assert.Equal(1, report.Environment.Threads.RequestedInterOpThreads);
        Assert.False(report.Environment.Threads.AppliedToSession);
        Assert.Null(report.Environment.Threads.EffectiveIntraOpThreads);
        Assert.Null(report.Environment.Threads.EffectiveInterOpThreads);
        Assert.Equal(6, report.BoundarySamples.Length);
        Assert.Contains(report.BoundarySamples, static sample => sample is
            { Scenario: "batch-input", Status: "NOT_SUPPORTED" });
        Assert.Empty(report.RawSamples);
        Assert.Contains(report.Failures, static failure => failure.Contains("model", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--model", report.ReplayArguments);
        Assert.Contains("--environment", report.ReplayArguments);
        Assert.Contains("--intra-op-threads", report.ReplayArguments);
        Assert.Contains("--inter-op-threads", report.ReplayArguments);
        Assert.DoesNotContain("--target-model-evidence", report.ReplayArguments);

        string reportPath = Path.Combine(_root, "missing-output", M27LocalOnnxEvidenceRunner.ReportFileName);
        M27LocalOnnxVerificationResult verification = M27LocalOnnxEvidenceVerifier.Verify(reportPath);
        Assert.Equal("NOT_READY", verification.Status);
        Assert.Contains(verification.Findings, static finding => finding.Contains("model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_InvalidOnnxFallback_CannotBecomeReady()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: true);
        M27LocalOnnxEvidenceReport report = await M27LocalOnnxEvidenceRunner.RunAsync(
            inputs.ToOptions(Path.Combine(_root, "fallback-output")));

        Assert.Equal("NOT_READY", report.Status);
        Assert.True(report.Model.Exists);
        Assert.False(string.IsNullOrWhiteSpace(report.Model.Sha256));
        Assert.True(report.ProviderFallback);
        Assert.False(string.IsNullOrWhiteSpace(report.FallbackReason));
        Assert.Contains(report.BoundarySamples, static sample => sample is
            { Scenario: "blank-input", Status: "PASS", ObservedOutcome: "argument-rejected" });
        Assert.Contains(report.BoundarySamples, static sample => sample is
            { Scenario: "malformed-model", Status: "PASS", ObservedOutcome: "observable-fallback" });
        M27LocalOnnxVerificationResult verification = M27LocalOnnxEvidenceVerifier.Verify(
            Path.Combine(_root, "fallback-output", M27LocalOnnxEvidenceRunner.ReportFileName));
        Assert.Equal("NOT_READY", verification.Status);
        Assert.Contains(verification.Findings, static finding => finding.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_TamperedSummaryAndChangedFixture_AreRejectedOrNotReady()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        string output = Path.Combine(_root, "tamper-output");
        _ = await M27LocalOnnxEvidenceRunner.RunAsync(inputs.ToOptions(output));
        string reportPath = Path.Combine(output, M27LocalOnnxEvidenceRunner.ReportFileName);

        JsonNode root = JsonNode.Parse(File.ReadAllText(reportPath))!;
        root["summary"]!["p95Milliseconds"] = 123.0;
        File.WriteAllText(reportPath, root.ToJsonString());
        M27LocalOnnxVerificationResult tampered = M27LocalOnnxEvidenceVerifier.Verify(reportPath);
        Assert.Equal("INVALID", tampered.Status);
        Assert.Contains(tampered.Findings, static finding => finding.Contains("Summary", StringComparison.Ordinal));

        _ = await M27LocalOnnxEvidenceRunner.RunAsync(inputs.ToOptions(output));
        File.AppendAllText(inputs.CorpusPath, " ");
        M27LocalOnnxVerificationResult changedFixture = M27LocalOnnxEvidenceVerifier.Verify(reportPath);
        Assert.Equal("NOT_READY", changedFixture.Status);
        Assert.Contains(changedFixture.Findings, static finding => finding.Contains("corpus", StringComparison.OrdinalIgnoreCase)
            && finding.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_ReportMissingGitEvidence_ReturnsInvalid()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        string output = Path.Combine(_root, "missing-git-output");
        _ = await M27LocalOnnxEvidenceRunner.RunAsync(inputs.ToOptions(output));
        string reportPath = Path.Combine(output, M27LocalOnnxEvidenceRunner.ReportFileName);
        JsonObject root = JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject();
        Assert.True(root.Remove("git"));
        File.WriteAllText(reportPath, root.ToJsonString());

        M27LocalOnnxVerificationResult verification = M27LocalOnnxEvidenceVerifier.Verify(reportPath);

        Assert.Equal("INVALID", verification.Status);
        Assert.Contains(verification.Findings, static finding => finding.Contains("git evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_V1Schema_IsRejectedWithStableVersionFinding()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        string output = Path.Combine(_root, "v1-output");
        _ = await M27LocalOnnxEvidenceRunner.RunAsync(inputs.ToOptions(output));
        string reportPath = Path.Combine(output, M27LocalOnnxEvidenceRunner.ReportFileName);
        JsonNode root = JsonNode.Parse(File.ReadAllText(reportPath))!;
        root["schema"] = "m27-local-onnx-evidence-v1";
        File.WriteAllText(reportPath, root.ToJsonString());

        M27LocalOnnxVerificationResult verification = M27LocalOnnxEvidenceVerifier.Verify(reportPath);

        Assert.Equal("INVALID", verification.Status);
        Assert.Contains(verification.Findings, static finding => finding.Contains(
            "expected m27-local-onnx-evidence-v2",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_ClaimedAppliedThreads_IsRejected()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        string output = Path.Combine(_root, "thread-claim-output");
        _ = await M27LocalOnnxEvidenceRunner.RunAsync(inputs.ToOptions(output));
        string reportPath = Path.Combine(output, M27LocalOnnxEvidenceRunner.ReportFileName);
        JsonNode root = JsonNode.Parse(File.ReadAllText(reportPath))!;
        root["environment"]!["threads"]!["appliedToSession"] = true;
        root["environment"]!["threads"]!["effectiveIntraOpThreads"] = 2;
        File.WriteAllText(reportPath, root.ToJsonString());

        M27LocalOnnxVerificationResult verification = M27LocalOnnxEvidenceVerifier.Verify(reportPath);

        Assert.Equal("INVALID", verification.Status);
        Assert.Contains(verification.Findings, static finding => finding.Contains(
            "cannot claim applied/effective",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_MissingBoundaryScenario_IsRejected()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        string output = Path.Combine(_root, "missing-boundary-output");
        _ = await M27LocalOnnxEvidenceRunner.RunAsync(inputs.ToOptions(output));
        string reportPath = Path.Combine(output, M27LocalOnnxEvidenceRunner.ReportFileName);
        JsonNode root = JsonNode.Parse(File.ReadAllText(reportPath))!;
        JsonArray samples = root["boundarySamples"]!.AsArray();
        JsonNode batch = samples.Single(static sample => sample!["scenario"]!.GetValue<string>() == "batch-input")!;
        Assert.True(samples.Remove(batch));
        File.WriteAllText(reportPath, root.ToJsonString());

        M27LocalOnnxVerificationResult verification = M27LocalOnnxEvidenceVerifier.Verify(reportPath);

        Assert.Equal("INVALID", verification.Status);
        Assert.Contains(verification.Findings, static finding => finding.Contains(
            "Required boundary scenario 'batch-input' is missing",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_FabricatedBatchPass_IsRejected()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        string output = Path.Combine(_root, "batch-claim-output");
        _ = await M27LocalOnnxEvidenceRunner.RunAsync(inputs.ToOptions(output));
        string reportPath = Path.Combine(output, M27LocalOnnxEvidenceRunner.ReportFileName);
        JsonNode root = JsonNode.Parse(File.ReadAllText(reportPath))!;
        JsonNode batch = root["boundarySamples"]!.AsArray()
            .Single(static sample => sample!["scenario"]!.GetValue<string>() == "batch-input")!;
        batch["status"] = "PASS";
        batch["observedOutcome"] = "embedding";
        File.WriteAllText(reportPath, root.ToJsonString());

        M27LocalOnnxVerificationResult verification = M27LocalOnnxEvidenceVerifier.Verify(reportPath);

        Assert.Equal("INVALID", verification.Status);
        Assert.Contains(verification.Findings, static finding => finding.Contains(
            "cannot claim real batch evidence",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("documents", "documents collection")]
    [InlineData("queries", "queries collection")]
    [InlineData("relevantDocumentIds", "relevantDocumentIds collection")]
    public async Task Run_CorpusMissingRequiredMember_WritesClearNotReadyFailure(
        string missingMember,
        string expectedFinding)
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        string corpusJson = missingMember switch
        {
            "documents" =>
                """
                {
                  "schema": "m27-local-onnx-corpus-v1",
                  "name": "missing-documents-v1",
                  "k": 1,
                  "minimumRecallAtK": 1.0,
                  "queries": [
                    { "id": "query-hello", "text": "hello", "relevantDocumentIds": ["doc-hello"] }
                  ]
                }
                """,
            "queries" =>
                """
                {
                  "schema": "m27-local-onnx-corpus-v1",
                  "name": "missing-queries-v1",
                  "k": 1,
                  "minimumRecallAtK": 1.0,
                  "documents": [
                    { "id": "doc-hello", "text": "hello" }
                  ]
                }
                """,
            "relevantDocumentIds" =>
                """
                {
                  "schema": "m27-local-onnx-corpus-v1",
                  "name": "missing-relevant-documents-v1",
                  "k": 1,
                  "minimumRecallAtK": 1.0,
                  "documents": [
                    { "id": "doc-hello", "text": "hello" }
                  ],
                  "queries": [
                    { "id": "query-hello", "text": "hello" }
                  ]
                }
                """,
            _ => throw new InvalidOperationException($"Unknown fixture selector '{missingMember}'."),
        };
        File.WriteAllText(inputs.CorpusPath, corpusJson);

        M27LocalOnnxEvidenceReport report = await M27LocalOnnxEvidenceRunner.RunAsync(
            inputs.ToOptions(Path.Combine(_root, "missing-corpus-" + missingMember)));

        Assert.Equal("NOT_READY", report.Status);
        Assert.Contains(report.Failures, finding => finding.Contains(expectedFinding, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_HonestLowRecallFailure_RemainsNotReadyInsteadOfInvalid()
    {
        EvidenceInputs inputs = WriteInputs(writeInvalidModel: false);
        string output = Path.Combine(_root, "low-recall-output");
        M27LocalOnnxEvidenceReport baseline = await M27LocalOnnxEvidenceRunner.RunAsync(inputs.ToOptions(output));
        M27LocalOnnxQuerySample[] samples =
        [
            Sample(1, 1.0),
            Sample(2, 2.0),
        ];
        string[] failures = ["Recall@1 0.000000 is below required 1.000000."];
        M27LocalOnnxEvidenceReport lowRecall = baseline with
        {
            Status = "NOT_READY",
            RawSamples = samples,
            Failures = failures,
            Summary = M27LocalOnnxEvidenceRunner.Summarize(samples, failures.Length),
        };
        File.WriteAllBytes(
            Path.Combine(output, M27LocalOnnxEvidenceRunner.ReportFileName),
            JsonSerializer.SerializeToUtf8Bytes(
                lowRecall,
                M27LocalOnnxEvidenceJsonContext.Default.M27LocalOnnxEvidenceReport));

        M27LocalOnnxVerificationResult verification = M27LocalOnnxEvidenceVerifier.Verify(
            Path.Combine(output, M27LocalOnnxEvidenceRunner.ReportFileName));
        Assert.Equal("NOT_READY", verification.Status);
        Assert.DoesNotContain(verification.Findings, static finding => finding.Contains("Summary", StringComparison.Ordinal));

        static M27LocalOnnxQuerySample Sample(int iteration, double elapsed)
            => new(
                "query-hello",
                iteration,
                true,
                elapsed,
                384,
                new string('a', 64),
                [new M27LocalOnnxCandidateSample("doc-world", 0.5)],
                0,
                null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private EvidenceInputs WriteInputs(bool writeInvalidModel)
    {
        string modelPath = Path.Combine(_root, writeInvalidModel ? "invalid.onnx" : "missing.onnx");
        string tokenizerPath = Path.Combine(_root, "vocab.txt");
        string profilePath = Path.Combine(_root, "profile.json");
        string corpusPath = Path.Combine(_root, "corpus.json");
        if (writeInvalidModel)
            File.WriteAllBytes(modelPath, [0]);
        File.WriteAllText(tokenizerPath, "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nhello\nworld\n");
        File.WriteAllText(profilePath,
            """
            {
              "tokenizerType": "bert-wordpiece",
              "inputIdsName": "input_ids",
              "attentionMaskName": "attention_mask",
              "sendAttentionMask": true,
              "sendTokenTypeIds": false,
              "maxTokens": 8,
              "paddingSide": "right",
              "pooling": "mean",
              "outputName": "last_hidden_state",
              "normalize": true,
              "dimensions": 384,
              "addSpecialTokens": true
            }
            """);
        File.WriteAllText(corpusPath,
            """
            {
              "schema": "m27-local-onnx-corpus-v1",
              "name": "contract-fixture-v1",
              "k": 1,
              "minimumRecallAtK": 1.0,
              "documents": [
                { "id": "doc-hello", "text": "hello" },
                { "id": "doc-world", "text": "world" }
              ],
              "queries": [
                { "id": "query-hello", "text": "hello", "relevantDocumentIds": ["doc-hello"] }
              ]
            }
            """);
        return new EvidenceInputs(modelPath, tokenizerPath, profilePath, corpusPath);
    }

    private sealed record EvidenceInputs(
        string ModelPath,
        string TokenizerPath,
        string ProfilePath,
        string CorpusPath)
    {
        internal M27LocalOnnxEvidenceRunOptions ToOptions(string output)
            => new(
                ModelPath,
                TokenizerPath,
                ProfilePath,
                CorpusPath,
                "https://example.invalid/model-card",
                "contract-fixture-v1",
                "Apache-2.0",
                output,
                "contract-test",
                2,
                1,
                false,
                1,
                2);
    }
}
