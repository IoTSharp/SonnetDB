using System.Buffers;
using System.Globalization;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.FullText.Index;
using SonnetDB.FullText.Query;
using SonnetDB.FullText.Storage;
using SonnetDB.FullText.Tokenization;
using SonnetDB.FullText.Tokenizers.Cjk;
using SonnetDB.FullText.Tokenizers.Jieba;
using SonnetDB.FullText.Tokenizers.Unicode;

namespace SonnetDB.FullText;

/// <summary>
/// 文档集合全文索引的 SonnetDB-backed 派生索引。
/// </summary>
public sealed class DocumentFullTextIndexStore
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly DocumentFullTextIndex _definition;
    private readonly PersistentFullTextIndex _index;

    private DocumentFullTextIndexStore(
        string directory,
        DocumentFullTextIndex definition,
        PersistentFullTextIndex index)
    {
        _directory = directory;
        _definition = definition;
        _index = index;
    }

    /// <summary>索引声明。</summary>
    public DocumentFullTextIndex Definition => _definition;

    /// <summary>当前可见文档总数。</summary>
    public int DocumentCount
    {
        get
        {
            lock (_sync)
                return _index.DocumentCount;
        }
    }

    /// <summary>当前索引中可见字段词项总数，按字段和 term 去重。</summary>
    public int TermCount
    {
        get
        {
            lock (_sync)
            {
                IReadOnlyDictionary<string, IReadOnlyCollection<string>> snapshots =
                    _index.SnapshotActiveTerms(_definition.Fields);
                int count = 0;
                foreach (var terms in snapshots.Values)
                    count += terms.Count;
                return count;
            }
        }
    }

    /// <summary>活跃 term 快照实际重建次数，供性能回归测试和基准校验。</summary>
    internal long ActiveTermSnapshotBuildCount => _index.ActiveTermSnapshotBuildCount;

    /// <summary>
    /// 打开全文索引目录。
    /// </summary>
    /// <param name="directory">SonnetDB 全文索引持久化目录。</param>
    /// <param name="definition">全文索引声明。</param>
    public static DocumentFullTextIndexStore Open(string directory, DocumentFullTextIndex definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(definition);

        Directory.CreateDirectory(directory);
        var index = PersistentFullTextIndex.Open(
            directory,
            CreateTokenizer(definition.Tokenizer),
            options: new PersistentIndexOptions { EnableBackgroundMerge = false });
        return new DocumentFullTextIndexStore(directory, definition, index);
    }

    /// <summary>
    /// 将一条文档记录写入全文索引。
    /// </summary>
    /// <param name="row">文档记录。</param>
    public void Upsert(DocumentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_sync)
            _index.Index(BuildDocument(_definition, row));
    }

    /// <summary>
    /// 批量将文档记录写入全文索引：整批构建为单个段，manifest 只落盘一次。
    /// </summary>
    /// <param name="rows">文档记录序列。</param>
    public void UpsertMany(IEnumerable<DocumentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var documents = new List<Document>();
        foreach (var row in rows)
            documents.Add(BuildDocument(_definition, row));
        if (documents.Count == 0)
            return;
        lock (_sync)
            _index.IndexMany(documents);
    }

    /// <summary>
    /// 从全文索引删除一条文档。
    /// </summary>
    /// <param name="id">文档 ID。</param>
    public void Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_sync)
            _index.Delete(new DocumentId(id));
    }

    /// <summary>
    /// 批量从全文索引删除文档，manifest 只落盘一次。
    /// </summary>
    /// <param name="ids">文档 ID 序列。</param>
    public void DeleteMany(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_sync)
            _index.DeleteMany(ids.Select(static id => new DocumentId(id)));
    }

    /// <summary>
    /// 搜索全文索引。
    /// </summary>
    /// <param name="field">索引字段名，或 <c>*</c> 表示全部字段。</param>
    /// <param name="queryText">查询文本。</param>
    /// <param name="topK">返回前 K 条。</param>
    public IReadOnlyList<DocumentFullTextSearchHit> Search(string field, string queryText, int topK)
        => Search(field, queryText, topK, FullTextSearchMode.Exact);

    /// <summary>
    /// 搜索全文索引，支持显式的检索模式（exact / fuzzy）。
    /// </summary>
    /// <param name="field">索引字段名，或 <c>*</c> 表示全部字段。</param>
    /// <param name="queryText">查询文本。</param>
    /// <param name="topK">返回前 K 条。</param>
    /// <param name="mode">检索模式；fuzzy 模式按 Damerau-Levenshtein 距离展开候选 term。</param>
    public IReadOnlyList<DocumentFullTextSearchHit> Search(string field, string queryText, int topK, FullTextSearchMode mode)
        => Search(field, queryText, topK, mode, FullTextQueryKind.All);

    /// <summary>
    /// 搜索全文索引，支持精确/模糊模式与前端 playground 的查询组合方式。
    /// </summary>
    /// <param name="field">索引字段名，或 <c>*</c> 表示全部字段。</param>
    /// <param name="queryText">查询文本。</param>
    /// <param name="topK">返回前 K 条。</param>
    /// <param name="mode">检索模式；fuzzy 模式按 Damerau-Levenshtein 距离展开候选 term。</param>
    /// <param name="queryKind">查询组合方式：全部词、任意词或短语。</param>
    public IReadOnlyList<DocumentFullTextSearchHit> Search(
        string field,
        string queryText,
        int topK,
        FullTextSearchMode mode,
        FullTextQueryKind queryKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        if (topK <= 0)
            return [];

        lock (_sync)
        {
            SonnetDB.FullText.Query.Query query = BuildQuery(field, queryText, mode, queryKind);
            var hits = _index.Search(query, topK);
            var result = new DocumentFullTextSearchHit[hits.Count];
            for (int i = 0; i < hits.Count; i++)
                result[i] = new DocumentFullTextSearchHit(hits[i].DocumentId.Value, hits[i].Score);
            return result;
        }
    }

    /// <summary>
    /// 重建索引目录。
    /// </summary>
    /// <param name="rows">要重建的文档快照。</param>
    public void Rebuild(IEnumerable<DocumentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        UpsertMany(rows);
    }

    private SonnetDB.FullText.Query.Query BuildQuery(
        string field,
        string queryText,
        FullTextSearchMode mode,
        FullTextQueryKind queryKind)
    {
        string[] tokens = Tokenize(queryText, _definition.Tokenizer);
        if (tokens.Length == 0)
            tokens = [queryText.ToLowerInvariant()];

        if (field == "*")
        {
            IReadOnlyDictionary<string, IReadOnlyCollection<string>>? activeTerms =
                mode == FullTextSearchMode.Fuzzy
                    ? _index.SnapshotActiveTerms(_definition.Fields)
                    : null;
            var fieldQueries = new SonnetDB.FullText.Query.Query[_definition.Fields.Count];
            for (int i = 0; i < _definition.Fields.Count; i++)
            {
                string fieldName = _definition.Fields[i];
                fieldQueries[i] = BuildFieldQuery(
                    fieldName,
                    tokens,
                    mode,
                    queryKind,
                    activeTerms is null ? null : activeTerms[fieldName]);
            }
            return fieldQueries.Length == 1 ? fieldQueries[0] : new OrQuery(fieldQueries);
        }

        string normalizedField = NormalizeField(field);
        if (!_definition.Fields.Contains(normalizedField, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"全文索引 '{_definition.Name}' 不包含字段 '{normalizedField}'。");
        }

        IReadOnlyCollection<string>? fieldTerms = mode == FullTextSearchMode.Fuzzy
            ? _index.SnapshotActiveTerms([normalizedField])[normalizedField]
            : null;
        return BuildFieldQuery(normalizedField, tokens, mode, queryKind, fieldTerms);
    }

    private SonnetDB.FullText.Query.Query BuildFieldQuery(
        string field,
        IReadOnlyList<string> tokens,
        FullTextSearchMode mode,
        FullTextQueryKind queryKind,
        IReadOnlyCollection<string>? activeTerms)
    {
        if (queryKind == FullTextQueryKind.Phrase)
            return tokens.Count == 1 ? new TermQuery(field, tokens[0]) : new PhraseQuery(field, tokens);

        if (mode == FullTextSearchMode.Fuzzy)
        {
            var expanded = new SonnetDB.FullText.Query.Query[tokens.Count];
            for (int i = 0; i < tokens.Count; i++)
                expanded[i] = ExpandFuzzyTermQuery(field, tokens[i], activeTerms!);
            return tokens.Count == 1
                ? expanded[0]
                : queryKind == FullTextQueryKind.Any ? new OrQuery(expanded) : new AndQuery(expanded);
        }

        if (tokens.Count == 1)
            return new TermQuery(field, tokens[0]);

        var clauses = new SonnetDB.FullText.Query.Query[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
            clauses[i] = new TermQuery(field, tokens[i]);
        return queryKind == FullTextQueryKind.Any ? new OrQuery(clauses) : new AndQuery(clauses);
    }

    /// <summary>
    /// 把单个查询 term 展开为字段下所有 Damerau-Levenshtein 距离 ≤ 阈值的索引 term 的 OR。
    /// 阈值随 term 长度递增：≤2 字符要求精确（容错距离 0），3-4 字符容 1 编辑，≥5 字符容 2 编辑。
    /// 若展开后无候选，回退到原 term 的 TermQuery，至少不会引入误判。
    /// </summary>
    private static SonnetDB.FullText.Query.Query ExpandFuzzyTermQuery(
        string field,
        string queryToken,
        IReadOnlyCollection<string> activeTerms)
    {
        int maxEdits = ComputeFuzzyMaxEdits(queryToken);
        if (maxEdits == 0)
            return new TermQuery(field, queryToken);

        var matches = new List<string>();
        foreach (string term in activeTerms)
        {
            // 长度差超过 maxEdits 时编辑距离一定 > maxEdits，直接剪枝避免 O(N²) DP。
            if (Math.Abs(term.Length - queryToken.Length) > maxEdits)
                continue;
            int dist = DamerauLevenshtein.Distance(queryToken, term, maxEdits);
            if (dist <= maxEdits)
                matches.Add(term);
        }

        if (matches.Count == 0)
            return new TermQuery(field, queryToken);
        if (matches.Count == 1)
            return new TermQuery(field, matches[0]);

        var clauses = new SonnetDB.FullText.Query.Query[matches.Count];
        for (int i = 0; i < matches.Count; i++)
            clauses[i] = new TermQuery(field, matches[i]);
        return new OrQuery(clauses);
    }

    private static int ComputeFuzzyMaxEdits(string token)
    {
        if (token.Length <= 2) return 0;
        if (token.Length <= 4) return 1;
        return 2;
    }

    private static Document BuildDocument(DocumentFullTextIndex definition, DocumentRow row)
    {
        var document = new Document(new DocumentId(row.Id));
        using var json = JsonDocument.Parse(row.Json);
        foreach (string field in definition.Fields)
            document.Set(field, ExtractFieldValue(field, row.Json, json.RootElement));
        return document;
    }

    private static string ExtractFieldValue(string field, string rawJson, JsonElement root)
    {
        if (IsDocumentField(field))
            return rawJson;

        try
        {
            var path = JsonPath.Parse(field);
            if (!JsonPathEvaluator.TryResolve(root, path, out var element))
                return string.Empty;

            return element.ValueKind switch
            {
                JsonValueKind.Null => string.Empty,
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
                _ => string.Empty,
            };
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"全文索引字段 '{field}' 不是 document/json 伪列，也不是有效 JSON path。");
        }
    }

    private static string[] Tokenize(string text, string tokenizerName)
    {
        var tokenizer = CreateTokenizer(tokenizerName);
        var sink = new CollectingTokenSink();
        tokenizer.Tokenize(text.AsSpan(), sink);
        return sink.Tokens
            .Select(static token => token.Text)
            .Where(static token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static ITokenizer CreateTokenizer(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "unicode" => new UnicodeTokenizer(),
            "cjk" or "cjk_bigram" or "bigram" => new CjkBigramTokenizer(),
            "jieba" or "chinese" => new ChineseTokenizer(),
            _ => throw new InvalidOperationException(
                $"未知全文分词器 '{name}'，支持 unicode / cjk / jieba。"),
        };
    }

    private static bool IsDocumentField(string field)
        => string.Equals(field, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field, "json", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeField(string field)
    {
        if (string.Equals(field, "document", StringComparison.OrdinalIgnoreCase))
            return "document";
        if (string.Equals(field, "json", StringComparison.OrdinalIgnoreCase))
            return "json";

        try
        {
            return JsonPath.Parse(field).Text;
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"全文索引字段 '{field}' 不是 document/json 伪列，也不是有效 JSON path。");
        }
    }
}

/// <summary>全文检索模式：精确匹配 vs 拼写容错（fuzzy / typo-tolerant）。</summary>
public enum FullTextSearchMode
{
    /// <summary>精确：每个查询 token 必须存在于索引中（默认）。</summary>
    Exact,
    /// <summary>容错：每个查询 token 展开为编辑距离 ≤ 阈值的全部索引 term 的 OR。</summary>
    Fuzzy,
}

/// <summary>
/// 全文检索查询组合方式，用于管理端 playground 在不改变索引语义的前提下构造常见查询。
/// </summary>
public enum FullTextQueryKind
{
    /// <summary>全部词项都必须匹配。</summary>
    All,

    /// <summary>任意词项匹配即可。</summary>
    Any,

    /// <summary>按 token 位置连续匹配短语。</summary>
    Phrase,
}

/// <summary>Damerau-Levenshtein 距离（带相邻字符 transposition）的剪枝实现。</summary>
internal static class DamerauLevenshtein
{
    /// <summary>
    /// 计算 <paramref name="a"/> 与 <paramref name="b"/> 之间的 Damerau-Levenshtein 距离。
    /// 当行内最小值已超过 <paramref name="maxDistance"/> 时提前返回 <c>maxDistance + 1</c>
    /// 作为剪枝信号——上层只需要"是否 ≤ maxDistance"的二值判断，无需精确距离。
    /// </summary>
    public static int Distance(string a, string b, int maxDistance)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (b is null) throw new ArgumentNullException(nameof(b));
        if (maxDistance < 0) throw new ArgumentOutOfRangeException(nameof(maxDistance));

        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > maxDistance) return maxDistance + 1;
        if (la == 0) return lb;
        if (lb == 0) return la;

        // 相邻交换只依赖当前行、前一行和前两行。短 term 使用栈缓冲，
        // 长 term 租用数组，避免 fuzzy 枚举为每个候选分配二维矩阵。
        int rowLength = lb + 1;
        int bufferLength = checked(rowLength * 3);
        int[]? rented = null;
        Span<int> buffer = bufferLength <= 384
            ? stackalloc int[bufferLength]
            : (rented = ArrayPool<int>.Shared.Rent(bufferLength)).AsSpan(0, bufferLength);
        try
        {
            Span<int> previousPrevious = buffer[..rowLength];
            Span<int> previous = buffer.Slice(rowLength, rowLength);
            Span<int> current = buffer.Slice(rowLength * 2, rowLength);
            for (int j = 0; j <= lb; j++)
                previous[j] = j;

            for (int i = 1; i <= la; i++)
            {
                current[0] = i;
                int rowMin = current[0];
                for (int j = 1; j <= lb; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int min = Math.Min(
                        Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        min = Math.Min(min, previousPrevious[j - 2] + 1);
                    current[j] = min;
                    if (min < rowMin) rowMin = min;
                }
                if (rowMin > maxDistance)
                    return maxDistance + 1;

                Span<int> swap = previousPrevious;
                previousPrevious = previous;
                previous = current;
                current = swap;
            }

            return previous[lb];
        }
        finally
        {
            if (rented is not null)
                ArrayPool<int>.Shared.Return(rented);
        }
    }
}

/// <summary>
/// 文档全文检索命中。
/// </summary>
/// <param name="DocumentId">文档 ID。</param>
/// <param name="Score">BM25 分数，越大越相关。</param>
public readonly record struct DocumentFullTextSearchHit(string DocumentId, double Score)
{
    /// <summary>格式化后的分数。</summary>
    public string FormatScore()
        => Score.ToString("G17", CultureInfo.InvariantCulture);
}
