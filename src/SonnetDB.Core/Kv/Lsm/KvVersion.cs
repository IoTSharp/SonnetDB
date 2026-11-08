namespace SonnetDB.Kv.Lsm;

/// <summary>
/// 不可变的 SST 文件集合快照，带引用计数。
/// </summary>
/// <remarks>
/// L0 按文件号降序（新文件在前，范围可重叠）；L1 及以上按最小 key 升序且互不重叠。
/// 引用计数归零后，只被它引用的文件才可能成为待删除文件，由 <see cref="KvVersionSet"/> 统一回收。
/// </remarks>
internal sealed class KvVersion
{
    /// <summary>层数（L0～L6）。</summary>
    internal const int LevelCount = 7;

    private readonly KvVersionSet? _owner;
    private readonly KvLsmFileMetadata[][] _levels;
    private int _references;

    internal KvVersion(KvVersionSet? owner, KvLsmFileMetadata[][] levels)
    {
        _owner = owner;
        _levels = levels;
    }

    /// <summary>空版本。</summary>
    internal static KvVersion Empty(KvVersionSet? owner)
        => new(owner, Enumerable.Range(0, LevelCount).Select(static _ => Array.Empty<KvLsmFileMetadata>()).ToArray());

    /// <summary>返回某一层的文件（按该层排序规则）。</summary>
    internal IReadOnlyList<KvLsmFileMetadata> Files(int level) => _levels[level];

    /// <summary>所有层的全部文件。</summary>
    internal IEnumerable<KvLsmFileMetadata> AllFiles => _levels.SelectMany(static files => files);

    /// <summary>文件总数。</summary>
    internal int FileCount => _levels.Sum(static files => files.Length);

    /// <summary>当前引用计数（诊断与测试用）。</summary>
    internal int References => Volatile.Read(ref _references);

    /// <summary>增加一个引用。</summary>
    internal KvVersion AddReference()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    /// <summary>释放一个引用；归零时通知所属版本集。</summary>
    /// <exception cref="InvalidOperationException">引用计数已经为零。</exception>
    internal void Release()
    {
        int remaining = Interlocked.Decrement(ref _references);
        if (remaining < 0)
            throw new InvalidOperationException("KV-LSM version was released more times than it was acquired.");
        if (remaining == 0)
            _owner?.OnVersionReleased(this);
    }

    /// <summary>在当前版本上应用编辑，生成新版本（不修改自身）。</summary>
    /// <exception cref="InvalidDataException">删除不存在的文件、文件号重复或 L1+ 范围重叠。</exception>
    internal KvVersion Apply(KvVersionEdit edit, KvVersionSet? owner)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var levels = _levels.Select(static files => files.ToList()).ToArray();
        var numbers = new HashSet<long>(AllFiles.Select(static f => f.FileNumber));

        foreach ((int level, long fileNumber) in edit.DeletedFiles)
        {
            if (level is < 0 or >= LevelCount)
                throw new InvalidDataException($"KV-LSM edit deletes file #{fileNumber} from invalid level {level}.");
            int index = levels[level].FindIndex(f => f.FileNumber == fileNumber);
            if (index < 0)
                throw new InvalidDataException($"KV-LSM edit deletes missing file #{fileNumber} at L{level}.");
            levels[level].RemoveAt(index);
            numbers.Remove(fileNumber);
        }

        foreach (KvLsmFileMetadata file in edit.AddedFiles)
        {
            file.Validate();
            if (!numbers.Add(file.FileNumber))
                throw new InvalidDataException($"KV-LSM edit adds duplicate file #{file.FileNumber}.");
            levels[file.Level].Add(file);
        }

        var result = new KvLsmFileMetadata[LevelCount][];
        result[0] = [.. levels[0].OrderByDescending(static f => f.FileNumber)];
        for (int level = 1; level < LevelCount; level++)
        {
            List<KvLsmFileMetadata> files = levels[level];
            files.Sort(static (left, right) => KvInternalKey.Compare(left.SmallestKey, right.SmallestKey));
            for (int i = 1; i < files.Count; i++)
            {
                if (KvInternalKey.Compare(files[i - 1].LargestKey, files[i].SmallestKey) >= 0)
                {
                    throw new InvalidDataException(
                        $"KV-LSM L{level} files #{files[i - 1].FileNumber} and #{files[i].FileNumber} overlap.");
                }
            }
            result[level] = [.. files];
        }
        return new KvVersion(owner, result);
    }

    /// <summary>把整个版本编码为一条完整快照编辑的文件部分。</summary>
    internal void AddAllFilesTo(KvVersionEdit edit)
    {
        foreach (KvLsmFileMetadata file in AllFiles)
            edit.AddedFiles.Add(file);
    }
}
