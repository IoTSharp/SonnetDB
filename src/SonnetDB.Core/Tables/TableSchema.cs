using System.Collections.Frozen;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表 schema，包含列顺序、主键声明与二级索引声明。
/// </summary>
public sealed class TableSchema
{
    private readonly FrozenDictionary<string, TableColumn> _columnsByName;
    private readonly FrozenDictionary<string, TableIndex> _indexesByName;

    private TableSchema(
        string name,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndex> indexes,
        long createdAtUtcTicks)
    {
        Name = name;
        Columns = columns;
        PrimaryKey = primaryKey;
        Indexes = indexes;
        CreatedAtUtcTicks = createdAtUtcTicks;
        _columnsByName = columns.ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);
        _indexesByName = indexes.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal);
    }

    /// <summary>表名。</summary>
    public string Name { get; }

    /// <summary>按声明顺序排列的列。</summary>
    public IReadOnlyList<TableColumn> Columns { get; }

    /// <summary>按声明顺序排列的主键列名。</summary>
    public IReadOnlyList<string> PrimaryKey { get; }

    /// <summary>按创建顺序排列的二级索引声明。</summary>
    public IReadOnlyList<TableIndex> Indexes { get; }

    /// <summary>创建时间 UTC ticks。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>
    /// 创建并校验关系表 schema。
    /// </summary>
    /// <param name="name">表名。</param>
    /// <param name="columns">列定义。</param>
    /// <param name="primaryKey">主键列名。</param>
    /// <param name="createdAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
    public static TableSchema Create(
        string name,
        IReadOnlyList<(string Name, TableColumnType DataType, bool IsNullable)> columns,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        long createdAtUtcTicks = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(primaryKey);

        if (columns.Count == 0)
            throw new ArgumentException("关系表至少需要 1 个列。", nameof(columns));
        if (primaryKey.Count == 0)
            throw new ArgumentException("关系表 MVP 要求声明 PRIMARY KEY。", nameof(primaryKey));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var columnList = new List<TableColumn>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(column.Name);
            if (!seen.Add(column.Name))
                throw new ArgumentException($"关系表 '{name}' 中列 '{column.Name}' 重复。", nameof(columns));
            if (!Enum.IsDefined(column.DataType))
                throw new ArgumentException($"关系表 '{name}' 的列 '{column.Name}' 使用了未知类型 {column.DataType}。", nameof(columns));
            columnList.Add(new TableColumn(column.Name, column.DataType, IsPrimaryKey: false, column.IsNullable, i));
        }

        var primaryKeyList = new List<string>(primaryKey.Count);
        var primaryKeySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyColumn in primaryKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
            if (!seen.Contains(keyColumn))
                throw new ArgumentException($"PRIMARY KEY 引用了未知列 '{keyColumn}'。", nameof(primaryKey));
            if (!primaryKeySet.Add(keyColumn))
                throw new ArgumentException($"PRIMARY KEY 中列 '{keyColumn}' 重复。", nameof(primaryKey));
            primaryKeyList.Add(keyColumn);
        }

        for (int i = 0; i < columnList.Count; i++)
        {
            var column = columnList[i];
            if (primaryKeySet.Contains(column.Name))
                columnList[i] = column with { IsPrimaryKey = true, IsNullable = false };
        }

        var indexList = BuildIndexes(name, columnList, primaryKeySet, indexes, createdAtUtcTicks);

        return new TableSchema(
            name,
            columnList.AsReadOnly(),
            primaryKeyList.AsReadOnly(),
            indexList.AsReadOnly(),
            createdAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : createdAtUtcTicks);
    }

    /// <summary>
    /// 尝试按列名查找列定义。
    /// </summary>
    /// <param name="name">列名。</param>
    /// <returns>找到时返回列定义；否则返回 null。</returns>
    public TableColumn? TryGetColumn(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _columnsByName.TryGetValue(name, out var column) ? column : null;
    }

    /// <summary>
    /// 尝试按索引名查找二级索引声明。
    /// </summary>
    /// <param name="name">索引名。</param>
    /// <returns>找到时返回索引声明；否则返回 null。</returns>
    public TableIndex? TryGetIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _indexesByName.TryGetValue(name, out var index) ? index : null;
    }

    /// <summary>
    /// 返回添加指定索引后的新 schema。
    /// </summary>
    /// <param name="definition">索引声明。</param>
    public TableSchema WithIndex(TableIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_indexesByName.ContainsKey(definition.Name))
            throw new InvalidOperationException($"table '{Name}' 中索引 '{definition.Name}' 已存在。");

        var definitions = Indexes
            .Select(static i => new TableIndexDefinition(i.Name, i.Columns, i.IsUnique, i.CreatedAtUtcTicks))
            .Append(definition)
            .ToArray();
        return Create(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            definitions,
            CreatedAtUtcTicks);
    }

    /// <summary>
    /// 返回删除指定索引后的新 schema。
    /// </summary>
    /// <param name="indexName">索引名。</param>
    /// <returns>索引存在时返回新 schema；否则返回当前实例。</returns>
    public TableSchema WithoutIndex(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        if (!_indexesByName.ContainsKey(indexName))
            return this;

        var definitions = Indexes
            .Where(i => !string.Equals(i.Name, indexName, StringComparison.Ordinal))
            .Select(static i => new TableIndexDefinition(i.Name, i.Columns, i.IsUnique, i.CreatedAtUtcTicks))
            .ToArray();
        return Create(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            definitions,
            CreatedAtUtcTicks);
    }

    private static List<TableIndex> BuildIndexes(
        string tableName,
        IReadOnlyList<TableColumn> columns,
        HashSet<string> primaryKeySet,
        IReadOnlyList<TableIndexDefinition>? indexes,
        long createdAtUtcTicks)
    {
        var result = new List<TableIndex>();
        if (indexes is null || indexes.Count == 0)
            return result;

        var columnNames = columns.Select(static c => c.Name).ToHashSet(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(index.Name);
            if (!seenNames.Add(index.Name))
                throw new ArgumentException($"关系表 '{tableName}' 中索引 '{index.Name}' 重复。", nameof(indexes));
            if (index.Columns.Count == 0)
                throw new ArgumentException($"索引 '{index.Name}' 至少需要 1 个列。", nameof(indexes));

            var seenIndexColumns = new HashSet<string>(StringComparer.Ordinal);
            var indexColumns = new List<string>(index.Columns.Count);
            foreach (var column in index.Columns)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(column);
                if (!columnNames.Contains(column))
                    throw new ArgumentException($"索引 '{index.Name}' 引用了未知列 '{column}'。", nameof(indexes));
                if (primaryKeySet.Contains(column))
                {
                    // 主键列可被二级索引包含，但单纯为主键建 secondary index 没有意义；
                    // 这里允许复合索引包含主键作为区分列。
                }
                if (!seenIndexColumns.Add(column))
                    throw new ArgumentException($"索引 '{index.Name}' 中列 '{column}' 重复。", nameof(indexes));
                indexColumns.Add(column);
            }

            result.Add(new TableIndex(
                index.Name,
                indexColumns.AsReadOnly(),
                index.IsUnique,
                index.CreatedAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : index.CreatedAtUtcTicks));
        }

        return result;
    }
}
