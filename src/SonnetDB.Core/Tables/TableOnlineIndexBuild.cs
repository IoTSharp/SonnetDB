using System.Diagnostics;
using System.Text;
using SonnetDB.Kv;

namespace SonnetDB.Tables;

/// <summary>一次有界在线索引构建调用的结果；未完成时可以使用相同声明继续调用。</summary>
/// <param name="TableName">目标关系表。</param>
/// <param name="IndexName">目标非唯一索引。</param>
/// <param name="Completed">索引已持久发布到目录时为 true。</param>
/// <param name="PagesProcessed">本次已提交的页数。</param>
/// <param name="RowsProcessed">本次已提交的历史行数。</param>
/// <param name="Status">complete、pending、waiting_checkpoint 或 yielded。</param>
public sealed record TableOnlineIndexBuildResult(
    string TableName, string IndexName, bool Completed, int PagesProcessed, int RowsProcessed, string Status);

public sealed partial class TableManager
{
    /// <summary>仅供测试在页锁全部释放后安排并发 DML、取消或数据库关闭。</summary>
    internal Action<int>? OnlineIndexPageCompletedTestHook { get; set; }

    /// <summary>
    /// 分页构建普通非唯一索引；单次最多 64 页、每页 16 行、总预算 30 秒。
    /// 每页释放所有 schema、manager 和 store 锁并退避，取消或预算耗尽后保留持久游标。
    /// </summary>
    /// <param name="tableName">表名。</param>
    /// <param name="definition">仅支持非唯一普通列索引。</param>
    /// <param name="cancellationToken">取消本次推进，不删除已经提交的进度。</param>
    /// <param name="maximumPages">本次页数上限，范围 1 到 64。</param>
    /// <returns>构建进度；只有 Completed 为 true 才能使用索引。</returns>
    public TableOnlineIndexBuildResult CreateIndexOnline(
        string tableName,
        TableIndexDefinition definition,
        CancellationToken cancellationToken = default,
        int maximumPages = 64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPages, 64);
        if (definition.IsUnique || definition.JsonPath is not null)
            throw new NotSupportedException("ONLINE 目前仅支持普通非唯一关系表索引。");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        int pages = 0;
        int rows = 0;
        for (int page = 0; page < maximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 单页的锁等待、覆盖层准备、读行和写批次共用短预算；默认 KV 背压不会占住业务锁 30 秒。
            using var pageBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            pageBudget.CancelAfter(TimeSpan.FromMilliseconds(250));
            try
            {
                using (EnterOnlineIndexLock(_schemaSync, pageBudget.Token))
                using (EnterOnlineIndexLock(_sync, pageBudget.Token))
                {
                    ThrowIfDisposed();
                    var schema = Catalog.TryGet(tableName)
                        ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                    // 冷开本身可能需要传统恢复；在线入口要求调用方先让正常业务完成开表。
                    if (!_stores.TryGetValue(tableName, out var store))
                        return new(tableName, definition.Name, false, pages, rows, "yielded");
                    using (EnterOnlineIndexLock(store.SynchronizationRoot, pageBudget.Token))
                    {
                        if (schema.TryGetIndex(definition.Name) is { } existing)
                        {
                            EnsureOnlineIndexDefinition(existing, definition);
                            store.CleanupPublishedOnlineIndexBuild(pageBudget.Token);
                            return new(tableName, definition.Name, true, pages, rows, "complete");
                        }
                        var progress = store.AdvanceOnlineIndexBuild(definition, pageBudget.Token);
                        if (progress.WaitingForCheckpoint)
                            return new(tableName, definition.Name, false, pages, rows, "waiting_checkpoint");
                        pages++;
                        rows += progress.Rows;
                        if (progress.Completed)
                        {
                            store.PublishOnlineIndexBuild(updated =>
                            {
                                // 索引 WAL 先同步，候选目录完整落盘后才发布内存目录，失败时保留可恢复进度。
                                var candidateSchemas = Catalog.Snapshot()
                                    .Select(item => item.Name == tableName ? updated : item).ToArray();
                                TableSchemaCodec.Save(SchemaPath, candidateSchemas);
                                AfterCatalogPersistedBeforePublishTestHook?.Invoke();
                            }, updated => Catalog.LoadOrReplace(updated), pageBudget.Token);
                            return new(tableName, definition.Name, true, pages, rows, "complete");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(tableName, definition.Name, false, pages, rows, "yielded");
            }
            OnlineIndexPageCompletedTestHook?.Invoke(pages);
            if (budget.IsCancellationRequested)
                return new(tableName, definition.Name, false, pages, rows, "yielded");
            // 不持锁等待，前台写入和多表事务可在页间推进。
            if (cancellationToken.WaitHandle.WaitOne(10))
                cancellationToken.ThrowIfCancellationRequested();
        }
        return new(tableName, definition.Name, false, pages, rows, "pending");
    }

    /// <summary>同名续建只允许完全相同的列顺序与索引种类，避免误把别的索引当成完成。</summary>
    internal static void EnsureOnlineIndexDefinition(TableIndex index, TableIndexDefinition definition)
    {
        if (index.IsUnique != definition.IsUnique || index.JsonPath != definition.JsonPath
            || !index.Columns.SequenceEqual(definition.Columns, StringComparer.Ordinal))
            throw new InvalidOperationException($"索引 '{definition.Name}' 已有不同定义，不能继续 ONLINE 构建。");
    }

    /// <summary>以可取消的短等待取得在线构建所需锁，不进入不可取消的 Monitor.Enter。</summary>
    private static IDisposable EnterOnlineIndexLock(object synchronizationRoot, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        for (int attempt = 0; attempt < 30 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(300); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Monitor.TryEnter(synchronizationRoot, 10))
                return new OnlineIndexLockLease(synchronizationRoot);
        }
        throw new OperationCanceledException("在线索引本页锁等待预算已用尽。", cancellationToken);
    }

    /// <summary>只释放当前线程已经成功取得的一个锁。</summary>
    private sealed class OnlineIndexLockLease(object synchronizationRoot) : IDisposable
    {
        /// <summary>释放本页的同步锁。</summary>
        public void Dispose() => Monitor.Exit(synchronizationRoot);
    }
}

public sealed partial class TableStore
{
    private static readonly byte[] OnlineIndexStateKey = [(byte)'m', (byte)'o'];
    private OnlineIndexBuildState? _onlineIndexBuild;

    /// <summary>在所有行和索引写入共用的原子批次中维护尚未发布的索引。</summary>
    private IReadOnlyList<IndexEntry> BuildMutationIndexEntries(TableSchema schema, TableRow row)
    {
        var entries = BuildIndexEntries(schema, row);
        if (_onlineIndexBuild is not { } pending || schema.TryGetIndex(pending.Index.Name) is not null)
            return entries;
        byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(pending.Index, row.Values, schema, row.PrimaryKey.Span);
        if (key is null)
            return entries;
        var combined = entries.ToList();
        combined.Add(new IndexEntry(pending.Index, -1, key, TableIndexCodec.EncodeIndexEntryValue(row.PrimaryKey.Span)));
        return combined;
    }

    /// <summary>正在构建的索引依赖稳定表结构；普通 DDL 和 TRUNCATE 必须等待它完成。</summary>
    internal void EnsureNoOnlineIndexBuild()
    {
        lock (_sync)
        {
            if (_onlineIndexBuild is not null)
                throw new InvalidOperationException($"table '{_schema.Name}' 正在 ONLINE 构建索引 '{_onlineIndexBuild.Index.Name}'，请完成后再执行结构变更。");
        }
    }

    /// <summary>读取独立元数据，只恢复游标；未完成索引绝不加入已发布 schema。</summary>
    private void LoadOnlineIndexBuildLocked()
    {
        byte[]? payload = _keyspace.Get(OnlineIndexStateKey);
        if (payload is null)
            return;
        var pending = OnlineIndexBuildState.Decode(payload);
        if (pending.Generation != Generation)
            throw new InvalidDataException("在线索引构建的存储 generation 已变化。");
        TableSchema baseSchema = _schema.TryGetIndex(pending.Index.Name) is { } published
            ? _schema.WithoutIndex(published.Name) : _schema;
        if (!pending.SchemaFingerprint.AsSpan().SequenceEqual(TableStoreMaintenanceFile.ComputeSchemaFingerprint(baseSchema)))
            throw new InvalidDataException("在线索引构建的源表结构指纹已变化。");
        _onlineIndexBuild = pending;
        if (_schema.TryGetIndex(pending.Index.Name) is { } active)
        {
            TableManager.EnsureOnlineIndexDefinition(active, pending.ToDefinition());
            if (!pending.Completed)
                throw new InvalidDataException("未完成在线索引被写入目录，拒绝暴露不完整索引。");
            CleanupPublishedOnlineIndexBuild(CancellationToken.None);
        }
    }

    /// <summary>在调用方持有三个短期锁时推进最多 16 行，索引键与新游标同一 WAL 原子提交。</summary>
    internal (int Rows, bool Completed, bool WaitingForCheckpoint) AdvanceOnlineIndexBuild(
        TableIndexDefinition definition, CancellationToken cancellationToken)
    {
        ThrowIfDisposedLocked();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_keyspace.TryEnableOrderedOverlayScans(100_000, cancellationToken))
            return (0, false, true);
        var pending = _onlineIndexBuild;
        if (pending is null)
        {
            var index = _schema.WithIndex(definition).TryGetIndex(definition.Name)!;
            pending = new OnlineIndexBuildState(index, Generation,
                TableStoreMaintenanceFile.ComputeSchemaFingerprint(_schema), [], false);
            _keyspace.ApplyConditionalBatch([KvBatchMutation.Put(OnlineIndexStateKey, pending.Encode())], [], cancellationToken);
            _onlineIndexBuild = pending;
        }
        else
        {
            if (!string.Equals(pending.Index.Name, definition.Name, StringComparison.Ordinal))
                throw new InvalidOperationException($"本表仍在构建索引 '{pending.Index.Name}'，请先完成该索引。");
            TableManager.EnsureOnlineIndexDefinition(pending.Index, definition);
        }
        if (pending.Completed)
            return (0, true, false);

        var rows = _keyspace.ScanRange([(byte)'r'], null, null,
            pending.AfterKey.Length == 0 ? null : pending.AfterKey, 16, cancellationToken);
        var mutations = new List<KvBatchMutation>(rows.Count + 1);
        foreach (var entry in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = TableRowCodec.Decode(_schema, entry.Value.Span);
            ReadOnlyMemory<byte> primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(entry.Key).ToArray();
            byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(pending.Index, values, _schema, primaryKey.Span);
            if (key is not null)
                mutations.Add(KvBatchMutation.Put(key, TableIndexCodec.EncodeIndexEntryValue(primaryKey.Span.ToArray())));
        }
        var next = pending with
        {
            AfterKey = rows.Count == 0 ? pending.AfterKey : rows[^1].Key.ToArray(),
            Completed = rows.Count < 16
        };
        mutations.Add(KvBatchMutation.Put(OnlineIndexStateKey, next.Encode()));
        _keyspace.ApplyConditionalBatch(mutations, [], cancellationToken);
        _onlineIndexBuild = next;
        return (rows.Count, next.Completed, false);
    }

    /// <summary>先同步完成索引的 WAL，再持久化并发布目录；清理进度失败时允许后续幂等重试。</summary>
    internal void PublishOnlineIndexBuild(
        Action<TableSchema> persistCatalog, Action<TableSchema> publishCatalog, CancellationToken cancellationToken)
    {
        var pending = _onlineIndexBuild ?? throw new InvalidOperationException("没有待发布的在线索引。");
        if (!pending.Completed)
            throw new InvalidOperationException("在线索引还未扫描完成。");
        cancellationToken.ThrowIfCancellationRequested();
        var updated = _schema.WithIndex(pending.ToDefinition());
        _keyspace.SyncWalForMaintenance();
        cancellationToken.ThrowIfCancellationRequested();
        persistCatalog(updated);
        // 目录文件已提交后，不再因取消回滚内存发布，否则磁盘和运行态会长期分叉。
        _schema = updated;
        _statistics = null;
        _statisticsLoadFailureReason = "schema_changed";
        publishCatalog(updated);
        CleanupPublishedOnlineIndexBuild(CancellationToken.None);
    }

    /// <summary>目录提交后只删除小型构建元数据；失败时留下已完成标记供重启或下一次调用清理。</summary>
    internal void CleanupPublishedOnlineIndexBuild(CancellationToken cancellationToken)
    {
        if (_onlineIndexBuild is not { } pending || _schema.TryGetIndex(pending.Index.Name) is null)
            return;
        _keyspace.ApplyConditionalBatch([KvBatchMutation.Delete(OnlineIndexStateKey)], [], cancellationToken);
        _onlineIndexBuild = null;
    }
}

/// <summary>在线索引声明、基线指纹和已提交游标，使用独立 KV 元数据格式保存。</summary>
internal sealed record OnlineIndexBuildState(
    TableIndex Index, long Generation, byte[] SchemaFingerprint, byte[] AfterKey, bool Completed)
{
    /// <summary>把持久索引还原为具有相同创建时间的 schema 声明。</summary>
    internal TableIndexDefinition ToDefinition() => new(Index.Name, Index.Columns, false, Index.CreatedAtUtcTicks);

    /// <summary>编码有版本的内部元数据，不修改已经发布的表或段文件格式。</summary>
    internal byte[] Encode()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(1);
        writer.Write(Generation);
        writer.Write(Index.Name);
        writer.Write(Index.CreatedAtUtcTicks);
        writer.Write(Index.Columns.Count);
        foreach (string column in Index.Columns)
            writer.Write(column);
        writer.Write(SchemaFingerprint);
        writer.Write(AfterKey.Length);
        writer.Write(AfterKey);
        writer.Write(Completed);
        return stream.ToArray();
    }

    /// <summary>严格限制元数据大小、列数和游标长度；损坏时拒绝续建而不猜测进度。</summary>
    internal static OnlineIndexBuildState Decode(byte[] payload)
    {
        if (payload.Length > 1024 * 1024)
            throw new InvalidDataException("在线索引元数据超过大小上限。");
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadInt32() != 1)
            throw new InvalidDataException("在线索引元数据版本不支持。");
        long generation = reader.ReadInt64();
        string name = reader.ReadString();
        long createdAt = reader.ReadInt64();
        int columnCount = reader.ReadInt32();
        if (columnCount is < 1 or > 1024)
            throw new InvalidDataException("在线索引元数据列数无效。");
        var columns = new string[columnCount];
        for (int index = 0; index < columnCount; index++)
            columns[index] = reader.ReadString();
        byte[] fingerprint = reader.ReadBytes(32);
        int cursorLength = reader.ReadInt32();
        if (fingerprint.Length != 32 || cursorLength is < 0 or > 65536)
            throw new InvalidDataException("在线索引元数据指纹或游标长度无效。");
        byte[] cursor = reader.ReadBytes(cursorLength);
        bool completed = reader.ReadBoolean();
        if (cursor.Length != cursorLength || (cursor.Length > 0 && cursor[0] != (byte)'r')
            || stream.Position != stream.Length)
            throw new InvalidDataException("在线索引元数据内容不完整。");
        return new(new TableIndex(name, columns, false, createdAt), generation, fingerprint, cursor, completed);
    }
}
