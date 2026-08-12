using System.Buffers.Binary;
using SonnetDB.Kv;

namespace SonnetDB.Tables;

/// <summary>
/// 单个关系表的 KV-backed 行存储。
/// </summary>
public sealed class TableStore : IDisposable
{
    private static readonly byte[] _autoIncrementStateKey = [(byte)'m', (byte)'a'];
    private const int MaintenanceKeyPageSize = 256;
    private const int IndexRebuildRowPageSize = 4;
    private const int IndexRepairMutationPageSize = 1024;
    private readonly object _sync = new();
    private readonly KvKeyspace _keyspace;
    private TableSchema _schema;
    private int _rowCount;
    private long _fullScanCount;
    private long _primaryKeyLookupCount;
    private long _uniqueIndexValidationScanCount;
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

    /// <summary>
    /// 返回当前表 active WAL 的逻辑长度，供基准和恢复证据读取。
    /// </summary>
    internal long ActiveWalLengthForEvidence => _keyspace.ActiveWalLength;

    /// <summary>公开全表扫描累计次数，供访问计划回归测试观测。</summary>
    internal long FullScanCount => Interlocked.Read(ref _fullScanCount);

    /// <summary>公开主键点读累计次数，供访问计划回归测试观测。</summary>
    internal long PrimaryKeyLookupCount => Interlocked.Read(ref _primaryKeyLookupCount);

    /// <summary>公开唯一索引持久扫描累计次数，供未变化索引回归测试观测。</summary>
    internal long UniqueIndexValidationScanCount => Interlocked.Read(ref _uniqueIndexValidationScanCount);

    /// <summary>测试范围查询时记录实际下推到索引扫描的候选上限。</summary>
    internal Action<int>? RangeScanLimitTestHook { get; set; }

    /// <summary>测试范围分页是否从上一页索引键继续扫描。</summary>
    internal Action<bool>? RangeScanContinuationTestHook { get; set; }

    /// <summary>测试普通查询实际解码的关系行数，验证分页完成后是否提前停止。</summary>
    internal Action<int>? RowDecodedTestHook { get; set; }

    /// <summary>底层 rowstore generation 旧文件回收状态。</summary>
    public KvCleanupStatus GetCleanupStatus() => _keyspace.GetCleanupStatus();

    /// <summary>
    /// 为待插入行填充自增列，并持久化新的序列高水位。
    /// </summary>
    /// <param name="rows">按 schema 列顺序排列的可变行值。</param>
    /// <returns>完成预留时底层 rowstore 的 generation。</returns>
    internal long ApplyAutoIncrement(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_sync)
        {
            if (rows.Count > 0)
                ApplyAutoIncrementLocked(_schema, rows);
            return _keyspace.Generation;
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
            var normalizedValues = values.ToArray();
            ApplyAutoIncrementLocked(schema, [normalizedValues]);
            ValidateRow(schema, normalizedValues);
            byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, normalizedValues);
            byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
            var oldRow = TryGetByRowKeyLocked(schema, rowKey);
            var operation = TableMutationOperation.Update(rowKey, oldRow, new TableRow(normalizedValues, primaryKey));
            ValidateUniqueIndexesLocked(schema, operation);
            ApplyMutationLocked(schema, operation);
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
            var normalizedValues = values.ToArray();
            ApplyAutoIncrementLocked(schema, [normalizedValues]);
            ValidateRow(schema, normalizedValues);
            byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, normalizedValues);
            byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
            if (_keyspace.Get(rowKey) is not null)
                throw new InvalidOperationException($"table '{schema.Name}' 中主键已存在。");

            var newRow = new TableRow(normalizedValues, primaryKey);
            var operation = TableMutationOperation.Insert(rowKey, newRow);
            ValidateUniqueIndexesLocked(schema, operation);
            ApplyMutationLocked(schema, operation);
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
            var normalizedRows = rows.Select(static row => row.ToArray()).ToArray();
            ApplyAutoIncrementLocked(schema, normalizedRows);
            var operations = new List<TableMutationOperation>(rows.Count);
            var pendingRowKeys = new HashSet<string>(StringComparer.Ordinal);
            var pendingUniqueKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var values in normalizedRows)
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
                ApplyMutationsLocked(schema, operations, applied);
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

            ApplyMutationLocked(schema, TableMutationOperation.Delete(rowKey, oldRow));
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
        return EnumerateScan(limit).ToArray();
    }

    /// <summary>
    /// 按主键顺序惰性扫描关系表。每次只保留一个 KV 页，调用方停止枚举时立即释放快照。
    /// </summary>
    /// <param name="limit">最多返回的行数；为空表示扫描到末尾。</param>
    /// <returns>按主键升序排列的关系行序列。</returns>
    internal IEnumerable<TableRow> EnumerateScan(int? limit = null)
    {
        Interlocked.Increment(ref _fullScanCount);
        if (limit is <= 0)
            yield break;

        TableSchema schema;
        lock (_sync)
            schema = _schema;

        using var snapshot = _keyspace.AcquireReadSnapshot();
        using var cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = new byte[] { (byte)'r' },
            PageSize = 256,
        });

        int emitted = 0;
        while (!cursor.IsExhausted && (limit is null || emitted < limit.Value))
        {
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
            if (page.Count == 0)
                yield break;

            foreach (var entry in page)
            {
                if (limit is int && emitted >= limit.Value)
                    yield break;

                var primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(entry.Key);
                emitted++;
                RowDecodedTestHook?.Invoke(emitted);
                yield return new TableRow(
                    TableRowCodec.Decode(schema, entry.Value.Span),
                    primaryKey.ToArray());
            }
        }
    }

    /// <summary>
    /// 按完整二级索引等值键读取候选行。
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
            if (indexColumnValues.Count != index.Columns.Count)
                throw new ArgumentException("索引值数量与索引列数量不一致。", nameof(indexColumnValues));

            return GetByIndexPrefixLocked(index, indexColumnValues, limit);
        }
    }

    /// <summary>
    /// 按联合二级索引的连续首列等值前缀读取候选行。
    /// </summary>
    /// <param name="index">索引声明。</param>
    /// <param name="indexColumnValues">从索引首列开始连续匹配的等值谓词值。</param>
    /// <param name="limit">最多返回行数。</param>
    /// <returns>索引中命中该前缀的候选行。</returns>
    public IReadOnlyList<TableRow> GetByIndexPrefix(
        TableIndex index,
        IReadOnlyList<object?> indexColumnValues,
        int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(indexColumnValues);
        lock (_sync)
        {
            if (indexColumnValues.Count == 0 || indexColumnValues.Count > index.Columns.Count)
            {
                throw new ArgumentException(
                    "索引前缀值必须从首列开始连续提供，且不能超过索引列数量。",
                    nameof(indexColumnValues));
            }

            return GetByIndexPrefixLocked(index, indexColumnValues, limit);
        }
    }

    /// <summary>
    /// 按二级索引前缀惰性读取候选行，使用同一 KV 快照完成索引项和回表读取。
    /// </summary>
    /// <param name="index">索引声明。</param>
    /// <param name="indexColumnValues">从索引首列开始连续匹配的等值谓词值。</param>
    /// <param name="limit">最多返回的有效行数。</param>
    /// <returns>按索引键顺序返回的候选行。</returns>
    internal IEnumerable<TableRow> EnumerateByIndexPrefix(
        TableIndex index,
        IReadOnlyList<object?> indexColumnValues,
        int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(indexColumnValues);

        TableSchema schema;
        byte[] prefix;
        lock (_sync)
        {
            schema = _schema;
            prefix = TableIndexCodec.EncodeLookupPrefix(index, indexColumnValues, schema)
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 的等值前缀无法编码。");
        }

        if (limit is <= 0)
            yield break;

        using var snapshot = _keyspace.AcquireReadSnapshot();
        using var cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = prefix,
            PageSize = 256,
        });

        int emitted = 0;
        while (!cursor.IsExhausted && (limit is null || emitted < limit.Value))
        {
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
            if (page.Count == 0)
                yield break;

            foreach (var entry in page)
            {
                if (limit is int && emitted >= limit.Value)
                    yield break;

                byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(entry.Value.Span);
                var payload = snapshot.GetEntry(rowKey);
                if (payload is null)
                    continue;

                emitted++;
                yield return new TableRow(
                    TableRowCodec.Decode(schema, payload.Value.Span),
                    entry.Value.ToArray());
            }
        }
    }

    /// <summary>
    /// 按范围索引的物理区间惰性读取候选行；不改变范围值的逻辑排序语义。
    /// </summary>
    /// <param name="index">普通联合索引。</param>
    /// <param name="equalityPrefixValues">索引连续等值前缀。</param>
    /// <param name="range">范围列约束。</param>
    /// <param name="limit">最多返回的有效行数。</param>
    /// <returns>按索引范围物理顺序返回的候选行。</returns>
    internal IEnumerable<TableRow> EnumerateByIndexRange(
        TableIndex index,
        IReadOnlyList<object?> equalityPrefixValues,
        TableIndexRange range,
        int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(equalityPrefixValues);
        ArgumentNullException.ThrowIfNull(range);

        TableSchema schema;
        byte[] prefix;
        IReadOnlyList<TableIndexKeyRange> keyRanges;
        lock (_sync)
        {
            schema = _schema;
            if (!string.IsNullOrWhiteSpace(index.JsonPath)
                || equalityPrefixValues.Count >= index.Columns.Count)
            {
                throw new ArgumentException("范围扫描要求普通联合索引仍有一个未绑定的下一列。", nameof(equalityPrefixValues));
            }

            var expectedColumn = schema.TryGetColumn(index.Columns[equalityPrefixValues.Count])
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列。");
            if (!string.Equals(expectedColumn.Name, range.Column.Name, StringComparison.Ordinal)
                || expectedColumn.DataType is not (TableColumnType.Int64 or TableColumnType.DateTime))
            {
                throw new ArgumentException("范围列必须是索引等值前缀后的 Int64 或 DATETIME 列。", nameof(range));
            }

            prefix = TableIndexCodec.EncodeLookupPrefix(index, equalityPrefixValues, schema)
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 的等值前缀无法编码。");
            keyRanges = BuildSignedKeyRanges(index, equalityPrefixValues, range, schema);
        }

        int take = limit ?? int.MaxValue;
        if (take <= 0)
            yield break;
        RangeScanLimitTestHook?.Invoke(take);

        using var snapshot = _keyspace.AcquireReadSnapshot();
        int emitted = 0;
        foreach (var keyRange in keyRanges)
        {
            using var cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = prefix,
                StartInclusive = keyRange.StartInclusive,
                EndExclusive = keyRange.EndExclusive,
                PageSize = 256,
            });

            while (!cursor.IsExhausted && emitted < take)
            {
                IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
                if (page.Count == 0)
                    break;

                foreach (var entry in page)
                {
                    if (emitted >= take)
                        yield break;

                    byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(entry.Value.Span);
                    var payload = snapshot.GetEntry(rowKey);
                    if (payload is null)
                        continue;

                    emitted++;
                    yield return new TableRow(
                        TableRowCodec.Decode(schema, payload.Value.Span),
                        entry.Value.ToArray());
                }
            }
        }
    }

    /// <summary>
    /// 按联合二级索引的连续等值前缀和下一列 Int64/DATETIME 范围读取候选行。
    /// 返回顺序是范围列的逻辑升序，不受现有 signed big-endian 物理排序跨零影响。
    /// </summary>
    internal IReadOnlyList<TableRow> GetByIndexRange(
        TableIndex index,
        IReadOnlyList<object?> equalityPrefixValues,
        TableIndexRange range,
        int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(equalityPrefixValues);
        ArgumentNullException.ThrowIfNull(range);
        lock (_sync)
        {
            var schema = _schema;
            if (!string.IsNullOrWhiteSpace(index.JsonPath)
                || equalityPrefixValues.Count >= index.Columns.Count)
            {
                throw new ArgumentException("范围扫描要求普通联合索引仍有一个未绑定的下一列。", nameof(equalityPrefixValues));
            }

            var expectedColumn = schema.TryGetColumn(index.Columns[equalityPrefixValues.Count])
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列 '{index.Columns[equalityPrefixValues.Count]}'。");
            if (!string.Equals(expectedColumn.Name, range.Column.Name, StringComparison.Ordinal)
                || expectedColumn.DataType is not (TableColumnType.Int64 or TableColumnType.DateTime))
            {
                throw new ArgumentException("范围列必须是索引等值前缀后的 Int64 或 DATETIME 列。", nameof(range));
            }

            int take = limit ?? int.MaxValue;
            if (take <= 0)
                return [];
            RangeScanLimitTestHook?.Invoke(take);

            byte[] prefix = TableIndexCodec.EncodeLookupPrefix(index, equalityPrefixValues, schema)
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 的等值前缀无法编码。");
            var keyRanges = BuildSignedKeyRanges(index, equalityPrefixValues, range, schema);
            var rows = new List<TableRow>();
            foreach (var keyRange in keyRanges)
            {
                int remaining = take - rows.Count;
                if (remaining <= 0)
                    break;

                var entries = _keyspace.ScanRange(
                    prefix,
                    keyRange.StartInclusive,
                    keyRange.EndExclusive,
                    afterKey: ReadOnlySpan<byte>.Empty,
                    remaining);
                MaterializeIndexEntriesLocked(schema, entries, rows);
            }

            return rows;
        }
    }

    /// <summary>
    /// 按范围索引顺序读取至少指定数量的有效行，并继续到该范围值的完整并列组结束。
    /// 每页使用上一页末尾索引键续读，避免为扩展并列组而反复从范围起点扫描。
    /// </summary>
    /// <param name="index">已选中的普通二级索引。</param>
    /// <param name="equalityPrefixValues">索引连续等值前缀。</param>
    /// <param name="range">等值前缀后范围列的边界。</param>
    /// <param name="candidateLimit">SQL OFFSET 加 LIMIT 得到的候选行数。</param>
    /// <returns>包含分页边界范围值完整并列组的候选行。</returns>
    internal IReadOnlyList<TableRow> GetByIndexRangeThroughValueGroup(
        TableIndex index,
        IReadOnlyList<object?> equalityPrefixValues,
        TableIndexRange range,
        int candidateLimit)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(equalityPrefixValues);
        ArgumentNullException.ThrowIfNull(range);
        if (candidateLimit <= 0)
            return [];

        lock (_sync)
        {
            var schema = _schema;
            if (!string.IsNullOrWhiteSpace(index.JsonPath)
                || equalityPrefixValues.Count >= index.Columns.Count)
            {
                throw new ArgumentException("范围扫描要求普通联合索引仍有一个未绑定的下一列。", nameof(equalityPrefixValues));
            }

            var expectedColumn = schema.TryGetColumn(index.Columns[equalityPrefixValues.Count])
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列 '{index.Columns[equalityPrefixValues.Count]}'。");
            if (!string.Equals(expectedColumn.Name, range.Column.Name, StringComparison.Ordinal)
                || expectedColumn.DataType is not (TableColumnType.Int64 or TableColumnType.DateTime))
            {
                throw new ArgumentException("范围列必须是索引等值前缀后的 Int64 或 DATETIME 列。", nameof(range));
            }

            byte[] prefix = TableIndexCodec.EncodeLookupPrefix(index, equalityPrefixValues, schema)
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 的等值前缀无法编码。");
            var rows = new List<TableRow>();
            object? boundaryValue = null;
            bool boundaryReached = false;
            int pageLimit = candidateLimit == int.MaxValue ? int.MaxValue : candidateLimit + 1;

            foreach (var keyRange in BuildSignedKeyRanges(index, equalityPrefixValues, range, schema))
            {
                byte[] afterKey = [];
                while (true)
                {
                    RangeScanLimitTestHook?.Invoke(pageLimit);
                    RangeScanContinuationTestHook?.Invoke(afterKey.Length != 0);
                    var entries = _keyspace.ScanRange(
                        prefix,
                        keyRange.StartInclusive,
                        keyRange.EndExclusive,
                        afterKey,
                        pageLimit);
                    foreach (var entry in entries)
                    {
                        TableRow? row = TryMaterializeIndexEntryLocked(schema, entry);
                        if (row is null)
                            continue;

                        object? rangeValue = row.Values[range.Column.Ordinal];
                        if (!boundaryReached)
                        {
                            rows.Add(row);
                            if (rows.Count == candidateLimit)
                            {
                                boundaryValue = rangeValue;
                                boundaryReached = true;
                            }

                            continue;
                        }

                        if (!RangeValuesEqual(boundaryValue, rangeValue))
                            return rows;

                        rows.Add(row);
                    }

                    if (entries.Count < pageLimit)
                        break;

                    afterKey = entries[^1].Key.ToArray();
                    pageLimit = DoubleRangeScanPageLimit(pageLimit);
                }
            }

            return rows;
        }
    }

    /// <summary>
    /// 以饱和倍增扩展范围页大小，使超大并列组只被顺序读取一次。
    /// </summary>
    private static int DoubleRangeScanPageLimit(int pageLimit)
        => pageLimit >= int.MaxValue / 2 ? int.MaxValue : pageLimit * 2;

    /// <summary>
    /// 比较范围列值；DATETIME 统一到 UTC ticks，避免 Kind 差异拆分同一时刻的并列组。
    /// </summary>
    private static bool RangeValuesEqual(object? left, object? right)
        => left is DateTime leftTime && right is DateTime rightTime
            ? leftTime.ToUniversalTime().Ticks == rightTime.ToUniversalTime().Ticks
            : Equals(left, right);

    /// <summary>
    /// 在持有表锁时扫描二级索引前缀并回表物化候选行。
    /// </summary>
    private IReadOnlyList<TableRow> GetByIndexPrefixLocked(
        TableIndex index,
        IReadOnlyList<object?> indexColumnValues,
        int? limit)
    {
        var schema = _schema;
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

    /// <summary>
    /// 把 signed big-endian 的物理顺序拆成负数、非负数两个逻辑升序区间。
    /// </summary>
    private static IReadOnlyList<TableIndexKeyRange> BuildSignedKeyRanges(
        TableIndex index,
        IReadOnlyList<object?> equalityPrefixValues,
        TableIndexRange range,
        TableSchema schema)
    {
        var ranges = new List<TableIndexKeyRange>(2);
        TryAddSignedSegment(long.MinValue, -1, index, equalityPrefixValues, range, schema, ranges);
        TryAddSignedSegment(0, long.MaxValue, index, equalityPrefixValues, range, schema, ranges);
        return ranges;
    }

    /// <summary>
    /// 求逻辑范围与一个同号区间的交集，并转换为半开物理 key 范围。
    /// </summary>
    private static void TryAddSignedSegment(
        long segmentMinimum,
        long segmentMaximum,
        TableIndex index,
        IReadOnlyList<object?> equalityPrefixValues,
        TableIndexRange range,
        TableSchema schema,
        List<TableIndexKeyRange> destination)
    {
        var lower = range.Lower is { } requestedLower && requestedLower.Value >= segmentMinimum
            ? requestedLower
            : new TableIndexRangeBound(segmentMinimum, Inclusive: true);
        var upper = range.Upper is { } requestedUpper && requestedUpper.Value <= segmentMaximum
            ? requestedUpper
            : new TableIndexRangeBound(segmentMaximum, Inclusive: true);

        if (lower.Value < segmentMinimum
            || lower.Value > segmentMaximum
            || upper.Value < segmentMinimum
            || upper.Value > segmentMaximum
            || lower.Value > upper.Value
            || (lower.Value == upper.Value && (!lower.Inclusive || !upper.Inclusive)))
        {
            return;
        }

        byte[] lowerPrefix = TableIndexCodec.EncodeRangeValuePrefix(
            index,
            equalityPrefixValues,
            lower.Value,
            schema);
        byte[] upperPrefix = TableIndexCodec.EncodeRangeValuePrefix(
            index,
            equalityPrefixValues,
            upper.Value,
            schema);
        byte[] startInclusive = lower.Inclusive
            ? lowerPrefix
            : TableIndexCodec.GetPrefixSuccessor(lowerPrefix);
        byte[] endExclusive = upper.Inclusive
            ? TableIndexCodec.GetPrefixSuccessor(upperPrefix)
            : upperPrefix;
        if (SonnetDB.Kv.KvKeyComparer.Instance.Compare(startInclusive, endExclusive) < 0)
            destination.Add(new TableIndexKeyRange(startInclusive, endExclusive));
    }

    /// <summary>
    /// 回表物化二级索引条目，忽略已经被并发删除的主行。
    /// </summary>
    private void MaterializeIndexEntriesLocked(
        TableSchema schema,
        IReadOnlyList<SonnetDB.Kv.KvEntry> entries,
        List<TableRow> rows)
    {
        foreach (var entry in entries)
        {
            TableRow? row = TryMaterializeIndexEntryLocked(schema, entry);
            if (row is not null)
                rows.Add(row);
        }
    }

    /// <summary>
    /// 由二级索引条目回表物化行，主行已删除时返回空。
    /// </summary>
    private TableRow? TryMaterializeIndexEntryLocked(
        TableSchema schema,
        SonnetDB.Kv.KvEntry entry)
    {
        byte[] primaryKey = entry.Value.Span.ToArray();
        byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
        byte[]? payload = _keyspace.Get(rowKey);
        return payload is null
            ? null
            : new TableRow(TableRowCodec.Decode(schema, payload), primaryKey);
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

    internal void ApplyMetadataSchema(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
            _schema = schema;
    }

    internal void ValidateExistingRows(
        TableSchema schema,
        Action<TableSchema, IReadOnlyList<object?>>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
        {
            byte[] afterKey = [];
            while (true)
            {
                var entries = _keyspace.ScanPrefixAfter(
                    [(byte)'r'],
                    afterKey,
                    MaintenanceKeyPageSize);
                if (entries.Count == 0)
                    break;

                afterKey = entries[^1].Key.ToArray();
                foreach (var entry in entries)
                {
                    var values = TableRowCodec.Decode(_schema, entry.Value.Span);
                    ValidateRow(schema, values);
                    validate?.Invoke(schema, values);
                }
            }
        }
    }

    internal Action ApplySchemaTransform(
        TableSchema schema,
        Func<TableSchema, TableRow, IReadOnlyList<object?>> transform,
        Action<TableSchema, IReadOnlyList<object?>>? validate = null)
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
            var payloadsApplied = false;

            try
            {
                foreach (var entry in originalPayloads)
                {
                    var primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(entry.Key).ToArray();
                    var row = new TableRow(TableRowCodec.Decode(previous, entry.Value.AsSpan()), primaryKey);
                    var values = transform(previous, row);
                    ValidateRow(schema, values);
                    validate?.Invoke(schema, values);

                    var transformedPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
                    if (!transformedPrimaryKey.AsSpan().SequenceEqual(primaryKey))
                        throw new InvalidOperationException("ALTER TABLE 当前不支持改变 PRIMARY KEY 值或列定义。");

                    transformedRows.Add((entry.Key, TableRowCodec.Encode(schema, values)));
                }

                _keyspace.ApplyBatch(transformedRows
                    .Select(static row => KvBatchMutation.Put(row.Key, row.Value))
                    .ToArray());
                payloadsApplied = true;

                _schema = schema;
                RebuildIndexesLocked();
                return () => RestoreSchemaTransform(previous, originalPayloads);
            }
            catch
            {
                if (payloadsApplied)
                {
                    foreach (var entry in originalPayloads)
                        _keyspace.Put(entry.Key, entry.Value);
                    _schema = previous;
                    RebuildIndexesLocked();
                }
                throw;
            }
        }
    }

    private void RestoreSchemaTransform(TableSchema schema, IReadOnlyList<(byte[] Key, byte[] Value)> payloads)
    {
        lock (_sync)
        {
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

    private void ApplyAutoIncrementLocked(TableSchema schema, IReadOnlyList<object?[]> rows)
    {
        var column = schema.AutoIncrementColumn;
        if (column is null || rows.Count == 0)
            return;

        long current = ReadAutoIncrementStateLocked();
        bool changed = false;
        try
        {
            foreach (var values in rows)
            {
                if (values.Length != schema.Columns.Count)
                    throw new ArgumentException("行值数量必须与表 schema 列数量一致。", nameof(rows));

                ApplyAutoIncrementValue(column, values, allocateMissing: true, ref current, ref changed);
            }
        }
        finally
        {
            WriteAutoIncrementStateLocked(current, changed);
        }
    }

    private void ApplyAutoIncrementMutationsLocked(
        TableSchema schema,
        IReadOnlyList<(object?[] Values, bool AllocateMissing)> rows)
    {
        var column = schema.AutoIncrementColumn;
        if (column is null || rows.Count == 0)
            return;

        long current = ReadAutoIncrementStateLocked();
        bool changed = false;
        try
        {
            foreach (var row in rows)
            {
                if (row.Values.Length != schema.Columns.Count)
                    throw new ArgumentException("行值数量必须与表 schema 列数量一致。", nameof(rows));

                ApplyAutoIncrementValue(
                    column,
                    row.Values,
                    row.AllocateMissing,
                    ref current,
                    ref changed);
            }
        }
        finally
        {
            WriteAutoIncrementStateLocked(current, changed);
        }
    }

    private static void ApplyAutoIncrementValue(
        TableColumn column,
        object?[] values,
        bool allocateMissing,
        ref long current,
        ref bool changed)
    {
        if (values[column.Ordinal] is null)
        {
            if (!allocateMissing)
                return;

            values[column.Ordinal] = checked(current + 1);
            current = (long)values[column.Ordinal]!;
            changed = true;
            return;
        }

        long explicitValue = ToAutoIncrementValue(values[column.Ordinal]!, column.Name);
        if (explicitValue <= current)
            return;

        current = explicitValue;
        changed = true;
    }

    private void WriteAutoIncrementStateLocked(long current, bool changed)
    {
        if (!changed)
            return;

        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, current);
        _keyspace.Put(_autoIncrementStateKey, buffer.ToArray());
    }

    private long ReadAutoIncrementStateLocked()
    {
        byte[]? payload = _keyspace.Get(_autoIncrementStateKey);
        if (payload is null)
            return 0L;
        if (payload.Length != sizeof(long))
            throw new InvalidDataException("关系表自增序列状态损坏。");
        return BinaryPrimitives.ReadInt64LittleEndian(payload);
    }

    private static long ToAutoIncrementValue(object value, string columnName)
        => value switch
        {
            byte v => v,
            sbyte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v => v,
            long v => v,
            ulong v => checked((long)v),
            _ => throw new InvalidOperationException($"AUTO_INCREMENT 列 '{columnName}' 只能写入整数值。"),
        };

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
            byte[]? indexKey = TableIndexCodec.TryEncodeIndexEntryKey(
                index,
                operation.NewRow.Values,
                schema,
                operation.NewRow.PrimaryKey.Span);
            if (indexKey is null)
                continue;
            if (operation.OldRow is not null)
            {
                byte[]? oldIndexKey = TableIndexCodec.TryEncodeIndexEntryKey(
                    index,
                    operation.OldRow.Values,
                    schema,
                    operation.OldRow.PrimaryKey.Span);
                if (oldIndexKey is not null
                    && oldIndexKey.AsSpan().SequenceEqual(indexKey)
                    && operation.OldRow.PrimaryKey.Span.SequenceEqual(operation.NewRow.PrimaryKey.Span))
                {
                    continue;
                }
            }

            byte[]? prefix = TableIndexCodec.TryEncodeIndexPrefix(index, operation.NewRow.Values, schema);
            if (prefix is null)
                continue;
            Interlocked.Increment(ref _uniqueIndexValidationScanCount);
            foreach (var entry in _keyspace.ScanPrefix(prefix))
            {
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

    private void ApplyMutationLocked(TableSchema schema, TableMutationOperation operation)
        => ApplyMutationsLocked(schema, [operation], applied: null);

    private void ApplyMutationsLocked(
        TableSchema schema,
        IReadOnlyList<TableMutationOperation> operations,
        List<RollbackAction>? applied)
    {
        var desiredValues = new Dictionary<byte[], byte[]?>(KvKeyComparer.Instance);
        for (int i = 0; i < operations.Count; i++)
        {
            TableMutationOperation operation = operations[i];
            if (operation.OldRow is null && operation.NewRow is null)
                continue;

            if (operation.OldRow is not null)
            {
                foreach (IndexEntry indexEntry in BuildIndexEntries(schema, operation.OldRow))
                    desiredValues[indexEntry.Key] = null;
            }

            if (operation.NewRow is null)
            {
                desiredValues[operation.RowKey] = null;
            }
            else
            {
                desiredValues[operation.RowKey] = TableRowCodec.Encode(schema, operation.NewRow.Values);
                foreach (IndexEntry indexEntry in BuildIndexEntries(schema, operation.NewRow))
                    desiredValues[indexEntry.Key] = indexEntry.Value;
            }
        }

        var mutations = new List<KvBatchMutation>(desiredValues.Count);
        var rollback = applied is null ? null : new List<RollbackAction>(desiredValues.Count);
        foreach ((byte[] key, byte[]? desiredValue) in desiredValues)
        {
            byte[]? previous = _keyspace.Get(key);
            if (desiredValue is null)
            {
                if (previous is null)
                    continue;
                mutations.Add(KvBatchMutation.Delete(key));
            }
            else
            {
                bool isPrimaryRow = key.Length > 0 && key[0] == (byte)'r';
                if (!isPrimaryRow
                    && previous is not null
                    && previous.AsSpan().SequenceEqual(desiredValue))
                {
                    continue;
                }
                mutations.Add(KvBatchMutation.Put(key, desiredValue));
            }

            rollback?.Add(previous is null
                ? RollbackAction.Delete(key)
                : RollbackAction.Put(key, previous));
        }

        if (mutations.Count == 0)
            return;

        _keyspace.ApplyBatch(mutations);
        if (rollback is not null)
            applied!.AddRange(rollback);
    }

    private IReadOnlyList<IndexEntry> BuildIndexEntries(TableSchema schema, TableRow row)
    {
        if (schema.Indexes.Count == 0)
            return [];

        var entries = new List<IndexEntry>(schema.Indexes.Count);
        for (int indexOrdinal = 0; indexOrdinal < schema.Indexes.Count; indexOrdinal++)
        {
            var index = schema.Indexes[indexOrdinal];
            byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(index, row.Values, schema, row.PrimaryKey.Span);
            if (key is null)
                continue;
            byte[] value = TableIndexCodec.EncodeIndexEntryValue(row.PrimaryKey.Span);
            entries.Add(new IndexEntry(index, indexOrdinal, key, value));
        }

        return entries;
    }

    private void RebuildIndexesLocked()
    {
        using var budgetScope = _keyspace.EnterIndexRebuildBudgetScope();
        int actualIndexCount = _keyspace.CountPrefix([(byte)'i']);
        int expectedIndexCount = RepairExpectedIndexesLocked(out int addedIndexCount);

        // 期望键均已校验或补齐；实际数量相等时不可能再存在额外 stale/orphan 键。
        if (checked(actualIndexCount + addedIndexCount) == expectedIndexCount)
            return;

        RemoveUnexpectedIndexesLocked();
    }

    /// <summary>
    /// 单次稳定扫描全部主行，把需要修复的索引顺序写入临时 spool，扫描结束后再按页原子回放。
    /// 扫描阶段不扩张 KV overlay，内存中始终只保留一页主行及其索引。
    /// </summary>
    /// <param name="addedIndexCount">本次实际补写的缺失索引键数量。</param>
    /// <returns>当前 schema 与全部主行共同要求的索引键数量。</returns>
    private int RepairExpectedIndexesLocked(out int addedIndexCount)
    {
        int added = 0;
        int expectedIndexCount = 0;
        if (_schema.Indexes.Count == 0)
        {
            addedIndexCount = 0;
            return expectedIndexCount;
        }

        using var repairSpool = new TableIndexRepairSpool();
        _keyspace.ReadStablePrefixPages([(byte)'r'], IndexRebuildRowPageSize, rows =>
        {
            var desiredEntries = new Dictionary<byte[], DesiredIndexEntry>(KvKeyComparer.Instance);
            foreach (var rowEntry in rows)
            {
                var primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(rowEntry.Key).ToArray();
                var row = new TableRow(TableRowCodec.Decode(_schema, rowEntry.Value.Span), primaryKey);
                var entries = BuildIndexEntries(_schema, row);
                foreach (var entry in entries)
                {
                    TableIndex? uniqueIndex = entry.Index.IsUnique ? entry.Index : null;
                    if (desiredEntries.TryGetValue(entry.Key, out var previous))
                    {
                        if (uniqueIndex is not null
                            && !previous.Entry.Value.AsSpan().SequenceEqual(entry.Value))
                        {
                            throw UniqueViolation(_schema, uniqueIndex, "无法重建索引");
                        }
                        continue;
                    }

                    desiredEntries.Add(entry.Key, new DesiredIndexEntry(entry, uniqueIndex));
                    expectedIndexCount = checked(expectedIndexCount + 1);
                }
            }

            foreach (var desired in desiredEntries.Values)
            {
                byte[]? persistedValue = _keyspace.Get(desired.Entry.Key);
                if (persistedValue is not null
                    && persistedValue.AsSpan().SequenceEqual(desired.Entry.Value))
                {
                    continue;
                }

                if (persistedValue is not null
                    && desired.UniqueIndex is { } uniqueIndex
                    && IsLiveUniqueIndexConflictLocked(desired.Entry.Key, persistedValue))
                {
                    throw UniqueViolation(_schema, uniqueIndex, "无法重建索引");
                }

                int uniqueIndexOrdinal = desired.UniqueIndex is null
                    ? -1
                    : desired.Entry.IndexOrdinal;
                repairSpool.AppendPut(
                    desired.Entry.Key,
                    desired.Entry.Value,
                    uniqueIndexOrdinal);
                if (persistedValue is null)
                    added = checked(added + 1);
            }
        });

        int mutationPageSize = _keyspace.GetIndexRebuildBatchEntryLimit(IndexRepairMutationPageSize);
        repairSpool.ReplayPages(mutationPageSize, ApplyExpectedIndexRepairPageLocked);
        addedIndexCount = added;
        return expectedIndexCount;
    }

    /// <summary>
    /// 原子应用一页期望索引修复，并在回放时复核跨扫描页的唯一键冲突。
    /// 前页已写入的唯一键通过持久值发现，本页重复键通过有界字典发现。
    /// </summary>
    private void ApplyExpectedIndexRepairPageLocked(
        IReadOnlyList<TableIndexRepairSpoolEntry> entries)
    {
        var mutations = new List<KvBatchMutation>(entries.Count);
        var pendingUniqueValues = new Dictionary<byte[], byte[]>(KvKeyComparer.Instance);
        foreach (var entry in entries)
        {
            if (entry.IsDelete || entry.Value is null)
                throw new InvalidDataException("Index repair spool contains an unexpected delete record.");

            if (entry.UniqueIndexOrdinal >= 0)
            {
                if ((uint)entry.UniqueIndexOrdinal >= (uint)_schema.Indexes.Count
                    || !_schema.Indexes[entry.UniqueIndexOrdinal].IsUnique)
                {
                    throw new InvalidDataException("Index repair spool unique index ordinal is invalid.");
                }

                var uniqueIndex = _schema.Indexes[entry.UniqueIndexOrdinal];
                if (pendingUniqueValues.TryGetValue(entry.Key, out byte[]? pendingValue))
                {
                    if (!pendingValue.AsSpan().SequenceEqual(entry.Value))
                        throw UniqueViolation(_schema, uniqueIndex, "无法重建索引");
                    continue;
                }

                byte[]? persistedValue = _keyspace.Get(entry.Key);
                if (persistedValue is not null
                    && persistedValue.AsSpan().SequenceEqual(entry.Value))
                {
                    continue;
                }

                if (persistedValue is not null
                    && IsLiveUniqueIndexConflictLocked(entry.Key, persistedValue))
                {
                    throw UniqueViolation(_schema, uniqueIndex, "无法重建索引");
                }

                pendingUniqueValues.Add(entry.Key, entry.Value);
            }

            mutations.Add(KvBatchMutation.Put(entry.Key, entry.Value));
        }

        if (mutations.Count > 0)
            _keyspace.ApplyIndexRebuildBatch(mutations);
    }

    /// <summary>
    /// 单次稳定扫描现有索引，把无主行、未知索引或已不符合当前主行内容的 key 写入临时 spool，
    /// 扫描结束后再按页原子删除，避免删除 tombstone 反复扩大后续分页扫描成本。
    /// </summary>
    private void RemoveUnexpectedIndexesLocked()
    {
        using var deleteSpool = new TableIndexRepairSpool();
        _keyspace.ReadStablePrefixPages([(byte)'i'], MaintenanceKeyPageSize, entries =>
        {
            foreach (var entry in entries)
            {
                if (!IsCurrentIndexEntryLocked(entry))
                    deleteSpool.AppendDelete(entry.Key.ToArray());
            }
        });

        int mutationPageSize = _keyspace.GetIndexRebuildBatchEntryLimit(MaintenanceKeyPageSize);
        deleteSpool.ReplayPages(mutationPageSize, ApplyUnexpectedIndexDeletePageLocked);
    }

    /// <summary>原子应用一页已确认无效的索引删除。</summary>
    private void ApplyUnexpectedIndexDeletePageLocked(
        IReadOnlyList<TableIndexRepairSpoolEntry> entries)
    {
        var mutations = new List<KvBatchMutation>(entries.Count);
        foreach (var entry in entries)
        {
            if (!entry.IsDelete || entry.Value is not null || entry.UniqueIndexOrdinal != -1)
                throw new InvalidDataException("Index repair spool contains an unexpected put record.");
            mutations.Add(KvBatchMutation.Delete(entry.Key));
        }

        if (mutations.Count > 0)
            _keyspace.ApplyIndexRebuildBatch(mutations);
    }

    /// <summary>核对一个现有索引条目是否仍由其指向的当前主行产生。</summary>
    private bool IsCurrentIndexEntryLocked(KvEntry entry)
    {
        byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(entry.Value.Span);
        byte[]? rowPayload = _keyspace.Get(rowKey);
        if (rowPayload is null)
            return false;

        var row = new TableRow(
            TableRowCodec.Decode(_schema, rowPayload),
            entry.Value.ToArray());
        return BuildIndexEntries(_schema, row).Any(expected =>
            expected.Key.AsSpan().SequenceEqual(entry.Key.Span)
            && expected.Value.AsSpan().SequenceEqual(entry.Value.Span));
    }

    /// <summary>
    /// 判断唯一索引当前指向的另一主行是否仍真实产生同一个索引键；无主行或行值已变化均属于可修复 stale 条目。
    /// </summary>
    private bool IsLiveUniqueIndexConflictLocked(ReadOnlySpan<byte> indexKey, ReadOnlySpan<byte> persistedPrimaryKey)
    {
        byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(persistedPrimaryKey);
        byte[]? rowPayload = _keyspace.Get(rowKey);
        if (rowPayload is null)
            return false;

        var row = new TableRow(
            TableRowCodec.Decode(_schema, rowPayload),
            persistedPrimaryKey.ToArray());
        foreach (var entry in BuildIndexEntries(_schema, row))
        {
            if (entry.Key.AsSpan().SequenceEqual(indexKey)
                && entry.Value.AsSpan().SequenceEqual(persistedPrimaryKey))
            {
                return true;
            }
        }

        return false;
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
        if (applied.Count == 0)
            return;

        var mutations = new List<KvBatchMutation>(applied.Count);
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            var action = applied[i];
            if (action.Value is null)
                mutations.Add(KvBatchMutation.Delete(action.Key));
            else
                mutations.Add(KvBatchMutation.Put(action.Key, action.Value));
        }

        _keyspace.ApplyBatch(mutations);
        applied.Clear();
    }

    private PreparedTableBatch PrepareBatchLocked(IReadOnlyList<TableRowMutation> mutations)
    {
        var schema = _schema;
        var normalizedMutations = new List<TableRowMutation>(mutations.Count);
        var autoIncrementRows = new List<(object?[] Values, bool AllocateMissing)>(mutations.Count);
        foreach (var mutation in mutations)
        {
            if (mutation.NewValues is null)
            {
                normalizedMutations.Add(mutation);
                continue;
            }

            var values = mutation.NewValues.ToArray();
            autoIncrementRows.Add((values, mutation.PrimaryKeyValues is null));
            normalizedMutations.Add(mutation with { NewValues = values });
        }

        ApplyAutoIncrementMutationsLocked(schema, autoIncrementRows);

        var operations = new List<TableMutationOperation>(normalizedMutations.Count);
        var pendingRowKeys = new HashSet<string>(StringComparer.Ordinal);
        var pendingUniqueKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mutation in normalizedMutations)
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

        ApplyMutationsLocked(batch.Schema, batch.Operations, batch.Applied);

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

    private sealed record IndexEntry(
        TableIndex Index,
        int IndexOrdinal,
        byte[] Key,
        byte[] Value);

    /// <summary>一页主行计算出的期望索引条目及其唯一约束信息。</summary>
    private sealed record DesiredIndexEntry(IndexEntry Entry, TableIndex? UniqueIndex);

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
