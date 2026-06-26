namespace SonnetDB.FullText.Tokenization;

/// <summary>
/// 分词器抽象（AOT 友好）。
/// </summary>
/// <remarks>
/// 设计要点：
/// <list type="bullet">
///   <item>仅使用 <see cref="ReadOnlySpan{T}"/> 输入，不要求堆分配。</item>
///   <item>不返回 <see cref="System.Collections.Generic.IEnumerable{T}"/>，避免迭代器装箱。</item>
///   <item>结果通过 <see cref="ITokenSink"/> 主动推送，便于流式构建倒排。</item>
/// </list>
/// </remarks>
public interface ITokenizer
{
    /// <summary>
    /// 对 <paramref name="text"/> 进行切分，并把每个 token 推送给 <paramref name="sink"/>。
    /// </summary>
    /// <param name="text">输入文本。</param>
    /// <param name="sink">用于接收 token 的回调宿主。</param>
    void Tokenize(ReadOnlySpan<char> text, ITokenSink sink);
}
