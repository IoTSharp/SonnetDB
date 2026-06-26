using System.Globalization;
using SonnetDB.FullText.Tokenization;

namespace SonnetDB.FullText.Tokenizers.Cjk;

/// <summary>
/// CJK 二元（bigram）分词器：对 CJK 字符做相邻字符两两组合，对非 CJK 字符按
/// 空白/标点切分并整体小写输出。完全无词典、无外部依赖、AOT 友好。
/// </summary>
/// <remarks>
/// 适合 IoT / 嵌入式场景：包体积最小，召回稳定，缺点是精度低于词典型分词器。
/// </remarks>
public sealed class CjkBigramTokenizer : ITokenizer
{
    /// <inheritdoc />
    public void Tokenize(ReadOnlySpan<char> text, ITokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int latinStart = -1;
        int position = 0;
        Span<char> buffer = stackalloc char[64];

        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : '\0';
            bool isCjk = i < text.Length && IsCjk(c);
            bool isLatinWord = i < text.Length && !isCjk && IsWordChar(c);

            if (isLatinWord)
            {
                if (latinStart < 0)
                {
                    latinStart = i;
                }
                continue;
            }

            // 收尾上一个西文 token。
            if (latinStart >= 0)
            {
                EmitLowered(text[latinStart..i], latinStart, i, ref position, buffer, sink);
                latinStart = -1;
            }

            if (isCjk)
            {
                // 优先用相邻两字符组成 bigram。
                if (i + 1 < text.Length && IsCjk(text[i + 1]))
                {
                    position++;
                    sink.Emit(text.Slice(i, 2), i, i + 2, 1);
                }
                else if (i == 0 || !IsCjk(text[i - 1]))
                {
                    // 当前 CJK 字符无法形成 bigram 且不属于已发射 bigram 的尾字
                    // （前一字不是 CJK 或不存在）——这是孤立的 CJK 字符，发射 unigram。
                    // 反之（前一字是 CJK）则该字已被上一个 bigram 覆盖，不再单独发射，
                    // 保证查询端 "水泵" → [水泵] 与索引端 "水泵报警" → [水泵, 泵报, 报警]
                    // 的 token 集合在公共前缀上对齐，AND-of-tokens 检索不会被尾字单 token 误杀。
                    position++;
                    sink.Emit(text.Slice(i, 1), i, i + 1, 1);
                }
            }
        }
    }

    private static void EmitLowered(
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
        string lower = token.ToString().ToLowerInvariant();
        sink.Emit(lower.AsSpan(), startOffset, endOffset, 1);
    }

    private static bool IsCjk(char c)
    {
        // 覆盖 CJK Unified Ideographs、扩展 A、兼容、平假名、片假名、谚文音节等常用范围。
        return c switch
        {
            >= '\u3040' and <= '\u309F' => true,                       // Hiragana
            >= '\u30A0' and <= '\u30FF' => true,                       // Katakana
            >= '\u3400' and <= '\u4DBF' => true,                       // CJK Ext-A
            >= '\u4E00' and <= '\u9FFF' => true,                       // CJK Unified
            >= '\uAC00' and <= '\uD7AF' => true,                       // Hangul Syllables
            >= '\uF900' and <= '\uFAFF' => true,                       // CJK Compatibility
            _ => false,
        };
    }

    private static bool IsWordChar(char c)
    {
        UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat switch
        {
            UnicodeCategory.UppercaseLetter => true,
            UnicodeCategory.LowercaseLetter => true,
            UnicodeCategory.TitlecaseLetter => true,
            UnicodeCategory.OtherLetter => true,
            UnicodeCategory.ModifierLetter => true,
            UnicodeCategory.DecimalDigitNumber => true,
            UnicodeCategory.LetterNumber => true,
            UnicodeCategory.OtherNumber => true,
            _ => false,
        };
    }
}
