using System.Diagnostics.CodeAnalysis;

namespace SonnetDB.Data.Internal;

/// <summary>逐个持有只读结果；释放当前流后才执行下一条 SELECT。</summary>
internal sealed class BatchExecutionResult(
    IExecutionResult first,
    int statementCount,
    Func<int, CancellationToken, Task<IExecutionResult>> execute) : IExecutionResult
{
    private IExecutionResult _current = first;
    private int _statementIndex;
    private bool _currentDisposed;
    private bool _completed;

    public int RecordsAffected => -1;
    public bool Truncated => _current.Truncated;
    public IReadOnlyList<string> Columns => _current.Columns;
    public bool ReadNextRow() => !_currentDisposed && _current.ReadNextRow();
    public ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken)
        => _currentDisposed ? ValueTask.FromResult(false) : _current.ReadNextRowAsync(cancellationToken);
    public object? GetValue(int ordinal) => _current.GetValue(ordinal);

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public Type GetFieldType(int ordinal) => _current.GetFieldType(ordinal);

    public ExecutionColumnMetadata? GetColumnMetadata(int ordinal) => _current.GetColumnMetadata(ordinal);

    public async ValueTask<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_completed)
            return false;
        // 先终结当前流的所有权；异常时也禁止重试同一个已失败的批次步骤。
        _completed = true;
        DisposeCurrent();
        if (++_statementIndex >= statementCount)
            return false;
        _current = await execute(_statementIndex, cancellationToken).ConfigureAwait(false);
        _currentDisposed = false;
        _completed = false;
        return true;
    }

    public void Dispose()
    {
        _completed = true;
        DisposeCurrent();
    }

    private void DisposeCurrent()
    {
        if (_currentDisposed)
            return;
        _currentDisposed = true;
        _current.Dispose();
    }
}
