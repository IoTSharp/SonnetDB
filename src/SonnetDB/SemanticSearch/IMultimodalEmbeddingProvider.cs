namespace SonnetDB.SemanticSearch;

/// <summary>
/// 同一向量空间中的文本与图片 embedding provider。
/// </summary>
public interface IMultimodalEmbeddingProvider
{
    /// <summary>返回 provider 的稳定能力描述。</summary>
    MultimodalEmbeddingProviderInfo Info { get; }

    /// <summary>
    /// 为自然语言文本生成归一化向量。
    /// </summary>
    /// <param name="text">输入文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>L2 归一化后的 embedding。</returns>
    ValueTask<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为已编码的图片字节生成归一化向量。
    /// </summary>
    /// <param name="image">PNG、JPEG、WebP 等 ImageSharp 支持的图片字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>L2 归一化后的 embedding。</returns>
    ValueTask<float[]> EmbedImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default);
}

/// <summary>
/// 多模态 embedding provider 的运行能力描述。
/// </summary>
/// <param name="Name">provider 名称。</param>
/// <param name="Profile">embedding profile 标识。</param>
/// <param name="Dimensions">输出向量维度。</param>
/// <param name="Ready">provider 当前是否可执行。</param>
/// <param name="Reason">未就绪原因。</param>
public sealed record MultimodalEmbeddingProviderInfo(
    string Name,
    string Profile,
    int Dimensions,
    bool Ready,
    string? Reason = null);
