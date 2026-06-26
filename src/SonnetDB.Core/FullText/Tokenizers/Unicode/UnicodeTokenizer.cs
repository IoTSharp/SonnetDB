using System.Globalization;
using SonnetDB.FullText.Tokenization;

namespace SonnetDB.FullText.Tokenizers.Unicode;

/// <summary>
/// 默认 Unicode 分词器：按 Unicode 类别识别词边界，对字母/数字按连续段切分，
/// 全部输出小写形式（基于 invariant culture）。
/// </summary>
/// <remarks>
/// 设计目标：
/// <list type="bullet">
///   <item>纯托管、零分配、AOT 友好。</item>
///   <item>适配大多数欧美语言；中日韩等字符按单字符 token 输出，
///         若需要 CJK 二元切分请改用 <c>SonnetDB.FullText.Tokenizers.Cjk</c>。</item>
/// </list>
/// </remarks>
public sealed class UnicodeTokenizer : ITokenizer
{
    /// <inheritdoc />
    public void Tokenize(ReadOnlySpan<char> text, ITokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int start = -1;
        int position = 0;
        Span<char> buffer = stackalloc char[64];

        for (int i = 0; i <= text.Length; i++)
        {
            bool isWord = i < text.Length && IsWordChar(text[i]);
            if (isWord)
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                EmitToken(text[start..i], start, i, ref position, buffer, sink);
                start = -1;
            }
        }
    }

    private static void EmitToken(
        ReadOnlySpan<char> token,
        int startOffset,
        int endOffset,
        ref int position,
        Span<char> buffer,
        ITokenSink sink)
    {
        position++;
        if (token.Length <= buffer.Length)
        {
            int written = token.ToLowerInvariant(buffer);
            if (written > 0)
            {
                sink.Emit(buffer[..written], startOffset, endOffset, 1);
                return;
            }
        }

        // 罕见的超长 token：回退到 string.ToLowerInvariant。
        string lower = token.ToString().ToLowerInvariant();
        sink.Emit(lower.AsSpan(), startOffset, endOffset, 1);
    }

    private static bool IsWordChar(char c)
    {
        UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat switch
        {
            UnicodeCategory.UppercaseLetter => true,
            UnicodeCategory.LowercaseLetter => true,
            UnicodeCategory.TitlecaseLetter => true,
            UnicodeCategory.ModifierLetter => true,
            UnicodeCategory.OtherLetter => true,
            UnicodeCategory.DecimalDigitNumber => true,
            UnicodeCategory.LetterNumber => true,
            UnicodeCategory.OtherNumber => true,
            UnicodeCategory.NonSpacingMark => true,
            _ => false,
        };
    }
}
