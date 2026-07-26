using SonnetDB.Configuration;

namespace SonnetDB.SemanticSearch;

/// <summary>
/// 语义检索关闭时使用的明确失败 provider，保证状态端点仍可查询。
/// </summary>
public sealed class DisabledMultimodalEmbeddingProvider : IMultimodalEmbeddingProvider
{
    /// <summary>
    /// 初始化关闭状态 provider。
    /// </summary>
    /// <param name="options">语义检索配置。</param>
    public DisabledMultimodalEmbeddingProvider(SemanticSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Info = new MultimodalEmbeddingProviderInfo(
            options.Provider,
            options.Profile,
            options.Dimensions,
            Ready: false,
            "语义图片检索未启用。");
    }

    /// <inheritdoc />
    public MultimodalEmbeddingProviderInfo Info { get; }

    /// <inheritdoc />
    public ValueTask<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
        => ValueTask.FromException<float[]>(new InvalidOperationException("语义图片检索未启用。"));

    /// <inheritdoc />
    public ValueTask<float[]> EmbedImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
        => ValueTask.FromException<float[]>(new InvalidOperationException("语义图片检索未启用。"));
}
