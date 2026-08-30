using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Configuration;
using SonnetDB.Copilot;

namespace SonnetDB.Benchmarks.Benchmarks;

internal sealed record M27LocalOnnxEvidenceRunOptions(
    string ModelPath,
    string TokenizerPath,
    string ProfilePath,
    string CorpusPath,
    string ModelSource,
    string ModelVersion,
    string ModelLicense,
    string OutputDirectory,
    string EnvironmentName,
    int IntraOpThreads = 0,
    int InterOpThreads = 0,
    bool TargetModelEvidence = false,
    int WarmupIterations = 3,
    int MeasurementIterations = 10);

internal static class M27LocalOnnxEvidenceRunner
{
    internal const string ReportFileName = "m27-local-onnx-evidence.json";
    private const string ReportSchema = "m27-local-onnx-evidence-v2";
    private const string CorpusSchema = "m27-local-onnx-corpus-v1";
    private static readonly string[] RequiredBoundaryScenarios =
    [
        "blank-input",
        "unicode-cjk-input",
        "overlong-input",
        "attention-mask-padding",
        "batch-input",
        "malformed-model",
    ];

    internal static async Task<M27LocalOnnxEvidenceReport> RunAsync(
        M27LocalOnnxEvidenceRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TokenizerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CorpusPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelLicense);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EnvironmentName);
        ArgumentOutOfRangeException.ThrowIfNegative(options.IntraOpThreads);
        ArgumentOutOfRangeException.ThrowIfNegative(options.InterOpThreads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.WarmupIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MeasurementIterations);

        Directory.CreateDirectory(options.OutputDirectory);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        long workingSetBefore = Environment.WorkingSet;
        long managedMemoryBefore = GC.GetTotalMemory(forceFullCollection: false);
        var memory = new MemorySampler(workingSetBefore, managedMemoryBefore);
        M27LocalOnnxGitEvidence git = CaptureGitEvidence();
        M27LocalOnnxArtifactEvidence model = ReadArtifact(options.ModelPath);
        M27LocalOnnxArtifactEvidence tokenizer = ReadArtifact(options.TokenizerPath);
        M27LocalOnnxArtifactEvidence profileArtifact = ReadArtifact(options.ProfilePath);
        M27LocalOnnxArtifactEvidence corpusArtifact = ReadArtifact(options.CorpusPath);
        var failures = new List<string>();
        RequireArtifact(model, "model", failures);
        RequireArtifact(tokenizer, "tokenizer", failures);
        RequireArtifact(profileArtifact, "profile", failures);
        RequireArtifact(corpusArtifact, "corpus", failures);

        CopilotEmbeddingModelProfile? profile = TryReadProfile(profileArtifact, failures);
        M27LocalOnnxCorpus? corpus = TryReadCorpus(corpusArtifact, failures);
        if (corpus is not null)
            ValidateCorpus(corpus, failures);
        if (profile is not null)
            profile.TokenizerModelPath = tokenizer.Path;

        var samples = new List<M27LocalOnnxQuerySample>();
        var boundarySamples = new List<M27LocalOnnxBoundarySample>();
        bool providerConfigured = false;
        bool providerFallback = false;
        bool targetModelLoaded = false;
        string? fallbackReason = null;
        LocalOnnxExecutionState? executionState = null;
        string? profileSha256 = profile is null ? null : HashProfile(profile);

        if (failures.Count == 0 && profile is not null && corpus is not null)
        {
            using var provider = new LocalOnnxEmbeddingProvider(new CopilotEmbeddingOptions
            {
                Provider = "local",
                LocalModelPath = model.Path,
                ModelProfile = profile,
                IntraOpThreads = options.IntraOpThreads,
                InterOpThreads = options.InterOpThreads,
            });
            providerConfigured = provider.IsConfigured;
            boundarySamples.Add(await RunBlankInputBoundaryAsync(provider, cancellationToken).ConfigureAwait(false));
            try
            {
                for (int index = 0; index < options.WarmupIterations; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = await EmbedAndSampleAsync(provider, corpus.Queries[0].Text, memory, cancellationToken).ConfigureAwait(false);
                    if (provider.IsFallback)
                        throw new M27LocalOnnxNotReadyException(provider.FallbackReason ?? "Provider entered hash fallback during warmup.");
                    targetModelLoaded = true;
                }

                boundarySamples.Add(await RunEmbeddingBoundaryAsync(
                    "unicode-cjk-input",
                    "工业温度传感器告警：泵站压力异常。",
                    provider,
                    memory,
                    cancellationToken).ConfigureAwait(false));
                boundarySamples.Add(await RunEmbeddingBoundaryAsync(
                    "overlong-input",
                    BuildOverlongInput(corpus.Queries[0].Text, profile.MaxTokens),
                    provider,
                    memory,
                    cancellationToken).ConfigureAwait(false));
                boundarySamples.Add(await RunAttentionMaskBoundaryAsync(
                    provider,
                    profile,
                    corpus.Queries[0].Text,
                    memory,
                    cancellationToken).ConfigureAwait(false));
                boundarySamples.Add(await RunBatchBoundaryAsync(
                    provider,
                    [corpus.Documents[0].Text, corpus.Queries[0].Text],
                    profile.Dimensions,
                    memory,
                    cancellationToken).ConfigureAwait(false));

                var documentVectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
                string[] documentTexts = corpus.Documents.Select(static document => document.Text).ToArray();
                IReadOnlyList<float[]> embeddedDocuments = await EmbedBatchAndSampleAsync(
                    provider,
                    documentTexts,
                    memory,
                    cancellationToken).ConfigureAwait(false);
                if (provider.IsFallback)
                    throw new M27LocalOnnxNotReadyException(provider.FallbackReason ?? "Provider entered hash fallback while embedding corpus documents.");
                for (var index = 0; index < corpus.Documents.Length; index++)
                {
                    documentVectors.Add(corpus.Documents[index].Id, embeddedDocuments[index]);
                }

                foreach (M27LocalOnnxCorpusQuery query in corpus.Queries)
                {
                    for (int iteration = 1; iteration <= options.MeasurementIterations; iteration++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        samples.Add(await MeasureQueryAsync(
                            provider,
                            query,
                            corpus.K,
                            iteration,
                            documentVectors,
                            memory,
                            cancellationToken).ConfigureAwait(false));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException
                or M27LocalOnnxNotReadyException
                or IOException)
            {
                failures.Add(exception.Message);
            }
            providerConfigured = provider.IsConfigured;
            providerFallback = provider.IsFallback;
            fallbackReason = provider.FallbackReason;
            executionState = provider.ExecutionState;
        }
        if (profile is not null && tokenizer.Exists)
        {
            boundarySamples.Add(await RunMalformedModelBoundaryAsync(
                profile,
                tokenizer.Path,
                cancellationToken).ConfigureAwait(false));
        }
        AddMissingBoundarySamples(boundarySamples);

        if (!IsSha256(git.CommitSha, expectedLength: 40))
            failures.Add("Evidence is not bound to a valid 40-hex Git commit.");
        if (!git.WorktreeClean)
            failures.Add("Evidence worktree is not clean.");
        if (!options.TargetModelEvidence)
            failures.Add("Run was not explicitly declared as target-model evidence.");
        if (!targetModelLoaded)
            failures.Add("The specified target model was not successfully loaded and executed.");
        if (executionState is null || !executionState.AppliedToSession)
            failures.Add("Requested ONNX thread settings were not applied to a successfully initialized target-model session.");
        foreach (M27LocalOnnxBoundarySample boundary in boundarySamples.Where(static sample => sample.Status != "PASS"))
            failures.Add($"Boundary '{boundary.Scenario}' is {boundary.Status}: {boundary.Detail}");
        M27LocalOnnxEvidenceSummary measuredSummary = Summarize(samples, failures.Count);
        if (corpus is not null && measuredSummary.SampleCount > 0 && measuredSummary.RecallAtK < corpus.MinimumRecallAtK)
            failures.Add($"Recall@{corpus.K} {measuredSummary.RecallAtK:F6} is below required {corpus.MinimumRecallAtK:F6}.");
        if (providerFallback && !failures.Any(static value => value.Contains("fallback", StringComparison.OrdinalIgnoreCase)))
            failures.Add(fallbackReason ?? "Provider used hash fallback.");
        M27LocalOnnxEvidenceSummary summary = Summarize(samples, failures.Count);
        bool qualityReady = corpus?.Documents is not null
            && corpus.Queries is not null
            && summary.SampleCount == checked(corpus.Queries.Length * options.MeasurementIterations)
            && summary.FailureCount == 0
            && summary.RecallAtK >= corpus.MinimumRecallAtK;
        string status = failures.Count == 0
            && providerConfigured
            && !providerFallback
            && targetModelLoaded
            && options.TargetModelEvidence
            && boundarySamples.All(static sample => sample.Status == "PASS")
            && qualityReady
            && git.WorktreeClean
            && IsSha256(git.CommitSha, expectedLength: 40)
            ? "PASS"
            : "NOT_READY";
        M27LocalOnnxCorpusEvidence? corpusEvidence = corpus?.Documents is not null
            && corpus.Queries is not null
            ? new M27LocalOnnxCorpusEvidence(
                corpus.Schema,
                corpus.Name,
                corpus.K,
                corpus.MinimumRecallAtK,
                corpus.Documents.Length,
                corpus.Queries.Length)
            : null;

        var report = new M27LocalOnnxEvidenceReport(
            ReportSchema,
            "M27 #185",
            status,
            git,
            startedUtc,
            DateTimeOffset.UtcNow,
            options.WarmupIterations,
            options.MeasurementIterations,
            model,
            tokenizer,
            profileArtifact,
            corpusArtifact,
            new M27LocalOnnxModelProvenance(
                options.ModelSource,
                options.ModelVersion,
            options.ModelLicense),
            profile,
            profileSha256,
            corpusEvidence,
            providerConfigured,
            providerFallback,
            fallbackReason,
            targetModelLoaded,
            options.TargetModelEvidence,
            CaptureEnvironment(memory, options, executionState),
            boundarySamples.ToArray(),
            samples.ToArray(),
            failures.ToArray(),
            summary,
            BuildReplayArguments(options));
        WriteReport(options.OutputDirectory, report);
        return report;
    }

    private static async Task<M27LocalOnnxQuerySample> MeasureQueryAsync(
        LocalOnnxEmbeddingProvider provider,
        M27LocalOnnxCorpusQuery query,
        int k,
        int iteration,
        IReadOnlyDictionary<string, float[]> documentVectors,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            float[] vector = await EmbedAndSampleAsync(provider, query.Text, memory, cancellationToken).ConfigureAwait(false);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (provider.IsFallback)
                throw new M27LocalOnnxNotReadyException(provider.FallbackReason ?? "Provider entered hash fallback during measured query.");
            M27LocalOnnxCandidateSample[] candidates = documentVectors
                .Select(pair => new M27LocalOnnxCandidateSample(pair.Key, Cosine(vector, pair.Value)))
                .OrderByDescending(static candidate => candidate.Similarity)
                .ThenBy(static candidate => candidate.DocumentId, StringComparer.Ordinal)
                .Take(k)
                .ToArray();
            double recall = Recall(query.RelevantDocumentIds, candidates.Select(static value => value.DocumentId));
            return new M27LocalOnnxQuerySample(
                query.Id,
                iteration,
                true,
                elapsed,
                vector.Length,
                HashVector(vector),
                candidates,
                recall,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or M27LocalOnnxNotReadyException
            or IOException)
        {
            return new M27LocalOnnxQuerySample(
                query.Id,
                iteration,
                false,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                0,
                null,
                [],
                0,
                exception.Message);
        }
    }

    internal static M27LocalOnnxEvidenceSummary Summarize(
        IReadOnlyList<M27LocalOnnxQuerySample> samples,
        int setupFailureCount)
    {
        double[] latencies = samples
            .Where(static sample => sample.Success)
            .Select(static sample => sample.ElapsedMilliseconds)
            .Order()
            .ToArray();
        double recall = samples.Count == 0
            ? 0
            : samples.Where(static sample => sample.Success).Select(static sample => sample.RecallAtK).DefaultIfEmpty().Average();
        return new M27LocalOnnxEvidenceSummary(
            samples.Count,
            setupFailureCount + samples.Count(static sample => !sample.Success),
            recall,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99));
    }

    internal static double Recall(
        IReadOnlyList<string> relevantDocumentIds,
        IEnumerable<string> retrievedDocumentIds)
    {
        var relevant = relevantDocumentIds.ToHashSet(StringComparer.Ordinal);
        if (relevant.Count == 0)
            return 0;
        int hits = retrievedDocumentIds.Distinct(StringComparer.Ordinal).Count(relevant.Contains);
        return (double)hits / relevant.Count;
    }

    internal static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0)
            return 0;
        int index = Math.Max(0, (int)Math.Ceiling(percentile * ordered.Count) - 1);
        return ordered[index];
    }

    internal static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string HashProfile(CopilotEmbeddingModelProfile profile)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            profile,
            M27LocalOnnxEvidenceJsonContext.Default.CopilotEmbeddingModelProfile))).ToLowerInvariant();

    internal static IReadOnlyList<string> BoundaryScenarios => RequiredBoundaryScenarios;

    private static async ValueTask<float[]> EmbedAndSampleAsync(
        LocalOnnxEmbeddingProvider provider,
        string text,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            memory.Sample();
        }
    }

    private static async ValueTask<IReadOnlyList<float[]>> EmbedBatchAndSampleAsync(
        LocalOnnxEmbeddingProvider provider,
        IReadOnlyList<string> texts,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            memory.Sample();
        }
    }

    private static async Task<M27LocalOnnxBoundarySample> RunBlankInputBoundaryAsync(
        LocalOnnxEmbeddingProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            float[] vector = await provider.EmbedAsync(" \t\r\n", cancellationToken).ConfigureAwait(false);
            return new M27LocalOnnxBoundarySample(
                "blank-input",
                "FAILED",
                "Whitespace-only input must be rejected before inference.",
                "embedding",
                4,
                vector.Length,
                HashVector(vector),
                "Provider returned an embedding for whitespace-only input.");
        }
        catch (ArgumentException exception)
        {
            return new M27LocalOnnxBoundarySample(
                "blank-input",
                "PASS",
                "Whitespace-only input must be rejected before inference.",
                "argument-rejected",
                4,
                0,
                null,
                exception.Message);
        }
    }

    private static async Task<M27LocalOnnxBoundarySample> RunEmbeddingBoundaryAsync(
        string scenario,
        string text,
        LocalOnnxEmbeddingProvider provider,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        try
        {
            float[] vector = await EmbedAndSampleAsync(provider, text, memory, cancellationToken).ConfigureAwait(false);
            if (provider.IsFallback)
            {
                return new M27LocalOnnxBoundarySample(
                    scenario,
                    "FAILED",
                    "The specified ONNX model must produce a non-fallback embedding.",
                    "hash-fallback",
                    text.Length,
                    vector.Length,
                    HashVector(vector),
                    provider.FallbackReason ?? "Provider entered hash fallback.");
            }

            return new M27LocalOnnxBoundarySample(
                scenario,
                "PASS",
                "The specified ONNX model must produce a non-fallback embedding.",
                "embedding",
                text.Length,
                vector.Length,
                HashVector(vector),
                "The target provider returned a vector without fallback.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            return new M27LocalOnnxBoundarySample(
                scenario,
                "FAILED",
                "The specified ONNX model must produce a non-fallback embedding.",
                "rejected",
                text.Length,
                0,
                null,
                exception.Message);
        }
    }

    private static async Task<M27LocalOnnxBoundarySample> RunBatchBoundaryAsync(
        LocalOnnxEmbeddingProvider provider,
        IReadOnlyList<string> texts,
        int expectedDimensions,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        int characterCount = texts.Sum(static text => text.Length);
        long runCountBefore = provider.InferenceRunCount;
        try
        {
            IReadOnlyList<float[]> vectors = await EmbedBatchAndSampleAsync(
                provider,
                texts,
                memory,
                cancellationToken).ConfigureAwait(false);
            if (provider.IsFallback)
            {
                return new M27LocalOnnxBoundarySample(
                    "batch-input",
                    "FAILED",
                    "One ONNX Run must return one target-model vector per input.",
                    "hash-fallback",
                    characterCount,
                    0,
                    null,
                    provider.FallbackReason ?? "Provider entered hash fallback.");
            }

            long runCountDelta = provider.InferenceRunCount - runCountBefore;
            bool valid = runCountDelta == 1
                && vectors.Count == texts.Count
                && vectors.All(vector => vector.Length == expectedDimensions);
            return new M27LocalOnnxBoundarySample(
                "batch-input",
                valid ? "PASS" : "FAILED",
                "One ONNX Run must return one target-model vector per input.",
                valid ? "batched-embedding" : "invalid-batch-shape",
                characterCount,
                valid ? expectedDimensions : 0,
                valid ? HashVectors(vectors) : null,
                valid
                    ? $"A single batched provider call returned {vectors.Count} target-model vectors with one InferenceSession.Run."
                    : $"Batch used {runCountDelta} Run call(s), returned {vectors.Count} vector(s) for {texts.Count} input(s), or a vector dimension differed from {expectedDimensions}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            return new M27LocalOnnxBoundarySample(
                "batch-input",
                "FAILED",
                "One ONNX Run must return one target-model vector per input.",
                "rejected",
                characterCount,
                0,
                null,
                exception.Message);
        }
    }

    private static Task<M27LocalOnnxBoundarySample> RunAttentionMaskBoundaryAsync(
        LocalOnnxEmbeddingProvider provider,
        CopilotEmbeddingModelProfile profile,
        string queryText,
        MemorySampler memory,
        CancellationToken cancellationToken)
    {
        if (profile.SendAttentionMask is null)
        {
            return Task.FromResult(new M27LocalOnnxBoundarySample(
                "attention-mask-padding",
                "NOT_RUN",
                "The effective attention-mask binding and padding path must be explicit.",
                "auto-binding-unobservable",
                0,
                0,
                null,
                "The profile uses automatic attention-mask binding, but the provider does not expose the resolved input binding."));
        }

        if (profile.SendAttentionMask == false)
        {
            return Task.FromResult(new M27LocalOnnxBoundarySample(
                "attention-mask-padding",
                "PASS",
                "A model that does not use attention masks must explicitly disable the input.",
                "profile-explicitly-disabled",
                0,
                0,
                null,
                "The effective profile explicitly disables attention-mask input."));
        }

        if (string.IsNullOrWhiteSpace(profile.AttentionMaskName))
        {
            return Task.FromResult(new M27LocalOnnxBoundarySample(
                "attention-mask-padding",
                "FAILED",
                "An enabled attention mask requires an explicit tensor name and a padded inference.",
                "missing-explicit-name",
                0,
                0,
                null,
                "SendAttentionMask is true but AttentionMaskName is blank."));
        }

        string shortText = queryText.Length <= 16 ? queryText : queryText[..16];
        return RunEmbeddingBoundaryAsync(
            "attention-mask-padding",
            shortText,
            provider,
            memory,
            cancellationToken);
    }

    private static async Task<M27LocalOnnxBoundarySample> RunMalformedModelBoundaryAsync(
        CopilotEmbeddingModelProfile profile,
        string tokenizerPath,
        CancellationToken cancellationToken)
    {
        string invalidModelPath = Path.Combine(
            Path.GetTempPath(),
            "sndb-m27-malformed-model-" + Guid.NewGuid().ToString("N") + ".onnx");
        try
        {
            File.WriteAllBytes(invalidModelPath, [0]);
            profile.TokenizerModelPath = tokenizerPath;
            using var provider = new LocalOnnxEmbeddingProvider(new CopilotEmbeddingOptions
            {
                Provider = "local",
                LocalModelPath = invalidModelPath,
                ModelProfile = profile,
            });
            try
            {
                float[] vector = await provider.EmbedAsync("malformed model evidence", cancellationToken).ConfigureAwait(false);
                if (provider.IsFallback)
                {
                    return new M27LocalOnnxBoundarySample(
                        "malformed-model",
                        "PASS",
                        "A malformed ONNX artifact must be rejected or enter observable fallback.",
                        "observable-fallback",
                        24,
                        vector.Length,
                        HashVector(vector),
                        provider.FallbackReason ?? "Provider entered observable fallback.");
                }

                return new M27LocalOnnxBoundarySample(
                    "malformed-model",
                    "FAILED",
                    "A malformed ONNX artifact must be rejected or enter observable fallback.",
                    "embedding",
                    24,
                    vector.Length,
                    HashVector(vector),
                    "Malformed model unexpectedly produced a non-fallback embedding.");
            }
            catch (InvalidDataException exception)
            {
                return new M27LocalOnnxBoundarySample(
                    "malformed-model",
                    "PASS",
                    "A malformed ONNX artifact must be rejected or enter observable fallback.",
                    "fail-closed-rejection",
                    24,
                    0,
                    null,
                    exception.Message);
            }
        }
        finally
        {
            try
            {
                File.Delete(invalidModelPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string BuildOverlongInput(string seed, int maxTokens)
    {
        string boundedSeed = seed.Length <= 64 ? seed : seed[..64];
        int repetitions = checked(maxTokens + 8);
        return string.Join(' ', Enumerable.Repeat(boundedSeed, repetitions));
    }

    private static void AddMissingBoundarySamples(ICollection<M27LocalOnnxBoundarySample> samples)
    {
        var observed = samples.Select(static sample => sample.Scenario).ToHashSet(StringComparer.Ordinal);
        foreach (string scenario in RequiredBoundaryScenarios)
        {
            if (observed.Add(scenario))
            {
                samples.Add(new M27LocalOnnxBoundarySample(
                    scenario,
                    "NOT_RUN",
                    "The boundary scenario must execute against the selected evidence configuration.",
                    "not-run",
                    0,
                    0,
                    null,
                    "Setup or target-model execution failed before this boundary could run."));
            }
        }
    }

    private static string HashVector(float[] vector)
    {
        var bytes = new byte[checked(vector.Length * sizeof(float))];
        for (int index = 0; index < vector.Length; index++)
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), vector[index]);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string HashVectors(IReadOnlyList<float[]> vectors)
    {
        string digestManifest = string.Join(':', vectors.Select(HashVector));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestManifest))).ToLowerInvariant();
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length)
            throw new InvalidDataException("Model returned inconsistent embedding dimensions.");
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (int index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        if (leftNorm <= 0 || rightNorm <= 0)
            return 0;
        return dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private static M27LocalOnnxArtifactEvidence ReadArtifact(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new M27LocalOnnxArtifactEvidence(path, false, 0, null);
        }
        if (!File.Exists(fullPath))
            return new M27LocalOnnxArtifactEvidence(fullPath, false, 0, null);
        var info = new FileInfo(fullPath);
        return new M27LocalOnnxArtifactEvidence(fullPath, true, info.Length, HashFile(fullPath));
    }

    private static void RequireArtifact(
        M27LocalOnnxArtifactEvidence artifact,
        string name,
        ICollection<string> failures)
    {
        if (!artifact.Exists)
            failures.Add($"Required {name} artifact is missing: {artifact.Path}");
        else if (artifact.Length <= 0 || string.IsNullOrWhiteSpace(artifact.Sha256))
            failures.Add($"Required {name} artifact is empty or unhashed: {artifact.Path}");
    }

    private static CopilotEmbeddingModelProfile? TryReadProfile(
        M27LocalOnnxArtifactEvidence artifact,
        ICollection<string> failures)
    {
        if (!artifact.Exists)
            return null;
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllBytes(artifact.Path),
                M27LocalOnnxEvidenceJsonContext.Default.CopilotEmbeddingModelProfile)
                ?? throw new InvalidDataException("Profile JSON is null.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            failures.Add("Profile fixture is invalid: " + exception.Message);
            return null;
        }
    }

    private static M27LocalOnnxCorpus? TryReadCorpus(
        M27LocalOnnxArtifactEvidence artifact,
        ICollection<string> failures)
    {
        if (!artifact.Exists)
            return null;
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllBytes(artifact.Path),
                M27LocalOnnxEvidenceJsonContext.Default.M27LocalOnnxCorpus)
                ?? throw new InvalidDataException("Corpus JSON is null.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            failures.Add("Corpus fixture is invalid: " + exception.Message);
            return null;
        }
    }

    internal static void ValidateCorpus(M27LocalOnnxCorpus? corpus, ICollection<string> failures)
    {
        if (corpus is null)
        {
            failures.Add("Corpus JSON must contain an object.");
            return;
        }
        if (!string.Equals(corpus.Schema, CorpusSchema, StringComparison.Ordinal))
            failures.Add($"Corpus schema must be {CorpusSchema}.");
        if (string.IsNullOrWhiteSpace(corpus.Name))
            failures.Add("Corpus name is required.");
        if (corpus.Documents is null)
            failures.Add("Corpus documents collection is required.");
        if (corpus.Queries is null)
            failures.Add("Corpus queries collection is required.");
        if (corpus.Documents is null || corpus.Queries is null)
            return;
        if (corpus.Documents.Length == 0 || corpus.Queries.Length == 0)
            failures.Add("Corpus requires at least one document and one query.");
        if (corpus.K <= 0 || corpus.K > corpus.Documents.Length)
            failures.Add("Corpus K must be within the document count.");
        if (!double.IsFinite(corpus.MinimumRecallAtK) || corpus.MinimumRecallAtK is < 0 or > 1)
            failures.Add("Corpus minimumRecallAtK must be between zero and one.");
        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (M27LocalOnnxCorpusDocument? document in corpus.Documents)
        {
            if (document is null)
            {
                failures.Add("Corpus documents cannot contain null entries.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(document.Id) || string.IsNullOrWhiteSpace(document.Text)
                || !documentIds.Add(document.Id))
                failures.Add("Corpus document ids/text must be non-empty and ids must be unique.");
        }
        var queryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (M27LocalOnnxCorpusQuery? query in corpus.Queries)
        {
            if (query is null)
            {
                failures.Add("Corpus queries cannot contain null entries.");
                continue;
            }
            if (query.RelevantDocumentIds is null)
            {
                failures.Add($"Corpus query '{query.Id}' requires a relevantDocumentIds collection.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(query.Id) || string.IsNullOrWhiteSpace(query.Text)
                || !queryIds.Add(query.Id) || query.RelevantDocumentIds.Length == 0
                || query.RelevantDocumentIds.Distinct(StringComparer.Ordinal).Count() != query.RelevantDocumentIds.Length
                || query.RelevantDocumentIds.Any(id => string.IsNullOrWhiteSpace(id) || !documentIds.Contains(id)))
            {
                failures.Add("Corpus queries require unique ids, text, and unique relevant document ids from the corpus.");
            }
        }
    }

    private static M27LocalOnnxEnvironmentEvidence CaptureEnvironment(
        MemorySampler memory,
        M27LocalOnnxEvidenceRunOptions options,
        LocalOnnxExecutionState? executionState)
    {
        string onnxVersion = typeof(LocalOnnxEmbeddingProvider).Assembly
            .GetReferencedAssemblies()
            .FirstOrDefault(static name => string.Equals(name.Name, "Microsoft.ML.OnnxRuntime", StringComparison.Ordinal))
            ?.Version?.ToString() ?? "unknown";
        ThreadPool.GetMinThreads(out int minimumWorkerThreads, out int minimumCompletionPortThreads);
        ThreadPool.GetMaxThreads(out int maximumWorkerThreads, out int maximumCompletionPortThreads);
        return new M27LocalOnnxEnvironmentEvidence(
            options.EnvironmentName,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.RuntimeIdentifier,
            onnxVersion,
            "cpu-default",
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            new M27LocalOnnxThreadEvidence(
                options.IntraOpThreads,
                options.InterOpThreads,
                executionState?.AppliedToSession ?? false,
                executionState?.EffectiveIntraOpThreads,
                executionState?.EffectiveInterOpThreads,
                executionState?.ExecutionMode ?? "not-initialized",
                Environment.GetEnvironmentVariable("OMP_NUM_THREADS"),
                minimumWorkerThreads,
                minimumCompletionPortThreads,
                maximumWorkerThreads,
                maximumCompletionPortThreads),
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            memory.WorkingSetBeforeBytes,
            Environment.WorkingSet,
            memory.PeakWorkingSetBytes,
            memory.ManagedMemoryBeforeBytes,
            GC.GetTotalMemory(forceFullCollection: false),
            memory.PeakManagedMemoryBytes);
    }

    internal static M27LocalOnnxGitEvidence CaptureGitEvidence()
    {
        try
        {
            var headInfo = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            headInfo.ArgumentList.Add("rev-parse");
            headInfo.ArgumentList.Add("HEAD");
            using Process? head = Process.Start(headInfo);
            if (head is null)
                return new M27LocalOnnxGitEvidence("unknown", false);
            string commit = head.StandardOutput.ReadToEnd().Trim().ToLowerInvariant();
            head.WaitForExit();

            var statusInfo = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            statusInfo.ArgumentList.Add("status");
            statusInfo.ArgumentList.Add("--porcelain");
            statusInfo.ArgumentList.Add("--untracked-files=normal");
            using Process? status = Process.Start(statusInfo);
            if (status is null)
                return new M27LocalOnnxGitEvidence(commit, false);
            string changes = status.StandardOutput.ReadToEnd();
            status.WaitForExit();
            return new M27LocalOnnxGitEvidence(
                head.ExitCode == 0 && IsSha256(commit, expectedLength: 40) ? commit : "unknown",
                head.ExitCode == 0 && status.ExitCode == 0 && string.IsNullOrWhiteSpace(changes));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new M27LocalOnnxGitEvidence("unknown", false);
        }
    }

    internal static bool IsSha256(string? value, int expectedLength = 64)
        => value is not null
            && value.Length == expectedLength
            && value.All(static character => char.IsAsciiHexDigit(character));

    private static string[] BuildReplayArguments(M27LocalOnnxEvidenceRunOptions options)
    {
        var arguments = new List<string>
        {
            "--m27-local-onnx-evidence",
            "--model", Path.GetFullPath(options.ModelPath),
            "--tokenizer", Path.GetFullPath(options.TokenizerPath),
            "--profile", Path.GetFullPath(options.ProfilePath),
            "--corpus", Path.GetFullPath(options.CorpusPath),
            "--model-source", options.ModelSource,
            "--model-version", options.ModelVersion,
            "--model-license", options.ModelLicense,
            "--output", Path.GetFullPath(options.OutputDirectory),
            "--environment", options.EnvironmentName,
            "--intra-op-threads", options.IntraOpThreads.ToString(CultureInfo.InvariantCulture),
            "--inter-op-threads", options.InterOpThreads.ToString(CultureInfo.InvariantCulture),
            "--warmup", options.WarmupIterations.ToString(CultureInfo.InvariantCulture),
            "--iterations", options.MeasurementIterations.ToString(CultureInfo.InvariantCulture),
        };
        if (options.TargetModelEvidence)
            arguments.Add("--target-model-evidence");
        return arguments.ToArray();
    }

    private static void WriteReport(string outputDirectory, M27LocalOnnxEvidenceReport report)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            report,
            M27LocalOnnxEvidenceJsonContext.Default.M27LocalOnnxEvidenceReport);
        File.WriteAllBytes(Path.Combine(outputDirectory, ReportFileName), json);
    }

    private sealed class M27LocalOnnxNotReadyException(string message) : Exception(message);

    private sealed class MemorySampler(long workingSetBeforeBytes, long managedMemoryBeforeBytes)
    {
        internal long WorkingSetBeforeBytes { get; } = workingSetBeforeBytes;

        internal long ManagedMemoryBeforeBytes { get; } = managedMemoryBeforeBytes;

        internal long PeakWorkingSetBytes { get; private set; } = workingSetBeforeBytes;

        internal long PeakManagedMemoryBytes { get; private set; } = managedMemoryBeforeBytes;

        internal void Sample()
        {
            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, Environment.WorkingSet);
            PeakManagedMemoryBytes = Math.Max(
                PeakManagedMemoryBytes,
                GC.GetTotalMemory(forceFullCollection: false));
        }
    }
}

internal static class M27LocalOnnxEvidenceVerifier
{
    internal static M27LocalOnnxVerificationResult Verify(string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        var invalid = new List<string>();
        var notReady = new List<string>();
        M27LocalOnnxEvidenceReport? report;
        try
        {
            report = JsonSerializer.Deserialize(
                File.ReadAllBytes(reportPath),
                M27LocalOnnxEvidenceJsonContext.Default.M27LocalOnnxEvidenceReport);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new M27LocalOnnxVerificationResult(
                "INVALID",
                [exception.Message],
                new M27LocalOnnxEvidenceSummary(0, 1, 0, 0, 0, 0));
        }
        if (report is null)
        {
            return new M27LocalOnnxVerificationResult(
                "INVALID",
                ["Report JSON is null."],
                new M27LocalOnnxEvidenceSummary(0, 1, 0, 0, 0, 0));
        }
        if (!string.Equals(report.Schema, "m27-local-onnx-evidence-v2", StringComparison.Ordinal))
        {
            return new M27LocalOnnxVerificationResult(
                "INVALID",
                [$"Unsupported report schema '{report.Schema ?? "<missing>"}'; expected m27-local-onnx-evidence-v2."],
                new M27LocalOnnxEvidenceSummary(0, 1, 0, 0, 0, 0));
        }
        if (!ValidateReportStructure(report, invalid))
        {
            return new M27LocalOnnxVerificationResult(
                "INVALID",
                invalid.Distinct(StringComparer.Ordinal).ToArray(),
                new M27LocalOnnxEvidenceSummary(0, 1, 0, 0, 0, 0));
        }
        if (report.Status is not ("PASS" or "NOT_READY"))
            invalid.Add("Report status must be PASS or NOT_READY.");
        if (report.WarmupIterations <= 0 || report.MeasurementIterations <= 0)
            invalid.Add("Iteration counts must be positive.");
        if (!M27LocalOnnxEvidenceRunner.IsSha256(report.Git.CommitSha, expectedLength: 40))
            notReady.Add("Report is not bound to a 40-hex Git commit.");
        if (!report.Git.WorktreeClean)
            notReady.Add("Evidence worktree was not clean.");
        M27LocalOnnxGitEvidence currentGit = M27LocalOnnxEvidenceRunner.CaptureGitEvidence();
        if (!string.Equals(currentGit.CommitSha, report.Git.CommitSha, StringComparison.Ordinal)
            || !currentGit.WorktreeClean)
            notReady.Add("Current verification worktree is dirty or does not match the evidence commit.");
        if (string.IsNullOrWhiteSpace(report.Provenance.Source)
            || string.IsNullOrWhiteSpace(report.Provenance.Version)
            || string.IsNullOrWhiteSpace(report.Provenance.License))
            notReady.Add("Model source, version, and license provenance are required.");

        CheckArtifact(report.Model, "model", notReady);
        CheckArtifact(report.Tokenizer, "tokenizer", notReady);
        CheckArtifact(report.ProfileArtifact, "profile", notReady);
        CheckArtifact(report.CorpusArtifact, "corpus", notReady);
        if (report.ProviderFallback)
            notReady.Add("Provider fallback: " + (report.FallbackReason ?? "hash fallback reason unavailable."));
        if (!report.ProviderConfigured)
            notReady.Add("Provider did not finish in configured ONNX mode.");
        if (!report.TargetModelEvidence)
            notReady.Add("Run is contract-only and was not declared as target-model evidence.");
        if (!report.TargetModelLoaded)
            notReady.Add("The specified target model was not successfully loaded and executed.");
        VerifyEnvironment(report, invalid, notReady);
        VerifyBoundarySamples(report, invalid, notReady);
        if (report.Profile is null || string.IsNullOrWhiteSpace(report.ProfileSha256))
            notReady.Add("Effective profile echo/hash is missing.");
        else if (!string.Equals(
            M27LocalOnnxEvidenceRunner.HashProfile(report.Profile),
            report.ProfileSha256,
            StringComparison.Ordinal))
            invalid.Add("Effective profile echo hash does not match.");
        CopilotEmbeddingModelProfile? inputProfile = ReadProfile(report.ProfileArtifact, invalid);
        if (inputProfile is not null && report.Profile is not null)
        {
            inputProfile.TokenizerModelPath = report.Tokenizer.Path;
            if (!string.Equals(
                M27LocalOnnxEvidenceRunner.HashProfile(inputProfile),
                report.ProfileSha256,
                StringComparison.Ordinal))
                invalid.Add("Effective profile echo does not match the hashed profile fixture and explicit tokenizer path.");
        }

        M27LocalOnnxCorpus? corpus = ReadCorpus(report.CorpusArtifact, notReady, invalid);
        if (corpus is not null)
        {
            var corpusFailures = new List<string>();
            M27LocalOnnxEvidenceRunner.ValidateCorpus(corpus, corpusFailures);
            invalid.AddRange(corpusFailures);
            if (corpusFailures.Count == 0)
            {
                if (report.Corpus is null
                    || report.Corpus.Schema != corpus.Schema
                    || report.Corpus.Name != corpus.Name
                    || report.Corpus.K != corpus.K
                    || !NearlyEqual(report.Corpus.MinimumRecallAtK, corpus.MinimumRecallAtK)
                    || report.Corpus.DocumentCount != corpus.Documents.Length
                    || report.Corpus.QueryCount != corpus.Queries.Length)
                    invalid.Add("Corpus echo does not match the hashed corpus fixture.");
                VerifySamples(report, corpus, invalid, notReady);
            }
        }
        else if (report.RawSamples.Length != 0)
        {
            invalid.Add("Raw samples cannot be verified without the corpus fixture.");
        }

        M27LocalOnnxEvidenceSummary recomputed = M27LocalOnnxEvidenceRunner.Summarize(
            report.RawSamples,
            report.Failures.Length);
        if (report.Summary.SampleCount != recomputed.SampleCount
            || report.Summary.FailureCount != recomputed.FailureCount
            || !NearlyEqual(report.Summary.RecallAtK, recomputed.RecallAtK)
            || !NearlyEqual(report.Summary.P50Milliseconds, recomputed.P50Milliseconds)
            || !NearlyEqual(report.Summary.P95Milliseconds, recomputed.P95Milliseconds)
            || !NearlyEqual(report.Summary.P99Milliseconds, recomputed.P99Milliseconds))
            invalid.Add("Summary does not match raw query samples and failures.");
        if (corpus is not null && recomputed.RecallAtK < corpus.MinimumRecallAtK)
            notReady.Add($"Recomputed Recall@{corpus.K} is below the corpus threshold.");
        if (report.RawSamples.Length == 0)
            notReady.Add("No raw measured query samples are present.");
        if (report.RawSamples.Any(static sample => !sample.Success))
            notReady.Add("One or more measured query samples failed.");
        if (report.Failures.Length != 0)
            notReady.AddRange(report.Failures);
        if (!string.Equals(report.Status, "PASS", StringComparison.Ordinal))
            notReady.Add("Runner report status is not PASS.");

        string status = invalid.Count != 0 ? "INVALID" : notReady.Count != 0 ? "NOT_READY" : "PASS";
        return new M27LocalOnnxVerificationResult(
            status,
            invalid.Concat(notReady).Distinct(StringComparer.Ordinal).ToArray(),
            recomputed);
    }

    private static void VerifySamples(
        M27LocalOnnxEvidenceReport report,
        M27LocalOnnxCorpus corpus,
        ICollection<string> invalid,
        ICollection<string> notReady)
    {
        var queries = corpus.Queries.ToDictionary(static query => query.Id, StringComparer.Ordinal);
        var documents = corpus.Documents.Select(static document => document.Id).ToHashSet(StringComparer.Ordinal);
        var keys = new HashSet<(string QueryId, int Iteration)>();
        foreach (M27LocalOnnxQuerySample sample in report.RawSamples)
        {
            if (!queries.TryGetValue(sample.QueryId, out M27LocalOnnxCorpusQuery? query))
            {
                invalid.Add($"Unknown raw query id '{sample.QueryId}'.");
                continue;
            }
            if (sample.Iteration <= 0 || sample.Iteration > report.MeasurementIterations
                || !keys.Add((sample.QueryId, sample.Iteration)))
                invalid.Add($"Raw sample iteration is invalid or duplicated for '{sample.QueryId}'.");
            if (!double.IsFinite(sample.ElapsedMilliseconds) || sample.ElapsedMilliseconds < 0)
                invalid.Add($"Raw latency is invalid for '{sample.QueryId}'.");
            if (!sample.Success)
            {
                if (string.IsNullOrWhiteSpace(sample.FailureReason))
                    invalid.Add($"Failed sample '{sample.QueryId}' lacks failureReason.");
                continue;
            }
            if (report.Profile is not null && sample.VectorDimension != report.Profile.Dimensions)
                invalid.Add($"Vector dimension mismatch for '{sample.QueryId}'.");
            if (!M27LocalOnnxEvidenceRunner.IsSha256(sample.VectorSha256))
                invalid.Add($"Vector digest is invalid for '{sample.QueryId}'.");
            if (sample.Candidates.Length > corpus.K
                || sample.Candidates.Select(static candidate => candidate.DocumentId).Distinct(StringComparer.Ordinal).Count() != sample.Candidates.Length
                || sample.Candidates.Any(candidate => !documents.Contains(candidate.DocumentId)
                    || !double.IsFinite(candidate.Similarity)))
                invalid.Add($"Candidate list is invalid for '{sample.QueryId}'.");
            M27LocalOnnxCandidateSample[] ordered = sample.Candidates
                .OrderByDescending(static candidate => candidate.Similarity)
                .ThenBy(static candidate => candidate.DocumentId, StringComparer.Ordinal)
                .ToArray();
            if (!ordered.SequenceEqual(sample.Candidates))
                invalid.Add($"Candidate list is not deterministically ranked for '{sample.QueryId}'.");
            double recall = M27LocalOnnxEvidenceRunner.Recall(
                query.RelevantDocumentIds,
                sample.Candidates.Select(static candidate => candidate.DocumentId));
            if (!NearlyEqual(sample.RecallAtK, recall))
                invalid.Add($"Recall@K does not match raw candidates for '{sample.QueryId}'.");
        }

        int expected = checked(corpus.Queries.Length * report.MeasurementIterations);
        if (report.Status == "PASS" && report.RawSamples.Length != expected)
            invalid.Add($"PASS report requires {expected} raw samples.");
        else if (report.RawSamples.Length != expected)
            notReady.Add($"Expected {expected} raw samples but found {report.RawSamples.Length}.");
    }

    private static void VerifyEnvironment(
        M27LocalOnnxEvidenceReport report,
        ICollection<string> invalid,
        ICollection<string> notReady)
    {
        M27LocalOnnxEnvironmentEvidence environment = report.Environment;
        if (string.IsNullOrWhiteSpace(environment.Name)
            || string.IsNullOrWhiteSpace(environment.Framework)
            || string.IsNullOrWhiteSpace(environment.Os)
            || string.IsNullOrWhiteSpace(environment.Architecture)
            || string.IsNullOrWhiteSpace(environment.RuntimeIdentifier)
            || string.IsNullOrWhiteSpace(environment.OnnxRuntimeVersion)
            || string.IsNullOrWhiteSpace(environment.ExecutionProvider))
        {
            invalid.Add("Environment identity/runtime fields are required.");
        }
        if (environment.ProcessorCount <= 0)
            invalid.Add("Environment processor count must be positive.");

        M27LocalOnnxThreadEvidence threads = environment.Threads;
        if (threads.RequestedIntraOpThreads < 0 || threads.RequestedInterOpThreads < 0)
            invalid.Add("Requested ONNX thread counts cannot be negative.");
        if (!threads.AppliedToSession)
        {
            if (threads.EffectiveIntraOpThreads is not null
                || threads.EffectiveInterOpThreads is not null
                || !string.Equals(threads.ExecutionMode, "not-initialized", StringComparison.Ordinal))
            {
                invalid.Add("Thread evidence cannot claim applied/effective settings without an initialized ONNX session.");
            }
            notReady.Add("ONNX Runtime thread settings were not applied to an initialized target-model session.");
        }
        else
        {
            if (!report.ProviderConfigured || report.ProviderFallback || !report.TargetModelLoaded)
                invalid.Add("Thread evidence cannot claim applied/effective settings when the target provider session was not loaded.");
            int? expectedIntraOp = threads.RequestedIntraOpThreads > 0
                ? threads.RequestedIntraOpThreads
                : null;
            int? expectedInterOp = threads.RequestedInterOpThreads > 0
                ? threads.RequestedInterOpThreads
                : null;
            string expectedMode = threads.RequestedInterOpThreads > 0 ? "parallel" : "sequential";
            if (threads.EffectiveIntraOpThreads != expectedIntraOp
                || threads.EffectiveInterOpThreads != expectedInterOp
                || !string.Equals(threads.ExecutionMode, expectedMode, StringComparison.Ordinal))
            {
                invalid.Add("Applied ONNX thread evidence does not match the requested SessionOptions values and execution mode.");
            }
        }
        if (threads.ManagedMinimumWorkerThreads <= 0
            || threads.ManagedMinimumCompletionPortThreads <= 0
            || threads.ManagedMaximumWorkerThreads < threads.ManagedMinimumWorkerThreads
            || threads.ManagedMaximumCompletionPortThreads < threads.ManagedMinimumCompletionPortThreads)
        {
            invalid.Add("Managed thread-pool evidence is invalid.");
        }
        if (!HasReplayOption(report.ReplayArguments, "--environment", environment.Name)
            || !HasReplayOption(
                report.ReplayArguments,
                "--intra-op-threads",
                threads.RequestedIntraOpThreads.ToString(CultureInfo.InvariantCulture))
            || !HasReplayOption(
                report.ReplayArguments,
                "--inter-op-threads",
                threads.RequestedInterOpThreads.ToString(CultureInfo.InvariantCulture)))
        {
            invalid.Add("Replay arguments do not match the recorded environment/thread configuration.");
        }
        if (report.ReplayArguments.Contains("--target-model-evidence", StringComparer.Ordinal) != report.TargetModelEvidence)
            invalid.Add("Replay target-model evidence flag does not match the report scope.");
    }

    private static void VerifyBoundarySamples(
        M27LocalOnnxEvidenceReport report,
        ICollection<string> invalid,
        ICollection<string> notReady)
    {
        var samples = new Dictionary<string, M27LocalOnnxBoundarySample>(StringComparer.Ordinal);
        foreach (M27LocalOnnxBoundarySample sample in report.BoundarySamples)
        {
            if (!samples.TryAdd(sample.Scenario, sample))
                invalid.Add($"Boundary scenario '{sample.Scenario}' is duplicated.");
            if (!M27LocalOnnxEvidenceRunner.BoundaryScenarios.Contains(sample.Scenario, StringComparer.Ordinal))
                invalid.Add($"Unknown boundary scenario '{sample.Scenario}'.");
            if (sample.Status is not ("PASS" or "FAILED" or "NOT_RUN" or "NOT_SUPPORTED"))
                invalid.Add($"Boundary scenario '{sample.Scenario}' has an invalid status.");
            if (string.IsNullOrWhiteSpace(sample.ExpectedOutcome)
                || string.IsNullOrWhiteSpace(sample.ObservedOutcome)
                || string.IsNullOrWhiteSpace(sample.Detail)
                || sample.InputCharacterCount < 0
                || sample.VectorDimension < 0)
            {
                invalid.Add($"Boundary scenario '{sample.Scenario}' has incomplete raw evidence.");
            }
        }

        foreach (string scenario in M27LocalOnnxEvidenceRunner.BoundaryScenarios)
        {
            if (!samples.TryGetValue(scenario, out M27LocalOnnxBoundarySample? sample))
            {
                invalid.Add($"Required boundary scenario '{scenario}' is missing.");
                continue;
            }
            if (sample.Status != "PASS")
                notReady.Add($"Boundary scenario '{scenario}' is {sample.Status}.");
        }

        if (samples.TryGetValue("batch-input", out M27LocalOnnxBoundarySample? batch))
        {
            if (batch.Status == "PASS"
                && (batch.ObservedOutcome != "batched-embedding"
                    || report.Profile is null
                    || batch.VectorDimension != report.Profile.Dimensions
                    || batch.InputCharacterCount <= 0
                    || !M27LocalOnnxEvidenceRunner.IsSha256(batch.VectorSha256)))
            {
                invalid.Add("Batch-input PASS lacks a real batched target-model result digest.");
            }
        }
        if (samples.TryGetValue("blank-input", out M27LocalOnnxBoundarySample? blank)
            && blank.Status == "PASS"
            && (blank.ObservedOutcome != "argument-rejected"
                || blank.VectorDimension != 0
                || blank.VectorSha256 is not null))
        {
            invalid.Add("Blank-input PASS must contain the raw argument rejection without a vector.");
        }
        if (samples.TryGetValue("malformed-model", out M27LocalOnnxBoundarySample? malformed)
            && malformed.Status == "PASS")
        {
            bool observableFallback = malformed.ObservedOutcome == "observable-fallback"
                && malformed.VectorDimension == BuiltinHashEmbeddingProvider.VectorDimension
                && M27LocalOnnxEvidenceRunner.IsSha256(malformed.VectorSha256);
            bool failClosed = malformed.ObservedOutcome == "fail-closed-rejection"
                && malformed.VectorDimension == 0
                && malformed.VectorSha256 is null;
            if (!observableFallback && !failClosed)
                invalid.Add("Malformed-model PASS must contain an observable fallback or fail-closed rejection.");
        }

        foreach (string scenario in new[] { "unicode-cjk-input", "overlong-input" })
        {
            if (samples.TryGetValue(scenario, out M27LocalOnnxBoundarySample? sample)
                && sample.Status == "PASS"
                && (sample.ObservedOutcome != "embedding"
                    || report.Profile is null
                    || sample.VectorDimension != report.Profile.Dimensions
                    || !M27LocalOnnxEvidenceRunner.IsSha256(sample.VectorSha256)))
            {
                invalid.Add($"Boundary scenario '{scenario}' PASS lacks a target-model vector digest.");
            }
        }

        if (samples.TryGetValue("attention-mask-padding", out M27LocalOnnxBoundarySample? mask)
            && mask.Status == "PASS")
        {
            bool explicitlyDisabled = mask.ObservedOutcome == "profile-explicitly-disabled"
                && report.Profile?.SendAttentionMask == false
                && mask.VectorDimension == 0
                && mask.VectorSha256 is null;
            bool embedded = mask.ObservedOutcome == "embedding"
                && report.Profile?.SendAttentionMask == true
                && !string.IsNullOrWhiteSpace(report.Profile.AttentionMaskName)
                && mask.VectorDimension == report.Profile.Dimensions
                && M27LocalOnnxEvidenceRunner.IsSha256(mask.VectorSha256);
            if (!explicitlyDisabled && !embedded)
                invalid.Add("Attention-mask boundary PASS does not match the effective profile and raw result.");
        }
    }

    private static bool HasReplayOption(IReadOnlyList<string> arguments, string option, string value)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal)
                && string.Equals(arguments[index + 1], value, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool ValidateReportStructure(
        M27LocalOnnxEvidenceReport report,
        ICollection<string> invalid)
    {
        if (string.IsNullOrWhiteSpace(report.Schema))
            invalid.Add("Report schema is required.");
        if (string.IsNullOrWhiteSpace(report.Issue))
            invalid.Add("Report issue is required.");
        if (string.IsNullOrWhiteSpace(report.Status))
            invalid.Add("Report status is required.");
        if (report.Git is null)
            invalid.Add("Report git evidence is required.");
        if (report.Model is null)
            invalid.Add("Report model artifact evidence is required.");
        if (report.Tokenizer is null)
            invalid.Add("Report tokenizer artifact evidence is required.");
        if (report.ProfileArtifact is null)
            invalid.Add("Report profile artifact evidence is required.");
        if (report.CorpusArtifact is null)
            invalid.Add("Report corpus artifact evidence is required.");
        if (report.Provenance is null)
            invalid.Add("Report model provenance is required.");
        if (report.Environment is null)
            invalid.Add("Report environment evidence is required.");
        else if (report.Environment.Threads is null)
            invalid.Add("Report ONNX thread evidence is required.");
        if (report.Summary is null)
            invalid.Add("Report summary is required.");
        if (report.BoundarySamples is null)
            invalid.Add("Report boundarySamples collection is required.");
        else if (report.BoundarySamples.Any(static sample => sample is null))
            invalid.Add("Report boundarySamples cannot contain null entries.");
        if (report.RawSamples is null)
            invalid.Add("Report rawSamples collection is required.");
        else
        {
            foreach (M27LocalOnnxQuerySample? sample in report.RawSamples)
            {
                if (sample is null)
                {
                    invalid.Add("Report rawSamples cannot contain null entries.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(sample.QueryId))
                    invalid.Add("Every raw sample requires a queryId.");
                if (sample.Candidates is null)
                    invalid.Add($"Raw sample '{sample.QueryId}' requires a candidates collection.");
                else if (sample.Candidates.Any(static candidate => candidate is null
                    || string.IsNullOrWhiteSpace(candidate.DocumentId)))
                    invalid.Add($"Raw sample '{sample.QueryId}' contains an invalid candidate.");
            }
        }
        if (report.Failures is null)
            invalid.Add("Report failures collection is required.");
        else if (report.Failures.Any(static failure => failure is null))
            invalid.Add("Report failures cannot contain null entries.");
        if (report.ReplayArguments is null || report.ReplayArguments.Length == 0)
            invalid.Add("Report replayArguments collection is required.");
        else if (report.ReplayArguments.Any(string.IsNullOrWhiteSpace))
            invalid.Add("Report replayArguments cannot contain empty values.");

        ValidateArtifactStructure(report.Model, "model", invalid);
        ValidateArtifactStructure(report.Tokenizer, "tokenizer", invalid);
        ValidateArtifactStructure(report.ProfileArtifact, "profile", invalid);
        ValidateArtifactStructure(report.CorpusArtifact, "corpus", invalid);
        return invalid.Count == 0;
    }

    private static void ValidateArtifactStructure(
        M27LocalOnnxArtifactEvidence? artifact,
        string name,
        ICollection<string> invalid)
    {
        if (artifact is not null && string.IsNullOrWhiteSpace(artifact.Path))
            invalid.Add($"Report {name} artifact path is required.");
    }

    private static M27LocalOnnxCorpus? ReadCorpus(
        M27LocalOnnxArtifactEvidence artifact,
        ICollection<string> notReady,
        ICollection<string> invalid)
    {
        if (!artifact.Exists || !File.Exists(artifact.Path))
            return null;
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllBytes(artifact.Path),
                M27LocalOnnxEvidenceJsonContext.Default.M27LocalOnnxCorpus)
                ?? throw new InvalidDataException("Corpus fixture JSON is null.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            invalid.Add("Corpus fixture cannot be parsed: " + exception.Message);
            return null;
        }
    }

    private static CopilotEmbeddingModelProfile? ReadProfile(
        M27LocalOnnxArtifactEvidence artifact,
        ICollection<string> invalid)
    {
        if (!artifact.Exists || !File.Exists(artifact.Path))
            return null;
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllBytes(artifact.Path),
                M27LocalOnnxEvidenceJsonContext.Default.CopilotEmbeddingModelProfile)
                ?? throw new InvalidDataException("Profile fixture JSON is null.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            invalid.Add("Profile fixture cannot be parsed: " + exception.Message);
            return null;
        }
    }

    private static void CheckArtifact(
        M27LocalOnnxArtifactEvidence artifact,
        string name,
        ICollection<string> notReady)
    {
        if (!artifact.Exists || !File.Exists(artifact.Path))
        {
            notReady.Add($"{name} artifact is missing.");
            return;
        }
        var info = new FileInfo(artifact.Path);
        if (info.Length != artifact.Length
            || !M27LocalOnnxEvidenceRunner.IsSha256(artifact.Sha256)
            || !string.Equals(M27LocalOnnxEvidenceRunner.HashFile(artifact.Path), artifact.Sha256, StringComparison.Ordinal))
            notReady.Add($"{name} artifact length or SHA-256 no longer matches.");
    }

    private static bool NearlyEqual(double left, double right)
        => double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= 1e-9;
}

internal sealed record M27LocalOnnxEvidenceReport(
    string Schema,
    string Issue,
    string Status,
    M27LocalOnnxGitEvidence Git,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    int WarmupIterations,
    int MeasurementIterations,
    M27LocalOnnxArtifactEvidence Model,
    M27LocalOnnxArtifactEvidence Tokenizer,
    M27LocalOnnxArtifactEvidence ProfileArtifact,
    M27LocalOnnxArtifactEvidence CorpusArtifact,
    M27LocalOnnxModelProvenance Provenance,
    CopilotEmbeddingModelProfile? Profile,
    string? ProfileSha256,
    M27LocalOnnxCorpusEvidence? Corpus,
    bool ProviderConfigured,
    bool ProviderFallback,
    string? FallbackReason,
    bool TargetModelLoaded,
    bool TargetModelEvidence,
    M27LocalOnnxEnvironmentEvidence Environment,
    M27LocalOnnxBoundarySample[] BoundarySamples,
    M27LocalOnnxQuerySample[] RawSamples,
    string[] Failures,
    M27LocalOnnxEvidenceSummary Summary,
    string[] ReplayArguments);

internal sealed record M27LocalOnnxArtifactEvidence(string Path, bool Exists, long Length, string? Sha256);

internal sealed record M27LocalOnnxGitEvidence(string CommitSha, bool WorktreeClean);

internal sealed record M27LocalOnnxModelProvenance(string Source, string Version, string License);

internal sealed record M27LocalOnnxEnvironmentEvidence(
    string Name,
    string Framework,
    string Os,
    string Architecture,
    string RuntimeIdentifier,
    string OnnxRuntimeVersion,
    string ExecutionProvider,
    string ProcessorIdentifier,
    int ProcessorCount,
    bool ServerGc,
    M27LocalOnnxThreadEvidence Threads,
    long AvailableMemoryBytes,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long PeakWorkingSetBytes,
    long ManagedMemoryBeforeBytes,
    long ManagedMemoryAfterBytes,
    long PeakManagedMemoryBytes);

internal sealed record M27LocalOnnxThreadEvidence(
    int RequestedIntraOpThreads,
    int RequestedInterOpThreads,
    bool AppliedToSession,
    int? EffectiveIntraOpThreads,
    int? EffectiveInterOpThreads,
    string ExecutionMode,
    string? OmpNumThreads,
    int ManagedMinimumWorkerThreads,
    int ManagedMinimumCompletionPortThreads,
    int ManagedMaximumWorkerThreads,
    int ManagedMaximumCompletionPortThreads);

internal sealed record M27LocalOnnxBoundarySample(
    string Scenario,
    string Status,
    string ExpectedOutcome,
    string ObservedOutcome,
    int InputCharacterCount,
    int VectorDimension,
    string? VectorSha256,
    string? Detail);

internal sealed record M27LocalOnnxCorpusEvidence(
    string Schema,
    string Name,
    int K,
    double MinimumRecallAtK,
    int DocumentCount,
    int QueryCount);

internal sealed record M27LocalOnnxQuerySample(
    string QueryId,
    int Iteration,
    bool Success,
    double ElapsedMilliseconds,
    int VectorDimension,
    string? VectorSha256,
    M27LocalOnnxCandidateSample[] Candidates,
    double RecallAtK,
    string? FailureReason);

internal sealed record M27LocalOnnxCandidateSample(string DocumentId, double Similarity);

internal sealed record M27LocalOnnxEvidenceSummary(
    int SampleCount,
    int FailureCount,
    double RecallAtK,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds);

internal sealed record M27LocalOnnxVerificationResult(
    string Status,
    string[] Findings,
    M27LocalOnnxEvidenceSummary RecomputedSummary);

internal sealed record M27LocalOnnxCorpus(
    string Schema,
    string Name,
    int K,
    double MinimumRecallAtK,
    M27LocalOnnxCorpusDocument[] Documents,
    M27LocalOnnxCorpusQuery[] Queries);

internal sealed record M27LocalOnnxCorpusDocument(string Id, string Text);

internal sealed record M27LocalOnnxCorpusQuery(
    string Id,
    string Text,
    string[] RelevantDocumentIds);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(CopilotEmbeddingModelProfile))]
[JsonSerializable(typeof(M27LocalOnnxCorpus))]
[JsonSerializable(typeof(M27LocalOnnxEvidenceReport))]
internal sealed partial class M27LocalOnnxEvidenceJsonContext : JsonSerializerContext;
