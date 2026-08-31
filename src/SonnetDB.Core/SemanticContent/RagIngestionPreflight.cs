namespace SonnetDB.SemanticContent;

internal readonly record struct RagNestedBudgetLimits(
    long MaxTotalChunks,
    long MaxTotalSegments,
    long MaxTotalEmbeddings,
    long MaxTotalTextCharacters);

internal sealed class RagNestedBudgetUsage
{
    internal long TotalChunks;

    internal long TotalSegments;

    internal long TotalEmbeddings;

    internal long TotalTextCharacters;
}

internal static class RagIngestionPreflight
{
    public static SemanticContentManifest[] FreezeSnapshot(
        RagIngestionSnapshot snapshot,
        int maxManifests,
        RagNestedBudgetLimits limits,
        RagNestedBudgetUsage usage,
        string parameterName,
        CancellationToken cancellationToken)
    {
        if (snapshot.SchemaVersion <= 0)
            throw new ArgumentException("快照 SchemaVersion 必须大于 0。", parameterName);

        IReadOnlyList<SemanticContentManifest> source = snapshot.Manifests
            ?? throw new ArgumentException("快照 Manifests 不能为 null。", parameterName);
        int manifestCount = source.Count;
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCount(manifestCount, parameterName + ".Manifests", parameterName);
        if (manifestCount > maxManifests)
        {
            throw new InvalidOperationException(
                $"{parameterName} 清单数 {manifestCount} 超过预算 {maxManifests}。");
        }

        var manifests = new SemanticContentManifest[manifestCount];
        for (int index = 0; index < manifestCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifests[index] = source[index]
                ?? throw new ArgumentException(
                    $"{parameterName}.Manifests[{index}] 不能为 null。",
                    parameterName);
        }

        for (int index = 0; index < manifests.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifests[index] = FreezeManifest(
                manifests[index],
                limits,
                usage,
                $"{parameterName}.Manifests[{index}]",
                parameterName,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return manifests;
    }

    public static SemanticContentManifest FreezeManifest(
        SemanticContentManifest manifest,
        RagNestedBudgetLimits limits,
        RagNestedBudgetUsage usage,
        string path,
        string parameterName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SemanticContentChunk> chunkSource = manifest.Chunks
            ?? throw new ArgumentException($"{path}.Chunks 不能为 null。", parameterName);
        IReadOnlyList<SemanticContentSegment> segmentSource = manifest.Segments
            ?? throw new ArgumentException($"{path}.Segments 不能为 null。", parameterName);
        IReadOnlyList<SemanticEmbeddingBinding> embeddingSource = manifest.Embeddings
            ?? throw new ArgumentException($"{path}.Embeddings 不能为 null。", parameterName);

        int chunkCount = chunkSource.Count;
        cancellationToken.ThrowIfCancellationRequested();
        int segmentCount = segmentSource.Count;
        cancellationToken.ThrowIfCancellationRequested();
        int embeddingCount = embeddingSource.Count;
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCount(chunkCount, path + ".Chunks", parameterName);
        ValidateCount(segmentCount, path + ".Segments", parameterName);
        ValidateCount(embeddingCount, path + ".Embeddings", parameterName);

        AddToBudget(ref usage.TotalChunks, chunkCount, limits.MaxTotalChunks, "分块");
        AddToBudget(ref usage.TotalSegments, segmentCount, limits.MaxTotalSegments, "时间分段");
        AddToBudget(ref usage.TotalEmbeddings, embeddingCount, limits.MaxTotalEmbeddings, "embedding 绑定");
        AddTextToBudget(ref usage.TotalTextCharacters, manifest.Text, limits.MaxTotalTextCharacters);

        var chunks = new SemanticContentChunk[chunkCount];
        for (int index = 0; index < chunks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticContentChunk? chunk = chunkSource[index];
            chunks[index] = chunk!;
            AddTextToBudget(
                ref usage.TotalTextCharacters,
                chunk?.Text,
                limits.MaxTotalTextCharacters);
        }

        var segments = new SemanticContentSegment[segmentCount];
        for (int index = 0; index < segments.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticContentSegment? segment = segmentSource[index];
            segments[index] = segment!;
            AddTextToBudget(
                ref usage.TotalTextCharacters,
                segment?.Text,
                limits.MaxTotalTextCharacters);
        }

        var embeddings = new SemanticEmbeddingBinding[embeddingCount];
        for (int index = 0; index < embeddings.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings[index] = embeddingSource[index]!;
        }

        var frozen = manifest with
        {
            Chunks = chunks,
            Segments = segments,
            Embeddings = embeddings,
        };
        cancellationToken.ThrowIfCancellationRequested();
        SemanticContentValidator.ValidateOrThrow(
            frozen,
            profiles: null,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return frozen;
    }

    private static void AddTextToBudget(ref long total, string? text, long maximum)
        => AddToBudget(ref total, text?.Length ?? 0, maximum, "文本字符");

    private static void AddToBudget(ref long total, int amount, long maximum, string name)
    {
        if (total > maximum - amount)
            throw new InvalidOperationException($"摄取输入{name}总数超过预算 {maximum}。");
        total += amount;
    }

    private static void ValidateCount(int count, string path, string parameterName)
    {
        if (count < 0)
            throw new ArgumentException($"{path}.Count 不能为负数。", parameterName);
    }
}
