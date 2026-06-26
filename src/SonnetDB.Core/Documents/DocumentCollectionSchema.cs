using System.Collections.Frozen;

namespace SonnetDB.Documents;

/// <summary>
/// JSON 文档集合 schema，包含集合名称、创建时间与 JSON path 索引声明。
/// </summary>
public sealed class DocumentCollectionSchema
{
    private readonly FrozenDictionary<string, DocumentPathIndex> _indexesByName;
    private readonly FrozenDictionary<string, DocumentFullTextIndex> _fullTextIndexesByName;

    private DocumentCollectionSchema(
        string name,
        IReadOnlyList<DocumentPathIndex> indexes,
        IReadOnlyList<DocumentFullTextIndex> fullTextIndexes,
        long createdAtUtcTicks)
    {
        Name = name;
        Indexes = indexes;
        FullTextIndexes = fullTextIndexes;
        CreatedAtUtcTicks = createdAtUtcTicks;
        _indexesByName = indexes.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal);
        _fullTextIndexesByName = fullTextIndexes.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal);
    }

    /// <summary>文档集合名称。</summary>
    public string Name { get; }

    /// <summary>按创建顺序排列的 JSON path 索引声明。</summary>
    public IReadOnlyList<DocumentPathIndex> Indexes { get; }

    /// <summary>按创建顺序排列的全文索引声明。</summary>
    public IReadOnlyList<DocumentFullTextIndex> FullTextIndexes { get; }

    /// <summary>创建时间 UTC ticks。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>
    /// 创建并校验文档集合 schema。
    /// </summary>
    /// <param name="name">集合名称。</param>
    /// <param name="indexes">JSON path 索引声明。</param>
    /// <param name="createdAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
    public static DocumentCollectionSchema Create(
        string name,
        IReadOnlyList<DocumentPathIndexDefinition>? indexes = null,
        IReadOnlyList<DocumentFullTextIndexDefinition>? fullTextIndexes = null,
        long createdAtUtcTicks = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var indexList = new List<DocumentPathIndex>();
        if (indexes is not null)
        {
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var index in indexes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(index.Name);
                if (!seenNames.Add(index.Name))
                    throw new ArgumentException($"文档集合 '{name}' 中索引 '{index.Name}' 重复。", nameof(indexes));

                var path = JsonPath.Parse(index.Path);
                indexList.Add(new DocumentPathIndex(
                    index.Name,
                    path.Text,
                    index.CreatedAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : index.CreatedAtUtcTicks));
            }
        }

        var fullTextIndexList = new List<DocumentFullTextIndex>();
        if (fullTextIndexes is not null)
        {
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var index in fullTextIndexes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(index.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(index.Tokenizer);
                if (!seenNames.Add(index.Name))
                    throw new ArgumentException($"文档集合 '{name}' 中全文索引 '{index.Name}' 重复。", nameof(fullTextIndexes));
                if (index.Fields.Count == 0)
                    throw new ArgumentException($"文档集合 '{name}' 的全文索引 '{index.Name}' 至少需要一个字段。", nameof(fullTextIndexes));

                var fields = new string[index.Fields.Count];
                var seenFields = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < index.Fields.Count; i++)
                {
                    string field = NormalizeFullTextField(index.Fields[i]);
                    if (!seenFields.Add(field))
                        throw new ArgumentException($"文档集合 '{name}' 的全文索引 '{index.Name}' 中字段 '{field}' 重复。", nameof(fullTextIndexes));
                    fields[i] = field;
                }

                fullTextIndexList.Add(new DocumentFullTextIndex(
                    index.Name,
                    Array.AsReadOnly(fields),
                    index.Tokenizer,
                    index.CreatedAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : index.CreatedAtUtcTicks));
            }
        }

        return new DocumentCollectionSchema(
            name,
            indexList.AsReadOnly(),
            fullTextIndexList.AsReadOnly(),
            createdAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : createdAtUtcTicks);
    }

    /// <summary>
    /// 尝试按索引名查找 JSON path 索引声明。
    /// </summary>
    /// <param name="name">索引名。</param>
    /// <returns>找到时返回索引声明；否则返回 null。</returns>
    public DocumentPathIndex? TryGetIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _indexesByName.TryGetValue(name, out var index) ? index : null;
    }

    /// <summary>
    /// 尝试按索引名查找全文索引声明。
    /// </summary>
    /// <param name="name">索引名。</param>
    /// <returns>找到时返回索引声明；否则返回 null。</returns>
    public DocumentFullTextIndex? TryGetFullTextIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _fullTextIndexesByName.TryGetValue(name, out var index) ? index : null;
    }

    /// <summary>
    /// 返回添加指定 JSON path 索引后的新 schema。
    /// </summary>
    /// <param name="definition">索引声明。</param>
    public DocumentCollectionSchema WithIndex(DocumentPathIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_indexesByName.ContainsKey(definition.Name))
            throw new InvalidOperationException($"document collection '{Name}' 中索引 '{definition.Name}' 已存在。");

        var definitions = Indexes
            .Select(static i => new DocumentPathIndexDefinition(i.Name, i.Path, i.CreatedAtUtcTicks))
            .Append(definition)
            .ToArray();

        return Create(Name, definitions, FullTextIndexDefinitions(), CreatedAtUtcTicks);
    }

    /// <summary>
    /// 返回删除指定索引后的新 schema。
    /// </summary>
    /// <param name="indexName">索引名。</param>
    public DocumentCollectionSchema WithoutIndex(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        if (!_indexesByName.ContainsKey(indexName))
            return this;

        var definitions = Indexes
            .Where(i => !string.Equals(i.Name, indexName, StringComparison.Ordinal))
            .Select(static i => new DocumentPathIndexDefinition(i.Name, i.Path, i.CreatedAtUtcTicks))
            .ToArray();

        return Create(Name, definitions, FullTextIndexDefinitions(), CreatedAtUtcTicks);
    }

    /// <summary>
    /// 返回添加指定全文索引后的新 schema。
    /// </summary>
    /// <param name="definition">全文索引声明。</param>
    public DocumentCollectionSchema WithFullTextIndex(DocumentFullTextIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_fullTextIndexesByName.ContainsKey(definition.Name))
            throw new InvalidOperationException($"document collection '{Name}' 中全文索引 '{definition.Name}' 已存在。");

        var definitions = FullTextIndexes
            .Select(static i => new DocumentFullTextIndexDefinition(i.Name, i.Fields, i.Tokenizer, i.CreatedAtUtcTicks))
            .Append(definition)
            .ToArray();

        return Create(Name, PathIndexDefinitions(), definitions, CreatedAtUtcTicks);
    }

    /// <summary>
    /// 返回删除指定全文索引后的新 schema。
    /// </summary>
    /// <param name="indexName">索引名。</param>
    public DocumentCollectionSchema WithoutFullTextIndex(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        if (!_fullTextIndexesByName.ContainsKey(indexName))
            return this;

        var definitions = FullTextIndexes
            .Where(i => !string.Equals(i.Name, indexName, StringComparison.Ordinal))
            .Select(static i => new DocumentFullTextIndexDefinition(i.Name, i.Fields, i.Tokenizer, i.CreatedAtUtcTicks))
            .ToArray();

        return Create(Name, PathIndexDefinitions(), definitions, CreatedAtUtcTicks);
    }

    private IReadOnlyList<DocumentPathIndexDefinition> PathIndexDefinitions()
        => Indexes
            .Select(static i => new DocumentPathIndexDefinition(i.Name, i.Path, i.CreatedAtUtcTicks))
            .ToArray();

    private IReadOnlyList<DocumentFullTextIndexDefinition> FullTextIndexDefinitions()
        => FullTextIndexes
            .Select(static i => new DocumentFullTextIndexDefinition(i.Name, i.Fields, i.Tokenizer, i.CreatedAtUtcTicks))
            .ToArray();

    private static string NormalizeFullTextField(string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (string.Equals(field, "document", StringComparison.OrdinalIgnoreCase))
            return "document";
        if (string.Equals(field, "json", StringComparison.OrdinalIgnoreCase))
            return "json";

        return JsonPath.Parse(field).Text;
    }
}

/// <summary>
/// JSON 文档集合 path 索引声明。
/// </summary>
/// <param name="Name">索引名，在单集合内唯一。</param>
/// <param name="Path">JSON path 表达式。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks。</param>
public sealed record DocumentPathIndex(
    string Name,
    string Path,
    long CreatedAtUtcTicks);

/// <summary>
/// 创建或加载 JSON path 索引时使用的轻量声明。
/// </summary>
/// <param name="Name">索引名。</param>
/// <param name="Path">JSON path 表达式。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
public sealed record DocumentPathIndexDefinition(
    string Name,
    string Path,
    long CreatedAtUtcTicks = 0);

/// <summary>
/// JSON 文档集合全文索引声明。
/// </summary>
/// <param name="Name">索引名，在单集合内唯一。</param>
/// <param name="Fields">写入 SonnetDB 全文文档的字段列表；支持 <c>document</c> / <c>json</c> 和 JSON path。</param>
/// <param name="Tokenizer">分词器名称。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks。</param>
public sealed record DocumentFullTextIndex(
    string Name,
    IReadOnlyList<string> Fields,
    string Tokenizer,
    long CreatedAtUtcTicks);

/// <summary>
/// 创建或加载全文索引时使用的轻量声明。
/// </summary>
/// <param name="Name">索引名。</param>
/// <param name="Fields">写入 SonnetDB 全文文档的字段列表。</param>
/// <param name="Tokenizer">分词器名称。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
public sealed record DocumentFullTextIndexDefinition(
    string Name,
    IReadOnlyList<string> Fields,
    string Tokenizer = "unicode",
    long CreatedAtUtcTicks = 0);
