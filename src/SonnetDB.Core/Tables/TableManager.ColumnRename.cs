using SonnetDB.Exceptions;
using SonnetDB.Sql.Execution;
using SonnetDB.Wal;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    /// <summary>仅供崩溃测试在列重命名的目录提交点之前暂停。</summary>
    internal Action? BeforeColumnRenameCatalogPersistTestHook { get; set; }

    /// <summary>
    /// 重命名只修改元数据，列序号、主键和二级索引字节保持一致；整个 schema 文件是唯一持久提交点。
    /// </summary>
    private void ApplyColumnRenameLocked(TableSchema current, string oldName, string newName)
    {
        TableSchema renamed = current.WithRenamedColumn(oldName, newName);
        IReadOnlyList<TableSchema> previousSchemas = _catalog.Snapshot();
        TableSchema[] candidateSchemas = previousSchemas.Select(schema =>
            string.Equals(schema.Name, current.Name, StringComparison.Ordinal)
                ? renamed
                : schema.WithRenamedPrincipalColumn(current.Name, oldName, newName)).ToArray();
        var changed = candidateSchemas.Zip(previousSchemas)
            .Where(static pair => !ReferenceEquals(pair.First, pair.Second))
            .Select(pair => (Previous: pair.Second, Updated: pair.First, Store: OpenStoreLocked(pair.Second)))
            .ToArray();

        // 分配候选快照必须早于任何修改；发布时整套 FK 与主键在同一次 Volatile.Write 中切换。
        Action publish = _catalog.PrepareReplacement(candidateSchemas);
        var lockedStores = new List<TableStore>(changed.Length);
        bool persisted = false;
        int metadataApplied = 0;
        try
        {
            foreach (var entry in changed)
            {
                Monitor.Enter(entry.Store.SynchronizationRoot);
                lockedStores.Add(entry.Store);
                entry.Store.EnsureNoOnlineIndexBuild();
            }
            SqlExecutor.ThrowIfCancellationRequested();
            foreach (var entry in changed)
            {
                entry.Store.ApplyMetadataSchema(entry.Updated);
                metadataApplied++;
            }

            BeforeColumnRenameCatalogPersistTestHook?.Invoke();
            TableSchemaCodec.Save(SchemaPath, candidateSchemas);
            persisted = true;
            DirectoryFsync.FlushRequired(_rootDirectory);
            AfterCatalogPersistedBeforePublishTestHook?.Invoke();
            publish();
        }
        catch (Exception renameFailure)
        {
            try
            {
                if (persisted)
                {
                    TableSchemaCodec.Save(SchemaPath, previousSchemas, ".rollback.tmp");
                    DirectoryFsync.FlushRequired(_rootDirectory);
                }
                for (int index = 0; index < metadataApplied; index++)
                    changed[index].Store.ApplyMetadataSchema(changed[index].Previous);
            }
            catch (Exception rollbackFailure)
            {
                // 替换或补偿的持久化结果未知时禁止继续写；重开按唯一的完整 schema 文件恢复旧或新合同。
                _transactionFailure = rollbackFailure;
                foreach (TableStore store in _stores.Values)
                    store.InvalidateTransaction(rollbackFailure);
                throw new TableTransactionRecoveryException(
                    "关系表列重命名回滚失败，必须关闭并重新打开数据库确认旧或新 schema。",
                    new AggregateException(renameFailure, rollbackFailure));
            }
            throw;
        }
        finally
        {
            for (int index = lockedStores.Count - 1; index >= 0; index--)
                Monitor.Exit(lockedStores[index].SynchronizationRoot);
        }
    }
}
