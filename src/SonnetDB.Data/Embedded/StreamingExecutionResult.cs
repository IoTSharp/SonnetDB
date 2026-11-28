using System.Diagnostics.CodeAnalysis;
using SonnetDB.Data.Internal;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Data.Embedded;

/// <summary>把一次性 SQL 行游标交给嵌入式读取器，并随读取器或连接释放。</summary>
internal sealed class StreamingExecutionResult(
    SqlStreamingSelectResult result, Action<StreamingExecutionResult> onDispose) : IExecutionResult
{
    private readonly object _sync = new();
    private readonly MaterializedExecutionResult _metadata = MaterializedExecutionResult.FromSelect(
        new SelectExecutionResult(result.Columns, []) { ColumnSchema = result.ColumnSchema, ColumnInfo = result.ColumnInfo });
    private bool _disposed;

    public int RecordsAffected => -1;

    public IReadOnlyList<string> Columns => _metadata.Columns;

    public bool DisposeOnReadFailure => true;

    public bool ReadNextRow() => ReadCore(CancellationToken.None);

    public ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(ReadCore(cancellationToken));

    private bool ReadCore(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed)
                return false;
            try
            {
                if (result.MoveNext(cancellationToken))
                    return true;
            }
            catch
            {
                Dispose();
                throw;
            }
            Dispose();
            return false;
        }
    }

    public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }

    public object? GetValue(int ordinal) => result.Current[ordinal];

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public Type GetFieldType(int ordinal) => _metadata.GetFieldType(ordinal);

    public ExecutionColumnMetadata? GetColumnMetadata(int ordinal) => _metadata.GetColumnMetadata(ordinal);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { result.Dispose(); }
            finally { onDispose(this); }
        }
    }
}
