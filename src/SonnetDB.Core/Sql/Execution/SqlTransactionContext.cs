using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// SQL 轻事务上下文。当前聚焦关系表小批量 DML，在 COMMIT 时按表原子提交。
/// </summary>
public sealed class SqlTransactionContext
{
    private readonly Dictionary<string, List<TableRowMutation>> _tableMutations = new(StringComparer.Ordinal);
    private bool _completed;

    /// <summary>事务是否已经提交或回滚。</summary>
    public bool IsCompleted => _completed;

    internal void AddTableMutation(string tableName, TableRowMutation mutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(mutation);
        ThrowIfCompleted();

        if (!_tableMutations.TryGetValue(tableName, out var list))
        {
            list = [];
            _tableMutations.Add(tableName, list);
        }

        list.Add(mutation);
    }

    internal IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> SnapshotTableMutations()
        => _tableMutations.ToDictionary(
            static p => p.Key,
            static p => (IReadOnlyList<TableRowMutation>)p.Value.ToArray(),
            StringComparer.Ordinal);

    internal void MarkCompleted()
        => _completed = true;

    internal void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("轻事务已结束。");
    }
}
