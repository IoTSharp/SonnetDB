using System.Collections.Generic;

namespace SonnetDB.FullText.Tokenization;

/// <summary>
/// 收集分词结果到列表的 <see cref="ITokenSink"/> 实现，主要用于测试。
/// </summary>
public sealed class CollectingTokenSink : ITokenSink
{
    private readonly List<Token> _tokens = new();

    /// <summary>
    /// 收集到的所有 token。
    /// </summary>
    public IReadOnlyList<Token> Tokens => _tokens;

    /// <inheritdoc />
    public void Emit(ReadOnlySpan<char> token, int startOffset, int endOffset, int positionIncrement)
    {
        _tokens.Add(new Token(token.ToString(), startOffset, endOffset, positionIncrement));
    }

    /// <summary>
    /// 清空已收集的 token。
    /// </summary>
    public void Clear() => _tokens.Clear();
}

/// <summary>
/// 简化的 token 记录。
/// </summary>
/// <param name="Text">token 文本。</param>
/// <param name="StartOffset">起始字符偏移。</param>
/// <param name="EndOffset">结束字符偏移。</param>
/// <param name="PositionIncrement">位置增量。</param>
public readonly record struct Token(string Text, int StartOffset, int EndOffset, int PositionIncrement);
