using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SonnetDB.SemanticContent;

/// <summary>
/// RAG 文本分块的确定性边界与资源预算。
/// 所有长度均按 .NET UTF-16 字符计数。
/// </summary>
public sealed record RagTextChunkingOptions
{
    /// <summary>内容稳定标识允许的最大 UTF-16 字符数。</summary>
    public int MaxContentIdCharacters { get; init; } = 4_096;

    /// <summary>单个分块允许的最大 UTF-16 字符数。</summary>
    public int MaxCharacters { get; init; } = 800;

    /// <summary>相邻分块最多重复的 UTF-16 字符数。</summary>
    public int OverlapCharacters { get; init; } = 100;

    /// <summary>单次分块允许处理的最大输入字符数。</summary>
    public int MaxInputCharacters { get; init; } = 4 * 1024 * 1024;

    /// <summary>单次分块允许产生的最大分块数。</summary>
    public int MaxChunks { get; init; } = 10_000;
}

/// <summary>
/// 一份文本内容的确定性分块快照。
/// </summary>
public sealed record RagTextSnapshot
{
    /// <summary>创建空快照，供 source-generated JSON 反序列化使用。</summary>
    public RagTextSnapshot()
    {
    }

    /// <summary>创建文本分块快照。</summary>
    /// <param name="contentId">父内容的稳定标识。</param>
    /// <param name="contentHash">完整输入文本的 SHA-256 hash。</param>
    /// <param name="chunks">确定性文本分块。</param>
    public RagTextSnapshot(
        string contentId,
        string contentHash,
        IReadOnlyList<SemanticContentChunk> chunks)
    {
        ContentId = contentId;
        ContentHash = contentHash;
        Chunks = chunks;
    }

    /// <summary>父内容的稳定标识。</summary>
    public string ContentId { get; init; } = string.Empty;

    /// <summary>完整输入文本的 SHA-256 hash。</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>确定性文本分块。</summary>
    public IReadOnlyList<SemanticContentChunk> Chunks { get; init; }
        = Array.Empty<SemanticContentChunk>();
}

/// <summary>
/// 为通用 RAG 摄取生成确定性文本 hash 和稳定分块。
/// 该类型不读取文件、不调用 embedding provider，也不写入数据库。
/// </summary>
public static class RagTextChunker
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// 对完整文本进行确定性分块。
    /// hash 基于输入文本的精确 UTF-8 表示；偏移基于原始字符串的 UTF-16 位置。
    /// </summary>
    /// <param name="contentId">父内容的稳定标识。</param>
    /// <param name="text">待分块的完整文本。</param>
    /// <param name="options">可选的分块边界和资源预算。</param>
    /// <param name="cancellationToken">用于停止 hash 和分块工作的取消令牌。</param>
    /// <returns>包含内容 hash 与稳定分块的快照。</returns>
    /// <exception cref="ArgumentException">标识为空或选项不合法。</exception>
    /// <exception cref="ArgumentOutOfRangeException">输入超过字符预算。</exception>
    /// <exception cref="EncoderFallbackException">
    /// 内容标识或文本包含未正确配对的 UTF-16 surrogate，无法无损编码为 UTF-8。
    /// </exception>
    /// <exception cref="InvalidOperationException">输出超过分块数预算。</exception>
    public static RagTextSnapshot Chunk(
        string contentId,
        string text,
        RagTextChunkingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        ArgumentNullException.ThrowIfNull(text);
        options ??= new RagTextChunkingOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (contentId.Length > options.MaxContentIdCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentId),
                contentId.Length,
                $"内容标识长度不能超过 {options.MaxContentIdCharacters} 个 UTF-16 字符。");
        }

        // 即使正文为空，也先拒绝无法无损进入稳定 ID/JSON 合同的标识。
        _ = StrictUtf8.GetByteCount(contentId);

        if (text.Length > options.MaxInputCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                text.Length,
                $"文本长度不能超过 {options.MaxInputCharacters} 个 UTF-16 字符。");
        }

        string contentHash = ComputeHash(text, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            return new RagTextSnapshot(contentId, contentHash, Array.Empty<SemanticContentChunk>());

        var chunks = new List<SemanticContentChunk>();
        var hashOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        int start = SkipWhitespace(text, 0);
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = FindChunkEnd(text, start, options.MaxCharacters);
            int contentEnd = TrimEndWhitespace(text, start, end);
            if (contentEnd > start)
            {
                if (chunks.Count == options.MaxChunks)
                {
                    throw new InvalidOperationException(
                        $"文本分块数超过预算 {options.MaxChunks}。");
                }

                string chunkText = text[start..contentEnd];
                string chunkHash = ComputeHash(chunkText, cancellationToken);
                int occurrence = hashOccurrences.GetValueOrDefault(chunkHash);
                hashOccurrences[chunkHash] = checked(occurrence + 1);
                string stableId = CreateStableId(contentId, chunkHash, occurrence, cancellationToken);
                chunks.Add(new SemanticContentChunk(
                    stableId,
                    chunks.Count,
                    chunkText,
                    start,
                    contentEnd,
                    chunkHash));
            }

            if (end >= text.Length)
                break;

            int nextStart = Math.Max(contentEnd - options.OverlapCharacters, start + 1);
            nextStart = AdjustStartForSurrogatePair(text, nextStart);
            start = SkipWhitespace(text, nextStart);
        }

        return new RagTextSnapshot(contentId, contentHash, chunks.ToArray());
    }

    private static void ValidateOptions(RagTextChunkingOptions options)
    {
        if (options.MaxContentIdCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxContentIdCharacters 必须大于 0。");
        if (options.MaxCharacters < 2)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxCharacters 必须大于等于 2。");
        if (options.OverlapCharacters < 0
            || options.OverlapCharacters >= options.MaxCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "OverlapCharacters 必须大于等于 0 且小于 MaxCharacters。");
        }

        if (options.MaxInputCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxInputCharacters 必须大于 0。");
        if (options.MaxChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxChunks 必须大于 0。");
    }

    private static int FindChunkEnd(string text, int start, int maxCharacters)
    {
        int candidate = (int)Math.Min((long)start + maxCharacters, text.Length);
        candidate = AdjustEndForSurrogatePair(text, candidate);
        if (candidate >= text.Length)
            return text.Length;

        int minimumBoundary = start + (candidate - start) / 2;
        int whitespaceBoundary = -1;
        for (int index = candidate - 1; index >= minimumBoundary; index--)
        {
            if (text[index] == '\n')
                return index + 1;
            if (whitespaceBoundary < 0 && char.IsWhiteSpace(text[index]))
                whitespaceBoundary = index + 1;
        }

        return whitespaceBoundary >= 0 ? whitespaceBoundary : candidate;
    }

    private static int AdjustEndForSurrogatePair(string text, int end)
    {
        if (end > 0
            && end < text.Length
            && char.IsHighSurrogate(text[end - 1])
            && char.IsLowSurrogate(text[end]))
        {
            return end - 1;
        }

        return end;
    }

    private static int AdjustStartForSurrogatePair(string text, int start)
    {
        if (start > 0
            && start < text.Length
            && char.IsLowSurrogate(text[start])
            && char.IsHighSurrogate(text[start - 1]))
        {
            // 向前跨过整个 scalar，避免 overlap 把下一块重新拉回当前块起点。
            return start + 1;
        }

        return start;
    }

    private static int SkipWhitespace(string text, int start)
    {
        int current = start;
        while (current < text.Length && char.IsWhiteSpace(text[current]))
            current++;
        return current;
    }

    private static int TrimEndWhitespace(string text, int start, int end)
    {
        int current = end;
        while (current > start && char.IsWhiteSpace(text[current - 1]))
            current--;
        return current;
    }

    private static string CreateStableId(
        string contentId,
        string chunkHash,
        int occurrence,
        CancellationToken cancellationToken)
    {
        string seed = string.Concat(
            contentId,
            "\n",
            chunkHash,
            "\n",
            occurrence.ToString(CultureInfo.InvariantCulture));
        return "rag:" + ComputeHash(seed, cancellationToken)["sha256:".Length..];
    }

    private static string ComputeHash(string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] utf8 = StrictUtf8.GetBytes(value);
        byte[] hash = SHA256.HashData(utf8);
        cancellationToken.ThrowIfCancellationRequested();
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
