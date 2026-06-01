using System.Collections.Frozen;

namespace SonnetDB.Documents;

/// <summary>
/// JSON 文档集合 schema，包含集合名称、创建时间与 JSON path 索引声明。
/// </summary>
public sealed class DocumentCollectionSchema
{
    private readonly FrozenDictionary<string, DocumentPathIndex> _indexesByName;

    private DocumentCollectionSchema(
        string name,
        IReadOnlyList<DocumentPathIndex> indexes,
        long createdAtUtcTicks)
    {
        Name = name;
        Indexes = indexes;
        CreatedAtUtcTicks = createdAtUtcTicks;
        _indexesByName = indexes.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal);
    }

    /// <summary>文档集合名称。</summary>
    public string Name { get; }

    /// <summary>按创建顺序排列的 JSON path 索引声明。</summary>
    public IReadOnlyList<DocumentPathIndex> Indexes { get; }

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

        return new DocumentCollectionSchema(
            name,
            indexList.AsReadOnly(),
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

        return Create(Name, definitions, CreatedAtUtcTicks);
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

        return Create(Name, definitions, CreatedAtUtcTicks);
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
