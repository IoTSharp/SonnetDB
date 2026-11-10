using System.Text;
using SonnetDB.Wal;

namespace SonnetDB.Kv.Lsm;

/// <summary>manifest 持久化流程中的故障注入点。</summary>
internal enum KvManifestPhase
{
    /// <summary>追加记录之前。</summary>
    BeforeRecordWrite,

    /// <summary>记录已写入 OS 缓冲、尚未 fsync。</summary>
    AfterRecordWriteBeforeSync,

    /// <summary>记录已 fsync（提交点之后、安装新版本之前）。</summary>
    AfterRecordSync,

    /// <summary>新 manifest 已写完并 fsync，CURRENT 尚未切换。</summary>
    NewManifestWritten,

    /// <summary>CURRENT.tmp 已 fsync，尚未改名。</summary>
    CurrentTempWritten,

    /// <summary>CURRENT 已切换并对目录 fsync，旧 manifest 尚未删除。</summary>
    AfterCurrentSwitch,
}

/// <summary>版本集选项。</summary>
internal sealed record KvVersionSetOptions
{
    /// <summary>默认选项。</summary>
    internal static KvVersionSetOptions Default { get; } = new();

    /// <summary>manifest 超过该字节数时滚动为新的快照 manifest。</summary>
    internal long MaxManifestBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>测试用故障注入钩子；抛出异常即模拟在该点崩溃。</summary>
    internal Action<KvManifestPhase>? TestHook { get; init; }
}

/// <summary>一次过期文件清理的结果。</summary>
/// <param name="DeletedSstFiles">删除的 SST 文件号。</param>
/// <param name="DeletedManifests">删除的旧 manifest 文件名。</param>
/// <param name="DeletedTempFiles">删除的临时文件名。</param>
/// <param name="SkippedFiles">因 I/O 或权限失败而留待下一轮的文件数。</param>
internal sealed record KvObsoleteCleanupResult(
    IReadOnlyList<long> DeletedSstFiles,
    IReadOnlyList<string> DeletedManifests,
    IReadOnlyList<string> DeletedTempFiles,
    int SkippedFiles);

/// <summary>打开时的恢复信息。</summary>
/// <param name="ReplayedEdits">回放的编辑条数。</param>
/// <param name="IgnoredTailBytes">manifest 末尾被忽略的撕裂字节数。</param>
/// <param name="Cleanup">打开后执行的过期文件清理。</param>
internal sealed record KvVersionSetRecovery(int ReplayedEdits, long IgnoredTailBytes, KvObsoleteCleanupResult Cleanup);

/// <summary>
/// KV-LSM 版本集：持有当前 <see cref="KvVersion"/>、实例级计数器与 manifest 日志，负责原子提交
/// <see cref="KvVersionEdit"/> 与回收不再被任何版本或进行中输出引用的文件。
/// </summary>
/// <remarks>
/// <para>提交点：普通编辑为 manifest 记录 fsync 完成；滚动时为 CURRENT 改名并对目录 fsync 完成。</para>
/// <para>
/// 写入 manifest 或切换 CURRENT 的 I/O 失败后提交结果未知，版本集进入 faulted 状态并拒绝后续编辑，
/// 调用方必须重新打开（与 KV WAL 的 fail closed 合同一致）。校验失败（尚未发生 I/O）不会进入 faulted。
/// </para>
/// <para>每次打开都会把当前状态重写为新的快照 manifest，从而截掉撕裂尾部并限制 manifest 体积。</para>
/// </remarks>
internal sealed class KvVersionSet : IDisposable
{
    private readonly object _sync = new();
    private readonly string _root;
    private readonly KvVersionSetOptions _options;
    private readonly HashSet<KvVersion> _liveVersions = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<long> _pendingOutputs = [];
    private KvVersion _current;
    private FileStream? _manifest;
    private long _manifestNumber;
    private long _generation;
    private long _logNumber;
    private long _lastSequence;
    private long _nextFileNumber;
    private Exception? _fault;
    private bool _disposed;

    private KvVersionSet(string root, KvVersionSetOptions options)
    {
        _root = root;
        _options = options;
        // 占位版本同样由版本集持有一个引用，使 Dispose 在任何阶段都只释放自己持有的那一个。
        _current = KvVersion.Empty(this).AddReference();
    }

    /// <summary>实例根目录。</summary>
    internal string RootDirectory => _root;

    /// <summary>当前 generation。</summary>
    internal long Generation { get { lock (_sync) return _generation; } }

    /// <summary>当前 WAL 下界。</summary>
    internal long LogNumber { get { lock (_sync) return _logNumber; } }

    /// <summary>当前最后 sequence。</summary>
    internal long LastSequence { get { lock (_sync) return _lastSequence; } }

    /// <summary>下一个将分配的文件号。</summary>
    internal long NextFileNumber { get { lock (_sync) return _nextFileNumber; } }

    /// <summary>当前 manifest 文件号。</summary>
    internal long ManifestNumber { get { lock (_sync) return _manifestNumber; } }

    /// <summary>打开时的恢复信息；新建实例为 null。</summary>
    internal KvVersionSetRecovery? Recovery { get; private set; }

    /// <summary>是否因提交结果未知而拒绝后续编辑。</summary>
    internal bool IsFaulted { get { lock (_sync) return _fault is not null; } }

    /// <summary>在空目录创建新实例。</summary>
    /// <exception cref="IOException">目录中已存在 CURRENT。</exception>
    internal static KvVersionSet Create(string root, long generation = 0, KvVersionSetOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        string fullRoot = Path.GetFullPath(root);
        if (File.Exists(KvLsmPaths.CurrentPath(fullRoot)))
            throw new IOException($"KV-LSM instance at '{fullRoot}' already exists.");
        Directory.CreateDirectory(KvLsmPaths.SstDirectory(fullRoot));
        DirectoryFsync.FlushRequired(fullRoot);

        var set = new KvVersionSet(fullRoot, options ?? KvVersionSetOptions.Default)
        {
            _generation = generation,
            _nextFileNumber = 1,
        };
        set.InstallInitial(KvVersion.Empty(set));
        try
        {
            set.WriteNewManifestLocked(set._current);
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    /// <summary>打开已有实例：读取 CURRENT 与 manifest，核对 SST 文件，重写快照 manifest 并清理孤儿文件。</summary>
    /// <exception cref="InvalidDataException">CURRENT/manifest 损坏、计数器回退或引用的 SST 缺失/大小不符。</exception>
    /// <exception cref="FileNotFoundException">CURRENT 不存在。</exception>
    internal static KvVersionSet Open(string root, KvVersionSetOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        string fullRoot = Path.GetFullPath(root);
        long manifestNumber = ReadCurrent(fullRoot);
        string manifestPath = KvLsmPaths.ManifestPath(fullRoot, manifestNumber);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"KV-LSM CURRENT points to missing manifest '{manifestPath}'.");

        KvManifestReadResult read = KvManifestFile.Read(manifestPath);
        if (read.Edits.Count == 0 || !read.Edits[0].IsComplete)
            throw new InvalidDataException($"KV-LSM manifest '{manifestPath}' does not start with a complete snapshot.");

        var set = new KvVersionSet(fullRoot, options ?? KvVersionSetOptions.Default);
        try
        {
            KvVersion version = KvVersion.Empty(set);
            bool first = true;
            foreach (KvVersionEdit edit in read.Edits)
            {
                set.ApplyCountersLocked(edit, initial: first);
                first = false;
                try
                {
                    version = version.Apply(edit, set);
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException($"KV-LSM manifest '{manifestPath}' replay failed: {exception.Message}", exception);
                }
            }

            long maxUsed = Math.Max(manifestNumber, version.AllFiles.Select(static f => f.FileNumber).DefaultIfEmpty(0).Max());
            if (set._nextFileNumber <= maxUsed)
                throw new InvalidDataException($"KV-LSM manifest '{manifestPath}' NextFileNumber is not above used file numbers.");

            foreach (KvLsmFileMetadata file in version.AllFiles)
            {
                string path = KvLsmPaths.SstPath(fullRoot, file.FileNumber);
                var info = new FileInfo(path);
                if (!info.Exists)
                    throw new InvalidDataException($"KV-LSM manifest references missing SST '{path}'.");
                if (info.Length != file.FileSize)
                    throw new InvalidDataException($"KV-LSM SST '{path}' size {info.Length} does not match manifest size {file.FileSize}.");
            }

            set._manifestNumber = manifestNumber;
            set.InstallInitial(version);
            lock (set._sync)
                set.WriteNewManifestLocked(version);
            KvObsoleteCleanupResult cleanup = set.DeleteObsoleteFiles();
            set.Recovery = new KvVersionSetRecovery(read.Edits.Count, read.IgnoredTailBytes, cleanup);
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    /// <summary>取得当前版本的一个引用；调用方用完必须 <see cref="KvVersion.Release"/>。</summary>
    internal KvVersion AcquireCurrent()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _current.AddReference();
        }
    }

    /// <summary>分配新文件号并登记为进行中输出，清理时不会删除。</summary>
    internal long NewFileNumber()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaultedLocked();
            long number = _nextFileNumber++;
            _pendingOutputs.Add(number);
            return number;
        }
    }

    /// <summary>撤销进行中输出登记（文件已提交或已放弃）。</summary>
    internal void ReleasePendingOutput(long fileNumber)
    {
        lock (_sync)
            _pendingOutputs.Remove(fileNumber);
    }

    /// <summary>
    /// 校验并原子提交一次编辑，成功后安装新版本。新增文件必须已由 <see cref="NewFileNumber"/> 分配。
    /// </summary>
    /// <exception cref="ArgumentException">编辑与当前状态不一致（未发生 I/O，状态不变）。</exception>
    /// <exception cref="InvalidOperationException">版本集已 faulted。</exception>
    internal void LogAndApply(KvVersionEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaultedLocked();

            KvVersion next;
            try
            {
                ValidateCountersLocked(edit);
                foreach (KvLsmFileMetadata file in edit.AddedFiles)
                {
                    if (file.FileNumber >= _nextFileNumber)
                        throw new InvalidDataException($"KV-LSM edit adds unallocated file #{file.FileNumber}.");
                }
                next = _current.Apply(edit, this);
            }
            catch (InvalidDataException exception)
            {
                throw new ArgumentException(exception.Message, nameof(edit), exception);
            }

            edit.NextFileNumber = _nextFileNumber;
            try
            {
                if (_manifest!.Length >= _options.MaxManifestBytes)
                {
                    ApplyCountersLocked(edit, initial: false);
                    WriteNewManifestLocked(next);
                }
                else
                {
                    _options.TestHook?.Invoke(KvManifestPhase.BeforeRecordWrite);
                    KvManifestFile.WriteRecord(_manifest, edit.Encode());
                    _options.TestHook?.Invoke(KvManifestPhase.AfterRecordWriteBeforeSync);
                    _manifest.Flush(flushToDisk: true);
                    _options.TestHook?.Invoke(KvManifestPhase.AfterRecordSync);
                    ApplyCountersLocked(edit, initial: false);
                }
            }
            catch (Exception exception)
            {
                _fault = exception;
                throw;
            }

            InstallLocked(next);
            foreach (KvLsmFileMetadata file in edit.AddedFiles)
                _pendingOutputs.Remove(file.FileNumber);
        }
    }

    /// <summary>
    /// 删除不被任何存活版本或进行中输出引用的 SST、旧 manifest 与临时文件。删除失败的文件留待下一轮。
    /// </summary>
    internal KvObsoleteCleanupResult DeleteObsoleteFiles()
    {
        // 全程持锁：避免与并发的 NewFileNumber / manifest 滚动交错而误删刚创建的文件。删除只是元数据操作。
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            HashSet<long> live = [.. _liveVersions.SelectMany(static v => v.AllFiles).Select(static f => f.FileNumber)];
            return DeleteObsoleteFilesLocked(live, _pendingOutputs, _manifestNumber);
        }
    }

    private KvObsoleteCleanupResult DeleteObsoleteFilesLocked(
        HashSet<long> live, HashSet<long> pending, long currentManifest)
    {

        var deletedSst = new List<long>();
        var deletedManifests = new List<string>();
        var deletedTemp = new List<string>();
        int skipped = 0;

        string sstDirectory = KvLsmPaths.SstDirectory(_root);
        if (Directory.Exists(sstDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(sstDirectory))
            {
                string name = Path.GetFileName(path);
                if (name.EndsWith(KvLsmPaths.TempSuffix, StringComparison.Ordinal))
                {
                    string baseName = name[..^KvLsmPaths.TempSuffix.Length];
                    if (KvLsmPaths.TryParseSstFileName(baseName, out long tempNumber) && pending.Contains(tempNumber))
                        continue;
                    if (TryDelete(path, ref skipped))
                        deletedTemp.Add(name);
                }
                else if (KvLsmPaths.TryParseSstFileName(name, out long number)
                    && !live.Contains(number)
                    && !pending.Contains(number)
                    && TryDelete(path, ref skipped))
                {
                    deletedSst.Add(number);
                }
            }
        }

        foreach (string path in Directory.EnumerateFiles(_root))
        {
            string name = Path.GetFileName(path);
            if (KvLsmPaths.TryParseManifestFileName(name, out long number))
            {
                if (number != currentManifest && TryDelete(path, ref skipped))
                    deletedManifests.Add(name);
            }
            else if (name.EndsWith(KvLsmPaths.TempSuffix, StringComparison.Ordinal)
                && (name.StartsWith(KvLsmPaths.CurrentFileName, StringComparison.Ordinal)
                    || name.StartsWith(KvLsmPaths.ManifestPrefix, StringComparison.Ordinal))
                && TryDelete(path, ref skipped))
            {
                deletedTemp.Add(name);
            }
        }

        if (deletedSst.Count > 0 || deletedTemp.Count > 0)
            DirectoryFsync.FlushBestEffort(sstDirectory);
        if (deletedManifests.Count > 0 || deletedTemp.Count > 0)
            DirectoryFsync.FlushBestEffort(_root);

        deletedSst.Sort();
        return new KvObsoleteCleanupResult(deletedSst, deletedManifests, deletedTemp, skipped);
    }

    /// <summary>关闭 manifest；不删除任何文件。</summary>
    public void Dispose()
    {
        KvVersion current;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _manifest?.Dispose();
            _manifest = null;
            current = _current;
        }

        // 释放版本集自身持有的当前版本引用：之后只有被快照固定的版本仍然存活，
        // 引擎据此关闭其余 SST 读取器，关闭后的目录可以立即移动或删除（Windows 打开句柄会阻止目录改名）。
        current.Release();
    }

    internal void OnVersionReleased(KvVersion version)
    {
        lock (_sync)
            _liveVersions.Remove(version);
    }

    /// <summary>当前仍被任何存活版本引用的 SST 文件号（关闭后仍可调用）。</summary>
    internal HashSet<long> LiveFileNumbers()
    {
        lock (_sync)
            return [.. _liveVersions.SelectMany(static v => v.AllFiles).Select(static f => f.FileNumber)];
    }

    private void InstallInitial(KvVersion version)
    {
        lock (_sync)
        {
            KvVersion placeholder = _current;
            _current = version.AddReference();
            _liveVersions.Add(version);
            placeholder.Release();
        }
    }

    private void InstallLocked(KvVersion next)
    {
        KvVersion previous = _current;
        _current = next.AddReference();
        _liveVersions.Add(next);
        previous.Release();
    }

    private void WriteNewManifestLocked(KvVersion version)
    {
        long number = _nextFileNumber++;
        var snapshot = new KvVersionEdit
        {
            Generation = _generation,
            LogNumber = _logNumber,
            LastSequence = _lastSequence,
            NextFileNumber = _nextFileNumber,
        };
        version.AddAllFilesTo(snapshot);

        string path = KvLsmPaths.ManifestPath(_root, number);
        FileStream stream = KvManifestFile.Create(path);
        bool switched = false;
        try
        {
            KvManifestFile.WriteRecord(stream, snapshot.Encode());
            stream.Flush(flushToDisk: true);
            DirectoryFsync.FlushRequired(_root);
            _options.TestHook?.Invoke(KvManifestPhase.NewManifestWritten);

            string currentPath = KvLsmPaths.CurrentPath(_root);
            string tempPath = currentPath + KvLsmPaths.TempSuffix;
            using (var current = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                current.Write(Encoding.ASCII.GetBytes(KvLsmPaths.ManifestFileName(number) + "\n"));
                current.Flush(flushToDisk: true);
            }
            _options.TestHook?.Invoke(KvManifestPhase.CurrentTempWritten);
            File.Move(tempPath, currentPath, overwrite: true);

            // 改名一旦返回，CURRENT 可能已指向新 manifest：此后任何失败都不能删除新 manifest。
            switched = true;
            long previousNumber = _manifestNumber;
            _manifest?.Dispose();
            _manifest = stream;
            _manifestNumber = number;

            DirectoryFsync.FlushRequired(_root);
            _options.TestHook?.Invoke(KvManifestPhase.AfterCurrentSwitch);
            if (previousNumber != 0 && previousNumber != number)
                TryDeleteQuietly(KvLsmPaths.ManifestPath(_root, previousNumber));
        }
        finally
        {
            if (!switched)
            {
                stream.Dispose();
                TryDeleteQuietly(path);
            }
        }
    }

    private static long ReadCurrent(string root)
    {
        string path = KvLsmPaths.CurrentPath(root);
        if (!File.Exists(path))
            throw new FileNotFoundException($"KV-LSM instance at '{root}' has no CURRENT file.", path);
        byte[] content = File.ReadAllBytes(path);
        if (content.Length != KvLsmPaths.ManifestPrefix.Length + 12 + 1 || content[^1] != (byte)'\n')
            throw new InvalidDataException($"KV-LSM CURRENT at '{path}' is malformed.");
        string name = Encoding.ASCII.GetString(content, 0, content.Length - 1);
        if (!KvLsmPaths.TryParseManifestFileName(name, out long number))
            throw new InvalidDataException($"KV-LSM CURRENT at '{path}' is malformed.");
        return number;
    }

    private void ValidateCountersLocked(KvVersionEdit edit)
    {
        if (edit.Generation is long generation && generation < _generation)
            throw new InvalidDataException("KV-LSM edit moves Generation backwards.");
        if (edit.LogNumber is long logNumber && logNumber < _logNumber)
            throw new InvalidDataException("KV-LSM edit moves LogNumber backwards.");
        if (edit.LastSequence is long lastSequence
            && (lastSequence < _lastSequence || lastSequence > KvInternalKey.MaxSequence))
        {
            throw new InvalidDataException("KV-LSM edit LastSequence is invalid.");
        }
        if (edit.NextFileNumber is long nextFileNumber && nextFileNumber < _nextFileNumber)
            throw new InvalidDataException("KV-LSM edit moves NextFileNumber backwards.");
        foreach (KvLsmFileMetadata file in edit.AddedFiles)
        {
            long sequenceCeiling = Math.Max(_lastSequence, edit.LastSequence ?? 0);
            if (file.MaxSequence > sequenceCeiling)
                throw new InvalidDataException($"KV-LSM file #{file.FileNumber} contains sequences above LastSequence.");
        }
    }

    private void ApplyCountersLocked(KvVersionEdit edit, bool initial)
    {
        if (!initial)
            ValidateCountersLocked(edit);
        if (edit.Generation is long generation)
            _generation = generation;
        if (edit.LogNumber is long logNumber)
            _logNumber = logNumber;
        if (edit.LastSequence is long lastSequence)
            _lastSequence = lastSequence;
        if (edit.NextFileNumber is long nextFileNumber)
            _nextFileNumber = nextFileNumber;
    }

    private void ThrowIfFaultedLocked()
    {
        if (_fault is not null)
        {
            throw new InvalidOperationException(
                "KV-LSM version set is faulted after a manifest write with unknown outcome; reopen the instance.",
                _fault);
        }
    }

    private static bool TryDelete(string path, ref int skipped)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            skipped++;
            return false;
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 由下一次 DeleteObsoleteFiles 回收。
        }
    }
}
