using System.Buffers.Binary;
using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Catalog;

/// <summary>
/// <see cref="CatalogFileCodec"/> 单元测试。
/// </summary>
public sealed class CatalogFileCodecTests : IDisposable
{
    private readonly string _tmpDir;

    public CatalogFileCodecTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"SDBCAT_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string GetPath(string name) => Path.Combine(_tmpDir, name);

    private static SeriesCatalog BuildCatalog(int count)
    {
        var catalog = new SeriesCatalog();
        for (int i = 0; i < count; i++)
        {
            catalog.GetOrAdd($"measurement_{i % 5}",
                new Dictionary<string, string>
                {
                    ["id"] = i.ToString(),
                    ["region"] = (i % 3 == 0) ? "us" : "eu",
                });
        }

        return catalog;
    }

    // ── 空 catalog round-trip ─────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_EmptyCatalog_CountIsZero_Path()
    {
        var path = GetPath("empty.SDBCAT");
        var original = new SeriesCatalog();
        CatalogFileCodec.Save(original, path);

        var loaded = CatalogFileCodec.Load(path);
        Assert.Equal(0, loaded.Count);
    }

    [Fact]
    public void RoundTrip_EmptyCatalog_CountIsZero_Stream()
    {
        using var ms = new MemoryStream();
        CatalogFileCodec.Save(new SeriesCatalog(), ms);

        ms.Position = 0;
        var loaded = CatalogFileCodec.Load(ms);
        Assert.Equal(0, loaded.Count);
    }

    // ── N=100 series round-trip ───────────────────────────────────────────────

    [Fact]
    public void RoundTrip_100Series_AllFieldsMatch_Path()
    {
        var path = GetPath("catalog100.SDBCAT");
        var original = BuildCatalog(100);
        CatalogFileCodec.Save(original, path);

        var loaded = CatalogFileCodec.Load(path);
        Assert.Equal(original.Count, loaded.Count);

        foreach (var entry in original.Snapshot())
        {
            var found = loaded.TryGet(entry.Id);
            Assert.NotNull(found);
            Assert.Equal(entry.Key.Canonical, found!.Key.Canonical);
            Assert.Equal(entry.Measurement, found.Measurement);
            Assert.Equal(entry.Tags.Count, found.Tags.Count);
            foreach (var (k, v) in entry.Tags)
                Assert.Equal(v, found.Tags[k]);
        }
    }

    [Fact]
    public void RoundTrip_100Series_AllFieldsMatch_Stream()
    {
        var original = BuildCatalog(100);
        using var ms = new MemoryStream();
        CatalogFileCodec.Save(original, ms);

        ms.Position = 0;
        var loaded = CatalogFileCodec.Load(ms);
        Assert.Equal(original.Count, loaded.Count);
    }

    // ── 文件不存在 ────────────────────────────────────────────────────────────

    [Fact]
    public void Load_FileNotExist_ReturnsEmptyCatalog()
    {
        var catalog = CatalogFileCodec.Load(GetPath("nonexistent.SDBCAT"));
        Assert.Equal(0, catalog.Count);
    }

    // ── 文件损坏校验 ──────────────────────────────────────────────────────────

    [Fact]
    public void Load_WrongMagic_ThrowsInvalidDataException()
    {
        var path = GetPath("badmagic.SDBCAT");
        CatalogFileCodec.Save(new SeriesCatalog(), path);

        // 篡改第一个字节（magic 字段）
        byte[] data = File.ReadAllBytes(path);
        data[0] = 0xFF;
        File.WriteAllBytes(path, data);

        Assert.Throws<InvalidDataException>(() => CatalogFileCodec.Load(path));
    }

    [Fact]
    public void Load_WrongFormatVersion_ThrowsInvalidDataException()
    {
        var path = GetPath("badversion.SDBCAT");
        CatalogFileCodec.Save(new SeriesCatalog(), path);

        // FormatVersion 在偏移 8（magic=8B）
        byte[] data = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), 99);
        File.WriteAllBytes(path, data);

        Assert.Throws<InvalidDataException>(() => CatalogFileCodec.Load(path));
    }

    [Fact]
    public void Load_TamperedSeriesIdValue_ThrowsInvalidDataException()
    {
        var path = GetPath("tamperedid.SDBCAT");
        var catalog = new SeriesCatalog();
        catalog.GetOrAdd("cpu", new Dictionary<string, string> { ["host"] = "srv" });
        CatalogFileCodec.Save(catalog, path);

        // 第一条 entry 的 SeriesIdValue 从偏移 64 开始（header=64B），占 8 字节
        byte[] data = File.ReadAllBytes(path);
        // 将 SeriesIdValue 改为全 0xAA
        for (int i = FormatSizes.CatalogFileHeaderSize; i < FormatSizes.CatalogFileHeaderSize + 8; i++)
            data[i] = 0xAA;
        File.WriteAllBytes(path, data);

        Assert.Throws<InvalidDataException>(() => CatalogFileCodec.Load(path));
    }

    // ── Stream 重载与 path 重载等价 ───────────────────────────────────────────

    [Fact]
    public void StreamAndPath_ProduceEquivalentResults()
    {
        var catalog = BuildCatalog(10);
        var path = GetPath("equiv.SDBCAT");

        // 保存到 path
        CatalogFileCodec.Save(catalog, path);
        var loadedFromPath = CatalogFileCodec.Load(path);

        // 保存到 stream
        using var ms = new MemoryStream();
        CatalogFileCodec.Save(catalog, ms);
        ms.Position = 0;
        var loadedFromStream = CatalogFileCodec.Load(ms);

        Assert.Equal(loadedFromPath.Count, loadedFromStream.Count);
        foreach (var entry in loadedFromPath.Snapshot())
            Assert.NotNull(loadedFromStream.TryGet(entry.Id));
    }

    // ── 原子替换（临时文件不残留）────────────────────────────────────────────

    [Fact]
    public void Save_AtomicReplace_TempFileNotLeft()
    {
        var path = GetPath("atomic.SDBCAT");
        var tmpPath = path + ".tmp";

        var catalog = BuildCatalog(5);
        CatalogFileCodec.Save(catalog, path);

        // 目标文件存在，临时文件不残留
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(tmpPath));
    }

    [Fact]
    public void Save_Overwrite_ReplacesOldContent()
    {
        var path = GetPath("overwrite.SDBCAT");

        // 首次保存：100 条
        CatalogFileCodec.Save(BuildCatalog(100), path);

        // 再次保存：5 条（覆盖）
        CatalogFileCodec.Save(BuildCatalog(5), path);

        var loaded = CatalogFileCodec.Load(path);
        Assert.Equal(5, loaded.Count);
    }

    [Fact]
    public void AtomicPointUpgrade_PreservesSeriesAndCannotBeDowngradedByLaterCheckpoints()
    {
        string path = GetPath("atomic-points.SDBCAT");
        var original = BuildCatalog(5);
        CatalogFileCodec.Save(original, path);
        byte[] legacy = File.ReadAllBytes(path);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(legacy.AsSpan(8, 4)));

        CatalogFileCodec.RequireAtomicPointWal(original, path);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8, 4)));
        SeriesCatalog loaded = CatalogFileCodec.Load(path);
        foreach (SeriesEntry expected in original.Snapshot())
        {
            SeriesEntry actual = Assert.IsType<SeriesEntry>(loaded.TryGet(expected.Id));
            Assert.Equal(expected.Key.Canonical, actual.Key.Canonical);
            Assert.Equal(expected.CreatedAtUtcTicks, actual.CreatedAtUtcTicks);
        }

        loaded.GetOrAdd("after-upgrade", new Dictionary<string, string> { ["host"] = "new" });
        CatalogFileCodec.Save(loaded, path);
        using var copy = new MemoryStream();
        CatalogFileCodec.Save(CatalogFileCodec.Load(path), copy);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(copy.ToArray().AsSpan(8, 4)));
        Assert.Equal(6, CatalogFileCodec.Load(path).Count);
    }

    [Fact]
    public void AtomicPointUpgrade_FailedTemporaryWriteDoesNotPublishAPartialCatalog()
    {
        string path = GetPath("blocked.SDBCAT");
        var original = BuildCatalog(2);
        CatalogFileCodec.Save(original, path);
        byte[] legacy = File.ReadAllBytes(path);
        using (var lease = new FileStream(path + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => CatalogFileCodec.RequireAtomicPointWal(original, path));

        Assert.Equal(legacy, File.ReadAllBytes(path));
        Assert.Equal(2, CatalogFileCodec.Load(path).Count);
        // The failed instance stays at the safer version: a subsequent checkpoint cannot erase the intent.
        CatalogFileCodec.Save(original, path);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8, 4)));
    }
}
