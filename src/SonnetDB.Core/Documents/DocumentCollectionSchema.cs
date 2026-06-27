using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace SonnetDB.Documents;

/// <summary>
/// JSON 文档集合 schema，包含集合名称、创建时间、二级索引、全文索引与 validator 声明。
/// </summary>
public sealed class DocumentCollectionSchema
{
    private readonly FrozenDictionary<string, DocumentPathIndex> _indexesByName;
    private readonly FrozenDictionary<string, DocumentFullTextIndex> _fullTextIndexesByName;

    private DocumentCollectionSchema(
        string name,
        IReadOnlyList<DocumentPathIndex> indexes,
        IReadOnlyList<DocumentFullTextIndex> fullTextIndexes,
        long createdAtUtcTicks,
        DocumentValidator? validator)
    {
        Name = name;
        Indexes = indexes;
        FullTextIndexes = fullTextIndexes;
        CreatedAtUtcTicks = createdAtUtcTicks;
        Validator = validator;
        _indexesByName = indexes.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal);
        _fullTextIndexesByName = fullTextIndexes.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal);
    }

    /// <summary>文档集合名称。</summary>
    public string Name { get; }

    /// <summary>按创建顺序排列的文档二级索引声明。</summary>
    public IReadOnlyList<DocumentPathIndex> Indexes { get; }

    /// <summary>按创建顺序排列的全文索引声明。</summary>
    public IReadOnlyList<DocumentFullTextIndex> FullTextIndexes { get; }

    /// <summary>创建时间 UTC ticks。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>可选的集合写入 validator。</summary>
    public DocumentValidator? Validator { get; }

    /// <summary>
    /// 创建并校验文档集合 schema。
    /// </summary>
    /// <param name="name">集合名称。</param>
    /// <param name="indexes">文档二级索引声明。</param>
    /// <param name="fullTextIndexes">全文索引声明。</param>
    /// <param name="createdAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
    /// <param name="validator">可选文档 validator 声明。</param>
    public static DocumentCollectionSchema Create(
        string name,
        IReadOnlyList<DocumentPathIndexDefinition>? indexes = null,
        IReadOnlyList<DocumentFullTextIndexDefinition>? fullTextIndexes = null,
        long createdAtUtcTicks = 0,
        DocumentValidatorDefinition? validator = null)
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
                if (index.Paths.Count == 0)
                    throw new ArgumentException($"文档集合 '{name}' 的索引 '{index.Name}' 至少需要一个 path。", nameof(indexes));
                if (index.TtlSeconds is <= 0)
                    throw new ArgumentOutOfRangeException(nameof(indexes), "TTL index 的 ttlSeconds 必须大于 0。");

                var paths = new string[index.Paths.Count];
                var seenPaths = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < index.Paths.Count; i++)
                {
                    string path = JsonPath.Parse(index.Paths[i]).Text;
                    if (!seenPaths.Add(path))
                        throw new ArgumentException($"文档集合 '{name}' 的索引 '{index.Name}' 中 path '{path}' 重复。", nameof(indexes));
                    paths[i] = path;
                }

                string? ttlPath = string.IsNullOrWhiteSpace(index.TtlPath)
                    ? null
                    : JsonPath.Parse(index.TtlPath).Text;
                if (index.TtlSeconds is not null && ttlPath is null)
                    ttlPath = paths[0];
                if (ttlPath is not null && index.TtlSeconds is null)
                    throw new ArgumentException($"TTL index '{index.Name}' 必须声明 ttlSeconds。", nameof(indexes));

                indexList.Add(new DocumentPathIndex(
                    index.Name,
                    Array.AsReadOnly(paths),
                    index.IsUnique,
                    index.IsSparse,
                    NormalizePartialFilter(index.PartialFilter),
                    ttlPath,
                    index.TtlSeconds,
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
            createdAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : createdAtUtcTicks,
            NormalizeValidator(validator));
    }

    /// <summary>
    /// 尝试按索引名查找文档二级索引声明。
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
    /// 返回添加指定文档二级索引后的新 schema。
    /// </summary>
    /// <param name="definition">索引声明。</param>
    public DocumentCollectionSchema WithIndex(DocumentPathIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_indexesByName.ContainsKey(definition.Name))
            throw new InvalidOperationException($"document collection '{Name}' 中索引 '{definition.Name}' 已存在。");

        var definitions = IndexDefinitions()
            .Append(definition)
            .ToArray();

        return Create(Name, definitions, FullTextIndexDefinitions(), CreatedAtUtcTicks, ValidatorDefinition());
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
            .Select(ToDefinition)
            .ToArray();

        return Create(Name, definitions, FullTextIndexDefinitions(), CreatedAtUtcTicks, ValidatorDefinition());
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

        return Create(Name, IndexDefinitions(), definitions, CreatedAtUtcTicks, ValidatorDefinition());
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

        return Create(Name, IndexDefinitions(), definitions, CreatedAtUtcTicks, ValidatorDefinition());
    }

    /// <summary>
    /// 返回设置指定 validator 后的新 schema。
    /// </summary>
    /// <param name="definition">validator 声明。</param>
    public DocumentCollectionSchema WithValidator(DocumentValidatorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        long createdAt = Validator?.CreatedAtUtcTicks ?? DateTime.UtcNow.Ticks;
        long updatedAt = definition.UpdatedAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : definition.UpdatedAtUtcTicks;
        var normalized = definition with
        {
            CreatedAtUtcTicks = definition.CreatedAtUtcTicks == 0 ? createdAt : definition.CreatedAtUtcTicks,
            UpdatedAtUtcTicks = updatedAt,
        };
        return Create(Name, IndexDefinitions(), FullTextIndexDefinitions(), CreatedAtUtcTicks, normalized);
    }

    /// <summary>
    /// 返回删除 validator 后的新 schema。
    /// </summary>
    public DocumentCollectionSchema WithoutValidator()
        => Validator is null
            ? this
            : Create(Name, IndexDefinitions(), FullTextIndexDefinitions(), CreatedAtUtcTicks);

    private IReadOnlyList<DocumentPathIndexDefinition> IndexDefinitions()
        => Indexes.Select(ToDefinition).ToArray();

    private IReadOnlyList<DocumentFullTextIndexDefinition> FullTextIndexDefinitions()
        => FullTextIndexes
            .Select(static i => new DocumentFullTextIndexDefinition(i.Name, i.Fields, i.Tokenizer, i.CreatedAtUtcTicks))
            .ToArray();

    private DocumentValidatorDefinition? ValidatorDefinition()
        => Validator is null
            ? null
            : new DocumentValidatorDefinition(
                Validator.Rules.Select(static rule => new DocumentValidatorRuleDefinition(
                    rule.Path,
                    rule.Required,
                    rule.Types,
                    rule.Minimum,
                    rule.Maximum,
                    rule.EnumValues,
                    rule.Pattern)).ToArray(),
                Validator.Action,
                Validator.CreatedAtUtcTicks,
                Validator.UpdatedAtUtcTicks);

    private static DocumentPathIndexDefinition ToDefinition(DocumentPathIndex index)
        => new(
            index.Name,
            index.Paths,
            index.CreatedAtUtcTicks,
            index.IsUnique,
            index.IsSparse,
            index.PartialFilter,
            index.TtlPath,
            index.TtlSeconds);

    private static string NormalizeFullTextField(string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (string.Equals(field, "document", StringComparison.OrdinalIgnoreCase))
            return "document";
        if (string.Equals(field, "json", StringComparison.OrdinalIgnoreCase))
            return "json";

        return JsonPath.Parse(field).Text;
    }

    private static DocumentIndexPartialFilter? NormalizePartialFilter(DocumentIndexPartialFilter? filter)
    {
        if (filter is null)
            return null;

        return new DocumentIndexPartialFilter(
            JsonPath.Parse(filter.Path).Text,
            filter.Operator,
            filter.ValueScalar);
    }

    private static DocumentValidator? NormalizeValidator(DocumentValidatorDefinition? validator)
    {
        if (validator is null)
            return null;
        if (!Enum.IsDefined(validator.Action))
            throw new ArgumentException("validator validationAction 必须是 error 或 warn。", nameof(validator));
        if (validator.Rules.Count == 0)
            throw new ArgumentException("validator 至少需要一条字段规则。", nameof(validator));

        var rules = new List<DocumentValidatorRule>(validator.Rules.Count);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in validator.Rules)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.Path);
            string path = JsonPath.Parse(rule.Path).Text;
            if (!seenPaths.Add(path))
                throw new ArgumentException($"validator 中 path '{path}' 重复。", nameof(validator));
            if (rule.Minimum is { } min && rule.Maximum is { } max && min > max)
                throw new ArgumentException($"validator path '{path}' 的 minimum 不能大于 maximum。", nameof(validator));

            var types = NormalizeValidatorTypes(rule.Types, path);
            var enumValues = NormalizeEnumValues(rule.EnumValues);
            string? pattern = string.IsNullOrWhiteSpace(rule.Pattern) ? null : rule.Pattern;
            if (pattern is not null)
            {
                try
                {
                    _ = Regex.IsMatch(string.Empty, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"validator path '{path}' 的 pattern 无效：{ex.Message}", nameof(validator), ex);
                }
            }

            rules.Add(new DocumentValidatorRule(
                path,
                rule.Required,
                types,
                rule.Minimum,
                rule.Maximum,
                enumValues,
                pattern));
        }

        long now = DateTime.UtcNow.Ticks;
        return new DocumentValidator(
            rules.AsReadOnly(),
            validator.Action,
            validator.CreatedAtUtcTicks == 0 ? now : validator.CreatedAtUtcTicks,
            validator.UpdatedAtUtcTicks == 0 ? now : validator.UpdatedAtUtcTicks);
    }

    private static IReadOnlyList<DocumentValidatorValueType> NormalizeValidatorTypes(
        IReadOnlyList<DocumentValidatorValueType>? types,
        string path)
    {
        if (types is null || types.Count == 0)
            return Array.Empty<DocumentValidatorValueType>();

        var values = new List<DocumentValidatorValueType>(types.Count);
        var seen = new HashSet<DocumentValidatorValueType>();
        foreach (var type in types)
        {
            if (!Enum.IsDefined(type))
                throw new ArgumentException($"validator path '{path}' 包含不支持的 type。", nameof(types));
            if (seen.Add(type))
                values.Add(type);
        }

        return values.AsReadOnly();
    }

    private static IReadOnlyList<string> NormalizeEnumValues(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return Array.Empty<string>();

        var result = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (seen.Add(value))
                result.Add(value);
        }

        return result.AsReadOnly();
    }
}

/// <summary>
/// JSON 文档集合二级索引声明。
/// </summary>
/// <param name="Name">索引名，在单集合内唯一。</param>
/// <param name="Paths">索引字段 JSON path 列表。</param>
/// <param name="IsUnique">是否为唯一索引。</param>
/// <param name="IsSparse">是否为 sparse 索引。</param>
/// <param name="PartialFilter">可选 partial index 过滤条件。</param>
/// <param name="TtlPath">可选 TTL 时间字段 path。</param>
/// <param name="TtlSeconds">TTL 保留秒数。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks。</param>
public sealed record DocumentPathIndex(
    string Name,
    IReadOnlyList<string> Paths,
    bool IsUnique,
    bool IsSparse,
    DocumentIndexPartialFilter? PartialFilter,
    string? TtlPath,
    long? TtlSeconds,
    long CreatedAtUtcTicks)
{
    /// <summary>
    /// 创建旧版单字段 JSON path 索引声明。
    /// </summary>
    public DocumentPathIndex(string Name, string Path, long CreatedAtUtcTicks)
        : this(Name, [Path], IsUnique: false, IsSparse: false, PartialFilter: null, TtlPath: null, TtlSeconds: null, CreatedAtUtcTicks)
    {
    }

    /// <summary>首个索引字段 path，兼容旧 JSON path 索引调用方。</summary>
    public string Path => Paths.Count == 0 ? string.Empty : Paths[0];

    /// <summary>当前索引是否为 TTL index。</summary>
    public bool IsTtl => !string.IsNullOrWhiteSpace(TtlPath) && TtlSeconds is not null;
}

/// <summary>
/// 创建或加载文档二级索引时使用的轻量声明。
/// </summary>
/// <param name="Name">索引名。</param>
/// <param name="Paths">JSON path 列表。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
/// <param name="IsUnique">是否为唯一索引。</param>
/// <param name="IsSparse">是否为 sparse 索引。</param>
/// <param name="PartialFilter">可选 partial index 过滤条件。</param>
/// <param name="TtlPath">可选 TTL 时间字段 path。</param>
/// <param name="TtlSeconds">TTL 保留秒数。</param>
public sealed record DocumentPathIndexDefinition(
    string Name,
    IReadOnlyList<string> Paths,
    long CreatedAtUtcTicks = 0,
    bool IsUnique = false,
    bool IsSparse = false,
    DocumentIndexPartialFilter? PartialFilter = null,
    string? TtlPath = null,
    long? TtlSeconds = null)
{
    /// <summary>
    /// 创建单字段 JSON path 索引声明。
    /// </summary>
    public DocumentPathIndexDefinition(
        string Name,
        string Path,
        long CreatedAtUtcTicks = 0,
        bool IsUnique = false,
        bool IsSparse = false,
        DocumentIndexPartialFilter? PartialFilter = null,
        string? TtlPath = null,
        long? TtlSeconds = null)
        : this(Name, [Path], CreatedAtUtcTicks, IsUnique, IsSparse, PartialFilter, TtlPath, TtlSeconds)
    {
    }

    /// <summary>首个索引字段 path，兼容旧 JSON path 索引调用方。</summary>
    public string Path => Paths.Count == 0 ? string.Empty : Paths[0];
}

/// <summary>
/// Document partial index 过滤条件。
/// </summary>
/// <param name="Path">JSON path。</param>
/// <param name="Operator">比较运算符。</param>
/// <param name="ValueScalar">比较值的稳定索引标量；exists 过滤可为空。</param>
public sealed record DocumentIndexPartialFilter(
    string Path,
    DocumentIndexPartialFilterOperator Operator,
    string? ValueScalar);

/// <summary>
/// Document partial index 支持的过滤运算符。
/// </summary>
public enum DocumentIndexPartialFilterOperator
{
    /// <summary>字段存在。</summary>
    Exists,
    /// <summary>字段等于给定值。</summary>
    Equal,
    /// <summary>字段不等于给定值。</summary>
    NotEqual,
    /// <summary>字段大于给定值。</summary>
    GreaterThan,
    /// <summary>字段大于等于给定值。</summary>
    GreaterThanOrEqual,
    /// <summary>字段小于给定值。</summary>
    LessThan,
    /// <summary>字段小于等于给定值。</summary>
    LessThanOrEqual,
}

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
