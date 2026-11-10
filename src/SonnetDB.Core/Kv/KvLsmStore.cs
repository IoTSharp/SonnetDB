using System.Buffers.Binary;
using System.Text;
using SonnetDB.Kv.Lsm;

namespace SonnetDB.Kv;

/// <summary>
/// 一个数据库内多个逻辑 keyspace 共享的 LSM 实例（决策 D4，K6）：一个 WAL、一组 memtable、一套 manifest/SST，
/// 按 8 字节大端 <c>StorageId</c> 前缀区分逻辑 keyspace。
/// </summary>
/// <remarks>
/// <para>
/// <c>StorageId = 0</c> 是保留的元数据前缀：<c>ks|id</c> 记录名称与 generation，<c>sq|id</c> 记录该逻辑 keyspace 最后一次提交的
/// sequence，<c>next-id</c> 记录下一个可分配编号。编号单调分配、永不复用。元数据与数据在同一原子批次中提交。
/// </para>
/// <para>
/// 逻辑 keyspace 通过 <see cref="KvKeyspace.OpenShared"/> 打开；关闭逻辑 keyspace 不会关闭本实例，由所有者释放。
/// </para>
/// </remarks>
internal sealed class KvLsmStore : IDisposable
{
    private const byte RecordFormatVersion = 1;
    private static readonly byte[] MetaPrefix = new byte[8];
    private static readonly byte[] KeyspaceTag = "ks"u8.ToArray();
    private static readonly byte[] SequenceTag = "sq"u8.ToArray();
    private static readonly byte[] MaintenanceTag = "maint"u8.ToArray();
    private static readonly byte[] NextIdKey = [.. MetaPrefix, .. "next-id"u8];

    private readonly KvLsmEngine _engine;
    private readonly object _sync = new();
    private readonly Dictionary<long, KeyspaceInfo> _keyspaces = [];
    private long _nextId;
    private bool _disposed;

    private KvLsmStore(string rootDirectory, KvLsmEngine engine)
    {
        RootDirectory = rootDirectory;
        _engine = engine;
    }

    /// <summary>实例根目录。</summary>
    internal string RootDirectory { get; }

    /// <summary>底层引擎（逻辑 keyspace 与诊断使用）。</summary>
    internal KvLsmEngine Engine => _engine;

    /// <summary>打开（或在空目录创建）共享实例并加载逻辑 keyspace 元数据。</summary>
    /// <exception cref="InvalidDataException">元数据损坏。</exception>
    internal static KvLsmStore Open(string rootDirectory, KvOptions options, KvDiskReadBudget readBudget)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readBudget);
        string fullRoot = Path.GetFullPath(rootDirectory);
        KvLsmEngine engine = KvLsmEngine.Open(fullRoot, KvLsmKeyspaceBackend.CreateEngineOptions(options, readBudget) with
        {
            SstWriterOptions = new KvSstWriterOptions { SharedPrefixes = true },
        });
        var store = new KvLsmStore(fullRoot, engine);
        try
        {
            store.LoadMetadata();
            return store;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>逻辑 keyspace 的物理 key 前缀（8 字节大端编号）。</summary>
    internal static byte[] Prefix(long storageId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storageId);
        byte[] prefix = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(prefix, storageId);
        return prefix;
    }

    /// <summary>已登记的逻辑 keyspace（编号与名称）。</summary>
    internal IReadOnlyList<(long StorageId, string Name)> List()
    {
        lock (_sync)
            return [.. _keyspaces.OrderBy(static pair => pair.Key).Select(static pair => (pair.Key, pair.Value.Name))];
    }

    /// <summary>编号是否已登记。</summary>
    internal bool Contains(long storageId)
    {
        lock (_sync)
            return _keyspaces.ContainsKey(storageId);
    }

    /// <summary>分配新的逻辑 keyspace 编号并持久化其元数据（与 next-id 同批提交）。</summary>
    internal long Create(string name, long generation = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        return _engine.ExecuteUnderWriteLock(() =>
        {
            ThrowIfDisposed();
            _engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            long id;
            lock (_sync)
                id = _nextId;
            long sequence = _engine.NextSequence;
            byte[] nextId = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(nextId, id + 1);
            var info = new KeyspaceInfo(name, Generation: generation, CreatedSequence: sequence, LastSequence: sequence);
            _engine.ApplyUnderWriteLock(
                [
                    new KvBatchMutation(KeyspaceKey(id), EncodeRecord(info), null),
                    new KvBatchMutation(SequenceKey(id), EncodeSequence(sequence), null),
                    new KvBatchMutation(NextIdKey, nextId, null),
                ],
                [],
                sequence,
                CancellationToken.None);
            lock (_sync)
            {
                _keyspaces[id] = info;
                _nextId = id + 1;
            }
            return id;
        }, CancellationToken.None, lockTimeout: null);
    }

    /// <summary>删除逻辑 keyspace：同一批次写入覆盖其全部数据的 range tombstone 并删除元数据。编号不再复用。</summary>
    /// <returns>编号存在并已删除时返回 true。</returns>
    internal bool Drop(long storageId)
    {
        return _engine.ExecuteUnderWriteLock(() =>
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                if (!_keyspaces.ContainsKey(storageId))
                    return false;
            }
            _engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            byte[] prefix = Prefix(storageId);
            byte[] maintenance = MaintenancePrefix(storageId);
            _engine.ApplyUnderWriteLock(
                [KvBatchMutation.Delete(KeyspaceKey(storageId)), KvBatchMutation.Delete(SequenceKey(storageId))],
                [new KvRangeDeletion(prefix, KvLsmKeyspaceBackend.PrefixUpperBound(prefix)!),
                    new KvRangeDeletion(maintenance, KvLsmKeyspaceBackend.PrefixUpperBound(maintenance)!)],
                null,
                CancellationToken.None);
            lock (_sync)
                _keyspaces.Remove(storageId);
            return true;
        }, CancellationToken.None, lockTimeout: null);
    }

    /// <summary>逻辑 keyspace 的名称。</summary>
    internal string GetName(long storageId) => Get(storageId).Name;

    /// <summary>
    /// 把一批用户 key 的变更原子写入逻辑 keyspace（迁移导入用），与 sequence 元数据同批提交；返回提交 sequence。
    /// </summary>
    internal long Import(long storageId, IReadOnlyList<KvBatchMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        byte[] prefix = Prefix(storageId);
        return _engine.ExecuteUnderWriteLock(() =>
        {
            ThrowIfDisposed();
            _ = Get(storageId);
            _engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            long next = _engine.NextSequence;
            var physical = new List<KvBatchMutation>(mutations.Count + 1);
            foreach (KvBatchMutation mutation in mutations)
                physical.Add(new KvBatchMutation([.. prefix, .. mutation.Key], mutation.Value, mutation.ExpiresAtUtc));
            physical.Add(SequenceMutation(storageId, next));
            long sequence = _engine.ApplyUnderWriteLock(physical, [], next, CancellationToken.None);
            OnCommitted(storageId, sequence);
            return sequence;
        }, CancellationToken.None, lockTimeout: null);
    }

    /// <summary>逻辑 keyspace 当前 generation。</summary>
    internal long GetGeneration(long storageId) => Get(storageId).Generation;

    /// <summary>逻辑 keyspace 最后一次提交的 sequence（创建时为创建批次的 sequence）。</summary>
    internal long GetLastSequence(long storageId) => Get(storageId).LastSequence;

    /// <summary>逻辑 keyspace 的已提交创建 sequence，兼容旧空表迁移记录。</summary>
    internal long GetCreatedSequence(long storageId) => Get(storageId).CreatedSequence;

    internal static byte[] MaintenancePrefix(long storageId) => MetaKey(MaintenanceTag, storageId);

    private static byte[] MaintenanceKey(long storageId, string name)
        => [.. MaintenancePrefix(storageId), .. Encoding.UTF8.GetBytes(name)];

    /// <summary>读取派生的表维护状态；读取本身不追加 WAL。</summary>
    internal byte[]? ReadMaintenance(long storageId, string name)
    {
        _ = Get(storageId);
        using KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.MinValue);
        return snapshot.GetRaw(MaintenanceKey(storageId, name)) is { } value ? value.Value.ToArray() : null;
    }

    /// <summary>持久写入或消费表维护状态，不改变逻辑数据 sequence，因而不伪造迁移后业务写入。</summary>
    internal void WriteMaintenance(long storageId, string name, byte[]? value)
    {
        _engine.ExecuteUnderWriteLock(() =>
        {
            _ = Get(storageId);
            _engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            _engine.ApplyUnderWriteLock([new KvBatchMutation(MaintenanceKey(storageId, name), value, null)], [], null, CancellationToken.None);
            if (!_engine.SyncWalOnEveryWrite)
                _engine.SyncWal();
            return 0;
        }, CancellationToken.None, lockTimeout: null);
    }

    internal int? ReadRowCount(long storageId)
    {
        byte[]? value = ReadMaintenance(storageId, "row-count");
        KeyspaceInfo info = Get(storageId);
        if (value is not { Length: 25 } || value[0] != 1
            || BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(1)) != info.Generation
            || BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(9)) != info.LastSequence)
            return null;
        long count = BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(17));
        return count is >= 0 and <= int.MaxValue ? (int)count : null;
    }

    internal void InitializeRowCount(long storageId, int count)
        => WriteMaintenance(storageId, "row-count", EncodeRowCount(GetGeneration(storageId), GetLastSequence(storageId), count));

    /// <summary>主行计数与行、索引、sequence 在同一个 WAL 记录中发布。旧写者改变 sequence 后旧计数自动失效。</summary>
    internal KvBatchMutation? RowCountMutation(long storageId, long sequence, IReadOnlyList<KvBatchMutation> mutations)
    {
        if (ReadRowCount(storageId) is not int count)
            return null;
        byte[] prefix = Prefix(storageId);
        using KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.MinValue);
        foreach (KvBatchMutation mutation in mutations)
        {
            if (mutation.Key.Length == 0 || mutation.Key[0] != (byte)'r')
                continue;
            bool existed = snapshot.GetRaw([.. prefix, .. mutation.Key]) is not null;
            if (existed != (mutation.Value is not null))
                count = checked(count + (existed ? -1 : 1));
        }
        return new KvBatchMutation(MaintenanceKey(storageId, "row-count"), EncodeRowCount(GetGeneration(storageId), sequence, count), null);
    }

    internal KvBatchMutation EmptyRowCountMutation(long storageId, long generation, long sequence)
        => new(MaintenanceKey(storageId, "row-count"), EncodeRowCount(generation, sequence, 0), null);

    private static byte[] EncodeRowCount(long generation, long sequence, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        byte[] value = new byte[25];
        value[0] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(1), generation);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(9), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(17), count);
        return value;
    }

    /// <summary>与数据同批提交的 sequence 元数据。</summary>
    internal static KvBatchMutation SequenceMutation(long storageId, long sequence)
        => new(SequenceKey(storageId), EncodeSequence(sequence), null);

    /// <summary>Clear 批次中的元数据：generation + 1 的 keyspace 记录。</summary>
    internal KvBatchMutation ClearRecordMutation(long storageId, out long nextGeneration)
    {
        KeyspaceInfo info = Get(storageId);
        nextGeneration = checked(info.Generation + 1);
        return new KvBatchMutation(KeyspaceKey(storageId), EncodeRecord(info with { Generation = nextGeneration }), null);
    }

    /// <summary>数据批次提交后更新内存中的 sequence。</summary>
    internal void OnCommitted(long storageId, long sequence)
    {
        lock (_sync)
        {
            if (_keyspaces.TryGetValue(storageId, out KeyspaceInfo? info) && sequence > info.LastSequence)
                _keyspaces[storageId] = info with { LastSequence = sequence };
        }
    }

    /// <summary>Clear 批次提交后更新内存中的 generation 与 sequence。</summary>
    internal void OnCleared(long storageId, long generation, long sequence)
    {
        lock (_sync)
        {
            if (_keyspaces.TryGetValue(storageId, out KeyspaceInfo? info))
                _keyspaces[storageId] = info with { Generation = generation, LastSequence = Math.Max(info.LastSequence, sequence) };
        }
    }

    /// <summary>关闭共享实例（所有逻辑 keyspace 应已关闭）。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _engine.Dispose();
    }

    private KeyspaceInfo Get(long storageId)
    {
        lock (_sync)
        {
            return _keyspaces.TryGetValue(storageId, out KeyspaceInfo? info)
                ? info
                : throw new KeyNotFoundException($"KV-LSM shared store '{RootDirectory}' has no logical keyspace #{storageId}.");
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void LoadMetadata()
    {
        var sequences = new Dictionary<long, long>();
        long nextId = 1;
        using KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.MinValue.ToUniversalTime());
        using KvSnapshotIterator iterator = snapshot.CreateIterator(MetaPrefix, KvLsmKeyspaceBackend.PrefixUpperBound(MetaPrefix));
        for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
        {
            ReadOnlySpan<byte> key = iterator.Key;
            if (!key.StartsWith(MetaPrefix))
                break;
            ReadOnlySpan<byte> tail = key[MetaPrefix.Length..];
            if (tail.SequenceEqual("next-id"u8))
            {
                nextId = DecodeInt64(iterator.Value, "next-id");
            }
            else if (tail.Length == 10 && tail.StartsWith(KeyspaceTag))
            {
                long id = BinaryPrimitives.ReadInt64BigEndian(tail[2..]);
                _keyspaces[id] = DecodeRecord(iterator.Value, id);
            }
            else if (tail.Length == 10 && tail.StartsWith(SequenceTag))
            {
                sequences[BinaryPrimitives.ReadInt64BigEndian(tail[2..])] = DecodeInt64(iterator.Value, "sequence");
            }
        }

        foreach ((long id, KeyspaceInfo info) in _keyspaces.ToArray())
        {
            if (id <= 0 || id >= nextId)
                throw new InvalidDataException($"KV-LSM shared store '{RootDirectory}' has keyspace #{id} outside the allocated range.");
            if (sequences.TryGetValue(id, out long sequence))
                _keyspaces[id] = info with { LastSequence = Math.Max(info.CreatedSequence, sequence) };
        }
        _nextId = Math.Max(1, nextId);
    }

    private static byte[] KeyspaceKey(long storageId) => MetaKey(KeyspaceTag, storageId);

    private static byte[] SequenceKey(long storageId) => MetaKey(SequenceTag, storageId);

    private static byte[] MetaKey(byte[] tag, long storageId)
    {
        byte[] key = new byte[MetaPrefix.Length + tag.Length + 8];
        tag.CopyTo(key, MetaPrefix.Length);
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(MetaPrefix.Length + tag.Length), storageId);
        return key;
    }

    private static byte[] EncodeSequence(long sequence)
    {
        byte[] value = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(value, sequence);
        return value;
    }

    private static long DecodeInt64(ReadOnlySpan<byte> value, string what)
        => value.Length == 8
            ? BinaryPrimitives.ReadInt64LittleEndian(value)
            : throw new InvalidDataException($"KV-LSM shared store {what} metadata is invalid.");

    private static byte[] EncodeRecord(KeyspaceInfo info)
    {
        byte[] name = Encoding.UTF8.GetBytes(info.Name);
        byte[] value = new byte[1 + 8 + 8 + name.Length];
        value[0] = RecordFormatVersion;
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(1, 8), info.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(9, 8), info.CreatedSequence);
        name.CopyTo(value, 17);
        return value;
    }

    private static KeyspaceInfo DecodeRecord(ReadOnlySpan<byte> value, long storageId)
    {
        if (value.Length < 17 || value[0] != RecordFormatVersion)
            throw new InvalidDataException($"KV-LSM shared store keyspace record #{storageId} is invalid.");
        long generation = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(1, 8));
        long created = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(9, 8));
        if (generation < 0 || created < 0)
            throw new InvalidDataException($"KV-LSM shared store keyspace record #{storageId} is invalid.");
        return new KeyspaceInfo(Encoding.UTF8.GetString(value[17..]), generation, created, created);
    }

    private sealed record KeyspaceInfo(string Name, long Generation, long CreatedSequence, long LastSequence);
}
