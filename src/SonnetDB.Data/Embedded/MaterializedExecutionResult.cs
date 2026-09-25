using System.Diagnostics.CodeAnalysis;
using SonnetDB.Data.Internal;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Data.Embedded;

/// <summary>
/// 把内存物化的 <see cref="SelectExecutionResult"/> 适配为 <see cref="IExecutionResult"/>；
/// 也用于非 SELECT 语句返回受影响行数。
/// </summary>
internal sealed class MaterializedExecutionResult : IExecutionResult
{
    private readonly IReadOnlyList<IReadOnlyList<object?>> _rows;
    private readonly ExecutionFieldTypeKind[] _columnTypes;
    private readonly ExecutionColumnMetadata[]? _columnMetadata;
    private int _rowIndex = -1;

    private MaterializedExecutionResult(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int recordsAffected,
        bool truncated,
        IReadOnlyList<TableColumn>? resultSchema = null,
        IReadOnlyList<SelectColumnInfo>? columnInfo = null)
    {
        Columns = columns;
        _rows = rows;
        RecordsAffected = recordsAffected;
        Truncated = truncated;
        _columnTypes = new ExecutionFieldTypeKind[columns.Count];
        if (resultSchema is not null)
        {
            _columnMetadata = new ExecutionColumnMetadata[columns.Count];
            for (int c = 0; c < columns.Count; c++)
            {
                var declared = resultSchema[c];
                _columnTypes[c] = ExecutionFieldTypeResolver.Resolve(declared.DataType);
                _columnMetadata[c] = new ExecutionColumnMetadata(
                    declared.IsNullable, declared.IsPrimaryKey,
                    declared.IsAutoIncrement, declared.IsRowVersion);
            }
            return;
        }
        if (columnInfo is not null)
        {
            _columnMetadata = new ExecutionColumnMetadata[columns.Count];
            for (int c = 0; c < columns.Count; c++)
            {
                var declared = columnInfo[c];
                _columnTypes[c] = declared.DataType is { } type
                    ? ExecutionFieldTypeResolver.Resolve(type)
                    : ResolveFromRows(rows, c);
                _columnMetadata[c] = new ExecutionColumnMetadata(
                    declared.IsNullable ?? true,
                    declared.IsKey,
                    declared.IsAutoIncrement,
                    declared.IsRowVersion);
            }
            return;
        }
        for (int c = 0; c < columns.Count; c++)
            _columnTypes[c] = ResolveFromRows(rows, c);
    }

    public int RecordsAffected { get; }

    public bool Truncated { get; }

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

    public ExecutionColumnMetadata? GetColumnMetadata(int ordinal) => _columnMetadata?[ordinal];

    public void Dispose() { /* 无非托管资源 */ }

    public static MaterializedExecutionResult FromSelect(
        SelectExecutionResult result,
        int recordsAffected = -1)
        => new(result.Columns, result.Rows, recordsAffected, result.Truncated,
            result.ColumnSchema, result.ColumnInfo);

    public static MaterializedExecutionResult NonQuery(int recordsAffected)
        => new(Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>(), recordsAffected, truncated: false);

    private static ExecutionFieldTypeKind ResolveFromRows(
        IReadOnlyList<IReadOnlyList<object?>> rows, int column)
    {
        var kind = ExecutionFieldTypeKind.Object;
        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row][column] is { } value)
            {
                var next = ExecutionFieldTypeResolver.Resolve(value);
                if (kind != ExecutionFieldTypeKind.Object && kind != next)
                    return ExecutionFieldTypeKind.Object;
                kind = next;
            }
        }
        return kind;
    }
}
