using System.Buffers.Binary;
using System.Text;
using SonnetDB.Catalog;
using SonnetDB.Kv;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Engine;

/// <summary>
/// 使用内部 KV 原子批次 WAL 保存 measurement VECTOR 的逻辑替换值。
/// 旧时序点仍留在原段；查询按逻辑键只暴露本表中的替换值。
/// </summary>
internal sealed class MeasurementVectorReplacementStore
{
    internal const int MaxReplacements = 4096;
    internal const int MaxRowsPerUpdate = 256;
    internal const long MaxReplacementBytes = 128L * 1024 * 1024;
    private const int RecoveryPageSize = 64;
    private const byte KeyVersion = 1;
    private const byte ValueVersion = 1;

    private readonly object _sync = new();
    private readonly KvKeyspaceManager _keyspaces;
    private KvKeyspace? _keyspace;
    private ReplacementSnapshot _snapshot = CreateSnapshot([]);
    private Exception? _writeFault;

    private sealed record ReplacementSnapshot(
        Dictionary<(ulong SeriesId, string FieldName, long Timestamp), FieldValue> Values,
        HashSet<(ulong SeriesId, string FieldName)> SeriesFields);

    internal MeasurementVectorReplacementStore(
        KvKeyspaceManager keyspaces,
        SeriesCatalog catalog,
        MeasurementCatalog measurements)
    {
        _keyspaces = keyspaces;
        string path = keyspaces.MeasurementVectorReplacementDirectory;
        if (!Directory.Exists(path))
            return;

        _keyspace = keyspaces.OpenMeasurementVectorReplacements();
        var loaded = new Dictionary<(ulong, string, long), FieldValue>();
        var stale = new List<KvBatchMutation>();
        long loadedBytes = 0;
        int scanned = 0;
        byte[] afterKey = [];
        while (true)
        {
            var entries = _keyspace.ScanPrefixAfter([], afterKey, RecoveryPageSize);
            if (entries.Count == 0)
                break;
            foreach (var entry in entries)
            {
                if (++scanned > MaxReplacements)
                    throw new InvalidDataException("measurement VECTOR 替换记录超过恢复上限。");
                var key = DecodeKey(entry.Key.Span);
                SeriesEntry? series = catalog.TryGet(key.SeriesId);
                MeasurementColumn? column = series is null
                    ? null
                    : measurements.TryGet(series.Measurement)?.TryGetColumn(key.FieldName);
                if (column is null)
                {
                    stale.Add(KvBatchMutation.Delete(entry.Key.ToArray()));
                    continue;
                }
                if (column.DataType != FieldType.Vector)
                    throw new InvalidDataException("measurement VECTOR 替换记录与 schema 类型不一致。");

                var value = DecodeValue(entry.Value.Span);
                if (value.VectorDimension != column.VectorDimension)
                    throw new InvalidDataException("measurement VECTOR 替换记录与 schema 维度不一致。");
                loadedBytes += EstimateBytes(key, value);
                if (loadedBytes > MaxReplacementBytes)
                    throw new InvalidDataException("measurement VECTOR 替换记录超过恢复字节预算。");
                loaded.Add(key, value);
            }
            afterKey = entries[^1].Key.ToArray();
        }

        _snapshot = CreateSnapshot(loaded);
        if (stale.Count != 0)
            _keyspace.ApplyBatch(stale);
    }

    internal object SyncRoot => _sync;

    internal void EnsureHealthy() => ThrowIfWriteFaulted();

    internal void Invalidate(Exception exception) => Volatile.Write(ref _writeFault, exception);

    internal bool TryGet(ulong seriesId, string fieldName, long timestamp, out FieldValue value)
    {
        ThrowIfWriteFaulted();
        return Volatile.Read(ref _snapshot).Values.TryGetValue((seriesId, fieldName, timestamp), out value);
    }

    internal (IReadOnlyDictionary<(ulong SeriesId, string FieldName, long Timestamp), FieldValue> Values,
        IReadOnlySet<(ulong SeriesId, string FieldName)> SeriesFields) Snapshot()
    {
        ThrowIfWriteFaulted();
        var snapshot = Volatile.Read(ref _snapshot);
        return (snapshot.Values, snapshot.SeriesFields);
    }

    internal bool HasSeriesField(ulong seriesId, string fieldName)
    {
        ThrowIfWriteFaulted();
        return Volatile.Read(ref _snapshot).SeriesFields.Contains((seriesId, fieldName));
    }

    internal int Count
    {
        get
        {
            ThrowIfWriteFaulted();
            return Volatile.Read(ref _snapshot).Values.Count;
        }
    }

    internal void RemoveCovered(ulong seriesId, string fieldName, long from, long to)
        => RemoveWhere(key => key.SeriesId == seriesId
            && string.Equals(key.FieldName, fieldName, StringComparison.Ordinal)
            && key.Timestamp >= from && key.Timestamp <= to);

    internal void RemoveSeries(IReadOnlySet<ulong> seriesIds)
        => RemoveWhere(key => seriesIds.Contains(key.SeriesId));

    internal void PruneStale(Func<ulong, string, long, bool> isLive)
    {
        ArgumentNullException.ThrowIfNull(isLive);
        RemoveWhere(key => !isLive(key.SeriesId, key.FieldName, key.Timestamp));
    }

    private void RemoveWhere(Func<(ulong SeriesId, string FieldName, long Timestamp), bool> predicate)
    {
        lock (_sync)
        {
            ThrowIfWriteFaulted();
            var current = Volatile.Read(ref _snapshot).Values;
            var keys = current.Keys.Where(predicate).ToArray();
            if (keys.Length == 0)
                return;
            var batch = keys.Select(key => KvBatchMutation.Delete(EncodeKey(key))).ToArray();
            try
            {
                _keyspace!.ApplyBatch(batch);
            }
            catch (Exception ex)
            {
                if (_keyspace!.IsWriteCommitOutcomeUnknown(ex))
                    Invalidate(ex);
                throw;
            }
            var next = new Dictionary<(ulong, string, long), FieldValue>(current);
            foreach (var key in keys)
                next.Remove(key);
            Volatile.Write(ref _snapshot, CreateSnapshot(next));
        }
    }

    internal Action? WalSyncTestHook
    {
        set
        {
            _keyspace ??= _keyspaces.OpenMeasurementVectorReplacements();
            _keyspace.WalSyncTestHook = value;
        }
    }

    internal void ReplaceMany(
        IReadOnlyList<(ulong SeriesId, long Timestamp)> targets,
        string fieldName,
        FieldValue value)
    {
        if (targets.Count == 0)
            return;
        if (targets.Count > MaxRowsPerUpdate)
            throw new InvalidOperationException($"measurement VECTOR UPDATE 单次最多修改 {MaxRowsPerUpdate} 行。");
        if (value.Type != FieldType.Vector)
            throw new ArgumentException("替换值必须是 VECTOR。", nameof(value));

        lock (_sync)
        {
            ThrowIfWriteFaulted();
            var current = Volatile.Read(ref _snapshot).Values;
            var next = new Dictionary<(ulong, string, long), FieldValue>(current);
            for (int i = 0; i < targets.Count; i++)
            {
                var key = (targets[i].SeriesId, fieldName, targets[i].Timestamp);
                next[key] = value;
            }
            if (next.Count > MaxReplacements)
                throw new InvalidOperationException($"measurement VECTOR 替换记录最多保留 {MaxReplacements} 项。");
            long nextBytes = 0;
            foreach (var entry in next)
            {
                nextBytes += EstimateBytes(entry.Key, entry.Value);
                if (nextBytes > MaxReplacementBytes)
                    throw new InvalidOperationException($"measurement VECTOR 替换记录最多保留 {MaxReplacementBytes} 字节。");
            }

            var batch = new KvBatchMutation[targets.Count];
            var encodedValue = EncodeValue(value);
            for (int i = 0; i < targets.Count; i++)
            {
                var key = (targets[i].SeriesId, fieldName, targets[i].Timestamp);
                batch[i] = KvBatchMutation.Put(EncodeKey(key), encodedValue);
            }

            _keyspace ??= _keyspaces.OpenMeasurementVectorReplacements();
            try
            {
                _keyspace.ApplyBatch(_ => batch);
            }
            catch (Exception ex)
            {
                if (_keyspace.IsWriteCommitOutcomeUnknown(ex))
                    Volatile.Write(ref _writeFault, ex);
                throw;
            }
            Volatile.Write(ref _snapshot, CreateSnapshot(next));
        }
    }

    private static ReplacementSnapshot CreateSnapshot(
        Dictionary<(ulong SeriesId, string FieldName, long Timestamp), FieldValue> values)
        => new(values, values.Keys.Select(static key => (key.SeriesId, key.FieldName)).ToHashSet());

    private static long EstimateBytes(
        (ulong SeriesId, string FieldName, long Timestamp) key,
        FieldValue value)
        => 64L + Encoding.UTF8.GetByteCount(key.FieldName)
            + (long)value.VectorDimension * sizeof(float);

    private void ThrowIfWriteFaulted()
    {
        if (Volatile.Read(ref _writeFault) is { } fault)
            throw new InvalidOperationException(
                "VECTOR UPDATE 的提交结果不确定；关闭并重新打开数据库以恢复一致视图。",
                fault);
    }

    private static byte[] EncodeKey((ulong SeriesId, string FieldName, long Timestamp) key)
    {
        int nameBytes = Encoding.UTF8.GetByteCount(key.FieldName);
        if (nameBytes > ushort.MaxValue)
            throw new InvalidOperationException("measurement VECTOR FIELD 名称过长。");
        var bytes = new byte[1 + 8 + 2 + nameBytes + 8];
        bytes[0] = KeyVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1), key.SeriesId);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), checked((ushort)nameBytes));
        Encoding.UTF8.GetBytes(key.FieldName, bytes.AsSpan(11, nameBytes));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(11 + nameBytes), key.Timestamp);
        return bytes;
    }

    private static (ulong SeriesId, string FieldName, long Timestamp) DecodeKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 19 || bytes[0] != KeyVersion)
            throw new InvalidDataException("measurement VECTOR 替换记录 key 格式无效。");
        int nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes[9..]);
        if (nameBytes == 0 || bytes.Length != 19 + nameBytes)
            throw new InvalidDataException("measurement VECTOR 替换记录 key 长度无效。");
        return (
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[1..]),
            Encoding.UTF8.GetString(bytes.Slice(11, nameBytes)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[(11 + nameBytes)..]));
    }

    private static byte[] EncodeValue(FieldValue value)
    {
        var vector = value.AsVector().Span;
        var bytes = new byte[5 + vector.Length * 4];
        bytes[0] = ValueVersion;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), vector.Length);
        for (int i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(5 + i * 4), vector[i]);
        return bytes;
    }

    private static FieldValue DecodeValue(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 9 || bytes[0] != ValueVersion)
            throw new InvalidDataException("measurement VECTOR 替换记录 value 格式无效。");
        int dimension = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        if (dimension <= 0 || bytes.Length != 5L + dimension * 4L)
            throw new InvalidDataException("measurement VECTOR 替换记录维度无效。");
        var vector = new float[dimension];
        for (int i = 0; i < dimension; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(5 + i * 4)..]);
            if (!float.IsFinite(vector[i]))
                throw new InvalidDataException("measurement VECTOR 替换记录含非有限数值。");
        }
        return FieldValue.FromVector(vector);
    }
}
