using SonnetDB.Kv;

namespace SonnetDB.Tables;

/// <summary>
/// 单个关系表的 KV-backed 行存储。
/// </summary>
public sealed class TableStore : IDisposable
{
    private readonly object _sync = new();
    private readonly KvKeyspace _keyspace;
    private TableSchema _schema;

    internal TableStore(TableSchema schema, KvKeyspace keyspace)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keyspace);
        _schema = schema;
        _keyspace = keyspace;
        MigrateLegacyRowsLocked();
        RebuildIndexesLocked();
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
                    byte[] indexKey = TableIndexCodec.EncodeIndexEntryKey(index, row.Values, schema, primaryKey);
                    string uniqueKey = index.Name + ":" + Convert.ToHexString(indexKey);
                    if (!pendingUniqueKeys.Add(uniqueKey))
                        throw new InvalidOperationException($"唯一索引 '{index.Name}' 冲突。");
                    byte[] prefix = TableIndexCodec.EncodeIndexPrefix(index, row.Values, schema);
                    foreach (var entry in _keyspace.ScanPrefix(prefix))
                    {
                        if (entry.Key.Span.SequenceEqual(indexKey))
                            throw new InvalidOperationException($"唯一索引 '{index.Name}' 冲突。");
                    }
                }

                operations.Add(TableMutationOperation.Insert(rowKey, row));
            }

            var applied = new List<RollbackAction>(operations.Count * Math.Max(1, schema.Indexes.Count + 1));
            try
            {
                foreach (var operation in operations)
                    ApplyMutationLocked(schema, operation, TableRowCodec.Encode(schema, operation.NewRow!.Values), applied);
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
            var schema = _schema;
            var operations = new List<TableMutationOperation>(mutations.Count);
            var pendingRowKeys = new HashSet<string>(StringComparer.Ordinal);
            var pendingUniqueKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var mutation in mutations)
            {
                if (mutation.NewValues is not null)
                    ValidateRow(schema, mutation.NewValues);

                byte[] primaryKey;
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

            var applied = new List<RollbackAction>(operations.Count * Math.Max(1, schema.Indexes.Count + 1));
            var affected = 0;
            try
            {
                foreach (var operation in operations)
                {
                    if (operation.OldRow is null && operation.NewRow is null)
                        continue;

                    ApplyMutationLocked(
                        schema,
                        operation,
                        operation.NewRow is null ? null : TableRowCodec.Encode(schema, operation.NewRow.Values),
                        applied);
                    affected++;
                }
            }
            catch
            {
                RollbackAppliedLocked(applied);
                throw;
            }

            return affected;
        }
    }

    /// <summary>
    /// 按主键读取一行。
    /// </summary>
    /// <param name="primaryKeyValues">主键值。</param>
    /// <returns>找到时返回行；否则返回 null。</returns>
    public TableRow? GetByPrimaryKey(IReadOnlyList<object?> primaryKeyValues)
    {
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
            return true;
        }
    }

    /// <summary>
    /// 扫描当前表的所有行，按主键字节序升序返回。
    /// </summary>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<TableRow> Scan(int? limit = null)
    {
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

            var values = new object?[schema.Columns.Count];
            for (int i = 0; i < index.Columns.Count; i++)
            {
                var column = schema.TryGetColumn(index.Columns[i])
                    ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列 '{index.Columns[i]}'。");
                values[column.Ordinal] = indexColumnValues[i];
            }

            byte[] prefix = TableIndexCodec.EncodeIndexPrefix(index, values, schema);
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

    internal void Compact() => _keyspace.Compact();

    /// <summary>
    /// 关闭底层 KV keyspace。
    /// </summary>
    public void Dispose() => _keyspace.Dispose();

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
            byte[] prefix = TableIndexCodec.EncodeIndexPrefix(index, operation.NewRow.Values, schema);
            foreach (var entry in _keyspace.ScanPrefix(prefix))
            {
                byte[] indexKey = TableIndexCodec.EncodeIndexEntryKey(
                    index,
                    operation.NewRow.Values,
                    schema,
                    operation.NewRow.PrimaryKey.Span);
                if (!entry.Key.Span.SequenceEqual(indexKey))
                    continue;

                if (operation.OldRow is not null && entry.Value.Span.SequenceEqual(operation.OldRow.PrimaryKey.Span))
                    continue;

                throw new InvalidOperationException($"唯一索引 '{index.Name}' 冲突。");
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
            byte[] key = TableIndexCodec.EncodeIndexEntryKey(index, row.Values, schema, row.PrimaryKey.Span);
            string scopedKey = index.Name + ":" + Convert.ToHexString(key);
            if (pendingUniqueKeys.TryGetValue(scopedKey, out var existingPrimaryKey)
                && !string.Equals(existingPrimaryKey, primaryKeyText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"唯一索引 '{index.Name}' 冲突。");
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
            byte[] key = TableIndexCodec.EncodeIndexEntryKey(index, row.Values, schema, row.PrimaryKey.Span);
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
        foreach (var entry in _keyspace.ScanPrefix(new byte[] { (byte)'i' }, int.MaxValue))
            _keyspace.Delete(entry.Key.Span);

        if (_schema.Indexes.Count == 0)
            return;

        foreach (var rowEntry in _keyspace.ScanPrefix(new byte[] { (byte)'r' }, int.MaxValue))
        {
            var primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(rowEntry.Key).ToArray();
            var row = new TableRow(TableRowCodec.Decode(_schema, rowEntry.Value.Span), primaryKey);
            var entries = BuildIndexEntries(_schema, row);
            foreach (var indexEntry in entries)
            {
                if (_keyspace.Get(indexEntry.Key) is not null
                    && TryResolveUniqueIndexConflict(_schema, entries, indexEntry.Key) is { } index)
                {
                    throw new InvalidOperationException($"唯一索引 '{index.Name}' 冲突，无法重建索引。");
                }

                _keyspace.Put(indexEntry.Key, indexEntry.Value);
            }
        }
    }

    private void MigrateLegacyRowsLocked()
    {
        var legacyEntries = _keyspace.ScanPrefix(ReadOnlySpan<byte>.Empty, int.MaxValue)
            .Where(e => IsLegacyRowEntry(_schema, e))
            .ToArray();

        foreach (var entry in legacyEntries)
        {
            byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(entry.Key.Span);
            if (_keyspace.Get(rowKey) is null)
                _keyspace.Put(rowKey, entry.Value.Span);
            _keyspace.Delete(entry.Key.Span);
        }
    }

    private static bool IsLegacyRowEntry(TableSchema schema, KvEntry entry)
    {
        try
        {
            var values = TableRowCodec.Decode(schema, entry.Value.Span);
            byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
            if (entry.Key.Span.SequenceEqual(primaryKey))
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

    private sealed record IndexEntry(byte[] Key, byte[] Value);

    private sealed record TableMutationOperation(
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

    private sealed record RollbackAction(byte[] Key, byte[]? Value)
    {
        public static RollbackAction Delete(byte[] key)
            => new(key.ToArray(), null);

        public static RollbackAction Put(byte[] key, byte[] value)
            => new(key.ToArray(), value.ToArray());
    }
}
