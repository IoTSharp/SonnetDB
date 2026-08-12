using System.Globalization;
using System.Numerics;
using System.Text;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Modbus;
using SonnetDB.Query.Functions;
using SonnetDB.Routines;
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
        new List<string>(7)
        {
            "column_name", "data_type", "is_nullable", "is_primary_key", "ordinal", "column_default", "is_auto_increment"
        }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showIndexColumns =
        new List<string>(4) { "index_name", "is_unique", "columns", "created_utc" }.AsReadOnly();

    /// <summary>
    /// 创建关系表；带 Modbus 映射时在数据库级 schema 锁内串行提交表与绑定。
    /// </summary>
    /// <param name="tsdb">目标数据库。</param>
    /// <param name="statement">CREATE TABLE 语句。</param>
    /// <returns>创建或已存在的关系表 schema。</returns>
    public static TableSchema ExecuteCreateTable(Tsdb tsdb, CreateTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        return tsdb.ExecuteSchemaMutation(() => ExecuteCreateTableLocked(tsdb, statement));
    }

    /// <summary>在数据库级 schema 锁内执行关系表创建和可选 Modbus 绑定提交。</summary>
    private static TableSchema ExecuteCreateTableLocked(Tsdb tsdb, CreateTableStatement statement)
    {

        SqlExecutor.EnsureNameDoesNotBelongToView(tsdb, statement.Name, "table");

        if (statement.IfNotExists)
        {
            var existing = tsdb.Tables.Catalog.TryGet(statement.Name);
            if (existing is not null)
            {
                if (statement.ModbusBinding is not null
                    && tsdb.Modbus.Catalog.TryGetBinding(statement.Name) is null)
                {
                    throw new InvalidDataException(
                        $"table '{statement.Name}' 已存在但缺少 MODBUS 绑定；"
                        + "不能把 CREATE TABLE IF NOT EXISTS 视为成功，请先修复或删除该不完整表。");
                }

                return existing;
            }
        }

        var columns = new List<(string Name, TableColumnType DataType, bool IsNullable)>(statement.Columns.Count);
        var columnDefaults = new Dictionary<string, string?>(StringComparer.Ordinal);
        var autoIncrementColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in statement.Columns)
        {
            var dataType = MapTableColumnType(column.DataType);
            var isPrimaryKey = statement.PrimaryKey.Contains(column.Name, StringComparer.Ordinal);
            var isAutoIncrement = column.IsAutoIncrement;
            var isNullable = column.Nullability != ColumnNullability.NotNull && !isPrimaryKey && !isAutoIncrement;
            if (isAutoIncrement)
                autoIncrementColumns.Add(column.Name);
            if (column.DefaultExpression is not null)
            {
                if (column.IsRowVersion)
                    throw new InvalidOperationException("ROWVERSION 列不允许声明 DEFAULT。");
                if (column.IsAutoIncrement)
                    throw new InvalidOperationException("AUTO_INCREMENT 列不允许声明 DEFAULT。");
                var tempColumn = new TableColumn(
                    column.Name,
                    dataType,
                    IsPrimaryKey: false,
                    isNullable,
                    Ordinal: 0);
                var defaultSql = ValidateAndFormatDefault(column.DefaultExpression, tempColumn);
                columnDefaults.Add(column.Name, defaultSql);
            }

            columns.Add((
                column.Name,
                dataType,
                isNullable));
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
        var checkConstraints = statement.CheckConstraintClauses
            .Select(static constraint => new TableCheckConstraintDefinition(
                constraint.Name ?? string.Empty,
                constraint.ExpressionSql))
            .ToArray();
        var schema = TableSchema.CreateWithDefaults(
            statement.Name,
            columns,
            statement.PrimaryKey,
            indexes: null,
            foreignKeys: foreignKeys,
            rowVersionColumns: rowVersionColumns,
            createdAtUtcTicks: 0,
            checkConstraints: checkConstraints,
            columnDefaults: columnDefaults,
            autoIncrementColumns: autoIncrementColumns);
        ModbusTableBinding? modbusBinding = ModbusSqlExecutor.ResolveTableBinding(tsdb, statement, schema);
        tsdb.Tables.Create(schema);
        if (modbusBinding is null)
            return schema;

        try
        {
            // 两份独立 catalog 按“先表、后绑定”发布；绑定落盘失败时撤销刚创建的表。
            tsdb.Modbus.CreateBinding(modbusBinding);
        }
        catch (Exception bindingException)
        {
            try
            {
                _ = tsdb.Tables.Drop(schema.Name);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    $"table '{schema.Name}' 的 MODBUS 绑定创建失败，且关系表回滚失败。",
                    new AggregateException(bindingException, rollbackException));
            }

            throw;
        }

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

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.Name, "DROP TABLE");

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

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.TableName, "ALTER TABLE");

        var dataType = MapTableColumnType(statement.DataType);
        var isNullable = statement.Nullability != ColumnNullability.NotNull;
        object? defaultValue = null;
        string? defaultExpressionSql = null;
        if (statement.DefaultExpression is not null)
        {
            var tempColumn = new TableColumn(statement.ColumnName, dataType, IsPrimaryKey: false, isNullable, Ordinal: 0);
            defaultExpressionSql = ValidateAndFormatDefault(statement.DefaultExpression, tempColumn);
            defaultValue = EvaluateAndConvertDefault(statement.DefaultExpression, tempColumn);
        }
        else if (!isNullable)
        {
            throw new InvalidOperationException("ALTER TABLE ADD COLUMN 添加 NOT NULL 列时必须提供 DEFAULT。");
        }

        tsdb.Tables.AlterTableAddColumn(
            statement.TableName,
            statement.ColumnName,
            dataType,
            isNullable,
            defaultValue,
            defaultExpressionSql);
        return new RowsAffectedExecutionResult(statement.TableName, 1, "alter_table_add_column");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableAlterColumn(
        Tsdb tsdb,
        AlterTableAlterColumnStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.TableName, "ALTER TABLE");

        var schema = tsdb.Tables.Catalog.TryGet(statement.TableName)
            ?? throw new InvalidOperationException($"table '{statement.TableName}' 不存在。");
        var currentColumn = schema.TryGetColumn(statement.ColumnName)
            ?? throw new InvalidOperationException(
                $"table '{statement.TableName}' 中不存在列 '{statement.ColumnName}'。");
        if (currentColumn.IsRowVersion)
        {
            throw new InvalidOperationException(
                "ALTER TABLE ALTER COLUMN 当前不支持修改 ROWVERSION 列定义。");
        }

        var targetType = statement.DataType is null
            ? currentColumn.DataType
            : MapTableColumnType(statement.DataType.Value);
        var targetNullable = statement.Nullability switch
        {
            ColumnNullability.Unspecified => currentColumn.IsNullable,
            ColumnNullability.Nullable => true,
            ColumnNullability.NotNull => false,
            _ => throw new ArgumentOutOfRangeException(nameof(statement)),
        };
        var targetColumn = currentColumn with
        {
            DataType = targetType,
            IsNullable = targetNullable,
        };

        string? targetDefaultSql = statement.DefaultAction switch
        {
            ColumnDefaultAction.Unchanged => currentColumn.DefaultExpressionSql,
            ColumnDefaultAction.Drop => null,
            ColumnDefaultAction.Set when statement.DefaultExpression is not null =>
                ValidateAndFormatDefault(statement.DefaultExpression, targetColumn),
            ColumnDefaultAction.Set => throw new InvalidOperationException("ALTER COLUMN SET DEFAULT 缺少默认表达式。"),
            _ => throw new ArgumentOutOfRangeException(nameof(statement)),
        };

        if (statement.DefaultAction == ColumnDefaultAction.Unchanged
            && targetDefaultSql is not null)
        {
            _ = EvaluateAndConvertSchemaDefault(SqlParser.ParsePredicate(targetDefaultSql), targetColumn);
        }

        if (!targetNullable && targetDefaultSql is not null)
        {
            var defaultValue = EvaluateAndConvertSchemaDefault(
                SqlParser.ParsePredicate(targetDefaultSql),
                targetColumn);
            if (defaultValue is null)
            {
                throw new InvalidOperationException(
                    $"NOT NULL 列 '{statement.ColumnName}' 的 DEFAULT 不能是 NULL。");
            }
        }

        tsdb.Tables.AlterTableAlterColumn(
            statement.TableName,
            statement.ColumnName,
            targetType,
            targetNullable,
            targetDefaultSql,
            value => ConvertTableValueForSchemaChange(value, targetColumn));
        return new RowsAffectedExecutionResult(statement.TableName, 1, "alter_table_alter_column");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableAddForeignKey(Tsdb tsdb, AlterTableAddForeignKeyStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.TableName, "ALTER TABLE");

        tsdb.Tables.AddForeignKey(
            statement.TableName,
            new TableForeignKeyDefinition(
                statement.ConstraintName ?? string.Empty,
                statement.Columns,
                statement.PrincipalTable,
                statement.PrincipalColumns,
                statement.OnDelete));
        return new RowsAffectedExecutionResult(statement.TableName, 1, "alter_table_add_foreign_key");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableAddCheckConstraint(
        Tsdb tsdb,
        AlterTableAddCheckConstraintStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.TableName, "ALTER TABLE");

        tsdb.Tables.AddCheckConstraint(
            statement.TableName,
            new TableCheckConstraintDefinition(
                statement.ConstraintName ?? string.Empty,
                statement.ExpressionSql));
        return new RowsAffectedExecutionResult(statement.TableName, 1, "alter_table_add_check_constraint");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableDropColumn(Tsdb tsdb, AlterTableDropColumnStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.TableName, "ALTER TABLE");

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

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.TableName, "ALTER TABLE");

        bool removed = tsdb.Tables.DropForeignKey(statement.TableName, statement.ConstraintName)
            || tsdb.Tables.DropCheckConstraint(statement.TableName, statement.ConstraintName);
        return new RowsAffectedExecutionResult(statement.TableName, removed ? 1 : 0, "alter_table_drop_constraint");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableRenameColumn(Tsdb tsdb, AlterTableRenameColumnStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.TableName, "ALTER TABLE");

        tsdb.Tables.AlterTableRenameColumn(statement.TableName, statement.OldColumnName, statement.NewColumnName);
        return new RowsAffectedExecutionResult(statement.TableName, 1, "alter_table_rename_column");
    }

    public static RowsAffectedExecutionResult ExecuteAlterTableRenameTable(Tsdb tsdb, AlterTableRenameTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        SqlExecutor.EnsureNoViewDependents(tsdb, statement.OldTableName, "ALTER TABLE RENAME");

        tsdb.Tables.RenameTable(statement.OldTableName, statement.NewTableName);
        return new RowsAffectedExecutionResult(statement.NewTableName, 1, "alter_table_rename_table");
    }

    public static InsertExecutionResult ExecuteInsert(Tsdb tsdb, InsertStatement statement, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var bindings = BindInsertColumns(statement, schema);
        var defaults = BindInsertDefaults(schema, bindings);
        var returningColumns = BindReturningColumns(statement, schema);

        var valuesRows = new List<object?[]>(statement.Rows.Count);
        foreach (var row in statement.Rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            var values = new object?[schema.Columns.Count];
            for (int i = 0; i < bindings.Length; i++)
            {
                var column = bindings[i];
                values[column.Ordinal] = ConvertInsertValue(row[i], column);
            }

            ApplyInsertDefaults(defaults, values);
            ApplyInsertRowVersion(schema, values);
            ValidateRequiredColumns(schema, values);
            valuesRows.Add(values);
        }

        var mutations = valuesRows
            .Select(static values => new TableRowMutation(PrimaryKeyValues: null, values))
            .ToList();

        var mutationsByTable = new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
        {
            [schema.Name] = mutations,
        };
        if (returningColumns.Length == 0)
        {
            int inserted = tsdb.Tables.ApplyTransaction(mutationsByTable);
            return new InsertExecutionResult(schema.Name, inserted);
        }

        int insertedWithRows = tsdb.Tables.ApplyTransaction(mutationsByTable, out var finalRowsByTable);
        if (!finalRowsByTable.TryGetValue(schema.Name, out var finalRows))
            throw new InvalidOperationException($"table '{schema.Name}' 的 INSERT 提交结果缺少最终行。");

        var insertedRows = finalRows
            .Select(static row => row.Values.ToArray())
            .ToArray();
        return CreateInsertResult(schema.Name, insertedWithRows, returningColumns, insertedRows);
    }

    public static InsertExecutionResult QueueInsert(SqlTransactionContext transaction, InsertStatement statement, TableSchema schema)
        => QueueInsertCore(
            tsdb: null,
            transaction,
            statement,
            schema,
            out _);

    internal static InsertExecutionResult QueueInsert(
        Tsdb tsdb,
        SqlTransactionContext transaction,
        InsertStatement statement,
        TableSchema schema,
        out IReadOnlyList<TableRowChange> changes)
        => QueueInsertCore(tsdb, transaction, statement, schema, out changes);

    private static InsertExecutionResult QueueInsertCore(
        Tsdb? tsdb,
        SqlTransactionContext transaction,
        InsertStatement statement,
        TableSchema schema,
        out IReadOnlyList<TableRowChange> changes)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var bindings = BindInsertColumns(statement, schema);
        var defaults = BindInsertDefaults(schema, bindings);
        var returningColumns = BindReturningColumns(statement, schema);
        var valuesRows = new List<object?[]>(statement.Rows.Count);
        foreach (var row in statement.Rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            var values = new object?[schema.Columns.Count];
            for (int i = 0; i < bindings.Length; i++)
            {
                var column = bindings[i];
                values[column.Ordinal] = ConvertInsertValue(row[i], column);
            }

            ApplyInsertDefaults(defaults, values);
            ApplyInsertRowVersion(schema, values);
            ValidateRequiredColumns(schema, values);
            valuesRows.Add(values);
        }

        if (tsdb is not null && schema.AutoIncrementColumn is { } autoIncrementColumn)
        {
            bool reservedGeneratedValue = valuesRows.Any(
                values => values[autoIncrementColumn.Ordinal] is null);
            long generation = tsdb.Tables.Open(schema.Name).ApplyAutoIncrement(valuesRows);
            if (reservedGeneratedValue)
                transaction.RecordAutoIncrementReservation(schema.Name, generation);
        }

        var mutations = new List<TableRowMutation>(valuesRows.Count);
        var rowChanges = new List<TableRowChange>(valuesRows.Count);
        foreach (var values in valuesRows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            mutations.Add(new TableRowMutation(PrimaryKeyValues: null, values));
            rowChanges.Add(new TableRowChange(schema, OldValues: null, values.ToArray()));
        }

        // 整条 INSERT 的所有行都转换、校验成功后再写缓冲，避免后续行失败留下部分插入。
        foreach (var mutation in mutations)
            transaction.AddOrMergeTableMutation(schema, mutation);

        changes = rowChanges;
        return CreateInsertResult(schema.Name, mutations.Count, returningColumns, valuesRows);
    }

    private static TableColumn[] BindReturningColumns(InsertStatement statement, TableSchema schema)
    {
        if (statement.ReturningColumns.Count == 0)
            return [];

        if (statement.ReturningColumns.Count == 1 && statement.ReturningColumns[0] == "*")
            return [.. schema.Columns];

        var columns = new TableColumn[statement.ReturningColumns.Count];
        for (int i = 0; i < columns.Length; i++)
        {
            string name = statement.ReturningColumns[i];
            columns[i] = schema.TryGetColumn(name)
                ?? throw new InvalidOperationException(
                    $"table '{schema.Name}' 的 RETURNING 中不存在列 '{name}'。");
        }

        return columns;
    }

    private static InsertExecutionResult CreateInsertResult(
        string tableName,
        int rowsInserted,
        IReadOnlyList<TableColumn> returningColumns,
        IReadOnlyList<object?[]> insertedRows)
    {
        var result = new InsertExecutionResult(tableName, rowsInserted);
        if (returningColumns.Count == 0)
            return result;

        var columns = returningColumns.Select(static column => column.Name).ToArray();
        var rows = new IReadOnlyList<object?>[insertedRows.Count];
        for (int r = 0; r < insertedRows.Count; r++)
        {
            var row = new object?[returningColumns.Count];
            for (int c = 0; c < returningColumns.Count; c++)
                row[c] = insertedRows[r][returningColumns[c].Ordinal];
            rows[r] = row;
        }

        return result with { Returning = new SelectExecutionResult(columns, rows) };
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
        var hiddenOrderColumns = ResolveHiddenOrderColumns(projections, statement.OrderByList, schema);
        var (rows, rangeOrderSatisfied) = LoadSelectCandidateRowsForStatement(
            tsdb.Tables.Open(schema.Name),
            schema,
            statement,
            projections);
        var filtered = new List<IReadOnlyList<object?>>();
        foreach (var row in rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (!EvaluateWhere(statement.Where, schema, row.Values))
                continue;

            var output = new object?[projections.Length + hiddenOrderColumns.Length];
            for (int i = 0; i < projections.Length; i++)
                output[i] = EvaluateProjection(projections[i], schema, row.Values);
            for (int i = 0; i < hiddenOrderColumns.Length; i++)
                output[projections.Length + i] = row.Values[hiddenOrderColumns[i].Ordinal];
            filtered.Add(output);
        }

        var result = new SelectExecutionResult(
            projections.Select(static projection => projection.ColumnName)
                .Concat(hiddenOrderColumns.Select(static column => column.Name))
                .ToArray(),
            filtered);
        var ordered = rangeOrderSatisfied
            ? ApplyPagination(result, statement.Pagination)
            : ApplyOrderByAndPagination(result, statement.OrderByList, statement.Pagination);
        return hiddenOrderColumns.Length == 0
            ? ordered
            : RemoveHiddenOrderColumns(ordered, projections.Length);
    }

    internal static IReadOnlyList<string> ResolveProjectionColumnNames(
        SelectStatement statement,
        TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);
        return BuildProjections(statement.Projections, schema)
            .Select(static projection => projection.ColumnName)
            .ToArray();
    }

    public static RowsAffectedExecutionResult ExecuteDelete(Tsdb tsdb, DeleteStatement statement, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var where = TableInSubqueryExecutor.Materialize(tsdb, statement.Where, schema);
        if (schema.AutoIncrementColumn is null
            && where is LiteralExpression { Kind: SqlLiteralKind.Boolean, BooleanValue: true }
            && tsdb.Tables.TryTruncateFast(schema.Name, out int truncated))
        {
            return new RowsAffectedExecutionResult(schema.Name, truncated, "delete_generation");
        }

        int deleted = 0;
        if (TryExtractPrimaryKeyValues(schema, where, allowExtraPredicates: false, out var keyValues))
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
        var candidateRows = LoadMutationCandidateRows(store, schema, where, out bool predicateSatisfied);
        foreach (var row in candidateRows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (!predicateSatisfied && !EvaluateWhere(where, schema, row.Values))
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
        // IN 子查询可能间接执行用户代码，必须在表管理锁之外完成物化；赋值表达式仍在锁内按最新行求值。
        var where = TableInSubqueryExecutor.Materialize(tsdb, statement.Where, schema);

        // UDF 是任意用户回调，可能等待另一个 SQL 线程，不能在全局表管理锁内执行。
        if (ContainsUserScalarFunction(tsdb.Functions, where)
            || assignments.Any(assignment => ContainsUserScalarFunction(tsdb.Functions, assignment.Value)))
        {
            return ExecuteBoundUpdate(tsdb, schema, store, assignments, where);
        }

        return tsdb.Tables.ExecuteLocked(() =>
            ExecuteBoundUpdate(tsdb, schema, store, assignments, where));
    }

    /// <summary>
    /// 对已绑定的关系表 UPDATE 扫描候选行、按原行快照计算全部右值并统一提交。
    /// </summary>
    private static RowsAffectedExecutionResult ExecuteBoundUpdate(
        Tsdb tsdb,
        TableSchema schema,
        TableStore store,
        IReadOnlyList<BoundAssignment> assignments,
        SqlExpression where)
    {
        var mutations = new List<TableRowMutation>();
        var candidateRows = LoadMutationCandidateRows(store, schema, where, out bool predicateSatisfied);
        foreach (var row in candidateRows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (!predicateSatisfied && !EvaluateWhere(where, schema, row.Values))
                continue;

            var values = row.Values.ToArray();
            foreach (var assignment in assignments)
            {
                // 同一 SET 子句中的所有右值都读取更新前的原行，保证 SET a=b, b=a 可正确交换。
                values[assignment.Column.Ordinal] = EvaluateAssignment(assignment, schema, row.Values);
            }

            ValidateRequiredColumns(schema, values);
            var expectedRowVersion = ExtractRowVersion(schema, row.Values);
            ApplyUpdateRowVersion(schema, values, expectedRowVersion);
            mutations.Add(new TableRowMutation(
                ExtractPrimaryKeyValues(schema, row.Values), values, expectedRowVersion));
        }

        ThrowIfStaleRowVersionPredicate(schema, store, where, mutations.Count);

        int updated = tsdb.Tables.ApplyTransaction(
            new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
            {
                [schema.Name] = mutations,
            });
        return new RowsAffectedExecutionResult(schema.Name, updated, "update");
    }

    /// <summary>
    /// 递归判断表达式是否调用当前数据库注册的用户标量函数，供 UPDATE 选择锁外回调路径。
    /// </summary>
    private static bool ContainsUserScalarFunction(
        UserFunctionRegistry functions,
        SqlExpression expression)
    {
        switch (expression)
        {
            case FunctionCallExpression function:
                if (functions.TryGetScalar(function.Name, out _))
                    return true;
                return function.Arguments.Any(argument => ContainsUserScalarFunction(functions, argument));
            case UnaryExpression unary:
                return ContainsUserScalarFunction(functions, unary.Operand);
            case BinaryExpression binary:
                return ContainsUserScalarFunction(functions, binary.Left)
                    || ContainsUserScalarFunction(functions, binary.Right);
            case CaseExpression caseExpression:
                return caseExpression.WhenClauses.Any(clause =>
                        ContainsUserScalarFunction(functions, clause.Condition)
                        || ContainsUserScalarFunction(functions, clause.Result))
                    || (caseExpression.Else is not null
                        && ContainsUserScalarFunction(functions, caseExpression.Else));
            case IsNullExpression isNull:
                return ContainsUserScalarFunction(functions, isNull.Operand);
            case InExpression inExpression:
                return ContainsUserScalarFunction(functions, inExpression.Value)
                    || inExpression.Values.Any(value => ContainsUserScalarFunction(functions, value));
            default:
                return false;
        }
    }

    public static RowsAffectedExecutionResult QueueUpdate(SqlTransactionContext transaction, Tsdb tsdb, UpdateStatement statement)
        => QueueUpdate(transaction, tsdb, statement, out _);

    internal static RowsAffectedExecutionResult QueueUpdate(
        SqlTransactionContext transaction,
        Tsdb tsdb,
        UpdateStatement statement,
        out IReadOnlyList<TableRowChange> changes)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var schema = tsdb.Tables.Catalog.TryGet(statement.TableName)
            ?? throw new InvalidOperationException($"table '{statement.TableName}' 不存在。");
        ThrowIfBufferedTargetMakesSubqueryViewInconsistent(transaction, schema, statement.Where);
        var store = tsdb.Tables.Open(schema.Name);
        var assignments = BindAssignments(statement, schema);
        var where = TableInSubqueryExecutor.Materialize(tsdb, statement.Where, schema);

        var mutations = new List<TableRowMutation>();
        var rowChanges = new List<TableRowChange>();
        IReadOnlyList<TableRow> candidateRows;
        bool predicateSatisfied;
        if (transaction.TryGetBufferedMutations(schema.Name, out var buffered))
        {
            candidateRows = ApplyMutationOverlay(schema, store.Scan(), buffered);
            predicateSatisfied = false;
        }
        else
        {
            candidateRows = LoadMutationCandidateRows(store, schema, where, out predicateSatisfied);
        }
        foreach (var row in candidateRows)
        {
            if (!predicateSatisfied && !EvaluateWhere(where, schema, row.Values))
                continue;

            var values = row.Values.ToArray();
            foreach (var assignment in assignments)
            {
                // 事务缓冲路径同样使用原行快照，避免赋值顺序改变 SQL 语义。
                values[assignment.Column.Ordinal] = EvaluateAssignment(assignment, schema, row.Values);
            }

            ValidateRequiredColumns(schema, values);
            var expectedRowVersion = ExtractRowVersion(schema, row.Values);
            ApplyUpdateRowVersion(schema, values, expectedRowVersion);
            mutations.Add(new TableRowMutation(
                ExtractPrimaryKeyValues(schema, row.Values), values, expectedRowVersion));
            rowChanges.Add(new TableRowChange(schema, row.Values.ToArray(), values.ToArray()));
        }

        ThrowIfStaleRowVersionPredicate(schema, store, where, mutations.Count);

        // 整条语句全部求值成功后才写入事务缓冲，避免后续行失败时残留部分 UPDATE。
        foreach (var mutation in mutations)
            transaction.AddOrMergeTableMutation(schema, mutation);

        changes = rowChanges;
        return new RowsAffectedExecutionResult(schema.Name, mutations.Count, "update");
    }

    public static RowsAffectedExecutionResult QueueDelete(SqlTransactionContext transaction, Tsdb tsdb, DeleteStatement statement, TableSchema schema)
        => QueueDelete(transaction, tsdb, statement, schema, out _);

    internal static RowsAffectedExecutionResult QueueDelete(
        SqlTransactionContext transaction,
        Tsdb tsdb,
        DeleteStatement statement,
        TableSchema schema,
        out IReadOnlyList<TableRowChange> changes)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        ThrowIfBufferedTargetMakesSubqueryViewInconsistent(transaction, schema, statement.Where);
        var where = TableInSubqueryExecutor.Materialize(tsdb, statement.Where, schema);
        var store = tsdb.Tables.Open(schema.Name);
        var mutations = new List<TableRowMutation>();
        var rowChanges = new List<TableRowChange>();
        IReadOnlyList<TableRow> candidateRows;
        bool predicateSatisfied;
        if (transaction.TryGetBufferedMutations(schema.Name, out var buffered))
        {
            // DELETE 必须读取本事务前序写入；叠加后索引谓词已不能直接视为满足。
            candidateRows = ApplyMutationOverlay(schema, store.Scan(), buffered);
            predicateSatisfied = false;
        }
        else
        {
            candidateRows = LoadMutationCandidateRows(store, schema, where, out predicateSatisfied);
        }

        foreach (var row in candidateRows)
        {
            if (!predicateSatisfied && !EvaluateWhere(where, schema, row.Values))
                continue;

            mutations.Add(new TableRowMutation(
                ExtractPrimaryKeyValues(schema, row.Values), NewValues: null, ExtractRowVersion(schema, row.Values)));
            rowChanges.Add(new TableRowChange(schema, row.Values.ToArray(), NewValues: null));
        }

        // WHERE 对全部候选行求值成功后再合并，保证一条 DELETE 在事务缓冲中也是语句原子的。
        foreach (var mutation in mutations)
            transaction.AddOrMergeTableMutation(schema, mutation);

        changes = rowChanges;
        return new RowsAffectedExecutionResult(schema.Name, mutations.Count, "delete");
    }

    private static void ThrowIfBufferedTargetMakesSubqueryViewInconsistent(
        SqlTransactionContext transaction,
        TableSchema schema,
        SqlExpression where)
    {
        if (TableInSubqueryExecutor.ContainsInSubquery(where)
            && transaction.TryGetBufferedMutations(schema.Name, out _))
        {
            throw new NotSupportedException(
                $"轻事务中 table '{schema.Name}' 已有缓冲写时，不支持对该表执行带 IN 子查询的 UPDATE/DELETE。");
        }
    }

    public static RowsAffectedExecutionResult CommitTransaction(Tsdb tsdb, SqlTransactionContext transaction)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(transaction);

        int affected;
        try
        {
            var mutations = transaction.SnapshotTableMutations();
            var reservationGenerations = transaction.SnapshotAutoIncrementReservationGenerations();
            affected = reservationGenerations.Count == 0
                ? tsdb.Tables.ApplyTransaction(mutations)
                : tsdb.Tables.ExecuteLocked(() =>
                {
                    foreach (var (tableName, expectedGeneration) in reservationGenerations)
                    {
                        long actualGeneration = tsdb.Tables.Open(tableName).Generation;
                        if (actualGeneration != expectedGeneration)
                        {
                            throw new InvalidOperationException(
                                $"table '{tableName}' 在 AUTO_INCREMENT 值预留后执行了 TRUNCATE；"
                                + $"预留 generation={expectedGeneration}，当前 generation={actualGeneration}，事务已拒绝提交。");
                        }
                    }

                    return tsdb.Tables.ApplyTransaction(mutations);
                });
        }
        catch
        {
            tsdb.Routines.Diagnostics.MarkTriggerTransactionFailure(
                transaction.SnapshotTriggerAuditSequences(),
                RoutineErrorCodes.ExecutionFailed);
            transaction.ClearTriggerAuditSequences();
            throw;
        }

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
                column.DefaultExpressionSql,
                column.IsAutoIncrement,
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
        if (statement.IsDefaultValues)
        {
            if (statement.Columns.Count != 0
                || statement.Rows.Count != 1
                || statement.Rows[0].Count != 0)
            {
                throw new InvalidOperationException("INSERT DEFAULT VALUES AST 的列和值必须为空。");
            }
        }

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

    private static object? ConvertInsertValue(SqlExpression expression, TableColumn column)
    {
        if (expression is not DefaultValueExpression)
            return ConvertTableValue(expression, column);

        return TryEvaluateColumnDefault(column, out var defaultValue)
            ? defaultValue
            : null;
    }

    private static BoundColumnDefault[] BindInsertDefaults(
        TableSchema schema,
        IReadOnlyList<TableColumn> bindings)
    {
        var assignedOrdinals = bindings
            .Select(static column => column.Ordinal)
            .ToHashSet();
        return schema.Columns
            .Where(column => column.DefaultExpressionSql is not null
                && !assignedOrdinals.Contains(column.Ordinal))
            .Select(static column => new BoundColumnDefault(
                column,
                SqlParser.ParsePredicate(column.DefaultExpressionSql!)))
            .ToArray();
    }

    private static void ApplyInsertDefaults(
        IReadOnlyList<BoundColumnDefault> defaults,
        object?[] values)
    {
        foreach (var binding in defaults)
            values[binding.Column.Ordinal] = EvaluateAndConvertDefault(binding.Expression, binding.Column);
    }

    internal static bool TryEvaluateColumnDefault(TableColumn column, out object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.DefaultExpressionSql is null)
        {
            value = null;
            return false;
        }

        value = EvaluateAndConvertDefault(
            SqlParser.ParsePredicate(column.DefaultExpressionSql),
            column);
        return true;
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
            if (column.IsRowVersion)
                throw new InvalidOperationException($"ROWVERSION 列 '{column.Name}' 由数据库自动维护，不允许显式赋值。");
            if (column.IsAutoIncrement)
                throw new InvalidOperationException($"AUTO_INCREMENT 列 '{column.Name}' 由数据库自动维护，不允许 UPDATE 显式赋值。");

            if (assignment.Value is DefaultValueExpression)
            {
                assignments.Add(new BoundAssignment(column, assignment.Value, UsesDefault: true));
                continue;
            }

            ValidateTableValueExpression(assignment.Value, schema, "UPDATE SET");

            assignments.Add(new BoundAssignment(column, assignment.Value, UsesDefault: false));
        }

        return [.. assignments];
    }

    private static object? EvaluateAssignment(
        BoundAssignment assignment,
        TableSchema schema,
        IReadOnlyList<object?> row)
    {
        if (assignment.UsesDefault)
        {
            return TryEvaluateColumnDefault(assignment.Column, out var defaultValue)
                ? defaultValue
                : null;
        }

        var evaluated = EvaluateScalar(assignment.Value, schema, row);
        return ConvertTableValue(evaluated, assignment.Column);
    }

    /// <summary>
    /// 在扫描行之前校验关系表值表达式，确保空结果也会报告未知列、错误参数或常量运算错误。
    /// </summary>
    private static void ValidateTableValueExpression(
        SqlExpression expression,
        TableSchema schema,
        string context)
    {
        switch (expression)
        {
            case LiteralExpression or DurationLiteralExpression:
                return;
            case IdentifierExpression identifier:
                _ = schema.TryGetColumn(identifier.Name)
                    ?? throw new InvalidOperationException($"{context} 中引用了未知列 '{identifier.Name}'。");
                return;
            case UnaryExpression { Operator: SqlUnaryOperator.Negate } unary:
                ValidateTableValueExpression(unary.Operand, schema, context);
                ValidateConstantArithmeticExpression(unary, schema);
                return;
            case BinaryExpression binary when IsArithmeticOperator(binary.Operator):
                ValidateTableValueExpression(binary.Left, schema, context);
                ValidateTableValueExpression(binary.Right, schema, context);
                ValidateConstantArithmeticExpression(binary, schema);
                return;
            case BinaryExpression binary when binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or
                || IsComparisonOperator(binary.Operator):
                ValidateTablePredicate(binary, schema, context);
                return;
            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                ValidateTablePredicate(unary, schema, context);
                return;
            case IsNullExpression isNull:
                ValidateTablePredicate(isNull, schema, context);
                return;
            case InExpression inExpression:
                ValidateTablePredicate(inExpression, schema, context);
                return;
            case CaseExpression caseExpression:
                foreach (var when in caseExpression.WhenClauses)
                {
                    ValidateTablePredicate(when.Condition, schema, context);
                    ValidateTableValueExpression(when.Result, schema, context);
                }
                if (caseExpression.Else is not null)
                    ValidateTableValueExpression(caseExpression.Else, schema, context);
                return;
            case FunctionCallExpression function
                when string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase):
                ValidateJsonValueFunction(function, schema, context);
                return;
            case FunctionCallExpression function when !function.IsStar
                && FunctionRegistry.TryGetScalar(function.Name, out var scalarFunction):
                ValidateScalarFunctionArgumentCount(function, scalarFunction);
                foreach (var argument in function.Arguments)
                    ValidateTableValueExpression(argument, schema, context);
                return;
            default:
                throw new InvalidOperationException(
                    $"{context} 暂不支持表达式 '{expression.GetType().Name}'。");
        }
    }

    /// <summary>
    /// 校验关系表 CASE WHEN 中使用的布尔谓词及其标量叶子节点。
    /// </summary>
    private static void ValidateTablePredicate(
        SqlExpression expression,
        TableSchema schema,
        string context)
    {
        switch (expression)
        {
            case BinaryExpression binary when binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or
                || IsComparisonOperator(binary.Operator):
                ValidateTablePredicate(binary.Left, schema, context);
                ValidateTablePredicate(binary.Right, schema, context);
                return;
            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                ValidateTablePredicate(unary.Operand, schema, context);
                return;
            case IsNullExpression isNull:
                ValidateTableValueExpression(isNull.Operand, schema, context);
                return;
            case InExpression { Subquery: null } inExpression:
                ValidateTableValueExpression(inExpression.Value, schema, context);
                foreach (var item in inExpression.Values)
                    ValidateTableValueExpression(item, schema, context);
                return;
            case InExpression:
                throw new InvalidOperationException($"{context} 的 CASE 条件暂不支持 IN 子查询。");
            default:
                ValidateTableValueExpression(expression, schema, context);
                return;
        }
    }

    /// <summary>
    /// 校验关系表 json_value 的固定参数形状，并递归校验 JSON 来源表达式。
    /// </summary>
    private static void ValidateJsonValueFunction(
        FunctionCallExpression function,
        TableSchema schema,
        string context)
    {
        if (function.IsStar
            || function.Arguments.Count != 2
            || function.Arguments[1] is not LiteralExpression { Kind: SqlLiteralKind.String })
        {
            throw new InvalidOperationException("json_value 需要两个参数，第二个参数必须是 JSON path 字符串字面量。");
        }

        ValidateTableValueExpression(function.Arguments[0], schema, context);
    }

    /// <summary>
    /// 对不含列和函数的常量算术子树立即求值，使类型、除零和溢出错误不依赖是否扫描到行。
    /// </summary>
    private static void ValidateConstantArithmeticExpression(
        SqlExpression expression,
        TableSchema schema)
    {
        if (IsConstantArithmeticExpression(expression))
            _ = EvaluateScalar(expression, schema, Array.Empty<object?>());
    }

    /// <summary>
    /// 判断表达式是否只由字面量、duration、一元负号和基础算术构成。
    /// </summary>
    private static bool IsConstantArithmeticExpression(SqlExpression expression)
        => expression switch
        {
            LiteralExpression or DurationLiteralExpression => true,
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary =>
                IsConstantArithmeticExpression(unary.Operand),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) =>
                IsConstantArithmeticExpression(binary.Left)
                && IsConstantArithmeticExpression(binary.Right),
            _ => false,
        };

    /// <summary>
    /// 在扫描关系表前校验标量函数参数个数，保证空结果语句也能报告调用错误。
    /// </summary>
    private static void ValidateScalarFunctionArgumentCount(
        FunctionCallExpression function,
        IScalarFunction scalarFunction)
    {
        if (function.Arguments.Count < scalarFunction.MinArgumentCount
            || function.Arguments.Count > scalarFunction.MaxArgumentCount)
        {
            string expected = scalarFunction.MinArgumentCount == scalarFunction.MaxArgumentCount
                ? scalarFunction.MinArgumentCount.ToString(CultureInfo.InvariantCulture)
                : $"{scalarFunction.MinArgumentCount}~{scalarFunction.MaxArgumentCount}";
            throw new InvalidOperationException(
                $"函数 {function.Name} 需要 {expected} 个参数，实际为 {function.Arguments.Count}。");
        }
    }

    private static void ValidateRequiredColumns(TableSchema schema, IReadOnlyList<object?> values)
    {
        if (values.Count != schema.Columns.Count)
            throw new InvalidOperationException("内部错误：行值数量与 schema 列数量不一致。");

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var column = schema.Columns[i];
            if (values[i] is null && !column.IsNullable && !column.IsAutoIncrement)
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
        // 现场批量合成查询通常是单列主键 IN；逐键点查可避免读取包含图片的大量无关行。
        if (TryLoadInCandidateRows(store, schema, where, out var inRows))
            return inRows;

        if (TryExtractPrimaryKeyValues(schema, where, allowExtraPredicates: true, out var keyValues))
        {
            var row = store.GetByPrimaryKey(keyValues);
            return row is null ? Array.Empty<TableRow>() : [row];
        }

        if (ChooseBestIndexAccessPlan(schema, where) is { } plan)
        {
            if (plan.Range is not null)
                return store.GetByIndexRange(plan.Index, plan.EqualityPrefixValues, plan.Range);

            return plan.IsFullEquality
                ? store.GetByIndex(plan.Index, plan.EqualityPrefixValues)
                : store.GetByIndexPrefix(plan.Index, plan.EqualityPrefixValues);
        }

        return store.Scan();
    }

    /// <summary>按安全的单列正向 IN 形状加载候选行，失败时返回 false 交给既有规划器。</summary>
    private static bool TryLoadInCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression? where,
        out IReadOnlyList<TableRow> rows)
    {
        rows = Array.Empty<TableRow>();
        if (!TryChooseInAccessPlan(schema, where, out var plan))
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TableRow>();
        foreach (var value in plan.Values)
        {
            IReadOnlyList<TableRow> candidates = plan.UsesPrimaryKey
                ? (store.GetByPrimaryKey([value]) is { } primary ? [primary] : Array.Empty<TableRow>())
                : store.GetByIndex(plan.Index!, [value]);
            foreach (var candidate in candidates)
            {
                var key = Convert.ToHexString(TableKeyCodec.EncodePrimaryKey(schema, candidate.Values));
                if (seen.Add(key))
                    result.Add(candidate);
            }
        }

        rows = result;
        return true;
    }

    /// <summary>识别单列主键或单列非 JSON 二级索引的正向 IN，并完成键值转换。</summary>
    internal static bool TryChooseInAccessPlan(
        TableSchema schema,
        SqlExpression? where,
        out TableInAccessPlan plan)
    {
        plan = null!;
        if (where is null)
            return false;

        InExpression? inExpression = null;
        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is not InExpression { Negated: false, Subquery: null } candidate)
                continue;
            if (inExpression is not null)
                return false;
            inExpression = candidate;
        }

        if (inExpression?.Value is not IdentifierExpression identifier)
        {
            return false;
        }

        TableColumn? column = schema.TryGetColumn(identifier.Name);
        if (column is null)
            return false;

        TableIndex? index = null;
        bool usesPrimaryKey = schema.PrimaryKey.Count == 1
            && string.Equals(schema.PrimaryKey[0], column.Name, StringComparison.Ordinal);
        if (!usesPrimaryKey)
        {
            index = schema.Indexes.FirstOrDefault(candidate =>
                candidate.Columns.Count == 1
                && string.IsNullOrWhiteSpace(candidate.JsonPath)
                && string.Equals(candidate.Columns[0], column.Name, StringComparison.Ordinal));
            if (index is null)
                return false;
        }

        var values = new List<object>(inExpression.Values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expression in inExpression.Values)
        {
            object? value;
            try
            {
                value = expression switch
                {
                    LiteralExpression literal => EvaluateLiteral(literal),
                    UnaryExpression
                    {
                        Operator: SqlUnaryOperator.Negate,
                        Operand: LiteralExpression negatedLiteral,
                    } => NegateLiteral(negatedLiteral),
                    DurationLiteralExpression duration => duration.Milliseconds,
                    MaterializedSubqueryValueExpression materialized => materialized.Value,
                    _ => throw new InvalidOperationException("IN 值不是可直接绑定的字面量。"),
                };
                if (value is null)
                    continue;
                if (!CanUsePrimaryKeyPointLookup(column.DataType, value))
                    return false;
                value = ConvertTableValue(value, column);
                if (value is null)
                    continue;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or ArgumentOutOfRangeException
                or InvalidCastException
                or FormatException
                or OverflowException)
            {
                return false;
            }

            var encoded = Convert.ToHexString(EncodeInLookupKey(schema, column, index, usesPrimaryKey, value));
            if (seen.Add(encoded))
                values.Add(value);
        }

        plan = new TableInAccessPlan(index, usesPrimaryKey, values);
        return true;
    }

    /// <summary>为主键或二级索引点查生成稳定去重键。</summary>
    private static byte[] EncodeInLookupKey(
        TableSchema schema,
        TableColumn column,
        TableIndex? index,
        bool usesPrimaryKey,
        object value)
        => usesPrimaryKey
            ? TableKeyCodec.EncodePrimaryKeyValues(schema, [value])
            : TableIndexCodec.EncodeLookupPrefix(index!, [value], schema)!;

    /// <summary>
    /// A materialized positive IN over a single-column primary key can use point reads. This is
    /// especially important for cleanup queries whose rows contain large image/video values.
    /// </summary>
    private static IReadOnlyList<TableRow> LoadMutationCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression where,
        out bool predicateSatisfied)
    {
        predicateSatisfied = false;
        if (schema.PrimaryKey.Count != 1
            || where is not InExpression
            {
                Negated: false,
                Subquery: null,
                Value: IdentifierExpression identifier,
            } inExpression
            || !string.Equals(identifier.Name, schema.PrimaryKey[0], StringComparison.Ordinal)
            || (identifier.Qualifier is not null
                && !string.Equals(identifier.Qualifier, schema.Name, StringComparison.OrdinalIgnoreCase))
            || inExpression.Values.Any(static value => value is not MaterializedSubqueryValueExpression))
        {
            return LoadCandidateRows(store, schema, where);
        }

        var primaryKeyColumn = schema.TryGetColumn(schema.PrimaryKey[0])!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TableRow>(inExpression.Values.Count);
        foreach (var expression in inExpression.Values.Cast<MaterializedSubqueryValueExpression>())
        {
            if (expression.Value is null)
                continue;
            if (!CanUsePrimaryKeyPointLookup(primaryKeyColumn.DataType, expression.Value))
                return LoadCandidateRows(store, schema, where);

            object? keyValue;
            try
            {
                keyValue = ConvertTableValue(expression.Value, primaryKeyColumn);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or ArgumentOutOfRangeException
                or InvalidCastException
                or FormatException
                or OverflowException)
            {
                // Cross-type SQL equality can be broader than physical key encoding; preserve
                // semantics by falling back to the normal predicate scan when a value cannot bind.
                return LoadCandidateRows(store, schema, where);
            }

            byte[] encoded = TableKeyCodec.EncodePrimaryKeyValues(schema, [keyValue]);
            if (!seen.Add(Convert.ToHexString(encoded)))
                continue;
            var row = store.GetByPrimaryKey([keyValue]);
            if (row is not null)
                rows.Add(row);
        }

        predicateSatisfied = true;
        return rows;
    }

    private static bool CanUsePrimaryKeyPointLookup(TableColumnType type, object value)
        => type switch
        {
            TableColumnType.Int64 => value is
                byte or sbyte or short or ushort or int or uint or long or ulong,
            // SQL Float64 equality treats +0.0 and -0.0 as equal, while physical keys preserve
            // their distinct IEEE bit patterns. A single point read is therefore not complete.
            TableColumnType.Float64 => false,
            TableColumnType.Boolean => value is bool,
            TableColumnType.String or TableColumnType.Json => value is string,
            TableColumnType.DateTime => value is DateTime or DateTimeOffset or long,
            TableColumnType.Blob => value is byte[],
            _ => false,
        };

    /// <summary>
    /// SELECT 候选行加载：在已提交基线上叠加当前 ambient 轻事务对本表的缓冲写（read-your-writes，#218）。
    /// 无活动事务、事务已结束或该表无缓冲写时走既有 PK/二级索引/scan 快路径；一旦本表有缓冲写，
    /// 则改为全表 scan 后叠加缓冲变更（快路径可能漏掉尚未提交的插入行或返回被缓冲更新覆盖前的旧值），
    /// 由调用方 WHERE 再过滤。事务 UPDATE/DELETE 也复用该叠加逻辑读取自身前序缓冲。
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
    /// 为单表 EXISTS 生成与实际候选读取共用的访问计划。
    /// </summary>
    /// <param name="schema">目标关系表结构。</param>
    /// <param name="where">已完成参数和相关外层值绑定的谓词。</param>
    /// <returns>主键、二级索引或全表扫描计划。</returns>
    internal static TableExistsAccessPlan PlanExistsAccess(TableSchema schema, SqlExpression? where)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (SqlTransactionContext.Current is { } transaction
            && transaction.TryGetBufferedMutations(schema.Name, out _))
        {
            return CreateTransactionOverlayExistsPlan(where);
        }

        if (CanUsePrimaryKeyLookup(schema, where))
        {
            bool predicateCovered = IsWhereFullyCoveredByPrimaryKey(where, schema);
            return new TableExistsAccessPlan(
                AccessPath: "primary_key",
                IndexName: "primary",
                UsesPrimaryKey: true,
                IndexPlan: null,
                PredicateCovered: predicateCovered,
                HasResidualPredicate: !predicateCovered);
        }

        if (TryChooseInAccessPlan(schema, where, out var inPlan))
        {
            return new TableExistsAccessPlan(
                AccessPath: inPlan.UsesPrimaryKey ? "primary_key_in" : "secondary_index_in",
                IndexName: inPlan.UsesPrimaryKey ? "primary" : inPlan.Index!.Name,
                UsesPrimaryKey: false,
                IndexPlan: null,
                PredicateCovered: IsWhereOnlyInPredicate(where),
                HasResidualPredicate: !IsWhereOnlyInPredicate(where),
                InPlan: inPlan);
        }

        if (ChooseBestIndexAccessPlan(schema, where) is { } indexPlan)
        {
            bool predicateCovered = IsWhereFullyCoveredByIndexPlan(where, schema, indexPlan);
            return new TableExistsAccessPlan(
                AccessPath: FormatIndexAccessPath(indexPlan),
                IndexName: indexPlan.Index.Name,
                UsesPrimaryKey: false,
                IndexPlan: indexPlan,
                PredicateCovered: predicateCovered,
                HasResidualPredicate: !predicateCovered);
        }

        return new TableExistsAccessPlan(
            AccessPath: "table_scan",
            IndexName: null,
            UsesPrimaryKey: false,
            IndexPlan: null,
            PredicateCovered: where is null,
            HasResidualPredicate: where is not null,
            FallbackReason: where is null ? null : "no_sargable_predicate");
    }

    /// <summary>
    /// 按 EXISTS 访问计划加载候选行；完整覆盖的谓词最多读取一行，残余谓词保留全部必要候选。
    /// </summary>
    /// <param name="store">目标表存储。</param>
    /// <param name="schema">目标关系表结构。</param>
    /// <param name="where">已完成参数和相关外层值绑定的谓词。</param>
    /// <returns>实际计划和待复检候选行。</returns>
    internal static TableExistsCandidateRows LoadExistsCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schema);

        var plan = PlanExistsAccess(schema, where);
        var transaction = SqlTransactionContext.Current;
        if (transaction is not null && transaction.TryGetBufferedMutations(schema.Name, out var buffered))
        {
            var overlayRows = ApplyMutationOverlay(schema, store.Scan(), buffered);
            return new TableExistsCandidateRows(plan, overlayRows);
        }

        if (plan.UsesPrimaryKey)
        {
            _ = TryExtractPrimaryKeyValues(
                schema,
                where,
                allowExtraPredicates: true,
                out var keyValues,
                allowNonEqualityExtraPredicates: true);
            var row = store.GetByPrimaryKey(keyValues);
            return new TableExistsCandidateRows(
                plan,
                row is null ? Array.Empty<TableRow>() : [row]);
        }

        if (plan.InPlan is { } inPlan)
        {
            var rows = new List<TableRow>();
            foreach (var value in inPlan.Values)
            {
                if (inPlan.UsesPrimaryKey)
                {
                    if (store.GetByPrimaryKey([value]) is { } primary)
                        rows.Add(primary);
                }
                else
                {
                    rows.AddRange(store.GetByIndex(inPlan.Index!, [value]));
                }
            }
            return new TableExistsCandidateRows(plan, rows);
        }

        int? candidateLimit = plan.PredicateCovered ? 1 : null;
        if (plan.IndexPlan is { } indexPlan)
        {
            IReadOnlyList<TableRow> rows = indexPlan.Range is not null
                ? store.GetByIndexRange(
                    indexPlan.Index,
                    indexPlan.EqualityPrefixValues,
                    indexPlan.Range,
                    candidateLimit)
                : indexPlan.IsFullEquality
                    ? store.GetByIndex(indexPlan.Index, indexPlan.EqualityPrefixValues, candidateLimit)
                    : store.GetByIndexPrefix(indexPlan.Index, indexPlan.EqualityPrefixValues, candidateLimit);
            return new TableExistsCandidateRows(plan, rows);
        }

        return new TableExistsCandidateRows(plan, store.Scan(candidateLimit));
    }

    /// <summary>
    /// 构造事务写集要求的全表扫描计划，确保 EXPLAIN 与实际 overlay 读取保持一致。
    /// </summary>
    private static TableExistsAccessPlan CreateTransactionOverlayExistsPlan(SqlExpression? where)
        => new(
            AccessPath: "table_scan",
            IndexName: null,
            UsesPrimaryKey: false,
            IndexPlan: null,
            PredicateCovered: false,
            HasResidualPredicate: where is not null,
            FallbackReason: "transaction_overlay_requires_scan");

    /// <summary>
    /// 为普通关系 SELECT 加载候选行；仅在范围索引满足升序且 WHERE 无残余时安全下推分页候选上限。
    /// </summary>
    private static (IReadOnlyList<TableRow> Rows, bool RangeOrderSatisfied) LoadSelectCandidateRowsForStatement(
        TableStore store,
        TableSchema schema,
        SelectStatement statement,
        IReadOnlyList<Projection> projections)
    {
        var transaction = SqlTransactionContext.Current;
        if (transaction is not null && transaction.TryGetBufferedMutations(schema.Name, out var buffered))
            return (ApplyMutationOverlay(schema, store.Scan(), buffered), false);

        var plan = ChooseBestIndexAccessPlan(schema, statement.Where);
        if (plan?.Range is not null
            && TryGetOrderedRangeCandidateLimit(
                statement,
                schema,
                projections,
                plan,
                out int candidateLimit))
        {
            if (statement.OrderByList.Count > 1)
            {
                return (
                    store.GetByIndexRangeThroughValueGroup(
                        plan.Index,
                        plan.EqualityPrefixValues,
                        plan.Range,
                        candidateLimit),
                    false);
            }

            return (
                store.GetByIndexRange(plan.Index, plan.EqualityPrefixValues, plan.Range, candidateLimit),
                true);
        }

        return (LoadCandidateRows(store, schema, statement.Where), false);
    }

    /// <summary>
    /// 判断范围索引能否同时满足 ORDER BY ASC 与 LIMIT/OFFSET，并计算安全的分页边界。
    /// </summary>
    private static bool TryGetOrderedRangeCandidateLimit(
        SelectStatement statement,
        TableSchema schema,
        IReadOnlyList<Projection> projections,
        TableIndexAccessPlan plan,
        out int candidateLimit)
    {
        candidateLimit = 0;
        if (plan.Range is null
            || !OrderByMatchesRangeIndexSequence(
                statement.OrderByList,
                schema,
                projections,
                plan)
            || !IsWhereFullyCoveredByRangePlan(statement.Where, schema, plan))
        {
            return false;
        }

        return TryGetPaginationCandidateLimit(statement.Pagination, out candidateLimit);
    }

    /// <summary>
    /// 计算可由存储扫描表达的 OFFSET + LIMIT 候选上限，超出 Int32 容量时放弃下推。
    /// </summary>
    internal static bool TryGetPaginationCandidateLimit(
        PaginationSpec? pagination,
        out int candidateLimit)
    {
        candidateLimit = 0;
        if (pagination?.Fetch is not int fetch)
            return false;

        long requested = (long)pagination.Offset + fetch;
        if (requested > int.MaxValue)
            return false;

        candidateLimit = (int)requested;
        return true;
    }

    /// <summary>
    /// 确认 ORDER BY 从范围列开始连续匹配索引后缀；非唯一索引还可继续匹配隐式主键后缀。
    /// </summary>
    private static bool OrderByMatchesRangeIndexSequence(
        IReadOnlyList<OrderBySpec> orderBy,
        TableSchema schema,
        IReadOnlyList<Projection> projections,
        TableIndexAccessPlan plan)
    {
        if (plan.Range is null || orderBy.Count == 0)
            return false;

        int explicitStart = plan.EqualityPrefixValues.Count;
        int explicitCount = plan.Index.Columns.Count - explicitStart;
        int implicitCount = plan.Index.IsUnique ? 0 : schema.PrimaryKey.Count;
        if (orderBy.Count > explicitCount + implicitCount)
            return false;

        for (int i = 0; i < orderBy.Count; i++)
        {
            if (orderBy[i] is not
                {
                    Direction: SortDirection.Ascending,
                    Expression: IdentifierExpression orderIdentifier,
                })
            {
                return false;
            }

            string expectedColumnName = i < explicitCount
                ? plan.Index.Columns[explicitStart + i]
                : schema.PrimaryKey[i - explicitCount];
            var expectedColumn = schema.TryGetColumn(expectedColumnName)
                ?? throw new InvalidOperationException(
                    $"索引 '{plan.Index.Name}' 引用了未知列 '{expectedColumnName}'。");
            if (!OrderByResolvesToColumn(orderIdentifier, projections, expectedColumn))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 按关系 SELECT 当前的别名优先规则确认 ORDER BY 最终绑定到指定原始列。
    /// </summary>
    private static bool OrderByResolvesToColumn(
        IdentifierExpression orderIdentifier,
        IReadOnlyList<Projection> projections,
        TableColumn expectedColumn)
    {
        foreach (var projection in projections)
        {
            if (!string.Equals(projection.ColumnName, orderIdentifier.Name, StringComparison.Ordinal))
                continue;

            return projection.Kind == ProjectionKind.Column
                && projection.Column?.Ordinal == expectedColumn.Ordinal;
        }

        // ORDER BY 未命中投影名时，执行器会把同名源列作为隐藏排序列。
        return string.Equals(orderIdentifier.Name, expectedColumn.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// 确认 WHERE 的每个 AND 叶子都已由等值前缀或范围约束表达，防止在残余过滤前错误截断候选。
    /// </summary>
    private static bool IsWhereFullyCoveredByRangePlan(
        SqlExpression? where,
        TableSchema schema,
        TableIndexAccessPlan plan)
        => plan.Range is not null && IsWhereFullyCoveredByIndexPlan(where, schema, plan);

    /// <summary>
    /// 确认 WHERE 只包含完整主键等值约束，用于 EXPLAIN 标记残余谓词。
    /// </summary>
    private static bool IsWhereFullyCoveredByPrimaryKey(SqlExpression? where, TableSchema schema)
    {
        if (where is null
            || !TryCollectEqualityExpressions(where, allowNonEquality: false, out var equalityByColumn)
            || equalityByColumn.Count != schema.PrimaryKey.Count)
        {
            return false;
        }

        foreach (var columnName in schema.PrimaryKey)
        {
            if (!equalityByColumn.ContainsKey(columnName))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 确认 WHERE 的每个 AND 叶子都已由当前索引计划表达，避免在残余过滤前错误截断候选。
    /// </summary>
    private static bool IsWhereFullyCoveredByIndexPlan(
        SqlExpression? where,
        TableSchema schema,
        TableIndexAccessPlan plan)
    {
        if (where is null)
            return false;

        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is BinaryExpression { Operator: SqlBinaryOperator.Equal } equality)
            {
                var (identifier, expression) = NormalizeIdentifierComparison(equality);
                if (identifier is null || expression is null)
                    return false;

                int equalityIndex = -1;
                for (int i = 0; i < plan.EqualityPrefixValues.Count; i++)
                {
                    if (string.Equals(plan.Index.Columns[i], identifier.Name, StringComparison.Ordinal))
                    {
                        equalityIndex = i;
                        break;
                    }
                }

                if (equalityIndex < 0)
                    return false;

                var column = schema.TryGetColumn(identifier.Name)
                    ?? throw new InvalidOperationException($"索引 '{plan.Index.Name}' 引用了未知列 '{identifier.Name}'。");
                try
                {
                    if (!ValuesEqual(ConvertTableValue(expression, column), plan.EqualityPrefixValues[equalityIndex]))
                        return false;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or ArgumentOutOfRangeException
                    or InvalidCastException
                    or FormatException
                    or OverflowException)
                {
                    return false;
                }

                continue;
            }

            if (plan.Range is not null
                && leaf is BinaryExpression rangeComparison
                && TryNormalizeRangeComparison(
                    rangeComparison,
                    plan.Range.Column.Name,
                    out _,
                    out var rangeExpression)
                && TryConvertRangeBound(rangeExpression, plan.Range.Column, out _))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// 把二级索引计划映射为稳定的访问路径名称，供运行时指标与 EXPLAIN 对拍。
    /// </summary>
    private static string FormatIndexAccessPath(TableIndexAccessPlan plan)
        => !string.IsNullOrWhiteSpace(plan.Index.JsonPath)
            ? "json_path_index"
            : plan.Range is not null
                ? "secondary_index_range"
                : plan.IsFullEquality ? "secondary_index" : "secondary_index_prefix";

    /// <summary>判断 WHERE 是否仅由一个正向 IN 谓词组成。</summary>
    private static bool IsWhereOnlyInPredicate(SqlExpression? where)
        => where is InExpression { Negated: false, Subquery: null };

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

    /// <summary>
    /// 提取完整主键等值；调用方可分别允许额外等值谓词或任意非等值残余谓词。
    /// </summary>
    private static bool TryExtractPrimaryKeyValues(
        TableSchema schema,
        SqlExpression? where,
        bool allowExtraPredicates,
        out IReadOnlyList<object?> keyValues,
        bool allowNonEqualityExtraPredicates = false)
    {
        keyValues = Array.Empty<object?>();
        if (where is null)
            return false;

        if (!TryCollectEqualityExpressions(
            where,
            allowNonEquality: allowNonEqualityExtraPredicates,
            out var equalityByColumn))
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
            if (!CanUseIndexEqualityLookup(column, expression))
                return false;
            try
            {
                values[i] = ConvertTableValue(expression, column);
                if (values[i] is null)
                    return false;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or ArgumentOutOfRangeException
                or InvalidCastException
                or FormatException
                or OverflowException)
            {
                return false;
            }
        }

        keyValues = values;
        return true;
    }

    internal static TableIndexAccessPlan? ChooseBestIndexAccessPlan(
        TableSchema schema,
        SqlExpression? where)
    {
        if (TryExtractPrimaryKeyValues(schema, where, allowExtraPredicates: true, out _))
            return null;
        if (where is null || schema.Indexes.Count == 0)
            return null;

        bool hasColumnEqualities = TryCollectEqualityExpressions(
            where,
            allowNonEquality: true,
            out var equalityByColumn);
        TableIndexAccessPlan? bestPlan = null;

        foreach (var candidate in schema.Indexes.OrderByDescending(static i => i.Columns.Count))
        {
            IReadOnlyList<object?> candidateValues;
            TableIndexRange? candidateRange = null;
            if (!string.IsNullOrWhiteSpace(candidate.JsonPath))
            {
                if (!TryExtractJsonPathIndexValue(candidate, where, out var jsonPathValue))
                    continue;

                candidateValues = [jsonPathValue];
            }
            else
            {
                var values = new List<object?>(candidate.Columns.Count);
                if (hasColumnEqualities)
                {
                    for (int i = 0; i < candidate.Columns.Count; i++)
                    {
                        if (!equalityByColumn.TryGetValue(candidate.Columns[i], out var expression))
                            break;

                        var column = schema.TryGetColumn(candidate.Columns[i])
                            ?? throw new InvalidOperationException($"索引 '{candidate.Name}' 引用了未知列 '{candidate.Columns[i]}'。");
                        if (!CanUseIndexEqualityLookup(column, expression))
                            break;
                        try
                        {
                            values.Add(ConvertTableValue(expression, column));
                        }
                        catch (Exception exception) when (exception is InvalidOperationException
                            or ArgumentOutOfRangeException
                            or InvalidCastException
                            or FormatException
                            or OverflowException)
                        {
                            // 已成功绑定的前导列仍可缩小候选集，当前列及其后缀留给残余谓词判断。
                            break;
                        }
                    }
                }

                if (values.Count < candidate.Columns.Count)
                {
                    var rangeColumn = schema.TryGetColumn(candidate.Columns[values.Count])
                        ?? throw new InvalidOperationException($"索引 '{candidate.Name}' 引用了未知列 '{candidate.Columns[values.Count]}'。");
                    if (rangeColumn.DataType is TableColumnType.Int64 or TableColumnType.DateTime)
                        _ = TryExtractIndexRange(where, rangeColumn, out candidateRange);
                }

                if (values.Count == 0 && candidateRange is null)
                    continue;

                int matchedColumnCount = values.Count + (candidateRange is null ? 0 : 1);
                bool isFullEquality = candidateRange is null && values.Count == candidate.Columns.Count;
                if (!isFullEquality
                    && candidate.IsUnique
                    && HasNullableUnmatchedIndexColumn(schema, candidate, matchedColumnCount))
                {
                    // 唯一索引不保存任何含 NULL 的键；未绑定可空后缀会使前缀或范围扫描漏行。
                    continue;
                }

                candidateValues = values;
            }

            var candidatePlan = new TableIndexAccessPlan(candidate, candidateValues, candidateRange);
            if (bestPlan is null)
            {
                bestPlan = candidatePlan;
                continue;
            }

            bool candidateIsUniquePoint = candidatePlan.Index.IsUnique && candidatePlan.IsFullEquality;
            bool bestIsUniquePoint = bestPlan.Index.IsUnique && bestPlan.IsFullEquality;
            if ((candidateIsUniquePoint && !bestIsUniquePoint)
                || (candidateIsUniquePoint == bestIsUniquePoint
                    && (candidatePlan.MatchedColumnCount > bestPlan.MatchedColumnCount
                        || (candidatePlan.MatchedColumnCount == bestPlan.MatchedColumnCount
                            && candidatePlan.EqualityPrefixValues.Count > bestPlan.EqualityPrefixValues.Count)
                        || (candidatePlan.MatchedColumnCount == bestPlan.MatchedColumnCount
                            && candidatePlan.EqualityPrefixValues.Count == bestPlan.EqualityPrefixValues.Count
                            && candidatePlan.IsFullEquality
                            && !bestPlan.IsFullEquality))))
            {
                bestPlan = candidatePlan;
            }
        }

        return bestPlan;
    }

    /// <summary>
    /// 判断 WHERE 是否能按完整主键字面量执行点查，供执行与 EXPLAIN 复用同一判定。
    /// </summary>
    internal static bool CanUsePrimaryKeyLookup(TableSchema schema, SqlExpression? where)
        => TryExtractPrimaryKeyValues(
            schema,
            where,
            allowExtraPredicates: true,
            out _,
            allowNonEqualityExtraPredicates: true);

    /// <summary>
    /// 判断 SQL 等值字面量是否能由单个物理键完整覆盖，避免数值折叠或多种位表示漏行。
    /// </summary>
    private static bool CanUseIndexEqualityLookup(TableColumn column, SqlExpression expression)
    {
        object? value;
        try
        {
            if (expression is LiteralExpression literal)
            {
                value = EvaluateLiteral(literal);
            }
            else if (expression is UnaryExpression
            {
                Operator: SqlUnaryOperator.Negate,
                Operand: LiteralExpression negatedLiteral,
            })
            {
                value = NegateLiteral(negatedLiteral);
            }
            else if (expression is DurationLiteralExpression duration)
            {
                value = duration.Milliseconds;
            }
            else if (expression is MaterializedSubqueryValueExpression materialized)
            {
                value = materialized.Value;
            }
            else
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
        {
            return false;
        }

        // “= NULL”没有 SQL 真值行；允许空前缀候选仍不会漏掉有效结果。
        return value is null || CanUsePrimaryKeyPointLookup(column.DataType, value);
    }

    /// <summary>
    /// 判断唯一联合索引未绑定的后缀中是否存在可空列。
    /// </summary>
    private static bool HasNullableUnmatchedIndexColumn(
        TableSchema schema,
        TableIndex index,
        int matchedColumnCount)
    {
        for (int i = matchedColumnCount; i < index.Columns.Count; i++)
        {
            var column = schema.TryGetColumn(index.Columns[i])
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列 '{index.Columns[i]}'。");
            if (column.IsNullable)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 从 AND 谓词中提取指定 Int64/DATETIME 列的最强上下界。
    /// 无法无损绑定的比较保留为残余条件，不参与物理范围裁剪。
    /// </summary>
    private static bool TryExtractIndexRange(
        SqlExpression where,
        TableColumn column,
        out TableIndexRange? range)
    {
        TableIndexRangeBound? lower = null;
        TableIndexRangeBound? upper = null;
        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is not BinaryExpression binary
                || !TryNormalizeRangeComparison(binary, column.Name, out var rangeOperator, out var expression)
                || !TryConvertRangeBound(expression, column, out long value))
            {
                continue;
            }

            switch (rangeOperator)
            {
                case SqlBinaryOperator.GreaterThan:
                    lower = SelectStrongerLower(lower, new TableIndexRangeBound(value, Inclusive: false));
                    break;
                case SqlBinaryOperator.GreaterThanOrEqual:
                    lower = SelectStrongerLower(lower, new TableIndexRangeBound(value, Inclusive: true));
                    break;
                case SqlBinaryOperator.LessThan:
                    upper = SelectStrongerUpper(upper, new TableIndexRangeBound(value, Inclusive: false));
                    break;
                case SqlBinaryOperator.LessThanOrEqual:
                    upper = SelectStrongerUpper(upper, new TableIndexRangeBound(value, Inclusive: true));
                    break;
            }
        }

        range = lower is null && upper is null
            ? null
            : new TableIndexRange(column, lower, upper);
        return range is not null;
    }

    /// <summary>
    /// 把列位于比较右侧的条件翻转为统一的“列 operator 值”形式。
    /// </summary>
    private static bool TryNormalizeRangeComparison(
        BinaryExpression binary,
        string columnName,
        out SqlBinaryOperator rangeOperator,
        out SqlExpression expression)
    {
        rangeOperator = binary.Operator;
        expression = null!;
        if (binary.Operator is not (SqlBinaryOperator.LessThan
            or SqlBinaryOperator.LessThanOrEqual
            or SqlBinaryOperator.GreaterThan
            or SqlBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        if (binary.Left is IdentifierExpression left
            && string.Equals(left.Name, columnName, StringComparison.Ordinal))
        {
            expression = binary.Right;
            return true;
        }

        if (binary.Right is not IdentifierExpression right
            || !string.Equals(right.Name, columnName, StringComparison.Ordinal))
        {
            return false;
        }

        rangeOperator = binary.Operator switch
        {
            SqlBinaryOperator.LessThan => SqlBinaryOperator.GreaterThan,
            SqlBinaryOperator.LessThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
            SqlBinaryOperator.GreaterThan => SqlBinaryOperator.LessThan,
            SqlBinaryOperator.GreaterThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
            _ => throw new InvalidOperationException("内部错误：无法翻转非范围比较运算符。"),
        };
        expression = binary.Left;
        return true;
    }

    /// <summary>
    /// 把范围字面量无损转换为索引使用的有符号值；DATETIME 统一为 Unix 毫秒。
    /// </summary>
    private static bool TryConvertRangeBound(
        SqlExpression expression,
        TableColumn column,
        out long value)
    {
        value = 0;
        if (!IsIntegralRangeLiteral(expression))
            return false;

        try
        {
            object? converted = ConvertTableValue(expression, column);
            value = column.DataType switch
            {
                TableColumnType.Int64 => (long)converted!,
                TableColumnType.DateTime => new DateTimeOffset((DateTime)converted!).ToUnixTimeMilliseconds(),
                _ => throw new InvalidOperationException("范围索引只支持 Int64 或 DATETIME。"),
            };
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentOutOfRangeException
            or InvalidCastException
            or FormatException
            or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// 判断表达式是否是不会因数值舍入改变比较语义的整数范围字面量。
    /// </summary>
    private static bool IsIntegralRangeLiteral(SqlExpression expression)
        => expression is LiteralExpression { Kind: SqlLiteralKind.Integer }
            or UnaryExpression
        {
            Operator: SqlUnaryOperator.Negate,
            Operand: LiteralExpression { Kind: SqlLiteralKind.Integer }
        }
            or DurationLiteralExpression;

    /// <summary>
    /// 合并下界，值更大或同值排他的边界更强。
    /// </summary>
    private static TableIndexRangeBound SelectStrongerLower(
        TableIndexRangeBound? current,
        TableIndexRangeBound candidate)
        => current is null
            || candidate.Value > current.Value.Value
            || (candidate.Value == current.Value.Value && !candidate.Inclusive && current.Value.Inclusive)
                ? candidate
                : current.Value;

    /// <summary>
    /// 合并上界，值更小或同值排他的边界更强。
    /// </summary>
    private static TableIndexRangeBound SelectStrongerUpper(
        TableIndexRangeBound? current,
        TableIndexRangeBound candidate)
        => current is null
            || candidate.Value < current.Value.Value
            || (candidate.Value == current.Value.Value && !candidate.Inclusive && current.Value.Inclusive)
                ? candidate
                : current.Value;

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
            if (item.Expression is not StarExpression)
                ValidateTableValueExpression(item.Expression, schema, "SELECT 投影");

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
                    projections.Add(Projection.Expression(item.Alias ?? "expression", item.Expression));
                    break;
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

    internal static bool EvaluateCheckConstraint(
        SqlExpression expression,
        TableSchema schema,
        IReadOnlyList<object?> row)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(row);

        // SQL CHECK 只拒绝明确 FALSE；TRUE 与 UNKNOWN（NULL 传播）均通过。
        return EvaluateKleene(expression, schema, row) != false;
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
        var sawNull = false;
        foreach (var item in expression.Values)
        {
            var candidate = EvaluateScalar(item, schema, row);
            if (value is null || candidate is null)
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
            DurationLiteralExpression duration => duration.Milliseconds,
            MaterializedSubqueryValueExpression materialized => materialized.Value,
            IdentifierExpression identifier => GetColumnValue(schema, row, identifier.Name),
            FunctionCallExpression function => EvaluateFunction(function, schema, row),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => EvaluateNegation(unary, schema, row),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(binary, schema, row),
            BinaryExpression binary when binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or
                || IsComparisonOperator(binary.Operator) => EvaluateKleene(binary, schema, row),
            UnaryExpression { Operator: SqlUnaryOperator.Not } unary => EvaluateKleene(unary, schema, row),
            IsNullExpression isNull => EvaluateKleene(isNull, schema, row),
            InExpression inExpression => EvaluateKleene(inExpression, schema, row),
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

        if (string.Equals(function.Name, "coalesce", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count > 0)
        {
            foreach (var argument in function.Arguments)
            {
                var value = EvaluateScalar(argument, schema, row);
                if (value is not null)
                    return value;
            }

            return null;
        }

        if (string.Equals(function.Name, "regexp_like", StringComparison.OrdinalIgnoreCase))
        {
            ValidateRegexpLikeArguments(function);
            return RegexPatternMatcher.IsMatch(
                EvaluateScalar(function.Arguments[0], schema, row),
                EvaluateScalar(function.Arguments[1], schema, row),
                function.Arguments.Count == 3 ? EvaluateScalar(function.Arguments[2], schema, row) : null);
        }

        if (FunctionRegistry.TryGetScalar(function.Name, out var scalarFunction))
        {
            var arguments = function.Arguments
                .Select(argument => EvaluateScalar(argument, schema, row))
                .ToArray();
            return scalarFunction.Evaluate(arguments);
        }

        throw new InvalidOperationException($"关系表不支持标量函数 '{function.Name}'。");
    }

    private static void ValidateRegexpLikeArguments(FunctionCallExpression function)
    {
        if (function.IsStar || function.Arguments.Count is < 2 or > 3)
            throw new InvalidOperationException("函数 regexp_like 需要 2~3 个参数。");
    }

    private static object? EvaluateNegation(
        UnaryExpression unary,
        TableSchema schema,
        IReadOnlyList<object?> row)
    {
        var value = EvaluateScalar(unary.Operand, schema, row);
        return SqlScalarOperations.Negate(value);
    }

    private static object? EvaluateArithmetic(BinaryExpression binary, TableSchema schema, IReadOnlyList<object?> row)
    {
        var leftValue = EvaluateScalar(binary.Left, schema, row);
        var rightValue = EvaluateScalar(binary.Right, schema, row);
        return SqlScalarOperations.EvaluateArithmetic(binary.Operator, leftValue, rightValue);
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
            MaterializedSubqueryValueExpression materialized => materialized.Value,
            _ => throw new InvalidOperationException(
                $"列 '{column.Name}' 的值必须是字面量，不支持表达式 ({expression.GetType().Name})。"),
        };

        return ConvertTableValue(value, column);
    }

    private static string ValidateAndFormatDefault(SqlExpression expression, TableColumn column)
    {
        var value = EvaluateAndConvertSchemaDefault(expression, column);
        if (!column.IsNullable && value is null)
            throw new InvalidOperationException($"NOT NULL 列 '{column.Name}' 的 DEFAULT 不能是 NULL。");
        return SqlExpressionFormatter.Format(expression);
    }

    private static object? EvaluateAndConvertDefault(SqlExpression expression, TableColumn column)
        => ConvertTableValue(EvaluateDefaultExpression(expression), column);

    private static object? EvaluateAndConvertSchemaDefault(SqlExpression expression, TableColumn column)
        => ConvertTableValueForSchemaChange(EvaluateDefaultExpression(expression), column);

    private static object? EvaluateDefaultExpression(SqlExpression expression)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            DurationLiteralExpression duration => duration.Milliseconds,
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary =>
                SqlScalarOperations.Negate(EvaluateDefaultExpression(unary.Operand)),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) =>
                SqlScalarOperations.EvaluateArithmetic(
                    binary.Operator,
                    EvaluateDefaultExpression(binary.Left),
                    EvaluateDefaultExpression(binary.Right)),
            FunctionCallExpression { IsStar: false } function => EvaluateDefaultFunction(function),
            _ => throw new InvalidOperationException(
                $"列 DEFAULT 只支持字面量、常量算术和内置标量函数，不支持 {expression.GetType().Name}。"),
        };
    }

    private static object? EvaluateDefaultFunction(FunctionCallExpression function)
    {
        var scalarFunction = FunctionRegistry.ScalarFunctions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, function.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"列 DEFAULT 不支持标量函数 '{function.Name}'。");
        ValidateScalarFunctionArgumentCount(function, scalarFunction);
        var arguments = function.Arguments
            .Select(EvaluateDefaultExpression)
            .ToArray();
        return scalarFunction.Evaluate(arguments);
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

    /// <summary>
    /// Schema 迁移使用的严格类型转换。普通 INSERT 保持既有的数值兼容转换，
    /// 但 ALTER COLUMN 不能静默舍入或丢失整数精度。
    /// </summary>
    private static object? ConvertTableValueForSchemaChange(object? value, TableColumn column)
    {
        var converted = ConvertTableValue(value, column);
        if (value is null || converted is null)
            return converted;

        if (column.DataType == TableColumnType.Int64)
        {
            long integerTarget = (long)converted;
            bool isLossy = value switch
            {
                float floatSource => !float.IsFinite(floatSource) || (float)integerTarget != floatSource,
                double doubleSource => !double.IsFinite(doubleSource) || (double)integerTarget != doubleSource,
                _ when TryGetDecimalNumeric(value, out var decimalSource) =>
                    decimalSource != decimal.Truncate(decimalSource)
                    || decimalSource < long.MinValue
                    || decimalSource > long.MaxValue
                    || integerTarget != decimalSource,
                _ => false,
            };
            if (isLossy)
            {
                throw LossySchemaConversion(value, column);
            }
        }
        else if (column.DataType == TableColumnType.Float64)
        {
            double floatingTarget = (double)converted;
            if (!double.IsFinite(floatingTarget))
                throw LossySchemaConversion(value, column);

            if (TryGetIntegerNumeric(value, out var integerSource))
            {
                if (floatingTarget != Math.Truncate(floatingTarget)
                    || new BigInteger(floatingTarget) != integerSource)
                {
                    throw LossySchemaConversion(value, column);
                }
            }
            else if (TryGetDecimalNumeric(value, out var floatingSource))
            {
                decimal roundTripped;
                try
                {
                    roundTripped = Convert.ToDecimal(floatingTarget, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    throw LossySchemaConversion(value, column);
                }

                if (roundTripped != floatingSource)
                    throw LossySchemaConversion(value, column);
            }
        }

        return converted;
    }

    private static bool TryGetDecimalNumeric(object value, out decimal numeric)
    {
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                numeric = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            case float f when float.IsFinite(f):
                numeric = Convert.ToDecimal(f, CultureInfo.InvariantCulture);
                return true;
            case double d when double.IsFinite(d):
                try
                {
                    numeric = Convert.ToDecimal(d, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (OverflowException)
                {
                    break;
                }
            case string s when decimal.TryParse(
                s,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed):
                numeric = parsed;
                return true;
        }

        numeric = default;
        return false;
    }

    private static bool TryGetIntegerNumeric(object value, out BigInteger numeric)
    {
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                numeric = new BigInteger(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                return true;
            case decimal source when source == decimal.Truncate(source):
                numeric = new BigInteger(source);
                return true;
            case float source when float.IsFinite(source) && source == MathF.Truncate(source):
                numeric = new BigInteger(source);
                return true;
            case double source when double.IsFinite(source) && source == Math.Truncate(source):
                numeric = new BigInteger(source);
                return true;
            case string source when decimal.TryParse(
                source,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) && parsed == decimal.Truncate(parsed):
                numeric = new BigInteger(parsed);
                return true;
            default:
                numeric = default;
                return false;
        }
    }

    private static InvalidOperationException LossySchemaConversion(object value, TableColumn column)
        => new(
            $"ALTER COLUMN 不能将值 '{value}' 无损转换为列 '{column.Name}' 的 {column.DataType} 类型。");

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

    private static TableColumn[] ResolveHiddenOrderColumns(
        IReadOnlyList<Projection> projections,
        IReadOnlyList<OrderBySpec> orderBy,
        TableSchema schema)
    {
        var projectedNames = projections
            .Select(static projection => projection.ColumnName)
            .ToHashSet(StringComparer.Ordinal);
        var hiddenOrdinals = new HashSet<int>();
        var hiddenColumns = new List<TableColumn>();

        foreach (var order in orderBy)
        {
            if (order.Expression is not IdentifierExpression identifier
                || projectedNames.Contains(identifier.Name))
            {
                continue;
            }

            var column = schema.TryGetColumn(identifier.Name);
            if (column is not null && hiddenOrdinals.Add(column.Ordinal))
                hiddenColumns.Add(column);
        }

        return hiddenColumns.ToArray();
    }

    private static SelectExecutionResult RemoveHiddenOrderColumns(
        SelectExecutionResult result,
        int visibleColumnCount)
    {
        var rows = result.Rows
            .Select(row => (IReadOnlyList<object?>)row.Take(visibleColumnCount).ToArray())
            .ToArray();
        return new SelectExecutionResult(
            result.Columns.Take(visibleColumnCount).ToArray(),
            rows);
    }

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
        => SqlScalarComparer.ValuesEqual(left, right);

    private static int? CompareScalar(object? left, object? right)
        => SqlScalarComparer.Compare(left, right);

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

    private sealed record BoundAssignment(TableColumn Column, SqlExpression Value, bool UsesDefault);

    private sealed record BoundColumnDefault(TableColumn Column, SqlExpression Expression);

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
        /// <summary>创建直接读取关系表列的投影。</summary>
        public static Projection ForColumn(TableColumn column, string columnName)
            => new(ProjectionKind.Column, columnName, column, null);

        /// <summary>创建在每一结果行返回固定值的投影。</summary>
        public static Projection Constant(object? value, string columnName)
            => new(ProjectionKind.Constant, columnName, null, value);

        /// <summary>创建在每一关系表行上动态求值的标量表达式投影。</summary>
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
