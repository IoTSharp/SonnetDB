namespace SonnetDB.FullText.Tokenization;

/// <summary>
/// 分词结果接收器。
/// </summary>
public interface ITokenSink
{
    /// <summary>
    /// 接收一个 token。
    /// </summary>
    /// <param name="token">token 文本。生命周期仅限本次调用。</param>
    /// <param name="startOffset">在原始输入中的起始字符偏移（含）。</param>
    /// <param name="endOffset">在原始输入中的结束字符偏移（不含）。</param>
    /// <param name="positionIncrement">相对上一个 token 的位置增量，通常为 1。</param>
    void Emit(ReadOnlySpan<char> token, int startOffset, int endOffset, int positionIncrement);
}
