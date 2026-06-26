namespace SonnetDB.FullText.Tokenization;

/// <summary>
/// 用静态映射把中文 token 转为拼音 token 的过滤器。
/// </summary>
public sealed class PinyinTokenFilter : ITokenFilter
{
    private readonly IReadOnlyDictionary<string, string> _pinyin;

    /// <summary>
    /// 创建拼音过滤器。键为中文 token，值为不带声调的拼音。
    /// </summary>
    public PinyinTokenFilter(IReadOnlyDictionary<string, string> pinyin)
    {
        ArgumentNullException.ThrowIfNull(pinyin);
        _pinyin = new Dictionary<string, string>(pinyin, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool TryFilter(ReadOnlySpan<char> token, Span<char> buffer, out int written)
    {
        string key = token.ToString();
        if (!_pinyin.TryGetValue(key, out string? mapped))
        {
            if (token.Length > buffer.Length)
            {
                written = 0;
                return false;
            }

            token.CopyTo(buffer);
            written = token.Length;
            return true;
        }

        if (mapped.Length > buffer.Length)
        {
            written = 0;
            return false;
        }

        mapped.AsSpan().CopyTo(buffer);
        written = mapped.Length;
        return true;
    }
}
