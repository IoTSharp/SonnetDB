using System.Diagnostics.CodeAnalysis;
using SonnetDB.Data.Internal;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Data.Embedded;

/// <summary>
/// 把内存物化的 <see cref="SelectExecutionResult"/> 适配为 <see cref="IExecutionResult"/>；
/// 也用于非 SELECT 语句返回受影响行数。
/// </summary>
internal sealed class MaterializedExecutionResult : IExecutionResult
{
    private readonly IReadOnlyList<IReadOnlyList<object?>> _rows;
    private readonly ExecutionFieldTypeKind[] _columnTypes;
    private int _rowIndex = -1;

    private MaterializedExecutionResult(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int recordsAffected)
    {
        Columns = columns;
        _rows = rows;
        RecordsAffected = recordsAffected;
        _columnTypes = new ExecutionFieldTypeKind[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            var kind = ExecutionFieldTypeKind.Object;
            for (int r = 0; r < rows.Count; r++)
            {
                var v = rows[r][c];
                if (v is null) continue;
                kind = ExecutionFieldTypeResolver.Resolve(v);
                break;
            }
            _columnTypes[c] = kind;
        }
    }

    public int RecordsAffected { get; }

    public IReadOnlyList<string> Columns { get; }

    public bool ReadNextRow()
    {
        if (_rowIndex + 1 >= _rows.Count) return false;
        _rowIndex++;
        return true;
    }

    public ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadNextRow());
    }

    public object? GetValue(int ordinal)
    {
        if (_rowIndex < 0 || _rowIndex >= _rows.Count)
            throw new InvalidOperationException("当前未定位到任何行。");
        return _rows[_rowIndex][ordinal];
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public Type GetFieldType(int ordinal) => ExecutionFieldTypeResolver.GetRuntimeType(_columnTypes[ordinal]);

    public void Dispose() { /* 无非托管资源 */ }

    public static MaterializedExecutionResult FromSelect(SelectExecutionResult result)
        => new(result.Columns, result.Rows, recordsAffected: -1);

    public static MaterializedExecutionResult NonQuery(int recordsAffected)
        => new(Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>(), recordsAffected);
}
