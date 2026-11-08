using System.Text;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

public sealed class KvVersionSetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-vset-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Create_ThenOpen_RoundTripsEmptyStateAndRejectsSecondCreate()
    {
        using (KvVersionSet created = KvVersionSet.Create(_root, generation: 3))
        {
            Assert.Equal(3, created.Generation);
            Assert.True(File.Exists(Path.Combine(_root, "CURRENT")));
            Assert.Throws<IOException>(() => KvVersionSet.Create(_root));
        }

        using KvVersionSet opened = KvVersionSet.Open(_root);
        Assert.Equal(3, opened.Generation);
        Assert.Equal(0, opened.LastSequence);
        Assert.Equal(1, opened.Recovery!.ReplayedEdits);
        KvVersion version = opened.AcquireCurrent();
        Assert.Equal(0, version.FileCount);
        version.Release();
    }

    [Fact]
    public void LogAndApply_PersistsFilesAndCountersAcrossReopen_WithLevelOrdering()
    {
        using (KvVersionSet set = KvVersionSet.Create(_root))
        {
            long a = AddFile(set, level: 0, "a", "m", sequence: 10);
            long b = AddFile(set, level: 0, "c", "z", sequence: 20);
            long c = AddFile(set, level: 1, "n", "p", sequence: 5);
            long d = AddFile(set, level: 1, "a", "b", sequence: 6);

            KvVersion version = set.AcquireCurrent();
            Assert.Equal([b, a], version.Files(0).Select(static f => f.FileNumber));
            Assert.Equal([d, c], version.Files(1).Select(static f => f.FileNumber));
            version.Release();
            Assert.Equal(20, set.LastSequence);
        }

        using KvVersionSet reopened = KvVersionSet.Open(_root);
        Assert.Equal(20, reopened.LastSequence);
        KvVersion reloaded = reopened.AcquireCurrent();
        Assert.Equal(4, reloaded.FileCount);
        Assert.Equal(2, reloaded.Files(0).Count);
        Assert.True(reloaded.Files(0)[0].FileNumber > reloaded.Files(0)[1].FileNumber);
        reloaded.Release();
        Assert.True(reopened.NextFileNumber > reloaded.AllFiles.Max(static f => f.FileNumber));
    }

    [Fact]
    public void LogAndApply_RejectsInconsistentEditsWithoutChangingStateOrFaulting()
    {
        using KvVersionSet set = KvVersionSet.Create(_root);
        long existing = AddFile(set, level: 1, "d", "f", sequence: 10);
        long manifestLength = ManifestLength(set);

        // 删除不存在的文件
        var deleteMissing = new KvVersionEdit();
        deleteMissing.DeletedFiles.Add((1, 999));
        Assert.Throws<ArgumentException>(() => set.LogAndApply(deleteMissing));

        // L1 范围重叠
        long overlapping = set.NewFileNumber();
        WriteSst(set, overlapping, "e", "g", 11);
        Assert.Throws<ArgumentException>(() => set.LogAndApply(Edit(Meta(set, overlapping, 1, "e", "g", 11), lastSequence: 11)));

        // 重复文件号
        Assert.Throws<ArgumentException>(() => set.LogAndApply(Edit(Meta(set, existing, 2, "x", "y", 10), lastSequence: 10)));

        // 未分配的文件号
        Assert.Throws<ArgumentException>(() => set.LogAndApply(Edit(
            new KvLsmFileMetadata(set.NextFileNumber + 5, 2, 100, Key("x", 1), Key("y", 1), 1, 1), lastSequence: 10)));

        // 计数器回退 / 文件 sequence 超过 LastSequence
        Assert.Throws<ArgumentException>(() => set.LogAndApply(new KvVersionEdit { LastSequence = 9 }));
        Assert.Throws<ArgumentException>(() => set.LogAndApply(new KvVersionEdit { NextFileNumber = 1 }));
        long tooNew = set.NewFileNumber();
        WriteSst(set, tooNew, "x", "y", 50);
        Assert.Throws<ArgumentException>(() => set.LogAndApply(Edit(Meta(set, tooNew, 2, "x", "y", 50), lastSequence: null)));

        Assert.False(set.IsFaulted);
        Assert.Equal(manifestLength, ManifestLength(set));
        KvVersion version = set.AcquireCurrent();
        Assert.Equal([existing], version.AllFiles.Select(static f => f.FileNumber));
        version.Release();
    }

    [Fact]
    public void ManifestRollover_KeepsStateAndRemovesOldManifest()
    {
        var options = new KvVersionSetOptions { MaxManifestBytes = 256 };
        using (KvVersionSet set = KvVersionSet.Create(_root, options: options))
        {
            long firstManifest = set.ManifestNumber;
            for (int i = 0; i < 20; i++)
                AddFile(set, level: 0, $"k{i:D2}", $"k{i:D2}", sequence: i + 1);
            Assert.NotEqual(firstManifest, set.ManifestNumber);
            Assert.Single(Directory.GetFiles(_root, "MANIFEST-*"));
        }

        using KvVersionSet reopened = KvVersionSet.Open(_root, options);
        KvVersion version = reopened.AcquireCurrent();
        Assert.Equal(20, version.FileCount);
        Assert.Equal(20, reopened.LastSequence);
        version.Release();
    }

    [Fact]
    public void TornManifestTail_IsIgnoredAndRewrittenOnOpen()
    {
        long kept;
        long manifest;
        using (KvVersionSet set = KvVersionSet.Create(_root))
        {
            kept = AddFile(set, level: 0, "a", "b", sequence: 1);
            AddFile(set, level: 0, "c", "d", sequence: 2);
            manifest = set.ManifestNumber;
        }

        string path = Path.Combine(_root, $"MANIFEST-{manifest:D12}");
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 3).ToArray());

        using KvVersionSet reopened = KvVersionSet.Open(_root);
        Assert.True(reopened.Recovery!.IgnoredTailBytes > 0);
        Assert.Equal(1, reopened.LastSequence);
        KvVersion version = reopened.AcquireCurrent();
        Assert.Equal([kept], version.AllFiles.Select(static f => f.FileNumber));
        version.Release();
        // 未提交编辑引用的 SST 成为孤儿并被回收；旧 manifest 被快照 manifest 取代。
        Assert.Single(reopened.Recovery.Cleanup.DeletedSstFiles);
        Assert.Single(Directory.GetFiles(_root, "MANIFEST-*"));
    }

    [Fact]
    public void GarbageAppendedAfterManifest_IsTreatedAsTornTail()
    {
        long manifest;
        using (KvVersionSet set = KvVersionSet.Create(_root))
        {
            AddFile(set, level: 0, "a", "b", sequence: 1);
            manifest = set.ManifestNumber;
        }

        string path = Path.Combine(_root, $"MANIFEST-{manifest:D12}");
        using (var stream = new FileStream(path, FileMode.Append))
            stream.Write([0x10, 0, 0, 0, 1, 2, 3, 4, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);

        using KvVersionSet reopened = KvVersionSet.Open(_root);
        Assert.Equal(20, reopened.Recovery!.IgnoredTailBytes);
        Assert.Equal(1, reopened.LastSequence);
    }

    [Fact]
    public void CorruptRecordFollowedByMoreData_FailsClosed()
    {
        long manifest;
        using (KvVersionSet set = KvVersionSet.Create(_root))
        {
            AddFile(set, level: 0, "a", "b", sequence: 1);
            AddFile(set, level: 0, "c", "d", sequence: 2);
            manifest = set.ManifestNumber;
        }

        string path = Path.Combine(_root, $"MANIFEST-{manifest:D12}");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[KvManifestFile.HeaderSize + KvManifestFile.RecordHeaderSize + 1] ^= 0x40; // 首条快照记录载荷
        File.WriteAllBytes(path, bytes);

        var exception = Assert.Throws<InvalidDataException>(() => KvVersionSet.Open(_root));
        Assert.Contains("followed by more data", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MANIFEST-1\n")]
    [InlineData("MANIFEST-000000000002")]
    [InlineData("MANIFEST-00000000000x\n")]
    [InlineData("MANIFEST-000000000999\n")]
    public void InvalidCurrent_FailsClosed(string content)
    {
        using (KvVersionSet.Create(_root)) { }
        File.WriteAllText(Path.Combine(_root, "CURRENT"), content);
        Assert.Throws<InvalidDataException>(() => KvVersionSet.Open(_root));
    }

    [Fact]
    public void MissingCurrent_ThrowsFileNotFound()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<FileNotFoundException>(() => KvVersionSet.Open(_root));
    }

    [Fact]
    public void MissingOrResizedSst_FailsClosedOnOpen()
    {
        long file;
        using (KvVersionSet set = KvVersionSet.Create(_root))
            file = AddFile(set, level: 0, "a", "b", sequence: 1);

        string sst = KvLsmPaths.SstPath(_root, file);
        using (var stream = new FileStream(sst, FileMode.Append))
            stream.WriteByte(0);
        Assert.Contains("does not match", Assert.Throws<InvalidDataException>(() => KvVersionSet.Open(_root)).Message, StringComparison.Ordinal);

        File.Delete(sst);
        Assert.Contains("missing SST", Assert.Throws<InvalidDataException>(() => KvVersionSet.Open(_root)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RemovesOrphanSstTempAndStaleManifestFiles()
    {
        long live;
        using (KvVersionSet set = KvVersionSet.Create(_root))
            live = AddFile(set, level: 0, "a", "b", sequence: 1);

        string sstDir = KvLsmPaths.SstDirectory(_root);
        File.WriteAllBytes(KvLsmPaths.SstPath(_root, 9_000), new byte[80]);
        File.WriteAllBytes(KvLsmPaths.SstPath(_root, 9_001) + ".tmp", new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "MANIFEST-000000008888"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "CURRENT.tmp"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "MANIFEST-000000008889.tmp"), new byte[10]);
        File.WriteAllBytes(Path.Combine(sstDir, "unrelated.txt"), new byte[1]);

        using KvVersionSet reopened = KvVersionSet.Open(_root);
        KvObsoleteCleanupResult cleanup = reopened.Recovery!.Cleanup;
        Assert.Equal([9_000L], cleanup.DeletedSstFiles);
        Assert.Contains("MANIFEST-000000008888", cleanup.DeletedManifests);
        // 遗留的 CURRENT.tmp 被打开时的快照重写覆盖并改名消费，不会残留。
        Assert.False(File.Exists(Path.Combine(_root, "CURRENT.tmp")));
        Assert.Contains("000000009001.SDBKVSST.tmp", cleanup.DeletedTempFiles);
        Assert.Contains("MANIFEST-000000008889.tmp", cleanup.DeletedTempFiles);
        Assert.True(File.Exists(KvLsmPaths.SstPath(_root, live)));
        Assert.True(File.Exists(Path.Combine(sstDir, "unrelated.txt")));
        Assert.Single(Directory.GetFiles(_root, "MANIFEST-*"));
    }

    [Fact]
    public void ObsoleteFiles_StayWhileOlderVersionOrPendingOutputReferencesThem()
    {
        using KvVersionSet set = KvVersionSet.Create(_root);
        long oldFile = AddFile(set, level: 0, "a", "b", sequence: 1);
        KvVersion pinned = set.AcquireCurrent();

        long replacement = set.NewFileNumber();
        WriteSst(set, replacement, "a", "b", 2);
        var compaction = Edit(Meta(set, replacement, 1, "a", "b", 2), lastSequence: 2);
        compaction.DeletedFiles.Add((0, oldFile));
        set.LogAndApply(compaction);

        long pending = set.NewFileNumber();
        WriteSst(set, pending, "x", "y", 2);
        File.WriteAllBytes(KvLsmPaths.SstPath(_root, set.NewFileNumber()) + ".tmp", new byte[4]);
        set.ReleasePendingOutput(set.NextFileNumber - 1);

        KvObsoleteCleanupResult whilePinned = set.DeleteObsoleteFiles();
        Assert.Empty(whilePinned.DeletedSstFiles);
        Assert.Single(whilePinned.DeletedTempFiles);
        Assert.True(File.Exists(KvLsmPaths.SstPath(_root, oldFile)));

        pinned.Release();
        set.ReleasePendingOutput(pending);
        KvObsoleteCleanupResult afterRelease = set.DeleteObsoleteFiles();
        Assert.Equal([oldFile, pending], afterRelease.DeletedSstFiles);
        Assert.True(File.Exists(KvLsmPaths.SstPath(_root, replacement)));
        Assert.Throws<InvalidOperationException>(() => pinned.Release());
    }

    [Theory]
    [InlineData((int)KvManifestPhase.BeforeRecordWrite, false)]
    [InlineData((int)KvManifestPhase.AfterRecordWriteBeforeSync, null)]
    [InlineData((int)KvManifestPhase.AfterRecordSync, true)]
    public void CrashDuringAppend_ReopenSeesOldOrNewStateAndInstanceFaults(int phaseValue, bool? committed)
    {
        var phase = (KvManifestPhase)phaseValue;
        bool armed = false;
        var options = new KvVersionSetOptions { TestHook = p => { if (armed && p == phase) throw new IOException("injected crash"); } };
        long newFile;
        using (KvVersionSet set = KvVersionSet.Create(_root, options: options))
        {
            AddFile(set, level: 0, "a", "b", sequence: 1);
            newFile = set.NewFileNumber();
            WriteSst(set, newFile, "c", "d", 2);
            armed = true;
            Assert.Throws<IOException>(() => set.LogAndApply(Edit(Meta(set, newFile, 0, "c", "d", 2), lastSequence: 2)));
            Assert.True(set.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => set.LogAndApply(new KvVersionEdit()));
            Assert.Throws<InvalidOperationException>(() => set.NewFileNumber());
        }

        using KvVersionSet reopened = KvVersionSet.Open(_root);
        bool sawNew = reopened.LastSequence == 2;
        if (committed.HasValue)
            Assert.Equal(committed.Value, sawNew);
        Assert.Equal(sawNew, File.Exists(KvLsmPaths.SstPath(_root, newFile)));
        KvVersion version = reopened.AcquireCurrent();
        Assert.Equal(sawNew ? 2 : 1, version.FileCount);
        version.Release();
    }

    [Theory]
    [InlineData((int)KvManifestPhase.NewManifestWritten, false)]
    [InlineData((int)KvManifestPhase.CurrentTempWritten, false)]
    [InlineData((int)KvManifestPhase.AfterCurrentSwitch, true)]
    public void CrashDuringRollover_ReopenSeesConsistentStateAndCleansLeftovers(int phaseValue, bool committed)
    {
        var phase = (KvManifestPhase)phaseValue;
        bool armed = false;
        var options = new KvVersionSetOptions
        {
            MaxManifestBytes = 1,
            TestHook = p => { if (armed && p == phase) throw new IOException("injected crash"); },
        };
        long newFile;
        using (KvVersionSet set = KvVersionSet.Create(_root, options: options))
        {
            AddFile(set, level: 0, "a", "b", sequence: 1);
            newFile = set.NewFileNumber();
            WriteSst(set, newFile, "c", "d", 2);
            armed = true;
            Assert.Throws<IOException>(() => set.LogAndApply(Edit(Meta(set, newFile, 0, "c", "d", 2), lastSequence: 2)));
            Assert.True(set.IsFaulted);
        }

        // 模拟真实崩溃可能留下的未切换 manifest（注入点的 finally 已删除它）。
        File.WriteAllBytes(Path.Combine(_root, "MANIFEST-000000007777"), new byte[16]);

        using KvVersionSet reopened = KvVersionSet.Open(_root);
        Assert.Equal(committed ? 2 : 1, reopened.LastSequence);
        Assert.Equal(committed, File.Exists(KvLsmPaths.SstPath(_root, newFile)));
        Assert.Single(Directory.GetFiles(_root, "MANIFEST-*"));
        Assert.False(File.Exists(Path.Combine(_root, "CURRENT.tmp")));
    }

    [Theory]
    [InlineData((int)KvManifestPhase.NewManifestWritten, false)]
    [InlineData((int)KvManifestPhase.CurrentTempWritten, false)]
    [InlineData((int)KvManifestPhase.AfterCurrentSwitch, true)]
    public void CrashDuringOpenRewrite_NextOpenStillRecoversSameState(int phaseValue, bool switched)
    {
        var phase = (KvManifestPhase)phaseValue;
        using (KvVersionSet set = KvVersionSet.Create(_root))
            AddFile(set, level: 0, "a", "b", sequence: 7);

        long before = ReadCurrentNumber();
        var crashing = new KvVersionSetOptions { TestHook = p => { if (p == phase) throw new IOException("injected crash"); } };
        Assert.Throws<IOException>(() => KvVersionSet.Open(_root, crashing));
        Assert.Equal(switched, ReadCurrentNumber() != before);

        using KvVersionSet reopened = KvVersionSet.Open(_root);
        Assert.Equal(7, reopened.LastSequence);
        KvVersion version = reopened.AcquireCurrent();
        Assert.Equal(1, version.FileCount);
        version.Release();
        Assert.Single(Directory.GetFiles(_root, "MANIFEST-*"));
    }

    [Fact]
    public void VersionEdit_EncodingRoundTripsAndRejectsUnknownOrRepeatedTags()
    {
        var edit = new KvVersionEdit { Generation = 2, LogNumber = 3, LastSequence = 4, NextFileNumber = 5 };
        edit.DeletedFiles.Add((2, 7));
        edit.AddedFiles.Add(new KvLsmFileMetadata(4, 3, 4096, Key("a", 4), Key("z", 1), 1, 4));

        KvVersionEdit decoded = KvVersionEdit.Decode(edit.Encode());
        Assert.Equal(edit.Generation, decoded.Generation);
        Assert.Equal(edit.LogNumber, decoded.LogNumber);
        Assert.Equal(edit.LastSequence, decoded.LastSequence);
        Assert.Equal(edit.NextFileNumber, decoded.NextFileNumber);
        Assert.Equal(edit.DeletedFiles, decoded.DeletedFiles);
        Assert.Equal(edit.AddedFiles, decoded.AddedFiles);

        Assert.Throws<InvalidDataException>(() => KvVersionEdit.Decode([99, 1]));
        Assert.Throws<InvalidDataException>(() => KvVersionEdit.Decode([1, 1, 1, 2]));
        Assert.Throws<InvalidDataException>(() => KvVersionEdit.Decode([6, 1]));
        byte[] encoded = edit.Encode();
        Assert.Throws<InvalidDataException>(() => KvVersionEdit.Decode(encoded.AsSpan(0, encoded.Length - 1)));
    }

    private long AddFile(KvVersionSet set, int level, string smallest, string largest, long sequence)
    {
        long number = set.NewFileNumber();
        WriteSst(set, number, smallest, largest, sequence);
        set.LogAndApply(Edit(Meta(set, number, level, smallest, largest, sequence), lastSequence: Math.Max(sequence, set.LastSequence)));
        return number;
    }

    private static void WriteSst(KvVersionSet set, long number, string smallest, string largest, long sequence)
    {
        using var writer = new KvSstWriter(KvLsmPaths.SstPath(set.RootDirectory, number), new KvSstWriterOptions { SyncDirectory = false });
        writer.Add(Key(smallest, sequence), "v"u8);
        if (smallest != largest)
            writer.Add(Key(largest, sequence), "v"u8);
        writer.Finish();
    }

    private static KvLsmFileMetadata Meta(KvVersionSet set, long number, int level, string smallest, string largest, long sequence)
    {
        long size = new FileInfo(KvLsmPaths.SstPath(set.RootDirectory, number)).Length;
        return new KvLsmFileMetadata(number, level, size, Key(smallest, sequence), Key(largest, sequence), sequence, sequence);
    }

    private static KvVersionEdit Edit(KvLsmFileMetadata file, long? lastSequence)
    {
        var edit = new KvVersionEdit { LastSequence = lastSequence };
        edit.AddedFiles.Add(file);
        return edit;
    }

    private static byte[] Key(string userKey, long sequence)
        => KvInternalKey.Encode(Encoding.UTF8.GetBytes(userKey), sequence, KvEntryKind.Put);

    private static long ManifestLength(KvVersionSet set)
        => new FileInfo(KvLsmPaths.ManifestPath(set.RootDirectory, set.ManifestNumber)).Length;

    private long ReadCurrentNumber()
    {
        string content = File.ReadAllText(Path.Combine(_root, "CURRENT")).TrimEnd('\n');
        Assert.True(KvLsmPaths.TryParseManifestFileName(content, out long number));
        return number;
    }
}
