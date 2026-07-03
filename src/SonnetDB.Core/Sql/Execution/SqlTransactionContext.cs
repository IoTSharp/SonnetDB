using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// SQL 轻事务上下文。当前聚焦关系表小批量 DML，在 COMMIT 时按表原子提交。
/// </summary>
/// <remarks>
/// <para>
/// 隔离级别：<b>读已提交 + 本事务 read-your-writes</b>（#218）。事务内的关系表 SELECT
/// 会在已提交基线之上叠加本事务尚未提交的缓冲 insert/update/delete（见 <see cref="Current"/> /
/// <see cref="TryGetBufferedMutations"/>），因此能看到自身写入；对其他并发写入仍是读已提交
/// （不做快照，不加读锁）。measurement / document 写入在事务内被拒绝（#199），故 read-your-writes
/// 只覆盖关系表。
/// </para>
/// </remarks>
public sealed class SqlTransactionContext
{
    private static readonly AsyncLocal<SqlTransactionContext?> _current = new();

    private readonly Dictionary<string, List<TableRowMutation>> _tableMutations = new(StringComparer.Ordinal);
    private bool _completed;

    /// <summary>事务是否已经提交或回滚。</summary>
    public bool IsCompleted => _completed;

    /// <summary>当前语句执行作用域内的活动轻事务（基于 <see cref="AsyncLocal{T}"/>）；无事务时为 <c>null</c>。</summary>
    public static SqlTransactionContext? Current => _current.Value;

    /// <summary>把 <paramref name="transaction"/> 设为当前执行作用域的 ambient 轻事务，返回作用域释放器。</summary>
    public static AmbientScope EnterScope(SqlTransactionContext? transaction)
        => new(transaction);

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

    /// <summary>
    /// 读取某表本事务已缓冲、尚未提交的变更序列（按加入顺序）。供 read-your-writes 叠加使用（#218）。
    /// 无缓冲变更时返回 <c>false</c>。
    /// </summary>
    internal bool TryGetBufferedMutations(string tableName, out IReadOnlyList<TableRowMutation> mutations)
    {
        if (!_completed && _tableMutations.TryGetValue(tableName, out var list) && list.Count > 0)
        {
            mutations = list;
            return true;
        }

        mutations = [];
        return false;
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

    /// <summary>用于在 <c>using</c> 块中临时设置 ambient 轻事务上下文。</summary>
    public readonly struct AmbientScope : IDisposable
    {
        private readonly SqlTransactionContext? _previous;

        internal AmbientScope(SqlTransactionContext? transaction)
        {
            _previous = _current.Value;
            _current.Value = transaction;
        }

        /// <summary>恢复进入前的 ambient 轻事务上下文。</summary>
        public void Dispose()
            => _current.Value = _previous;
    }
}
