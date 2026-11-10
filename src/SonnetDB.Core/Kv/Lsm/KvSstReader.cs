using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.Kv.Lsm;

/// <summary>
/// 从 SST 中读出的一个条目版本。
/// </summary>
/// <param name="InternalKey">完整 internal key（独立副本）。</param>
/// <param name="Kind">条目类型。</param>
/// <param name="Sequence">条目 sequence。</param>
/// <param name="ExpiresAtUtcTicks">到期时间（UTC ticks），0 表示无 TTL；墓碑恒为 0。</param>
/// <param name="Value">value 字节；墓碑为空。</param>
internal readonly record struct KvSstEntry(
    byte[] InternalKey,
    KvEntryKind Kind,
    long Sequence,
    long ExpiresAtUtcTicks,
    ReadOnlyMemory<byte> Value)
{
    /// <summary>user key 部分。</summary>
    internal ReadOnlySpan<byte> UserKey => KvInternalKey.UserKey(InternalKey);

    internal static KvSstEntry Decode(ReadOnlySpan<byte> internalKey, ReadOnlyMemory<byte> encodedValue)
    {
        KvInternalKey.Validate(internalKey);
        KvEntryKind kind = KvInternalKey.Kind(internalKey);
        long sequence = KvInternalKey.Sequence(internalKey);
        if (kind == KvEntryKind.Delete)
        {
            if (!encodedValue.IsEmpty)
                throw new InvalidDataException("KV-LSM delete tombstone carries a value.");
            return new KvSstEntry(internalKey.ToArray(), kind, sequence, 0, ReadOnlyMemory<byte>.Empty);
        }

        int position = 0;
        long expires = KvLsmVarint.ReadInt64(encodedValue.Span, ref position);
        return new KvSstEntry(internalKey.ToArray(), kind, sequence, expires, encodedValue[position..]);
    }
}

/// <summary>
/// 不可变 SST 文件读取器：打开时只读取 footer、属性、索引与布隆过滤器，数据块按需读取并校验 CRC。
/// </summary>
/// <remarks>
/// 线程安全：打开后的状态只读，<see cref="RandomAccess"/> 位置无关读取可以并发。
/// 读取经过可选的 <see cref="KvDiskReadBudget"/>。任何结构或 CRC 错误抛出带文件路径的
/// <see cref="InvalidDataException"/>，不吞掉存储层异常。
/// </remarks>
internal sealed class KvSstReader : IDisposable
{
    private static long s_nextReaderId;
    private readonly SafeFileHandle _handle;
    private readonly KvDiskReadBudget? _readBudget;
    private readonly KvBlockCache? _blockCache;
    private readonly long _readerId = Interlocked.Increment(ref s_nextReaderId);
    private readonly KvSstBlock _index;
    private readonly KvBloomFilter _filter;
    private readonly KvBloomFilter? _prefixFilter;
    private readonly long _dataEnd;
    private readonly KvRangeTombstone[] _rangeTombstones;
    private int _disposed;

    private KvSstReader(
        string path,
        SafeFileHandle handle,
        long fileLength,
        KvDiskReadBudget? readBudget,
        KvSstProperties properties,
        KvSstBlock index,
        KvBloomFilter filter,
        long dataEnd,
        KvBlockCache? blockCache,
        KvRangeTombstone[] rangeTombstones,
        KvBloomFilter? prefixFilter)
    {
        _rangeTombstones = rangeTombstones;
        FilePath = path;
        _handle = handle;
        FileLength = fileLength;
        _readBudget = readBudget;
        _blockCache = blockCache;
        Properties = properties;
        _index = index;
        _filter = filter;
        _prefixFilter = prefixFilter;
        _dataEnd = dataEnd;
    }

    /// <summary>文件完整路径。</summary>
    internal string FilePath { get; }

    /// <summary>文件字节数。</summary>
    internal long FileLength { get; }

    /// <summary>属性块。</summary>
    internal KvSstProperties Properties { get; }

    /// <summary>文件中的 range tombstone（按起点升序）。</summary>
    internal IReadOnlyList<KvRangeTombstone> RangeTombstones => _rangeTombstones;

    /// <summary>打开并校验 SST 元数据。</summary>
    /// <exception cref="InvalidDataException">footer、属性、索引或过滤器无效。</exception>
    internal static KvSstReader Open(
        string path,
        KvDiskReadBudget? readBudget = null,
        CancellationToken cancellationToken = default,
        KvBlockCache? blockCache = null,
        bool maintenanceRead = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            long fileLength = RandomAccess.GetLength(handle);
            if (fileLength < KvSstFooter.Size)
                throw Corrupt(fullPath, "file is shorter than its footer");

            byte[] footerBytes = new byte[KvSstFooter.Size];
            ReadExact(handle, footerBytes, fileLength - KvSstFooter.Size, fullPath, readBudget, maintenanceRead, cancellationToken);
            KvSstFooter footer;
            try
            {
                footer = KvSstFooter.Read(footerBytes);
            }
            catch (InvalidDataException exception)
            {
                throw Corrupt(fullPath, exception.Message, exception);
            }

            long footerOffset = fileLength - KvSstFooter.Size;
            long trailer = KvSstFormat.BlockTrailerSize;
            bool hasRanges = footer.RangeDelLength > 0;
            long metadataEnd = hasRanges
                ? footer.RangeDelOffset
                : footerOffset;
            if (footer.FilterOffset < 0
                || footer.FilterLength <= 0
                || footer.IndexLength <= 0
                || footer.PropertiesLength <= 0
                || footer.IndexOffset != footer.FilterOffset + footer.FilterLength + trailer
                || footer.PropertiesOffset != footer.IndexOffset + footer.IndexLength + trailer
                || footer.PropertiesOffset + footer.PropertiesLength + trailer != metadataEnd
                || (hasRanges
                    && footer.RangeDelOffset + footer.RangeDelLength + trailer != footerOffset))
            {
                throw Corrupt(fullPath, "footer block layout is invalid");
            }

            KvSstProperties properties;
            KvSstBlock index;
            KvBloomFilter filter;
            KvBloomFilter? prefixFilter = null;
            KvRangeTombstone[] rangeTombstones;
            try
            {
                properties = KvSstProperties.Decode(ReadMetadataBlock(
                    handle, new KvSstBlockHandle(footer.PropertiesOffset, footer.PropertiesLength),
                    fullPath, readBudget, maintenanceRead, cancellationToken));
                // 索引与过滤器常驻读取器：直接持有 payload|trailer 缓冲区，避免打开时再复制一份。
                index = new KvSstBlock(ReadBlockCore(
                    handle, new KvSstBlockHandle(footer.IndexOffset, footer.IndexLength),
                    fullPath, readBudget, maintenanceRead, cancellationToken), footer.IndexLength);
                byte[] filterBytes = ReadBlockCore(
                    handle, new KvSstBlockHandle(footer.FilterOffset, footer.FilterLength),
                    fullPath, readBudget, maintenanceRead, cancellationToken);
                if (footer.FormatVersion == KvSstFormat.SharedPrefixVersion)
                {
                    if (!properties.SharedPrefixes || footer.FilterLength < 22)
                        throw new InvalidDataException("shared prefix properties or filter are missing");
                    int length = BinaryPrimitives.ReadInt32LittleEndian(filterBytes);
                    if (length < 9 || length > footer.FilterLength - 13)
                        throw new InvalidDataException("shared prefix filter lengths are invalid");
                    filter = KvBloomFilter.Parse(filterBytes, 4, length);
                    prefixFilter = KvBloomFilter.Parse(filterBytes, 4 + length, footer.FilterLength - 4 - length);
                }
                else
                {
                    if (properties.SharedPrefixes)
                        throw new InvalidDataException("shared prefix properties require SST v3");
                    filter = KvBloomFilter.Parse(filterBytes, footer.FilterLength);
                }
                rangeTombstones = hasRanges
                    ? KvSstWriter.DecodeRangeTombstones(ReadMetadataBlock(
                        handle, new KvSstBlockHandle(footer.RangeDelOffset, footer.RangeDelLength),
                        fullPath, readBudget, maintenanceRead, cancellationToken))
                    : [];
                if (rangeTombstones.Length != properties.RangeDeletionCount)
                    throw new InvalidDataException("range tombstone count does not match properties");
            }
            catch (InvalidDataException exception) when (!exception.Message.StartsWith("KV-LSM SST '", StringComparison.Ordinal))
            {
                throw Corrupt(fullPath, exception.Message, exception);
            }

            ValidateIndex(index, properties, footer.FilterOffset, fullPath);
            return new KvSstReader(fullPath, handle, fileLength, readBudget, properties, index, filter, footer.FilterOffset, blockCache, rangeTombstones, prefixFilter);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>布隆过滤器判断：false 表示文件中一定没有该 user key。</summary>
    internal bool MayContain(ReadOnlySpan<byte> userKey) => (userKey.Length < 8 || MayContainStoragePrefix(userKey[..8])) && _filter.MayContain(userKey);

    /// <summary>共享实例的表前缀过滤；范围墓碑始终按覆盖区间保留，不能产生假阴性。</summary>
    internal bool MayContainStoragePrefix(ReadOnlySpan<byte> prefix)
    {
        if (_prefixFilter is null || prefix.Length != 8)
            return true;
        if (_rangeTombstones.Length != 0)
        {
            byte[]? upper = KvLsmKeyspaceBackend.PrefixUpperBound(prefix.ToArray());
            foreach (KvRangeTombstone tombstone in _rangeTombstones)
                if (tombstone.End.AsSpan().SequenceCompareTo(prefix) > 0
                    && (upper is null || tombstone.Start.AsSpan().SequenceCompareTo(upper) < 0))
                    return true;
        }
        long id = BinaryPrimitives.ReadInt64BigEndian(prefix);
        return Properties.EntryCount > 0 && id >= Properties.MinStorageId && id <= Properties.MaxStorageId
            && _prefixFilter.MayContain(prefix);
    }

    /// <summary>
    /// 查找 <paramref name="userKey"/> 在 <paramref name="snapshotSequence"/> 可见的最新版本（可能是墓碑）。
    /// </summary>
    /// <returns>文件中存在可见版本时返回 true。</returns>
    internal bool TryGet(
        ReadOnlySpan<byte> userKey,
        long snapshotSequence,
        out KvSstEntry entry,
        CancellationToken cancellationToken = default)
    {
        entry = default;
        if (!MayContain(userKey))
            return false;
        if (userKey.SequenceCompareTo(KvInternalKey.UserKey(Properties.SmallestKey)) < 0
            || userKey.SequenceCompareTo(KvInternalKey.UserKey(Properties.LargestKey)) > 0)
        {
            return false;
        }

        using KvSstIterator iterator = CreateIterator(cancellationToken);
        iterator.Seek(KvInternalKey.EncodeSeekKey(userKey, snapshotSequence));
        if (!iterator.Valid || !KvInternalKey.UserKey(iterator.Key).SequenceEqual(userKey))
            return false;
        entry = iterator.Current;
        return true;
    }

    /// <summary>创建正向迭代器（创建后需定位）。</summary>
    /// <param name="cancellationToken">块读取的取消令牌。</param>
    /// <param name="maintenanceRead">true 时块读取走读取预算的维护通道（compaction），不占用也不受限于请求队列。</param>
    internal KvSstIterator CreateIterator(CancellationToken cancellationToken = default, bool maintenanceRead = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new KvSstIterator(this, _index.CreateIterator(), cancellationToken, maintenanceRead);
    }

    /// <summary>
    /// 全量校验：所有数据块 CRC、key 严格递增，以及条目数、墓碑数、sequence 范围与首尾 key 和属性一致。
    /// </summary>
    /// <exception cref="InvalidDataException">任一校验失败。</exception>
    internal void VerifyAll(CancellationToken cancellationToken = default)
    {
        bool ranged = _rangeTombstones.Length > 0;
        long entries = 0;
        long deletions = 0;
        long minSequence = long.MaxValue;
        long maxSequence = long.MinValue;
        byte[]? previous = null;
        using KvSstIterator iterator = CreateIterator(cancellationToken);
        for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            KvSstEntry current = iterator.Current;
            if (previous is not null && KvInternalKey.Compare(previous, current.InternalKey) >= 0)
                throw Corrupt(FilePath, "keys are not strictly increasing");
            if (previous is null
                && (ranged
                    ? KvInternalKey.Compare(current.InternalKey, Properties.SmallestKey) < 0
                    : !current.InternalKey.AsSpan().SequenceEqual(Properties.SmallestKey)))
            {
                throw Corrupt(FilePath, "first key does not match properties");
            }
            previous = current.InternalKey;
            entries++;
            if (current.Kind == KvEntryKind.Delete)
                deletions++;
            minSequence = Math.Min(minSequence, current.Sequence);
            maxSequence = Math.Max(maxSequence, current.Sequence);
        }

        foreach (KvRangeTombstone tombstone in _rangeTombstones)
        {
            minSequence = Math.Min(minSequence, tombstone.Sequence);
            maxSequence = Math.Max(maxSequence, tombstone.Sequence);
        }
        if (ranged
            ? previous is not null && KvInternalKey.Compare(previous, Properties.LargestKey) > 0
            : previous is null || !previous.AsSpan().SequenceEqual(Properties.LargestKey))
        {
            throw Corrupt(FilePath, "last key does not match properties");
        }
        if (entries != Properties.EntryCount
            || deletions != Properties.DeletionCount
            || minSequence != Properties.MinSequence
            || maxSequence != Properties.MaxSequence)
        {
            throw Corrupt(FilePath, "entry statistics do not match properties");
        }
    }

    /// <summary>释放文件句柄；已创建的迭代器随后不可再读取新块。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _handle.Dispose();
    }

    internal KvSstBlock ReadDataBlock(KvSstBlockHandle handle, CancellationToken cancellationToken, bool maintenanceRead = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (handle.Offset < 0 || handle.Length <= 0
            || handle.Offset + handle.Length + KvSstFormat.BlockTrailerSize > _dataEnd)
        {
            throw Corrupt(FilePath, $"data block handle ({handle.Offset}, {handle.Length}) is out of range");
        }

        if (_blockCache is not null && _blockCache.TryGet(_readerId, handle.Offset, out KvSstBlock cached))
            return cached;
        try
        {
            var block = new KvSstBlock(ReadBlockCore(_handle, handle, FilePath, _readBudget, maintenanceRead, cancellationToken), handle.Length);
            _blockCache?.Insert(_readerId, handle.Offset, block);
            return block;
        }
        catch (InvalidDataException exception) when (!exception.Message.StartsWith("KV-LSM SST '", StringComparison.Ordinal))
        {
            throw Corrupt(FilePath, exception.Message, exception);
        }
    }

    internal static InvalidDataException Corrupt(string path, string reason, Exception? inner = null)
        => new($"KV-LSM SST '{path}' is corrupt: {reason}.", inner);

    private static void ValidateIndex(KvSstBlock index, KvSstProperties properties, long dataEnd, string path)
    {
        KvSstBlockIterator iterator = index.CreateIterator();
        long expectedOffset = 0;
        int blocks = 0;
        // 复用同一缓冲区保存上一个索引 key，避免按数据块数量分配。
        byte[] previousBuffer = [];
        int previousLength = -1;
        try
        {
            for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
            {
                ReadOnlySpan<byte> key = iterator.Key;
                KvInternalKey.Validate(key);
                if (previousLength >= 0 && KvInternalKey.Compare(previousBuffer.AsSpan(0, previousLength), key) >= 0)
                    throw Corrupt(path, "index keys are not strictly increasing");
                KvSstBlockHandle handle = KvSstBlockHandle.Decode(iterator.Value);
                if (handle.Offset != expectedOffset || handle.Length <= 0)
                    throw Corrupt(path, "index block handles are not contiguous");
                expectedOffset = handle.Offset + handle.Length + KvSstFormat.BlockTrailerSize;
                if (previousBuffer.Length < key.Length)
                    previousBuffer = new byte[Math.Max(key.Length, previousBuffer.Length * 2)];
                key.CopyTo(previousBuffer);
                previousLength = key.Length;
                blocks++;
            }
        }
        catch (InvalidDataException exception) when (!exception.Message.StartsWith("KV-LSM SST '", StringComparison.Ordinal))
        {
            throw Corrupt(path, exception.Message, exception);
        }

        if (expectedOffset != dataEnd || blocks != properties.DataBlockCount)
            throw Corrupt(path, "index does not cover the data region");
        ReadOnlySpan<byte> previousKey = previousLength >= 0 ? previousBuffer.AsSpan(0, previousLength) : default;
        bool lastKeyValid = properties.RangeDeletionCount > 0
            ? previousLength < 0 || KvInternalKey.Compare(previousKey, properties.LargestKey) <= 0
            : previousLength >= 0 && previousKey.SequenceEqual(properties.LargestKey);
        if (!lastKeyValid)
            throw Corrupt(path, "last index key does not match properties");
    }

    /// <summary>读取并校验元数据块，返回独立的 payload 数组（元数据块小且只在打开时读取一次）。</summary>
    private static byte[] ReadMetadataBlock(
        SafeFileHandle handle,
        KvSstBlockHandle blockHandle,
        string path,
        KvDiskReadBudget? readBudget,
        bool maintenanceRead,
        CancellationToken cancellationToken)
        => ReadBlockCore(handle, blockHandle, path, readBudget, maintenanceRead, cancellationToken).AsSpan(0, blockHandle.Length).ToArray();

    /// <summary>
    /// 读取并校验一个块，返回 <c>payload | trailer</c> 缓冲区（payload 位于前 <see cref="KvSstBlockHandle.Length"/> 字节）。
    /// </summary>
    private static byte[] ReadBlockCore(
        SafeFileHandle handle,
        KvSstBlockHandle blockHandle,
        string path,
        KvDiskReadBudget? readBudget,
        bool maintenanceRead,
        CancellationToken cancellationToken)
    {
        // 一次物理读取 payload + 5 字节 trailer（读取预算按次计费）；块对象直接持有该缓冲区的 payload 部分，不再复制。
        byte[] buffer = new byte[checked(blockHandle.Length + KvSstFormat.BlockTrailerSize)];
        ReadExact(handle, buffer, blockHandle.Offset, path, readBudget, maintenanceRead, cancellationToken);
        byte compression = buffer[blockHandle.Length];
        uint expectedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(blockHandle.Length + 1));
        if (KvSstFormat.ComputeBlockCrc(buffer.AsSpan(0, blockHandle.Length), compression) != expectedCrc)
            throw Corrupt(path, $"block CRC mismatch at offset {blockHandle.Offset}");
        if (compression != KvSstFormat.CompressionNone)
            throw Corrupt(path, $"block compression type {compression} is not supported");
        return buffer;
    }

    private static void ReadExact(
        SafeFileHandle handle,
        Span<byte> destination,
        long offset,
        string path,
        KvDiskReadBudget? readBudget,
        bool maintenanceRead,
        CancellationToken cancellationToken)
    {
        using KvDiskReadBudget.ReadLease? lease = readBudget?.Acquire(cancellationToken, maintenanceRead);
        long readStarted = SonnetDB.Diagnostics.SonnetDbMeter.StartKvStateReadTiming();
        int total = 0;
        while (total < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read == 0)
                throw Corrupt(path, $"unexpected end of file at offset {offset + total}");
            total += read;
        }
        lease?.RecordRead(total);
        // 与 legacy state 读取相同：计入 KV state 读取指标，并归属到当前 SQL 执行的物理读统计。
        SonnetDB.Diagnostics.SonnetDbMeter.RecordKvStateRead(readStarted, total);
    }
}

/// <summary>
/// SST 双向迭代器：通过索引块逐个加载数据块。非线程安全。
/// </summary>
internal sealed class KvSstIterator : IKvInternalIterator
{
    private readonly KvSstReader _reader;
    private readonly KvSstBlockIterator _indexIterator;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _maintenanceRead;
    private KvSstBlockIterator? _dataIterator;

    internal KvSstIterator(KvSstReader reader, KvSstBlockIterator indexIterator, CancellationToken cancellationToken, bool maintenanceRead = false)
    {
        _reader = reader;
        _indexIterator = indexIterator;
        _cancellationToken = cancellationToken;
        _maintenanceRead = maintenanceRead;
    }

    /// <inheritdoc />
    public bool Valid => _dataIterator is { Valid: true };

    /// <inheritdoc />
    public ReadOnlySpan<byte> Key => Valid ? _dataIterator!.Key : throw InvalidPosition();

    /// <inheritdoc />
    public ReadOnlySpan<byte> Value
    {
        get
        {
            ReadOnlySpan<byte> encoded = Valid ? _dataIterator!.Value : throw InvalidPosition();
            if (KvInternalKey.Kind(_dataIterator.Key) == KvEntryKind.Delete)
                return [];
            try
            {
                int position = 0;
                KvLsmVarint.Read(encoded, ref position);
                return encoded[position..];
            }
            catch (InvalidDataException exception)
            {
                throw KvSstReader.Corrupt(_reader.FilePath, exception.Message, exception);
            }
        }
    }

    /// <inheritdoc />
    public long ExpiresAtUtcTicks
    {
        get
        {
            ReadOnlySpan<byte> encoded = Valid ? _dataIterator!.Value : throw InvalidPosition();
            if (KvInternalKey.Kind(_dataIterator.Key) == KvEntryKind.Delete)
                return 0;
            try
            {
                int position = 0;
                return KvLsmVarint.ReadInt64(encoded, ref position);
            }
            catch (InvalidDataException exception)
            {
                throw KvSstReader.Corrupt(_reader.FilePath, exception.Message, exception);
            }
        }
    }

    /// <summary>解码当前条目（独立副本）。</summary>
    internal KvSstEntry Current
    {
        get
        {
            if (!Valid)
                throw InvalidPosition();
            try
            {
                return KvSstEntry.Decode(_dataIterator!.Key, _dataIterator.ValueMemory);
            }
            catch (InvalidDataException exception)
            {
                throw KvSstReader.Corrupt(_reader.FilePath, exception.Message, exception);
            }
        }
    }

    /// <inheritdoc />
    public void SeekToFirst() => Guard(() =>
    {
        _indexIterator.SeekToFirst();
        LoadDataBlock();
        _dataIterator?.SeekToFirst();
        SkipEmptyBlocksForward();
    });

    /// <inheritdoc />
    public void SeekToLast() => Guard(() =>
    {
        _indexIterator.SeekToLast();
        LoadDataBlock();
        _dataIterator?.SeekToLast();
        SkipEmptyBlocksBackward();
    });

    /// <inheritdoc />
    public void Seek(ReadOnlySpan<byte> target)
    {
        // 索引 key 是各数据块的最后一个 key，第一个 ≥ target 的索引项指向唯一可能包含 target 的块。
        try
        {
            _indexIterator.Seek(target);
            LoadDataBlock();
            _dataIterator?.Seek(target);
            SkipEmptyBlocksForward();
        }
        catch (InvalidDataException exception) when (!IsWrapped(exception))
        {
            throw KvSstReader.Corrupt(_reader.FilePath, exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public void SeekForPrev(ReadOnlySpan<byte> target)
    {
        Seek(target);
        if (!Valid)
            SeekToLast();
        else if (KvInternalKey.Compare(Key, target) > 0)
            MovePrev();
    }

    /// <inheritdoc />
    public void MoveNext()
    {
        if (!Valid)
            throw InvalidPosition();
        try
        {
            _dataIterator!.MoveNext();
            SkipEmptyBlocksForward();
        }
        catch (InvalidDataException exception) when (!IsWrapped(exception))
        {
            throw KvSstReader.Corrupt(_reader.FilePath, exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public void MovePrev()
    {
        if (!Valid)
            throw InvalidPosition();
        try
        {
            _dataIterator!.MovePrev();
            SkipEmptyBlocksBackward();
        }
        catch (InvalidDataException exception) when (!IsWrapped(exception))
        {
            throw KvSstReader.Corrupt(_reader.FilePath, exception.Message, exception);
        }
    }

    /// <summary>迭代器不持有非托管资源；保留以便与合并迭代器统一生命周期。</summary>
    public void Dispose() => _dataIterator = null;

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException exception) when (!IsWrapped(exception))
        {
            throw KvSstReader.Corrupt(_reader.FilePath, exception.Message, exception);
        }
    }

    private static bool IsWrapped(InvalidDataException exception)
        => exception.Message.StartsWith("KV-LSM SST '", StringComparison.Ordinal);

    private void SkipEmptyBlocksForward()
    {
        while (_dataIterator is { Valid: false } && _indexIterator.Valid)
        {
            _indexIterator.MoveNext();
            LoadDataBlock();
            _dataIterator?.SeekToFirst();
        }
    }

    private void SkipEmptyBlocksBackward()
    {
        while (_dataIterator is { Valid: false } && _indexIterator.Valid)
        {
            _indexIterator.MovePrev();
            LoadDataBlock();
            _dataIterator?.SeekToLast();
        }
    }

    private void LoadDataBlock()
    {
        if (!_indexIterator.Valid)
        {
            _dataIterator = null;
            return;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        KvSstBlockHandle handle = KvSstBlockHandle.Decode(_indexIterator.Value);
        _dataIterator = _reader.ReadDataBlock(handle, _cancellationToken, _maintenanceRead).CreateIterator();
    }

    private static InvalidOperationException InvalidPosition()
        => new("KV-LSM SST iterator is not positioned on an entry.");
}
