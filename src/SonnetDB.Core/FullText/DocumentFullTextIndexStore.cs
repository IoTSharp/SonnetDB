using System.Globalization;
using System.Text.Json;
using DotSearch.Index;
using DotSearch.Query;
using DotSearch.Storage;
using DotSearch.Tokenization;
using DotSearch.Tokenizers.Cjk;
using DotSearch.Tokenizers.Jieba;
using DotSearch.Tokenizers.Unicode;
using SonnetDB.Documents;

namespace SonnetDB.FullText;

/// <summary>
/// 文档集合全文索引的 DotSearch-backed 派生索引。
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

    /// <summary>
    /// 打开全文索引目录。
    /// </summary>
    /// <param name="directory">DotSearch 持久化目录。</param>
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
    /// 搜索全文索引。
    /// </summary>
    /// <param name="field">索引字段名，或 <c>*</c> 表示全部字段。</param>
    /// <param name="queryText">查询文本。</param>
    /// <param name="topK">返回前 K 条。</param>
    public IReadOnlyList<DocumentFullTextSearchHit> Search(string field, string queryText, int topK)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        if (topK <= 0)
            return [];

        lock (_sync)
        {
            DotSearch.Query.Query query = BuildQuery(field, queryText);
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
        lock (_sync)
        {
            foreach (var row in rows)
                _index.Index(BuildDocument(_definition, row));
        }
    }

    private DotSearch.Query.Query BuildQuery(string field, string queryText)
    {
        string[] tokens = Tokenize(queryText, _definition.Tokenizer);
        if (tokens.Length == 0)
            tokens = [queryText.ToLowerInvariant()];

        if (field == "*")
        {
            var fieldQueries = new DotSearch.Query.Query[_definition.Fields.Count];
            for (int i = 0; i < _definition.Fields.Count; i++)
                fieldQueries[i] = BuildFieldQuery(_definition.Fields[i], tokens);
            return fieldQueries.Length == 1 ? fieldQueries[0] : new OrQuery(fieldQueries);
        }

        string normalizedField = NormalizeField(field);
        if (!_definition.Fields.Contains(normalizedField, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"全文索引 '{_definition.Name}' 不包含字段 '{normalizedField}'。");
        }

        return BuildFieldQuery(normalizedField, tokens);
    }

    private static DotSearch.Query.Query BuildFieldQuery(string field, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 1)
            return new TermQuery(field, tokens[0]);

        var clauses = new DotSearch.Query.Query[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
            clauses[i] = new TermQuery(field, tokens[i]);
        return new AndQuery(clauses);
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
