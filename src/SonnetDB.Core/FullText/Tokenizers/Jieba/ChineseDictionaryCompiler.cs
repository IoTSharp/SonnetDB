using System.Buffers.Binary;
using System.Text;

namespace SonnetDB.FullText.Tokenizers.Jieba;

/// <summary>
/// Jieba 中文词典编译器，用于把一个或多个文本词库合并并编译为 SonnetDB DAT 词典。
/// </summary>
/// <remarks>
/// 支持的文本格式为 <c>词项&lt;TAB&gt;词频</c>、<c>词项 词频</c> 或 cppjieba 风格的
/// <c>词项 词频 词性</c>。词典变更会改变全文索引 token，应用到既有索引后需要重建全文索引。
/// </remarks>
public static class ChineseDictionaryCompiler
{
    internal const string MagicV2 = "DSDAT002";

    /// <summary>
    /// 从一个或多个文本词库加载并合并词项。
    /// </summary>
    /// <param name="dictionaryPaths">词库文件路径，顺序靠后的文件可覆盖前面文件中的同名词频。</param>
    /// <returns>合并后的词项与词频。</returns>
    public static IReadOnlyDictionary<string, int> LoadTerms(IEnumerable<string> dictionaryPaths)
    {
        ArgumentNullException.ThrowIfNull(dictionaryPaths);

        Dictionary<string, int> terms = new(StringComparer.Ordinal);
        foreach (string path in dictionaryPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            using FileStream stream = File.OpenRead(path);
            MergeTerms(stream, terms);
        }
        return terms;
    }

    /// <summary>
    /// 把一个或多个文本词库编译为 SonnetDB DAT 词典文件。
    /// </summary>
    /// <param name="dictionaryPaths">词库文件路径，顺序靠后的文件可覆盖前面文件中的同名词频。</param>
    /// <param name="outputPath">输出 DAT 文件路径。</param>
    /// <returns>编译后的词典统计信息。</returns>
    public static ChineseDictionaryCompileResult Compile(IEnumerable<string> dictionaryPaths, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(dictionaryPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        IReadOnlyDictionary<string, int> terms = LoadTerms(dictionaryPaths);
        return Compile(terms, outputPath);
    }

    /// <summary>
    /// 把内存中的词项集合编译为 SonnetDB DAT 词典文件。
    /// </summary>
    /// <param name="terms">词项与词频。</param>
    /// <param name="outputPath">输出 DAT 文件路径。</param>
    /// <returns>编译后的词典统计信息。</returns>
    public static ChineseDictionaryCompileResult Compile(IReadOnlyDictionary<string, int> terms, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        long totalFrequency = 0;
        int maxTermLength = 0;
        List<DictionaryTermEntry> entries = new(terms.Count);
        foreach (KeyValuePair<string, int> term in terms)
        {
            if (string.IsNullOrWhiteSpace(term.Key))
                continue;

            int frequency = term.Value <= 0 ? 1 : term.Value;
            byte[] utf8 = Encoding.UTF8.GetBytes(term.Key);
            entries.Add(new DictionaryTermEntry(term.Key, utf8, frequency));
            totalFrequency += frequency;
            maxTermLength = Math.Max(maxTermLength, term.Key.Length);
        }
        entries.Sort(static (x, y) => CompareBytes(x.Utf8, y.Utf8));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using FileStream stream = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.ASCII.GetBytes(MagicV2));
        WriteInt32(stream, entries.Count);
        WriteInt64(stream, totalFrequency);
        WriteInt32(stream, maxTermLength);
        int offset = 0;
        foreach (DictionaryTermEntry entry in entries)
        {
            WriteInt32(stream, offset);
            WriteInt32(stream, entry.Utf8.Length);
            WriteInt32(stream, entry.Frequency);
            offset += entry.Utf8.Length;
        }
        foreach (DictionaryTermEntry entry in entries)
            stream.Write(entry.Utf8);

        return new ChineseDictionaryCompileResult(entries.Count, totalFrequency, maxTermLength, entries.Count);
    }

    /// <summary>
    /// 从 DAT 文件加载词典。
    /// </summary>
    /// <param name="path">DAT 词典文件路径。</param>
    /// <returns>可直接传给 <see cref="ChineseTokenizer"/> 的中文词典。</returns>
    public static ChineseDictionary LoadCompiled(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return ChineseDictionary.ReadCompiled(stream);
    }

    internal static IReadOnlyDictionary<string, int> LoadTerms(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Dictionary<string, int> terms = new(StringComparer.Ordinal);
        MergeTerms(stream, terms);
        return terms;
    }

    internal static ChineseDictionaryCompileResult Compile(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        IReadOnlyDictionary<string, int> terms = LoadTerms(input);
        return WriteCompiled(terms, output);
    }

    internal static ChineseDictionaryCompileResult WriteCompiled(IReadOnlyDictionary<string, int> terms, Stream output)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(output);

        string tempPath = Path.GetTempFileName();
        try
        {
            ChineseDictionaryCompileResult result = Compile(terms, tempPath);
            using FileStream compiled = File.OpenRead(tempPath);
            compiled.CopyTo(output);
            return result;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static void MergeTerms(Stream stream, Dictionary<string, int> terms)
    {
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParseLine(line.AsSpan(), out string? term, out int frequency))
                terms[term] = frequency;
        }
    }

    private static bool TryParseLine(ReadOnlySpan<char> line, out string term, out int frequency)
    {
        ReadOnlySpan<char> span = line.Trim();
        term = string.Empty;
        frequency = 0;
        if (span.IsEmpty || span[0] == '#')
            return false;

        int separator = span.IndexOf('\t');
        if (separator < 0)
            separator = span.IndexOf(' ');
        if (separator <= 0)
            return false;

        ReadOnlySpan<char> termSpan = span[..separator].Trim();
        ReadOnlySpan<char> rest = span[(separator + 1)..].TrimStart();
        if (termSpan.IsEmpty || rest.IsEmpty)
            return false;

        int end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end]))
            end++;

        if (!int.TryParse(rest[..end], out frequency) || frequency <= 0)
            frequency = 1;

        term = termSpan.ToString();
        return true;
    }

    private static int CompareBytes(byte[] left, byte[] right)
        => left.AsSpan().SequenceCompareTo(right);

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private sealed record DictionaryTermEntry(string Term, byte[] Utf8, int Frequency);
}

/// <summary>
/// 中文词典编译结果统计。
/// </summary>
/// <param name="TermCount">词项数量。</param>
/// <param name="TotalFrequency">词频总和。</param>
/// <param name="MaxTermLength">最长词项字符数。</param>
/// <param name="NodeCount">DAT 节点数量。</param>
public readonly record struct ChineseDictionaryCompileResult(
    int TermCount,
    long TotalFrequency,
    int MaxTermLength,
    int NodeCount);
