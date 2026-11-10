using SonnetDB.Buffers;
using SonnetDB.Wal;

namespace SonnetDB.Kv.Lsm;

/// <summary>
/// SST 写入选项。
/// </summary>
internal sealed record KvSstWriterOptions
{
    /// <summary>默认选项。</summary>
    internal static KvSstWriterOptions Default { get; } = new();

    /// <summary>数据块目标 payload 字节数；块在超过该值后切分。</summary>
    internal int BlockSize { get; init; } = 16 * 1024;

    /// <summary>数据块 restart 间隔。</summary>
    internal int RestartInterval { get; init; } = 16;

    /// <summary>布隆过滤器每个 key 的位数。</summary>
    internal int BloomBitsPerKey { get; init; } = KvBloomFilter.DefaultBitsPerKey;

    /// <summary>写入属性块的 keyspace generation。</summary>
    internal long Generation { get; init; }

    /// <summary>为 true 时在发布后对所在目录执行 fsync。</summary>
    internal bool SyncDirectory { get; init; } = true;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BlockSize, 256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RestartInterval);
        ArgumentOutOfRangeException.ThrowIfLessThan(BloomBitsPerKey, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BloomBitsPerKey, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(Generation);
    }
}

/// <summary>
/// 顺序写出一个不可变 SST 文件：<c>DataBlock* | Filter | Index | Properties | Footer</c>。
/// </summary>
/// <remarks>
/// 先写入 <c>&lt;path&gt;.tmp</c>，<see cref="Finish"/> 时 fsync、改名为最终路径并（可选）对目录 fsync；
/// 未完成即释放时删除临时文件。目标文件已存在时拒绝覆盖。
/// 条目 value 编码：<see cref="KvEntryKind.Put"/> 为 <c>varint expiresAtUtcTicks | value</c>，
/// <see cref="KvEntryKind.Delete"/> 为空。
/// </remarks>
internal sealed class KvSstWriter : IDisposable
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly KvSstWriterOptions _options;
    private readonly KvSstBlockBuilder _dataBlock;
    private readonly KvSstBlockBuilder _indexBlock = new(restartInterval: 1);
    private readonly List<ulong> _bloomHashes = [];
    private readonly List<KvRangeTombstone> _rangeTombstones = [];
    private FileStream? _stream;
    private long _offset;
    private byte[] _lastKey = [];
    private int _lastKeyLength;
    private byte[]? _smallestKey;
    private byte[] _valueBuffer = new byte[256];
    private long _entryCount;
    private long _deletionCount;
    private int _dataBlockCount;
    private long _minSequence = long.MaxValue;
    private long _maxSequence = long.MinValue;
    private long _minExpiresAtUtcTicks;
    private long _rawKeyBytes;
    private long _rawValueBytes;
    private bool _finished;

    /// <summary>创建写入器。</summary>
    /// <exception cref="IOException">目标文件或临时文件已存在。</exception>
    internal KvSstWriter(string path, KvSstWriterOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _options = options ?? KvSstWriterOptions.Default;
        _options.Validate();
        _path = Path.GetFullPath(path);
        _tempPath = _path + ".tmp";
        if (File.Exists(_path))
            throw new IOException($"KV-LSM SST '{_path}' already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _stream = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        _dataBlock = new KvSstBlockBuilder(_options.RestartInterval);
    }

    /// <summary>已追加的条目数。</summary>
    internal long EntryCount => _entryCount;

    /// <summary>已写出的数据字节数加上当前未落盘块的估计大小。</summary>
    internal long EstimatedFileSize => _offset + _dataBlock.EstimatedSize;

    /// <summary>
    /// 追加一个条目；internal key 必须按 <see cref="KvInternalKey.Compare"/> 严格递增。
    /// </summary>
    /// <exception cref="ArgumentException">key 无效、乱序、墓碑带值或到期时间为负。</exception>
    internal void Add(ReadOnlySpan<byte> internalKey, ReadOnlySpan<byte> value, long expiresAtUtcTicks = 0)
    {
        ThrowIfUnusable();
        try
        {
            KvInternalKey.Validate(internalKey);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(exception.Message, nameof(internalKey), exception);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expiresAtUtcTicks);
        if (_entryCount > 0 && KvInternalKey.Compare(_lastKey.AsSpan(0, _lastKeyLength), internalKey) >= 0)
            throw new ArgumentException("KV-LSM SST keys must be strictly increasing.", nameof(internalKey));

        KvEntryKind kind = KvInternalKey.Kind(internalKey);
        ReadOnlySpan<byte> encodedValue;
        if (kind == KvEntryKind.Delete)
        {
            if (!value.IsEmpty || expiresAtUtcTicks != 0)
                throw new ArgumentException("KV-LSM delete tombstones cannot carry a value or TTL.", nameof(value));
            encodedValue = [];
            _deletionCount++;
        }
        else
        {
            int needed = KvLsmVarint.Measure((ulong)expiresAtUtcTicks) + value.Length;
            if (_valueBuffer.Length < needed)
                _valueBuffer = new byte[Math.Max(needed, _valueBuffer.Length * 2)];
            int written = KvLsmVarint.Write(_valueBuffer, (ulong)expiresAtUtcTicks);
            value.CopyTo(_valueBuffer.AsSpan(written));
            encodedValue = _valueBuffer.AsSpan(0, written + value.Length);
            if (expiresAtUtcTicks != 0 && (_minExpiresAtUtcTicks == 0 || expiresAtUtcTicks < _minExpiresAtUtcTicks))
                _minExpiresAtUtcTicks = expiresAtUtcTicks;
        }

        // 大条目（单条 ≥ 块大小）独占数据块：小 key 所在的块不会夹带大 value，
        // 只读小条目（索引、元数据）的扫描不必读入相邻的大行，块缓存也不会被大块挤占。
        if (!_dataBlock.IsEmpty
            && (_dataBlock.EstimatedSize >= _options.BlockSize || internalKey.Length + encodedValue.Length >= _options.BlockSize))
        {
            FlushDataBlock();
        }

        ReadOnlySpan<byte> userKey = KvInternalKey.UserKey(internalKey);
        if (_entryCount == 0 || !KvInternalKey.UserKey(_lastKey.AsSpan(0, _lastKeyLength)).SequenceEqual(userKey))
            _bloomHashes.Add(KvBloomFilter.Hash(userKey));

        _dataBlock.Add(internalKey, encodedValue);

        long sequence = KvInternalKey.Sequence(internalKey);
        _minSequence = Math.Min(_minSequence, sequence);
        _maxSequence = Math.Max(_maxSequence, sequence);
        _rawKeyBytes += internalKey.Length;
        _rawValueBytes += value.Length;
        _smallestKey ??= internalKey.ToArray();
        if (_lastKey.Length < internalKey.Length)
            _lastKey = new byte[Math.Max(internalKey.Length, _lastKey.Length * 2)];
        internalKey.CopyTo(_lastKey);
        _lastKeyLength = internalKey.Length;
        _entryCount++;
    }

    /// <summary>加入一个 range tombstone（任意顺序；写出时排序）。</summary>
    /// <exception cref="ArgumentException">范围或 sequence 无效。</exception>
    internal void AddRangeTombstone(KvRangeTombstone tombstone)
    {
        ThrowIfUnusable();
        new KvRangeDeletion(tombstone.Start, tombstone.End).Validate();
        if (tombstone.Sequence <= 0 || tombstone.Sequence > KvInternalKey.MaxSequence)
            throw new ArgumentOutOfRangeException(nameof(tombstone), "KV-LSM range tombstone sequence is out of range.");
        _rangeTombstones.Add(tombstone);
    }

    /// <summary>写出过滤器、索引、属性与 footer，fsync 并发布最终文件。</summary>
    /// <returns>写入的属性。</returns>
    /// <exception cref="InvalidOperationException">没有任何条目。</exception>
    internal KvSstProperties Finish()
    {
        ThrowIfUnusable();
        if (_entryCount == 0 && _rangeTombstones.Count == 0)
            throw new InvalidOperationException("KV-LSM SST must contain at least one entry.");

        FlushDataBlock();
        long filterOffset = _offset;
        byte[] filter = KvBloomFilter.Build(_bloomHashes, _options.BloomBitsPerKey);
        KvSstBlockHandle filterHandle = WriteBlock(filter);
        KvSstBlockHandle indexHandle = WriteBlock(_indexBlock.Finish());

        byte[]? smallest = _smallestKey;
        byte[]? largest = _entryCount > 0 ? _lastKey.AsSpan(0, _lastKeyLength).ToArray() : null;
        long minSequence = _minSequence;
        long maxSequence = _maxSequence;
        foreach (KvRangeTombstone tombstone in _rangeTombstones)
        {
            byte[] start = KvInternalKey.EncodeRangeStart(tombstone.Start);
            byte[] end = KvInternalKey.EncodeRangeEnd(tombstone.End);
            if (smallest is null || KvInternalKey.Compare(start, smallest) < 0)
                smallest = start;
            if (largest is null || KvInternalKey.Compare(end, largest) > 0)
                largest = end;
            minSequence = Math.Min(minSequence, tombstone.Sequence);
            maxSequence = Math.Max(maxSequence, tombstone.Sequence);
        }

        var properties = new KvSstProperties(
            EntryCount: _entryCount,
            DeletionCount: _deletionCount,
            DataBlockCount: _dataBlockCount,
            MinSequence: minSequence,
            MaxSequence: maxSequence,
            MinExpiresAtUtcTicks: _minExpiresAtUtcTicks,
            Generation: _options.Generation,
            CreatedUtcTicks: DateTime.UtcNow.Ticks,
            RawKeyBytes: _rawKeyBytes,
            RawValueBytes: _rawValueBytes,
            SmallestKey: smallest!,
            LargestKey: largest!,
            RangeDeletionCount: _rangeTombstones.Count);
        KvSstBlockHandle propertiesHandle = WriteBlock(properties.Encode());
        KvSstBlockHandle rangeHandle = _rangeTombstones.Count > 0
            ? WriteBlock(EncodeRangeTombstones(_rangeTombstones))
            : default;

        KvSstFooter footer = default;
        KvSstFormat.Magic.CopyTo(footer.Magic.AsSpan());
        footer.FormatVersion = _rangeTombstones.Count > 0 ? KvSstFormat.RangeDeletionVersion : KvSstFormat.CurrentVersion;
        footer.RangeDelOffset = rangeHandle.Offset;
        footer.RangeDelLength = rangeHandle.Length;
        footer.FilterOffset = filterOffset;
        footer.FilterLength = filterHandle.Length;
        footer.IndexOffset = indexHandle.Offset;
        footer.IndexLength = indexHandle.Length;
        footer.PropertiesOffset = propertiesHandle.Offset;
        footer.PropertiesLength = propertiesHandle.Length;
        Span<byte> footerBytes = stackalloc byte[KvSstFooter.Size];
        footer.WriteTo(footerBytes);
        _stream!.Write(footerBytes);
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        _stream = null;

        File.Move(_tempPath, _path, overwrite: false);
        if (_options.SyncDirectory)
            DirectoryFsync.FlushRequired(Path.GetDirectoryName(_path)!);
        _finished = true;
        return properties;
    }

    /// <summary>未完成时关闭并删除临时文件。</summary>
    public void Dispose()
    {
        if (_finished)
            return;
        _stream?.Dispose();
        _stream = null;
        try
        {
            File.Delete(_tempPath);
        }
        catch (IOException)
        {
            // 临时文件由后续孤儿清理（K2 manifest）回收；此处不能掩盖调用方原始异常。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>编码 range tombstone 块：<c>varint count | (varint len | start | varint len | end | varint seq)*</c>，按起点升序、sequence 降序。</summary>
    internal static byte[] EncodeRangeTombstones(IReadOnlyList<KvRangeTombstone> tombstones)
    {
        var ordered = tombstones
            .OrderBy(static t => t.Start, Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ThenByDescending(static t => t.Sequence)
            .ToList();
        int size = KvLsmVarint.MaxLength;
        foreach (KvRangeTombstone tombstone in ordered)
            size += (KvLsmVarint.MaxLength * 3) + tombstone.Start.Length + tombstone.End.Length;
        byte[] buffer = new byte[size];
        int position = KvLsmVarint.Write(buffer, (ulong)ordered.Count);
        foreach (KvRangeTombstone tombstone in ordered)
        {
            position += KvLsmVarint.Write(buffer.AsSpan(position), (ulong)tombstone.Start.Length);
            tombstone.Start.CopyTo(buffer, position);
            position += tombstone.Start.Length;
            position += KvLsmVarint.Write(buffer.AsSpan(position), (ulong)tombstone.End.Length);
            tombstone.End.CopyTo(buffer, position);
            position += tombstone.End.Length;
            position += KvLsmVarint.Write(buffer.AsSpan(position), (ulong)tombstone.Sequence);
        }
        return buffer.AsSpan(0, position).ToArray();
    }

    /// <summary>解码 range tombstone 块并校验每个范围与 sequence。</summary>
    /// <exception cref="InvalidDataException">编码截断、范围无效或存在多余字节。</exception>
    internal static KvRangeTombstone[] DecodeRangeTombstones(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        int count = KvLsmVarint.ReadInt32(payload, ref position);
        if (count <= 0 || count > payload.Length)
            throw new InvalidDataException("KV-LSM range tombstone count is invalid.");
        var result = new KvRangeTombstone[count];
        for (int i = 0; i < count; i++)
        {
            byte[] start = ReadBytes(payload, ref position);
            byte[] end = ReadBytes(payload, ref position);
            long sequence = KvLsmVarint.ReadInt64(payload, ref position);
            if (end.Length == 0
                || start.AsSpan().SequenceCompareTo(end) >= 0
                || sequence <= 0
                || sequence > KvInternalKey.MaxSequence)
            {
                throw new InvalidDataException("KV-LSM range tombstone is invalid.");
            }
            result[i] = new KvRangeTombstone(start, end, sequence);
        }
        if (position != payload.Length)
            throw new InvalidDataException("KV-LSM range tombstone block has trailing bytes.");
        return result;

        static byte[] ReadBytes(ReadOnlySpan<byte> source, ref int offset)
        {
            int length = KvLsmVarint.ReadInt32(source, ref offset);
            if (length > source.Length - offset)
                throw new InvalidDataException("KV-LSM range tombstone key is truncated.");
            byte[] bytes = source.Slice(offset, length).ToArray();
            offset += length;
            return bytes;
        }
    }

    private void FlushDataBlock()
    {
        if (_dataBlock.IsEmpty)
            return;
        KvSstBlockHandle handle = WriteBlock(_dataBlock.Finish());
        _dataBlock.Reset();
        _dataBlockCount++;

        Span<byte> encodedHandle = stackalloc byte[KvSstBlockHandle.MaxEncodedLength];
        int length = handle.Encode(encodedHandle);
        _indexBlock.Add(_lastKey.AsSpan(0, _lastKeyLength), encodedHandle[..length]);
    }

    private KvSstBlockHandle WriteBlock(ReadOnlySpan<byte> payload)
    {
        long offset = _offset;
        _stream!.Write(payload);
        Span<byte> trailer = stackalloc byte[KvSstFormat.BlockTrailerSize];
        trailer[0] = KvSstFormat.CompressionNone;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            trailer[1..],
            KvSstFormat.ComputeBlockCrc(payload, KvSstFormat.CompressionNone));
        _stream.Write(trailer);
        _offset = checked(_offset + payload.Length + KvSstFormat.BlockTrailerSize);
        return new KvSstBlockHandle(offset, payload.Length);
    }

    private void ThrowIfUnusable()
    {
        if (_finished)
            throw new InvalidOperationException("KV-LSM SST writer has already finished.");
        ObjectDisposedException.ThrowIf(_stream is null, this);
    }
}
