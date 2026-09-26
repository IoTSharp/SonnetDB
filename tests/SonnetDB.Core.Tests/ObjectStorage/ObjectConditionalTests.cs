using System.Reflection;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Core.Tests.ObjectStorage;

public sealed class ObjectConditionalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-object-conditional-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PutObjectAsync_LegacySevenParameterSignature_RemainsAvailable()
    {
        MethodInfo method = typeof(SndbObjectStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(static candidate => candidate.Name == nameof(SndbObjectStore.PutObjectAsync)
                && candidate.GetParameters().Length == 7);

        Assert.Equal(typeof(Task<SndbObjectInfo>), method.ReturnType);
        Assert.Equal(
            new[]
            {
                typeof(string),
                typeof(string),
                typeof(Stream),
                typeof(string),
                typeof(IReadOnlyDictionary<string, string>),
                typeof(IReadOnlyDictionary<string, string>),
                typeof(CancellationToken),
            },
            method.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public async Task PutObjectConditionalAsync_IfMatchAndIfNoneMatch_EnforcesCurrentVersion()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        var initial = await store.PutObjectAsync("media", "state.txt", new MemoryStream(Encoding.UTF8.GetBytes("one")));

        await Assert.ThrowsAsync<SndbObjectStorageException>(() => store.PutObjectConditionalAsync(
            "media", "state.txt", new MemoryStream([2]), new SndbObjectWriteCondition(IfNoneMatch: true)));
        await Assert.ThrowsAsync<SndbObjectStorageException>(() => store.PutObjectConditionalAsync(
            "media", "state.txt", new MemoryStream([2]), new SndbObjectWriteCondition("\"wrong\"")));

        var updated = await store.PutObjectConditionalAsync(
            "media", "state.txt", new MemoryStream(Encoding.UTF8.GetBytes("two")), new SndbObjectWriteCondition(initial.ETag));
        Assert.NotEqual(initial.VersionId, updated.VersionId);
    }

    [Fact]
    public async Task PutObjectConditionalAsync_IfMatchListsUseStrongComparisonRules()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        var initial = await store.PutObjectAsync("media", "state.txt", new MemoryStream([1]));

        var updated = await store.PutObjectConditionalAsync(
            "media",
            "state.txt",
            new MemoryStream([2]),
            new SndbObjectWriteCondition("\"other\", " + initial.ETag));
        Assert.NotEqual(initial.VersionId, updated.VersionId);

        await Assert.ThrowsAsync<SndbObjectStorageException>(() => store.PutObjectConditionalAsync(
            "media",
            "state.txt",
            new MemoryStream([3]),
            new SndbObjectWriteCondition("W/" + updated.ETag + ", \"stale\"")));
    }

    [Fact]
    public async Task PutObjectConditionalAsync_IfNoneMatchEtagList_UsesWeakComparisonAfterIfMatch()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        var initial = await store.PutObjectAsync("media", "state.txt", new MemoryStream([1]));

        await Assert.ThrowsAsync<SndbObjectStorageException>(() => store.PutObjectConditionalAsync(
            "media",
            "state.txt",
            new MemoryStream([2]),
            new SndbObjectWriteCondition { IfNoneMatchEtags = "\"other\", W/" + initial.ETag }));

        var updated = await store.PutObjectConditionalAsync(
            "media",
            "state.txt",
            new MemoryStream([3]),
            new SndbObjectWriteCondition(initial.ETag) { IfNoneMatchEtags = "\"stale\"" });

        Assert.NotEqual(initial.VersionId, updated.VersionId);
    }

    [Fact]
    public async Task PutObjectConditionalAsync_FailedFastPreflight_DoesNotReadContent()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        await store.PutObjectAsync("media", "state.txt", new MemoryStream([1]));
        using var content = new MemoryStream([2, 3, 4]);

        await Assert.ThrowsAsync<SndbObjectStorageException>(() => store.PutObjectConditionalAsync(
            "media", "state.txt", content, new SndbObjectWriteCondition(IfNoneMatch: true)));

        Assert.Equal(0, content.Position);
    }

    [Fact]
    public async Task PutObjectConditionalAsync_IfMatchAndWildcardIfNoneMatch_RejectsBeforeReadingContent()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        await store.PutObjectAsync("media", "state.txt", new MemoryStream([1]));
        using var content = new MemoryStream([2, 3, 4]);

        await Assert.ThrowsAsync<SndbObjectStorageException>(() => store.PutObjectConditionalAsync(
            "media", "state.txt", content, new SndbObjectWriteCondition("*", IfNoneMatch: true)));

        Assert.Equal(0, content.Position);
    }

    [Fact]
    public async Task PutObjectConditionalAsync_WildcardAndEtagList_RejectsBeforeReadingContent()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        using var content = new MemoryStream([2, 3, 4]);

        await Assert.ThrowsAsync<ArgumentException>(() => store.PutObjectConditionalAsync(
            "media", "state.txt", content, new SndbObjectWriteCondition(IfNoneMatch: true) { IfNoneMatchEtags = "\"etag\"" }));

        Assert.Equal(0, content.Position);
    }

    [Fact]
    public async Task OpenReadConditional_IfNoneMatch_ReturnsNotModifiedWithoutStream()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        var info = await store.PutObjectAsync("media", "state.txt", new MemoryStream([1, 2, 3]));

        var result = store.OpenReadConditional("media", "state.txt", new SndbObjectReadCondition(IfNoneMatch: info.ETag));

        Assert.Equal(SndbObjectConditionalReadStatus.NotModified, result.Status);
        Assert.Null(result.Read);
        Assert.Equal(info.ETag, result.Info?.ETag);
        Assert.Equal(info.UpdatedUtc, result.Info?.UpdatedUtc);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("\"expected\"")]
    public void OpenReadConditional_MissingObjectWithIfMatch_ReturnsPreconditionFailed(string ifMatch)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");

        var result = store.OpenReadConditional(
            "media",
            "missing.txt",
            new SndbObjectReadCondition(IfMatch: ifMatch));

        Assert.Equal(SndbObjectConditionalReadStatus.PreconditionFailed, result.Status);
        Assert.Null(result.Read);
        Assert.Null(result.Info);
    }

    [Fact]
    public void OpenReadConditional_MissingObjectWithoutIfMatch_ReturnsNotFound()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");

        var result = store.OpenReadConditional("media", "missing.txt", new SndbObjectReadCondition());

        Assert.Equal(SndbObjectConditionalReadStatus.NotFound, result.Status);
        Assert.Null(result.Read);
        Assert.Null(result.Info);
    }

    [Fact]
    public async Task OpenReadConditional_IfModifiedSince_UsesHttpSecondPrecision()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        var info = await store.PutObjectAsync("media", "time.txt", new MemoryStream([1]));
        DateTime utc = info.UpdatedUtc.UtcDateTime;
        var rounded = new DateTimeOffset(utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);

        var result = store.OpenReadConditional(
            "media",
            "time.txt",
            new SndbObjectReadCondition(IfModifiedSince: rounded));

        Assert.Equal(SndbObjectConditionalReadStatus.NotModified, result.Status);
        Assert.Null(result.Read);

        var subsecond = store.OpenReadConditional(
            "media",
            "time.txt",
            new SndbObjectReadCondition(IfModifiedSince: rounded.AddTicks(TimeSpan.TicksPerSecond - 1)));
        Assert.Equal(SndbObjectConditionalReadStatus.NotModified, subsecond.Status);
        Assert.Null(subsecond.Read);
    }

    [Fact]
    public async Task OpenReadConditional_IfUnmodifiedSince_UsesHttpSecondPrecision()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        var info = await store.PutObjectAsync("media", "time.txt", new MemoryStream([1]));
        DateTime utc = info.UpdatedUtc.UtcDateTime;
        var lastModified = new DateTimeOffset(
            utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond)),
            TimeSpan.Zero);

        var result = store.OpenReadConditional(
            "media",
            "time.txt",
            new SndbObjectReadCondition(IfUnmodifiedSince: lastModified));

        Assert.Equal(SndbObjectConditionalReadStatus.Success, result.Status);
        Assert.NotNull(result.Read);
        result.Read!.Content.Dispose();
    }

    [Fact]
    public async Task OpenReadConditional_WithEtagConditions_IgnoresSubordinateDateConditions()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        var info = await store.PutObjectAsync("media", "state.txt", new MemoryStream([1]));

        var ifMatch = store.OpenReadConditional(
            "media",
            "state.txt",
            new SndbObjectReadCondition(
                IfMatch: info.ETag,
                IfUnmodifiedSince: info.UpdatedUtc.AddTicks(-1)));
        Assert.Equal(SndbObjectConditionalReadStatus.Success, ifMatch.Status);
        Assert.NotNull(ifMatch.Read);
        using (ifMatch.Read!.Content) { }

        var ifNoneMatch = store.OpenReadConditional(
            "media",
            "state.txt",
            new SndbObjectReadCondition(
                IfNoneMatch: "\"different\"",
                IfModifiedSince: info.UpdatedUtc.AddDays(1)));
        Assert.Equal(SndbObjectConditionalReadStatus.Success, ifNoneMatch.Status);
        Assert.NotNull(ifNoneMatch.Read);
        using (ifNoneMatch.Read!.Content) { }
    }

    [Fact]
    public async Task OpenReadConditional_EtagListsAndWeakTags_UseHttpComparisonRules()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        var info = await store.PutObjectAsync("media", "state.txt", new MemoryStream([1]));

        var ifMatchList = store.OpenReadConditional(
            "media", "state.txt", new SndbObjectReadCondition(IfMatch: "\"other\", " + info.ETag));
        Assert.Equal(SndbObjectConditionalReadStatus.Success, ifMatchList.Status);
        using (ifMatchList.Read!.Content) { }

        var weakIfMatch = store.OpenReadConditional(
            "media", "state.txt", new SndbObjectReadCondition(IfMatch: "W/" + info.ETag));
        Assert.Equal(SndbObjectConditionalReadStatus.PreconditionFailed, weakIfMatch.Status);

        var weakIfNoneMatch = store.OpenReadConditional(
            "media", "state.txt", new SndbObjectReadCondition(IfNoneMatch: "\"other\", W/" + info.ETag));
        Assert.Equal(SndbObjectConditionalReadStatus.NotModified, weakIfNoneMatch.Status);
    }

    [Fact]
    public void EvaluateReadCondition_EtagListsPreserveCommasInsideQuotedTags()
    {
        var info = new SndbObjectInfo(
            "media",
            "state.txt",
            "v1",
            "text/plain",
            1,
            "\"part,one\"",
            "sha256",
            IsDeleteMarker: false,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var ifMatch = SndbObjectStore.EvaluateReadCondition(
            info,
            new SndbObjectReadCondition(IfMatch: "\"other\", \"part,one\""));
        var ifNoneMatch = SndbObjectStore.EvaluateReadCondition(
            info,
            new SndbObjectReadCondition(IfNoneMatch: "\"other\", \"part,one\""));

        Assert.Equal(SndbObjectConditionalReadStatus.Success, ifMatch);
        Assert.Equal(SndbObjectConditionalReadStatus.NotModified, ifNoneMatch);
    }

    [Fact]
    public async Task ListObjectsCursorAsync_MultiplePages_ReturnsOrdinalObjects()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("media");
        foreach (string key in new[] { "items/c", "items/a", "items/b" })
            await store.PutObjectAsync("media", key, new MemoryStream([1]));

        var keys = new List<string>();
        await foreach (var item in store.ListObjectsCursorAsync("media", "items/", pageSize: 1))
            keys.Add(item.Key);

        Assert.Equal(new[] { "items/a", "items/b", "items/c" }, keys);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
