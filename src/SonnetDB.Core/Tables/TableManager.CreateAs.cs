using System.Diagnostics;
using System.Text;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Wal;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    /// <summary>供恢复测试在已同步的 CTAS 持久阶段建立真实子进程终止点。</summary>
    internal Action<string>? CreateAsPhaseTestHook { get; set; }

    /// <summary>完整构造独占 rowstore 后一次发布 CTAS；持久 catalog 在全体行就绪前不可见。</summary>
    internal void CreateAs(TableSchema schema, IReadOnlyList<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);
        if (schema.PrimaryKey.Count != 0 || schema.ForeignKeys.Count != 0 || schema.Indexes.Count != 0)
            throw new InvalidOperationException("CTAS 发布仅接受无键、外键和索引的派生 schema。");
        TableLockOperation.Run(RowLocks, () =>
        {
            TableLockOperation.Require(RowLocks, [TableRowLockManager.Resource.DatabaseExclusive], retain: false);
            lock (_schemaSync)
                lock (_sync)
                    CreateAsLocked(schema, rows, cancellationToken);
        });
    }

    private void CreateAsLocked(TableSchema schema, IReadOnlyList<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _nameAvailabilityGuard?.Invoke(schema.Name, "table");
        if (_catalog.Snapshot().Any(existing => string.Equals(existing.Name, schema.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"table '{schema.Name}' 已存在。");
        string journalPath = Path.Combine(_rootDirectory, TableCreationJournal.FileName);
        if (File.Exists(journalPath))
            throw new IOException("有尚未恢复的 CTAS 创建日志，须重启恢复后再创建。");
        string finalDirectory = TableDirectory(schema.Name);
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            throw new IOException("CTAS 最终 rowstore 路径已有未登记对象，拒绝覆盖或接管。");
        IReadOnlyList<TableSchema> previous = _catalog.Snapshot();
        TableSchema[] candidate = [.. previous, schema];
        Action publish = _catalog.PrepareReplacement(candidate);
        var journal = new TableCreationJournal(Guid.NewGuid(), schema.Name);
        bool publicationAttempted = false;
        bool published = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            journal.Prepare(journalPath);
            journal.CreateStage(_rootDirectory);
            CreateAsPhaseTestHook?.Invoke("stage-created");
            BuildCreateAsStore(schema, rows, journal.StageDirectory(_rootDirectory), cancellationToken);
            CreateAsPhaseTestHook?.Invoke("stage-durable");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            Directory.Move(journal.StageDirectory(_rootDirectory), finalDirectory);
            DirectoryFsync.FlushRequired(Path.GetDirectoryName(finalDirectory)!);
            DirectoryFsync.FlushRequired(_rootDirectory);
            CreateAsPhaseTestHook?.Invoke("rowstore-moved");
            // 完整 rowstore 的目录重命名已同步。以下原子 schema 替换才是提交决策。
            publicationAttempted = true;
            TableSchemaCodec.SaveWithRecoveryBarrier(SchemaPath, candidate);
            CreateAsPhaseTestHook?.Invoke("catalog-durable");
            AfterCatalogPersistedBeforePublishTestHook?.Invoke();
            publish();
            published = true;
            journal.Complete(_rootDirectory, finalDirectory);
        }
        catch (Exception failure)
        {
            try
            {
                if (published)
                    throw new IOException("CTAS 已发布，但创建日志回收未确认；提交结果须重启核对。", failure);
                if (publicationAttempted)
                    TableSchemaCodec.SaveWithRecoveryBarrier(SchemaPath, previous, ".ctas-rollback.tmp");
                TableCreationJournal? pending = TableCreationJournal.Read(journalPath);
                if (pending is not null)
                {
                    if (pending != journal)
                        throw new InvalidDataException("CTAS 恢复日志身份已经变化，拒绝清理。");
                    journal.RemoveOwnedDirectory(_rootDirectory, finalDirectory, staging: false);
                    journal.RemoveOwnedDirectory(_rootDirectory, journal.StageDirectory(_rootDirectory), staging: true);
                    journal.Complete(_rootDirectory, finalDirectory);
                }
            }
            catch (Exception recoveryFailure)
            {
                _transactionFailure = recoveryFailure;
                throw new IOException("CTAS 创建或回滚结果未确认，当前管理器停止访问；保留日志供重启恢复。",
                    new AggregateException(failure, recoveryFailure));
            }
            throw;
        }
    }

    private void BuildCreateAsStore(TableSchema schema, IReadOnlyList<IReadOnlyList<object?>> rows,
        string directory, CancellationToken cancellationToken)
    {
        if (rows.Count > 100_000) throw new InvalidOperationException("CTAS 最多物化 100000 行。");
        long started = Stopwatch.GetTimestamp();
        using var keyspace = KvKeyspace.Open("table." + schema.Name, directory, _kvOptions, _diskReadBudget);
        using var store = new TableStore(schema, keyspace, _statisticsRefreshBudget);
        long bytes = 0;
        for (int index = 0; index < rows.Count; index += 256)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqlExecutor.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                throw new TimeoutException("CTAS 暂存 rowstore 写入超过三十秒预算。");
            int count = Math.Min(256, rows.Count - index);
            var batch = new IReadOnlyList<object?>[count];
            for (int offset = 0; offset < count; offset++)
            {
                IReadOnlyList<object?> row = rows[index + offset];
                bytes = checked(bytes + SqlTemporaryTableWorkspace.EstimateRow(row));
                if (bytes > 64L * 1024 * 1024) throw new InvalidOperationException("CTAS 超出 64MiB 字节预算。");
                batch[offset] = row;
            }
            store.InsertMany(batch);
        }
        store.SyncTransactionWal();
        DirectoryFsync.FlushRequired(directory);
    }

    private void RecoverCreateAsBeforeOpeningStores()
    {
        string path = Path.Combine(_rootDirectory, TableCreationJournal.FileName);
        TableCreationJournal? journal = TableCreationJournal.Read(path);
        if (journal is null)
        {
            string temporary = path + ".tmp";
            if (!File.Exists(temporary)) return;
            try
            {
                // move 之前还没有 stage/final 写入。只移除完整、可验证的本组件 v1 日志；
                // 中途写断或未知内容保留证据，普通数据库仍能打开，后续 CTAS 显式拒绝覆盖。
                TableCreationJournal? preparation = TableCreationJournal.Read(temporary);
                if (preparation is null || Directory.Exists(preparation.StageDirectory(_rootDirectory))) return;
            }
            catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
            {
                return;
            }
            File.Delete(temporary);
            DirectoryFsync.FlushRequired(_rootDirectory);
            return;
        }
        string finalDirectory = TableDirectory(journal.TableName);
        if (_catalog.TryGet(journal.TableName) is not null)
        {
            if (!Directory.Exists(finalDirectory) || Directory.Exists(journal.StageDirectory(_rootDirectory)))
                throw new InvalidDataException("已提交 CTAS 的 rowstore 不完整，拒绝创建空表替代。");
            journal.ValidateOwner(finalDirectory);
        }
        else
        {
            journal.RemoveOwnedDirectory(_rootDirectory, finalDirectory, staging: false);
            journal.RemoveOwnedDirectory(_rootDirectory, journal.StageDirectory(_rootDirectory), staging: true);
        }
        journal.Complete(_rootDirectory, finalDirectory);
    }
}
