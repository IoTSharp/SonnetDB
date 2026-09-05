using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;
using Xunit.Abstractions;

namespace SonnetDB.Core.Tests.ObjectStorage;

public sealed class ObjectPaginationTests(ITestOutputHelper output) : IDisposable
{
    private const string Bucket = "page-bucket";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SonnetDB.ObjectPages." + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(90));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListObjects_OrdinalAndVersions_MatchesFullScanOracle(bool checkpoint)
    {
        using var db = Open();
        var store = new SndbObjectStore(db);
        store.CreateBucket(Bucket);
        string[] keys = ["a", "a/", "a/0", "a/子/叶", "a0", "%2F", "+", "?", "Z", "z", "é", "e\u0301", "中", "\U00010000", "\ue000", "\uffff", "\uffff/a", "/leading", "x::a", "x::b", "x:y"];
        foreach (string key in keys.Reverse())
        {
            _deadline.Token.ThrowIfCancellationRequested();
            await store.PutObjectAsync(Bucket, key, Stream.Null, cancellationToken: _deadline.Token);
        }
        await store.PutObjectAsync(Bucket, "a", new MemoryStream([1, 2]), cancellationToken: _deadline.Token);
        store.DeleteObject(Bucket, "Z");
        store.DeleteObject(Bucket, "gone/only");
        if (checkpoint)
            db.Keyspaces.Open("__object_storage").CreateSnapshot();

        foreach (string prefix in new[] { "", "a", "a/", "x", "\uffff", "missing", "/a" })
        foreach (string? delimiter in new string?[] { null, "/", "::", "\uffff" })
        foreach (int size in new[] { 1, 2, 7, int.MaxValue })
        {
            _deadline.Token.ThrowIfCancellationRequested();
            VerifyPages(store, Oracle(db, prefix, delimiter), prefix, delimiter, size);
        }
        Assert.Equal(24, store.ListObjectVersions(Bucket).Versions.Count);
    }

    [Theory]
    [InlineData(false, 64)]
    [InlineData(false, 8192)]
    [InlineData(true, 8192)]
    public void ListObjects_LargeBucketAndGroupedDirectory_VisitsOnlyPageCandidates(bool checkpoint, int count)
    {
        using var db = Open();
        var store = new SndbObjectStore(db);
        store.CreateBucket(Bucket);
        SeedLegacy(db, Enumerable.Range(0, count).Select(i => $"a/{i:D6}").Concat(["b", "c", "d"]));
        var first = store.ListObjects(Bucket, null, 1);
        Assert.True(first.IsTruncated);
        var metadata = db.Keyspaces.Open("__object_storage");
        if (checkpoint)
            metadata.CreateSnapshot();
        int candidates = 0;
        int diskVisits = 0;
        store.ListCandidateTestHook = () => candidates++;
        store.ListRebuildEntryTestHook = () => throw new InvalidOperationException("A ready index must not rebuild per page.");
        if (checkpoint)
            metadata.ConfigureSnapshotDiskScanTestHooks(null, _ => diskVisits++);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        var grouped = store.ListObjects(Bucket, "", 2, null, "/", _deadline.Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(["a/"], grouped.CommonPrefixes);
        Assert.Equal("b", Assert.Single(grouped.Objects).Key);
        Assert.True(grouped.IsTruncated);
        Assert.Equal(3, candidates);
        Assert.InRange(allocated, 1, 160_000);
        Assert.InRange(diskVisits, 0, 12);
        output.WriteLine($"keys={count + 3}, checkpoint={checkpoint}, grouped candidates={candidates}, disk visits={diskVisits}, allocated={allocated}, elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");

        candidates = 0;
        VerifyPages(store, ["o:c", "o:d"], "", "/", 2, grouped.NextContinuationToken);
        Assert.Equal(2, candidates);
        candidates = 0;
        var continuation = store.ListObjects(Bucket, null, 3, first.NextContinuationToken, null, _deadline.Token);
        Assert.Equal(["a/000001", "a/000002", "a/000003"], continuation.Objects.Select(x => x.Key));
        Assert.Equal(4, candidates);
        candidates = 0;
        var tail = store.ListObjects(Bucket, "a/", 3, LegacyToken($"a/{count - 3:D6}"), null, _deadline.Token);
        Assert.Equal(2, tail.Objects.Count);
        Assert.False(tail.IsTruncated);
        Assert.Equal(2, candidates);
    }

    [Fact]
    public void ListObjects_MissingIndexAndInterruptedRebuild_ReopensWithoutPartialPublication()
    {
        using (var db = Open())
        {
            var store = new SndbObjectStore(db);
            store.CreateBucket(Bucket);
            SeedLegacy(db, Enumerable.Range(0, 600).Select(i => $"key-{i:D4}"));
            Assert.Equal(600, store.ListObjects(Bucket).Objects.Count);
            var metadata = db.Keyspaces.Open("__object_storage");
            var derived = metadata.ScanPrefix("object-list-v1:" + Bucket + ":", 1);
            Assert.True(metadata.Delete(Assert.Single(derived).Key.Span));
            Assert.True(metadata.Delete("object-list-ready-v1:" + Bucket));
            using var cancel = new CancellationTokenSource();
            int visited = 0;
            store.ListRebuildEntryTestHook = () => { if (++visited == 260) cancel.Cancel(); };
            Assert.Throws<OperationCanceledException>(() => store.ListObjects(Bucket, null, 5, null, null, cancel.Token));
            Assert.Null(metadata.Get("object-list-ready-v1:" + Bucket));
            Assert.Equal(260, visited);
        }
        using (var db = Open())
        {
            var store = new SndbObjectStore(db);
            VerifyPages(store, Oracle(db, "", null), "", null, 73);
            Assert.Equal("1", Encoding.UTF8.GetString(db.Keyspaces.Open("__object_storage").Get("object-list-ready-v1:" + Bucket)!));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListObjects_ReopenAndSharedFacades_KeepLatestIndexAtomic(bool checkpoint)
    {
        string? continuation;
        string version;
        using (var db = Open())
        {
            var store = new SndbObjectStore(db);
            store.CreateBucket(Bucket);
            foreach (string key in new[] { "a", "b", "c", "d" })
                await store.PutObjectAsync(Bucket, key, Stream.Null, cancellationToken: _deadline.Token);
            continuation = store.ListObjects(Bucket, null, 1).NextContinuationToken;
            var second = new SndbObjectStore(db);
            second.DeleteObject(Bucket, "b");
            version = (await second.PutObjectAsync(Bucket, "c", new MemoryStream([3]), cancellationToken: _deadline.Token)).VersionId;
            Assert.Equal(["a", "c", "d"], store.ListObjects(Bucket).Objects.Select(x => x.Key));
            if (checkpoint)
                db.Keyspaces.Open("__object_storage").CreateSnapshot();
        }
        using (var db = Open())
        {
            var store = new SndbObjectStore(db) { ListRebuildEntryTestHook = () => throw new InvalidOperationException("Ready index must survive reopen.") };
            var page = store.ListObjects(Bucket, null, 1, continuation);
            Assert.Equal(version, Assert.Single(page.Objects).VersionId);
            store.SetLifecycle(Bucket, null, null, 0);
            store.ApplyLifecycle(Bucket);
            Assert.Contains(store.ListObjects(Bucket).Objects, x => x.Key == "b");
            VerifyPages(store, Oracle(db, "", null), "", null, 1);
        }
    }

    [Fact]
    public async Task ListObjects_CancelBeforeAndDuringPage_StopsWithoutListAudit()
    {
        using var db = Open();
        var store = new SndbObjectStore(db);
        store.CreateBucket(Bucket);
        await store.PutObjectAsync(Bucket, "a", Stream.Null, cancellationToken: _deadline.Token);
        store.ListObjects(Bucket);
        int before = store.ListAudit(Bucket).Count(x => x.Action == "bucket.objects.list");
        using var cancel = new CancellationTokenSource();
        store.ListCandidateTestHook = cancel.Cancel;
        Assert.Throws<OperationCanceledException>(() => store.ListObjects(Bucket, null, 1, null, null, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => store.ListObjects(Bucket, null, 1, null, null, cancel.Token));
        Assert.Equal(before, store.ListAudit(Bucket).Count(x => x.Action == "bucket.objects.list"));
    }

    [Fact]
    public async Task ListObjects_GroupTokens_RejectMismatchedScopeAndPreserveLegacyBoundary()
    {
        using var db = Open();
        var store = new SndbObjectStore(db);
        store.CreateBucket(Bucket);
        foreach (string key in new[] { "a/1", "a/2", "b", "c" })
            await store.PutObjectAsync(Bucket, key, Stream.Null, cancellationToken: _deadline.Token);
        var first = store.ListObjects(Bucket, null, 1, null, "/", _deadline.Token);
        Assert.Equal(["a/"], first.CommonPrefixes);
        Assert.Throws<ArgumentException>(() => store.ListObjects(Bucket, "a", 1, first.NextContinuationToken, "/"));
        Assert.Throws<ArgumentException>(() => store.ListObjects(Bucket, null, 1, first.NextContinuationToken, "::"));
        Assert.Throws<ArgumentException>(() => store.ListObjects("other-bucket", null, 1, first.NextContinuationToken, "/"));
        Assert.Throws<ArgumentException>(() => store.ListObjects(Bucket, null, 1, "!invalid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ListObjects(Bucket, maxKeys: 0));
        VerifyPages(store, ["o:b", "o:c"], "", "/", 1, LegacyToken("a/1"));
    }

    [Fact]
    public async Task ListObjects_EmbeddedSdk_PreservesGroupingAndCancellation()
    {
        using var client = new SndbObjectStorageClient($"Data Source={_root};Mode=Embedded");
        await client.CreateBucketAsync(Bucket, cancellationToken: _deadline.Token);
        await client.PutObjectAsync(Bucket, "a/1", Stream.Null, cancellationToken: _deadline.Token);
        await client.PutObjectAsync(Bucket, "b", Stream.Null, cancellationToken: _deadline.Token);
        var page = await client.ListObjectsAsync(Bucket, null, 1, null, "/", _deadline.Token);
        Assert.Equal(["a/"], page.CommonPrefixes);
        var next = await client.ListObjectsAsync(Bucket, null, 1, page.NextContinuationToken, "/", _deadline.Token);
        Assert.Equal("b", Assert.Single(next.Objects).Key);
        Assert.False(next.IsTruncated);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListObjectsAsync(Bucket, cancellationToken: cancel.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListObjects_WalSyncFailure_ReplaysIndexAndLatestTogether(bool delete)
    {
        using (var db = Open())
        {
            var store = new SndbObjectStore(db);
            store.CreateBucket(Bucket);
            await store.PutObjectAsync(Bucket, "a", Stream.Null, cancellationToken: _deadline.Token);
            store.ListObjects(Bucket);
            var metadata = db.Keyspaces.Open("__object_storage");
            var failure = new IOException("object-list test sync failure");
            metadata.WalSyncTestHook = () => throw failure;
            try
            {
                if (delete)
                    Assert.Same(failure, Assert.Throws<IOException>(() => store.DeleteObject(Bucket, "a")));
                else
                    Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => store.PutObjectAsync(Bucket, "b", Stream.Null, cancellationToken: _deadline.Token)));
            }
            finally { metadata.WalSyncTestHook = null; }
        }
        using var reopened = Open();
        var recovered = new SndbObjectStore(reopened) { ListRebuildEntryTestHook = () => throw new InvalidOperationException("Recovery must replay the ready index.") };
        Assert.Equal(delete ? [] : new[] { "a", "b" }, recovered.ListObjects(Bucket).Objects.Select(x => x.Key));
        VerifyPages(recovered, Oracle(reopened, "", null), "", null, 1);
    }

    [Fact]
    public async Task ListObjects_BucketLockWait_CanBeCancelledAcrossFacades()
    {
        using var db = Open();
        var store = new SndbObjectStore(db);
        store.CreateBucket(Bucket);
        await store.PutObjectAsync(Bucket, "a", Stream.Null, cancellationToken: _deadline.Token);
        store.ListObjects(Bucket);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ListCandidateTestHook = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), _deadline.Token));
        };
        Task<SndbObjectListResult> first = Task.Run(() => store.ListObjects(Bucket, null, 1, null, null, _deadline.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var second = new SndbObjectStore(db);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => second.ListObjects(Bucket, null, 1, null, null, cancel.Token))
                .WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token));
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
        }
    }

    [Fact]
    public async Task ListObjects_MultipartAndBucketRecreation_KeepDerivedRowsConsistent()
    {
        using var db = Open();
        var store = new SndbObjectStore(db);
        store.CreateBucket(Bucket);
        Assert.Empty(store.ListObjects(Bucket).Objects);
        var upload = store.InitiateMultipartUpload(Bucket, "folder/part");
        await store.UploadPartAsync(upload.UploadId, 1, new MemoryStream([1]), _deadline.Token);
        var written = await store.CompleteMultipartUploadAsync(upload.UploadId, [1], _deadline.Token);
        Assert.Equal(written.VersionId, Assert.Single(store.ListObjects(Bucket).Objects).VersionId);
        store.DeleteObject(Bucket, written.Key);
        store.SetLifecycle(Bucket, null, 0, 0);
        store.ApplyLifecycle(Bucket);
        Assert.Empty(store.ListObjects(Bucket).Objects);
        Assert.True(store.DeleteBucket(Bucket));
        store.CreateBucket(Bucket);
        Assert.Empty(store.ListObjects(Bucket).Objects);
        await store.PutObjectAsync(Bucket, "new", Stream.Null, cancellationToken: _deadline.Token);
        Assert.Equal("new", Assert.Single(store.ListObjects(Bucket).Objects).Key);
    }

    [Fact]
    public async Task RebuildBatch_CheckpointPressure_CancelsBeforeAppend()
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = KvOptions.Default with { IndexRebuildMaxOverlayEntries = 2 },
        });
        var metadata = db.Keyspaces.Open("__object_storage");
        metadata.Put("base", [0]);
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        metadata.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;
            frozen.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), _deadline.Token));
        };
        Task<long> checkpoint = Task.Run(metadata.Compact);
        try
        {
            Assert.True(frozen.Wait(TimeSpan.FromSeconds(5), _deadline.Token));
            metadata.Put("pad", [0]);
            long sequence = metadata.LastSequence;
            long walLength = metadata.ActiveWalLength;
            using var scope = metadata.EnterIndexRebuildBudgetScope(cancel.Token);
            metadata.WriteBackpressureTestHook = cancel.Cancel;
            Assert.Throws<OperationCanceledException>(() => metadata.ApplyIndexRebuildBatch(
                [KvBatchMutation.Put("x"u8.ToArray(), [1]), KvBatchMutation.Put("y"u8.ToArray(), [2])], cancel.Token));
            Assert.Equal(sequence, metadata.LastSequence);
            Assert.Equal(walLength, metadata.ActiveWalLength);
            Assert.Null(metadata.Get("x"));
            Assert.Null(metadata.Get("y"));
        }
        finally
        {
            metadata.WriteBackpressureTestHook = null;
            metadata.CheckpointTestHook = null;
            release.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanRange_OrderedOverlayWithLazyExpiry_PreservesRemainingKeys(bool checkpoint)
    {
        using var db = Open();
        var metadata = db.Keyspaces.Open("expiry-index");
        metadata.Put("a/1", [1], DateTimeOffset.UnixEpoch);
        metadata.Put("a/2", [2]);
        metadata.Put("a/3", [3], DateTimeOffset.UnixEpoch);
        metadata.Put("a/4", [4]);
        metadata.EnableOrderedOverlayScans(_deadline.Token);
        if (checkpoint)
            metadata.CreateSnapshot();
        var page = metadata.ScanRange("a/"u8.ToArray(), null, null, null, 2, _deadline.Token);
        Assert.Equal(["a/2", "a/4"], page.Select(x => Encoding.UTF8.GetString(x.Key.Span)));
    }

    [Fact]
    public void ListObjects_UncompactedDeleteMarkers_RejectsAtPhysicalBudgetAndRecoversAfterCompaction()
    {
        using var db = Open();
        var store = new SndbObjectStore(db);
        store.CreateBucket(Bucket);
        string[] deleted = Enumerable.Range(0, 1100).Select(i => $"a/{i:D5}").ToArray();
        SeedLegacy(db, deleted.Concat(["b"]));
        store.ListObjects(Bucket, maxKeys: 1);
        var metadata = db.Keyspaces.Open("__object_storage");
        metadata.CreateSnapshot();
        foreach (string key in deleted)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            store.DeleteObject(Bucket, key);
        }
        int physical = 0;
        store.ListPhysicalCandidateTestHook = () => physical++;
        var failure = Assert.Throws<SndbObjectStorageException>(() => store.ListObjects(Bucket, null, 1, null, null, _deadline.Token));
        Assert.Equal("object_list_scan_budget_exceeded", failure.Code);
        Assert.Equal(1025, physical);
        metadata.Compact();
        var page = store.ListObjects(Bucket, null, 1, null, null, _deadline.Token);
        Assert.Equal("b", Assert.Single(page.Objects).Key);
        Assert.False(page.IsTruncated);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private void SeedLegacy(Tsdb db, IEnumerable<string> keys)
    {
        var metadata = db.Keyspaces.Open("__object_storage");
        foreach (string[] chunk in keys.Chunk(128))
        {
            _deadline.Token.ThrowIfCancellationRequested();
            var batch = new List<KvBatchMutation>();
            foreach (string key in chunk)
            {
                var record = new SndbObjectRecord(Bucket, key, "v1", "application/octet-stream", 0, "etag", "sha", "", false,
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], []);
                string escaped = Escape(key);
                batch.Add(KvBatchMutation.Put(Encoding.UTF8.GetBytes($"latest:{Bucket}/{escaped}"), "v1"u8.ToArray()));
                batch.Add(KvBatchMutation.Put(Encoding.UTF8.GetBytes($"object:{Bucket}/{escaped}/v1"), JsonSerializer.SerializeToUtf8Bytes(record, SndbObjectStoreJsonContext.Default.SndbObjectRecord)));
            }
            metadata.ApplyBatch(batch);
        }
    }

    private static string[] Oracle(Tsdb db, string prefix, string? delimiter)
    {
        prefix = prefix.TrimStart('/');
        var metadata = db.Keyspaces.Open("__object_storage");
        var results = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var latest in metadata.ScanPrefix($"latest:{Bucket}/", int.MaxValue))
        {
            string encoded = Encoding.UTF8.GetString(latest.Key.Span)[$"latest:{Bucket}/".Length..];
            string version = Encoding.UTF8.GetString(latest.Value.Span);
            byte[]? bytes = metadata.Get($"object:{Bucket}/{encoded}/{version}");
            if (bytes is null)
                continue;
            var record = JsonSerializer.Deserialize(bytes, SndbObjectStoreJsonContext.Default.SndbObjectRecord)!;
            if (record.IsDeleteMarker || !record.Key.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            int index = delimiter is null ? -1 : record.Key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
            string key = index < 0 ? record.Key : record.Key[..(index + delimiter!.Length)];
            results[key] = (index < 0 ? "o:" : "p:") + key;
        }
        return results.Values.ToArray();
    }

    private void VerifyPages(SndbObjectStore store, string[] expected, string prefix, string? delimiter, int size, string? token = null)
    {
        int offset = 0;
        for (int pageNumber = 0; pageNumber <= expected.Length + 1; pageNumber++)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            var page = store.ListObjects(Bucket, prefix, size, token, delimiter, _deadline.Token);
            string[] actual = page.Objects.Select(x => "o:" + x.Key).Concat(page.CommonPrefixes.Select(x => "p:" + x))
                .OrderBy(x => x[2..], StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Skip(offset).Take(size), actual);
            offset += actual.Length;
            Assert.Equal(offset < expected.Length, page.IsTruncated);
            if (!page.IsTruncated) { Assert.Null(page.NextContinuationToken); return; }
            Assert.NotNull(page.NextContinuationToken);
            Assert.NotEqual(token, page.NextContinuationToken);
            token = page.NextContinuationToken;
        }
        Assert.Fail("Pagination did not terminate within the fixture item budget.");
    }

    private static string Escape(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string LegacyToken(string key) => Escape("v1:" + key);

    public void Dispose()
    {
        _deadline.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
