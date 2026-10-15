using SonnetDB.Kv;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    /// <summary>仅供重复崩溃测试观测恢复阶段；参数为 tables 目录和阶段名。</summary>
    internal static Action<string, string>? SchemaMigrationRecoveryPhaseTestHook { get; set; }

    private string SchemaMigrationJournalPath => Path.Combine(_rootDirectory, TableSchemaMigrationJournal.FileName);

    // 必须先于任何 TableStore 的构造或索引重建。raw undo 无需选择旧/新 schema 解码当前行。
    private void RecoverSchemaMigrationBeforeOpeningStores()
    {
        TableSchemaMigrationRecord? record = TableSchemaMigrationJournal.Read(SchemaMigrationJournalPath);
        if (record is null) return;
        if (record.IsComplete)
        {
            // Read 可见不等于已经 fsync。先确认上次结果不确定但完整可见的决定，再允许任何新写。
            TableSchemaMigrationJournal.Complete(SchemaMigrationJournalPath);
            return;
        }
        if (TableSchemaCodec.ReadFormatVersion(SchemaPath) < 13)
            throw new InvalidDataException("主键迁移日志缺少持久化 v13 恢复屏障，拒绝覆盖可能由旧引擎写入的数据。");

        using var originalCatalog = new MemoryStream(record.OriginalCatalog, writable: false);
        IReadOnlyList<TableSchema> originalSchemas = TableSchemaCodec.Load(originalCatalog);
        if (originalCatalog.Position != originalCatalog.Length)
            throw new InvalidDataException("主键迁移原目录包含未识别的尾部数据。");
        var originalByName = originalSchemas.ToDictionary(static schema => schema.Name, StringComparer.Ordinal);
        foreach (var undo in record.Tables)
        {
            if (!originalByName.TryGetValue(undo.TableName, out TableSchema? original)
                || !undo.SchemaFingerprint.AsSpan().SequenceEqual(
                    TableStoreMaintenanceFile.ComputeSchemaFingerprint(original)))
                throw new InvalidDataException($"table '{undo.TableName}' 的主键迁移恢复 schema 指纹不匹配。");
            if (!Directory.Exists(TableDirectory(undo.TableName)))
                throw new InvalidDataException($"table '{undo.TableName}' 的主键迁移恢复 rowstore 不存在。");
        }

        var opened = new List<(TableSchemaMigrationUndo Undo, KvKeyspace Keyspace)>(record.Tables.Count);
        Exception? recoveryFailure = null;
        using var recoveryBudget = new KvDiskReadBudget(_kvOptions.MaxConcurrentStateReads,
            _kvOptions.MaxQueuedStateReads, TimeSpan.FromMilliseconds(_kvOptions.StateReadWaitTimeoutMilliseconds));
        try
        {
            // 全部 generation 先校验，任一错配都不得先修改已成功打开的表。
            foreach (var undo in record.Tables.OrderBy(static table => table.TableName, StringComparer.Ordinal))
            {
                var keyspace = KvKeyspace.Open("table." + undo.TableName, TableDirectory(undo.TableName),
                    _kvOptions, recoveryBudget);
                opened.Add((undo, keyspace));
                if (keyspace.Generation != undo.Generation)
                    throw new InvalidDataException($"table '{undo.TableName}' 的主键迁移恢复 generation 不匹配。");
            }
            SchemaMigrationRecoveryPhaseTestHook?.Invoke(_rootDirectory, "validated");
            foreach (var entry in opened)
            {
                TableStore.ApplySchemaMigrationMutations(entry.Keyspace,
                    entry.Undo.Entries.Select(static item => new KvBatchMutation(item.Key, item.PreviousValue)).ToArray());
                SchemaMigrationRecoveryPhaseTestHook?.Invoke(_rootDirectory, "after-table:" + entry.Undo.TableName);
            }

            // 即使 raw 已完整恢复，complete 前再次崩溃仍会重放 undo，因此永远不能恢复旧版本头。
            TableSchemaCodec.SaveWithRecoveryBarrier(SchemaPath, originalSchemas, ".ddl-recovery.tmp");
            SchemaMigrationRecoveryPhaseTestHook?.Invoke(_rootDirectory, "after-catalog");
            TableSchemaMigrationJournal.Complete(SchemaMigrationJournalPath);
            SchemaMigrationRecoveryPhaseTestHook?.Invoke(_rootDirectory, "after-complete");
        }
        catch (Exception failure)
        {
            recoveryFailure = failure;
            throw;
        }
        finally
        {
            // 所有 raw 句柄释放后，构造函数才能使用已恢复的 schema 创建正常 TableStore。
            List<Exception>? disposeFailures = null;
            foreach (var entry in opened)
            {
                try { entry.Keyspace.Dispose(); }
                catch (Exception failure) { (disposeFailures ??= []).Add(failure); }
            }
            if (disposeFailures is not null)
            {
                if (recoveryFailure is not null) disposeFailures.Insert(0, recoveryFailure);
                throw new AggregateException("主键迁移恢复完成后关闭 rowstore 失败。", disposeFailures);
            }
        }
    }
}
