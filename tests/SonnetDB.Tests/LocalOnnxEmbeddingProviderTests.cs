using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.ML.Tokenizers;
using SonnetDB.Configuration;
using SonnetDB.Copilot;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// Local ONNX provider contract tests. The generated graph is deliberately tiny and
/// deterministic; it validates wiring and tensor semantics, not model quality.
/// </summary>
public sealed class LocalOnnxEmbeddingProviderTests
{
    [Fact]
    public async Task EmbedAsync_WithBertProfile_ExecutesOnnxAndAddsSpecialTokens()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(CreateOptions(fixture, pooling: "mean", maxTokens: 6));

        Assert.False(provider.IsFallback);
        Assert.True(provider.IsConfigured);
        Assert.Equal(1, provider.VectorDimension);
        Assert.Null(provider.FallbackReason);

        // [CLS]=2, hello=4, world=5, [SEP]=3; the right padding is masked.
        var embedding = await provider.EmbedAsync("HELLO WORLD");

        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
        Assert.True(provider.IsConfigured);
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.499f, 3.501f);
    }

    [Fact]
    public async Task EmbedAsync_WithWhitespaceBertSpecialTokens_UsesDefaults()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.UnknownToken = "   ";
        options.ModelProfile.ClassificationToken = "\t";
        options.ModelProfile.SeparatorToken = " ";
        options.ModelProfile.PaddingToken = "\r\n";
        options.ModelProfile.MaskingToken = "  ";
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello world");

        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.499f, 3.501f);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedAsync_WithMissingBertSpecialToken_RejectsTokenizerProfile()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.ClassificationToken = "[MISSING]";
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("tokenizer profile", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MISSING", exception.Message, StringComparison.Ordinal);
        Assert.False(provider.IsFallback);
    }

    [Fact]
    public async Task EmbedAsync_SnapshotsProfileBeforeExternalMutation()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        using var provider = new LocalOnnxEmbeddingProvider(options);

        options.LocalModelPath = fixture.ModelPath + ".missing";
        options.ModelProfile!.Pooling = "cls";
        options.ModelProfile.OutputName = "missing_output";
        options.ModelProfile.IgnoredInputNames.Add(fixture.AttentionMaskName);

        var first = await provider.EmbedAsync("hello world");
        Assert.InRange(first[0], 3.499f, 3.501f);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");

        // The inspection property is also a copy, so mutating it cannot alter the
        // already validated execution plan or the provider's tokenizer contract.
        var exposedProfile = provider.ModelProfile!;
        exposedProfile.Pooling = "cls";
        var second = await provider.EmbedAsync("hello world");
        Assert.InRange(second[0], 3.499f, 3.501f);
    }

    [Fact]
    public async Task EmbedAsync_WithSentencePieceProfile_UsesConfiguredEndOfSentence()
    {
        using var fixture = TinyOnnxFixture.CreateSentencePiece();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 2);
        options.ModelProfile!.TokenizerType = "sentencepiece";
        options.ModelProfile.AddSpecialTokens = false;
        options.ModelProfile.AddBeginningOfSentence = false;
        options.ModelProfile.AddEndOfSentence = true;

        using (var modelStream = File.OpenRead(fixture.VocabPath))
        {
            var tokenizer = SentencePieceTokenizer.Create(
                modelStream,
                addBeginningOfSentence: false,
                addEndOfSentence: true,
                specialTokens: new Dictionary<string, int>());
            var ids = tokenizer.EncodeToIds("hello", addBeginningOfSentence: false, addEndOfSentence: true);
            Assert.Equal([4, 2], ids);
        }

        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello");

        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
        // hello=4 and </s>=2 are the two rows returned by the tiny model.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 2.999f, 3.001f);
    }

    [Fact]
    public async Task EmbedAsync_WithBertSpecialTokensWithoutContentSlot_RejectsProfile()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 2));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("at least 3", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content token", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(provider.IsFallback);
    }

    [Fact]
    public async Task EmbedAsync_WithBertMinimumSpecialTokenBudget_RetainsContentToken()
    {
        using var fixture = TinyOnnxFixture.Create(fixedSequenceLength: 3);
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 3));

        var embedding = await provider.EmbedAsync("hello");

        // The minimum valid BERT budget is [CLS], one content token and [SEP].
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 2.999f, 3.001f);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedAsync_WithSentencePieceEndTokenWithoutContentSlot_RejectsProfile()
    {
        using var fixture = TinyOnnxFixture.CreateSentencePiece(fixedSequenceLength: 1);
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 1);
        options.ModelProfile!.TokenizerType = "sentencepiece";
        options.ModelProfile.AddSpecialTokens = false;
        options.ModelProfile.AddBeginningOfSentence = false;
        options.ModelProfile.AddEndOfSentence = true;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("at least 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content token", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(provider.IsFallback);
    }

    [Fact]
    public async Task EmbedAsync_WithSentencePieceMinimumEndTokenBudget_RetainsContentToken()
    {
        using var fixture = TinyOnnxFixture.CreateSentencePiece(fixedSequenceLength: 2);
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 2);
        options.ModelProfile!.TokenizerType = "sentencepiece";
        options.ModelProfile.AddSpecialTokens = false;
        options.ModelProfile.AddBeginningOfSentence = false;
        options.ModelProfile.AddEndOfSentence = true;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello");

        // The minimum valid EOS budget is one content token followed by </s>.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 2.999f, 3.001f);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedAsync_WithSentencePieceBosAndEosWithoutContentSlot_RejectsProfile()
    {
        using var fixture = TinyOnnxFixture.CreateSentencePiece(fixedSequenceLength: 2);
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 2);
        options.ModelProfile!.TokenizerType = "sentencepiece";
        options.ModelProfile.AddSpecialTokens = false;
        options.ModelProfile.AddBeginningOfSentence = true;
        options.ModelProfile.AddEndOfSentence = true;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("at least 3", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content token", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(provider.IsFallback);
    }

    [Fact]
    public async Task EmbedAsync_WithSentencePieceMinimumBosAndEosBudget_RetainsContentToken()
    {
        using var fixture = TinyOnnxFixture.CreateSentencePiece(fixedSequenceLength: 3);
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 3);
        options.ModelProfile!.TokenizerType = "sentencepiece";
        options.ModelProfile.AddSpecialTokens = false;
        options.ModelProfile.AddBeginningOfSentence = true;
        options.ModelProfile.AddEndOfSentence = true;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello");

        // The minimum valid BOS/EOS budget is <s>, one content token and </s>.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 2.332f, 2.335f);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedAsync_SentencePieceWithoutConfiguredPadId_UsesVocabularyPadToken()
    {
        using var fixture = TinyOnnxFixture.CreateSentencePiece(
            fixedSequenceLength: 4,
            flattenSequenceOutput: true,
            includeAttentionMask: false);
        var options = CreateOptions(fixture, pooling: "auto", maxTokens: 4, dimensions: 4);
        options.ModelProfile!.TokenizerType = "sentencepiece";
        options.ModelProfile.AddSpecialTokens = false;
        options.ModelProfile.AddBeginningOfSentence = false;
        options.ModelProfile.AddEndOfSentence = true;
        options.ModelProfile.AttentionMaskName = null;
        options.ModelProfile.PadTokenId = null;
        options.ModelProfile.Normalize = false;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello");

        // The fixture declares <pad> = 3. The pooled [1, 4] output exposes all
        // input slots so a fallback to zero would be observable in the last two.
        Assert.Equal([4f, 2f, 3f, 3f], embedding);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedAsync_MeanPooling_IgnoresRightPadding()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var shortProvider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 4));
        using var paddedProvider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 8));

        var shortEmbedding = await shortProvider.EmbedAsync("hello world");
        var paddedEmbedding = await paddedProvider.EmbedAsync("hello world");

        Assert.Equal(shortEmbedding.Length, paddedEmbedding.Length);
        Assert.InRange(Math.Abs(shortEmbedding[0] - paddedEmbedding[0]), 0f, 0.0001f);
        Assert.InRange(paddedEmbedding[0], 3.499f, 3.501f);
    }

    [Fact]
    public async Task EmbedAsync_ClsPooling_UsesFirstToken()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "cls", maxTokens: 8));

        var embedding = await provider.EmbedAsync("hello world");

        Assert.Single(embedding);
        Assert.InRange(embedding[0], 1.999f, 2.001f);
    }

    [Fact]
    public async Task EmbedAsync_WithLeftPadding_ClsPoolingUsesFirstUnmaskedToken()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "cls", maxTokens: 6);
        options.ModelProfile!.PaddingSide = "left";
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello world");

        // Left padding places [CLS] at row 2. CLS pooling must follow the first
        // attention-mask row instead of blindly selecting the padded row 0.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 1.999f, 2.001f);
    }

    [Fact]
    public async Task EmbedAsync_ClsPooling_DoesNotExcludeClassificationToken()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "cls", maxTokens: 6);
        options.ModelProfile!.ExcludeSpecialTokensFromPooling = true;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello world");

        // Special-token exclusion is a mean/auto pooling option. CLS pooling must
        // continue to select the classification row, including when it is enabled.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 1.999f, 2.001f);
    }

    [Fact]
    public async Task EmbedAsync_WithPositionIds_UsesConfiguredInputAndRelativePositions()
    {
        using var fixture = TinyOnnxFixture.Create(positionIdsName: "position_ids");
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.PositionIdsName = fixture.PositionIdsName;
        options.ModelProfile.SendPositionIds = true;
        options.ModelProfile.PaddingSide = "left";
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello world");

        // IDs [CLS, hello, world, SEP] are left-padded to rows 2..5. Position
        // ids for valid tokens are relative [0, 1, 2, 3], so the masked mean is 5.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 4.999f, 5.001f);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedAsync_WithExplicitlyDisabledAttentionMask_RejectsUnboundModelInput()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.AttentionMaskName = null;
        options.ModelProfile.SendAttentionMask = false;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("unbound", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attention_mask", exception.Message, StringComparison.Ordinal);
        Assert.False(provider.IsFallback);
    }

    [Fact]
    public async Task EmbedAsync_WithExplicitlyEnabledMissingPositionIds_RejectsProfile()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.PositionIdsName = "position_ids";
        options.ModelProfile.SendPositionIds = true;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("position", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("position_ids", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_WithOptionalPositionIds_AutoBindsKnownInput()
    {
        using var fixture = TinyOnnxFixture.Create(positionIdsName: "position_ids");
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.PositionIdsName = null;
        options.ModelProfile.SendPositionIds = null;
        options.ModelProfile.PaddingSide = "right";
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello world");

        Assert.Single(embedding);
        Assert.InRange(embedding[0], 4.999f, 5.001f);
    }

    [Fact]
    public async Task EmbedAsync_WithIgnoredInputName_RejectsRuntimeMissingInput()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.AttentionMaskName = null;
        options.ModelProfile.SendAttentionMask = false;
        options.ModelProfile.IgnoredInputNames.Add(fixture.AttentionMaskName);
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        // The profile explicitly accepts the unbound tensor, but this graph still
        // consumes it. ORT's missing-input error is a model contract failure and
        // must remain visible instead of being replaced by a hash vector.
        Assert.Contains("inference failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.AttentionMaskName, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(provider.IsFallback);
    }

    [Fact]
    public async Task EmbedAsync_WithMalformedOnnxModel_ReportsRuntimeFallback()
    {
        using var fixture = TinyOnnxFixture.Create();
        File.WriteAllBytes(fixture.ModelPath, [0]);
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6, dimensions: 2));

        Assert.False(provider.IsFallback);
        var embedding = await provider.EmbedAsync("malformed model");

        Assert.True(provider.IsFallback);
        Assert.Equal(BuiltinHashEmbeddingProvider.VectorDimension, provider.VectorDimension);
        Assert.Equal(BuiltinHashEmbeddingProvider.VectorDimension, embedding.Length);
        Assert.Contains("ONNX execution failed", provider.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbedBatchAsync_WithMalformedOnnxModel_ReturnsObservableFallbackBatch()
    {
        using var fixture = TinyOnnxFixture.Create();
        File.WriteAllBytes(fixture.ModelPath, [0]);
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6, dimensions: 2));

        IReadOnlyList<float[]> embeddings = await provider.EmbedBatchAsync(["hello", "world"]);

        Assert.True(provider.IsFallback);
        Assert.False(provider.ExecutionState.SessionInitialized);
        Assert.Equal(2, embeddings.Count);
        Assert.All(embeddings, static embedding =>
            Assert.Equal(BuiltinHashEmbeddingProvider.VectorDimension, embedding.Length));
    }

    [Fact]
    public async Task EmbedAsync_AutoPooling_UsesMeanForSequenceOutput()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "auto", maxTokens: 6));

        var embedding = await provider.EmbedAsync("hello world");

        // Auto only infers the safe shape-level rule: sequence output means masked mean.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.499f, 3.501f);
    }

    [Fact]
    public async Task EmbedAsync_MeanPooling_CanExcludeSpecialTokens()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.ExcludeSpecialTokensFromPooling = true;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello world");

        // Only hello=4 and world=5 contribute when CLS/SEP are excluded.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 4.499f, 4.501f);
    }

    [Fact]
    public async Task EmbedAsync_OverflowingText_RetainsTrailingSeparatorWhenTruncated()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 4));

        var embedding = await provider.EmbedAsync("hello world hello");

        // Truncation keeps [CLS], the first two tokens, and restores [SEP].
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.499f, 3.501f);
    }

    [Fact]
    public async Task EmbedAsync_WithVeryLongText_RespectsConfiguredTokenLimit()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 4));
        var text = string.Join(' ', Enumerable.Repeat("hello", 100_000));

        var embedding = await provider.EmbedAsync(text);

        // The bounded profile retains [CLS], two content tokens and [SEP], even
        // when the source text contains far more tokens than the model accepts.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.249f, 3.251f);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedAsync_WithOptionalTokenTypeIds_SendsTheConfiguredInput()
    {
        using var fixture = TinyOnnxFixture.Create(tokenTypeIdsName: "token_type_ids");
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.TokenTypeIdsName = fixture.TokenTypeIdsName;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello world");

        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.499f, 3.501f);
    }

    [Fact]
    public async Task EmbedAsync_WithoutAttentionMask_UsesAllReturnedRows()
    {
        using var fixture = TinyOnnxFixture.Create(includeAttentionMask: false);
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 4);
        options.ModelProfile!.AttentionMaskName = null;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello world");

        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.499f, 3.501f);
    }

    [Fact]
    public async Task EmbedAsync_WithFixedSequenceShape_RejectsLargerProfileLength()
    {
        using var fixture = TinyOnnxFixture.Create(fixedSequenceLength: 4);
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello world").AsTask());

        Assert.Contains("fixed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbedAsync_WithAlreadyPooledOutput_DoesNotPoolAgain()
    {
        using var fixture = TinyOnnxFixture.Create(fixedSequenceLength: 1, pooledOutput: true);
        var options = CreateOptions(fixture, pooling: "auto", maxTokens: 1);
        options.ModelProfile!.AddSpecialTokens = false;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var embedding = await provider.EmbedAsync("hello");

        // The graph squeezes [1, 1, 1] to [1, 1]. A second token mean would be
        // indistinguishable for this fixture, so assert the pooled shape/value contract.
        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.999f, 4.001f);
    }

    [Fact]
    public async Task EmbedAsync_Normalize_ProducesUnitVector()
    {
        using var fixture = TinyOnnxFixture.Create(outputDimensions: 2);
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6, dimensions: 2, normalize: true));

        var embedding = await provider.EmbedAsync("hello world");
        double norm = Math.Sqrt(embedding.Sum(static value => (double)value * value));

        Assert.Equal(2, embedding.Length);
        Assert.InRange(norm, 0.9999, 1.0001);
        Assert.InRange(embedding[0], 0.7069f, 0.7073f);
        Assert.InRange(embedding[1], 0.7069f, 0.7073f);
    }

    [Fact]
    public async Task EmbedAsync_UsesProfileTensorNamesAndInt32InputMetadata()
    {
        using var fixture = TinyOnnxFixture.Create(
            inputElementType: TinyOnnxElementType.Int32,
            inputName: "token_ids_custom",
            attentionMaskName: "mask_custom",
            outputName: "projection_custom");
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6));

        var embedding = await provider.EmbedAsync("hello world");

        Assert.Single(embedding);
        Assert.InRange(embedding[0], 3.499f, 3.501f);
    }

    [Fact]
    public async Task EmbedAsync_RejectsOutputDimensionMismatch()
    {
        using var fixture = TinyOnnxFixture.Create(outputDimensions: 2);
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6, dimensions: 1));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello world").AsTask());

        Assert.Contains("dimension", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbedAsync_RejectsUnknownOutputTensor()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.OutputName = "missing_output";
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("missing_output", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_RejectsUnknownTokenizerType()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.TokenizerType = "unsupported-tokenizer";
        using var provider = new LocalOnnxEmbeddingProvider(options);

        Assert.False(provider.IsFallback);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("tokenizer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbedAsync_RejectsUnknownInputTensor()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.ModelProfile!.InputIdsName = "missing_input_ids";
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("missing_input_ids", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_RejectsUnknownPoolingMode()
    {
        using var fixture = TinyOnnxFixture.Create();
        var options = CreateOptions(fixture, pooling: "not-a-pooling-mode", maxTokens: 6);
        using var provider = new LocalOnnxEmbeddingProvider(options);

        Assert.False(provider.IsFallback);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("pooling", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbedAsync_WithCancelledToken_StopsBeforeInference()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.EmbedAsync("hello", cancellation.Token).AsTask());
    }

    [Fact]
    public async Task EmbedAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        using var fixture = TinyOnnxFixture.Create();
        var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6));
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.EmbedAsync("hello").AsTask());
    }

    [Fact]
    public async Task EmbedAsync_ConcurrentCalls_ReuseConfiguredSession()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6));

        var embeddings = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => provider.EmbedAsync("hello world").AsTask()));

        Assert.Equal(8, embeddings.Length);
        Assert.All(embeddings, embedding =>
        {
            Assert.Single(embedding);
            Assert.InRange(embedding[0], 3.499f, 3.501f);
        });
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedBatchAsync_WithDynamicBatch_PoolsEachInputIndependently()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6));

        IReadOnlyList<float[]> embeddings = await provider.EmbedBatchAsync(
            ["hello", "hello world"]);

        Assert.Equal(2, embeddings.Count);
        Assert.Equal(1, provider.InferenceRunCount);
        Assert.InRange(embeddings[0][0], 2.999f, 3.001f);
        Assert.InRange(embeddings[1][0], 3.499f, 3.501f);
        Assert.False(provider.IsFallback, provider.FallbackReason ?? "unexpected fallback");
    }

    [Fact]
    public async Task EmbedBatchAsync_WithNormalization_NormalizesEveryVector()
    {
        using var fixture = TinyOnnxFixture.Create(outputDimensions: 2);
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6, dimensions: 2, normalize: true));

        IReadOnlyList<float[]> embeddings = await provider.EmbedBatchAsync(
            ["hello", "hello world"]);

        Assert.All(embeddings, embedding =>
        {
            Assert.Equal(2, embedding.Length);
            double norm = Math.Sqrt(embedding.Sum(static value => value * value));
            Assert.InRange(norm, 0.9999d, 1.0001d);
        });
    }

    [Fact]
    public async Task EmbedBatchAsync_WithPooledOutput_ReturnsEveryBatchRow()
    {
        using var fixture = TinyOnnxFixture.Create(fixedSequenceLength: 1, pooledOutput: true);
        CopilotEmbeddingOptions options = CreateOptions(fixture, pooling: "auto", maxTokens: 1);
        options.ModelProfile!.AddSpecialTokens = false;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        IReadOnlyList<float[]> embeddings = await provider.EmbedBatchAsync(["hello", "world"]);

        Assert.Equal(2, embeddings.Count);
        Assert.InRange(embeddings[0][0], 3.999f, 4.001f);
        Assert.InRange(embeddings[1][0], 4.999f, 5.001f);
    }

    [Fact]
    public async Task EmbedBatchAsync_WithFixedBatch_RequiresExactInputCount()
    {
        using var fixture = TinyOnnxFixture.Create(fixedBatchSize: 2);
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6));

        IReadOnlyList<float[]> embeddings = await provider.EmbedBatchAsync(["hello", "world"]);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Equal(2, embeddings.Count);
        Assert.Contains("fixed at 2", exception.Message, StringComparison.Ordinal);
        Assert.False(provider.IsFallback);
    }

    [Fact]
    public async Task EmbedBatchAsync_WithInvalidItem_RejectsBeforeSessionCreation()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(
            CreateOptions(fixture, pooling: "mean", maxTokens: 6));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => provider.EmbedBatchAsync(["hello", " "]).AsTask());

        Assert.Contains("index 1", exception.Message, StringComparison.Ordinal);
        Assert.False(provider.ExecutionState.SessionInitialized);
    }

    [Fact]
    public async Task EmbedAsync_WithExplicitThreadCounts_ReportsAppliedSessionState()
    {
        using var fixture = TinyOnnxFixture.Create();
        CopilotEmbeddingOptions options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.IntraOpThreads = 2;
        options.InterOpThreads = 1;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        Assert.False(provider.ExecutionState.SessionInitialized);

        _ = await provider.EmbedAsync("hello");

        LocalOnnxExecutionState state = provider.ExecutionState;
        Assert.True(state.SessionInitialized);
        Assert.True(state.AppliedToSession);
        Assert.Equal(2, state.RequestedIntraOpThreads);
        Assert.Equal(1, state.RequestedInterOpThreads);
        Assert.Equal(2, state.EffectiveIntraOpThreads);
        Assert.Equal(1, state.EffectiveInterOpThreads);
        Assert.Equal("parallel", state.ExecutionMode);
    }

    [Fact]
    public async Task EmbedAsync_WithNegativeThreadCount_RejectsConfigurationWithoutFallback()
    {
        using var fixture = TinyOnnxFixture.Create();
        CopilotEmbeddingOptions options = CreateOptions(fixture, pooling: "mean", maxTokens: 6);
        options.IntraOpThreads = -1;
        using var provider = new LocalOnnxEmbeddingProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.EmbedAsync("hello").AsTask());

        Assert.Contains("IntraOpThreads", exception.Message, StringComparison.Ordinal);
        Assert.False(provider.IsFallback);
        Assert.False(provider.ExecutionState.SessionInitialized);
    }

    [Fact]
    public void ConfigurationBinding_BindsNestedModelProfileWithoutReflectionAtRuntime()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Provider"] = "local",
            ["LocalModelPath"] = "model.onnx",
            ["ModelProfile:TokenizerType"] = "bert-wordpiece",
            ["ModelProfile:TokenizerModelPath"] = "vocab.txt",
            ["ModelProfile:InputIdsName"] = "ids",
            ["ModelProfile:AttentionMaskName"] = "mask",
            ["ModelProfile:TokenTypeIdsName"] = "types",
            ["ModelProfile:OutputName"] = "embedding",
            ["ModelProfile:MaxTokens"] = "12",
            ["ModelProfile:Pooling"] = "mean",
            ["ModelProfile:Normalize"] = "true",
            ["ModelProfile:Dimensions"] = "384",
            ["ModelProfile:AddSpecialTokens"] = "false",
            ["ModelProfile:PadTokenId"] = "99",
            ["IntraOpThreads"] = "2",
            ["InterOpThreads"] = "1",
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = configuration.Get<CopilotEmbeddingOptions>();

        Assert.NotNull(options);
        Assert.Equal("local", options!.Provider);
        Assert.NotNull(options.ModelProfile);
        Assert.Equal("bert-wordpiece", options.ModelProfile!.TokenizerType);
        Assert.Equal("vocab.txt", options.ModelProfile.TokenizerModelPath);
        Assert.Equal("ids", options.ModelProfile.InputIdsName);
        Assert.Equal("mask", options.ModelProfile.AttentionMaskName);
        Assert.Equal("types", options.ModelProfile.TokenTypeIdsName);
        Assert.Equal("embedding", options.ModelProfile.OutputName);
        Assert.Equal(12, options.ModelProfile.MaxTokens);
        Assert.Equal("mean", options.ModelProfile.Pooling);
        Assert.True(options.ModelProfile.Normalize);
        Assert.Equal(384, options.ModelProfile.Dimensions);
        Assert.False(options.ModelProfile.AddSpecialTokens);
        Assert.Equal(99, options.ModelProfile.PadTokenId);
        Assert.Equal(2, options.IntraOpThreads);
        Assert.Equal(1, options.InterOpThreads);
    }

    [Fact]
    public async Task MissingModelProfile_RemainsObservableFallback()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(new CopilotEmbeddingOptions
        {
            Provider = "local",
            LocalModelPath = fixture.ModelPath,
        });

        Assert.True(provider.IsFallback);
        Assert.Contains("profile", provider.FallbackReason!, StringComparison.OrdinalIgnoreCase);
        var embedding = await provider.EmbedAsync("offline fallback");
        Assert.Equal(BuiltinHashEmbeddingProvider.VectorDimension, embedding.Length);
    }

    [Fact]
    public async Task RuntimeFallback_ReportsFallbackVectorDimension()
    {
        using var fixture = TinyOnnxFixture.Create();
        using var provider = new LocalOnnxEmbeddingProvider(new CopilotEmbeddingOptions
        {
            Provider = "local",
            LocalModelPath = Path.Combine(fixture.ModelPath + ".missing"),
            ModelProfile = new CopilotEmbeddingModelProfile
            {
                TokenizerType = "bert-wordpiece",
                TokenizerModelPath = fixture.VocabPath,
                Dimensions = 2,
                MaxTokens = 6,
            },
        });

        Assert.True(provider.IsFallback);
        Assert.Equal(BuiltinHashEmbeddingProvider.VectorDimension, provider.VectorDimension);
        var embedding = await provider.EmbedAsync("offline fallback");
        Assert.Equal(provider.VectorDimension, embedding.Length);
    }

    private static CopilotEmbeddingOptions CreateOptions(
        TinyOnnxFixture fixture,
        string pooling,
        int maxTokens,
        int dimensions = 1,
        bool normalize = false)
        => new()
        {
            Provider = "local",
            LocalModelPath = fixture.ModelPath,
            ModelProfile = new CopilotEmbeddingModelProfile
            {
                TokenizerType = "bert-wordpiece",
                TokenizerModelPath = fixture.VocabPath,
                InputIdsName = fixture.InputName,
                AttentionMaskName = fixture.AttentionMaskName,
                OutputName = fixture.OutputName,
                MaxTokens = maxTokens,
                Pooling = pooling,
                Normalize = normalize,
                Dimensions = dimensions,
                AddSpecialTokens = true,
                PadTokenId = 0,
            },
        };

    private enum TinyOnnxElementType
    {
        Int32 = 6,
        Int64 = 7,
    }

    private sealed class TinyOnnxFixture : IDisposable
    {
        private readonly string _directory;

        private TinyOnnxFixture(
            string directory,
            string modelPath,
            string vocabPath,
            string inputName,
            string attentionMaskName,
            string outputName,
            string? tokenTypeIdsName,
            string? positionIdsName)
        {
            _directory = directory;
            ModelPath = modelPath;
            VocabPath = vocabPath;
            InputName = inputName;
            AttentionMaskName = attentionMaskName;
            OutputName = outputName;
            TokenTypeIdsName = tokenTypeIdsName;
            PositionIdsName = positionIdsName;
        }

        public string ModelPath { get; }

        public string VocabPath { get; }

        public string InputName { get; }

        public string AttentionMaskName { get; }

        public string OutputName { get; }

        public string? TokenTypeIdsName { get; }

        public string? PositionIdsName { get; }

        public static TinyOnnxFixture Create(
            int outputDimensions = 1,
            TinyOnnxElementType inputElementType = TinyOnnxElementType.Int64,
            string inputName = "input_ids",
            string attentionMaskName = "attention_mask",
            string outputName = "embedding",
            string? tokenTypeIdsName = null,
            int? fixedSequenceLength = null,
            int? fixedBatchSize = null,
            bool pooledOutput = false,
            bool includeAttentionMask = true,
            string? positionIdsName = null,
            bool flattenSequenceOutput = false)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "sndb-local-onnx-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string modelPath = Path.Combine(directory, "tiny.onnx");
            string vocabPath = Path.Combine(directory, "vocab.txt");

            const string vocab = "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhello\nworld\n[MASK]\n";
            File.WriteAllText(vocabPath, vocab, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllBytes(modelPath, TinyOnnxModel.Build(
                outputDimensions,
                inputElementType,
                inputName,
                attentionMaskName,
                outputName,
                tokenTypeIdsName,
                fixedSequenceLength,
                fixedBatchSize,
                pooledOutput,
                includeAttentionMask,
                positionIdsName,
                flattenSequenceOutput));

            return new TinyOnnxFixture(
                directory,
                modelPath,
                vocabPath,
                inputName,
                attentionMaskName,
                outputName,
                tokenTypeIdsName,
                positionIdsName);
        }

        public static TinyOnnxFixture CreateSentencePiece(
            int fixedSequenceLength = 2,
            bool flattenSequenceOutput = false,
            bool includeAttentionMask = true)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "sndb-local-onnx-sentencepiece-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string modelPath = Path.Combine(directory, "tiny.onnx");
            string vocabPath = Path.Combine(directory, "tokenizer.model");

            File.WriteAllBytes(vocabPath, TinySentencePieceModel.Build());
            File.WriteAllBytes(modelPath, TinyOnnxModel.Build(
                outputDimensions: flattenSequenceOutput ? fixedSequenceLength : 1,
                inputElementType: TinyOnnxElementType.Int64,
                inputName: "input_ids",
                attentionMaskName: "attention_mask",
                outputName: "embedding",
                tokenTypeIdsName: null,
                fixedSequenceLength: fixedSequenceLength,
                fixedBatchSize: null,
                pooledOutput: false,
                includeAttentionMask: includeAttentionMask,
                positionIdsName: null,
                flattenSequenceOutput: flattenSequenceOutput));

            return new TinyOnnxFixture(
                directory,
                modelPath,
                vocabPath,
                "input_ids",
                "attention_mask",
                "embedding",
                tokenTypeIdsName: null,
                positionIdsName: null);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Native ONNX handles can release just after provider disposal.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup; a failed cleanup must not hide test failures.
            }
        }
    }

    private static class TinyOnnxModel
    {
        private const int FloatType = 1;

        public static byte[] Build(
            int outputDimensions,
            TinyOnnxElementType inputElementType,
            string inputName,
            string attentionMaskName,
            string outputName,
            string? tokenTypeIdsName,
            int? fixedSequenceLength,
            int? fixedBatchSize,
            bool pooledOutput,
            bool includeAttentionMask,
            string? positionIdsName,
            bool flattenSequenceOutput)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(outputDimensions, 1);
            if (flattenSequenceOutput && fixedSequenceLength is not > 0)
                throw new ArgumentException("Flattened sequence fixtures require a fixed sequence length.", nameof(fixedSequenceLength));
            if (flattenSequenceOutput && outputDimensions != fixedSequenceLength)
                throw new ArgumentException("Flattened sequence output dimensions must equal the sequence length.", nameof(outputDimensions));

            int elementType = (int)inputElementType;
            byte[] axes = Tensor("axes", 7, [1], int64Values: [2]);
            byte[] squeezeAxes = Tensor(
                "squeeze_axes",
                7,
                [1],
                int64Values: [flattenSequenceOutput ? 2 : 1]);
            string sequenceDimension = fixedSequenceLength?.ToString() ?? "sequence";
            string batchDimension = fixedBatchSize?.ToString() ?? "batch";
            byte[] idsInput = ValueInfo(inputName, elementType, batchDimension, sequenceDimension);
            byte[] maskInput = ValueInfo(attentionMaskName, elementType, batchDimension, sequenceDimension);
            byte[] outputInfo = pooledOutput || flattenSequenceOutput
                ? ValueInfoPooled(
                    outputName,
                    FloatType,
                    batchDimension,
                    flattenSequenceOutput ? fixedSequenceLength!.Value : outputDimensions)
                : ValueInfo(outputName, FloatType, batchDimension, sequenceDimension, outputDimensions);

            var nodes = new List<byte[]>
            {
                Node(
                    "cast_ids",
                    "Cast",
                    [inputName],
                    ["ids_float"],
                    AttributeInt("to", FloatType)),
            };

            string sequenceSource = "ids_float";
            if (includeAttentionMask)
            {
                nodes.Add(Node(
                    "cast_mask",
                    "Cast",
                    [attentionMaskName],
                    ["mask_float"],
                    AttributeInt("to", FloatType)));
                nodes.Add(Node(
                    "apply_mask",
                    "Mul",
                    ["ids_float", "mask_float"],
                    ["masked_ids"]));
                sequenceSource = "masked_ids";
            }

            if (tokenTypeIdsName is not null)
            {
                nodes.Add(Node(
                    "cast_token_types",
                    "Cast",
                    [tokenTypeIdsName],
                    ["types_float"],
                    AttributeInt("to", FloatType)));
                nodes.Add(Node(
                    "add_token_types",
                    "Add",
                    [sequenceSource, "types_float"],
                    ["masked_ids_with_types"]));
                sequenceSource = "masked_ids_with_types";
            }

            if (positionIdsName is not null)
            {
                nodes.Add(Node(
                    "cast_position_ids",
                    "Cast",
                    [positionIdsName],
                    ["position_ids_float"],
                    AttributeInt("to", FloatType)));
                nodes.Add(Node(
                    "add_position_ids",
                    "Add",
                    [sequenceSource, "position_ids_float"],
                    ["sequence_ids_with_positions"]));
                sequenceSource = "sequence_ids_with_positions";
            }

            nodes.Add(Node(
                "unsqueeze",
                "Unsqueeze",
                [sequenceSource, "axes"],
                ["hidden_base"]));

            if (pooledOutput || flattenSequenceOutput)
            {
                nodes.Add(Node(
                    "squeeze_sequence",
                    "Squeeze",
                    ["hidden_base", "squeeze_axes"],
                    ["pooled_output"]));
            }

            if (flattenSequenceOutput)
            {
                nodes.Add(Node(
                    "identity_flattened",
                    "Identity",
                    ["pooled_output"],
                    [outputName]));
            }
            else if (outputDimensions == 1)
            {
                nodes.Add(Node(
                    "identity",
                    "Identity",
                    [pooledOutput ? "pooled_output" : "hidden_base"],
                    [outputName]));
            }
            else
            {
                var concatInputs = Enumerable.Repeat("hidden_base", outputDimensions).ToArray();
                string outputSource;
                if (pooledOutput)
                {
                    var pooledConcatInputs = Enumerable.Repeat("pooled_output", outputDimensions).ToArray();
                    nodes.Add(Node(
                        "concat_pooled",
                        "Concat",
                        pooledConcatInputs,
                        [outputName],
                        AttributeInt("axis", 1)));
                    outputSource = outputName;
                }
                else
                {
                    nodes.Add(Node(
                        "concat",
                        "Concat",
                        concatInputs,
                        [outputName],
                        AttributeInt("axis", 2)));
                    outputSource = outputName;
                }

                if (outputSource != outputName)
                    nodes.Add(Node("identity_output", "Identity", [outputSource], [outputName]));
            }

            byte[] graph = Message(writer =>
            {
                foreach (byte[] node in nodes)
                    FieldMessage(writer, 1, node);
                FieldString(writer, 2, "tiny-sonnetdb-profile");
                FieldMessage(writer, 5, axes);
                if (pooledOutput || flattenSequenceOutput)
                    FieldMessage(writer, 5, squeezeAxes);
                FieldMessage(writer, 11, idsInput);
                if (includeAttentionMask)
                    FieldMessage(writer, 11, maskInput);
                if (tokenTypeIdsName is not null)
                    FieldMessage(writer, 11, ValueInfo(tokenTypeIdsName, elementType, batchDimension, sequenceDimension));
                if (positionIdsName is not null)
                    FieldMessage(writer, 11, ValueInfo(positionIdsName, elementType, batchDimension, sequenceDimension));
                FieldMessage(writer, 12, outputInfo);
            });

            byte[] opset = Message(writer =>
            {
                FieldString(writer, 1, string.Empty);
                FieldVarint(writer, 2, 13);
            });

            return Message(writer =>
            {
                FieldVarint(writer, 1, 8);
                FieldString(writer, 2, "SonnetDB.Tests");
                FieldMessage(writer, 7, graph);
                FieldMessage(writer, 8, opset);
            });
        }

        private static byte[] ValueInfo(
            string name,
            int elementType,
            string batchDimension,
            string sequenceDimension,
            int? lastDimension = null)
        {
            byte[] firstDimension = int.TryParse(batchDimension, out var fixedBatch)
                ? Dimension(fixedBatch)
                : Dimension(batchDimension);
            byte[] sequence = int.TryParse(sequenceDimension, out var fixedLength)
                ? Dimension(fixedLength)
                : Dimension(sequenceDimension);
            byte[] shape = Message(writer =>
            {
                FieldMessage(writer, 1, firstDimension);
                FieldMessage(writer, 1, sequence);
                if (lastDimension.HasValue)
                    FieldMessage(writer, 1, Dimension(lastDimension.Value));
            });
            byte[] tensorType = Message(writer =>
            {
                FieldVarint(writer, 1, (ulong)elementType);
                FieldMessage(writer, 2, shape);
            });
            byte[] type = Message(writer => FieldMessage(writer, 1, tensorType));
            return Message(writer =>
            {
                FieldString(writer, 1, name);
                FieldMessage(writer, 2, type);
            });
        }

        private static byte[] ValueInfoPooled(
            string name,
            int elementType,
            string batchDimension,
            int dimensions)
        {
            byte[] shape = Message(writer =>
            {
                FieldMessage(
                    writer,
                    1,
                    int.TryParse(batchDimension, out var fixedBatch)
                        ? Dimension(fixedBatch)
                        : Dimension(batchDimension));
                FieldMessage(writer, 1, Dimension(dimensions));
            });
            byte[] tensorType = Message(writer =>
            {
                FieldVarint(writer, 1, (ulong)elementType);
                FieldMessage(writer, 2, shape);
            });
            byte[] type = Message(writer => FieldMessage(writer, 1, tensorType));
            return Message(writer =>
            {
                FieldString(writer, 1, name);
                FieldMessage(writer, 2, type);
            });
        }

        private static byte[] Dimension(long value)
            => Message(writer => FieldVarint(writer, 1, unchecked((ulong)value)));

        private static byte[] Dimension(string parameter)
            => Message(writer => FieldString(writer, 2, parameter));

        private static byte[] Tensor(
            string name,
            int elementType,
            IReadOnlyList<long> dimensions,
            IReadOnlyList<long> int64Values)
            => Message(writer =>
            {
                foreach (long dimension in dimensions)
                    FieldVarint(writer, 1, unchecked((ulong)dimension));
                FieldVarint(writer, 2, (ulong)elementType);
                foreach (long value in int64Values)
                    FieldVarint(writer, 7, unchecked((ulong)value));
                FieldString(writer, 8, name);
            });

        private static byte[] Node(
            string name,
            string operation,
            IReadOnlyList<string> inputs,
            IReadOnlyList<string> outputs,
            params byte[][] attributes)
            => Message(writer =>
            {
                foreach (string input in inputs)
                    FieldString(writer, 1, input);
                foreach (string output in outputs)
                    FieldString(writer, 2, output);
                FieldString(writer, 3, name);
                FieldString(writer, 4, operation);
                foreach (byte[] attribute in attributes)
                    FieldMessage(writer, 5, attribute);
            });

        private static byte[] AttributeInt(string name, int value)
            => Message(writer =>
            {
                FieldString(writer, 1, name);
                FieldVarint(writer, 3, unchecked((ulong)value));
                FieldVarint(writer, 20, 2); // AttributeProto.INT
            });

        private static byte[] Message(Action<Stream> write)
        {
            using var stream = new MemoryStream();
            write(stream);
            return stream.ToArray();
        }

        private static void FieldString(Stream stream, int fieldNumber, string value)
            => FieldBytes(stream, fieldNumber, Encoding.UTF8.GetBytes(value));

        private static void FieldMessage(Stream stream, int fieldNumber, byte[] value)
            => FieldBytes(stream, fieldNumber, value);

        private static void FieldBytes(Stream stream, int fieldNumber, ReadOnlySpan<byte> value)
        {
            Tag(stream, fieldNumber, wireType: 2);
            Varint(stream, (ulong)value.Length);
            stream.Write(value);
        }

        private static void FieldVarint(Stream stream, int fieldNumber, ulong value)
        {
            Tag(stream, fieldNumber, wireType: 0);
            Varint(stream, value);
        }

        private static void Tag(Stream stream, int fieldNumber, int wireType)
            => Varint(stream, checked((ulong)((fieldNumber << 3) | wireType)));

        private static void Varint(Stream stream, ulong value)
        {
            Span<byte> buffer = stackalloc byte[10];
            int index = 0;
            while (value >= 0x80)
            {
                buffer[index++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[index++] = (byte)value;
            stream.Write(buffer[..index]);
        }
    }

    private static class TinySentencePieceModel
    {
        public static byte[] Build()
        {
            byte[] trainerSpec = Message(writer =>
            {
                FieldVarint(writer, 3, 1); // TrainerSpec.model_type = UNIGRAM.
                FieldVarint(writer, 40, 0); // unk_id.
                FieldVarint(writer, 41, 1); // bos_id.
                FieldVarint(writer, 42, 2); // eos_id.
                FieldVarint(writer, 43, 3); // pad_id.
            });

            return Message(writer =>
            {
                FieldMessage(writer, 1, Piece("<unk>", 0f, type: 2));
                FieldMessage(writer, 1, Piece("<s>", 0f, type: 3));
                FieldMessage(writer, 1, Piece("</s>", 0f, type: 3));
                FieldMessage(writer, 1, Piece("<pad>", 0f, type: 3));
                // SentencePiece's default normalizer adds U+2581 as a dummy prefix.
                FieldMessage(writer, 1, Piece("\u2581hello", -1f, type: 1));
                FieldMessage(writer, 2, trainerSpec);
                // Microsoft.ML.Tokenizers expects the optional generated protobuf
                // property to be materialized before reading normalizer defaults.
                FieldMessage(writer, 3, Array.Empty<byte>());
            });
        }

        private static byte[] Piece(string text, float score, int type)
            => Message(writer =>
            {
                FieldString(writer, 1, text);
                FieldFixed32(writer, 2, unchecked((uint)BitConverter.SingleToInt32Bits(score)));
                FieldVarint(writer, 3, unchecked((ulong)type));
            });

        private static byte[] Message(Action<Stream> write)
        {
            using var stream = new MemoryStream();
            write(stream);
            return stream.ToArray();
        }

        private static void FieldString(Stream stream, int fieldNumber, string value)
            => FieldBytes(stream, fieldNumber, Encoding.UTF8.GetBytes(value));

        private static void FieldMessage(Stream stream, int fieldNumber, byte[] value)
            => FieldBytes(stream, fieldNumber, value);

        private static void FieldBytes(Stream stream, int fieldNumber, ReadOnlySpan<byte> value)
        {
            Tag(stream, fieldNumber, wireType: 2);
            Varint(stream, (ulong)value.Length);
            stream.Write(value);
        }

        private static void FieldFixed32(Stream stream, int fieldNumber, uint value)
        {
            Tag(stream, fieldNumber, wireType: 5);
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        private static void FieldVarint(Stream stream, int fieldNumber, ulong value)
        {
            Tag(stream, fieldNumber, wireType: 0);
            Varint(stream, value);
        }

        private static void Tag(Stream stream, int fieldNumber, int wireType)
            => Varint(stream, checked((ulong)((fieldNumber << 3) | wireType)));

        private static void Varint(Stream stream, ulong value)
        {
            Span<byte> buffer = stackalloc byte[10];
            int index = 0;
            while (value >= 0x80)
            {
                buffer[index++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[index++] = (byte)value;
            stream.Write(buffer[..index]);
        }
    }
}
