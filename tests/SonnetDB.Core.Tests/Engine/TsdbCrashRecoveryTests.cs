using System.Runtime.InteropServices;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="Tsdb"/> 崩溃恢复场景测试。
/// </summary>
/// <remarks>
/// 三个崩溃场景：
/// <list type="bullet">
///   <item><description>场景 A：写入未 Flush 即崩溃 → WAL replay 重建 MemTable 和 Catalog</description></item>
///   <item><description>场景 B：Flush 完成后崩溃 → segments 保留，WAL replay 重建后续点</description></item>
///   <item><description>场景 C：Flush 中途崩溃（段文件已落盘但 Checkpoint 未写） → 删除孤立段后恰好一次 replay</description></item>
/// </list>
/// </remarks>
public sealed class TsdbCrashRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public TsdbCrashRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions(SegmentWriterOptions? segOpts = null) =>
        new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = 10_000_000,
                MaxBytes = 1024L * 1024 * 1024,
                MaxAge = TimeSpan.FromHours(24),
            },
            SegmentWriterOptions = segOpts ?? new SegmentWriterOptions { FsyncOnCommit = false },
        };

    private static Point MakePoint(string measurement, long timestamp, string host, double value) =>
        Point.Create(measurement, timestamp,
            new Dictionary<string, string> { ["host"] = host },
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(value) });

    private static IReadOnlyList<WalRecord> ReadAllWalRecords(string rootDirectory)
    {
        var records = new List<WalRecord>();
        foreach (var segment in WalSegmentLayout.Enumerate(TsdbPaths.WalDir(rootDirectory)))
        {
            if (!File.Exists(segment.Path))
                continue;

            using var reader = WalReader.Open(segment.Path);
            records.AddRange(reader.Replay());
        }

        return records;
    }

    private static WalCheckpointState LoadCheckpointRequired(string rootDirectory)
    {
        WalCheckpointState? checkpoint = WalCheckpointFile.TryLoad(
            WalSegmentLayout.CheckpointPath(TsdbPaths.WalDir(rootDirectory)));
        Assert.True(checkpoint.HasValue, "Expected a durable WAL checkpoint.");
        return checkpoint.Value;
    }

    private static int QueryPointCount(Tsdb db, string measurement, string host, string fieldName = "v")
    {
        var entry = Assert.Single(db.Catalog.Find(
            measurement,
            new Dictionary<string, string> { ["host"] = host }));
        return db.Query.Execute(new PointQuery(entry.Id, fieldName, TimeRange.All)).Count();
    }

    private (string SegmentPath, string PublicationPath, string WalPath, long CheckpointLsn)
        CreatePendingFlushPublicationForWalCoverage()
    {
        bool failedAfterRename = false;
        var db = Tsdb.Open(MakeOptions(new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            PostRenameAction = () =>
            {
                if (!failedAfterRename)
                {
                    failedAfterRename = true;
                    throw new IOException("Simulated post-rename failure for WAL coverage recovery.");
                }
            },
        }));

        db.Write(Point.Create(
            "cpu",
            1000L,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue>
            {
                ["v"] = FieldValue.FromDouble(1.0),
                ["v2"] = FieldValue.FromDouble(2.0),
            }));
        long checkpointLsn = db.MemTable.LastLsn;

        Assert.Throws<IOException>(() => db.FlushNow());
        Assert.True(failedAfterRename);
        db.CrashSimulationCloseWal();

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        var markerWalSegments = new List<WalSegmentInfo>();
        foreach (WalSegmentInfo candidate in WalSegmentLayout.Enumerate(walDirectory))
        {
            using var reader = WalReader.Open(candidate.Path);
            if (reader.Replay().Any(record => record.Lsn == checkpointLsn))
                markerWalSegments.Add(candidate);
        }

        WalSegmentInfo walSegment = Assert.Single(markerWalSegments);
        string segmentPath = TsdbPaths.SegmentPath(_tempDir, 1L);
        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, 1L);
        Assert.True(File.Exists(segmentPath));
        Assert.True(File.Exists(publicationPath));

        return (segmentPath, publicationPath, walSegment.Path, checkpointLsn);
    }

    private static long GetWalRecordOffset(string path, int recordIndex)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        long offset = FormatSizes.WalFileHeaderSize;
        Span<byte> payloadLengthBuffer = stackalloc byte[sizeof(int)];
        for (int index = 0; index < recordIndex; index++)
        {
            stream.Position = offset + 8;
            stream.ReadExactly(payloadLengthBuffer);
            int payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payloadLengthBuffer);
            offset += FormatSizes.WalRecordHeaderSize + payloadLength;
        }

        return offset;
    }

    private static void RewriteWalRecordLsn(string path, int recordIndex, long expectedOriginalLsn, long replacementLsn)
    {
        long offset = GetWalRecordOffset(path, recordIndex);
        Span<byte> headerBuffer = stackalloc byte[FormatSizes.WalRecordHeaderSize];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = offset;
        stream.ReadExactly(headerBuffer);

        WalRecordHeader header = MemoryMarshal.Read<WalRecordHeader>(headerBuffer);
        Assert.Equal(expectedOriginalLsn, header.Lsn);
        header.Lsn = replacementLsn;
        header.Reserved = 0;
        MemoryMarshal.Write(headerBuffer, in header);
        header.Reserved = WalRecordHeader.ComputeHeaderChecksum(headerBuffer);
        MemoryMarshal.Write(headerBuffer, in header);

        stream.Position = offset;
        stream.Write(headerBuffer);
        stream.Flush(flushToDisk: true);
    }

    private static void AppendTornWalTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.SetLength(stream.Length - FormatSizes.WalLastLsnFooterSize);
        stream.Position = stream.Length;
        stream.Write([0x52, 0x4C, 0x41, 0x57, 0x00]);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// 场景 A：写入 10 个点后崩溃（不 Flush，不 Dispose）。
    /// 重启后应通过 WAL replay 恢复全部 10 个点及 catalog。
    /// </summary>
    [Fact]
    public void ScenarioA_WriteWithoutFlush_RecoveredOnReopen()
    {
        const int pointCount = 10;
        var tags = new Dictionary<string, string> { ["host"] = "srv1" };

        // 会话 1：写入但不 Dispose（模拟崩溃）
        var db = Tsdb.Open(MakeOptions());
        for (int i = 0; i < pointCount; i++)
        {
            var p = Point.Create("cpu", 1000L + i, tags,
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(i * 10.0) });
            db.Write(p);
        }
        db.CrashSimulationCloseWal(); // 崩溃：不保存 catalog，不 Flush

        // 验证：catalog 文件未保存
        Assert.False(File.Exists(TsdbPaths.CatalogPath(_tempDir)));

        // 会话 2：重新打开，应通过 WAL replay 恢复
        using var db2 = Tsdb.Open(MakeOptions());

        // catalog 应通过 WAL 的 CreateSeries 记录重建出 1 个 series
        Assert.Equal(1, db2.Catalog.Count);

        // MemTable 应恢复 10 个点
        Assert.Equal(pointCount, (int)db2.MemTable.PointCount);
    }

    /// <summary>
    /// 场景 B：写入 1000 个点 → Flush → 再写 10 个点 → 崩溃。
    /// 重启后：segments 含 1 个，MemTable 含 10 个点。
    /// </summary>
    [Fact]
    public void ScenarioB_FlushThenWriteThenCrash_RecoveredOnReopen()
    {
        const int flushCount = 100;
        const int afterFlushCount = 10;
        var tags = new Dictionary<string, string> { ["host"] = "srv1" };

        // 会话 1：写入、Flush、再写、崩溃
        var db = Tsdb.Open(MakeOptions());
        for (int i = 0; i < flushCount; i++)
        {
            var p = Point.Create("cpu", 1000L + i, tags,
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(i) });
            db.Write(p);
        }

        var flushResult = db.FlushNow();
        Assert.NotNull(flushResult);
        Assert.Equal(0L, db.MemTable.PointCount);

        // 再写 10 个点
        for (int i = 0; i < afterFlushCount; i++)
        {
            var p = Point.Create("cpu", 2000L + i, tags,
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(100 + i) });
            db.Write(p);
        }

        Assert.Equal(afterFlushCount, (int)db.MemTable.PointCount);
        db.CrashSimulationCloseWal(); // 崩溃

        // 会话 2：重新打开
        using var db2 = Tsdb.Open(MakeOptions());

        // Segment 应保留
        var segs = db2.ListSegments();
        Assert.Single(segs);

        // catalog 由 Flush 前持久化的 checkpoint snapshot 恢复
        Assert.Equal(1, db2.Catalog.Count);

        // MemTable 应包含 Flush 之后写入的 10 个点
        Assert.Equal(afterFlushCount, (int)db2.MemTable.PointCount);
    }

    /// <summary>
    /// 兼容独立 checkpoint 文件之前创建的数据库：删除新格式的 checkpoint 元数据后，
    /// 只要 WAL 中仍保留旧 checkpoint 记录，重开仍应加载已发布段而不拒绝数据库。
    /// </summary>
    [Fact]
    public void Reopen_LegacyWalCheckpointWithoutSidecar_RemainsCompatible()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        _ = Assert.IsType<SegmentBuildResult>(db.FlushNow());
        db.CrashSimulationCloseWal();

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        string checkpointPath = WalSegmentLayout.CheckpointPath(walDirectory);
        Assert.True(File.Exists(checkpointPath));
        File.Delete(checkpointPath);

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Single(reopened.ListSegments());
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
    }

    /// <summary>
    /// 场景 C：SegmentWriter 完成 rename 后、写 Checkpoint 之前崩溃。
    /// 重启时 pending marker 必须删除孤立段，再由 WAL 恰好一次回放全部点。
    /// </summary>
    [Fact]
    public void ScenarioC_SegmentWrittenButCheckpointNotWritten_DeletesOrphanAndReplaysExactlyOnce()
    {
        const int pointCount = 5;
        var tags = new Dictionary<string, string> { ["host"] = "srv1" };

        // 配置：rename 后立即抛出异常，模拟 rename 之后、Checkpoint 之前崩溃
        bool crashed = false;
        var segOpts = new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            PostRenameAction = () =>
            {
                if (!crashed)
                {
                    crashed = true;
                    throw new IOException("Simulated crash after rename");
                }
            },
        };
        var options = MakeOptions(segOpts);

        // 会话 1：写入点，然后尝试 FlushNow（会在 rename 之后崩溃）
        var db = Tsdb.Open(options);
        for (int i = 0; i < pointCount; i++)
        {
            var p = Point.Create("cpu", 1000L + i, tags,
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(i * 10.0) });
            db.Write(p);
        }

        // FlushNow 应抛出异常（来自 PostRenameAction）
        Assert.Throws<IOException>(() => db.FlushNow());

        // 段文件应已存在（rename 已完成）
        string segPath = TsdbPaths.SegmentPath(_tempDir, 1L);
        Assert.True(File.Exists(segPath), "Segment file should exist after rename");
        string publicationPath = WalSegmentLayout.FlushPublicationPath(TsdbPaths.WalDir(_tempDir), 1L);
        Assert.True(File.Exists(publicationPath), "Pending publication marker should exist after rename");

        // 强制关闭（崩溃模拟：不保存 catalog，不再次 Flush）
        db.CrashSimulationCloseWal();

        // 验证：WAL segment 中没有 Checkpoint 记录
        var walDir = TsdbPaths.WalDir(_tempDir);
        var walSegments = WalSegmentLayout.Enumerate(walDir);
        var allCheckpoints = new List<CheckpointRecord>();
        foreach (var seg in walSegments)
        {
            using var walReader = WalReader.Open(seg.Path);
            allCheckpoints.AddRange(walReader.Replay().OfType<CheckpointRecord>());
        }
        Assert.Empty(allCheckpoints);

        // 会话 2：重新打开（不使用崩溃注入选项）
        using var db2 = Tsdb.Open(MakeOptions());

        // catalog 通过 WAL replay 重建
        Assert.Equal(1, db2.Catalog.Count);

        // orphan Segment 已被移除，WAL 仅重放一次而非与 Segment 叠加。
        Assert.Equal(pointCount, (int)db2.MemTable.PointCount);
        Assert.Equal(pointCount, QueryPointCount(db2, "cpu", "srv1", "usage"));
        Assert.Empty(db2.ListSegments());
        Assert.False(File.Exists(segPath));
        Assert.False(File.Exists(publicationPath));

        // 已删除的未提交 id 可以安全复用。
        var result = db2.FlushNow();
        Assert.NotNull(result);
        Assert.Equal(1L, result.SegmentId);
    }

    /// <summary>
    /// Pending marker 对应的 WAL 完全缺失时，重开不得删除唯一 segment 后继续运行。
    /// </summary>
    [Fact]
    public void Reopen_PendingPublicationWithMissingWal_FailsClosedBeforeDeletingSegment()
    {
        var state = CreatePendingFlushPublicationForWalCoverage();
        File.Delete(state.WalPath);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
        Assert.Contains("complete WAL coverage", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(state.PublicationPath));
        Assert.True(File.Exists(state.SegmentPath));
    }

    /// <summary>
    /// Pending marker 所需范围内的 WAL 发生尾部截断时，重开必须在删除段前拒绝。
    /// </summary>
    [Fact]
    public void Reopen_PendingPublicationWithTruncatedWalBeforeMarker_FailsClosedBeforeDeletingSegment()
    {
        var state = CreatePendingFlushPublicationForWalCoverage();
        long firstWriteOffset = GetWalRecordOffset(state.WalPath, recordIndex: 1);
        using (var stream = new FileStream(state.WalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            stream.SetLength(firstWriteOffset + 5);
            stream.Flush(flushToDisk: true);
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
        Assert.Contains("complete WAL coverage", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(state.PublicationPath));
        Assert.True(File.Exists(state.SegmentPath));
    }

    /// <summary>
    /// Pending marker 所需范围内出现 LSN gap 时，重开必须在删除段前拒绝。
    /// </summary>
    [Fact]
    public void Reopen_PendingPublicationWithWalLsnGap_FailsClosedBeforeDeletingSegment()
    {
        var state = CreatePendingFlushPublicationForWalCoverage();
        RewriteWalRecordLsn(state.WalPath, recordIndex: 1, expectedOriginalLsn: 2L, replacementLsn: 3L);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
        Assert.Contains("LSN gap", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(state.PublicationPath));
        Assert.True(File.Exists(state.SegmentPath));
    }

    /// <summary>
    /// marker 之后的合法 torn tail 不影响其已完整覆盖的记录，重开仍应回放并移除孤立段。
    /// </summary>
    [Fact]
    public void Reopen_PendingPublicationWithTornWalTailAfterMarker_ReplaysAndRemovesOrphan()
    {
        var state = CreatePendingFlushPublicationForWalCoverage();
        AppendTornWalTail(state.WalPath);

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
        Assert.False(File.Exists(state.PublicationPath));
        Assert.False(File.Exists(state.SegmentPath));
    }

    /// <summary>
    /// 直接 Dispose Flush 会保留其 checkpoint record，因而下一个 Dispose 在 rename 后
    /// 中断时仍可从前一个 durable checkpoint 的下一条 LSN 严格连续重放。
    /// </summary>
    [Fact]
    public void Reopen_PendingPublicationAfterDirectFlush_ReplaysFromRetainedCheckpointRecord()
    {
        var first = Tsdb.Open(MakeOptions());
        first.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        first.Dispose();

        bool failedAfterRename = false;
        var second = Tsdb.Open(MakeOptions(new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            PostRenameAction = () =>
            {
                failedAfterRename = true;
                throw new IOException("Simulated direct-flush post-rename failure.");
            },
        }));
        second.Write(MakePoint("cpu", 2000L, "srv1", 2.0));
        second.Dispose();

        Assert.True(failedAfterRename);
        string walDirectory = TsdbPaths.WalDir(_tempDir);
        Assert.Contains(ReadAllWalRecords(_tempDir), static record => record is CheckpointRecord);
        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, 2L);
        Assert.True(File.Exists(publicationPath));

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Equal(2, QueryPointCount(reopened, "cpu", "srv1"));
        Assert.False(File.Exists(publicationPath));
        Assert.False(File.Exists(TsdbPaths.SegmentPath(_tempDir, 2L)));
    }

    /// <summary>
    /// pending marker 已经持久化、但 SegmentWriter 尚未 rename 时若进程被终止，重启必须清理
    /// 配置后缀的临时段文件；否则重用同一个 SegmentId 时 FileMode.CreateNew 会永久冲突。
    /// </summary>
    [Fact]
    public void Reopen_PendingPublicationBeforeRename_RemovesTemporarySegmentAndReusesSegmentId()
    {
        const string temporarySuffix = ".flush-partial";
        var options = MakeOptions(new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            TempFileSuffix = temporarySuffix,
        });

        var db = Tsdb.Open(options);
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        long pendingLsn = db.MemTable.LastLsn;
        db.CrashSimulationCloseWal();

        string temporarySegmentPath = TsdbPaths.SegmentPath(_tempDir, 1L) + temporarySuffix;
        string? segmentDirectory = Path.GetDirectoryName(temporarySegmentPath);
        Assert.False(string.IsNullOrEmpty(segmentDirectory));
        Directory.CreateDirectory(segmentDirectory!);
        File.WriteAllBytes(temporarySegmentPath, [0x01, 0x02, 0x03]);

        string publicationPath = WalSegmentLayout.FlushPublicationPath(TsdbPaths.WalDir(_tempDir), 1L);
        _ = FlushPublicationFile.SavePending(publicationPath, 1L, pendingLsn);

        using var reopened = Tsdb.Open(options);
        Assert.False(File.Exists(temporarySegmentPath));
        Assert.False(File.Exists(publicationPath));
        Assert.Empty(reopened.ListSegments());
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));

        SegmentBuildResult result = Assert.IsType<SegmentBuildResult>(reopened.FlushNow());
        Assert.Equal(1L, result.SegmentId);
        Assert.False(File.Exists(temporarySegmentPath));
    }

    /// <summary>
    /// 没有 publication marker 的 compaction 临时段也必须在完整 Tsdb 启动路径中清理；
    /// 这覆盖进程在 marker 写入前终止的边界。只匹配 canonical 段文件名，其他临时文件保留。
    /// </summary>
    [Fact]
    public void Reopen_UnassociatedCanonicalSegmentTemporaryFile_IsRemoved()
    {
        const string temporarySuffix = ".compaction-partial";
        var options = MakeOptions(new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            TempFileSuffix = temporarySuffix,
        });

        var db = Tsdb.Open(options);
        db.CrashSimulationCloseWal();

        string temporarySegmentPath = TsdbPaths.SegmentPath(_tempDir, 77L) + temporarySuffix;
        string? segmentDirectory = Path.GetDirectoryName(temporarySegmentPath);
        Assert.False(string.IsNullOrEmpty(segmentDirectory));
        Directory.CreateDirectory(segmentDirectory!);
        File.WriteAllBytes(temporarySegmentPath, [0x01, 0x02, 0x03]);

        string unrelatedPath = Path.Combine(segmentDirectory!, "unrelated.SDBSEG" + temporarySuffix);
        File.WriteAllBytes(unrelatedPath, [0x04, 0x05]);
        string wrongSuffixPath = TsdbPaths.SegmentPath(_tempDir, 78L) + ".other-temp";
        File.WriteAllBytes(wrongSuffixPath, [0x06, 0x07]);

        using var reopened = Tsdb.Open(options);

        Assert.False(File.Exists(temporarySegmentPath));
        Assert.True(File.Exists(unrelatedPath), "非 canonical 文件名不应被临时段清理误删");
        Assert.True(File.Exists(wrongSuffixPath), "未配置的临时后缀不应被清理");
    }

    [Fact]
    public void FlushFailure_FollowedByWriteMutations_RejectsWithoutChangingRecoveryState()
    {
        const int firstBatchCount = 5;
        bool failedAfterRename = false;
        var options = MakeOptions(new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            PostRenameAction = () =>
            {
                if (!failedAfterRename)
                {
                    failedAfterRename = true;
                    throw new IOException("Simulated post-rename flush failure");
                }
            },
        });

        var db = Tsdb.Open(options);
        Assert.Equal(1, db.WriteMany([MakePoint("cpu", 1000L, "srv1", 0.0)], "before-flush-failure"));
        for (int i = 1; i < firstBatchCount; i++)
            db.Write(MakePoint("cpu", 1000L + i, "srv1", i));

        Assert.Throws<IOException>(() => db.FlushNow());

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        string ledgerPath = TsdbPaths.MeasurementBatchLedgerPath(_tempDir);
        ulong seriesId = Assert.Single(db.Catalog.Find(
            "cpu",
            new Dictionary<string, string> { ["host"] = "srv1" })).Id;
        long activePointCountBefore = db.MemTable.PointCount;
        int visiblePointCountBefore = QueryPointCount(db, "cpu", "srv1");
        int walRecordCountBefore = ReadAllWalRecords(_tempDir).Count;
        byte[] ledgerBefore = File.ReadAllBytes(ledgerPath);

        // 发布失败后，不得再接受新的写、批量写、幂等批次账本或删除。调用方收到的异常
        // 必须发生在 WAL / MemTable / ledger 变更之前，避免重试时出现不可控的额外数据。
        var writeFault = Assert.Throws<InvalidOperationException>(
            () => db.Write(MakePoint("cpu", 2000L, "srv1", 100.0)));
        Assert.IsType<IOException>(writeFault.InnerException);

        var writeManyFault = Assert.Throws<InvalidOperationException>(
            () => db.WriteMany([MakePoint("cpu", 2001L, "srv1", 101.0)]));
        Assert.IsType<IOException>(writeManyFault.InnerException);

        var batchFault = Assert.Throws<InvalidOperationException>(
            () => db.WriteMany([MakePoint("cpu", 2002L, "srv1", 102.0)], "blocked-after-flush-failure"));
        Assert.IsType<IOException>(batchFault.InnerException);

        var deleteFault = Assert.Throws<InvalidOperationException>(
            () => db.Delete(seriesId, "v", 1000L, 1000L));
        Assert.IsType<IOException>(deleteFault.InnerException);

        Assert.Equal(activePointCountBefore, db.MemTable.PointCount);
        Assert.Equal(visiblePointCountBefore, QueryPointCount(db, "cpu", "srv1"));
        Assert.Equal(walRecordCountBefore, ReadAllWalRecords(_tempDir).Count);
        Assert.Equal(ledgerBefore, File.ReadAllBytes(ledgerPath));
        Assert.Null(WalCheckpointFile.TryLoad(WalSegmentLayout.CheckpointPath(walDirectory)));
        Assert.Empty(ReadAllWalRecords(_tempDir).OfType<CheckpointRecord>());

        db.CrashSimulationCloseWal();

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Empty(reopened.ListSegments());
        Assert.Equal(firstBatchCount, (int)reopened.MemTable.PointCount);
        Assert.Equal(firstBatchCount, QueryPointCount(reopened, "cpu", "srv1"));
    }

    [Fact]
    public async Task FlushFailure_BeforeBatchAdmission_DoesNotPersistPendingLedger()
    {
        bool failedAfterRename = false;
        var options = MakeOptions(new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            PostRenameAction = () =>
            {
                if (!failedAfterRename)
                {
                    failedAfterRename = true;
                    throw new IOException("Simulated batch-admission flush failure");
                }
            },
        });

        var db = Tsdb.Open(options);
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));

        using var admissionReached = new ManualResetEventSlim(initialState: false);
        using var releaseAdmission = new ManualResetEventSlim(initialState: false);
        db.BeforeMeasurementBatchAdmissionTestHook = () =>
        {
            admissionReached.Set();
            if (!releaseAdmission.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out waiting to release batch admission.");
        };

        Task<Exception?> batchTask = Task.Run(() =>
        {
            try
            {
                _ = db.WriteMany([MakePoint("cpu", 2000L, "srv1", 2.0)], "blocked-batch-admission");
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        try
        {
            Assert.True(admissionReached.Wait(TimeSpan.FromSeconds(5)), "批次应在 admission 前暂停。");
            Assert.Throws<IOException>(() => db.FlushNow());
            Assert.False(File.Exists(TsdbPaths.MeasurementBatchLedgerPath(_tempDir)));
        }
        finally
        {
            releaseAdmission.Set();
        }

        Exception exception = Assert.IsType<InvalidOperationException>(await batchTask);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.False(File.Exists(TsdbPaths.MeasurementBatchLedgerPath(_tempDir)));

        db.BeforeMeasurementBatchAdmissionTestHook = null;
        db.CrashSimulationCloseWal();
    }

    [Fact]
    public async Task FlushFailure_AfterBatchAdmission_CompletesFirstMutationAndCommitsLedger()
    {
        bool failedAfterRename = false;
        var options = MakeOptions(new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            PostRenameAction = () =>
            {
                if (!failedAfterRename)
                {
                    failedAfterRename = true;
                    throw new IOException("Simulated admitted-batch flush failure");
                }
            },
        }) with
        {
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = 10_000_000,
                MaxBytes = 1024L * 1024 * 1024,
                HardCapBytes = 128,
                MaxAge = TimeSpan.FromHours(24),
            },
        };

        var db = Tsdb.Open(options);
        db.Write(Point.Create(
            "cpu",
            1000L,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { ["payload"] = FieldValue.FromString("seed") }));

        using var admissionReached = new ManualResetEventSlim(initialState: false);
        using var releaseAdmission = new ManualResetEventSlim(initialState: false);
        db.AfterMeasurementBatchAdmissionTestHook = () =>
        {
            admissionReached.Set();
            if (!releaseAdmission.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out waiting to release admitted batch.");
        };

        Task<int> batchTask = Task.Run(
            () => db.WriteMany(
                [Point.Create(
                    "cpu",
                    2000L,
                    new Dictionary<string, string> { ["host"] = "srv1" },
                    new Dictionary<string, FieldValue>
                    {
                        ["payload"] = FieldValue.FromString(new string('x', 1024)),
                    })],
                "admitted-batch"));

        try
        {
            Assert.True(admissionReached.Wait(TimeSpan.FromSeconds(5)), "批次应在取得 admission 后暂停。");
            Assert.Throws<IOException>(() => db.FlushNow());
        }
        finally
        {
            releaseAdmission.Set();
        }

        Assert.Equal(1, await batchTask);
        string[] ledger = File.ReadAllLines(TsdbPaths.MeasurementBatchLedgerPath(_tempDir));
        Assert.Equal(2, ledger.Length);
        Assert.StartsWith("P\tadmitted-batch\t", ledger[0], StringComparison.Ordinal);
        Assert.StartsWith("C\tadmitted-batch\t", ledger[1], StringComparison.Ordinal);
        Assert.Equal(2, QueryPointCount(db, "cpu", "srv1", "payload"));

        db.AfterMeasurementBatchAdmissionTestHook = null;
        db.CrashSimulationCloseWal();
    }

    [Fact]
    public void Dispose_WhenWorkerShutdownThrows_ReleasesRootLeaseAfterCommittedCleanup()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        db.BeforeBackgroundWorkerShutdownTestHook = () =>
            throw new IOException("Simulated worker shutdown failure");

        Assert.Throws<IOException>(db.Dispose);

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
    }

    [Fact]
    public async Task Dispose_BeforeStoppingFlushPump_RejectsConcurrentWrite()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));

        using var stoppingWorkers = new ManualResetEventSlim(initialState: false);
        using var releaseWorkers = new ManualResetEventSlim(initialState: false);
        db.BeforeBackgroundWorkerShutdownTestHook = () =>
        {
            stoppingWorkers.Set();
            if (!releaseWorkers.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out waiting to continue shutdown.");
        };

        Task disposeTask = Task.Run(db.Dispose);
        try
        {
            Assert.True(stoppingWorkers.Wait(TimeSpan.FromSeconds(5)), "Dispose 应在停止 worker 前进入测试同步点。");
            Assert.Throws<ObjectDisposedException>(() => db.Write(MakePoint("cpu", 2000L, "srv1", 2.0)));
        }
        finally
        {
            releaseWorkers.Set();
        }

        await disposeTask;
        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
    }

    /// <summary>
    /// 懒枚举查询持有 reader lease 时，关闭可以返回，但根目录 lease 必须延后到枚举器释放。
    /// 否则新进程可能在旧查询仍读取 segment 时进入恢复或写入路径。
    /// </summary>
    [Fact]
    public async Task Dispose_WithActiveReadSnapshot_KeepsRootLeaseUntilSnapshotReleased()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        _ = db.FlushNow();

        ulong seriesId = Assert.Single(db.Catalog.Find(
            "cpu",
            new Dictionary<string, string> { ["host"] = "srv1" })).Id;
        IEnumerator<DataPoint> enumerator = db.Query.Execute(
            new PointQuery(seriesId, "v", TimeRange.All)).GetEnumerator();
        Assert.True(enumerator.MoveNext());

        Task disposeTask = Task.Run(db.Dispose);
        try
        {
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<IOException>(() => Tsdb.Open(MakeOptions()));
        }
        finally
        {
            enumerator.Dispose();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        // reader drain 的 continuation 异步释放目录锁；等待实际可重开，而不是假定 Dispose 同步完成 continuation。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Tsdb? reopenedDatabase = null;
        for (int attempt = 0; attempt < 100 && reopenedDatabase is null; attempt++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                reopenedDatabase = Tsdb.Open(MakeOptions());
            }
            catch (IOException) when (attempt < 99)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
        }
        using var reopened = Assert.IsType<Tsdb>(reopenedDatabase);
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
    }

    [Fact]
    public void CrashSimulationCloseWal_WhenWorkerShutdownThrows_ReleasesRootLeaseAfterCommittedCleanup()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        db.BeforeBackgroundWorkerShutdownTestHook = () =>
            throw new IOException("Simulated crash worker shutdown failure");

        Assert.Throws<IOException>(db.CrashSimulationCloseWal);

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
    }

    [Fact]
    public void Reopen_PendingPublicationWithExactDurableCheckpoint_PreservesSegmentAndCleansMarker()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        var segment = Assert.IsType<SegmentBuildResult>(db.FlushNow());

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        WalCheckpointState checkpoint = LoadCheckpointRequired(_tempDir);
        Assert.Equal(segment.SegmentId, checkpoint.SegmentId);

        db.CrashSimulationCloseWal();

        // 模拟掉电正好发生在 checkpoint 持久化后、marker 升级前。
        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, segment.SegmentId);
        _ = FlushPublicationFile.SavePending(
            publicationPath,
            segment.SegmentId,
            checkpoint.CheckpointLsn);

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Single(reopened.ListSegments());
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
        Assert.False(File.Exists(publicationPath));
    }

    /// <summary>
    /// checkpoint 有效性检查必须使用调用方配置的段读取策略。关闭索引 CRC 校验的实例可以
    /// 打开带有无害索引保留位差异的既有段；重开时不能因 checkpoint 检查退回默认校验而重放
    /// 已落段的 WAL，从而把同一数据同时暴露为 segment 和 MemTable。
    /// </summary>
    [Fact]
    public void Reopen_ConfiguredSegmentReaderOptions_PreservesCheckpointRecoveryBoundary()
    {
        var options = MakeOptions() with
        {
            SegmentReaderOptions = new SegmentReaderOptions
            {
                VerifyIndexCrc = false,
            },
        };

        var db = Tsdb.Open(options);
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        var segment = Assert.IsType<SegmentBuildResult>(db.FlushNow());
        db.CrashSimulationCloseWal();

        // 保留字段不参与 BlockDescriptor 的解释，但会改变 index CRC。该段只应在显式关闭
        // VerifyIndexCrc 的配置下可读，正好覆盖 checkpoint 预验证与正式加载策略不一致的边界。
        byte[] bytes = File.ReadAllBytes(segment.Path);
        var footer = MemoryMarshal.Read<SegmentFooter>(
            bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));
        bytes[checked((int)footer.IndexOffset + 40)] ^= 0x01;
        File.WriteAllBytes(segment.Path, bytes);

        using var reopened = Tsdb.Open(options);
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
        Assert.Equal(0L, reopened.MemTable.PointCount);
        Assert.Single(reopened.ListSegments());
    }

    /// <summary>
    /// 独立 checkpoint 所指段已随 WAL 回收而丢失时，WAL 内残留 checkpoint 不能单独
    /// 作为跳过依据；否则重开会成功但把唯一已落盘数据静默丢失。
    /// </summary>
    [Fact]
    public void Reopen_MissingCheckpointSegmentAfterWalRecycle_FailsClosed()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        SegmentBuildResult segment = Assert.IsType<SegmentBuildResult>(db.FlushNow());
        db.CrashSimulationCloseWal();

        Assert.Contains(
            ReadAllWalRecords(_tempDir),
            record => record is CheckpointRecord { CheckpointLsn: > 0L });
        File.Delete(segment.Path);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
        Assert.Contains("highest verified durable checkpoint", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// checkpoint 元数据本身损坏时，不能因 WAL 内 checkpoint 继续跳过已经被回收的写入。
    /// </summary>
    [Fact]
    public void Reopen_CorruptDurableCheckpointWithWalCheckpoint_FailsClosed()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        _ = db.FlushNow();
        db.CrashSimulationCloseWal();

        string checkpointPath = WalSegmentLayout.CheckpointPath(TsdbPaths.WalDir(_tempDir));
        byte[] checkpoint = File.ReadAllBytes(checkpointPath);
        checkpoint[0] ^= 0x01;
        File.WriteAllBytes(checkpointPath, checkpoint);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
        Assert.Contains("highest verified durable checkpoint", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 已发布的历史 canonical segment 损坏时，启动必须失败而不是跳过该文件；对应 WAL
    /// 已在两次成功 flush 后回收，静默继续会永久丢失早期数据。
    /// </summary>
    [Fact]
    public void Reopen_CorruptPublishedHistoricalSegmentAfterWalRecycle_FailsClosed()
    {
        TsdbOptions options = MakeOptions() with
        {
            Compaction = new SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
        };
        var db = Tsdb.Open(options);
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        SegmentBuildResult firstSegment = Assert.IsType<SegmentBuildResult>(db.FlushNow());
        db.Write(MakePoint("cpu", 2000L, "srv1", 2.0));
        _ = db.FlushNow();
        db.CrashSimulationCloseWal();

        Assert.Empty(ReadAllWalRecords(_tempDir).OfType<WritePointRecord>());
        byte[] bytes = File.ReadAllBytes(firstSegment.Path);
        var footer = MemoryMarshal.Read<SegmentFooter>(
            bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));
        bytes[checked((int)footer.IndexOffset)] ^= 0x01;
        File.WriteAllBytes(firstSegment.Path, bytes);

        Assert.Throws<SegmentCorruptedException>(() => Tsdb.Open(options));
    }

    [Fact]
    public void Reopen_StaleCommittedPublicationAfterLaterCheckpoint_PreservesDataAndCleansMarker()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        var firstSegment = Assert.IsType<SegmentBuildResult>(db.FlushNow());

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        WalCheckpointState firstCheckpoint = LoadCheckpointRequired(_tempDir);

        db.Write(MakePoint("cpu", 2000L, "srv1", 2.0));
        _ = db.FlushNow();
        WalCheckpointState laterCheckpoint = LoadCheckpointRequired(_tempDir);
        Assert.True(laterCheckpoint.CheckpointLsn > firstCheckpoint.CheckpointLsn);

        db.CrashSimulationCloseWal();

        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, firstSegment.SegmentId);
        FlushPublicationState pending = FlushPublicationFile.SavePending(
            publicationPath,
            firstSegment.SegmentId,
            firstCheckpoint.CheckpointLsn);
        FlushPublicationFile.MarkCommittedRequired(publicationPath, pending);

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Equal(2, reopened.ListSegments().Count);
        Assert.Equal(2, QueryPointCount(reopened, "cpu", "srv1"));
        Assert.False(File.Exists(publicationPath));
    }

    [Fact]
    public void Reopen_PendingPublicationBehindLaterCheckpoint_FailsClosed()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        var firstSegment = Assert.IsType<SegmentBuildResult>(db.FlushNow());

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        WalCheckpointState firstCheckpoint = LoadCheckpointRequired(_tempDir);

        db.Write(MakePoint("cpu", 2000L, "srv1", 2.0));
        _ = db.FlushNow();
        db.CrashSimulationCloseWal();

        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, firstSegment.SegmentId);
        _ = FlushPublicationFile.SavePending(
            publicationPath,
            firstSegment.SegmentId,
            firstCheckpoint.CheckpointLsn);

        Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
        Assert.True(File.Exists(publicationPath));
        Assert.True(File.Exists(TsdbPaths.SegmentPath(_tempDir, firstSegment.SegmentId)));
    }

    [Fact]
    public void Reopen_PendingPublicationCoveredByWalCheckpoint_FailsClosed()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        long pendingLsn = db.MemTable.LastLsn;
        db.CrashSimulationCloseWal();

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, 1L);
        _ = FlushPublicationFile.SavePending(publicationPath, 1L, pendingLsn);

        using (var walSet = WalSegmentSet.Open(
            walDirectory,
            new WalRollingPolicy { Enabled = false },
            bufferSize: 64 * 1024))
        {
            _ = walSet.AppendCheckpoint(pendingLsn);
            walSet.Sync();
        }

        Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
        Assert.True(File.Exists(publicationPath));
    }

    [Fact]
    public void Reopen_PendingPublicationCoveredByLegacyWalCheckpoint_FailsClosed()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        long pendingLsn = db.MemTable.LastLsn;
        db.CrashSimulationCloseWal();

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        using (var walSet = WalSegmentSet.Open(
            walDirectory,
            new WalRollingPolicy { Enabled = false },
            bufferSize: 64 * 1024))
        {
            _ = walSet.AppendCheckpoint(pendingLsn);
            walSet.Sync();
        }

        WalSegmentInfo walSegment = Assert.Single(WalSegmentLayout.Enumerate(walDirectory));
        string legacyPath = Path.Combine(walDirectory, WalSegmentLayout.LegacyActiveFileName);
        File.Move(walSegment.Path, legacyPath);

        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, 1L);
        _ = FlushPublicationFile.SavePending(publicationPath, 1L, pendingLsn);

        Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
        Assert.True(File.Exists(publicationPath));
        Assert.False(File.Exists(legacyPath));
        Assert.Single(WalSegmentLayout.Enumerate(walDirectory));
    }

    [Fact]
    public void Reopen_CorruptFinalPublicationMarker_FailsClosed()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        db.CrashSimulationCloseWal();

        string publicationPath = WalSegmentLayout.FlushPublicationPath(TsdbPaths.WalDir(_tempDir), 1L);
        File.WriteAllBytes(publicationPath, [0x01, 0x02, 0x03]);

        Assert.Throws<InvalidDataException>(() => Tsdb.Open(MakeOptions()));
    }

    [Fact]
    public void Reopen_TemporaryPublicationMarker_IsIgnored()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(MakePoint("cpu", 1000L, "srv1", 1.0));
        db.CrashSimulationCloseWal();

        string temporaryPublicationPath = WalSegmentLayout.FlushPublicationPath(
            TsdbPaths.WalDir(_tempDir),
            1L) + FlushPublicationFile.TempSuffix;
        File.WriteAllBytes(temporaryPublicationPath, [0x01, 0x02, 0x03]);

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Equal(1, QueryPointCount(reopened, "cpu", "srv1"));
        Assert.True(File.Exists(temporaryPublicationPath));
    }

    [Fact]
    public void Dispose_PostRenameFailure_LeavesPendingMarkerAndReopensExactlyOnce()
    {
        const int pointCount = 4;
        bool failedAfterRename = false;
        var db = Tsdb.Open(MakeOptions(new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            PostRenameAction = () =>
            {
                if (!failedAfterRename)
                {
                    failedAfterRename = true;
                    throw new IOException("Simulated Dispose post-rename failure");
                }
            },
        }));

        for (int i = 0; i < pointCount; i++)
            db.Write(MakePoint("cpu", 1000L + i, "srv1", i));

        // Dispose 的直接 Flush 会吞掉 I/O 异常以完成资源释放；marker 仍必须保留给下次 Open。
        db.Dispose();
        Assert.True(failedAfterRename);

        string walDirectory = TsdbPaths.WalDir(_tempDir);
        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, 1L);
        Assert.True(File.Exists(publicationPath));
        Assert.True(File.Exists(TsdbPaths.SegmentPath(_tempDir, 1L)));

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Empty(reopened.ListSegments());
        Assert.Equal(pointCount, QueryPointCount(reopened, "cpu", "srv1"));
        Assert.False(File.Exists(publicationPath));
    }

    /// <summary>
    /// 崩溃后重新打开，catalog 通过 WAL 的 CreateSeries 记录正确重建。
    /// </summary>
    [Fact]
    public void CrashRecovery_CatalogRebuiltFromWalCreateSeries()
    {
        var tags1 = new Dictionary<string, string> { ["host"] = "a" };
        var tags2 = new Dictionary<string, string> { ["host"] = "b" };

        // 会话 1：写入两个不同 series 的点，然后崩溃
        var db = Tsdb.Open(MakeOptions());
        db.Write(Point.Create("cpu", 1000L, tags1,
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(1.0) }));
        db.Write(Point.Create("cpu", 2000L, tags2,
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(2.0) }));
        db.CrashSimulationCloseWal();

        // 会话 2：通过 WAL replay 重建 catalog
        using var db2 = Tsdb.Open(MakeOptions());
        Assert.Equal(2, db2.Catalog.Count);
        Assert.Equal(2L, db2.MemTable.PointCount);
    }

    [Fact]
    public void FlushNow_DoesNotRewriteFullCatalogSnapshotIntoWal()
    {
        var db = Tsdb.Open(MakeOptions());
        const int seriesCount = 64;

        for (int i = 0; i < seriesCount; i++)
            db.Write(MakePoint("metric", 1000L + i, $"h{i}", i));

        db.FlushNow();

        var walRecords = ReadAllWalRecords(_tempDir);
        Assert.Empty(walRecords.OfType<CreateSeriesRecord>());
        Assert.Empty(walRecords.OfType<WritePointRecord>());
        Assert.Equal(seriesCount, CatalogFileCodec.Load(TsdbPaths.CatalogPath(_tempDir)).Count);

        db.CrashSimulationCloseWal();
    }

    [Fact]
    public void CrashRecovery_AfterFlushWithoutWalCreateSeries_UsesCatalogCheckpoint()
    {
        var db = Tsdb.Open(MakeOptions());
        const int seriesCount = 12;

        for (int i = 0; i < seriesCount; i++)
            db.Write(MakePoint("sensor", 1000L + i, $"s{i}", i));

        db.FlushNow();
        Assert.Empty(ReadAllWalRecords(_tempDir).OfType<CreateSeriesRecord>());
        db.CrashSimulationCloseWal();

        using var db2 = Tsdb.Open(MakeOptions());
        Assert.Equal(seriesCount, db2.Catalog.Count);
        Assert.Equal(0L, db2.MemTable.PointCount);
        Assert.Equal(1, QueryPointCount(db2, "sensor", "s5"));
    }

    [Fact]
    public void CrashRecovery_CatalogCheckpointPlusWalDelta_RebuildsConsistentState()
    {
        var db = Tsdb.Open(MakeOptions());

        for (int i = 0; i < 5; i++)
            db.Write(MakePoint("cpu", 1000L + i, "flushed", i));

        db.FlushNow();

        db.Write(MakePoint("cpu", 2000L, "wal-delta", 100.0));

        Assert.Equal(1, CatalogFileCodec.Load(TsdbPaths.CatalogPath(_tempDir)).Count);
        db.CrashSimulationCloseWal();

        using var db2 = Tsdb.Open(MakeOptions());
        Assert.Equal(2, db2.Catalog.Count);
        Assert.Equal(1L, db2.MemTable.PointCount);
        Assert.Equal(5, QueryPointCount(db2, "cpu", "flushed"));
        Assert.Equal(1, QueryPointCount(db2, "cpu", "wal-delta"));
    }

    [Fact]
    public void CrashRecovery_AfterBatchedSchemaOnWrite_ReplaysDataWithPersistedSchema()
    {
        var db = Tsdb.Open(MakeOptions());
        Point[] points =
        [
            MakePoint("batch_metric", 1000L, "h1", 1.0),
            Point.Create("batch_metric", 1001L,
                new Dictionary<string, string> { ["host"] = "h1", ["rack"] = "r1" },
                new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(2.0), ["extra"] = FieldValue.FromLong(7L) }),
        ];

        Assert.Equal(2, db.WriteMany(points));
        Assert.Equal(1L, db.MeasurementSchemaPersistCount);
        db.CrashSimulationCloseWal();

        using var reopened = Tsdb.Open(MakeOptions());
        var schema = reopened.Measurements.TryGet("batch_metric");
        Assert.NotNull(schema);
        Assert.NotNull(schema!.TryGetColumn("rack"));
        Assert.NotNull(schema.TryGetColumn("extra"));
        Assert.Equal(3L, reopened.MemTable.PointCount);
    }
}
