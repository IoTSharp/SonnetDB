using System.Buffers.Binary;

namespace SonnetDB.Kv.Lsm;

/// <summary>
/// SST 块构建器：前缀压缩条目 + restart 偏移数组。
/// </summary>
/// <remarks>
/// 布局：<c>entry* | restart(uint32 LE)* | restartCount(uint32 LE)</c>；
/// <c>entry = varint shared | varint nonShared | varint valueLength | keyDelta | value</c>。
/// 每个 restart 点的条目 <c>shared = 0</c>，使读取方可以在 restart 点二分查找。
/// 调用方负责保证 key 按 <see cref="KvInternalKey.Compare"/> 严格递增。
/// </remarks>
internal sealed class KvSstBlockBuilder
{
    private readonly int _restartInterval;
    private readonly List<int> _restarts = [0];
    private byte[] _buffer = new byte[4096];
    private int _length;
    private byte[] _lastKey = [];
    private int _counter;
    private int _entryCount;

    /// <summary>创建块构建器。</summary>
    /// <param name="restartInterval">restart 间隔，必须为正数。</param>
    internal KvSstBlockBuilder(int restartInterval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(restartInterval);
        _restartInterval = restartInterval;
    }

    /// <summary>当前块中的条目数。</summary>
    internal int EntryCount => _entryCount;

    /// <summary>块是否没有任何条目。</summary>
    internal bool IsEmpty => _entryCount == 0;

    /// <summary>若此刻调用 <see cref="Finish"/>，块载荷的字节数。</summary>
    internal int EstimatedSize => _length + (_restarts.Count * 4) + 4;

    /// <summary>追加一个条目。</summary>
    internal void Add(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        int shared = 0;
        if (_counter < _restartInterval)
        {
            int limit = Math.Min(_lastKey.Length, key.Length);
            while (shared < limit && _lastKey[shared] == key[shared])
                shared++;
        }
        else
        {
            _restarts.Add(_length);
            _counter = 0;
        }

        int nonShared = key.Length - shared;
        int needed = KvLsmVarint.Measure((ulong)shared)
            + KvLsmVarint.Measure((ulong)nonShared)
            + KvLsmVarint.Measure((ulong)value.Length)
            + nonShared
            + value.Length;
        EnsureCapacity(needed);
        Span<byte> destination = _buffer.AsSpan(_length);
        int written = KvLsmVarint.Write(destination, (ulong)shared);
        written += KvLsmVarint.Write(destination[written..], (ulong)nonShared);
        written += KvLsmVarint.Write(destination[written..], (ulong)value.Length);
        key[shared..].CopyTo(destination[written..]);
        written += nonShared;
        value.CopyTo(destination[written..]);
        written += value.Length;
        _length += written;

        _lastKey = key.ToArray();
        _counter++;
        _entryCount++;
    }

    /// <summary>写出 restart 数组并返回完整块载荷；构建器随后需 <see cref="Reset"/> 才能复用。</summary>
    internal ReadOnlySpan<byte> Finish()
    {
        EnsureCapacity((_restarts.Count * 4) + 4);
        foreach (int restart in _restarts)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length, 4), (uint)restart);
            _length += 4;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length, 4), (uint)_restarts.Count);
        _length += 4;
        return _buffer.AsSpan(0, _length);
    }

    /// <summary>清空构建器以开始下一个块。</summary>
    internal void Reset()
    {
        _restarts.Clear();
        _restarts.Add(0);
        _length = 0;
        _lastKey = [];
        _counter = 0;
        _entryCount = 0;
    }

    private void EnsureCapacity(int additional)
    {
        int required = checked(_length + additional);
        if (required <= _buffer.Length)
            return;
        int newSize = Math.Max(required, _buffer.Length * 2);
        Array.Resize(ref _buffer, newSize);
    }
}

/// <summary>
/// 已校验 CRC 的 SST 块（不可变），可创建多个独立迭代器。
/// </summary>
internal sealed class KvSstBlock
{
    private readonly byte[] _data;
    private readonly int _length;
    private readonly int _restartsOffset;
    private readonly int _restartCount;

    /// <summary>解析块载荷并校验 restart 数组结构。</summary>
    /// <exception cref="InvalidDataException">restart 数组越界、非递增或首个 restart 不为 0。</exception>
    internal KvSstBlock(byte[] data)
        : this(data, data?.Length ?? 0)
    {
    }

    /// <summary>
    /// 以 <paramref name="data"/> 的前 <paramref name="length"/> 字节作为块载荷（其后可附带 trailer），避免为读取结果再复制一次。
    /// </summary>
    /// <exception cref="InvalidDataException">restart 数组越界、非递增或首个 restart 不为 0。</exception>
    internal KvSstBlock(byte[] data, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, data.Length);
        if (length < 8)
            throw new InvalidDataException("KV-LSM block is shorter than its restart array.");
        uint restartCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(length - 4, 4));
        long restartsOffset = length - 4 - ((long)restartCount * 4);
        if (restartCount == 0 || restartsOffset < 0)
            throw new InvalidDataException("KV-LSM block restart count is invalid.");

        _data = data;
        _length = length;
        _restartCount = (int)restartCount;
        _restartsOffset = (int)restartsOffset;
        if (_restartsOffset == 0)
        {
            // 空块（例如只含 range tombstone 的 SST 的索引块）：唯一 restart 必须为 0。
            if (_restartCount != 1 || RestartPoint(0) != 0)
                throw new InvalidDataException("KV-LSM empty block restart array is invalid.");
            return;
        }
        int previous = -1;
        for (int i = 0; i < _restartCount; i++)
        {
            int restart = RestartPoint(i);
            if ((i == 0 && restart != 0) || restart <= previous || restart >= _restartsOffset)
                throw new InvalidDataException("KV-LSM block restart offsets are invalid.");
            previous = restart;
        }
    }

    /// <summary>块载荷字节数（不含 trailer），用于块缓存计费。</summary>
    internal int Size => _length;

    /// <summary>创建位于首条目之前的迭代器。</summary>
    internal KvSstBlockIterator CreateIterator() => new(_data, _restartsOffset, _restartCount);

    private int RestartPoint(int index)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_restartsOffset + (index * 4), 4));
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }
}

/// <summary>
/// SST 块双向迭代器；key 缓冲区在迭代过程中复用，调用方需要保留时自行复制。
/// 反向移动从当前条目之前最近的 restart 点重新正向解码，代价为 O(restart 间隔)。
/// </summary>
internal sealed class KvSstBlockIterator
{
    private readonly byte[] _data;
    private readonly int _restartsOffset;
    private readonly int _restartCount;
    private byte[] _key = new byte[64];
    private int _keyLength;
    private int _nextOffset;
    private int _currentOffset;
    private int _valueOffset;
    private int _valueLength;
    private bool _valid;

    internal KvSstBlockIterator(byte[] data, int restartsOffset, int restartCount)
    {
        _data = data;
        _restartsOffset = restartsOffset;
        _restartCount = restartCount;
        _nextOffset = _restartsOffset;
    }

    /// <summary>迭代器当前是否指向一个条目。</summary>
    internal bool Valid => _valid;

    /// <summary>当前条目的 key。</summary>
    internal ReadOnlySpan<byte> Key => _valid ? _key.AsSpan(0, _keyLength) : throw InvalidPosition();

    /// <summary>当前条目的 value。</summary>
    internal ReadOnlySpan<byte> Value => _valid ? _data.AsSpan(_valueOffset, _valueLength) : throw InvalidPosition();

    /// <summary>当前条目的 value 内存视图（与块共享底层数组）。</summary>
    internal ReadOnlyMemory<byte> ValueMemory => _valid ? _data.AsMemory(_valueOffset, _valueLength) : throw InvalidPosition();

    /// <summary>定位到首条目。</summary>
    internal void SeekToFirst()
    {
        _keyLength = 0;
        _nextOffset = 0;
        ParseNext(atRestart: true);
    }

    /// <summary>定位到首个 key ≥ <paramref name="target"/> 的条目；不存在时 <see cref="Valid"/> 为 false。</summary>
    internal void Seek(ReadOnlySpan<byte> target)
    {
        int left = 0;
        int right = _restartCount - 1;
        while (left < right)
        {
            int middle = left + ((right - left + 1) / 2);
            _keyLength = 0;
            _nextOffset = RestartPoint(middle);
            ParseNext(atRestart: true);
            if (KvInternalKey.Compare(Key, target) < 0)
                left = middle;
            else
                right = middle - 1;
        }

        _keyLength = 0;
        _nextOffset = RestartPoint(left);
        bool atRestart = true;
        while (true)
        {
            ParseNext(atRestart);
            atRestart = false;
            if (!_valid || KvInternalKey.Compare(Key, target) >= 0)
                return;
        }
    }

    /// <summary>前进到下一条目。</summary>
    internal void MoveNext()
    {
        if (!_valid)
            throw InvalidPosition();
        ParseNext(atRestart: false);
    }

    /// <summary>定位到最后一个条目。</summary>
    internal void SeekToLast()
    {
        _keyLength = 0;
        _nextOffset = RestartPoint(_restartCount - 1);
        ParseNext(atRestart: true);
        while (_valid && _nextOffset < _restartsOffset)
            ParseNext(atRestart: false);
    }

    /// <summary>后退到上一条目；已在首条目时 <see cref="Valid"/> 变为 false。</summary>
    internal void MovePrev()
    {
        if (!_valid)
            throw InvalidPosition();
        int original = _currentOffset;
        if (original == 0)
        {
            _valid = false;
            return;
        }

        int restartIndex = _restartCount - 1;
        while (RestartPoint(restartIndex) >= original)
            restartIndex--;

        _keyLength = 0;
        _nextOffset = RestartPoint(restartIndex);
        ParseNext(atRestart: true);
        while (_nextOffset < original)
            ParseNext(atRestart: false);
        if (!_valid || _nextOffset != original)
            throw new InvalidDataException("KV-LSM block entries do not chain to the previous position.");
    }

    private void ParseNext(bool atRestart)
    {
        if (_nextOffset >= _restartsOffset)
        {
            if (_nextOffset != _restartsOffset)
                throw new InvalidDataException("KV-LSM block entry overruns the restart array.");
            _valid = false;
            return;
        }

        ReadOnlySpan<byte> entries = _data.AsSpan(0, _restartsOffset);
        int position = _nextOffset;
        _currentOffset = position;
        int shared = KvLsmVarint.ReadInt32(entries, ref position);
        int nonShared = KvLsmVarint.ReadInt32(entries, ref position);
        int valueLength = KvLsmVarint.ReadInt32(entries, ref position);
        if (atRestart && shared != 0)
            throw new InvalidDataException("KV-LSM block restart entry has a shared key prefix.");
        if (shared > _keyLength)
            throw new InvalidDataException("KV-LSM block entry shares more bytes than the previous key.");
        if ((long)position + nonShared + valueLength > entries.Length)
            throw new InvalidDataException("KV-LSM block entry is truncated.");

        int keyLength = shared + nonShared;
        if (keyLength > _key.Length)
            Array.Resize(ref _key, Math.Max(keyLength, _key.Length * 2));
        entries.Slice(position, nonShared).CopyTo(_key.AsSpan(shared));
        _keyLength = keyLength;
        position += nonShared;
        _valueOffset = position;
        _valueLength = valueLength;
        _nextOffset = position + valueLength;
        _valid = true;
    }

    private int RestartPoint(int index)
        => (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_restartsOffset + (index * 4), 4));

    private static InvalidOperationException InvalidPosition()
        => new("KV-LSM block iterator is not positioned on an entry.");
}
