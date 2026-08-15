using SonnetDB.Kv;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表的一致只读视图；schema 与底层 KV 快照在同一个短锁捕获点绑定。
/// </summary>
internal sealed class TableReadSnapshot : IDisposable
{
    private KvReadSnapshot? _snapshot;

    internal TableReadSnapshot(TableSchema schema, KvReadSnapshot snapshot)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>快照捕获时可见的不可变表 schema。</summary>
    internal TableSchema Schema { get; }

    /// <summary>快照捕获时可见的 KV 版本与数据视图。</summary>
    internal KvReadSnapshot Snapshot
        => _snapshot ?? throw new ObjectDisposedException(nameof(TableReadSnapshot));

    /// <summary>释放底层 KV 快照租约。</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _snapshot, null)?.Dispose();
    }
}
