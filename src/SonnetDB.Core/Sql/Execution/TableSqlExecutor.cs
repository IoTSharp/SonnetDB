using System.Globalization;
using System.Text;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 关系表 MVP 的 SQL 执行辅助。表数据存放在 <see cref="TableStore"/> 的 KV-backed rowstore 中。
/// </summary>
internal static class TableSqlExecutor
{
    private static readonly IReadOnlyList<string> _nameColumns =
        new List<string>(1) { "name" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _describeTableColumns =
        new List<string>(5) { "column_name", "data_type", "is_nullable", "is_primary_key", "ordinal" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showIndexColumns =
        new List<string>(4) { "index_name", "is_unique", "columns", "created_utc" }.AsReadOnly();

    public static TableSchema ExecuteCreateTable(Tsdb tsdb, CreateTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (statement.IfNotExists)
        {
            var existing = tsdb.Tables.Catalog.TryGet(statement.Name);
            if (existing is not null)
                return existing;
        }

        var columns = new List<(string Name, TableColumnType DataType, bool IsNullable)>(statement.Columns.Count);
        foreach (var column in statement.Columns)
        {
            columns.Add((
                column.Name,
                MapTableColumnType(column.DataType),
                column.Nullability != ColumnNullability.NotNull));
        }

        var foreignKeys = statement.ForeignKeyClauses
            .Select(static fk => new TableForeignKeyDefinition(
                Name: string.Empty,
                fk.Columns,
                fk.PrincipalTable,
                fk.PrincipalColumns,
                fk.OnDelete))
            .ToArray();
        var rowVersionColumns = statement.Columns
            .Where(static c => c.IsRowVersion)
            .Select(static c => c.Name)
            .ToHashSet(StringComparer.Ordinal);
        var schema = TableSchema.Create(
            statement.Name,
            columns,
            statement.PrimaryKey,
            foreignKeys: foreignKeys,
            rowVersionColumns: rowVersionColumns);
        tsdb.Tables.Create(schema);
        return schema;
    }

    public static TableIndex ExecuteCreateIndex(Tsdb tsdb, CreateTableIndexStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var schema = tsdb.Tables.Catalog.TryGet(statement.TableName)
            ?? throw new InvalidOperationException($"table '{statement.TableName}' 不存在。");
        if (statement.IfNotExists && schema.TryGetIndex(statement.IndexName) is { } existing)
            return existing;

        return tsdb.Tables.CreateIndex(
            statement.TableName,
            new TableIndexDefinition(statement.IndexName, statement.Columns, statement.IsUnique));
    }

    public static TableIndex ExecuteCreateJsonPathIndex(Tsdb tsdb, CreateTableJsonPathIndexStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var schema = tsdb.Tables.Catalog.TryGet(statement.TableName)
            ?? throw new InvalidOperationException($"table '{statement.TableName}' 不存在。");
        if (statement.IfNotExists && schema.TryGetIndex(statement.IndexName) is { } existing)
            return existing;

        var column = schema.TryGetColumn(statement.JsonColumnName)
            ?? throw new InvalidOperationException($"table '{statement.TableName}' 中不存在列 '{statement.JsonColumnName}'。");
        if (column.DataType != TableColumnType.Json)
            throw new InvalidOperationException($"JSON path 索引列 '{statement.JsonColumnName}' 必须是 JSON 类型。");

        var path = JsonPath.Parse(statement.Path);
        return tsdb.Tables.CreateIndex(
            statement.TableName,
            new TableIndexDefinition(statement.IndexName, [statement.JsonColumnName], IsUnique: false, JsonPath: path.Text));
    }

    public static RowsAffectedExecutionResult ExecuteDropTable(Tsdb tsdb, DropTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        bool removed = tsdb.Tables.Drop(statement.Name);
        if (!removed && !statement.IfExists)
            throw new InvalidOperationException($"table '{statement.Name}' 不存在。");

        return new RowsAffectedExecutionResult(statement.Name, removed ? 1 : 0, "drop_table");
    }

    public static RowsAffectedExecutionResult ExecuteDropIndex(Tsdb tsdb, DropTableIndexStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        bool removed = tsdb.Tables.DropIndex(statement.TableName, statement.IndexName);
        return new RowsAffectedExecutionResult(statement.TableName, removed ? 1 : 0, "drop_index");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableAddColumn(Tsdb tsdb, AlterTableAddColumnStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var dataType = MapTableColumnType(statement.DataType);
        var isNullable = statement.Nullability != ColumnNullability.NotNull;
        object? defaultValue = null;
        if (statement.DefaultExpression is not null)
        {
            var tempColumn = new TableColumn(statement.ColumnName, dataType, IsPrimaryKey: false, isNullable, Ordinal: 0);
            defaultValue = ConvertTableValue(statement.DefaultExpression, tempColumn);
        }
        else if (!isNullable)
        {
            throw new InvalidOperationException("ALTER TABLE ADD COLUMN 添加 NOT NULL 列时必须提供 DEFAULT。");
        }

        tsdb.Tables.AlterTableAddColumn(statement.TableName, statement.ColumnName, dataType, isNullable, defaultValue);
        return new RowsAffectedExecutionResult(statement.TableName, 1, "alter_table_add_column");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableDropColumn(Tsdb tsdb, AlterTableDropColumnStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (statement.IfExists)
        {
            var schema = tsdb.Tables.Catalog.TryGet(statement.TableName)
                ?? throw new InvalidOperationException($"table '{statement.TableName}' 不存在。");
            if (schema.TryGetColumn(statement.ColumnName) is null)
                return new RowsAffectedExecutionResult(statement.TableName, 0, "alter_table_drop_column");
        }

        tsdb.Tables.AlterTableDropColumn(statement.TableName, statement.ColumnName);
        return new RowsAffectedExecutionResult(statement.TableName, 1, "alter_table_drop_column");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableDropConstraint(Tsdb tsdb, AlterTableDropConstraintStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        bool removed = tsdb.Tables.DropForeignKey(statement.TableName, statement.ConstraintName);
        return new RowsAffectedExecutionResult(statement.TableName, removed ? 1 : 0, "alter_table_drop_constraint");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableRenameColumn(Tsdb tsdb, AlterTableRenameColumnStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        tsdb.Tables.AlterTableRenameColumn(statement.TableName, statement.OldColumnName, statement.NewColumnName);
        return new RowsAffectedExecutionResult(statement.TableName, 1, "alter_table_rename_column");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableRenameTable(Tsdb tsdb, AlterTableRenameTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        tsdb.Tables.RenameTable(statement.OldTableName, statement.NewTableName);
        return new RowsAffectedExecutionResult(statement.NewTableName, 1, "alter_table_rename_table");
    }

    public static InsertExecutionResult ExecuteInsert(Tsdb tsdb, InsertStatement statement, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var bindings = BindInsertColumns(statement, schema);

        var mutations = new List<TableRowMutation>(statement.Rows.Count);
        foreach (var row in statement.Rows)
        {
            var values = new object?[schema.Columns.Count];
            for (int i = 0; i < bindings.Length; i++)
            {
                var column = bindings[i];
                values[column.Ordinal] = ConvertTableValue(row[i], column);
            }

            ApplyInsertRowVersion(schema, values);
            ValidateRequiredColumns(schema, values);
            mutations.Add(new TableRowMutation(PrimaryKeyValues: null, values));
        }

        int inserted = tsdb.Tables.ApplyTransaction(
            new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
            {
                [schema.Name] = mutations,
            });
        return new InsertExecutionResult(schema.Name, inserted);
    }

    public static InsertExecutionResult QueueInsert(SqlTransactionContext transaction, InsertStatement statement, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var bindings = BindInsertColumns(statement, schema);
        foreach (var row in statement.Rows)
        {
            var values = new object?[schema.Columns.Count];
            for (int i = 0; i < bindings.Length; i++)
            {
                var column = bindings[i];
                values[column.Ordinal] = ConvertTableValue(row[i], column);
            }

            ApplyInsertRowVersion(schema, values);
            ValidateRequiredColumns(schema, values);
            transaction.AddTableMutation(schema.Name, new TableRowMutation(PrimaryKeyValues: null, values));
        }

        return new InsertExecutionResult(schema.Name, statement.Rows.Count);
    }

    public static SelectExecutionResult ExecuteSelect(Tsdb tsdb, SelectStatement statement, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        ValidateTableAliasReferences(statement);
        if (statement.TableValuedFunction is not null)
            throw new InvalidOperationException("关系表 SELECT 不支持 FROM 表值函数。");
        if (statement.GroupBy.Count != 0)
            throw new InvalidOperationException("关系表 MVP 暂不支持 GROUP BY。");

        var projections = BuildProjections(statement.Projections, schema);
        var rows = LoadSelectCandidateRows(tsdb.Tables.Open(schema.Name), schema, statement.Where);
        var filtered = new List<IReadOnlyList<object?>>();
        foreach (var row in rows)
        {
            if (!EvaluateWhere(statement.Where, schema, row.Values))
                continue;

            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
                output[i] = EvaluateProjection(projections[i], schema, row.Values);
            filtered.Add(output);
        }

        var result = new SelectExecutionResult(
            projections.Select(static p => p.ColumnName).ToArray(),
            filtered);
        return ApplyOrderByAndPagination(result, statement.OrderByList, statement.Pagination);
    }

    public static RowsAffectedExecutionResult ExecuteDelete(Tsdb tsdb, DeleteStatement statement, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        int deleted = 0;
        if (TryExtractPrimaryKeyValues(schema, statement.Where, allowExtraPredicates: false, out var keyValues))
        {
            deleted = tsdb.Tables.ApplyTransaction(
                new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
                {
                    [schema.Name] = [new TableRowMutation(keyValues, NewValues: null)],
                });
            return new RowsAffectedExecutionResult(schema.Name, deleted, "delete");
        }

        var store = tsdb.Tables.Open(schema.Name);
        var mutations = new List<TableRowMutation>();
        foreach (var row in LoadCandidateRows(store, schema, statement.Where))
        {
            if (!EvaluateWhere(statement.Where, schema, row.Values))
                continue;

            var primaryKeyValues = ExtractPrimaryKeyValues(schema, row.Values);
            mutations.Add(new TableRowMutation(primaryKeyValues, NewValues: null, ExtractRowVersion(schema, row.Values)));
        }

        deleted = tsdb.Tables.ApplyTransaction(
            new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
            {
                [schema.Name] = mutations,
            });
        return new RowsAffectedExecutionResult(schema.Name, deleted, "delete");
    }

    public static RowsAffectedExecutionResult ExecuteUpdate(Tsdb tsdb, UpdateStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var schema = tsdb.Tables.Catalog.TryGet(statement.TableName)
            ?? throw new InvalidOperationException($"table '{statement.TableName}' 不存在。");
        var store = tsdb.Tables.Open(schema.Name);
        var assignments = BindAssignments(statement, schema);

        var mutations = new List<TableRowMutation>();
        foreach (var row in LoadCandidateRows(store, schema, statement.Where))
        {
            if (!EvaluateWhere(statement.Where, schema, row.Values))
                continue;

            var values = row.Values.ToArray();
            foreach (var assignment in assignments)
                values[assignment.Column.Ordinal] = ConvertTableValue(assignment.Value, assignment.Column);

            ValidateRequiredColumns(schema, values);
            var expectedRowVersion = ExtractRowVersion(schema, row.Values);
            ApplyUpdateRowVersion(schema, values, expectedRowVersion);
            mutations.Add(new TableRowMutation(ExtractPrimaryKeyValues(schema, row.Values), values, expectedRowVersion));
        }

        ThrowIfStaleRowVersionPredicate(schema, store, statement.Where, mutations.Count);

        int updated = tsdb.Tables.ApplyTransaction(
            new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
            {
                [schema.Name] = mutations,
            });
        return new RowsAffectedExecutionResult(schema.Name, updated, "update");
    }

    public static RowsAffectedExecutionResult QueueUpdate(SqlTransactionContext transaction, Tsdb tsdb, UpdateStatement statement)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var schema = tsdb.Tables.Catalog.TryGet(statement.TableName)
            ?? throw new InvalidOperationException($"table '{statement.TableName}' 不存在。");
        var store = tsdb.Tables.Open(schema.Name);
        var assignments = BindAssignments(statement, schema);

        int updated = 0;
        foreach (var row in LoadCandidateRows(store, schema, statement.Where))
        {
            if (!EvaluateWhere(statement.Where, schema, row.Values))
                continue;

            var values = row.Values.ToArray();
            foreach (var assignment in assignments)
                values[assignment.Column.Ordinal] = ConvertTableValue(assignment.Value, assignment.Column);

            ValidateRequiredColumns(schema, values);
            var expectedRowVersion = ExtractRowVersion(schema, row.Values);
            ApplyUpdateRowVersion(schema, values, expectedRowVersion);
            transaction.AddTableMutation(
                schema.Name,
                new TableRowMutation(ExtractPrimaryKeyValues(schema, row.Values), values, expectedRowVersion));
            updated++;
        }

        ThrowIfStaleRowVersionPredicate(schema, store, statement.Where, updated);

        return new RowsAffectedExecutionResult(schema.Name, updated, "update");
    }

    public static RowsAffectedExecutionResult QueueDelete(SqlTransactionContext transaction, Tsdb tsdb, DeleteStatement statement, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var store = tsdb.Tables.Open(schema.Name);
        int deleted = 0;
        foreach (var row in LoadCandidateRows(store, schema, statement.Where))
        {
            if (!EvaluateWhere(statement.Where, schema, row.Values))
                continue;

            transaction.AddTableMutation(
                schema.Name,
                new TableRowMutation(ExtractPrimaryKeyValues(schema, row.Values), NewValues: null, ExtractRowVersion(schema, row.Values)));
            deleted++;
        }

        return new RowsAffectedExecutionResult(schema.Name, deleted, "delete");
    }

    public static RowsAffectedExecutionResult CommitTransaction(Tsdb tsdb, SqlTransactionContext transaction)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(transaction);

        int affected = tsdb.Tables.ApplyTransaction(transaction.SnapshotTableMutations());

        transaction.MarkCompleted();
        return new RowsAffectedExecutionResult("*", affected, "commit");
    }

    public static SelectExecutionResult ShowTables(Tsdb tsdb)
    {
        ArgumentNullException.ThrowIfNull(tsdb);

        var snapshot = tsdb.Tables.Catalog.Snapshot();
        var rows = new List<IReadOnlyList<object?>>(snapshot.Count);
        foreach (var schema in snapshot)
            rows.Add(new object?[] { schema.Name });
        return new SelectExecutionResult(_nameColumns, rows);
    }

    public static SelectExecutionResult DescribeTable(Tsdb tsdb, string name)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var schema = tsdb.Tables.Catalog.TryGet(name)
            ?? throw new InvalidOperationException($"table '{name}' 不存在。");
        var rows = new List<IReadOnlyList<object?>>(schema.Columns.Count);
        foreach (var column in schema.Columns)
        {
            rows.Add(new object?[]
            {
                column.Name,
                FormatTableColumnType(column.DataType),
                column.IsNullable,
                column.IsPrimaryKey,
                (long)column.Ordinal,
            });
        }

        return new SelectExecutionResult(_describeTableColumns, rows);
    }

    public static SelectExecutionResult ShowIndexes(Tsdb tsdb, string tableName)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var schema = tsdb.Tables.Catalog.TryGet(tableName)
            ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
        var rows = new List<IReadOnlyList<object?>>(schema.Indexes.Count);
        foreach (var index in schema.Indexes.OrderBy(static i => i.Name, StringComparer.Ordinal))
        {
            rows.Add(new object?[]
            {
                index.Name,
                index.IsUnique,
                FormatIndexColumns(index),
                new DateTime(index.CreatedAtUtcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
            });
        }

        return new SelectExecutionResult(_showIndexColumns, rows);
    }

    public static object? ConvertLiteralForIndex(TableSchema schema, string columnName, SqlExpression expression)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(expression);

        var column = schema.TryGetColumn(columnName)
            ?? throw new InvalidOperationException($"table '{schema.Name}' 中不存在列 '{columnName}'。");
        return ConvertTableValue(expression, column);
    }

    private static TableColumn[] BindInsertColumns(InsertStatement statement, TableSchema schema)
    {
        var bindings = new TableColumn[statement.Columns.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < statement.Columns.Count; i++)
        {
            var name = statement.Columns[i];
            if (!seen.Add(name))
                throw new InvalidOperationException($"INSERT 列列表中列 '{name}' 重复。");

            bindings[i] = schema.TryGetColumn(name)
                ?? throw new InvalidOperationException($"table '{schema.Name}' 中不存在列 '{name}'。");
        }

        return bindings;
    }

    private static BoundAssignment[] BindAssignments(UpdateStatement statement, TableSchema schema)
    {
        var assignments = new List<BoundAssignment>(statement.Assignments.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in statement.Assignments)
        {
            if (!seen.Add(assignment.ColumnName))
                throw new InvalidOperationException($"UPDATE SET 中列 '{assignment.ColumnName}' 重复。");

            var column = schema.TryGetColumn(assignment.ColumnName)
                ?? throw new InvalidOperationException($"table '{schema.Name}' 中不存在列 '{assignment.ColumnName}'。");
            if (column.IsPrimaryKey)
                throw new InvalidOperationException("关系表 MVP 暂不支持更新 PRIMARY KEY 列。");

            assignments.Add(new BoundAssignment(column, assignment.Value));
        }

        return [.. assignments];
    }

    private static void ValidateRequiredColumns(TableSchema schema, IReadOnlyList<object?> values)
    {
        if (values.Count != schema.Columns.Count)
            throw new InvalidOperationException("内部错误：行值数量与 schema 列数量不一致。");

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var column = schema.Columns[i];
            if (values[i] is null && !column.IsNullable)
                throw new InvalidOperationException($"列 '{column.Name}' 不允许为 NULL。");
        }
    }

    private static void ApplyInsertRowVersion(TableSchema schema, object?[] values)
    {
        if (schema.RowVersionColumn is { } column)
            values[column.Ordinal] = 1L;
    }

    private static void ApplyUpdateRowVersion(TableSchema schema, object?[] values, long? expectedRowVersion)
    {
        if (schema.RowVersionColumn is not { } column)
            return;

        values[column.Ordinal] = checked((expectedRowVersion ?? 0L) + 1L);
    }

    private static long? ExtractRowVersion(TableSchema schema, IReadOnlyList<object?> values)
    {
        if (schema.RowVersionColumn is not { } column)
            return null;

        return values[column.Ordinal] is null
            ? 0L
            : Convert.ToInt64(values[column.Ordinal], CultureInfo.InvariantCulture);
    }

    private static void ThrowIfStaleRowVersionPredicate(
        TableSchema schema,
        TableStore store,
        SqlExpression where,
        int matchedRows)
    {
        if (matchedRows != 0 || schema.RowVersionColumn is not { } rowVersionColumn)
            return;
        if (!TryExtractPrimaryKeyValues(schema, where, allowExtraPredicates: true, out var keyValues))
            return;
        if (!TryCollectEqualityExpressions(where, allowNonEquality: true, out var equalityByColumn))
            return;
        if (!equalityByColumn.TryGetValue(rowVersionColumn.Name, out var expectedExpression))
            return;

        var existing = store.GetByPrimaryKey(keyValues);
        if (existing is null)
            return;

        var expected = ConvertTableValue(expectedExpression, rowVersionColumn);
        var actual = existing.Values[rowVersionColumn.Ordinal];
        if (!ValuesEqual(expected, actual))
        {
            throw new TableConstraintException(
                TableConstraintException.ConcurrencyConflict,
                schema.Name,
                rowVersionColumn.Name,
                $"table '{schema.Name}' 乐观并发冲突：列 '{rowVersionColumn.Name}' 当前版本已变化。");
        }
    }

    internal static IReadOnlyList<TableRow> LoadCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
    {
        if (TryExtractPrimaryKeyValues(schema, where, allowExtraPredicates: true, out var keyValues))
        {
            var row = store.GetByPrimaryKey(keyValues);
            return row is null ? Array.Empty<TableRow>() : [row];
        }

        if (TryExtractSecondaryIndexValues(schema, where, out var index, out var indexValues))
            return store.GetByIndex(index, indexValues);

        return store.Scan();
    }

    /// <summary>
    /// SELECT 候选行加载：在已提交基线上叠加当前 ambient 轻事务对本表的缓冲写（read-your-writes，#218）。
    /// 无活动事务、事务已结束或该表无缓冲写时走既有 PK/二级索引/scan 快路径；一旦本表有缓冲写，
    /// 则改为全表 scan 后叠加缓冲变更（快路径可能漏掉尚未提交的插入行或返回被缓冲更新覆盖前的旧值），
    /// 由调用方 WHERE 再过滤。仅用于 SELECT 读路径，不改变 queue update/delete 的候选加载。
    /// </summary>
    internal static IReadOnlyList<TableRow> LoadSelectCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
    {
        var transaction = SqlTransactionContext.Current;
        if (transaction is not null && transaction.TryGetBufferedMutations(schema.Name, out var buffered))
            return ApplyMutationOverlay(schema, store.Scan(), buffered);

        return LoadCandidateRows(store, schema, where);
    }

    /// <summary>
    /// 把轻事务缓冲的 insert/update/delete 叠加到已提交基线行上（按主键合并，保序追加新插入）。
    /// 主键编码复用 <see cref="TableKeyCodec"/>，与 COMMIT 时 <see cref="TableStore.ApplyBatch"/> 的键语义一致。
    /// </summary>
    private static IReadOnlyList<TableRow> ApplyMutationOverlay(
        TableSchema schema,
        IReadOnlyList<TableRow> baseRows,
        IReadOnlyList<TableRowMutation> mutations)
    {
        var order = new List<string>(baseRows.Count + mutations.Count);
        var byKey = new Dictionary<string, TableRow>(baseRows.Count + mutations.Count, StringComparer.Ordinal);

        foreach (var row in baseRows)
        {
            var key = Convert.ToHexString(TableKeyCodec.EncodePrimaryKey(schema, row.Values));
            if (byKey.TryAdd(key, row))
                order.Add(key);
            else
                byKey[key] = row;
        }

        foreach (var mutation in mutations)
        {
            if (mutation.NewValues is not null)
            {
                var pk = mutation.PrimaryKeyValues is not null
                    ? TableKeyCodec.EncodePrimaryKeyValues(schema, mutation.PrimaryKeyValues)
                    : TableKeyCodec.EncodePrimaryKey(schema, mutation.NewValues);
                var key = Convert.ToHexString(pk);
                var newRow = new TableRow(mutation.NewValues.ToArray(), pk);
                if (byKey.TryAdd(key, newRow))
                    order.Add(key);
                else
                    byKey[key] = newRow;
            }
            else
            {
                var key = Convert.ToHexString(TableKeyCodec.EncodePrimaryKeyValues(schema, mutation.PrimaryKeyValues!));
                byKey.Remove(key);
            }
        }

        var result = new List<TableRow>(order.Count);
        foreach (var key in order)
            if (byKey.TryGetValue(key, out var row))
                result.Add(row);
        return result;
    }

    private static IReadOnlyList<object?> ExtractPrimaryKeyValues(TableSchema schema, IReadOnlyList<object?> row)
    {
        var values = new object?[schema.PrimaryKey.Count];
        for (int i = 0; i < schema.PrimaryKey.Count; i++)
        {
            var column = schema.TryGetColumn(schema.PrimaryKey[i])
                ?? throw new InvalidOperationException($"PRIMARY KEY 引用了未知列 '{schema.PrimaryKey[i]}'。");
            values[i] = row[column.Ordinal];
        }

        return values;
    }

    private static bool TryExtractPrimaryKeyValues(
        TableSchema schema,
        SqlExpression? where,
        bool allowExtraPredicates,
        out IReadOnlyList<object?> keyValues)
    {
        keyValues = Array.Empty<object?>();
        if (where is null)
            return false;

        if (!TryCollectEqualityExpressions(where, allowNonEquality: false, out var equalityByColumn))
            return false;

        var values = new object?[schema.PrimaryKey.Count];
        if (!allowExtraPredicates && equalityByColumn.Count != schema.PrimaryKey.Count)
            return false;

        for (int i = 0; i < schema.PrimaryKey.Count; i++)
        {
            var keyColumnName = schema.PrimaryKey[i];
            if (!equalityByColumn.TryGetValue(keyColumnName, out var expression))
                return false;

            var column = schema.TryGetColumn(keyColumnName)
                ?? throw new InvalidOperationException($"PRIMARY KEY 引用了未知列 '{keyColumnName}'。");
            try
            {
                values[i] = ConvertTableValue(expression, column);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        keyValues = values;
        return true;
    }

    internal static TableIndex? ChooseBestIndexForWhere(
        TableSchema schema,
        SqlExpression? where,
        out IReadOnlyList<object?> indexValues)
    {
        if (TryExtractPrimaryKeyValues(schema, where, allowExtraPredicates: true, out var primaryKeyValues))
        {
            indexValues = primaryKeyValues;
            return null;
        }

        if (TryExtractSecondaryIndexValues(schema, where, out var index, out var values))
        {
            indexValues = values;
            return index;
        }

        indexValues = [];
        return null;
    }

    private static bool TryExtractSecondaryIndexValues(
        TableSchema schema,
        SqlExpression? where,
        out TableIndex index,
        out IReadOnlyList<object?> indexValues)
    {
        index = null!;
        indexValues = [];
        if (where is null || schema.Indexes.Count == 0)
            return false;

        foreach (var candidate in schema.Indexes.OrderByDescending(static i => i.Columns.Count))
        {
            if (!string.IsNullOrWhiteSpace(candidate.JsonPath))
            {
                if (TryExtractJsonPathIndexValue(candidate, where, out var jsonPathValue))
                {
                    index = candidate;
                    indexValues = [jsonPathValue];
                    return true;
                }

                continue;
            }

            if (!TryCollectEqualityExpressions(where, allowNonEquality: true, out var equalityByColumn))
                return false;

            var values = new object?[candidate.Columns.Count];
            var matched = true;
            for (int i = 0; i < candidate.Columns.Count; i++)
            {
                if (!equalityByColumn.TryGetValue(candidate.Columns[i], out var expression))
                {
                    matched = false;
                    break;
                }

                var column = schema.TryGetColumn(candidate.Columns[i])
                    ?? throw new InvalidOperationException($"索引 '{candidate.Name}' 引用了未知列 '{candidate.Columns[i]}'。");
                try
                {
                    values[i] = ConvertTableValue(expression, column);
                }
                catch (InvalidOperationException)
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            index = candidate;
            indexValues = values;
            return true;
        }

        return false;
    }

    private static bool TryExtractJsonPathIndexValue(
        TableIndex index,
        SqlExpression? where,
        out object? value)
    {
        value = null;
        if (where is null || string.IsNullOrWhiteSpace(index.JsonPath) || index.Columns.Count != 1)
            return false;

        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
                continue;

            if (!TryExtractJsonValueComparison(binary, out var columnName, out var path, out var literalValue))
                continue;

            if (string.Equals(columnName, index.Columns[0], StringComparison.Ordinal)
                && string.Equals(path.Text, index.JsonPath, StringComparison.Ordinal))
            {
                value = literalValue;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractJsonValueComparison(
        BinaryExpression binary,
        out string columnName,
        out JsonPath path,
        out object? literalValue)
    {
        columnName = string.Empty;
        path = null!;
        literalValue = null;
        if (TryBindJsonValue(binary.Left, out columnName, out path) && TryEvaluateLiteral(binary.Right, out literalValue))
            return true;
        if (TryBindJsonValue(binary.Right, out columnName, out path) && TryEvaluateLiteral(binary.Left, out literalValue))
            return true;
        return false;
    }

    private static bool TryBindJsonValue(SqlExpression expression, out string columnName, out JsonPath path)
    {
        columnName = string.Empty;
        path = null!;
        if (expression is not FunctionCallExpression
            {
                Name: var name,
                IsStar: false,
                Arguments.Count: 2,
                Arguments: [IdentifierExpression jsonColumn, LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var pathText }]
            }
            || !string.Equals(name, "json_value", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            path = JsonPath.Parse(pathText!);
            columnName = jsonColumn.Name;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryEvaluateLiteral(SqlExpression expression, out object? value)
    {
        value = null;
        if (expression is not LiteralExpression literal)
            return false;
        value = EvaluateLiteral(literal);
        return true;
    }

    private static bool TryCollectEqualityExpressions(
        SqlExpression where,
        bool allowNonEquality,
        out Dictionary<string, SqlExpression> equalityByColumn)
    {
        equalityByColumn = new Dictionary<string, SqlExpression>(StringComparer.Ordinal);
        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
            {
                if (allowNonEquality)
                    continue;
                return false;
            }

            var (identifier, value) = NormalizeIdentifierComparison(binary);
            if (identifier is null || value is null)
            {
                if (allowNonEquality)
                    continue;
                return false;
            }

            if (!equalityByColumn.TryAdd(identifier.Name, value))
                return false;
        }

        return equalityByColumn.Count > 0;
    }

    private static (IdentifierExpression? Identifier, SqlExpression? Value) NormalizeIdentifierComparison(BinaryExpression binary)
    {
        if (binary.Left is IdentifierExpression left)
            return (left, binary.Right);
        if (binary.Right is IdentifierExpression right)
            return (right, binary.Left);
        return (null, null);
    }

    private static Projection[] BuildProjections(IReadOnlyList<SelectItem> items, TableSchema schema)
    {
        var projections = new List<Projection>(items.Count);
        foreach (var item in items)
        {
            switch (item.Expression)
            {
                case StarExpression:
                    if (item.Alias is not null)
                        throw new InvalidOperationException("'*' 不允许带 alias。");
                    foreach (var column in schema.Columns)
                        projections.Add(Projection.ForColumn(column, column.Name));
                    break;

                case IdentifierExpression id:
                    var selectedColumn = schema.TryGetColumn(id.Name)
                        ?? throw new InvalidOperationException($"SELECT 中引用了未知列 '{id.Name}'。");
                    projections.Add(Projection.ForColumn(selectedColumn, item.Alias ?? selectedColumn.Name));
                    break;

                case LiteralExpression literal:
                    projections.Add(Projection.Constant(EvaluateLiteral(literal), item.Alias ?? FormatLiteralColumnName(literal)));
                    break;

                case FunctionCallExpression function:
                    projections.Add(Projection.Expression(item.Alias ?? FormatFunctionColumnName(function), function));
                    break;

                case CaseExpression caseExpression:
                    projections.Add(Projection.Expression(item.Alias ?? "case", caseExpression));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"关系表 SELECT 暂不支持投影表达式 '{item.Expression.GetType().Name}'。");
            }
        }

        return [.. projections];
    }

    private static object? EvaluateProjection(Projection projection, TableSchema schema, IReadOnlyList<object?> row)
        => projection.Kind switch
        {
            ProjectionKind.Column => row[projection.Column!.Ordinal],
            ProjectionKind.Constant => projection.ConstantValue,
            ProjectionKind.Expression => EvaluateScalar(projection.ExpressionValue!, schema, row),
            _ => throw new InvalidOperationException("未知关系表投影类型。"),
        };

    internal static bool EvaluateWhere(SqlExpression? expression, TableSchema schema, IReadOnlyList<object?> row)
    {
        if (expression is null)
            return true;

        // 三值逻辑：仅当谓词确定为 TRUE 时保留该行；UNKNOWN（NULL 传播）与 FALSE 一样排除。
        return EvaluateBoolean(expression, schema, row);
    }

    private static bool EvaluateBoolean(SqlExpression expression, TableSchema schema, IReadOnlyList<object?> row)
        => EvaluateKleene(expression, schema, row) == true;

    private static bool? EvaluateKleene(SqlExpression expression, TableSchema schema, IReadOnlyList<object?> row)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                {
                    var left = EvaluateKleene(binary.Left, schema, row);
                    if (left == false) return false;
                    var right = EvaluateKleene(binary.Right, schema, row);
                    if (right == false) return false;
                    return left is null || right is null ? null : true;
                }
                if (binary.Operator == SqlBinaryOperator.Or)
                {
                    var left = EvaluateKleene(binary.Left, schema, row);
                    if (left == true) return true;
                    var right = EvaluateKleene(binary.Right, schema, row);
                    if (right == true) return true;
                    return left is null || right is null ? null : false;
                }
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(binary, schema, row);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                {
                    var operand = EvaluateKleene(unary.Operand, schema, row);
                    return operand is null ? null : !operand;
                }

            case IsNullExpression isNull:
                {
                    var isNullValue = EvaluateScalar(isNull.Operand, schema, row) is null;
                    return isNull.Negated ? !isNullValue : isNullValue;
                }

            case InExpression inExpression:
                return EvaluateIn(inExpression, schema, row);
        }

        var value = EvaluateScalar(expression, schema, row);
        if (value is null)
            return null;
        if (TryConvertToBoolean(value, out var boolean))
            return boolean;
        throw new InvalidOperationException("WHERE 表达式必须计算为布尔值。");
    }

    private static bool TryConvertToBoolean(object? value, out bool result)
    {
        switch (value)
        {
            case bool boolean:
                result = boolean;
                return true;
            case byte number:
                result = number != 0;
                return true;
            case short number:
                result = number != 0;
                return true;
            case int number:
                result = number != 0;
                return true;
            case long number:
                result = number != 0;
                return true;
            case float number:
                result = number != 0;
                return true;
            case double number:
                result = number != 0;
                return true;
            case decimal number:
                result = number != 0;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool? EvaluateComparison(BinaryExpression binary, TableSchema schema, IReadOnlyList<object?> row)
    {
        var left = EvaluateScalar(binary.Left, schema, row);
        var right = EvaluateScalar(binary.Right, schema, row);

        // 三值逻辑：任一操作数为 NULL，比较结果为 UNKNOWN。检测 NULL 只能用 IS [NOT] NULL。
        if (left is null || right is null)
            return null;

        int? compare = CompareScalar(left, right);

        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => ValuesEqual(left, right),
            SqlBinaryOperator.NotEqual => !ValuesEqual(left, right),
            SqlBinaryOperator.LessThan => compare is < 0,
            SqlBinaryOperator.LessThanOrEqual => compare is <= 0,
            SqlBinaryOperator.GreaterThan => compare is > 0,
            SqlBinaryOperator.GreaterThanOrEqual => compare is >= 0,
            SqlBinaryOperator.Like => LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotLike => !LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.Regex => RegexPatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotRegex => !RegexPatternMatcher.IsMatch(left, right),
            _ => throw new InvalidOperationException($"不支持的比较运算符 {binary.Operator}。"),
        };
    }

    private static bool? EvaluateIn(InExpression expression, TableSchema schema, IReadOnlyList<object?> row)
    {
        if (expression.Subquery is not null)
            throw new InvalidOperationException("单表执行路径不支持 IN 子查询。");

        var value = EvaluateScalar(expression.Value, schema, row);
        if (value is null)
            return null;

        var sawNull = false;
        foreach (var item in expression.Values)
        {
            var candidate = EvaluateScalar(item, schema, row);
            if (candidate is null)
            {
                sawNull = true;
                continue;
            }

            if (ValuesEqual(value, candidate))
                return expression.Negated ? false : true;
        }

        // 无匹配：若列表内出现 NULL，结果为 UNKNOWN；否则为确定的 not-in。
        if (sawNull)
            return null;
        return expression.Negated ? true : false;
    }

    private static object? EvaluateScalar(SqlExpression expression, TableSchema schema, IReadOnlyList<object?> row)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            IdentifierExpression identifier => GetColumnValue(schema, row, identifier.Name),
            FunctionCallExpression function => EvaluateFunction(function, schema, row),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(unary.Operand, schema, row), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(binary, schema, row),
            CaseExpression caseExpression => EvaluateCase(caseExpression, schema, row),
            _ => throw new InvalidOperationException(
                $"关系表表达式暂不支持 '{expression.GetType().Name}'。"),
        };
    }

    private static object? EvaluateCase(CaseExpression expression, TableSchema schema, IReadOnlyList<object?> row)
    {
        foreach (var when in expression.WhenClauses)
        {
            if (EvaluateBoolean(when.Condition, schema, row))
                return EvaluateScalar(when.Result, schema, row);
        }

        return expression.Else is null ? null : EvaluateScalar(expression.Else, schema, row);
    }

    private static object? EvaluateFunction(FunctionCallExpression function, TableSchema schema, IReadOnlyList<object?> row)
    {
        if (function.IsStar)
        {
            throw new InvalidOperationException($"关系表函数 {function.Name}(*) 非法。");
        }

        if (string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            var json = EvaluateScalar(function.Arguments[0], schema, row) as string;
            return JsonPathEvaluator.Evaluate(json, path!);
        }

        if (string.Equals(function.Name, "lower", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1)
        {
            return EvaluateScalar(function.Arguments[0], schema, row)?.ToString()?.ToLowerInvariant();
        }

        if (string.Equals(function.Name, "upper", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1)
        {
            return EvaluateScalar(function.Arguments[0], schema, row)?.ToString()?.ToUpperInvariant();
        }

        throw new InvalidOperationException("关系表当前仅支持 json_value(json_column, '$.path')、lower(value)、upper(value) 函数。");
    }

    private static object EvaluateArithmetic(BinaryExpression binary, TableSchema schema, IReadOnlyList<object?> row)
    {
        var leftValue = EvaluateScalar(binary.Left, schema, row);
        var rightValue = EvaluateScalar(binary.Right, schema, row);
        if (binary.Operator == SqlBinaryOperator.Add
            && (leftValue is string || rightValue is string))
        {
            return Convert.ToString(leftValue, CultureInfo.InvariantCulture)
                + Convert.ToString(rightValue, CultureInfo.InvariantCulture);
        }

        var left = RequireDouble(leftValue, binary.Operator.ToString());
        var right = RequireDouble(rightValue, binary.Operator.ToString());
        return binary.Operator switch
        {
            SqlBinaryOperator.Add => left + right,
            SqlBinaryOperator.Subtract => left - right,
            SqlBinaryOperator.Multiply => left * right,
            SqlBinaryOperator.Divide => left / right,
            SqlBinaryOperator.Modulo => left % right,
            _ => throw new InvalidOperationException($"不支持的算术运算符 {binary.Operator}。"),
        };
    }

    private static object? GetColumnValue(TableSchema schema, IReadOnlyList<object?> row, string name)
    {
        var column = schema.TryGetColumn(name)
            ?? throw new InvalidOperationException($"引用了未知列 '{name}'。");
        return row[column.Ordinal];
    }

    private static object? ConvertTableValue(SqlExpression expression, TableColumn column)
    {
        var value = expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            UnaryExpression { Operator: SqlUnaryOperator.Negate, Operand: LiteralExpression literal } => NegateLiteral(literal),
            DurationLiteralExpression duration => duration.Milliseconds,
            _ => throw new InvalidOperationException(
                $"列 '{column.Name}' 的值必须是字面量，不支持表达式 ({expression.GetType().Name})。"),
        };

        return ConvertTableValue(value, column);
    }

    private static object? ConvertTableValue(object? value, TableColumn column)
    {
        if (value is null)
            return null;

        return column.DataType switch
        {
            TableColumnType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            TableColumnType.Float64 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            TableColumnType.Boolean => value is bool b
                ? b
                : throw TypeMismatch(column, value),
            TableColumnType.String => value is string s
                ? s
                : throw TypeMismatch(column, value),
            TableColumnType.Json => value is string json
                ? json
                : throw TypeMismatch(column, value),
            TableColumnType.DateTime => ConvertDateTimeValue(value, column),
            TableColumnType.Blob => ConvertBlobValue(value, column),
            _ => throw new NotSupportedException($"不支持的关系表类型 {column.DataType}。"),
        };
    }

    private static object ConvertDateTimeValue(object value, TableColumn column)
    {
        return value switch
        {
            DateTime dt => dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt.ToUniversalTime(),
            DateTimeOffset dto => dto.UtcDateTime,
            long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
            int i32 => DateTimeOffset.FromUnixTimeMilliseconds(i32).UtcDateTime,
            string s when DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto) => dto.UtcDateTime,
            _ => throw TypeMismatch(column, value),
        };
    }

    private static object ConvertBlobValue(object value, TableColumn column)
    {
        if (value is byte[] bytes)
            return bytes;

        if (value is not string s)
            throw TypeMismatch(column, value);

        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(s);
        }
    }

    private static object? EvaluateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => null,
        SqlLiteralKind.Boolean => literal.BooleanValue,
        SqlLiteralKind.Integer => literal.IntegerValue,
        SqlLiteralKind.Float => literal.FloatValue,
        SqlLiteralKind.String => literal.StringValue,
        _ => throw new InvalidOperationException($"不支持的字面量类型 {literal.Kind}。"),
    };

    private static string FormatIndexColumns(TableIndex index)
        => string.IsNullOrWhiteSpace(index.JsonPath)
            ? string.Join(",", index.Columns)
            : $"{index.Columns[0]}->{index.JsonPath}";

    private static object NegateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Integer => checked(-literal.IntegerValue),
        SqlLiteralKind.Float => -literal.FloatValue,
        _ => throw new InvalidOperationException("一元负号只能用于数值字面量。"),
    };

    /// <summary>
    /// 融合 ORDER BY 与分页（#214）：有 ORDER BY + Fetch 上限时走有界 Top-N，避免全量排序百万行仅取 k 行。
    /// 无 ORDER BY 时仅分页；无分页时仅排序。
    /// </summary>
    private static SelectExecutionResult ApplyOrderByAndPagination(
        SelectExecutionResult result,
        IReadOnlyList<OrderBySpec> orderBy,
        PaginationSpec? pagination)
    {
        if (orderBy.Count == 0)
            return ApplyPagination(result, pagination);

        var sortItems = ResolveSortItems(result, orderBy);
        var comparer = new ResultRowSortComparer(sortItems);

        int offset = pagination?.Offset ?? 0;
        int? fetch = pagination?.Fetch;

        var rows = TopN.OrderByThenPaginate(result.Rows, comparer, offset, fetch);
        return new SelectExecutionResult(result.Columns, rows);
    }

    private static (int ColumnIndex, SortDirection Direction)[] ResolveSortItems(
        SelectExecutionResult result,
        IReadOnlyList<OrderBySpec> orderBy)
        => orderBy.Select(order =>
            {
                if (order.Expression is not IdentifierExpression { Name: var name })
                    throw new InvalidOperationException("关系表 ORDER BY 当前仅支持列名。");

                int columnIndex = -1;
                for (int i = 0; i < result.Columns.Count; i++)
                {
                    if (string.Equals(result.Columns[i], name, StringComparison.Ordinal))
                    {
                        columnIndex = i;
                        break;
                    }
                }

                if (columnIndex < 0)
                    throw new InvalidOperationException($"ORDER BY 引用了结果集中不存在的列 '{name}'。");

                return (ColumnIndex: columnIndex, order.Direction);
            })
            .ToArray();

    private sealed class ResultRowSortComparer(IReadOnlyList<(int ColumnIndex, SortDirection Direction)> sortItems)
        : IComparer<IReadOnlyList<object?>>
    {
        public int Compare(IReadOnlyList<object?>? x, IReadOnlyList<object?>? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            foreach (var item in sortItems)
            {
                var comparison = ScalarComparer.Instance.Compare(x[item.ColumnIndex], y[item.ColumnIndex]);
                if (comparison != 0)
                    return item.Direction == SortDirection.Descending ? -comparison : comparison;
            }

            return 0;
        }
    }

    private static SelectExecutionResult ApplyPagination(SelectExecutionResult result, PaginationSpec? pagination)
    {
        if (pagination is null)
            return result;

        int offset = pagination.Offset;
        if (offset >= result.Rows.Count)
            return new SelectExecutionResult(result.Columns, []);

        int take = pagination.Fetch ?? (result.Rows.Count - offset);
        if (take <= 0)
            return new SelectExecutionResult(result.Columns, []);

        return new SelectExecutionResult(
            result.Columns,
            result.Rows.Skip(offset).Take(Math.Min(take, result.Rows.Count - offset)).ToArray());
    }

    private static IEnumerable<SqlExpression> FlattenAnd(SqlExpression expression)
    {
        if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } binary)
        {
            foreach (var left in FlattenAnd(binary.Left))
                yield return left;
            foreach (var right in FlattenAnd(binary.Right))
                yield return right;
            yield break;
        }

        yield return expression;
    }

    private static void ValidateTableAliasReferences(SelectStatement statement)
    {
        foreach (var identifier in EnumerateIdentifierReferences(statement))
        {
            if (identifier.Qualifier is null)
                continue;

            if (statement.TableAlias is null)
            {
                throw new InvalidOperationException(
                    $"限定列名 '{identifier.Qualifier}.{identifier.Name}' 要求 FROM 子句声明单表别名。");
            }

            if (!string.Equals(identifier.Qualifier, statement.TableAlias, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"限定列名 '{identifier.Qualifier}.{identifier.Name}' 引用了未知别名 '{identifier.Qualifier}'；当前查询只声明了别名 '{statement.TableAlias}'。");
            }
        }
    }

    private static IEnumerable<IdentifierExpression> EnumerateIdentifierReferences(SelectStatement statement)
    {
        foreach (var projection in statement.Projections)
        {
            foreach (var identifier in EnumerateIdentifierReferences(projection.Expression))
                yield return identifier;
        }

        if (statement.Where is not null)
        {
            foreach (var identifier in EnumerateIdentifierReferences(statement.Where))
                yield return identifier;
        }

        if (statement.OrderBy is not null)
        {
            foreach (var identifier in EnumerateIdentifierReferences(statement.OrderBy.Expression))
                yield return identifier;
        }
    }

    private static IEnumerable<IdentifierExpression> EnumerateIdentifierReferences(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                yield return identifier;
                yield break;

            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                {
                    foreach (var identifier in EnumerateIdentifierReferences(argument))
                        yield return identifier;
                }
                yield break;

            case UnaryExpression unary:
                foreach (var identifier in EnumerateIdentifierReferences(unary.Operand))
                    yield return identifier;
                yield break;

            case BinaryExpression binary:
                foreach (var identifier in EnumerateIdentifierReferences(binary.Left))
                    yield return identifier;
                foreach (var identifier in EnumerateIdentifierReferences(binary.Right))
                    yield return identifier;
                yield break;

            case InExpression inExpression:
                foreach (var identifier in EnumerateIdentifierReferences(inExpression.Value))
                    yield return identifier;
                foreach (var item in inExpression.Values)
                {
                    foreach (var identifier in EnumerateIdentifierReferences(item))
                        yield return identifier;
                }
                if (inExpression.Subquery is not null)
                {
                    foreach (var identifier in EnumerateIdentifierReferences(inExpression.Subquery))
                        yield return identifier;
                }
                yield break;

            case CaseExpression caseExpression:
                foreach (var when in caseExpression.WhenClauses)
                {
                    foreach (var identifier in EnumerateIdentifierReferences(when.Condition))
                        yield return identifier;
                    foreach (var identifier in EnumerateIdentifierReferences(when.Result))
                        yield return identifier;
                }
                if (caseExpression.Else is not null)
                {
                    foreach (var identifier in EnumerateIdentifierReferences(caseExpression.Else))
                        yield return identifier;
                }
                yield break;
        }
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.AsSpan().SequenceEqual(rightBytes);

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        return Equals(left, right);
    }

    private static int? CompareScalar(object? left, object? right)
    {
        if (left is null || right is null)
            return null;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        if (left is DateTime leftDate && right is DateTime rightDate)
            return leftDate.CompareTo(rightDate);

        if (left is string leftString && right is string rightString)
            return string.Compare(leftString, rightString, StringComparison.Ordinal);

        if (left is bool leftBool && right is bool rightBool)
            return leftBool.CompareTo(rightBool);

        throw new InvalidOperationException($"无法比较 {left.GetType().Name} 与 {right.GetType().Name}。");
    }

    private static double RequireDouble(object? value, string operatorName)
    {
        if (value is null)
            throw new InvalidOperationException($"运算 {operatorName} 不接受 NULL 参数。");
        if (!IsNumeric(value))
            throw new InvalidOperationException($"运算 {operatorName} 需要数值参数。");
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static bool IsNumeric(object value) => value is
        byte or sbyte or
        short or ushort or
        int or uint or
        long or ulong or
        float or double or decimal;

    private static bool IsComparisonOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Equal or
        SqlBinaryOperator.NotEqual or
        SqlBinaryOperator.LessThan or
        SqlBinaryOperator.LessThanOrEqual or
        SqlBinaryOperator.GreaterThan or
        SqlBinaryOperator.GreaterThanOrEqual or
        SqlBinaryOperator.Like or
        SqlBinaryOperator.NotLike or
        SqlBinaryOperator.Regex or
        SqlBinaryOperator.NotRegex;

    private static bool IsArithmeticOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Add or
        SqlBinaryOperator.Subtract or
        SqlBinaryOperator.Multiply or
        SqlBinaryOperator.Divide or
        SqlBinaryOperator.Modulo;

    private static TableColumnType MapTableColumnType(SqlDataType type) => type switch
    {
        SqlDataType.Int64 => TableColumnType.Int64,
        SqlDataType.Float64 => TableColumnType.Float64,
        SqlDataType.Boolean => TableColumnType.Boolean,
        SqlDataType.String => TableColumnType.String,
        SqlDataType.DateTime => TableColumnType.DateTime,
        SqlDataType.Blob => TableColumnType.Blob,
        SqlDataType.Json => TableColumnType.Json,
        _ => throw new NotSupportedException($"关系表 MVP 暂不支持数据类型 {type}。"),
    };

    private static string FormatTableColumnType(TableColumnType type) => type switch
    {
        TableColumnType.Int64 => "int64",
        TableColumnType.Float64 => "float64",
        TableColumnType.Boolean => "boolean",
        TableColumnType.String => "string",
        TableColumnType.DateTime => "datetime",
        TableColumnType.Blob => "blob",
        TableColumnType.Json => "json",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static string FormatLiteralColumnName(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => "NULL",
        SqlLiteralKind.Boolean => literal.BooleanValue ? "TRUE" : "FALSE",
        SqlLiteralKind.Integer => literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.Float => literal.FloatValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.String => literal.StringValue ?? string.Empty,
        _ => literal.Kind.ToString(),
    };

    private static string FormatFunctionColumnName(FunctionCallExpression function)
        => function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path }
            ? path!
            : function.Name;

    private static InvalidOperationException TypeMismatch(TableColumn column, object value)
        => new($"列 '{column.Name}' 期望 {column.DataType}，实际值类型为 {value.GetType().Name}。");

    private sealed record BoundAssignment(TableColumn Column, SqlExpression Value);

    private enum ProjectionKind
    {
        Column,
        Constant,
        Expression,
    }

    private sealed record Projection(
        ProjectionKind Kind,
        string ColumnName,
        TableColumn? Column,
        object? ConstantValue,
        SqlExpression? ExpressionValue = null)
    {
        public static Projection ForColumn(TableColumn column, string columnName)
            => new(ProjectionKind.Column, columnName, column, null);

        public static Projection Constant(object? value, string columnName)
            => new(ProjectionKind.Constant, columnName, null, value);

        public static Projection Expression(string columnName, SqlExpression expression)
            => new(ProjectionKind.Expression, columnName, null, null, expression);
    }

    private sealed class ScalarComparer : IComparer<object?>
    {
        public static ScalarComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null)
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            return CompareScalar(x, y) ?? 0;
        }
    }
}
