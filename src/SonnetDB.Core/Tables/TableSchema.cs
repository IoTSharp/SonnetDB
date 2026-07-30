using System.Collections.Frozen;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表 schema，包含列顺序、主键声明与二级索引声明。
/// </summary>
public sealed class TableSchema
{
    private readonly FrozenDictionary<string, TableColumn> _columnsByName;
    private readonly FrozenDictionary<string, TableIndex> _indexesByName;
    private readonly FrozenDictionary<string, TableCheckConstraint> _checkConstraintsByName;

    private TableSchema(
        string name,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndex> indexes,
        IReadOnlyList<TableForeignKey> foreignKeys,
        IReadOnlyList<TableCheckConstraint> checkConstraints,
        long createdAtUtcTicks)
    {
        Name = name;
        Columns = columns;
        PrimaryKey = primaryKey;
        Indexes = indexes;
        ForeignKeys = foreignKeys;
        CheckConstraints = checkConstraints;
        CreatedAtUtcTicks = createdAtUtcTicks;
        _columnsByName = columns.ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);
        _indexesByName = indexes.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal);
        _checkConstraintsByName = checkConstraints.ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>表名。</summary>
    public string Name { get; }

    /// <summary>按声明顺序排列的列。</summary>
    public IReadOnlyList<TableColumn> Columns { get; }

    /// <summary>按声明顺序排列的主键列名。</summary>
    public IReadOnlyList<string> PrimaryKey { get; }

    /// <summary>按创建顺序排列的二级索引声明。</summary>
    public IReadOnlyList<TableIndex> Indexes { get; }

    /// <summary>按创建顺序排列的外键声明。</summary>
    public IReadOnlyList<TableForeignKey> ForeignKeys { get; }

    /// <summary>按创建顺序排列的检查约束声明。</summary>
    public IReadOnlyList<TableCheckConstraint> CheckConstraints { get; }

    /// <summary>创建时间 UTC ticks。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>
    /// 创建并校验关系表 schema。
    /// </summary>
    /// <param name="name">表名。</param>
    /// <param name="columns">列定义。</param>
    /// <param name="primaryKey">主键列名。</param>
    /// <param name="indexes">二级索引声明。</param>
    /// <param name="foreignKeys">外键声明。</param>
    /// <param name="rowVersionColumns">乐观并发版本列名。</param>
    /// <param name="createdAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
    /// <param name="checkConstraints">检查约束声明。</param>
    public static TableSchema Create(
        string name,
        IReadOnlyList<(string Name, TableColumnType DataType, bool IsNullable)> columns,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition>? indexes = null,
        IReadOnlyList<TableForeignKeyDefinition>? foreignKeys = null,
        IReadOnlySet<string>? rowVersionColumns = null,
        long createdAtUtcTicks = 0,
        IReadOnlyList<TableCheckConstraintDefinition>? checkConstraints = null)
        => CreateWithDefaults(
            name,
            columns,
            primaryKey,
            indexes,
            foreignKeys,
            rowVersionColumns,
            createdAtUtcTicks,
            checkConstraints,
            columnDefaults: null);

    /// <summary>
    /// 创建关系表 schema，并附带可持久化的列默认值定义。
    /// </summary>
    /// <param name="name">表名。</param>
    /// <param name="columns">列定义。</param>
    /// <param name="primaryKey">主键列名。</param>
    /// <param name="indexes">二级索引声明。</param>
    /// <param name="foreignKeys">外键声明。</param>
    /// <param name="rowVersionColumns">乐观并发版本列名。</param>
    /// <param name="createdAtUtcTicks">创建时间 UTC ticks。</param>
    /// <param name="checkConstraints">检查约束声明。</param>
    /// <param name="columnDefaults">列名到规范化默认表达式的映射。</param>
    public static TableSchema CreateWithDefaults(
        string name,
        IReadOnlyList<(string Name, TableColumnType DataType, bool IsNullable)> columns,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition>? indexes,
        IReadOnlyList<TableForeignKeyDefinition>? foreignKeys,
        IReadOnlySet<string>? rowVersionColumns,
        long createdAtUtcTicks,
        IReadOnlyList<TableCheckConstraintDefinition>? checkConstraints,
        IReadOnlyDictionary<string, string?>? columnDefaults)
        => CreateWithDefaults(
            name,
            columns,
            primaryKey,
            indexes,
            foreignKeys,
            rowVersionColumns,
            createdAtUtcTicks,
            checkConstraints,
            columnDefaults,
            autoIncrementColumns: null);

    /// <summary>
    /// 创建关系表 schema，并附带可持久化的列默认值与自增列定义。
    /// </summary>
    /// <param name="name">表名。</param>
    /// <param name="columns">列定义。</param>
    /// <param name="primaryKey">主键列名。</param>
    /// <param name="indexes">二级索引定义。</param>
    /// <param name="foreignKeys">外键定义。</param>
    /// <param name="rowVersionColumns">乐观并发版本列名。</param>
    /// <param name="createdAtUtcTicks">创建时间 UTC ticks。</param>
    /// <param name="checkConstraints">检查约束定义。</param>
    /// <param name="columnDefaults">列名到规范化默认表达式的映射。</param>
    /// <param name="autoIncrementColumns">由数据库自动分配递增整数的列名集合。</param>
    public static TableSchema CreateWithDefaults(
        string name,
        IReadOnlyList<(string Name, TableColumnType DataType, bool IsNullable)> columns,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition>? indexes,
        IReadOnlyList<TableForeignKeyDefinition>? foreignKeys,
        IReadOnlySet<string>? rowVersionColumns,
        long createdAtUtcTicks,
        IReadOnlyList<TableCheckConstraintDefinition>? checkConstraints,
        IReadOnlyDictionary<string, string?>? columnDefaults,
        IReadOnlySet<string>? autoIncrementColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(primaryKey);

        if (columns.Count == 0)
            throw new ArgumentException("关系表至少需要 1 个列。", nameof(columns));
        if (primaryKey.Count == 0)
            throw new ArgumentException("关系表 MVP 要求声明 PRIMARY KEY。", nameof(primaryKey));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var autoIncrementSet = autoIncrementColumns is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(autoIncrementColumns, StringComparer.Ordinal);
        var columnList = new List<TableColumn>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(column.Name);
            if (!seen.Add(column.Name))
                throw new ArgumentException($"关系表 '{name}' 中列 '{column.Name}' 重复。", nameof(columns));
            if (!Enum.IsDefined(column.DataType))
                throw new ArgumentException($"关系表 '{name}' 的列 '{column.Name}' 使用了未知类型 {column.DataType}。", nameof(columns));
            bool isRowVersion = rowVersionColumns?.Contains(column.Name) == true;
            bool isAutoIncrement = autoIncrementSet.Contains(column.Name);
            string? defaultExpressionSql = columnDefaults is not null
                && columnDefaults.TryGetValue(column.Name, out var configuredDefault)
                    ? configuredDefault
                    : null;
            if (columnDefaults?.ContainsKey(column.Name) == true
                && string.IsNullOrWhiteSpace(defaultExpressionSql))
            {
                throw new ArgumentException($"关系表 '{name}' 的列 '{column.Name}' 默认表达式不能为空。", nameof(columnDefaults));
            }
            if (isRowVersion && defaultExpressionSql is not null)
            {
                throw new ArgumentException(
                    $"关系表 '{name}' 的 ROWVERSION 列 '{column.Name}' 不允许声明默认值。",
                    nameof(columnDefaults));
            }
            if (isAutoIncrement && column.DataType != TableColumnType.Int64)
            {
                throw new ArgumentException(
                    $"关系表 '{name}' 的 AUTO_INCREMENT 列 '{column.Name}' 必须是 INT 类型。",
                    nameof(autoIncrementColumns));
            }
            if (isAutoIncrement && isRowVersion)
            {
                throw new ArgumentException(
                    $"关系表 '{name}' 的列 '{column.Name}' 不能同时是 AUTO_INCREMENT 和 ROWVERSION。",
                    nameof(autoIncrementColumns));
            }
            if (isAutoIncrement && defaultExpressionSql is not null)
            {
                throw new ArgumentException(
                    $"关系表 '{name}' 的 AUTO_INCREMENT 列 '{column.Name}' 不允许声明默认值。",
                    nameof(autoIncrementColumns));
            }
            var tableColumn = new TableColumn(
                column.Name,
                column.DataType,
                IsPrimaryKey: false,
                column.IsNullable && !isAutoIncrement,
                i,
                isRowVersion);
            if (isAutoIncrement)
                tableColumn = tableColumn with { IsAutoIncrement = true };
            if (defaultExpressionSql is not null)
                tableColumn = tableColumn with { DefaultExpressionSql = defaultExpressionSql };
            columnList.Add(tableColumn);
        }

        if (columnDefaults is not null)
        {
            foreach (var defaultColumn in columnDefaults.Keys)
            {
                if (!seen.Contains(defaultColumn))
                    throw new ArgumentException($"默认值引用了未知列 '{defaultColumn}'。", nameof(columnDefaults));
            }
        }

        foreach (var autoColumn in autoIncrementSet)
        {
            if (!seen.Contains(autoColumn))
                throw new ArgumentException($"AUTO_INCREMENT 引用了未知列 '{autoColumn}'。", nameof(autoIncrementColumns));
        }

        var autoIncrementColumnList = columnList.Where(static column => column.IsAutoIncrement).ToArray();
        if (autoIncrementColumnList.Length > 1)
            throw new ArgumentException($"关系表 '{name}' 最多只能声明一个 AUTO_INCREMENT 列。", nameof(autoIncrementColumns));

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

        ValidateRowVersionColumns(name, columnList);
        var indexList = BuildIndexes(name, columnList, primaryKeySet, indexes, createdAtUtcTicks);
        var foreignKeyList = BuildForeignKeys(name, columnList, foreignKeys);
        var checkConstraintList = BuildCheckConstraints(name, columnList, checkConstraints);
        var constraintNames = foreignKeyList.Select(static constraint => constraint.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var checkConstraint in checkConstraintList)
        {
            if (!constraintNames.Add(checkConstraint.Name))
                throw new ArgumentException($"关系表 '{name}' 中约束 '{checkConstraint.Name}' 重复。", nameof(checkConstraints));
        }

        return new TableSchema(
            name,
            columnList.AsReadOnly(),
            primaryKeyList.AsReadOnly(),
            indexList.AsReadOnly(),
            foreignKeyList.AsReadOnly(),
            checkConstraintList.AsReadOnly(),
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
    /// 尝试按约束名查找外键声明。
    /// </summary>
    /// <param name="name">外键约束名。</param>
    /// <returns>找到时返回外键声明；否则返回 null。</returns>
    public TableForeignKey? TryGetForeignKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ForeignKeys.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// 尝试按约束名查找检查约束。
    /// </summary>
    /// <param name="name">检查约束名。</param>
    /// <returns>找到时返回约束；否则返回 <c>null</c>。</returns>
    public TableCheckConstraint? TryGetCheckConstraint(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _checkConstraintsByName.TryGetValue(name, out var constraint) ? constraint : null;
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
            .Select(static i => new TableIndexDefinition(i.Name, i.Columns, i.IsUnique, i.CreatedAtUtcTicks, i.JsonPath))
            .Append(definition)
            .ToArray();
        return CreateWithDefaults(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            definitions,
            ForeignKeyDefinitions(),
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            ColumnDefaultDefinitions(),
            AutoIncrementColumnNames());
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
            .Select(static i => new TableIndexDefinition(i.Name, i.Columns, i.IsUnique, i.CreatedAtUtcTicks, i.JsonPath))
            .ToArray();
        return CreateWithDefaults(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            definitions,
            ForeignKeyDefinitions(),
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            ColumnDefaultDefinitions(),
            AutoIncrementColumnNames());
    }

    /// <summary>
    /// 返回删除指定外键后的新 schema；兼容 EF Core 默认外键命名规则。
    /// </summary>
    /// <param name="constraintName">外键约束名。</param>
    /// <returns>外键存在时返回新 schema；否则返回当前实例。</returns>
    public TableSchema WithoutForeignKey(string constraintName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
        var target = ForeignKeys.FirstOrDefault(f =>
            string.Equals(f.Name, constraintName, StringComparison.Ordinal)
            || string.Equals(BuildEfForeignKeyName(Name, f), constraintName, StringComparison.Ordinal));
        if (target is null)
            return this;

        var definitions = ForeignKeys
            .Where(f => !string.Equals(f.Name, target.Name, StringComparison.Ordinal))
            .Select(static f => new TableForeignKeyDefinition(f.Name, f.Columns, f.PrincipalTable, f.PrincipalColumns, f.OnDelete))
            .ToArray();
        return CreateWithDefaults(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            IndexDefinitions(),
            definitions,
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            ColumnDefaultDefinitions(),
            AutoIncrementColumnNames());
    }

    /// <summary>
    /// 返回添加指定外键后的新 schema。
    /// </summary>
    /// <param name="definition">外键声明。</param>
    public TableSchema WithForeignKey(TableForeignKeyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.IsNullOrWhiteSpace(definition.Name)
            && ForeignKeys.Any(f => string.Equals(f.Name, definition.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"table '{Name}' 中外键 '{definition.Name}' 已存在。");
        }

        var definitions = ForeignKeyDefinitions()
            .Append(definition)
            .ToArray();
        return CreateWithDefaults(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            IndexDefinitions(),
            definitions,
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            ColumnDefaultDefinitions(),
            AutoIncrementColumnNames());
    }

    /// <summary>
    /// 返回添加指定检查约束后的新 schema。
    /// </summary>
    /// <param name="definition">检查约束声明。</param>
    /// <returns>新 schema。</returns>
    public TableSchema WithCheckConstraint(TableCheckConstraintDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.IsNullOrWhiteSpace(definition.Name)
            && (TryGetCheckConstraint(definition.Name) is not null
                || TryGetForeignKey(definition.Name) is not null))
        {
            throw new InvalidOperationException($"table '{Name}' 中约束 '{definition.Name}' 已存在。");
        }

        return CreateWithDefaults(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            IndexDefinitions(),
            ForeignKeyDefinitions(),
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions().Append(definition).ToArray(),
            ColumnDefaultDefinitions(),
            AutoIncrementColumnNames());
    }

    /// <summary>
    /// 返回删除指定检查约束后的新 schema。
    /// </summary>
    /// <param name="constraintName">检查约束名。</param>
    /// <returns>约束存在时返回新 schema；否则返回当前实例。</returns>
    public TableSchema WithoutCheckConstraint(string constraintName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
        if (!_checkConstraintsByName.ContainsKey(constraintName))
            return this;

        return CreateWithDefaults(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            IndexDefinitions(),
            ForeignKeyDefinitions(),
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraints
                .Where(constraint => !string.Equals(constraint.Name, constraintName, StringComparison.Ordinal))
                .Select(static constraint => new TableCheckConstraintDefinition(
                    constraint.Name,
                    constraint.ExpressionSql))
                .ToArray(),
            ColumnDefaultDefinitions(),
            AutoIncrementColumnNames());
    }

    /// <summary>
    /// 返回添加列后的新 schema。新增列追加到末尾。
    /// </summary>
    public TableSchema WithAddedColumn(string name, TableColumnType dataType, bool isNullable)
        => WithAddedColumn(name, dataType, isNullable, defaultExpressionSql: null);

    /// <summary>
    /// 返回添加列后的新 schema，并保存该列的默认表达式。
    /// </summary>
    /// <param name="name">列名。</param>
    /// <param name="dataType">列数据类型。</param>
    /// <param name="isNullable">是否允许 NULL。</param>
    /// <param name="defaultExpressionSql">规范化默认表达式；没有默认值时为 <c>null</c>。</param>
    public TableSchema WithAddedColumn(
        string name,
        TableColumnType dataType,
        bool isNullable,
        string? defaultExpressionSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_columnsByName.ContainsKey(name))
            throw new InvalidOperationException($"table '{Name}' 中列 '{name}' 已存在。");

        var defaults = ColumnDefaultDefinitions().ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        if (defaultExpressionSql is not null)
            defaults[name] = defaultExpressionSql;

        return CreateWithDefaults(
            Name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable))
                .Append((name, dataType, isNullable))
                .ToArray(),
            PrimaryKey,
            IndexDefinitions(),
            ForeignKeyDefinitions(),
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            defaults,
            AutoIncrementColumnNames());
    }

    /// <summary>
    /// 返回修改指定列类型、空值约束和默认值后的新 schema。
    /// </summary>
    /// <param name="name">列名。</param>
    /// <param name="dataType">目标数据类型。</param>
    /// <param name="isNullable">目标空值约束。</param>
    /// <param name="defaultExpressionSql">目标默认表达式；<c>null</c> 表示没有默认值。</param>
    public TableSchema WithAlteredColumn(
        string name,
        TableColumnType dataType,
        bool isNullable,
        string? defaultExpressionSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var column = TryGetColumn(name)
            ?? throw new InvalidOperationException($"table '{Name}' 中不存在列 '{name}'。");
        if (column.IsPrimaryKey && (column.DataType != dataType || isNullable))
            throw new InvalidOperationException("ALTER TABLE ALTER COLUMN 当前不支持修改 PRIMARY KEY 列的类型或空值约束。");
        if (column.IsRowVersion
            && (column.DataType != dataType || isNullable || defaultExpressionSql is not null))
        {
            throw new InvalidOperationException("ALTER TABLE ALTER COLUMN 当前不支持修改 ROWVERSION 列定义。");
        }
        if (column.IsAutoIncrement
            && (column.DataType != dataType || isNullable || defaultExpressionSql is not null))
        {
            throw new InvalidOperationException("ALTER TABLE ALTER COLUMN 当前不支持修改 AUTO_INCREMENT 列定义。");
        }

        var defaults = ColumnDefaultDefinitions().ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        if (defaultExpressionSql is null)
            defaults.Remove(name);
        else
            defaults[name] = defaultExpressionSql;

        return CreateWithDefaults(
            Name,
            Columns.Select(c => string.Equals(c.Name, name, StringComparison.Ordinal)
                    ? (c.Name, dataType, isNullable)
                    : (c.Name, c.DataType, c.IsNullable))
                .ToArray(),
            PrimaryKey,
            IndexDefinitions(),
            ForeignKeyDefinitions(),
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            defaults,
            AutoIncrementColumnNames());
    }

    /// <summary>
    /// 返回删除列后的新 schema。首版不允许删除主键列或索引引用列。
    /// </summary>
    public TableSchema WithoutColumn(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var column = TryGetColumn(name)
            ?? throw new InvalidOperationException($"table '{Name}' 中不存在列 '{name}'。");
        if (column.IsPrimaryKey)
            throw new InvalidOperationException("ALTER TABLE DROP COLUMN 当前不支持删除 PRIMARY KEY 列。");
        foreach (var index in Indexes)
        {
            if (index.Columns.Any(c => string.Equals(c, name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"列 '{name}' 被索引 '{index.Name}' 引用，不能删除。");
        }
        foreach (var foreignKey in ForeignKeys)
        {
            if (foreignKey.Columns.Any(c => string.Equals(c, name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"列 '{name}' 被外键 '{foreignKey.Name}' 引用，不能删除。");
        }
        foreach (var checkConstraint in CheckConstraints)
        {
            if (checkConstraint.ReferencesColumn(name))
                throw new InvalidOperationException($"列 '{name}' 被检查约束 '{checkConstraint.Name}' 引用，不能删除。");
        }

        return CreateWithDefaults(
            Name,
            Columns.Where(c => !string.Equals(c.Name, name, StringComparison.Ordinal))
                .Select(static c => (c.Name, c.DataType, c.IsNullable))
                .ToArray(),
            PrimaryKey,
            IndexDefinitions(),
            ForeignKeyDefinitions(),
            RowVersionColumnNames(exceptColumn: name),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            ColumnDefaultDefinitions(exceptColumn: name),
            AutoIncrementColumnNames(exceptColumn: name));
    }

    /// <summary>
    /// 返回重命名列后的新 schema。首版不允许重命名主键列。
    /// </summary>
    public TableSchema WithRenamedColumn(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var column = TryGetColumn(oldName)
            ?? throw new InvalidOperationException($"table '{Name}' 中不存在列 '{oldName}'。");
        if (_columnsByName.ContainsKey(newName))
            throw new InvalidOperationException($"table '{Name}' 中列 '{newName}' 已存在。");
        if (column.IsPrimaryKey)
            throw new InvalidOperationException("ALTER TABLE RENAME COLUMN 当前不支持重命名 PRIMARY KEY 列。");
        if (CheckConstraints.Any(constraint => constraint.ReferencesColumn(oldName)))
            throw new InvalidOperationException($"列 '{oldName}' 被 CHECK 约束引用，当前不能重命名。");

        return CreateWithDefaults(
            Name,
            Columns.Select(c => string.Equals(c.Name, oldName, StringComparison.Ordinal)
                    ? (newName, c.DataType, c.IsNullable)
                    : (c.Name, c.DataType, c.IsNullable))
                .ToArray(),
            PrimaryKey,
            IndexDefinitions(renameColumn: (oldName, newName)),
            ForeignKeyDefinitions(renameColumn: (oldName, newName)),
            RowVersionColumnNames(renameColumn: (oldName, newName)),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            ColumnDefaultDefinitions(renameColumn: (oldName, newName)),
            AutoIncrementColumnNames(renameColumn: (oldName, newName)));
    }

    /// <summary>
    /// 返回重命名表后的新 schema。
    /// </summary>
    public TableSchema WithName(string name)
        => CreateWithDefaults(
            name,
            Columns.Select(static c => (c.Name, c.DataType, c.IsNullable)).ToArray(),
            PrimaryKey,
            IndexDefinitions(),
            ForeignKeyDefinitions(),
            RowVersionColumnNames(),
            CreatedAtUtcTicks,
            CheckConstraintDefinitions(),
            ColumnDefaultDefinitions(),
            AutoIncrementColumnNames());

    /// <summary>返回当前表的乐观并发列；未声明时返回 <c>null</c>。</summary>
    public TableColumn? RowVersionColumn
        => Columns.FirstOrDefault(static c => c.IsRowVersion);

    /// <summary>返回当前表的自增列；未声明时返回 <c>null</c>。</summary>
    public TableColumn? AutoIncrementColumn
        => Columns.FirstOrDefault(static c => c.IsAutoIncrement);

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
            if (!string.IsNullOrWhiteSpace(index.JsonPath) && index.Columns.Count != 1)
                throw new ArgumentException($"JSON path 索引 '{index.Name}' 只能引用 1 个 JSON 列。", nameof(indexes));

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

            if (!string.IsNullOrWhiteSpace(index.JsonPath))
            {
                var column = columns.First(c => string.Equals(c.Name, index.Columns[0], StringComparison.Ordinal));
                if (column.DataType != TableColumnType.Json)
                    throw new ArgumentException($"JSON path 索引 '{index.Name}' 的列 '{column.Name}' 必须是 JSON 类型。", nameof(indexes));
            }

            result.Add(new TableIndex(
                index.Name,
                indexColumns.AsReadOnly(),
                index.IsUnique,
                index.CreatedAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : index.CreatedAtUtcTicks,
                index.JsonPath));
        }

        return result;
    }

    private static List<TableForeignKey> BuildForeignKeys(
        string tableName,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<TableForeignKeyDefinition>? foreignKeys)
    {
        var result = new List<TableForeignKey>();
        if (foreignKeys is null || foreignKeys.Count == 0)
            return result;

        var columnNames = columns.Select(static c => c.Name).ToHashSet(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < foreignKeys.Count; i++)
        {
            var foreignKey = foreignKeys[i];
            var name = string.IsNullOrWhiteSpace(foreignKey.Name)
                ? $"fk_{tableName}_{i + 1}"
                : foreignKey.Name;
            if (!seenNames.Add(name))
                throw new ArgumentException($"关系表 '{tableName}' 中外键 '{name}' 重复。", nameof(foreignKeys));
            if (foreignKey.Columns.Count == 0 || foreignKey.PrincipalColumns.Count == 0)
                throw new ArgumentException($"外键 '{name}' 必须声明本表列和引用列。", nameof(foreignKeys));
            if (foreignKey.Columns.Count != foreignKey.PrincipalColumns.Count)
                throw new ArgumentException($"外键 '{name}' 的本表列数与引用列数不一致。", nameof(foreignKeys));
            ArgumentException.ThrowIfNullOrWhiteSpace(foreignKey.PrincipalTable);

            var seenColumns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in foreignKey.Columns)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(column);
                if (!columnNames.Contains(column))
                    throw new ArgumentException($"外键 '{name}' 引用了未知列 '{column}'。", nameof(foreignKeys));
                if (!seenColumns.Add(column))
                    throw new ArgumentException($"外键 '{name}' 中列 '{column}' 重复。", nameof(foreignKeys));
                if (foreignKey.OnDelete == ForeignKeyAction.SetNull)
                {
                    var columnDef = columns.First(c => string.Equals(c.Name, column, StringComparison.Ordinal));
                    if (!columnDef.IsNullable)
                        throw new ArgumentException(
                            $"外键 '{name}' 声明 ON DELETE SET NULL，但列 '{column}' 不可为空。", nameof(foreignKeys));
                }
            }

            result.Add(new TableForeignKey(
                name,
                foreignKey.Columns.ToArray(),
                foreignKey.PrincipalTable,
                foreignKey.PrincipalColumns.ToArray(),
                foreignKey.OnDelete));
        }

        return result;
    }

    private static List<TableCheckConstraint> BuildCheckConstraints(
        string tableName,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<TableCheckConstraintDefinition>? checkConstraints)
    {
        var result = new List<TableCheckConstraint>();
        if (checkConstraints is null || checkConstraints.Count == 0)
            return result;

        var columnNames = columns.Select(static column => column.Name).ToHashSet(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < checkConstraints.Count; i++)
        {
            var definition = checkConstraints[i];
            var name = string.IsNullOrWhiteSpace(definition.Name)
                ? $"ck_{tableName}_{i + 1}"
                : definition.Name;
            if (!seenNames.Add(name))
                throw new ArgumentException($"关系表 '{tableName}' 中检查约束 '{name}' 重复。", nameof(checkConstraints));

            result.Add(TableCheckConstraint.Create(
                tableName,
                columnNames,
                name,
                definition.ExpressionSql));
        }

        return result;
    }

    private static void ValidateRowVersionColumns(string tableName, IReadOnlyList<TableColumn> columns)
    {
        var rowVersionColumns = columns.Where(static c => c.IsRowVersion).ToArray();
        if (rowVersionColumns.Length > 1)
            throw new ArgumentException($"关系表 '{tableName}' 最多只能声明一个 ROWVERSION 列。", nameof(columns));
        if (rowVersionColumns is [var column] && column.DataType != TableColumnType.Int64)
            throw new ArgumentException($"ROWVERSION 列 '{column.Name}' 必须是 INT 类型。", nameof(columns));
    }

    private IReadOnlyList<TableIndexDefinition> IndexDefinitions((string OldName, string NewName)? renameColumn = null)
        => Indexes.Select(i =>
        {
            var columns = i.Columns;
            if (renameColumn is { } rename)
            {
                columns = i.Columns
                    .Select(c => string.Equals(c, rename.OldName, StringComparison.Ordinal) ? rename.NewName : c)
                    .ToArray();
            }

            return new TableIndexDefinition(i.Name, columns, i.IsUnique, i.CreatedAtUtcTicks, i.JsonPath);
        }).ToArray();

    private IReadOnlyList<TableForeignKeyDefinition> ForeignKeyDefinitions((string OldName, string NewName)? renameColumn = null)
        => ForeignKeys.Select(f =>
        {
            var columns = f.Columns;
            if (renameColumn is { } rename)
            {
                columns = f.Columns
                    .Select(c => string.Equals(c, rename.OldName, StringComparison.Ordinal) ? rename.NewName : c)
                    .ToArray();
            }

            return new TableForeignKeyDefinition(f.Name, columns, f.PrincipalTable, f.PrincipalColumns, f.OnDelete);
        }).ToArray();

    private IReadOnlyList<TableCheckConstraintDefinition> CheckConstraintDefinitions()
        => CheckConstraints
            .Select(static constraint => new TableCheckConstraintDefinition(
                constraint.Name,
                constraint.ExpressionSql))
            .ToArray();

    private IReadOnlyDictionary<string, string?> ColumnDefaultDefinitions(
        (string OldName, string NewName)? renameColumn = null,
        string? exceptColumn = null)
    {
        var defaults = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var column in Columns)
        {
            if (column.DefaultExpressionSql is null
                || string.Equals(column.Name, exceptColumn, StringComparison.Ordinal))
            {
                continue;
            }

            var name = renameColumn is { } rename
                && string.Equals(column.Name, rename.OldName, StringComparison.Ordinal)
                    ? rename.NewName
                    : column.Name;
            defaults.Add(name, column.DefaultExpressionSql);
        }

        return defaults;
    }

    private IReadOnlySet<string> RowVersionColumnNames(
        (string OldName, string NewName)? renameColumn = null,
        string? exceptColumn = null)
        => Columns
            .Where(c => c.IsRowVersion && !string.Equals(c.Name, exceptColumn, StringComparison.Ordinal))
            .Select(c => renameColumn is { } rename && string.Equals(c.Name, rename.OldName, StringComparison.Ordinal)
                ? rename.NewName
                : c.Name)
            .ToHashSet(StringComparer.Ordinal);

    private IReadOnlySet<string> AutoIncrementColumnNames(
        (string OldName, string NewName)? renameColumn = null,
        string? exceptColumn = null)
        => Columns
            .Where(c => c.IsAutoIncrement && !string.Equals(c.Name, exceptColumn, StringComparison.Ordinal))
            .Select(c => renameColumn is { } rename && string.Equals(c.Name, rename.OldName, StringComparison.Ordinal)
                ? rename.NewName
                : c.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static string BuildEfForeignKeyName(string tableName, TableForeignKey foreignKey)
        => "FK_" + tableName + "_" + foreignKey.PrincipalTable + "_" + string.Join("_", foreignKey.Columns);
}
