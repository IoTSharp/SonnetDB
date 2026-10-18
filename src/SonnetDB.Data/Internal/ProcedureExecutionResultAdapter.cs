using System.Diagnostics.CodeAnalysis;
using SonnetDB.Data.Embedded;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Data.Internal;

internal sealed class ProcedureExecutionResultAdapter : IExecutionResult
{
    private readonly IExecutionResult[] _results;
    private readonly ProcedureCallSnapshot _call;
    private readonly IReadOnlyDictionary<string, object?> _outputs;
    private readonly long _returnValue;
    private int _index;
    private bool _failed;
    private bool _completed;

    public ProcedureExecutionResultAdapter(IReadOnlyList<SelectExecutionResult> results,
        IReadOnlyDictionary<string, object?> outputs, long returnValue, int recordsAffected, ProcedureCallSnapshot call)
    {
        _results = results.Count == 0 ? [MaterializedExecutionResult.NonQuery(recordsAffected)]
            : results.Select(result => (IExecutionResult)MaterializedExecutionResult.FromSelect(result, recordsAffected)).ToArray();
        _outputs = outputs;
        _returnValue = returnValue;
        _call = call;
        RecordsAffected = recordsAffected;
    }

    public int RecordsAffected { get; }
    public IReadOnlyList<string> Columns => _results[_index].Columns;
    public bool Truncated => _results.Any(static result => result.Truncated);
    public bool ReadNextRow() => _results[_index].ReadNextRow();
    public ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _results[_index].ReadNextRowAsync(cancellationToken);
    }
    public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_index + 1 >= _results.Length) return ValueTask.FromResult(false);
        _results[_index++].Dispose();
        return ValueTask.FromResult(true);
    }
    public object? GetValue(int ordinal) => _results[_index].GetValue(ordinal);
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public Type GetFieldType(int ordinal) => _results[_index].GetFieldType(ordinal);
    public ExecutionColumnMetadata? GetColumnMetadata(int ordinal) => _results[_index].GetColumnMetadata(ordinal);
    public void Abort() => _failed = true;
    public void Complete(CancellationToken cancellationToken)
    {
        if (_completed) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (_failed) return;
        _call.Publish(_outputs, _returnValue);
        _completed = true;
    }
    public void Dispose()
    {
        foreach (var result in _results) result.Dispose();
    }
}
