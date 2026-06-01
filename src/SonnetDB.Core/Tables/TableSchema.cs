using System.Collections.Frozen;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表 schema，包含列顺序与主键声明。
/// </summary>
public sealed class TableSchema
{
    private readonly FrozenDictionary<string, TableColumn> _columnsByName;

    private TableSchema(string name, IReadOnlyList<TableColumn> columns, IReadOnlyList<string> primaryKey, long createdAtUtcTicks)
    {
        Name = name;
        Columns = columns;
        PrimaryKey = primaryKey;
        CreatedAtUtcTicks = createdAtUtcTicks;
        _columnsByName = columns.ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>表名。</summary>
    public string Name { get; }

    /// <summary>按声明顺序排列的列。</summary>
    public IReadOnlyList<TableColumn> Columns { get; }

    /// <summary>按声明顺序排列的主键列名。</summary>
    public IReadOnlyList<string> PrimaryKey { get; }

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

        return new TableSchema(
            name,
            columnList.AsReadOnly(),
            primaryKeyList.AsReadOnly(),
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
}
