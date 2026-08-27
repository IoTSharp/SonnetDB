using SonnetDB.Engine;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Wal;

/// <summary>
/// <see cref="TombstoneManifestCodec"/> 的单元测试。
/// </summary>
public sealed class TombstoneManifestCodecTests : IDisposable
{
    private readonly string _tempDir;

    public TombstoneManifestCodecTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string ManifestPath() =>
        Path.Combine(_tempDir, TombstoneManifestCodec.FileName);

    private static Tombstone MakeTombstone(ulong seriesId, string field, long from, long to, long lsn = 1) =>
        new Tombstone(seriesId, field, from, to, lsn);

    [Fact]
    public void SaveLoad_RoundTrip_SetIsEqual()
    {
        var tombstones = new List<Tombstone>
        {
            MakeTombstone(1UL, "temperature", 1000L, 2000L, 10L),
            MakeTombstone(2UL, "pressure", 5000L, 6000L, 20L),
            MakeTombstone(3UL, "humidity", 0L, 9999L, 30L),
        };

        string path = ManifestPath();
        TombstoneManifestCodec.Save(path, tombstones);

        var loaded = TombstoneManifestCodec.Load(path);

        Assert.Equal(tombstones.Count, loaded.Count);
        foreach (var t in tombstones)
            Assert.Contains(t, loaded);
    }

    [Fact]
    public void SaveLoad_EmptyCollection_RoundTrip()
    {
        string path = ManifestPath();
        TombstoneManifestCodec.Save(path, []);

        var loaded = TombstoneManifestCodec.Load(path);

        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_FileNotExists_ReturnsEmpty()
    {
        string path = Path.Combine(_tempDir, "nonexistent.tslmanifest");

        var loaded = TombstoneManifestCodec.Load(path);

        Assert.Empty(loaded);
    }

    [Fact]
    public void SaveLoad_ChineseFieldName_RoundTrip()
    {
        var tombstones = new List<Tombstone>
        {
            MakeTombstone(1UL, "温度字段", 1000L, 2000L, 42L),
        };

        string path = ManifestPath();
        TombstoneManifestCodec.Save(path, tombstones);

        var loaded = TombstoneManifestCodec.Load(path);

        Assert.Single(loaded);
        Assert.Equal("温度字段", loaded[0].FieldName);
    }

    [Fact]
    public void Load_TamperedMagic_ThrowsInvalidDataException()
    {
        string path = ManifestPath();
        TombstoneManifestCodec.Save(path, [MakeTombstone(1UL, "f", 1, 10, 1)]);

        // Tamper the header magic (first 8 bytes)
        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] = 0xFF;
        bytes[1] = 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => TombstoneManifestCodec.Load(path));
    }

    [Fact]
    public void Load_TamperedCrc_ThrowsInvalidDataException()
    {
        string path = ManifestPath();
        TombstoneManifestCodec.Save(path, [MakeTombstone(1UL, "f", 1, 10, 1)]);

        // Tamper a byte in the tombstone data area (byte 32 = start of first tombstone)
        byte[] bytes = File.ReadAllBytes(path);
        bytes[32] ^= 0xFF; // Flip some bits in the first tombstone
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => TombstoneManifestCodec.Load(path));
    }

    [Fact]
    public void Save_CreatesTempFileThenRenames_FinalFileExists()
    {
        string path = ManifestPath();
        string tmpPath = path + ".tmp";

        // No temp file before
        Assert.False(File.Exists(tmpPath));

        TombstoneManifestCodec.Save(path, [MakeTombstone(1UL, "f", 1, 10, 1)]);

        // Final file exists
        Assert.True(File.Exists(path));
        // Temp file cleaned up
        Assert.False(File.Exists(tmpPath));
    }

    [Fact]
    public void SaveLoad_MultipleRoundsOfOverwrite_CorrectData()
    {
        string path = ManifestPath();

        var initial = new List<Tombstone>
        {
            MakeTombstone(1UL, "f1", 100L, 200L, 1L),
        };
        TombstoneManifestCodec.Save(path, initial);

        var updated = new List<Tombstone>
        {
            MakeTombstone(2UL, "f2", 300L, 400L, 2L),
            MakeTombstone(3UL, "f3", 500L, 600L, 3L),
        };
        TombstoneManifestCodec.Save(path, updated);

        var loaded = TombstoneManifestCodec.Load(path);

        Assert.Equal(2, loaded.Count);
        Assert.DoesNotContain(initial[0], loaded);
        Assert.Contains(updated[0], loaded);
        Assert.Contains(updated[1], loaded);
    }

    /// <summary>主清单损坏时应读取上一代已校验备份。</summary>
    [Fact]
    public void LoadWithFallback_PrimaryCorrupted_LoadsLastVerifiedGeneration()
    {
        string path = ManifestPath();
        var initial = new[] { MakeTombstone(1UL, "initial", 100L, 200L, 10L) };
        var updated = new[] { MakeTombstone(2UL, "updated", 300L, 400L, 20L) };
        TombstoneManifestCodec.Save(path, initial);
        TombstoneManifestCodec.Save(path, updated);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var recovered = TombstoneManifestCodec.LoadWithFallback(path);

        Assert.Equal(initial, recovered);
    }

    /// <summary>主清单已损坏时，后续保存不能用损坏内容覆盖有效备份。</summary>
    [Fact]
    public void Save_CorruptedPrimary_DoesNotOverwriteVerifiedBackup()
    {
        string path = ManifestPath();
        var initial = new[] { MakeTombstone(1UL, "initial", 100L, 200L, 10L) };
        var updated = new[] { MakeTombstone(2UL, "updated", 300L, 400L, 20L) };
        TombstoneManifestCodec.Save(path, initial);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        TombstoneManifestCodec.Save(path, updated);

        Assert.Equal(updated, TombstoneManifestCodec.Load(path));
        Assert.Equal(initial, TombstoneManifestCodec.Load(path + TombstoneManifestCodec.BackupSuffix));
    }

    /// <summary>并发保存应串行完成，且不遗留唯一临时文件。</summary>
    [Fact]
    public async Task Save_ConcurrentCalls_LeavesValidManifestWithoutTemporaryFiles()
    {
        string path = ManifestPath();
        var generations = Enumerable.Range(1, 32)
            .Select(index => new[] { MakeTombstone((ulong)index, $"field-{index}", index, index + 1, index) })
            .ToArray();

        await Task.WhenAll(generations.Select(generation => Task.Run(() =>
            TombstoneManifestCodec.Save(path, generation))));

        var loaded = TombstoneManifestCodec.Load(path);
        Assert.Contains(generations, generation => generation.SequenceEqual(loaded));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, TombstoneManifestCodec.FileName + ".tmp.*"));
    }
}
