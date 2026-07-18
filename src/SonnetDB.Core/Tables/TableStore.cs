using SonnetDB.Kv;

namespace SonnetDB.Tables;

/// <summary>
/// 单个关系表的 KV-backed 行存储。
/// </summary>
public sealed class TableStore : IDisposable
{
    private const int MaintenanceKeyPageSize = 256;
    private const int IndexRebuildRowPageSize = 4;
    private readonly object _sync = new();
    private readonly KvKeyspace _keyspace;
    private TableSchema _schema;
    private int _rowCount;
    private long _fullScanCount;
    private long _primaryKeyLookupCount;
    private bool _disposed;

    internal TableStore(TableSchema schema, KvKeyspace keyspace)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keyspace);
        _schema = schema;
        _keyspace = keyspace;
        byte[] schemaFingerprint = TableStoreMaintenanceFile.ComputeSchemaFingerprint(schema);
        if (!TableStoreMaintenanceFile.IsLegacyMigrationComplete(
            keyspace.RootDirectory,
            keyspace.Generation,
            schemaFingerprint))
        {
            MigrateLegacyRowsLocked();
            keyspace.SyncWalForMaintenance();
            TableStoreMaintenanceFile.MarkLegacyMigrationComplete(
                keyspace.RootDirectory,
                keyspace.Generation,
                schemaFingerprint);
        }

        if (!TableStoreMaintenanceFile.ConsumeCleanIndexes(
            keyspace.RootDirectory,
            keyspace.Generation,
            keyspace.LastSequence,
            schemaFingerprint))
        {
            RebuildIndexesLocked();
        }
        _rowCount = _keyspace.CountPrefix([(byte)'r']);
    }

    /// <summary>表 schema。</summary>
    public TableSchema Schema
    {
        get
        {
            lock (_sync)
                return _schema;
        }
    }

    /// <summary>当前 generation 中的关系行数。</summary>
    public int RowCount
    {
        get
        {
            lock (_sync)
                return _rowCount;
        }
    }

    /// <summary>底层 rowstore 当前 generation。</summary>
    public long Generation => _keyspace.Generation;

    /// <summary>公开全表扫描累计次数，供访问计划回归测试观测。</summary>
    internal long FullScanCount => Interlocked.Read(ref _fullScanCount);

    /// <summary>公开主键点读累计次数，供访问计划回归测试观测。</summary>
    internal long PrimaryKeyLookupCount => Interlocked.Read(ref _primaryKeyLookupCount);

    /// <summary>底层 rowstore generation 旧文件回收状态。</summary>
    public KvCleanupStatus GetCleanupStatus() => _keyspace.GetCleanupStatus();

    /// <summary>
    /// 插入或覆盖一行。
    /// </summary>
    /// <param name="values">按 schema 列顺序排列的行值。</param>
    public void Upsert(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            var schema = _schema;
            ValidateRow(schema, values);
            byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
            byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
            var oldRow = TryGetByRowKeyLocked(schema, rowKey);
            byte[] value = TableRowCodec.Encode(schema, values);
            var operation = TableMutationOperation.Update(rowKey, oldRow, new TableRow(values.ToArray(), primaryKey));
            ValidateUniqueIndexesLocked(schema, operation);
            ApplyMutationLocked(schema, operation, value);
            if (oldRow is null)
                _rowCount++;
        }
    }

    /// <summary>
    /// 插入一行；若主键已存在则抛出异常。
    /// </summary>
    /// <param name="values">按 schema 列顺序排列的行值。</param>
    public void Insert(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            var schema = _schema;
            ValidateRow(schema, values);
            byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
            byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
            if (_keyspace.Get(rowKey) is not null)
                throw new InvalidOperationException($"table '{schema.Name}' 中主键已存在。");

            byte[] value = TableRowCodec.Encode(schema, values);
            var newRow = new TableRow(values.ToArray(), primaryKey);
            var operation = TableMutationOperation.Insert(rowKey, newRow);
            ValidateUniqueIndexesLocked(schema, operation);
            ApplyMutationLocked(schema, operation, value);
            _rowCount++;
        }
    }

    /// <summary>
    /// 批量插入多行；任意一行失败时回滚本批已经写入的行和索引。
    /// </summary>
    /// <param name="rows">按 schema 列顺序排列的行值集合。</param>
    /// <returns>成功插入的行数。</returns>
    public int InsertMany(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return 0;

        lock (_sync)
        {
            var schema = _schema;
            var operations = new List<TableMutationOperation>(rows.Count);
            var pendingRowKeys = new HashSet<string>(StringComparer.Ordinal);
            var pendingUniqueKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var values in rows)
            {
                ValidateRow(schema, values);
                byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
                byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
                if (!pendingRowKeys.Add(Convert.ToHexString(rowKey)))
                    throw new InvalidOperationException($"table '{schema.Name}' 中同一批 INSERT 存在重复主键。");
                if (_keyspace.Get(rowKey) is not null)
                    throw new InvalidOperationException($"table '{schema.Name}' 中主键已存在。");

                var row = new TableRow(values.ToArray(), primaryKey);
                foreach (var index in schema.Indexes.Where(static i => i.IsUnique))
                {
                    byte[]? indexKey = TableIndexCodec.TryEncodeIndexEntryKey(index, row.Values, schema, primaryKey);
                    if (indexKey is null)
                        continue;
                    string uniqueKey = index.Name + ":" + Convert.ToHexString(indexKey);
                    if (!pendingUniqueKeys.Add(uniqueKey))
                        throw UniqueViolation(schema, index);
                    byte[] prefix = TableIndexCodec.TryEncodeIndexPrefix(index, row.Values, schema)!;
                    foreach (var entry in _keyspace.ScanPrefix(prefix))
                    {
                        if (entry.Key.Span.SequenceEqual(indexKey))
                            throw UniqueViolation(schema, index);
                    }
                }

                operations.Add(TableMutationOperation.Insert(rowKey, row));
            }

            var applied = new List<RollbackAction>(operations.Count * Math.Max(1, schema.Indexes.Count + 1));
            try
            {
                foreach (var operation in operations)
                    ApplyMutationLocked(schema, operation, TableRowCodec.Encode(schema, operation.NewRow!.Values), applied);
                _rowCount = checked(_rowCount + operations.Count);
            }
            catch
            {
                RollbackAppliedLocked(applied);
                throw;
            }

            return operations.Count;
        }
    }

    /// <summary>
    /// 应用一组 upsert / delete 变更；任意变更失败时回滚已经应用的变更。
    /// </summary>
    /// <param name="mutations">变更集合。</param>
    /// <returns>实际写入或删除的行数。</returns>
    public int ApplyBatch(IReadOnlyList<TableRowMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0)
            return 0;

        lock (_sync)
        {
            var batch = PrepareBatchLocked(mutations);
            try
            {
                ApplyPreparedBatchLocked(batch);
            }
            catch
            {
                RollbackAppliedLocked(batch.Applied);
                throw;
            }

            return batch.AffectedRows;
        }
    }

    internal PreparedTableBatch PrepareBatch(IReadOnlyList<TableRowMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        lock (_sync)
            return PrepareBatchLocked(mutations);
    }

    internal int ApplyPreparedBatch(PreparedTableBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_sync)
        {
            try
            {
                ApplyPreparedBatchLocked(batch);
                return batch.AffectedRows;
            }
            catch
            {
                RollbackAppliedLocked(batch.Applied);
                throw;
            }
        }
    }

    internal void RollbackPreparedBatch(PreparedTableBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_sync)
        {
            RollbackAppliedLocked(batch.Applied);
            if (batch.RowCountApplied)
            {
                _rowCount = checked(_rowCount - batch.RowCountDelta);
                batch.RowCountApplied = false;
            }
        }
    }

    /// <summary>
    /// 按主键读取一行。
    /// </summary>
    /// <param name="primaryKeyValues">主键值。</param>
    /// <returns>找到时返回行；否则返回 null。</returns>
    public TableRow? GetByPrimaryKey(IReadOnlyList<object?> primaryKeyValues)
    {
        Interlocked.Increment(ref _primaryKeyLookupCount);
        lock (_sync)
        {
            var schema = _schema;
            byte[] key = TableKeyCodec.EncodePrimaryKeyValues(schema, primaryKeyValues);
            byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(key);
            byte[]? payload = _keyspace.Get(rowKey);
            return payload is null
                ? null
                : new TableRow(TableRowCodec.Decode(schema, payload), key);
        }
    }

    /// <summary>
    /// 删除主键对应的行。
    /// </summary>
    /// <param name="primaryKeyValues">主键值。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool DeleteByPrimaryKey(IReadOnlyList<object?> primaryKeyValues)
    {
        lock (_sync)
        {
            var schema = _schema;
            byte[] key = TableKeyCodec.EncodePrimaryKeyValues(schema, primaryKeyValues);
            byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(key);
            var oldRow = TryGetByRowKeyLocked(schema, rowKey);
            if (oldRow is null)
                return false;

            ApplyMutationLocked(schema, TableMutationOperation.Delete(rowKey, oldRow), encodedNewRow: null);
            _rowCount--;
            return true;
        }
    }

    /// <summary>切换到底层 KV 的新 generation，并返回被清空的关系行数。</summary>
    internal (int Rows, KvClearResult Clear) Truncate()
    {
        lock (_sync)
        {
            int rows = _rowCount;
            KvClearResult result = _keyspace.Clear();
            _rowCount = 0;
            return (rows, result);
        }
    }

    internal int CleanupPendingFiles(int maxFiles) => _keyspace.CleanupPendingFiles(maxFiles);

    internal KvCleanupRoundResult CleanupPendingFilesWithResult(int maxFiles)
        => _keyspace.CleanupPendingFilesWithResult(maxFiles);

    /// <summary>
    /// 扫描当前表的所有行，按主键字节序升序返回。
    /// </summary>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<TableRow> Scan(int? limit = null)
    {
        Interlocked.Increment(ref _fullScanCount);
        lock (_sync)
        {
            var schema = _schema;
            var entries = _keyspace.ScanPrefix(new byte[] { (byte)'r' }, limit ?? int.MaxValue);
            var rows = new List<TableRow>(entries.Count);
            foreach (var entry in entries)
            {
                var primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(entry.Key);
                rows.Add(new TableRow(TableRowCodec.Decode(schema, entry.Value.Span), primaryKey.ToArray()));
            }

            return rows;
        }
    }

    /// <summary>
    /// 按二级索引前缀读取候选行。
    /// </summary>
    /// <param name="index">索引声明。</param>
    /// <param name="indexColumnValues">与索引列数量一致的等值谓词。</param>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<TableRow> GetByIndex(TableIndex index, IReadOnlyList<object?> indexColumnValues, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(indexColumnValues);
        lock (_sync)
        {
            var schema = _schema;
            if (indexColumnValues.Count != index.Columns.Count)
                throw new ArgumentException("索引值数量与索引列数量不一致。", nameof(indexColumnValues));

            byte[]? prefix = TableIndexCodec.EncodeLookupPrefix(index, indexColumnValues, schema);
            if (prefix is null)
                return [];
            var entries = _keyspace.ScanPrefix(prefix, limit ?? int.MaxValue);
            var rows = new List<TableRow>(entries.Count);
            foreach (var entry in entries)
            {
                byte[] primaryKey = entry.Value.Span.ToArray();
                byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
                byte[]? payload = _keyspace.Get(rowKey);
                if (payload is null)
                    continue;
                rows.Add(new TableRow(TableRowCodec.Decode(schema, payload), primaryKey));
            }

            return rows;
        }
    }

    internal void ApplySchema(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
        {
            var previous = _schema;
            _schema = schema;
            try
            {
                RebuildIndexesLocked();
            }
            catch
            {
                _schema = previous;
                RebuildIndexesLocked();
                throw;
            }
        }
    }

    internal Action ApplySchemaTransform(
        TableSchema schema,
        Func<TableSchema, TableRow, IReadOnlyList<object?>> transform)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(transform);
        lock (_sync)
        {
            var previous = _schema;
            var originalPayloads = _keyspace.ScanPrefix(new byte[] { (byte)'r' }, int.MaxValue)
                .Select(entry => (Key: entry.Key.ToArray(), Value: entry.Value.ToArray()))
                .ToArray();
            var transformedRows = new List<(byte[] Key, byte[] Value)>(originalPayloads.Length);

            try
            {
                foreach (var entry in originalPayloads)
                {
                    var primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(entry.Key).ToArray();
                    var row = new TableRow(TableRowCodec.Decode(previous, entry.Value.AsSpan()), primaryKey);
                    var values = transform(previous, row);
                    ValidateRow(schema, values);

                    var transformedPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
                    if (!transformedPrimaryKey.AsSpan().SequenceEqual(primaryKey))
                        throw new InvalidOperationException("ALTER TABLE 当前不支持改变 PRIMARY KEY 值或列定义。");

                    transformedRows.Add((entry.Key, TableRowCodec.Encode(schema, values)));
                }

                foreach (var row in transformedRows)
                    _keyspace.Put(row.Key, row.Value);

                _schema = schema;
                RebuildIndexesLocked();
                return () => RestoreSchemaTransform(previous, originalPayloads);
            }
            catch
            {
                foreach (var entry in originalPayloads)
                    _keyspace.Put(entry.Key, entry.Value);
                _schema = previous;
                RebuildIndexesLocked();
                throw;
            }
        }
    }

    private void RestoreSchemaTransform(TableSchema schema, IReadOnlyList<(byte[] Key, byte[] Value)> payloads)
    {
        lock (_sync)
        {
            foreach (var current in _keyspace.ScanPrefix(new byte[] { (byte)'r' }, int.MaxValue))
                _keyspace.Delete(current.Key.Span);
            foreach (var entry in payloads)
                _keyspace.Put(entry.Key, entry.Value);
            _schema = schema;
            RebuildIndexesLocked();
        }
    }

    internal void Compact() => _keyspace.Compact();

    internal long CreateSnapshot() => _keyspace.CreateSnapshot();

    /// <summary>
    /// 关闭底层 KV keyspace，并在 WAL 成功同步后发布索引干净关闭令牌。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            long generation = _keyspace.Generation;
            long sequence = _keyspace.LastSequence;
            byte[] schemaFingerprint = TableStoreMaintenanceFile.ComputeSchemaFingerprint(_schema);
            _keyspace.Dispose();
            TableStoreMaintenanceFile.MarkIndexesClean(
                _keyspace.RootDirectory,
                generation,
                sequence,
                schemaFingerprint);
            _disposed = true;
        }
    }

    private void ValidateRow(TableSchema schema, IReadOnlyList<object?> values)
    {
        if (values.Count != schema.Columns.Count)
            throw new ArgumentException("行值数量必须与表 schema 列数量一致。", nameof(values));

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var column = schema.Columns[i];
            if (values[i] is null && !column.IsNullable)
                throw new InvalidOperationException($"列 '{column.Name}' 不允许为 NULL。");
        }
    }

    private TableRow? TryGetByRowKeyLocked(TableSchema schema, ReadOnlySpan<byte> rowKey)
    {
        byte[]? payload = _keyspace.Get(rowKey);
        if (payload is null)
            return null;

        var primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(rowKey.ToArray()).ToArray();
        return new TableRow(TableRowCodec.Decode(schema, payload), primaryKey);
    }

    private void ValidateUniqueIndexesLocked(TableSchema schema, TableMutationOperation operation)
    {
        if (operation.NewRow is null)
            return;

        foreach (var index in schema.Indexes.Where(static i => i.IsUnique))
        {
            byte[]? prefix = TableIndexCodec.TryEncodeIndexPrefix(index, operation.NewRow.Values, schema);
            if (prefix is null)
                continue;
            foreach (var entry in _keyspace.ScanPrefix(prefix))
            {
                byte[]? indexKey = TableIndexCodec.TryEncodeIndexEntryKey(
                    index,
                    operation.NewRow.Values,
                    schema,
                    operation.NewRow.PrimaryKey.Span);
                if (indexKey is null)
                    continue;
                if (!entry.Key.Span.SequenceEqual(indexKey))
                    continue;

                if (operation.OldRow is not null && entry.Value.Span.SequenceEqual(operation.OldRow.PrimaryKey.Span))
                    continue;

                throw UniqueViolation(schema, index);
            }
        }
    }

    private static void ValidatePendingUniqueIndexes(
        TableSchema schema,
        TableRow row,
        Dictionary<string, string> pendingUniqueKeys)
    {
        string primaryKeyText = Convert.ToHexString(row.PrimaryKey.Span);
        foreach (var index in schema.Indexes.Where(static i => i.IsUnique))
        {
            byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(index, row.Values, schema, row.PrimaryKey.Span);
            if (key is null)
                continue;
            string scopedKey = index.Name + ":" + Convert.ToHexString(key);
            if (pendingUniqueKeys.TryGetValue(scopedKey, out var existingPrimaryKey)
                && !string.Equals(existingPrimaryKey, primaryKeyText, StringComparison.Ordinal))
            {
                throw UniqueViolation(schema, index);
            }

            pendingUniqueKeys[scopedKey] = primaryKeyText;
        }
    }

    private void ApplyMutationLocked(TableSchema schema, TableMutationOperation operation, byte[]? encodedNewRow)
        => ApplyMutationLocked(schema, operation, encodedNewRow, applied: null);

    private void ApplyMutationLocked(
        TableSchema schema,
        TableMutationOperation operation,
        byte[]? encodedNewRow,
        List<RollbackAction>? applied)
    {
        var oldIndexKeys = operation.OldRow is null
            ? []
            : BuildIndexEntries(schema, operation.OldRow);
        var newIndexKeys = operation.NewRow is null
            ? []
            : BuildIndexEntries(schema, operation.NewRow);

        try
        {
            foreach (var indexEntry in oldIndexKeys)
            {
                byte[]? previous = _keyspace.Get(indexEntry.Key);
                if (_keyspace.Delete(indexEntry.Key))
                    applied?.Add(RollbackAction.Put(indexEntry.Key, previous ?? indexEntry.Value));
            }

            if (operation.NewRow is null)
            {
                byte[]? previous = _keyspace.Get(operation.RowKey);
                if (_keyspace.Delete(operation.RowKey))
                    applied?.Add(RollbackAction.Put(operation.RowKey, previous ?? []));
            }
            else
            {
                byte[]? previous = _keyspace.Get(operation.RowKey);
                _keyspace.Put(operation.RowKey, encodedNewRow ?? TableRowCodec.Encode(schema, operation.NewRow.Values));
                applied?.Add(previous is null
                    ? RollbackAction.Delete(operation.RowKey)
                    : RollbackAction.Put(operation.RowKey, previous));
            }

            foreach (var indexEntry in newIndexKeys)
            {
                byte[]? previous = _keyspace.Get(indexEntry.Key);
                _keyspace.Put(indexEntry.Key, indexEntry.Value);
                applied?.Add(previous is null
                    ? RollbackAction.Delete(indexEntry.Key)
                    : RollbackAction.Put(indexEntry.Key, previous));
            }
        }
        catch
        {
            if (applied is not null)
                RollbackAppliedLocked(applied);
            throw;
        }
    }

    private IReadOnlyList<IndexEntry> BuildIndexEntries(TableSchema schema, TableRow row)
    {
        if (schema.Indexes.Count == 0)
            return [];

        var entries = new List<IndexEntry>(schema.Indexes.Count);
        foreach (var index in schema.Indexes)
        {
            byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(index, row.Values, schema, row.PrimaryKey.Span);
            if (key is null)
                continue;
            byte[] value = TableIndexCodec.EncodeIndexEntryValue(row.PrimaryKey.Span);
            entries.Add(new IndexEntry(key, value));
        }

        return entries;
    }

    private static TableIndex? TryResolveUniqueIndexConflict(TableSchema schema, IReadOnlyList<IndexEntry> entries, ReadOnlySpan<byte> candidateKey)
    {
        foreach (var index in schema.Indexes.Where(static i => i.IsUnique))
        {
            foreach (var entry in entries)
            {
                if (entry.Key.AsSpan().SequenceEqual(candidateKey))
                    return index;
            }
        }

        return null;
    }

    private void RebuildIndexesLocked()
    {
        byte[] afterKey = [];
        while (true)
        {
            var keys = _keyspace.ScanKeysPrefixAfter([(byte)'i'], afterKey, MaintenanceKeyPageSize);
            if (keys.Count == 0)
                break;

            afterKey = keys[^1];
            _keyspace.DeleteMany(keys);
        }

        if (_schema.Indexes.Count == 0)
            return;

        afterKey = [];
        while (true)
        {
            var rows = _keyspace.ScanPrefixAfter([(byte)'r'], afterKey, IndexRebuildRowPageSize);
            if (rows.Count == 0)
                break;

            afterKey = rows[^1].Key.ToArray();
            foreach (var rowEntry in rows)
            {
                var primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(rowEntry.Key).ToArray();
                var row = new TableRow(TableRowCodec.Decode(_schema, rowEntry.Value.Span), primaryKey);
                var entries = BuildIndexEntries(_schema, row);
                foreach (var indexEntry in entries)
                {
                    if (_keyspace.Get(indexEntry.Key) is not null
                        && TryResolveUniqueIndexConflict(_schema, entries, indexEntry.Key) is { } index)
                    {
                        throw UniqueViolation(_schema, index, "无法重建索引");
                    }

                    _keyspace.Put(indexEntry.Key, indexEntry.Value);
                }
            }
        }
    }

    private void MigrateLegacyRowsLocked()
    {
        byte[] afterKey = [];
        while (true)
        {
            var keys = _keyspace.ScanKeysPrefixAfter(
                ReadOnlySpan<byte>.Empty,
                afterKey,
                MaintenanceKeyPageSize);
            if (keys.Count == 0)
                break;

            afterKey = keys[^1];
            foreach (var key in keys)
            {
                if (!TableKeyCodec.IsEncodedPrimaryKey(_schema, key))
                    continue;

                byte[]? value = _keyspace.Get(key);
                if (value is null || !IsLegacyRowEntry(_schema, key, value))
                    continue;

                byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(key);
                if (_keyspace.Get(rowKey) is null)
                    _keyspace.Put(rowKey, value);
                _keyspace.Delete(key);
            }
        }
    }

    private static bool IsLegacyRowEntry(
        TableSchema schema,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value)
    {
        try
        {
            var values = TableRowCodec.Decode(schema, value);
            byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
            if (key.SequenceEqual(primaryKey))
                return true;

            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void RollbackAppliedLocked(List<RollbackAction> applied)
    {
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            var action = applied[i];
            if (action.Value is null)
                _keyspace.Delete(action.Key);
            else
                _keyspace.Put(action.Key, action.Value);
        }

        applied.Clear();
    }

    private PreparedTableBatch PrepareBatchLocked(IReadOnlyList<TableRowMutation> mutations)
    {
        var schema = _schema;
        var operations = new List<TableMutationOperation>(mutations.Count);
        var pendingRowKeys = new HashSet<string>(StringComparer.Ordinal);
        var pendingUniqueKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            if (mutation.NewValues is not null)
                ValidateRow(schema, mutation.NewValues);

            byte[] primaryKey;
            bool isInsert = mutation.PrimaryKeyValues is null && mutation.NewValues is not null;
            if (mutation.PrimaryKeyValues is not null)
            {
                primaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, mutation.PrimaryKeyValues);
            }
            else if (mutation.NewValues is not null)
            {
                primaryKey = TableKeyCodec.EncodePrimaryKey(schema, mutation.NewValues);
            }
            else
            {
                throw new InvalidOperationException("DELETE mutation 必须提供主键值。");
            }

            byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
            string rowKeyText = Convert.ToHexString(rowKey);
            if (!pendingRowKeys.Add(rowKeyText))
                throw new InvalidOperationException($"轻事务中同一行被多次修改：table '{schema.Name}'。");

            var oldRow = TryGetByRowKeyLocked(schema, rowKey);
            if (isInsert && oldRow is not null)
                throw new InvalidOperationException($"table '{schema.Name}' 中主键已存在。");
            ValidateRowVersion(schema, mutation, oldRow);
            TableRow? newRow = mutation.NewValues is null
                ? null
                : new TableRow(mutation.NewValues.ToArray(), primaryKey);
            var operation = new TableMutationOperation(rowKey, oldRow, newRow);
            if (operation.NewRow is not null)
            {
                ValidateUniqueIndexesLocked(schema, operation);
                ValidatePendingUniqueIndexes(schema, operation.NewRow, pendingUniqueKeys);
            }
            operations.Add(operation);
        }

        var affected = operations.Count(static operation => operation.OldRow is not null || operation.NewRow is not null);
        int rowCountDelta = 0;
        for (int i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (operation.OldRow is null && operation.NewRow is not null)
                rowCountDelta++;
            else if (operation.OldRow is not null && operation.NewRow is null)
                rowCountDelta--;
        }
        return new PreparedTableBatch(
            schema,
            operations,
            affected,
            new List<RollbackAction>(operations.Count * Math.Max(1, schema.Indexes.Count + 1)),
            rowCountDelta);
    }

    private void ApplyPreparedBatchLocked(PreparedTableBatch batch)
    {
        if (!ReferenceEquals(batch.Schema, _schema))
            throw new InvalidOperationException($"table '{_schema.Name}' schema 已变化，无法提交已准备的轻事务批次。");

        bool isPureDeleteBatch = batch.Operations.Count > 1;
        for (int i = 0; isPureDeleteBatch && i < batch.Operations.Count; i++)
        {
            var operation = batch.Operations[i];
            isPureDeleteBatch = operation.OldRow is not null && operation.NewRow is null;
        }

        if (isPureDeleteBatch)
        {
            var keys = new List<byte[]>(batch.Operations.Count * Math.Max(1, batch.Schema.Indexes.Count + 1));
            foreach (var operation in batch.Operations)
            {
                foreach (var indexEntry in BuildIndexEntries(batch.Schema, operation.OldRow!))
                {
                    keys.Add(indexEntry.Key);
                    batch.Applied.Add(RollbackAction.Put(indexEntry.Key, indexEntry.Value));
                }

                keys.Add(operation.RowKey);
                batch.Applied.Add(RollbackAction.Put(
                    operation.RowKey,
                    TableRowCodec.Encode(batch.Schema, operation.OldRow!.Values)));
            }

            _keyspace.DeleteMany(keys);
        }
        else
        {
            foreach (var operation in batch.Operations)
            {
                if (operation.OldRow is null && operation.NewRow is null)
                    continue;

                ApplyMutationLocked(
                    batch.Schema,
                    operation,
                    operation.NewRow is null ? null : TableRowCodec.Encode(batch.Schema, operation.NewRow.Values),
                    batch.Applied);
            }
        }

        _rowCount = checked(_rowCount + batch.RowCountDelta);
        batch.RowCountApplied = true;
    }

    private static void ValidateRowVersion(TableSchema schema, TableRowMutation mutation, TableRow? oldRow)
    {
        if (schema.RowVersionColumn is not { } column || mutation.ExpectedRowVersion is null)
            return;

        var actual = oldRow is null || oldRow.Values[column.Ordinal] is null
            ? 0L
            : Convert.ToInt64(oldRow.Values[column.Ordinal], System.Globalization.CultureInfo.InvariantCulture);
        if (actual != mutation.ExpectedRowVersion.Value)
        {
            throw new TableConstraintException(
                TableConstraintException.ConcurrencyConflict,
                schema.Name,
                column.Name,
                $"table '{schema.Name}' 乐观并发冲突：列 '{column.Name}' 当前版本已变化。");
        }
    }

    private static TableConstraintException UniqueViolation(TableSchema schema, TableIndex index, string? suffix = null)
        => new(
            TableConstraintException.UniqueViolation,
            schema.Name,
            index.Name,
            suffix is null
                ? $"唯一索引 '{index.Name}' 冲突。"
                : $"唯一索引 '{index.Name}' 冲突，{suffix}。");

    private sealed record IndexEntry(byte[] Key, byte[] Value);

    internal sealed record TableMutationOperation(
        byte[] RowKey,
        TableRow? OldRow,
        TableRow? NewRow)
    {
        public static TableMutationOperation Insert(byte[] rowKey, TableRow row)
            => new(rowKey, OldRow: null, NewRow: row);

        public static TableMutationOperation Update(byte[] rowKey, TableRow? oldRow, TableRow row)
            => new(rowKey, oldRow, row);

        public static TableMutationOperation Delete(byte[] rowKey, TableRow oldRow)
            => new(rowKey, oldRow, NewRow: null);
    }

    internal sealed record RollbackAction(byte[] Key, byte[]? Value)
    {
        public static RollbackAction Delete(byte[] key)
            => new(key.ToArray(), null);

        public static RollbackAction Put(byte[] key, byte[] value)
            => new(key.ToArray(), value.ToArray());
    }

    internal sealed record PreparedTableBatch(
        TableSchema Schema,
        IReadOnlyList<TableMutationOperation> Operations,
        int AffectedRows,
        List<RollbackAction> Applied,
        int RowCountDelta)
    {
        public bool RowCountApplied { get; set; }
        public IReadOnlyList<TableRow> FinalRows
            => Operations
                .Where(static operation => operation.NewRow is not null)
                .Select(static operation => operation.NewRow!)
                .ToArray();

        public IReadOnlyList<TableRow> DeletedRows
            => Operations
                .Where(static operation => operation.OldRow is not null && operation.NewRow is null)
                .Select(static operation => operation.OldRow!)
                .ToArray();
    }
}
