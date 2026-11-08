namespace SonnetDB.Kv.Lsm;

/// <summary>
/// 把一层内按 key 排序且互不重叠的 SST 拼接为一个双向迭代器，只在需要时打开当前文件的迭代器。
/// </summary>
/// <remarks>用于 L1 及以上各层，使合并迭代器的子迭代器数量与层数而不是文件数相关。</remarks>
internal sealed class KvLevelIterator : IKvInternalIterator
{
    private readonly IReadOnlyList<KvLsmFileMetadata> _files;
    private readonly Func<KvLsmFileMetadata, IKvInternalIterator> _open;
    private IKvInternalIterator? _current;
    private int _index = -1;

    /// <summary>创建层迭代器。</summary>
    /// <param name="files">该层文件，按最小 key 升序且互不重叠。</param>
    /// <param name="open">为单个文件创建迭代器的委托。</param>
    internal KvLevelIterator(IReadOnlyList<KvLsmFileMetadata> files, Func<KvLsmFileMetadata, IKvInternalIterator> open)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(open);
        _files = files;
        _open = open;
    }

    /// <inheritdoc />
    public bool Valid => _current is { Valid: true };

    /// <inheritdoc />
    public ReadOnlySpan<byte> Key => Valid ? _current!.Key : throw InvalidPosition();

    /// <inheritdoc />
    public ReadOnlySpan<byte> Value => Valid ? _current!.Value : throw InvalidPosition();

    /// <inheritdoc />
    public long ExpiresAtUtcTicks => Valid ? _current!.ExpiresAtUtcTicks : throw InvalidPosition();

    /// <inheritdoc />
    public void SeekToFirst()
    {
        OpenFile(0);
        _current?.SeekToFirst();
        SkipForward();
    }

    /// <inheritdoc />
    public void SeekToLast()
    {
        OpenFile(_files.Count - 1);
        _current?.SeekToLast();
        SkipBackward();
    }

    /// <inheritdoc />
    public void Seek(ReadOnlySpan<byte> target)
    {
        // 第一个最大 key ≥ target 的文件是唯一可能包含首个 ≥ target 条目的文件。
        int low = 0;
        int high = _files.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (KvInternalKey.Compare(_files[middle].LargestKey, target) < 0)
                low = middle + 1;
            else
                high = middle;
        }
        OpenFile(low);
        _current?.Seek(target);
        SkipForward();
    }

    /// <inheritdoc />
    public void SeekForPrev(ReadOnlySpan<byte> target)
    {
        // 最后一个最小 key ≤ target 的文件包含最后一个 ≤ target 的条目。
        int low = 0;
        int high = _files.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (KvInternalKey.Compare(_files[middle].SmallestKey, target) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        OpenFile(low - 1);
        _current?.SeekForPrev(target);
        SkipBackward();
    }

    /// <inheritdoc />
    public void MoveNext()
    {
        if (!Valid)
            throw InvalidPosition();
        _current!.MoveNext();
        SkipForward();
    }

    /// <inheritdoc />
    public void MovePrev()
    {
        if (!Valid)
            throw InvalidPosition();
        _current!.MovePrev();
        SkipBackward();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _current?.Dispose();
        _current = null;
        _index = -1;
    }

    private void SkipForward()
    {
        while (_current is { Valid: false } && _index + 1 < _files.Count)
        {
            OpenFile(_index + 1);
            _current!.SeekToFirst();
        }
    }

    private void SkipBackward()
    {
        while (_current is { Valid: false } && _index > 0)
        {
            OpenFile(_index - 1);
            _current!.SeekToLast();
        }
    }

    private void OpenFile(int index)
    {
        if (index < 0 || index >= _files.Count)
        {
            _current?.Dispose();
            _current = null;
            _index = -1;
            return;
        }
        if (index == _index && _current is not null)
            return;
        _current?.Dispose();
        _current = _open(_files[index]);
        _index = index;
    }

    private static InvalidOperationException InvalidPosition()
        => new("KV-LSM level iterator is not positioned on an entry.");
}
