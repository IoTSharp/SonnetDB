using SonnetDB.Kv;
using SonnetDB.Routines;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

internal sealed class TableSchemaMigrationBudget
{
    internal const int MaxRows = 100_000;
    internal const long MaxWorkingBytes = 256L * 1024 * 1024;
    private int _rows;
    private long _bytes;

    internal void Reserve(long bytes, bool row = false)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        if (bytes < 0 || bytes > MaxWorkingBytes - _bytes || row && _rows == MaxRows)
            throw new TableConstraintException(TableConstraintException.SchemaEvolutionUnsupported,
                "schema_migration", null,
                $"主键迁移最多支持 {MaxRows} 行、{MaxWorkingBytes} 字节工作集，请先缩小迁移范围。");
        _bytes += bytes;
        if (row) _rows++;
    }
}

public sealed partial class TableStore
{
    /// <summary>仅供进程终止测试观测跨页 raw 应用的中间边界。</summary>
    internal static Action<int>? SchemaMigrationPageTestHook { get; set; }
    internal sealed record SchemaMigrationRow(byte[] OriginalPrimaryKey, TableRow UpdatedRow);

    internal sealed record SchemaMigrationPlan(
        TableSchema OriginalSchema,
        TableSchema UpdatedSchema,
        TableSchemaMigrationUndo Undo,
        IReadOnlyList<KvBatchMutation> Mutations,
        IReadOnlyList<SchemaMigrationRow> Rows);

    // 管理器从准备到持久决定和发布一直持有所有参与表的锁；本方法只准备，不修改 KV/schema。
    internal SchemaMigrationPlan PrepareSchemaMigration(
        TableSchema updated,
        Func<TableSchema, TableRow, IReadOnlyList<object?>> transform,
        Action<TableSchema, IReadOnlyList<object?>> validate,
        Func<byte[]> allocateRowIdentity,
        TableSchemaMigrationBudget budget)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(allocateRowIdentity);
        ArgumentNullException.ThrowIfNull(budget);
        lock (_sync)
        {
            ThrowIfDisposedLocked();
            EnsureNoOnlineIndexBuild();
            var before = new Dictionary<byte[], byte[]?>(KvKeyComparer.Instance);
            var after = new Dictionary<byte[], byte[]?>(KvKeyComparer.Instance);
            var rows = new List<SchemaMigrationRow>();
            var primaryKeys = new HashSet<byte[]>(KvKeyComparer.Instance);
            var uniqueKeys = new HashSet<byte[]>(KvKeyComparer.Instance);
            CancellationToken cancellationToken = RoutineExecutionContext.Current?.Options.CancellationToken
                ?? SqlQueryResources.Current?.CancellationToken ?? default;
            _keyspace.ReadSchemaMigrationPrefix((byte)'r',
                (keyLength, valueLength) => budget.Reserve(512L + keyLength * 4L + valueLength * 3L, row: true),
                (oldKey, oldValue) =>
                {
                    byte[] originalPrimaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(oldKey).ToArray();
                    var original = new TableRow(TableRowCodec.Decode(_schema, oldValue), originalPrimaryKey);
                    var values = transform(_schema, original);
                    ValidateRow(updated, values);
                    validate(updated, values);
                    byte[] primaryKey = updated.PrimaryKey.Count != 0 ? TableKeyCodec.EncodePrimaryKey(updated, values)
                        : _schema.PrimaryKey.Count == 0 ? originalPrimaryKey : allocateRowIdentity();
                    if (!primaryKeys.Add(primaryKey))
                        throw new TableConstraintException(TableConstraintException.UniqueViolation,
                            updated.Name, "PRIMARY KEY", $"table '{updated.Name}' 的迁移目标主键存在重复。");
                    byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
                    byte[] payload = TableRowCodec.Encode(updated, values);
                    _keyspace.ValidateWrite(rowKey, payload);
                    budget.Reserve(128L + rowKey.Length * 3L + payload.Length);
                    before.Add(oldKey, oldValue);
                    after.TryAdd(oldKey, null);
                    after[rowKey] = payload;
                    rows.Add(new SchemaMigrationRow(originalPrimaryKey, new TableRow(values.ToArray(), primaryKey)));

                    foreach (var index in updated.Indexes)
                    {
                        byte[]? indexKey = TableIndexCodec.TryEncodeIndexEntryKey(index, values, updated, primaryKey);
                        if (indexKey is null) continue;
                        byte[] indexValue = TableIndexCodec.EncodeIndexEntryValue(index, updated, values, primaryKey);
                        _keyspace.ValidateWrite(indexKey, indexValue);
                        if (index.IsUnique && !uniqueKeys.Add(indexKey))
                            throw new TableConstraintException(TableConstraintException.UniqueViolation,
                                updated.Name, index.Name, $"索引 '{index.Name}' 的迁移目标值存在重复。");
                        budget.Reserve(128L + indexKey.Length * 3L + indexValue.Length);
                        after[indexKey] = indexValue;
                    }
                }, cancellationToken);

            _keyspace.ReadSchemaMigrationPrefix((byte)'i',
                (keyLength, valueLength) => budget.Reserve(128L + keyLength * 3L + valueLength),
                (key, value) =>
                {
                    before.Add(key, value);
                    after.TryAdd(key, null);
                }, cancellationToken);

            // schema 变化使统计失效；高水位与 ROWVERSION 值不经 DML 分配或递增。
            budget.Reserve(128L);
            byte[]? statisticsState = _keyspace.ReadSchemaMigrationValue(_statisticsStateKey,
                length => budget.Reserve(length), cancellationToken);
            before[_statisticsStateKey] = statisticsState;
            after[_statisticsStateKey] = null;
            foreach (byte[] key in after.Keys)
            {
                if (!before.ContainsKey(key))
                {
                    budget.Reserve(64L + key.Length);
                    before.Add(key, null);
                }
            }
            if (before.Count > TableSchemaMigrationJournal.MaxEntries)
                throw new InvalidOperationException("主键迁移恢复条目超过固定上限。");
            var undo = new TableSchemaMigrationUndo(_schema.Name, Generation,
                TableStoreMaintenanceFile.ComputeSchemaFingerprint(_schema),
                before.Select(static item => new TableSchemaMigrationEntry(item.Key, item.Value)).ToArray());
            return new SchemaMigrationPlan(_schema, updated, undo,
                after.Select(static item => new KvBatchMutation(item.Key, item.Value)).ToArray(), rows);
        }
    }

    internal void ApplySchemaMigrationRaw(SchemaMigrationPlan plan, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_schema, plan.OriginalSchema) || Generation != plan.Undo.Generation)
                throw new InvalidOperationException("主键迁移准备后 schema 或 generation 已变化。");
            ApplySchemaMigrationMutations(_keyspace, plan.Mutations, cancellationToken);
        }
    }

    internal void PublishSchemaMigration(SchemaMigrationPlan plan)
    {
        lock (_sync)
        {
            _schema = plan.UpdatedSchema;
            _rowCount = plan.Rows.Count;
            _statistics = null;
            _statisticsLoadFailureReason = "schema_changed";
        }
    }

    internal void RestoreSchemaMigrationRaw(TableSchemaMigrationUndo undo)
    {
        lock (_sync)
        {
            if (Generation != undo.Generation)
                throw new InvalidDataException($"table '{undo.TableName}' 的主键迁移恢复 generation 不匹配。");
            ApplySchemaMigrationMutations(_keyspace, undo.Entries
                .Select(static entry => new KvBatchMutation(entry.Key, entry.PreviousValue)).ToArray());
        }
    }

    internal static void ApplySchemaMigrationMutations(
        KvKeyspace keyspace, IReadOnlyList<KvBatchMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        // 每页可按底层 WAL 预算继续拆分；跨页原子性由已经 fsync 的独立 DDL undo 保证。
        // 前向迁移传递 SQL 令牌；恢复用 None，但同一底层重载仍保留检查点等待期限。
        using var budgetScope = keyspace.EnterIndexRebuildBudgetScope(cancellationToken);
        int pageSize = keyspace.GetIndexRebuildBatchEntryLimit(IndexRepairMutationPageSize);
        for (int index = 0; index < mutations.Count; index += pageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(pageSize, mutations.Count - index);
            var page = new KvBatchMutation[count];
            for (int offset = 0; offset < count; offset++) page[offset] = mutations[index + offset];
            keyspace.ApplyIndexRebuildBatch(page, cancellationToken);
            SchemaMigrationPageTestHook?.Invoke(index + count);
        }
        keyspace.SyncWalForMaintenance(cancellationToken);
    }
}
