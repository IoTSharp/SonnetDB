using System.Text;
using SonnetDB.SemanticContent;

namespace SonnetDB.SemanticSearch;

/// <summary>从已经解析的对象内容生成向量；不负责下载 URL、读取对象或执行外发授权。</summary>
public interface IObjectEmbeddingProvider
{
    /// <summary>对象 provider 的执行位置、profile 与向量维度。</summary>
    MultimodalEmbeddingProviderInfo Info { get; }

    /// <summary>可以处理的规范媒体类型，不包含通配符。</summary>
    IReadOnlyList<string> ContentTypes { get; }

    /// <summary>为指定版本的对象内容生成向量。</summary>
    /// <param name="source">已解析的对象引用。</param>
    /// <param name="contentType">对象媒体类型。</param>
    /// <param name="content">有界读取的内容；仅用于本次调用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与能力描述维度一致的向量。</returns>
    ValueTask<float[]> EmbedObjectAsync(SemanticObjectReference source, string contentType,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}

/// <summary>使用现有文本和图片编码器处理 UTF-8 文本或图片对象，保留同一向量空间。</summary>
public sealed class MultimodalObjectEmbeddingProvider : IObjectEmbeddingProvider
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyList<string> SupportedTypes = Array.AsReadOnly(new[]
    {
        "text/plain", "text/markdown", "image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp",
    });
    private readonly IMultimodalEmbeddingProvider _provider;

    /// <summary>创建复用现有编码器的对象 adapter。</summary>
    /// <param name="provider">具有文本和图片能力的编码器。</param>
    public MultimodalObjectEmbeddingProvider(IMultimodalEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <inheritdoc />
    public MultimodalEmbeddingProviderInfo Info => _provider.Info;

    /// <inheritdoc />
    public IReadOnlyList<string> ContentTypes => SupportedTypes;

    /// <inheritdoc />
    public ValueTask<float[]> EmbedObjectAsync(SemanticObjectReference source, string contentType,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.VersionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (content.IsEmpty)
            throw new ArgumentException("对象内容不能为空。", nameof(content));
        string mediaType = NormalizeContentType(contentType);
        if (!SupportedTypes.Contains(mediaType, StringComparer.Ordinal))
            throw new NotSupportedException("对象媒体类型不在 provider 能力清单中。");
        if (mediaType.StartsWith("text/", StringComparison.Ordinal))
        {
            string text = StrictUtf8.GetString(content.Span);
            if (text.StartsWith('\uFEFF'))
                text = text[1..];
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            return _provider.EmbedTextAsync(text, cancellationToken);
        }
        return _provider.EmbedImageAsync(content, cancellationToken);
    }

    internal static string NormalizeContentType(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
    }
}
