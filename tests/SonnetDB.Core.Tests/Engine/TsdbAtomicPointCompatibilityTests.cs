using System.Buffers.Binary;
using SonnetDB.Backup;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

public sealed class TsdbAtomicPointCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-atomic-compat-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LegacyDirectory_UpgradeFlushBackupRestore_PreservesValuesAndPermanentDowngradeBarrier()
    {
        string source = Path.Combine(_root, "source");
        string backup = Path.Combine(_root, "backup");
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(TsdbPaths.WalDir(source));
        var legacyCatalog = new SeriesCatalog();
        SeriesEntry series = legacyCatalog.GetOrAdd("compat", new Dictionary<string, string> { ["host"] = "legacy" });
        CatalogFileCodec.Save(legacyCatalog, TsdbPaths.CatalogPath(source));
        string legacyPath = WalSegmentLayout.SegmentPath(TsdbPaths.WalDir(source), 1);
        using (WalWriter writer = WalWriter.Open(legacyPath))
        {
            writer.AppendCreateSeries(series.Id, "compat", series.Tags);
            writer.AppendWritePoint(series.Id, 1000, "number", FieldValue.FromLong(10));
            writer.AppendWritePoint(series.Id, 1000, "text", FieldValue.FromString("legacy-point"));
            writer.Sync();
        }
        byte[] historicalWal = File.ReadAllBytes(legacyPath);
        using (Tsdb db = Tsdb.Open(Options(source)))
        {
            Assert.Equal(historicalWal, File.ReadAllBytes(legacyPath));
            db.Write(Point.Create("compat", 2000, series.Tags, new Dictionary<string, FieldValue>
            {
                ["number"] = FieldValue.FromLong(20),
                ["text"] = FieldValue.FromString("new-point"),
            }));
            db.FlushNow();
            AssertCatalogV2(source);
            BackupManifest manifest = new BackupService().Create(db, new BackupCreateOptions { DestinationDirectory = backup });
            Assert.Contains(manifest.Files, entry => entry.Path == TsdbPaths.CatalogFileName && entry.Required);
            AssertCatalogV2(backup);
        }

        string offline = Path.Combine(_root, "source-offline");
        Directory.Move(source, offline);
        var service = new BackupService();
        Assert.True(service.Verify(backup).IsValid);
        service.Restore(new BackupRestoreOptions { BackupDirectory = backup, TargetDirectory = target });
        AssertCatalogV2(target);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using Tsdb restored = Tsdb.Open(Options(target));
            SeriesEntry entry = Assert.Single(restored.Catalog.Find("compat", series.Tags));
            Assert.Equal(new long[] { 10, 20 }, restored.Query.Execute(new PointQuery(entry.Id, "number", TimeRange.All))
                .Select(static value => value.Value.AsLong()));
            Assert.Equal(new[] { "legacy-point", "new-point" }, restored.Query.Execute(new PointQuery(entry.Id, "text", TimeRange.All))
                .Select(static value => value.Value.AsString()));
            AssertCatalogV2(target);
        }
    }

    private static void AssertCatalogV2(string root)
        => Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(TsdbPaths.CatalogPath(root)).AsSpan(8, 4)));

    private static TsdbOptions Options(string root) => new()
    {
        RootDirectory = root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = true },
        WalRolling = new WalRollingPolicy { Enabled = true, MaxRecordsPerSegment = 3, MaxBytesPerSegment = 1024 * 1024 },
    };

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(full));
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
