using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace DotSearch.Tokenizers.Jieba;

/// <summary>
/// 中文分词词典：词项 → 词频。
/// </summary>
/// <remarks>
/// 词典通过 embedded resource 加载，<see cref="Default"/> 第一次访问时懒加载。
/// AOT 友好：仅使用 <see cref="Assembly.GetManifestResourceStream(string)"/>，不依赖反射。
/// </remarks>
public sealed class ChineseDictionary
{
    private static readonly Lazy<ChineseDictionary> _default = new(LoadEmbedded);

    private readonly Dictionary<string, int> _frequencies;
    private readonly DoubleArrayTrie? _trie;
    private readonly long _totalFrequency;

    private ChineseDictionary(Dictionary<string, int> frequencies)
    {
        _frequencies = frequencies;
        long total = 0;
        foreach (int v in frequencies.Values)
        {
            total += v;
        }
        _totalFrequency = Math.Max(total, 1);
    }

    private ChineseDictionary(DoubleArrayTrie trie, int count, long totalFrequency, int maxTermLength)
    {
        _frequencies = new Dictionary<string, int>(0, StringComparer.Ordinal);
        _trie = trie;
        Count = count;
        _totalFrequency = Math.Max(totalFrequency, 1);
        MaxTermLength = maxTermLength;
    }

    /// <summary>
    /// 内嵌种子词典实例。
    /// </summary>
    public static ChineseDictionary Default => _default.Value;

    /// <summary>
    /// 词项总数。
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// 词项频次之和。
    /// </summary>
    public long TotalFrequency => _totalFrequency;

    /// <summary>
    /// 查询某个词项的频次；未登录词返回 0。
    /// </summary>
    public int GetFrequency(string term)
        => _trie is { } trie ? trie.GetFrequency(term.AsSpan()) : _frequencies.TryGetValue(term, out int v) ? v : 0;

    /// <summary>
    /// 是否已登录该词项。
    /// </summary>
    public bool Contains(string term) => GetFrequency(term) > 0;

    /// <summary>
    /// 词项最大长度（字符数）。
    /// </summary>
    public int MaxTermLength { get; private set; }

    private static ChineseDictionary LoadEmbedded()
    {
        Assembly asm = typeof(ChineseDictionary).Assembly;
        const string DatResourceName = "DotSearch.Tokenizers.Jieba.Resources.dict.dat";
        using (Stream? datStream = asm.GetManifestResourceStream(DatResourceName))
        {
            if (datStream is not null)
            {
                DoubleArrayTrie trie = DoubleArrayTrie.Read(datStream, out int count, out long totalFrequency, out int maxTermLength);
                return new ChineseDictionary(trie, count, totalFrequency, maxTermLength);
            }
        }

        const string ResourceName = "DotSearch.Tokenizers.Jieba.Resources.dict.txt";
        using Stream? stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using StreamReader reader = new(stream);

        Dictionary<string, int> map = new(StringComparer.Ordinal);
        int maxLen = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#')
            {
                continue;
            }

            int tab = span.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            string term = span[..tab].ToString();
            if (!int.TryParse(span[(tab + 1)..], out int freq) || freq <= 0)
            {
                freq = 1;
            }
            map[term] = freq;
            if (term.Length > maxLen)
            {
                maxLen = term.Length;
            }
        }

        ChineseDictionary dict = new(map) { Count = map.Count, MaxTermLength = maxLen };
        return dict;
    }
}
