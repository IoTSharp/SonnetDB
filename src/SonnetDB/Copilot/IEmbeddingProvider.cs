namespace SonnetDB.Copilot;

/// <summary>
/// 文本嵌入 provider 抽象。
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// 为单条文本生成 embedding 向量。
    /// </summary>
    /// <param name="text">输入文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>embedding 向量。</returns>
    ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为一批文本生成 embedding 向量，返回顺序与输入顺序一致。
    /// </summary>
    /// <param name="texts">非空文本集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入逐项对应的 embedding 向量。</returns>
    async ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            throw new ArgumentException("Embedding batch cannot be empty.", nameof(texts));

        var embeddings = new float[texts.Count][];
        for (var index = 0; index < texts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings[index] = await EmbedAsync(texts[index], cancellationToken).ConfigureAwait(false);
        }

        return embeddings;
    }
}
