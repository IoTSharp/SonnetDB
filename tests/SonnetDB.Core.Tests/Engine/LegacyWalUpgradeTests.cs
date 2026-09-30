using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// Legacy WAL（<c>wal/active.SDBWAL</c>）自动升级测试。
/// 验证 <see cref="Tsdb.Open"/> 能识别 PR #10/#13 留下的旧 WAL 格式并自动升级。
/// </summary>
public sealed class LegacyWalUpgradeTests : IDisposable
{
    private readonly string _tempDir;

    public LegacyWalUpgradeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions() =>
        new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 4 * 1024,
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        };

    /// <summary>
    /// 准备一个旧格式目录（仅含 wal/active.SDBWAL），写入 100 条记录，
    /// 然后通过 Tsdb.Open 升级并验证全部记录可 replay。
    /// </summary>
    [Fact]
    public void Open_WithLegacyActiveWal_AutoUpgradesAndReplays100Records()
    {
        string walDir = TsdbPaths.WalDir(_tempDir);
        Directory.CreateDirectory(walDir);
        Directory.CreateDirectory(TsdbPaths.SegmentsDir(_tempDir));

        const int recordCount = 100;
        var tags = new Dictionary<string, string> { ["host"] = "legacy" };

        const long startLsn = 42;
        // 创建非默认起始 LSN 的旧格式 WAL。
        string legacyPath = Path.Combine(walDir, WalSegmentLayout.LegacyActiveFileName);
        var preCatalog = new SonnetDB.Catalog.SeriesCatalog();
        var entry = preCatalog.GetOrAdd("cpu", tags);

        using (var writer = WalWriter.Open(legacyPath, startLsn: startLsn))
        {
            writer.AppendCreateSeries(entry.Id, "cpu", tags);
            for (int i = 0; i < recordCount; i++)
                writer.AppendWritePoint(entry.Id, 1000L + i, "usage", FieldValue.FromDouble(i));
            writer.Sync();
        }

        // 确认旧文件存在，新格式文件不存在
        Assert.True(File.Exists(legacyPath));

        // Tsdb.Open 应自动升级
        using var db = Tsdb.Open(MakeOptions());

        // 验证：wal/ 目录中无 active.SDBWAL，只有 {startLsn:X16}.SDBWAL
        Assert.False(File.Exists(legacyPath), "Legacy active.SDBWAL should not exist after upgrade");
        Assert.True(File.Exists(WalSegmentLayout.SegmentPath(walDir, startLsn)));

        var walSegs = WalSegmentLayout.Enumerate(walDir);
        Assert.NotEmpty(walSegs);
        Assert.All(walSegs, seg => Assert.True(
            WalSegmentLayout.TryParseStartLsn(Path.GetFileName(seg.Path), out _),
            $"Segment filename should be hex-format: {Path.GetFileName(seg.Path)}"));

        // catalog 和 MemTable 通过 WAL replay 正确恢复
        Assert.Equal(1, db.Catalog.Count);
        Assert.Equal(recordCount, (int)db.MemTable.PointCount);
    }

}
