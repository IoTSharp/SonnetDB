using System.Text;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证 Server 注册表把对象正文写入配置的卷根，并按数据库名隔离。</summary>
public sealed class TsdbRegistryObjectStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-registry-object-volume-{Guid.NewGuid():N}");
    private readonly string _volumeRoot = Path.Combine(
        Path.GetTempPath(),
        $"sndb-object-volume-{Guid.NewGuid():N}");

    /// <summary>验证新建数据库使用卷根下的独立对象目录，并不在数据库目录发布正文。</summary>
    [Fact]
    public async Task TryCreate_UsesDatabaseSpecificObjectVolume()
    {
        using var registry = new TsdbRegistry(
            _root,
            broadcaster: null,
            KvOptions.Default,
            SqlMemoryOptions.Default,
            measurementSchemaPolicy: null,
            objectStorageRootDirectory: _volumeRoot);

        Assert.True(registry.TryCreate("media", out Tsdb database));
        string expectedRoot = Path.Combine(_volumeRoot, "media", "objects");
        Assert.Equal(Path.GetFullPath(expectedRoot), database.ObjectStorageRootDirectory);

        var store = new SndbObjectStore(database);
        store.CreateBucket("videos");
        await store.PutObjectAsync(
            "videos",
            "sample.mp4",
            new MemoryStream(Encoding.UTF8.GetBytes("object payload"), writable: false));

        Assert.True(Directory.EnumerateFiles(expectedRoot, "*.bin", SearchOption.AllDirectories).Any());
        Assert.False(Directory.Exists(Path.Combine(_root, "media", "objects")));
    }

    /// <summary>验证对象卷根目录位于 DataRoot 下时不会被误识别为数据库。</summary>
    [Fact]
    public void LoadExisting_SkipsConfiguredObjectVolumeRoot()
    {
        string objectVolumeRoot = Path.Combine(_root, "object-volume");
        Directory.CreateDirectory(objectVolumeRoot);
        using var registry = new TsdbRegistry(
            _root,
            broadcaster: null,
            KvOptions.Default,
            SqlMemoryOptions.Default,
            measurementSchemaPolicy: null,
            objectStorageRootDirectory: objectVolumeRoot);

        registry.LoadExisting();

        Assert.Empty(registry.ListDatabases());
    }

    /// <summary>清理本测试创建的数据库卷和对象卷。</summary>
    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_volumeRoot);
    }

    /// <summary>仅删除本测试独占的临时目录。</summary>
    private static void TryDelete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
