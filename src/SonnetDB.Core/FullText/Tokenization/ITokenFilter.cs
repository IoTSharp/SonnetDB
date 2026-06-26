namespace SonnetDB.FullText.Tokenization;

/// <summary>
/// Token 过滤器（小写化、停用词、同义词等）。
/// </summary>
public interface ITokenFilter
{
    /// <summary>
    /// 对 <paramref name="token"/> 进行变换，并把结果写入 <paramref name="buffer"/>。
    /// </summary>
    /// <param name="token">原始 token。</param>
    /// <param name="buffer">输出缓冲区，至少与 <paramref name="token"/> 等长。</param>
    /// <param name="written">实际写入的字符数；返回 0 表示丢弃该 token。</param>
    /// <returns>true 表示已写入；false 表示 buffer 容量不足。</returns>
    bool TryFilter(ReadOnlySpan<char> token, Span<char> buffer, out int written);
}
