using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SonnetDB.FullText.Tokenizers.Jieba;

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
    private readonly CompactDictionary? _compact;
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

    private ChineseDictionary(CompactDictionary compact, int count, long totalFrequency, int maxTermLength)
    {
        _frequencies = new Dictionary<string, int>(0, StringComparer.Ordinal);
        _compact = compact;
        Count = count;
        _totalFrequency = Math.Max(totalFrequency, 1);
        MaxTermLength = maxTermLength;
    }

    /// <summary>
    /// 默认内嵌词典实例。
    /// </summary>
    public static ChineseDictionary Default => _default.Value;

    /// <summary>
    /// 从一个或多个 Jieba 风格文本词库创建中文词典。
    /// </summary>
    /// <param name="dictionaryPaths">词库文件路径，顺序靠后的文件可覆盖前面文件中的同名词频。</param>
    /// <returns>合并后的中文词典。</returns>
    /// <remarks>
    /// 支持 <c>词项&lt;TAB&gt;词频</c>、<c>词项 词频</c> 和 cppjieba 的 <c>词项 词频 词性</c> 格式。
    /// 词典变更会改变全文索引 token；对既有索引应用新词典后需要重建全文索引。
    /// </remarks>
    public static ChineseDictionary FromTextFiles(params string[] dictionaryPaths)
        => FromTextFiles((IEnumerable<string>)dictionaryPaths);

    /// <summary>
    /// 从一个或多个 Jieba 风格文本词库创建中文词典。
    /// </summary>
    /// <param name="dictionaryPaths">词库文件路径，顺序靠后的文件可覆盖前面文件中的同名词频。</param>
    /// <returns>合并后的中文词典。</returns>
    /// <remarks>
    /// 支持 <c>词项&lt;TAB&gt;词频</c>、<c>词项 词频</c> 和 cppjieba 的 <c>词项 词频 词性</c> 格式。
    /// 词典变更会改变全文索引 token；对既有索引应用新词典后需要重建全文索引。
    /// </remarks>
    public static ChineseDictionary FromTextFiles(IEnumerable<string> dictionaryPaths)
    {
        var terms = ChineseDictionaryCompiler.LoadTerms(dictionaryPaths);
        return FromTerms(terms);
    }

    /// <summary>
    /// 从已编译的 SonnetDB DAT 词典文件创建中文词典。
    /// </summary>
    /// <param name="compiledDictionaryPath">DAT 词典文件路径。</param>
    /// <returns>中文词典。</returns>
    public static ChineseDictionary FromCompiledFile(string compiledDictionaryPath)
        => ChineseDictionaryCompiler.LoadCompiled(compiledDictionaryPath);

    /// <summary>
    /// 从词项集合创建中文词典。
    /// </summary>
    /// <param name="terms">词项与词频。</param>
    /// <returns>中文词典。</returns>
    public static ChineseDictionary FromTerms(IReadOnlyDictionary<string, int> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        Dictionary<string, int> map = new(terms, StringComparer.Ordinal);
        int maxLen = 0;
        foreach (string term in map.Keys)
            maxLen = Math.Max(maxLen, term.Length);
        return new ChineseDictionary(map) { Count = map.Count, MaxTermLength = maxLen };
    }

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
    {
        if (_compact is { } compact)
            return compact.GetFrequency(term.AsSpan());
        if (_trie is { } trie)
            return trie.GetFrequency(term.AsSpan());
        return _frequencies.TryGetValue(term, out int v) ? v : 0;
    }

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
        const string DatResourceName = "SonnetDB.FullText.Tokenizers.Jieba.Resources.dict.dat";
        using (Stream? datStream = asm.GetManifestResourceStream(DatResourceName))
        {
            if (datStream is not null)
            {
                return ReadCompiled(datStream);
            }
        }

        const string ResourceName = "SonnetDB.FullText.Tokenizers.Jieba.Resources.dict.txt";
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

    internal static ChineseDictionary ReadCompiled(Stream stream)
    {
        byte[] data;
        using (MemoryStream ms = new())
        {
            stream.CopyTo(ms);
            data = ms.ToArray();
        }

        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual("DSDAT002"u8))
        {
            CompactDictionary compact = CompactDictionary.Read(data, out int count, out long totalFrequency, out int maxTermLength);
            return new ChineseDictionary(compact, count, totalFrequency, maxTermLength);
        }

        using MemoryStream legacy = new(data);
        DoubleArrayTrie trie = DoubleArrayTrie.Read(legacy, out int legacyCount, out long legacyTotalFrequency, out int legacyMaxTermLength);
        return new ChineseDictionary(trie, legacyCount, legacyTotalFrequency, legacyMaxTermLength);
    }
}
