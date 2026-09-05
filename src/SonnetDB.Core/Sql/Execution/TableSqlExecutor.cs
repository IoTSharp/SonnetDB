using System.Globalization;
using System.Numerics;
using System.Text;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
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
    private const int MaxIndexUnionBranches = 32;
    private const int MaxIndexUnionCandidates = 65_536;
    // 范围等值组只需要保存列值；主键已经包含在 Values 中，无需在 spill 文件中重复一份编码键。
    private static readonly SqlSpillCodec<TableRow> _tableRowSpillCodec = new(
        static row => row.Values as object?[] ?? row.Values.ToArray(),
        static values => new TableRow(values),
        static row => SqlSpillRowCodec.EstimateRowBytes(row.Values));
    private static readonly IReadOnlyList<string> _nameColumns =
        new List<string>(1) { "name" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _describeTableColumns =
        new List<string>(7)
        {
            "column_name", "data_type", "is_nullable", "is_primary_key", "ordinal", "column_default", "is_auto_increment"
        }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showIndexColumns =
        new List<string>(4) { "index_name", "is_unique", "columns", "created_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _analyzeColumns =
        new List<string>(10)
        {
            "table_name", "row_count", "logical_page_count", "average_row_width",
            "sampled_rows", "sample_rate", "is_complete", "refreshed_utc", "source_sequence", "index_count"
        }.AsReadOnly();

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

        IEnumerable<IReadOnlyList<object?>> ProjectMatchingRows()
        {
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
                yield return output;
            }
        }

        var columns = projections.Select(static projection => projection.ColumnName)
            .Concat(hiddenOrderColumns.Select(static column => column.Name))
            .ToArray();
        var projected = statement.Distinct
            ? DistinctRows(ProjectMatchingRows())
            : ProjectMatchingRows();
        var ordered = rangeOrderSatisfied
            ? ApplyPagination(columns, projected, statement.Pagination)
            : statement.OrderByList.Count == 0
                ? ApplyPagination(columns, projected, statement.Pagination)
                : ApplyOrderByAndPagination(columns, projected, statement.OrderByList, statement.Pagination);
        return hiddenOrderColumns.Length == 0
            ? ordered
            : RemoveHiddenOrderColumns(ordered, projections.Length);
    }

    /// <summary>
    /// 判断单表 DISTINCT 是否能在表扫描阶段安全完成去重并下推分页。
    /// </summary>
    internal static bool CanStreamDistinct(SelectStatement statement, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);
        if (!statement.Distinct
            || statement.FromSubquery is not null
            || statement.JoinClauses.Count != 0
            || statement.GroupBy.Count != 0
            || statement.Having is not null)
        {
            return false;
        }

        var projections = BuildProjections(statement.Projections, schema);
        return ResolveHiddenOrderColumns(projections, statement.OrderByList, schema).Length == 0;
    }

    /// <summary>
    /// 按逐列 SQL 相等语义去重惰性投影行，首个出现的行保留其输入顺序。
    /// </summary>
    private static IEnumerable<IReadOnlyList<object?>> DistinctRows(
        IEnumerable<IReadOnlyList<object?>> rows)
        => SqlBlockingOperators.DistinctRows(rows, SqlExecutor.DistinctRowComparer.Instance);

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
        RecordMutationCandidateRows(store, schema, where, candidateRows.Count);
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
        MutationAliasValidator.Validate(statement);
        var store = tsdb.Tables.Open(schema.Name);
        var assignments = BindAssignments(statement, schema);
        // IN 子查询可能间接执行用户代码，必须在表管理锁之外完成物化；赋值表达式仍在锁内按最新行求值。
        var where = TableInSubqueryExecutor.Materialize(
            tsdb,
            statement.Where,
            schema,
            statement.TableAlias ?? schema.Name);

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
        RecordMutationCandidateRows(store, schema, where, candidateRows.Count);
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
        MutationAliasValidator.Validate(statement);
        ThrowIfBufferedTargetMakesSubqueryViewInconsistent(transaction, schema, statement.Where);
        var store = tsdb.Tables.Open(schema.Name);
        var assignments = BindAssignments(statement, schema);
        var where = TableInSubqueryExecutor.Materialize(
            tsdb,
            statement.Where,
            schema,
            statement.TableAlias ?? schema.Name);

        var mutations = new List<TableRowMutation>();
        var rowChanges = new List<TableRowChange>();
        IReadOnlyList<TableRow> candidateRows;
        bool predicateSatisfied;
        if (transaction.TryGetBufferedMutations(schema.Name, out var buffered))
        {
            candidateRows = TryLoadPrimaryKeyCandidateRowsWithOverlay(
                store,
                schema,
                where,
                buffered,
                out var primaryKeyRows)
                ? primaryKeyRows
                : ApplyMutationOverlay(schema, store.Scan(), buffered);
            predicateSatisfied = false;
        }
        else
        {
            candidateRows = LoadMutationCandidateRows(store, schema, where, out predicateSatisfied);
        }
        RecordMutationCandidateRows(store, schema, where, candidateRows.Count);
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
            // 完整主键条件只需合并目标键；其他条件仍扫描叠加，避免漏掉事务内新增或改键行。
            candidateRows = TryLoadPrimaryKeyCandidateRowsWithOverlay(
                store,
                schema,
                where,
                buffered,
                out var primaryKeyRows)
                ? primaryKeyRows
                : ApplyMutationOverlay(schema, store.Scan(), buffered);
            predicateSatisfied = false;
        }
        else
        {
            candidateRows = LoadMutationCandidateRows(store, schema, where, out predicateSatisfied);
        }
        RecordMutationCandidateRows(store, schema, where, candidateRows.Count);

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

    /// <summary>执行 <c>ANALYZE TABLE</c> 并返回本次统计摘要。</summary>
    /// <param name="tsdb">目标数据库。</param>
    /// <param name="statement">ANALYZE 语句。</param>
    /// <returns>统计快照的摘要行。</returns>
    public static SelectExecutionResult ExecuteAnalyze(Tsdb tsdb, AnalyzeTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        TableStore store = tsdb.Tables.Open(statement.TableName);
        TableStatistics statistics = store.RefreshStatistics(
            cancellationToken: SqlQueryResources.Current?.CancellationToken ?? default);
        return new SelectExecutionResult(
            _analyzeColumns,
            [new object?[]
            {
                statement.TableName,
                statistics.RowCount,
                statistics.LogicalPageCount,
                statistics.AverageRowWidth,
                statistics.SampledRows,
                statistics.SampleRate,
                statistics.IsComplete,
                statistics.RefreshedAtUtc,
                statistics.SourceSequence,
                statistics.Indexes.Count,
            }]);
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
        if (TryExtractPrimaryKeyValues(
            schema,
            where,
            allowExtraPredicates: true,
            out var keyValues,
            allowNonEqualityExtraPredicates: true))
        {
            var row = store.GetByPrimaryKey(keyValues);
            return row is null ? Array.Empty<TableRow>() : [row];
        }

        // 现场批量合成查询通常是单列主键 IN；批量点读可避免读取包含图片的大量无关行。
        if (TryLoadInCandidateRows(store, schema, where, out var inRows))
            return inRows;

        if (ChooseBestIndexAccessPlan(store, schema, where) is { } plan)
        {
            if (plan.Range is not null)
                return store.GetByIndexRange(plan.Index, plan.EqualityPrefixValues, plan.Range);

            return plan.IsFullEquality
                ? store.GetByIndex(plan.Index, plan.EqualityPrefixValues)
                : store.GetByIndexPrefix(plan.Index, plan.EqualityPrefixValues);
        }

        if (TryChooseIndexUnionPlan(schema, where, out var unionPlan, out _)
            && TryLoadIndexUnionRows(store, schema, unionPlan, out var unionRows, out _))
        {
            return unionRows;
        }

        return store.Scan();
    }

    /// <summary>按安全的主键或复合索引正向 IN 形状加载候选行，失败时返回 false 交给既有规划器。</summary>
    private static bool TryLoadInCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression? where,
        out IReadOnlyList<TableRow> rows)
    {
        rows = Array.Empty<TableRow>();
        if (!TryChooseInAccessPlan(schema, where, out var plan))
            return false;

        rows = plan.UsesPrimaryKey
            ? store.GetByEncodedPrimaryKeys(plan.LookupKeys)
            : store.GetByEncodedIndexPrefixes(
                plan.Index!,
                plan.LookupKeys,
                IsUniqueInPointLookup(plan));
        return true;
    }

    /// <summary>识别单列主键，或复合索引“连续等值前缀 + 下一列 IN”，并完成键值转换。</summary>
    internal static bool TryChooseInAccessPlan(
        TableSchema schema,
        SqlExpression? where,
        out TableInAccessPlan plan)
        => TryChooseInAccessPlan(schema, where, equalityCollectionTestHook: null, out plan);

    /// <summary>识别 IN 批量访问计划，并允许测试观察复合索引等值前缀的收集动作。</summary>
    /// <param name="schema">目标表结构。</param>
    /// <param name="where">待规划的 WHERE 谓词。</param>
    /// <param name="equalityCollectionTestHook">开始收集等值前缀时触发的测试钩子；生产调用传空。</param>
    /// <param name="plan">成功选择的 IN 批量访问计划。</param>
    internal static bool TryChooseInAccessPlan(
        TableSchema schema,
        SqlExpression? where,
        Action? equalityCollectionTestHook,
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
        IReadOnlyList<object?> equalityPrefixValues = [];
        bool usesPrimaryKey = schema.PrimaryKey.Count == 1
            && string.Equals(schema.PrimaryKey[0], column.Name, StringComparison.Ordinal);
        if (!usesPrimaryKey)
        {
            int bestMatchedColumns = 0;
            bool hasPrefixedIndexCandidate = false;

            // 单列及 IN 位于首列的索引无需解析其他谓词，先走零前缀选择以保留低分配快路。
            foreach (TableIndex candidate in schema.Indexes)
            {
                if (!string.IsNullOrWhiteSpace(candidate.JsonPath))
                    continue;

                int inColumnOrdinal = FindIndexColumnOrdinal(candidate, column.Name);
                if (inColumnOrdinal < 0)
                    continue;
                if (inColumnOrdinal > 0)
                {
                    hasPrefixedIndexCandidate = true;
                    continue;
                }

                const int matchedColumns = 1;
                if (candidate.IsUnique
                    && matchedColumns < candidate.Columns.Count
                    && HasNullableUnmatchedIndexColumn(schema, candidate, matchedColumns))
                {
                    continue;
                }

                if (IsPreferredInIndexCandidate(candidate, matchedColumns, index, bestMatchedColumns))
                {
                    index = candidate;
                    bestMatchedColumns = matchedColumns;
                }
            }

            if (hasPrefixedIndexCandidate)
            {
                equalityCollectionTestHook?.Invoke();
                if (!TryCollectEqualityExpressions(where, allowNonEquality: true, out var equalityByColumn))
                    equalityByColumn.Clear();

                foreach (TableIndex candidate in schema.Indexes)
                {
                    if (!string.IsNullOrWhiteSpace(candidate.JsonPath))
                        continue;

                    int inColumnOrdinal = FindIndexColumnOrdinal(candidate, column.Name);
                    if (inColumnOrdinal <= 0)
                        continue;

                    var prefix = new List<object?>(inColumnOrdinal);
                    bool prefixBound = true;
                    for (int ordinal = 0; ordinal < inColumnOrdinal; ordinal++)
                    {
                        string prefixColumnName = candidate.Columns[ordinal];
                        if (!equalityByColumn.TryGetValue(prefixColumnName, out SqlExpression? expression))
                        {
                            prefixBound = false;
                            break;
                        }

                        TableColumn prefixColumn = schema.TryGetColumn(prefixColumnName)
                            ?? throw new InvalidOperationException(
                                $"索引 '{candidate.Name}' 引用了未知列 '{prefixColumnName}'。");
                        if (!CanUseIndexEqualityLookup(prefixColumn, expression))
                        {
                            prefixBound = false;
                            break;
                        }
                        try
                        {
                            prefix.Add(ConvertTableValue(expression, prefixColumn));
                        }
                        catch (Exception exception) when (exception is InvalidOperationException
                            or ArgumentOutOfRangeException
                            or InvalidCastException
                            or FormatException
                            or OverflowException)
                        {
                            prefixBound = false;
                            break;
                        }
                    }
                    if (!prefixBound)
                        continue;

                    int matchedColumns = inColumnOrdinal + 1;
                    if (candidate.IsUnique
                        && matchedColumns < candidate.Columns.Count
                        && HasNullableUnmatchedIndexColumn(schema, candidate, matchedColumns))
                    {
                        continue;
                    }

                    if (IsPreferredInIndexCandidate(candidate, matchedColumns, index, bestMatchedColumns))
                    {
                        index = candidate;
                        bestMatchedColumns = matchedColumns;
                        equalityPrefixValues = prefix;
                    }
                }
            }

            if (index is null)
                return false;
        }

        var lookupKeys = new List<byte[]>(inExpression.Values.Count);
        var seen = new HashSet<byte[]>(inExpression.Values.Count, KvKeyComparer.Instance);
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

            byte[] encoded = EncodeInLookupKey(
                schema,
                column,
                index,
                usesPrimaryKey,
                equalityPrefixValues,
                value);
            if (seen.Add(encoded))
                lookupKeys.Add(encoded);
        }

        // 规划阶段产生的物理键会直接交给单快照 MultiGet，避免执行阶段按每个 IN 值再次分配和编码。
        plan = new TableInAccessPlan(index, usesPrimaryKey, equalityPrefixValues, lookupKeys);
        return true;
    }

    /// <summary>返回目标列在索引中的序号；索引不包含该列时返回 -1。</summary>
    private static int FindIndexColumnOrdinal(TableIndex index, string columnName)
    {
        for (int ordinal = 0; ordinal < index.Columns.Count; ordinal++)
        {
            if (string.Equals(index.Columns[ordinal], columnName, StringComparison.Ordinal))
                return ordinal;
        }

        return -1;
    }

    /// <summary>按唯一点读、匹配列数和完整前缀的既有顺序比较 IN 索引候选。</summary>
    private static bool IsPreferredInIndexCandidate(
        TableIndex candidate,
        int candidateMatchedColumns,
        TableIndex? current,
        int currentMatchedColumns)
    {
        if (current is null)
            return true;

        bool candidateIsUniquePoint = candidate.IsUnique
            && candidateMatchedColumns == candidate.Columns.Count;
        bool currentIsUniquePoint = current.IsUnique
            && currentMatchedColumns == current.Columns.Count;
        if (candidateIsUniquePoint != currentIsUniquePoint)
            return candidateIsUniquePoint;
        if (candidateMatchedColumns != currentMatchedColumns)
            return candidateMatchedColumns > currentMatchedColumns;

        bool candidateIsFullPrefix = candidateMatchedColumns == candidate.Columns.Count;
        bool currentIsFullPrefix = currentMatchedColumns == current.Columns.Count;
        return candidateIsFullPrefix && !currentIsFullPrefix;
    }

    /// <summary>为主键或二级索引点查生成稳定去重键。</summary>
    private static byte[] EncodeInLookupKey(
        TableSchema schema,
        TableColumn column,
        TableIndex? index,
        bool usesPrimaryKey,
        IReadOnlyList<object?> equalityPrefixValues,
        object value)
    {
        if (usesPrimaryKey)
            return TableKeyCodec.EncodePrimaryKeyValues(schema, [value]);

        object?[] lookupValues = BuildInLookupValues(equalityPrefixValues, value);
        return TableIndexCodec.EncodeLookupPrefix(index!, lookupValues, schema)!;
    }

    /// <summary>把已绑定的索引等值前缀和一个 IN 值组合为连续物理查找键。</summary>
    private static object?[] BuildInLookupValues(
        IReadOnlyList<object?> equalityPrefixValues,
        object value)
    {
        var lookupValues = new object?[equalityPrefixValues.Count + 1];
        for (int prefixIndex = 0; prefixIndex < equalityPrefixValues.Count; prefixIndex++)
            lookupValues[prefixIndex] = equalityPrefixValues[prefixIndex];
        lookupValues[^1] = value;
        return lookupValues;
    }

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

    /// <summary>把关系 UPDATE/DELETE 的实际候选访问路径归属到当前 SQL 指标。</summary>
    private static void RecordMutationCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression where,
        int candidateRows)
    {
        if (!SqlExecutionTelemetry.IsEnabled)
            return;

        TableExistsAccessPlan plan = PlanExistsAccess(store, schema, where);
        SqlExecutionTelemetry.RecordAccessPath(plan.AccessPath, plan.IndexName, plan.FallbackReason);
        SqlExecutionTelemetry.RecordCandidateRows(candidateRows);
        SqlExecutionTelemetry.RecordExaminedRows(candidateRows);
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
    /// 无活动事务、事务已结束或该表无缓冲写时走既有 PK/二级索引/scan 快路径。完整主键等值条件
    /// 只合并目标键的已提交行与事务 mutation；其他条件仍全表 scan 后叠加缓冲变更，避免漏掉尚未
    /// 提交的插入行或返回被缓冲更新覆盖前的旧值。事务 UPDATE/DELETE 也复用该语义。
    /// </summary>
    internal static IReadOnlyList<TableRow> LoadSelectCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
    {
        var plan = PlanExistsAccess(store, schema, where);
        var transaction = SqlTransactionContext.Current;
        IReadOnlyList<TableRow> rows;
        if (transaction is not null && transaction.TryGetBufferedMutations(schema.Name, out var buffered))
            rows = TryLoadPrimaryKeyCandidateRowsWithOverlay(
                store,
                schema,
                where,
                buffered,
                out var primaryKeyRows)
                ? primaryKeyRows
                : ApplyMutationOverlay(schema, store.Scan(), buffered);
        else
            rows = LoadCandidateRows(store, schema, where);

        SqlExecutionTelemetry.RecordAccessPath(plan.AccessPath, plan.IndexName, plan.FallbackReason);
        SqlExecutionTelemetry.RecordCandidateRows(rows.Count);
        SqlExecutionTelemetry.RecordExaminedRows(rows.Count);
        return rows;
    }

    /// <summary>
    /// 惰性读取 SELECT 候选；scan 和单索引路径共享一个稳定 Table 快照并逐页消费。
    /// 事务 overlay、IN 与 index-union 是显式有界/阻塞回退，保持既有 read-your-writes 和去重顺序。
    /// </summary>
    internal static IEnumerable<TableRow> EnumerateSelectCandidateRows(
        TableStore store,
        TableSchema schema,
        SqlExpression? where,
        IReadOnlySet<string>? requiredColumns = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schema);
        TableExistsAccessPlan access = PlanExistsAccess(store, schema, where);

        IEnumerable<TableRow> candidates;
        string actualAccessPath = access.AccessPath;
        if (SqlTransactionContext.Current is { } transaction
            && transaction.TryGetBufferedMutations(schema.Name, out var buffered))
        {
            candidates = TryLoadPrimaryKeyCandidateRowsWithOverlay(
                store,
                schema,
                where,
                buffered,
                out var primaryKeyRows)
                ? primaryKeyRows
                : ApplyMutationOverlay(schema, store.Scan(), buffered);
        }
        else if (TryExtractPrimaryKeyValues(
            schema,
            where,
            allowExtraPredicates: true,
            out var keyValues,
            allowNonEqualityExtraPredicates: true))
        {
            TableRow? row = store.GetByPrimaryKey(keyValues);
            candidates = row is null ? [] : [row];
        }
        else if (TryLoadInCandidateRows(store, schema, where, out var inRows))
        {
            candidates = inRows;
        }
        else if (ChooseBestIndexAccessPlan(store, schema, where) is { } indexPlan)
        {
            if (CanUseCoveringIndexOnly(schema, where, indexPlan, requiredColumns))
            {
                candidates = store.EnumerateCoveredIndexEquality(
                    indexPlan.Index,
                    indexPlan.EqualityPrefixValues);
                actualAccessPath = "secondary_index_only";
            }
            else
            {
                candidates = indexPlan.Range is not null
                    ? store.EnumerateByIndexRange(
                        indexPlan.Index,
                        indexPlan.EqualityPrefixValues,
                        indexPlan.Range)
                    : store.EnumerateByIndexPrefix(
                        indexPlan.Index,
                        indexPlan.EqualityPrefixValues);
            }
        }
        else if (TryChooseIndexUnionPlan(schema, where, out var unionPlan, out _)
            && TryLoadIndexUnionRows(store, schema, unionPlan, out var unionRows, out _))
        {
            candidates = unionRows;
        }
        else
        {
            candidates = store.EnumerateScan();
        }

        SqlExecutionTelemetry.RecordAccessPath(actualAccessPath, access.IndexName, access.FallbackReason);
        return CountCandidates(candidates);

        static IEnumerable<TableRow> CountCandidates(IEnumerable<TableRow> rows)
        {
            int count = 0;
            try
            {
                foreach (TableRow row in rows)
                {
                    count++;
                    yield return row;
                }
            }
            finally
            {
                SqlExecutionTelemetry.RecordCandidateRows(count);
                SqlExecutionTelemetry.RecordExaminedRows(count);
            }
        }
    }

    private static bool CanUseCoveringIndexOnly(
        TableSchema schema,
        SqlExpression? where,
        TableIndexAccessPlan plan,
        IReadOnlySet<string>? requiredColumns)
        => requiredColumns is not null
            && string.IsNullOrWhiteSpace(plan.Index.JsonPath)
            && plan.IsFullEquality
            && IsWhereFullyCoveredByIndexPlan(where, schema, plan)
            && requiredColumns.All(column =>
                plan.Index.Columns.Contains(column, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 为单表 EXISTS 生成与实际候选读取共用的访问计划。
    /// </summary>
    /// <param name="schema">目标关系表结构。</param>
    /// <param name="where">已完成参数和相关外层值绑定的谓词。</param>
    /// <returns>主键、二级索引或全表扫描计划。</returns>
    internal static TableExistsAccessPlan PlanExistsAccess(TableSchema schema, SqlExpression? where)
        => PlanExistsAccess(
            store: null,
            schema,
            where,
            allowIndexUnion: true,
            allowAutomaticStatisticsRefresh: false);

    /// <summary>生成带统计成本选择的 EXISTS 访问计划。</summary>
    internal static TableExistsAccessPlan PlanExistsAccess(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
        => PlanExistsAccess(
            store,
            schema,
            where,
            allowIndexUnion: true,
            allowAutomaticStatisticsRefresh: true);

    /// <summary>生成 EXPLAIN 使用的只读 EXISTS 访问计划，不触发统计采样。</summary>
    internal static TableExistsAccessPlan PlanExistsAccessForExplain(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
        => PlanExistsAccess(
            store,
            schema,
            where,
            allowIndexUnion: true,
            allowAutomaticStatisticsRefresh: false);

    /// <summary>生成单表候选访问计划；OR 分支规划时关闭递归索引并集。</summary>
    private static TableExistsAccessPlan PlanExistsAccess(
        TableStore? store,
        TableSchema schema,
        SqlExpression? where,
        bool allowIndexUnion,
        bool allowAutomaticStatisticsRefresh)
    {
        ArgumentNullException.ThrowIfNull(schema);

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

        if (SqlTransactionContext.Current is { } transaction
            && transaction.TryGetBufferedMutations(schema.Name, out _))
        {
            return CreateTransactionOverlayExistsPlan(where);
        }

        if (TryChooseInAccessPlan(schema, where, out var inPlan))
        {
            bool predicateCovered = IsWhereFullyCoveredByInPlan(where, schema, inPlan);
            return new TableExistsAccessPlan(
                AccessPath: FormatInAccessPath(inPlan),
                IndexName: inPlan.UsesPrimaryKey ? "primary" : inPlan.Index!.Name,
                UsesPrimaryKey: false,
                IndexPlan: null,
                PredicateCovered: predicateCovered,
                HasResidualPredicate: !predicateCovered,
                InPlan: inPlan);
        }

        TableAccessCostEstimate? costEstimate = store is null
            ? null
            : TableCostPlanner.Estimate(
                store,
                schema,
                where,
                allowAutomaticRefresh: allowAutomaticStatisticsRefresh);
        TableIndexAccessPlan? indexPlan = costEstimate?.IndexPlan
            ?? (store is null ? ChooseBestIndexAccessPlan(schema, where) : null);
        if (indexPlan is not null)
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

        if (allowIndexUnion)
        {
            if (TryChooseIndexUnionPlan(schema, where, out var unionPlan, out var unionFallback))
            {
                return new TableExistsAccessPlan(
                    AccessPath: "index_union",
                    IndexName: null,
                    UsesPrimaryKey: false,
                    IndexPlan: null,
                    PredicateCovered: false,
                    HasResidualPredicate: where is not null,
                    UnionPlan: unionPlan);
            }

            if (unionFallback is not null)
            {
                return new TableExistsAccessPlan(
                    AccessPath: "table_scan",
                    IndexName: null,
                    UsesPrimaryKey: false,
                    IndexPlan: null,
                    PredicateCovered: false,
                    HasResidualPredicate: where is not null,
                    FallbackReason: unionFallback);
            }
        }

        return new TableExistsAccessPlan(
            AccessPath: "table_scan",
            IndexName: null,
            UsesPrimaryKey: false,
            IndexPlan: null,
            PredicateCovered: where is null,
            HasResidualPredicate: where is not null,
            FallbackReason: where is null
                ? null
                : costEstimate?.FallbackReason ?? "no_sargable_predicate");
    }

    /// <summary>
    /// 为一个顶层 OR 合取项构造有界索引并集；全部有效分支都必须能使用主键、IN 或二级索引。
    /// </summary>
    internal static bool TryChooseIndexUnionPlan(
        TableSchema schema,
        SqlExpression? where,
        out TableIndexUnionAccessPlan plan,
        out string? fallbackReason)
    {
        ArgumentNullException.ThrowIfNull(schema);
        plan = null!;
        fallbackReason = null;
        if (where is null)
            return false;

        if (SqlTransactionContext.Current is { } transaction
            && transaction.TryGetBufferedMutations(schema.Name, out _))
        {
            fallbackReason = "transaction_overlay_requires_scan";
            return false;
        }

        SqlExpression? disjunction = null;
        foreach (var conjunct in FlattenAnd(where))
        {
            if (conjunct is not BinaryExpression { Operator: SqlBinaryOperator.Or })
                continue;
            if (disjunction is not null)
            {
                fallbackReason = "multiple_index_unions_not_supported";
                return false;
            }
            disjunction = conjunct;
        }

        if (disjunction is null)
            return false;

        var predicates = new List<SqlExpression>(MaxIndexUnionBranches);
        if (!TryCollectOrPredicates(disjunction, predicates))
        {
            fallbackReason = "index_union_branch_limit_exceeded";
            return false;
        }

        foreach (var predicate in predicates)
        {
            if (TryEvaluateConstantPredicate(predicate, schema, out bool? constant)
                && constant == true)
            {
                fallbackReason = "index_union_branch_matches_all";
                return false;
            }
        }

        var branches = new List<TableIndexUnionBranch>(predicates.Count);
        foreach (var predicate in predicates)
        {
            if (TryEvaluateConstantPredicate(predicate, schema, out bool? constant))
            {
                // FALSE 与 UNKNOWN 在 WHERE 中都不会选中行，不需要访问分支。
                continue;
            }

            TableExistsAccessPlan access = PlanExistsAccess(
                store: null,
                schema,
                predicate,
                allowIndexUnion: false,
                allowAutomaticStatisticsRefresh: false);
            if (string.Equals(access.AccessPath, "table_scan", StringComparison.Ordinal))
            {
                string? branchFallback = access.FallbackReason;
                if (!TryChooseIsNullIndexAccessPlan(schema, predicate, out access))
                {
                    fallbackReason = branchFallback == "transaction_overlay_requires_scan"
                        ? branchFallback
                        : "index_union_unindexed_branch";
                    return false;
                }
            }

            branches.Add(new TableIndexUnionBranch(predicate, access));
        }

        plan = new TableIndexUnionAccessPlan(branches);
        return true;
    }

    /// <summary>为单列 IS NULL 分支选择保存 NULL 键的非唯一普通二级索引。</summary>
    private static bool TryChooseIsNullIndexAccessPlan(
        TableSchema schema,
        SqlExpression predicate,
        out TableExistsAccessPlan access)
    {
        access = null!;
        if (predicate is not IsNullExpression
            {
                Negated: false,
                Operand: IdentifierExpression identifier,
            })
        {
            return false;
        }

        TableIndex? index = null;
        foreach (var candidate in schema.Indexes)
        {
            if (!candidate.IsUnique
                && string.IsNullOrWhiteSpace(candidate.JsonPath)
                && candidate.Columns.Count > 0
                && string.Equals(candidate.Columns[0], identifier.Name, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }
        if (index is null)
            return false;

        var indexPlan = new TableIndexAccessPlan(index, [null], Range: null);
        access = new TableExistsAccessPlan(
            AccessPath: indexPlan.IsFullEquality ? "secondary_index" : "secondary_index_prefix",
            IndexName: index.Name,
            UsesPrimaryKey: false,
            IndexPlan: indexPlan,
            PredicateCovered: indexPlan.IsFullEquality,
            HasResidualPredicate: !indexPlan.IsFullEquality);
        return true;
    }

    /// <summary>加载并按主键去重 OR 分支候选；超过固定阈值时放弃已加载集合并要求全扫回退。</summary>
    internal static bool TryLoadIndexUnionRows(
        TableStore store,
        TableSchema schema,
        TableIndexUnionAccessPlan plan,
        out IReadOnlyList<TableRow> rows,
        out string? fallbackReason,
        int candidateLimit = MaxIndexUnionCandidates)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateLimit);
        fallbackReason = null;

        int initialCapacity = Math.Min(256, Math.Min(candidateLimit, store.RowCount));
        SqlQueryResources? resources = SqlQueryResources.Current;
        using var reservation = resources?.CreateReservation();
        var seen = new HashSet<byte[]>(initialCapacity, KvKeyComparer.Instance);
        SqlSpillableRowSet? diskSeen = null;
        var result = new List<TableRow>(initialCapacity);
        if (plan.Branches.Count == 0)
        {
            rows = result;
            return true;
        }

        using var snapshot = store.AcquireReadSnapshot();
        foreach (var branch in plan.Branches)
        {
            foreach (var candidate in LoadIndexUnionBranchRows(store, snapshot, schema, branch))
            {
                resources?.ThrowIfCancellationRequested();
                byte[] primaryKey = candidate.PrimaryKey.IsEmpty
                    ? TableKeyCodec.EncodePrimaryKey(schema, candidate.Values)
                    : candidate.PrimaryKey.ToArray();
                if (diskSeen is null)
                {
                    if (seen.Contains(primaryKey))
                        continue;
                    long bytes = checked(primaryKey.Length + 48L);
                    if (resources is null || reservation!.TryReserve(bytes))
                    {
                        _ = seen.Add(primaryKey);
                    }
                    else
                    {
                        diskSeen = new SqlSpillableRowSet(
                            resources.GetWorkspace(),
                            SqlExecutor.DistinctRowComparer.Instance);
                        foreach (byte[] existing in seen)
                            _ = diskSeen.Add([existing]);
                        seen.Clear();
                        reservation.ReleaseAll();
                        if (!diskSeen.Add([primaryKey]))
                            continue;
                    }
                }
                else if (!diskSeen.Add([primaryKey]))
                {
                    continue;
                }
                if (result.Count == candidateLimit)
                {
                    rows = [];
                    fallbackReason = "index_union_candidate_limit_exceeded";
                    return false;
                }
                result.Add(candidate);
            }
        }

        result.Sort(static (left, right) => left.PrimaryKey.Span.SequenceCompareTo(right.PrimaryKey.Span));
        diskSeen?.Dispose();
        rows = result;
        return true;
    }

    /// <summary>按单个 OR 分支的已选访问计划读取有界候选。</summary>
    private static IEnumerable<TableRow> LoadIndexUnionBranchRows(
        TableStore store,
        KvReadSnapshot snapshot,
        TableSchema schema,
        TableIndexUnionBranch branch)
    {
        TableExistsAccessPlan access = branch.AccessPlan;
        if (access.UsesPrimaryKey)
        {
            _ = TryExtractPrimaryKeyValues(
                schema,
                branch.Predicate,
                allowExtraPredicates: true,
                out var keyValues,
                allowNonEqualityExtraPredicates: true);
            if (store.GetByPrimaryKey(snapshot, schema, keyValues) is { } row)
                yield return row;
            yield break;
        }

        if (access.InPlan is { } inPlan)
        {
            bool uniquePointLookup = IsUniqueInPointLookup(inPlan);
            foreach (byte[] lookupKey in inPlan.LookupKeys)
            {
                if (inPlan.UsesPrimaryKey)
                {
                    if (store.GetByEncodedPrimaryKey(snapshot, schema, lookupKey) is { } primary)
                        yield return primary;
                    continue;
                }

                foreach (var row in store.EnumerateByEncodedIndexPrefix(
                    snapshot,
                    schema,
                    inPlan.Index!,
                    lookupKey,
                    uniquePointLookup))
                    yield return row;
            }
            yield break;
        }

        if (access.IndexPlan is not { } indexPlan)
            yield break;

        IEnumerable<TableRow> candidates = indexPlan.Range is not null
            ? store.EnumerateByIndexRange(
                snapshot,
                schema,
                indexPlan.Index,
                indexPlan.EqualityPrefixValues,
                indexPlan.Range)
            : store.EnumerateByIndexPrefix(
                snapshot,
                schema,
                indexPlan.Index,
                indexPlan.EqualityPrefixValues);
        foreach (var row in candidates)
            yield return row;
    }

    /// <summary>尝试计算不依赖行值的布尔分支，供 nullable 参数 OR 消去恒假/UNKNOWN 分支。</summary>
    private static bool TryEvaluateConstantPredicate(
        SqlExpression expression,
        TableSchema schema,
        out bool? value)
    {
        value = null;
        if (!IsConstantPredicateExpression(expression))
            return false;

        try
        {
            value = EvaluateKleene(expression, schema, Array.Empty<object?>());
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

    /// <summary>判断表达式是否只由字面量、物化值和基础逻辑/比较节点组成。</summary>
    private static bool IsConstantPredicateExpression(SqlExpression expression)
        => expression switch
        {
            LiteralExpression or DurationLiteralExpression or MaterializedSubqueryValueExpression => true,
            UnaryExpression unary => IsConstantPredicateExpression(unary.Operand),
            BinaryExpression binary => IsConstantPredicateExpression(binary.Left)
                && IsConstantPredicateExpression(binary.Right),
            IsNullExpression isNull => IsConstantPredicateExpression(isNull.Operand),
            InExpression { Subquery: null } inExpression =>
                IsConstantPredicateExpression(inExpression.Value)
                && AreAllConstantPredicateExpressions(inExpression.Values),
            _ => false,
        };

    /// <summary>判断表达式集合是否全部不依赖行值。</summary>
    private static bool AreAllConstantPredicateExpressions(IReadOnlyList<SqlExpression> expressions)
    {
        foreach (var expression in expressions)
        {
            if (!IsConstantPredicateExpression(expression))
                return false;
        }

        return true;
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

        var transaction = SqlTransactionContext.Current;
        var plan = PlanExistsAccess(store, schema, where);
        if (transaction is not null && transaction.TryGetBufferedMutations(schema.Name, out var buffered))
        {
            if (TryLoadPrimaryKeyCandidateRowsWithOverlay(
                store,
                schema,
                where,
                buffered,
                out var primaryKeyRows))
            {
                return new TableExistsCandidateRows(plan, primaryKeyRows);
            }

            var overlayRows = ApplyMutationOverlay(schema, store.Scan(), buffered);
            return new TableExistsCandidateRows(plan, overlayRows);
        }

        if (plan.UnionPlan is { } unionPlan)
        {
            if (TryLoadIndexUnionRows(store, schema, unionPlan, out var unionRows, out var unionFallback))
                return new TableExistsCandidateRows(plan, unionRows);

            var fallbackPlan = plan with
            {
                AccessPath = "table_scan",
                IndexName = null,
                PredicateCovered = false,
                HasResidualPredicate = where is not null,
                FallbackReason = unionFallback,
                UnionPlan = null,
            };
            return new TableExistsCandidateRows(fallbackPlan, store.Scan());
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

        int? candidateLimit = plan.PredicateCovered ? 1 : null;
        if (plan.InPlan is { } inPlan)
        {
            IReadOnlyList<TableRow> rows = inPlan.UsesPrimaryKey
                ? store.GetByEncodedPrimaryKeys(inPlan.LookupKeys, candidateLimit)
                : store.GetByEncodedIndexPrefixes(
                    inPlan.Index!,
                    inPlan.LookupKeys,
                    IsUniqueInPointLookup(inPlan),
                    candidateLimit);
            return new TableExistsCandidateRows(plan, rows);
        }

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
    /// 为普通关系 SELECT 加载候选行；仅在索引顺序满足 ORDER BY 且 WHERE 无残余时安全下推分页候选上限。
    /// </summary>
    private static (IEnumerable<TableRow> Rows, bool RangeOrderSatisfied) LoadSelectCandidateRowsForStatement(
        TableStore store,
        TableSchema schema,
        SelectStatement statement,
        IReadOnlyList<Projection> projections)
    {
        var transaction = SqlTransactionContext.Current;
        if (transaction is not null && transaction.TryGetBufferedMutations(schema.Name, out var buffered))
        {
            if (TryLoadPrimaryKeyCandidateRowsWithOverlay(
                store,
                schema,
                statement.Where,
                buffered,
                out var primaryKeyRows))
            {
                return (
                    ObserveCandidateRows(
                        primaryKeyRows,
                        "primary_key",
                        "primary",
                        fallbackReason: null),
                    false);
            }

            return (
                ObserveCandidateRows(
                    ApplyMutationOverlay(schema, store.Scan(), buffered),
                    "table_scan",
                    indexName: null,
                    "transaction_overlay_requires_scan"),
                false);
        }

        if (TryExtractPrimaryKeyValues(
            schema,
            statement.Where,
            allowExtraPredicates: true,
            out var keyValues,
            allowNonEqualityExtraPredicates: true))
        {
            var row = store.GetByPrimaryKey(keyValues);
            return (
                ObserveCandidateRows(
                    row is null ? Array.Empty<TableRow>() : [row],
                    "primary_key",
                    "primary",
                    fallbackReason: null),
                false);
        }

        if (TryChooseInAccessPlan(schema, statement.Where, out var inPlan)
            && TryLoadInCandidateRows(store, schema, statement.Where, out var inRows))
        {
            return (
                ObserveCandidateRows(
                    inRows,
                    FormatInAccessPath(inPlan),
                    inPlan.UsesPrimaryKey ? "primary" : inPlan.Index!.Name,
                    fallbackReason: null),
                false);
        }

        var plan = ChooseBestIndexAccessPlan(store, schema, statement.Where);
        if (TryChooseOrderedRangeAccessPlan(
            store,
            schema,
            statement,
            projections,
            plan,
            out var orderedPlan,
            out int candidateLimit,
            out bool descending))
        {
            if (statement.OrderByList.Count > 1)
            {
                return (
                    ObserveCandidateRows(
                        store.GetByIndexRangeThroughValueGroup(
                            orderedPlan.Index,
                            orderedPlan.EqualityPrefixValues,
                            orderedPlan.Range!,
                            candidateLimit),
                        FormatIndexAccessPath(orderedPlan),
                        orderedPlan.Index.Name,
                        fallbackReason: null),
                    false);
            }

            return (
                ObserveCandidateRows(
                    store.GetByIndexRange(
                        orderedPlan.Index,
                        orderedPlan.EqualityPrefixValues,
                        orderedPlan.Range!,
                        candidateLimit,
                        descending),
                    FormatIndexAccessPath(orderedPlan),
                    orderedPlan.Index.Name,
                    fallbackReason: null),
                true);
        }

        if (TryChooseOrderedResidualRangeAccessPlan(
            store,
            schema,
            statement,
            projections,
            plan,
            out var residualOrderedPlan,
            out bool residualDescending))
        {
            // 残余谓词必须先于分页执行，因此这里只保持索引顺序惰性读取，不下推候选行数上限。
            IEnumerable<TableRow> residualRows = ObserveCandidateRows(
                store.EnumerateByIndexRange(
                    residualOrderedPlan.Index,
                    residualOrderedPlan.EqualityPrefixValues,
                    residualOrderedPlan.Range!,
                    descending: residualDescending),
                FormatIndexAccessPath(residualOrderedPlan),
                residualOrderedPlan.Index.Name,
                fallbackReason: null);
            if (statement.OrderByList.Count > 1)
            {
                // 复合索引中的字符串等长度前缀物理顺序不等同于 SQL 顺序，按范围值分组后校正后缀排序。
                residualRows = OrderRowsWithinRangeValueGroups(
                    residualRows,
                    schema,
                    residualOrderedPlan,
                    statement.OrderByList);
            }

            return (
                residualRows,
                true);
        }

        if (plan?.Range is not null)
        {
            return (
                ObserveCandidateRows(
                    store.EnumerateByIndexRange(plan.Index, plan.EqualityPrefixValues, plan.Range),
                    FormatIndexAccessPath(plan),
                    plan.Index.Name,
                    fallbackReason: null),
                false);
        }

        if (plan is { Range: null })
        {
            return (
                ObserveCandidateRows(
                    store.EnumerateByIndexPrefix(plan.Index, plan.EqualityPrefixValues),
                    FormatIndexAccessPath(plan),
                    plan.Index.Name,
                    fallbackReason: null),
                false);
        }

        string? unionFallback = null;
        if (TryChooseIndexUnionPlan(schema, statement.Where, out var unionPlan, out unionFallback))
        {
            if (TryLoadIndexUnionRows(store, schema, unionPlan, out var unionRows, out var unionLoadFallback))
            {
                return (
                    ObserveCandidateRows(
                        unionRows,
                        "index_union",
                        indexName: null,
                        fallbackReason: null),
                    false);
            }

            unionFallback = unionLoadFallback;
        }

        if (statement.Where is null)
        {
            return (
                ObserveCandidateRows(
                    store.EnumerateScan(),
                    "table_scan",
                    indexName: null,
                    fallbackReason: null),
                false);
        }

        // 没有可用访问计划时按页扫描；主键 IN / JSON 等点查已在上方返回其专用候选路径。
        return (
            ObserveCandidateRows(
                store.EnumerateScan(),
                "table_scan",
                indexName: null,
                unionFallback ?? "no_sargable_predicate"),
            false);
    }

    /// <summary>在不物化惰性候选的前提下记录实际访问路径，并在枚举结束时批量累计检查数量。</summary>
    private static IEnumerable<TableRow> ObserveCandidateRows(
        IEnumerable<TableRow> rows,
        string accessPath,
        string? indexName,
        string? fallbackReason)
    {
        SqlExecutionTelemetry.RecordAccessPath(accessPath, indexName, fallbackReason);
        long observedRows = 0;
        try
        {
            foreach (var row in rows)
            {
                observedRows++;
                yield return row;
            }
        }
        finally
        {
            // LIMIT 早停、取消和异常都会释放枚举器；finally 保证只累计实际交给上层的候选行。
            if (observedRows > 0)
                SqlExecutionTelemetry.RecordCandidateAndExaminedRows(observedRows);
        }
    }

    /// <summary>
    /// 判断范围索引能否同时满足 ORDER BY ASC 与 LIMIT/OFFSET，并计算安全的分页边界。
    /// </summary>
    internal static bool TryChooseOrderedRangeAccessPlan(
        TableSchema schema,
        SelectStatement statement,
        out TableIndexAccessPlan plan,
        out int candidateLimit,
        out bool descending)
        => TryChooseOrderedRangeAccessPlan(
            store: null,
            schema,
            statement,
            BuildProjections(statement.Projections, schema),
            ChooseBestIndexAccessPlan(schema, statement.Where),
            out plan,
            out candidateLimit,
            out descending);

    /// <summary>使用运行时成本模型选中的基础计划判断有序范围，供 EXPLAIN 与真实执行保持一致。</summary>
    internal static bool TryChooseOrderedRangeAccessPlan(
        TableStore store,
        TableSchema schema,
        SelectStatement statement,
        TableIndexAccessPlan? existingPlan,
        out TableIndexAccessPlan plan,
        out int candidateLimit,
        out bool descending)
        => TryChooseOrderedRangeAccessPlan(
            store,
            schema,
            statement,
            BuildProjections(statement.Projections, schema),
            existingPlan,
            out plan,
            out candidateLimit,
            out descending);

    /// <summary>
    /// 选择能完整满足单表排序与分页的有符号范围索引；必要时为无界有序扫描合成全范围。
    /// </summary>
    private static bool TryChooseOrderedRangeAccessPlan(
        TableStore? store,
        TableSchema schema,
        SelectStatement statement,
        IReadOnlyList<Projection> projections,
        TableIndexAccessPlan? existingPlan,
        out TableIndexAccessPlan plan,
        out int candidateLimit,
        out bool descending)
    {
        plan = null!;
        candidateLimit = 0;
        descending = false;
        if (statement.Distinct
            || statement.OrderByList.Count == 0
            || !TryGetPaginationCandidateLimit(statement.Pagination, out candidateLimit)
            || (SqlTransactionContext.Current is { } transaction
                && transaction.TryGetBufferedMutations(schema.Name, out _)))
        {
            return false;
        }

        if (statement.Where is null)
        {
            foreach (var index in schema.Indexes)
            {
                var candidate = new TableIndexAccessPlan(index, [], Range: null);
                if (!TryPrepareFullyCoveredOrderedRangeCandidate(
                    schema,
                    statement,
                    projections,
                    candidate,
                    out plan,
                    out descending))
                    continue;

                return true;
            }

            return false;
        }

        foreach (TableIndexAccessPlan candidate in EnumerateOrderAwareIndexPlans(
            store,
            schema,
            statement.Where,
            existingPlan))
        {
            if (!TryPrepareFullyCoveredOrderedRangeCandidate(
                schema,
                statement,
                projections,
                candidate,
                out plan,
                out descending))
            {
                continue;
            }

            return true;
        }

        plan = null!;
        descending = false;
        return false;
    }

    /// <summary>
    /// 把索引前缀或已有范围计划转换为可满足排序的范围计划，并确认 WHERE 已被完整覆盖。
    /// </summary>
    private static bool TryPrepareFullyCoveredOrderedRangeCandidate(
        TableSchema schema,
        SelectStatement statement,
        IReadOnlyList<Projection> projections,
        TableIndexAccessPlan candidate,
        out TableIndexAccessPlan plan,
        out bool descending)
    {
        bool synthesizedRange = candidate.Range is null;
        plan = candidate;
        descending = false;
        if (synthesizedRange && !TryCreateUnboundedOrderRange(schema, candidate, out plan))
            return false;

        if (!OrderByMatchesRangeIndexSequence(
            statement.OrderByList,
            schema,
            projections,
            plan,
            out descending))
        {
            return false;
        }

        return statement.Where is null
            ? synthesizedRange && plan.EqualityPrefixValues.Count == 0
            : IsWhereFullyCoveredByRangePlan(statement.Where, schema, plan);
    }

    /// <summary>
    /// 先返回成本模型选中的计划；替代索引只有在不扩大估算候选集时才可换取 ORDER BY 顺序。
    /// </summary>
    private static IEnumerable<TableIndexAccessPlan> EnumerateOrderAwareIndexPlans(
        TableStore? store,
        TableSchema schema,
        SqlExpression where,
        TableIndexAccessPlan? existingPlan)
    {
        if (existingPlan is not null)
            yield return existingPlan;

        // 运行时成本模型明确选择 table scan 时，不允许排序偏好重新启用已被成本淘汰的索引。
        if (store is not null && existingPlan is null)
            yield break;

        foreach (TableIndexAccessPlan candidate in CollectIndexAccessPlans(schema, where))
        {
            if (existingPlan is not null
                && (string.Equals(candidate.Index.Name, existingPlan.Index.Name, StringComparison.Ordinal)
                    || !HasEquivalentEqualityPrefix(candidate, existingPlan)
                    || !IsOrderAwareAlternativeSelectiveEnough(store, schema, candidate, existingPlan)))
            {
                continue;
            }

            yield return candidate;
        }
    }

    /// <summary>确认两个候选绑定了相同列和值的连续等值前缀，避免为排序扩大 WHERE 扫描分区。</summary>
    private static bool HasEquivalentEqualityPrefix(
        TableIndexAccessPlan candidate,
        TableIndexAccessPlan existingPlan)
    {
        if (candidate.EqualityPrefixValues.Count != existingPlan.EqualityPrefixValues.Count)
            return false;

        for (int index = 0; index < candidate.EqualityPrefixValues.Count; index++)
        {
            if (!string.Equals(
                    candidate.Index.Columns[index],
                    existingPlan.Index.Columns[index],
                    StringComparison.Ordinal)
                || !ValuesEqual(
                    candidate.EqualityPrefixValues[index],
                    existingPlan.EqualityPrefixValues[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 保守判断排序替代索引是否会扩大候选范围；范围不同且缺少新鲜统计时一律保留原计划。
    /// </summary>
    private static bool IsOrderAwareAlternativeSelectiveEnough(
        TableStore? store,
        TableSchema schema,
        TableIndexAccessPlan candidate,
        TableIndexAccessPlan existingPlan)
    {
        // 原计划只有等值前缀时，等价前缀候选覆盖同一分区，替代计划不会比它更宽。
        if (existingPlan.Range is null)
            return true;
        if (candidate.Range is null)
            return false;
        if (Equals(candidate.Range, existingPlan.Range))
            return true;
        if (store is null)
            return false;

        TableStatisticsState state = store.GetStatisticsState();
        if (state is not { IsStale: false, Statistics: { } statistics })
            return false;

        long existingRows = TableCostPlanner.EstimateIndexRows(
            store.RowCount,
            schema,
            existingPlan,
            statistics);
        long candidateRows = TableCostPlanner.EstimateIndexRows(
            store.RowCount,
            schema,
            candidate,
            statistics);
        return candidateRows <= existingRows;
    }

    /// <summary>
    /// 判断已有范围索引能否按索引顺序惰性执行 AND 残余过滤，并由上层 LIMIT/OFFSET 安全早停。
    /// </summary>
    internal static bool TryChooseOrderedResidualRangeAccessPlan(
        TableSchema schema,
        SelectStatement statement,
        out TableIndexAccessPlan plan,
        out bool descending)
        => TryChooseOrderedResidualRangeAccessPlan(
            store: null,
            schema,
            statement,
            BuildProjections(statement.Projections, schema),
            ChooseBestIndexAccessPlan(schema, statement.Where),
            out plan,
            out descending);

    /// <summary>
    /// 选择只省略阻塞排序、不提前截断候选的有序残余范围计划。
    /// </summary>
    private static bool TryChooseOrderedResidualRangeAccessPlan(
        TableStore? store,
        TableSchema schema,
        SelectStatement statement,
        IReadOnlyList<Projection> projections,
        TableIndexAccessPlan? existingPlan,
        out TableIndexAccessPlan plan,
        out bool descending)
    {
        plan = null!;
        descending = false;
        if (statement.Distinct
            || statement.Where is not BinaryExpression { Operator: SqlBinaryOperator.And } where
            || statement.OrderByList.Count == 0
            || !TryGetPaginationCandidateLimit(statement.Pagination, out _)
            || ContainsDisjunctionOrIn(where)
            || (SqlTransactionContext.Current is { } transaction
                && transaction.TryGetBufferedMutations(schema.Name, out _)))
        {
            return false;
        }

        foreach (TableIndexAccessPlan candidate in EnumerateOrderAwareIndexPlans(
            store,
            schema,
            where,
            existingPlan))
        {
            if (candidate.Range is null
                || IsWhereFullyCoveredByRangePlan(where, schema, candidate)
                || !OrderByMatchesRangeIndexSequence(
                    statement.OrderByList,
                    schema,
                    projections,
                    candidate,
                    out descending))
            {
                continue;
            }

            plan = candidate;
            return true;
        }

        plan = null!;
        descending = false;
        return false;
    }

    /// <summary>
    /// 保持范围列的流式顺序，并在查询预算内外排单个并列组，以 SQL 比较语义校正复合索引后缀顺序。
    /// </summary>
    private static IEnumerable<TableRow> OrderRowsWithinRangeValueGroups(
        IEnumerable<TableRow> rows,
        TableSchema schema,
        TableIndexAccessPlan plan,
        IReadOnlyList<OrderBySpec> orderBy)
    {
        int rangeOrdinal = plan.Range!.Column.Ordinal;
        var orderOrdinals = new int[orderBy.Count];
        int explicitStart = plan.EqualityPrefixValues.Count;
        int explicitCount = plan.Index.Columns.Count - explicitStart;
        for (int index = 0; index < orderBy.Count; index++)
        {
            string columnName = index < explicitCount
                ? plan.Index.Columns[explicitStart + index]
                : schema.PrimaryKey[index - explicitCount];
            orderOrdinals[index] = schema.TryGetColumn(columnName)?.Ordinal
                ?? throw new InvalidOperationException(
                    $"索引 '{plan.Index.Name}' 引用了未知列 '{columnName}'。");
        }

        var comparer = new TableRowIndexOrderComparer(orderOrdinals, orderBy[0].Direction);
        using IEnumerator<TableRow> enumerator = rows.GetEnumerator();
        if (!enumerator.MoveNext())
            yield break;

        TableRow groupHead = enumerator.Current;
        bool hasGroup = true;
        while (hasGroup)
        {
            object? rangeValue = groupHead.Values[rangeOrdinal];

            // 该局部迭代器只消费当前范围值，并把下一组首行留给外层循环。
            IEnumerable<TableRow> EnumerateCurrentGroup()
            {
                yield return groupHead;
                while (enumerator.MoveNext())
                {
                    TableRow row = enumerator.Current;
                    if (!ValuesEqual(rangeValue, row.Values[rangeOrdinal]))
                    {
                        groupHead = row;
                        yield break;
                    }

                    yield return row;
                }

                hasGroup = false;
            }

            IEnumerable<TableRow> orderedGroup = SqlQueryResources.Current is null
                ? OrderRangeValueGroupInMemory(EnumerateCurrentGroup(), comparer)
                : SqlSpillSorter.Order(EnumerateCurrentGroup(), comparer, _tableRowSpillCodec);
            foreach (TableRow groupedRow in orderedGroup)
                yield return groupedRow;
        }
    }

    /// <summary>没有查询资源作用域时保持兼容的组内排序；正常 SQL 根执行均使用预算感知外排路径。</summary>
    private static IEnumerable<TableRow> OrderRangeValueGroupInMemory(
        IEnumerable<TableRow> rows,
        IComparer<TableRow> comparer)
    {
        var group = rows.ToList();
        group.Sort(comparer);
        foreach (TableRow groupedRow in group)
            yield return groupedRow;
    }

    /// <summary>递归排除 OR、IN 与子查询，确保新路径只覆盖可逐行执行的 AND 残余谓词。</summary>
    private static bool ContainsDisjunctionOrIn(SqlExpression expression)
        => expression switch
        {
            BinaryExpression { Operator: SqlBinaryOperator.Or } => true,
            BinaryExpression binary => ContainsDisjunctionOrIn(binary.Left)
                || ContainsDisjunctionOrIn(binary.Right),
            InExpression => true,
            UnaryExpression unary => ContainsDisjunctionOrIn(unary.Operand),
            IsNullExpression isNull => ContainsDisjunctionOrIn(isNull.Operand),
            FunctionCallExpression function => function.Arguments.Any(ContainsDisjunctionOrIn),
            NamedArgumentExpression named => ContainsDisjunctionOrIn(named.Value),
            CaseExpression @case => @case.WhenClauses.Any(static clause =>
                    ContainsDisjunctionOrIn(clause.Condition)
                    || ContainsDisjunctionOrIn(clause.Result))
                || (@case.Else is not null && ContainsDisjunctionOrIn(@case.Else)),
            SubqueryExpression or ExistsExpression => true,
            _ => false,
        };

    /// <summary>在等值前缀后的非空 Int64/DATETIME 列上合成无界范围，供 ORDER BY cursor 使用。</summary>
    private static bool TryCreateUnboundedOrderRange(
        TableSchema schema,
        TableIndexAccessPlan prefixPlan,
        out TableIndexAccessPlan rangePlan)
    {
        rangePlan = null!;
        if (!string.IsNullOrWhiteSpace(prefixPlan.Index.JsonPath)
            || prefixPlan.EqualityPrefixValues.Count >= prefixPlan.Index.Columns.Count)
        {
            return false;
        }

        var column = schema.TryGetColumn(prefixPlan.Index.Columns[prefixPlan.EqualityPrefixValues.Count])
            ?? throw new InvalidOperationException(
                $"索引 '{prefixPlan.Index.Name}' 引用了未知列。");
        if (column.IsNullable
            || column.DataType is not (TableColumnType.Int64 or TableColumnType.DateTime))
        {
            return false;
        }

        rangePlan = prefixPlan with { Range = new TableIndexRange(column, Lower: null, Upper: null) };
        return true;
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
        TableIndexAccessPlan plan,
        out bool descending)
    {
        descending = false;
        if (plan.Range is null || orderBy.Count == 0)
            return false;

        SortDirection direction = orderBy[0].Direction;
        descending = direction == SortDirection.Descending;
        if (descending && orderBy.Count > 1)
            return false;

        int explicitStart = plan.EqualityPrefixValues.Count;
        int explicitCount = plan.Index.Columns.Count - explicitStart;
        int implicitCount = plan.Index.IsUnique ? 0 : schema.PrimaryKey.Count;
        if (orderBy.Count > explicitCount + implicitCount)
            return false;

        for (int i = 0; i < orderBy.Count; i++)
        {
            if (orderBy[i].Direction != direction
                || orderBy[i].Expression is not IdentifierExpression orderIdentifier)
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

    /// <summary>把主键或二级索引 IN 计划映射为稳定的运行时与 EXPLAIN 访问路径名称。</summary>
    internal static string FormatInAccessPath(TableInAccessPlan plan)
        => plan.UsesPrimaryKey
            ? "primary_key_in"
            : plan.EqualityPrefixValues.Count == 0
                ? "secondary_index_in"
                : "secondary_index_prefix_in";

    /// <summary>判断二级索引 IN 的每个编码前缀是否已经覆盖完整唯一键，可直接执行单次 KV 点读。</summary>
    private static bool IsUniqueInPointLookup(TableInAccessPlan plan)
        => !plan.UsesPrimaryKey
            && plan.Index is { IsUnique: true } index
            && plan.EqualityPrefixValues.Count + 1 == index.Columns.Count;

    /// <summary>
    /// 确认 WHERE 仅由当前 IN 谓词及其复合索引连续等值前缀构成，供 EXISTS 安全下推首行上限。
    /// </summary>
    private static bool IsWhereFullyCoveredByInPlan(
        SqlExpression? where,
        TableSchema schema,
        TableInAccessPlan plan)
    {
        if (where is null)
            return false;

        string inColumnName = plan.UsesPrimaryKey
            ? schema.PrimaryKey[0]
            : plan.Index!.Columns[plan.EqualityPrefixValues.Count];
        var matchedPrefix = new bool[plan.EqualityPrefixValues.Count];
        bool matchedIn = false;

        foreach (SqlExpression leaf in FlattenAnd(where))
        {
            if (leaf is InExpression
                {
                    Negated: false,
                    Subquery: null,
                    Value: IdentifierExpression inIdentifier,
                })
            {
                if (matchedIn || !string.Equals(inIdentifier.Name, inColumnName, StringComparison.Ordinal))
                    return false;
                matchedIn = true;
                continue;
            }

            if (leaf is not BinaryExpression { Operator: SqlBinaryOperator.Equal } equality)
                return false;

            var (identifier, expression) = NormalizeIdentifierComparison(equality);
            if (identifier is null || expression is null || plan.UsesPrimaryKey)
                return false;

            int prefixIndex = -1;
            for (int index = 0; index < plan.EqualityPrefixValues.Count; index++)
            {
                if (string.Equals(plan.Index!.Columns[index], identifier.Name, StringComparison.Ordinal))
                {
                    prefixIndex = index;
                    break;
                }
            }
            if (prefixIndex < 0 || matchedPrefix[prefixIndex])
                return false;

            TableColumn column = schema.TryGetColumn(identifier.Name)
                ?? throw new InvalidOperationException(
                    $"索引 '{plan.Index!.Name}' 引用了未知列 '{identifier.Name}'。");
            if (!CanUseIndexEqualityLookup(column, expression))
                return false;

            try
            {
                if (!ValuesEqual(ConvertTableValue(expression, column), plan.EqualityPrefixValues[prefixIndex]))
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

            matchedPrefix[prefixIndex] = true;
        }

        return matchedIn && matchedPrefix.All(static matched => matched);
    }

    /// <summary>
    /// 对完整主键等值条件执行事务内点查：先读取已提交目标行，再仅检查当前事务的小型 mutation 集。
    /// mutation 若修改主键，会先从旧键移除、再按新键加入，保持与全表 overlay 相同的可见性。
    /// </summary>
    private static bool TryLoadPrimaryKeyCandidateRowsWithOverlay(
        TableStore store,
        TableSchema schema,
        SqlExpression? where,
        IReadOnlyList<TableRowMutation> mutations,
        out IReadOnlyList<TableRow> rows)
    {
        rows = [];
        if (!TryExtractPrimaryKeyValues(
            schema,
            where,
            allowExtraPredicates: true,
            out var keyValues,
            allowNonEqualityExtraPredicates: true))
        {
            return false;
        }

        byte[] targetKey = TableKeyCodec.EncodePrimaryKeyValues(schema, keyValues);
        TableRow? current = store.GetByPrimaryKey(keyValues);
        foreach (var mutation in mutations)
        {
            byte[]? oldKey = mutation.PrimaryKeyValues is null
                ? null
                : TableKeyCodec.EncodePrimaryKeyValues(schema, mutation.PrimaryKeyValues);
            if (oldKey is not null && oldKey.AsSpan().SequenceEqual(targetKey))
                current = null;

            if (mutation.NewValues is null)
                continue;

            byte[] newKey = TableKeyCodec.EncodePrimaryKey(schema, mutation.NewValues);
            if (newKey.AsSpan().SequenceEqual(targetKey))
                current = new TableRow(mutation.NewValues.ToArray(), newKey);
        }

        rows = current is null ? [] : [current];
        return true;
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
        TableIndexAccessPlan? bestPlan = null;
        foreach (TableIndexAccessPlan candidatePlan in CollectIndexAccessPlans(schema, where))
        {
            if (bestPlan is null || IsHeuristicallyBetter(candidatePlan, bestPlan))
                bestPlan = candidatePlan;
        }

        return bestPlan;
    }

    /// <summary>枚举 WHERE 可表达的全部二级索引候选，不读取业务数据。</summary>
    internal static IReadOnlyList<TableIndexAccessPlan> CollectIndexAccessPlans(
        TableSchema schema,
        SqlExpression? where)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (TryExtractPrimaryKeyValues(
            schema,
            where,
            allowExtraPredicates: true,
            out _,
            allowNonEqualityExtraPredicates: true)
            || where is null
            || schema.Indexes.Count == 0)
        {
            return Array.Empty<TableIndexAccessPlan>();
        }

        bool hasColumnEqualities = TryCollectEqualityExpressions(
            where,
            allowNonEquality: true,
            out var equalityByColumn);
        var plans = new List<TableIndexAccessPlan>(schema.Indexes.Count);
        foreach (TableIndex candidate in schema.Indexes.OrderByDescending(static index => index.Columns.Count))
        {
            IReadOnlyList<object?> candidateValues;
            TableIndexRange? candidateRange = null;
            if (!string.IsNullOrWhiteSpace(candidate.JsonPath))
            {
                if (!TryExtractJsonPathIndexValue(candidate, where, out object? jsonPathValue))
                    continue;
                candidateValues = [jsonPathValue];
            }
            else
            {
                var values = new List<object?>(candidate.Columns.Count);
                if (hasColumnEqualities)
                {
                    for (int index = 0; index < candidate.Columns.Count; index++)
                    {
                        if (!equalityByColumn.TryGetValue(candidate.Columns[index], out SqlExpression? expression))
                            break;
                        TableColumn column = schema.TryGetColumn(candidate.Columns[index])
                            ?? throw new InvalidOperationException(
                                $"索引 '{candidate.Name}' 引用了未知列 '{candidate.Columns[index]}'。");
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
                            break;
                        }
                    }
                }

                if (values.Count < candidate.Columns.Count)
                {
                    TableColumn rangeColumn = schema.TryGetColumn(candidate.Columns[values.Count])
                        ?? throw new InvalidOperationException(
                            $"索引 '{candidate.Name}' 引用了未知列 '{candidate.Columns[values.Count]}'。");
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
                    continue;
                }
                candidateValues = values;
            }

            plans.Add(new TableIndexAccessPlan(candidate, candidateValues, candidateRange));
        }
        return plans;
    }

    private static bool IsHeuristicallyBetter(
        TableIndexAccessPlan candidate,
        TableIndexAccessPlan current)
    {
        bool candidateIsUniquePoint = candidate.Index.IsUnique && candidate.IsFullEquality;
        bool currentIsUniquePoint = current.Index.IsUnique && current.IsFullEquality;
        return (candidateIsUniquePoint && !currentIsUniquePoint)
            || (candidateIsUniquePoint == currentIsUniquePoint
                && (candidate.MatchedColumnCount > current.MatchedColumnCount
                    || (candidate.MatchedColumnCount == current.MatchedColumnCount
                        && candidate.EqualityPrefixValues.Count > current.EqualityPrefixValues.Count)
                    || (candidate.MatchedColumnCount == current.MatchedColumnCount
                        && candidate.EqualityPrefixValues.Count == current.EqualityPrefixValues.Count
                        && candidate.IsFullEquality
                        && !current.IsFullEquality)));
    }

    /// <summary>
    /// 使用统计成本模型选择二级索引；统计缺失或过期时保持旧启发式结果。
    /// </summary>
    internal static TableIndexAccessPlan? ChooseBestIndexAccessPlan(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
        => ChooseBestIndexAccessPlan(store, schema, where, allowAutomaticStatisticsRefresh: true);

    private static TableIndexAccessPlan? ChooseBestIndexAccessPlan(
        TableStore store,
        TableSchema schema,
        SqlExpression? where,
        bool allowAutomaticStatisticsRefresh)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schema);
        return TableCostPlanner.Estimate(
            store,
            schema,
            where,
            allowAutomaticRefresh: allowAutomaticStatisticsRefresh).IndexPlan;
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

        var sortItems = ResolveSortItems(result.Columns, orderBy);
        var comparer = new ResultRowSortComparer(sortItems);

        int offset = pagination?.Offset ?? 0;
        int? fetch = pagination?.Fetch;

        var rows = TopN.OrderByThenPaginate(
            result.Rows, comparer, offset, fetch, SqlSpillCodecs.ReadOnlyRows);
        return new SelectExecutionResult(result.Columns, rows);
    }

    /// <summary>
    /// 从惰性投影序列直接执行 ORDER BY + LIMIT，避免先构造候选结果全集。
    /// </summary>
    private static SelectExecutionResult ApplyOrderByAndPagination(
        IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyList<object?>> rows,
        IReadOnlyList<OrderBySpec> orderBy,
        PaginationSpec? pagination)
    {
        var sortItems = ResolveSortItems(columns, orderBy);
        var comparer = new ResultRowSortComparer(sortItems);
        var selected = TopN.OrderByThenPaginate(
            rows,
            comparer,
            pagination?.Offset ?? 0,
            pagination?.Fetch,
            SqlSpillCodecs.ReadOnlyRows);
        return new SelectExecutionResult(columns, selected);
    }

    private static (int ColumnIndex, SortDirection Direction)[] ResolveSortItems(
        IReadOnlyList<string> columns,
        IReadOnlyList<OrderBySpec> orderBy)
        => orderBy.Select(order =>
            {
                if (order.Expression is not IdentifierExpression { Name: var name })
                    throw new InvalidOperationException("关系表 ORDER BY 当前仅支持列名。");

                int columnIndex = -1;
                for (int i = 0; i < columns.Count; i++)
                {
                    if (string.Equals(columns[i], name, StringComparison.Ordinal))
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

    /// <summary>按已解析的表列顺序比较索引范围行，供单个范围值并列组校正 SQL 排序。</summary>
    private sealed class TableRowIndexOrderComparer(
        IReadOnlyList<int> columnOrdinals,
        SortDirection direction) : IComparer<TableRow>
    {
        /// <summary>逐列应用统一 SQL 标量比较，并按查询方向返回首个非零结果。</summary>
        public int Compare(TableRow? x, TableRow? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            foreach (int ordinal in columnOrdinals)
            {
                int comparison = ScalarComparer.Instance.Compare(x.Values[ordinal], y.Values[ordinal]);
                if (comparison != 0)
                    return direction == SortDirection.Descending ? -comparison : comparison;
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

    /// <summary>
    /// 对惰性投影序列应用无排序分页，只保留所需窗口并在窗口完成后停止扫描。
    /// </summary>
    private static SelectExecutionResult ApplyPagination(
        IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyList<object?>> rows,
        PaginationSpec? pagination)
    {
        if (pagination is null)
            return new SelectExecutionResult(columns, rows.ToArray());

        int offset = pagination.Offset;
        int take = pagination.Fetch ?? int.MaxValue;
        if (take <= 0)
            return new SelectExecutionResult(columns, []);

        var selected = new List<IReadOnlyList<object?>>(Math.Min(take, 256));
        int skipped = 0;
        foreach (var row in rows)
        {
            if (skipped < offset)
            {
                skipped++;
                continue;
            }

            selected.Add(row);
            if (selected.Count >= take)
                break;
        }

        return new SelectExecutionResult(columns, selected);
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

    /// <summary>按原有从左到右顺序收集顶层 OR 叶子，并在固定分支预算处停止。</summary>
    private static bool TryCollectOrPredicates(
        SqlExpression expression,
        List<SqlExpression> predicates)
    {
        int maximumNodes = (MaxIndexUnionBranches * 2) - 1;
        int visitedNodes = 0;
        var pending = new Stack<SqlExpression>(maximumNodes);
        pending.Push(expression);
        while (pending.Count > 0)
        {
            if (++visitedNodes > maximumNodes)
                return false;

            SqlExpression current = pending.Pop();
            if (current is BinaryExpression { Operator: SqlBinaryOperator.Or } binary)
            {
                pending.Push(binary.Right);
                pending.Push(binary.Left);
                continue;
            }

            if (predicates.Count == MaxIndexUnionBranches)
                return false;
            predicates.Add(current);
        }

        return true;
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
