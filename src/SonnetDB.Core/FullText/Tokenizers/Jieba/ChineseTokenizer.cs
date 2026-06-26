using System;
using SonnetDB.FullText.Tokenization;

namespace SonnetDB.FullText.Tokenizers.Jieba;

/// <summary>
/// 基于词典 + 最大概率动态规划的中文分词器。
/// </summary>
/// <remarks>
/// 算法概述：
/// <list type="number">
///   <item>把输入按 CJK / 非 CJK 切成连续片段。</item>
///   <item>对 CJK 片段，用前向最大匹配生成候选 DAG，并用后向 DP
///         按 log 频率求最大概率切分。</item>
///   <item>未登录字符按单字符输出。</item>
///   <item>非 CJK 片段沿用 Unicode 词边界，整体小写输出。</item>
/// </list>
/// 词典默认使用 <see cref="ChineseDictionary.Default"/>（embedded resource）。
/// </remarks>
public sealed class ChineseTokenizer : ITokenizer
{
    private readonly ChineseDictionary _dictionary;

    /// <summary>
    /// 用默认内嵌词典构造分词器。
    /// </summary>
    public ChineseTokenizer() : this(ChineseDictionary.Default) { }

    /// <summary>
    /// 用指定词典构造分词器。
    /// </summary>
    public ChineseTokenizer(ChineseDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        _dictionary = dictionary;
    }

    /// <inheritdoc />
    public void Tokenize(ReadOnlySpan<char> text, ITokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (IsCjk(c))
            {
                int j = i;
                while (j < text.Length && IsCjk(text[j]))
                {
                    j++;
                }
                SegmentCjk(text[i..j], i, sink);
                i = j;
            }
            else if (IsLatinWord(c))
            {
                int j = i;
                while (j < text.Length && IsLatinWord(text[j]))
                {
                    j++;
                }
                EmitLatin(text[i..j], i, j, sink);
                i = j;
            }
            else
            {
                // 跳过空白与标点。
                i++;
            }
        }
    }

    private void SegmentCjk(ReadOnlySpan<char> segment, int absoluteStart, ITokenSink sink)
    {
        int n = segment.Length;
        if (n == 0)
        {
            return;
        }

        // route[i] = (logProb, nextIndex)
        Span<double> bestProb = stackalloc double[n + 1];
        Span<int> nextIdx = stackalloc int[n + 1];
        bestProb[n] = 0.0;
        nextIdx[n] = n;

        double logTotal = Math.Log(_dictionary.TotalFrequency);
        int maxLen = Math.Max(1, _dictionary.MaxTermLength);

        for (int idx = n - 1; idx >= 0; idx--)
        {
            double best = double.NegativeInfinity;
            int bestNext = idx + 1;
            int upper = Math.Min(n, idx + maxLen);
            for (int end = idx + 1; end <= upper; end++)
            {
                ReadOnlySpan<char> candidate = segment[idx..end];
                int freq = _dictionary.GetFrequency(candidate.ToString());
                double logFreq;
                if (freq > 0)
                {
                    logFreq = Math.Log(freq) - logTotal;
                }
                else if (end - idx == 1)
                {
                    // 未登录单字：给一个很小的概率，保证仍可成词。
                    logFreq = Math.Log(1.0) - logTotal;
                }
                else
                {
                    continue;
                }

                double prob = logFreq + bestProb[end];
                if (prob > best)
                {
                    best = prob;
                    bestNext = end;
                }
            }
            bestProb[idx] = best;
            nextIdx[idx] = bestNext;
        }

        int p = 0;
        while (p < n)
        {
            int next = nextIdx[p];
            sink.Emit(segment[p..next], absoluteStart + p, absoluteStart + next, 1);
            p = next;
        }
    }

    private static void EmitLatin(ReadOnlySpan<char> token, int startOffset, int endOffset, ITokenSink sink)
    {
        Span<char> buffer = stackalloc char[64];
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

    private static bool IsCjk(char c) => c switch
    {
        >= '\u3400' and <= '\u4DBF' => true,
        >= '\u4E00' and <= '\u9FFF' => true,
        >= '\uF900' and <= '\uFAFF' => true,
        _ => false,
    };

    private static bool IsLatinWord(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
}
