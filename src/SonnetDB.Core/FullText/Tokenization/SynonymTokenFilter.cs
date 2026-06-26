namespace SonnetDB.FullText.Tokenization;

/// <summary>
/// 基于词表的同义词 token 过滤器。
/// </summary>
public sealed class SynonymTokenFilter : ITokenFilter
{
    private readonly IReadOnlyDictionary<string, string> _synonyms;

    /// <summary>
    /// 创建同义词过滤器。键为输入 token，值为替换后的规范 token。
    /// </summary>
    public SynonymTokenFilter(IReadOnlyDictionary<string, string> synonyms)
    {
        ArgumentNullException.ThrowIfNull(synonyms);
        _synonyms = new Dictionary<string, string>(synonyms, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool TryFilter(ReadOnlySpan<char> token, Span<char> buffer, out int written)
    {
        string key = token.ToString();
        ReadOnlySpan<char> output = _synonyms.TryGetValue(key, out string? synonym) ? synonym.AsSpan() : token;
        if (output.Length > buffer.Length)
        {
            written = 0;
            return false;
        }

        output.CopyTo(buffer);
        written = output.Length;
        return true;
    }
}
