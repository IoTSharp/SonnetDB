using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal sealed class SqlTemporaryTableWorkspace : IDisposable
{
    internal SqlTemporaryTableWorkspace(Tsdb database, string directory)
    {
        DirectoryPath = directory;
        Tables = new TableManager(directory, database.RuntimeOptions.Kv);
    }

    internal string DirectoryPath { get; }
    internal TableManager Tables { get; }
    internal long EstimatedBytes { get; private set; }
    internal int RowCount { get; private set; }

    internal void CopyFrom(SqlTemporaryTableWorkspace source, SqlSessionOptions options,
        Guid sessionId, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using var transactionScope = SqlTransactionContext.EnterScope(null);
        using var reservation = SqlQueryResources.Current?.CreateReservation();
        IReadOnlyList<TableSchema> schemas = source.Tables.Catalog.Snapshot();
        if (schemas.Count > options.MaximumTemporaryTables)
            throw new InvalidOperationException("临时表数量超出会话上限。");
        int copied = 0;
        long bytes = 0;
        foreach (TableSchema schema in schemas)
        {
            CheckDeadline(started, options, cancellationToken);
            Tables.Create(schema);
            SqlTemporaryTableIdentity.Register(schema, sessionId);
            int remaining = options.MaximumCloneRows - copied;
            var values = new List<IReadOnlyList<object?>>();
            foreach (TableRow row in source.Tables.Open(schema.Name).EnumerateScan(checked(remaining + 1)))
            {
                CheckDeadline(started, options, cancellationToken);
                if (values.Count >= remaining)
                    throw new InvalidOperationException("临时目录复制超出行数预算。");
                long rowBytes = EstimateRow(row.Values);
                bytes = checked(bytes + rowBytes);
                EstimatedBytes = bytes;
                if (bytes > options.MaximumCloneBytes)
                    throw new InvalidOperationException("临时目录复制超出字节预算。");
                if (reservation is not null && !reservation.TryReserve(checked(rowBytes * 2)))
                    throw new InvalidOperationException("临时目录复制超出查询或数据库内存预算。");
                SqlRowRetentionBudget.RetainForExecution(row.Values);
                values.Add(row.Values.ToArray());
            }
            copied = checked(copied + values.Count);
            if (values.Count != 0)
                Tables.Open(schema.Name).InsertMany(values);
        }
        RowCount = copied;
        EstimatedBytes = bytes;
    }

    internal void ValidateCapacity(SqlSessionOptions options, Guid sessionId,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using var transactionScope = SqlTransactionContext.EnterScope(null);
        IReadOnlyList<TableSchema> schemas = Tables.Catalog.Snapshot();
        if (schemas.Count > options.MaximumTemporaryTables)
            throw new InvalidOperationException("临时表数量超出会话上限。");
        int count = 0;
        long bytes = 0;
        foreach (TableSchema schema in schemas)
        {
            CheckDeadline(started, options, cancellationToken);
            SqlTemporaryTableIdentity.Register(schema, sessionId);
            int remaining = options.MaximumTemporaryRows - count;
            int tableRows = 0;
            foreach (TableRow row in Tables.Open(schema.Name).EnumerateScan(checked(remaining + 1)))
            {
                CheckDeadline(started, options, cancellationToken);
                if (++tableRows > remaining)
                    throw new InvalidOperationException("临时表数据超出会话行数预算。");
                bytes = checked(bytes + EstimateRow(row.Values));
                EstimatedBytes = bytes;
                if (bytes > options.MaximumTemporaryBytes)
                    throw new InvalidOperationException("临时表数据超出会话字节预算。");
            }
            count = checked(count + tableRows);
        }
        RowCount = count;
        EstimatedBytes = bytes;
    }

    internal static long EstimateRow(IReadOnlyList<object?> values)
    {
        long bytes = checked(64L + values.Count * 16L);
        foreach (object? value in values)
        {
            bytes = checked(bytes + (value switch
            {
                string text => 32L + text.Length * 2L,
                byte[] blob => 32L + blob.Length,
                float[] vector => 32L + vector.Length * 4L,
                decimal or DateTimeOffset => 32L,
                null => 0L,
                _ => 24L,
            }));
        }
        return bytes;
    }

    private static void CheckDeadline(long started, SqlSessionOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqlExecutor.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= options.OperationTimeout)
            throw new TimeoutException("临时目录创建、复制或校验超时。");
    }

    public void Dispose() => Tables.Dispose();
}
