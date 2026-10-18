using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

public static partial class SqlExecutor
{
    private static bool IsTemporaryDropOrTruncate(SqlStatement statement)
    {
        string? name = statement switch
        {
            DropTableStatement drop => drop.Name,
            TruncateTableStatement truncate => truncate.TableName,
            _ => null,
        };
        return name is not null && SqlSessionContext.Current?.VisibleTables?.Catalog.TryGet(name) is not null;
    }

    private static bool IsTemporaryMutation(Tsdb database, SqlStatement statement)
    {
        if (statement is CreateTableStatement { IsTemporary: true } or CreateTableAsStatement { IsTemporary: true })
            return true;
        string? name = statement switch
        {
            InsertStatement insert => insert.Measurement,
            UpdateStatement update => update.TableName,
            DeleteStatement delete => delete.Measurement,
            DropTableStatement drop => drop.Name,
            TruncateTableStatement truncate => truncate.TableName,
            _ => null,
        };
        return name is not null && SqlSessionContext.TemporaryManagerFor(database.Tables)?.Catalog.TryGet(name) is not null;
    }

    private static object? ExecuteTemporaryMutation(Tsdb database, SqlStatement statement,
        SqlTransactionContext? transaction, SqlExecutionOptions options)
    {
        SqlSessionContext session = SqlSessionContext.Current
            ?? throw new InvalidOperationException("临时关系表需要由逻辑连接拥有的 SqlSessionContext。");
        if (!options.CanWrite)
            throw new InvalidOperationException("临时关系表变更需要数据库写权限。");
        if (statement is CreateTableAsStatement createAs)
            return ExecuteCreateTableAs(database, createAs, transaction, options);
        using var mutation = session.BeginTemporaryMutation(transaction, options.CancellationToken);
        object? result = statement switch
        {
            CreateTableStatement create => TableSqlExecutor.ExecuteCreateTemporaryTable(database, create),
            InsertStatement insert => ExecuteInsert(database, databaseName: null, insert, controlPlane: null, transaction: null),
            UpdateStatement update => ExecuteUpdate(database, databaseName: null, update, controlPlane: null, transaction: null),
            DeleteStatement delete => ExecuteDelete(database, databaseName: null, delete, controlPlane: null, transaction: null),
            DropTableStatement drop => new RowsAffectedExecutionResult(drop.Name,
                mutation.Tables.Drop(drop.Name) ? 1 : 0, "drop_table"),
            TruncateTableStatement truncate => new RowsAffectedExecutionResult(truncate.TableName,
                mutation.Tables.Truncate(truncate.TableName), "truncate"),
            _ => throw new NotSupportedException("当前临时关系对象仅支持 CREATE/CTAS/DROP/TRUNCATE 和关系 DML。"),
        };
        mutation.Complete();
        return result;
    }

    private static RowsAffectedExecutionResult ExecuteCreateTableAs(Tsdb database, CreateTableAsStatement statement,
        SqlTransactionContext? transaction, SqlExecutionOptions options)
    {
        if (!options.CanWrite)
            throw new InvalidOperationException("CTAS 需要数据库写权限。");
        if (SqlLockingReadContract.Contains(statement.Query) || SqlSequenceRuntime.Contains(statement.Query))
            throw new NotSupportedException("CTAS 来源必须是无锁定读和序列副作用的 SELECT。");
        if (!statement.IsTemporary && transaction is not null)
            throw new NotSupportedException("持久 CTAS 不能在活动事务内执行；持久 DDL 事务仍须独立实现。");
        TableSchema? existing = database.Tables.Catalog.Resolve(statement.Name, statement.NameIsQuoted);
        if (existing is not null)
        {
            bool temporary = SqlTemporaryTableIdentity.TryGet(existing, out _);
            if (temporary != statement.IsTemporary)
                throw new InvalidOperationException("CTAS 目标与另一作用域的同名对象冲突。");
            if (statement.IfNotExists)
                return new RowsAffectedExecutionResult(existing.Name, 0, "create_table_as");
        }
        SqlSourceNameResolver.EnsureAvailable(database, statement.Name);
        SqlMaterializationContract.Validate(database, statement);
        SelectExecutionResult source;
        using (SqlRowRetentionBudget.EnterCreateAsSource())
            source = ExecuteSelect(database, statement.Query, previewMaxRows: null, previewMaxBytes: null);
        TableSchema schema = SqlCreateTableAsSchema.Create(statement.Name, source);
        if (source.Rows.Count > 100_000)
            throw new InvalidOperationException("CTAS 一次物化最多允许 100000 行。");
        long bytes = 0;
        foreach (IReadOnlyList<object?> row in source.Rows)
        {
            ThrowIfCancellationRequested();
            if (row.Count != schema.Columns.Count)
                throw new InvalidDataException("CTAS 行宽与声明列数不一致。");
            bytes = checked(bytes + SqlTemporaryTableWorkspace.EstimateRow(row));
            if (bytes > 64L * 1024 * 1024)
                throw new InvalidOperationException("CTAS 数据超出 64MiB 估算字节预算。");
        }
        if (statement.IsTemporary)
        {
            SqlSessionContext session = CurrentSession();
            using var mutation = session.BeginTemporaryMutation(transaction, options.CancellationToken);
            database.Tables.Create(schema);
            if (source.Rows.Count != 0)
                mutation.Tables.Open(schema.Name).InsertMany(source.Rows);
            mutation.Complete();
        }
        else
        {
            database.Tables.CreateAs(schema, source.Rows, options.CancellationToken);
        }
        return new RowsAffectedExecutionResult(schema.Name, source.Rows.Count, "create_table_as");
    }

    private static SqlSessionContext CurrentSession()
        => SqlSessionContext.Current ?? throw new InvalidOperationException("临时 CTAS 需要连接 SqlSessionContext。");
}
