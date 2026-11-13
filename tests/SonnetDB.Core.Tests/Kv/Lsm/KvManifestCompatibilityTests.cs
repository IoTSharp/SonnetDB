using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

/// <summary>MANIFEST v2 防止无视 generation 标记的历史 LSM 读取器进入新目录。</summary>
public sealed class KvManifestCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-manifest-compat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void NewAndReopenedVersionSets_WriteVersionTwo()
    {
        string path = Path.Combine(_root, "version-set");
        using (KvVersionSet.Create(path))
            Assert.Equal(2, ReadVersion(path));
        using (KvVersionSet.Open(path))
            Assert.Equal(2, ReadVersion(path));
    }

    [Fact]
    public void LegacyVersionOne_ReopensWithAllEntriesAndUpgradesBeforeAnotherWrite()
    {
        string path = Path.Combine(_root, "legacy-manifest");
        long version;
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(1);
        using (KvKeyspace store = KvKeyspace.Open("m", path, Options()))
        {
            store.Put("first", [1, 2, 3], expiry);
            version = store.GetEntry("first")!.Version;
            store.CreateSnapshot();
        }
        RewriteManifestVersion(path, 1);
        using (KvKeyspace opened = KvKeyspace.Open("m", path, Options()))
        {
            Assert.Equal(2, ReadVersion(path));
            KvEntry entry = Assert.IsType<KvEntry>(opened.GetEntry("first"));
            Assert.Equal([1, 2, 3], entry.Value.ToArray());
            Assert.Equal(expiry.UtcTicks, entry.ExpiresAtUtc!.Value.UtcTicks);
            Assert.Equal(version, entry.Version);
            opened.Put("second", [4, 5, 6]);
        }
        using KvKeyspace final = KvKeyspace.Open("m", path, Options());
        Assert.Equal(2, final.Count);
        Assert.Equal([1, 2, 3], final.Get("first"));
        Assert.Equal([4, 5, 6], final.Get("second"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void UnsupportedVersion_IsRejectedBeforeManifestRewrite(int version)
    {
        string path = Path.Combine(_root, "unsupported");
        using (KvVersionSet.Create(path)) { }
        RewriteManifestVersion(path, version);
        string manifest = CurrentManifest(path);
        byte[] before = File.ReadAllBytes(manifest);
        string current = File.ReadAllText(KvLsmPaths.CurrentPath(path));
        Assert.Throws<InvalidDataException>(() => KvVersionSet.Open(path).Dispose());
        Assert.Equal(before, File.ReadAllBytes(manifest));
        Assert.Equal(current, File.ReadAllText(KvLsmPaths.CurrentPath(path)));
        Assert.Single(Directory.GetFiles(path, KvLsmPaths.ManifestPrefix + "*"));
    }

    [Fact]
    public void StandaloneMigration_AndRollback_PreserveVersionsAndUseVersionTwo()
    {
        string path = Path.Combine(_root, "migration");
        long version;
        using (KvKeyspace legacy = KvKeyspace.Open("m", path, Options() with
        {
            StorageEngine = KvStorageEngine.Legacy,
            LegacyMigration = KvLegacyMigrationMode.Disabled,
        }))
        {
            legacy.Put("value", [17, 42]);
            version = legacy.GetEntry("value")!.Version;
        }
        using (KvKeyspace migrated = KvKeyspace.Open("m", path, Options()))
        {
            Assert.Equal(2, ReadVersion(path));
            Assert.Equal(version, migrated.GetEntry("value")!.Version);
        }
        KvLegacyMigration.RollbackStandalone(path);
        Assert.False(File.Exists(KvLsmPaths.CurrentPath(path)));
        using KvKeyspace restored = KvKeyspace.Open("m", path, Options() with
        {
            StorageEngine = KvStorageEngine.Legacy,
            LegacyMigration = KvLegacyMigrationMode.Disabled,
        });
        Assert.Equal(version, restored.GetEntry("value")!.Version);
        Assert.Equal([17, 42], restored.Get("value"));
    }

    [Fact]
    public void SharedStore_AndPinnedVersionBackup_WriteVersionTwo()
    {
        string source = Path.Combine(_root, "source");
        string destination = Path.Combine(_root, "backup");
        string storePath = Path.Combine(source, "store");
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(storePath, Options(), budget);
        long id = store.Create("m");
        using KvKeyspace keyspace = KvKeyspace.OpenShared("m", Path.Combine(source, "sidecar"), store, id, Options(), budget);
        keyspace.Put("first", [17, 42]);
        store.Engine.Flush();
        Assert.Equal(2, ReadVersion(storePath));
        using (KvLsmBackupSet snapshot = KvLsmEngine.AcquireBackupsUnder(source))
            snapshot.CopyTo(source, destination, copied: null);
        string restoredPath = Path.Combine(destination, "store");
        Assert.Equal(2, ReadVersion(restoredPath));
        using KvLsmStore restored = KvLsmStore.Open(restoredPath, Options(), budget);
        using KvKeyspace restoredKeys = KvKeyspace.OpenShared("m", Path.Combine(destination, "sidecar"), restored, id, Options(), budget);
        Assert.Equal([17, 42], restoredKeys.Get("first"));
        Assert.Single(restored.List());
    }

    private static int ReadVersion(string root)
    {
        using var stream = new FileStream(CurrentManifest(root), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[KvManifestFile.HeaderSize];
        stream.ReadExactly(header);
        return BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
    }

    private static string CurrentManifest(string root)
        => Path.Combine(root, File.ReadAllText(KvLsmPaths.CurrentPath(root)).Trim());

    private static void RewriteManifestVersion(string root, int version)
    {
        string path = CurrentManifest(root);
        byte[] bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), Crc32.HashToUInt32(bytes.AsSpan(0, 12)));
        File.WriteAllBytes(path, bytes);
    }

    private static KvOptions Options() => KvOptions.Default with
    {
        StorageEngine = KvStorageEngine.Lsm,
        LegacyMigration = KvLegacyMigrationMode.Auto,
        SyncWalOnEveryWrite = true,
        AutoCheckpointEnabled = false,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };
}
