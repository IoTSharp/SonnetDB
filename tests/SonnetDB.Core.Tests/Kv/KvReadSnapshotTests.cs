using System.Text;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvReadSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-kv-read-snapshot-tests",
        Guid.NewGuid().ToString("N"));

    public KvReadSnapshotTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AcquireReadSnapshot_DuringCheckpoint_PreservesFrozenMutableAndDiskView()
    {
        using var keyspace = KvKeyspace.Open("checkpoint-view", _root, Options());
        keyspace.Put("item:01", Bytes("disk-one"));
        keyspace.Put("item:02", Bytes("disk-two"));
        keyspace.Compact();

        Assert.True(keyspace.Delete("item:01"));
        keyspace.Put("item:02", Bytes("frozen-two"));
        keyspace.Put("item:03", Bytes("frozen-three"));

        using var checkpointFrozen = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        keyspace.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;

            checkpointFrozen.Set();
            if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the frozen checkpoint");
        };

        Task<long> checkpoint = Task.Run(keyspace.Compact);
        KvRangeCursor? cursor = null;
        try
        {
            Assert.True(checkpointFrozen.Wait(TimeSpan.FromSeconds(10)));
            keyspace.Put("item:02", Bytes("mutable-two"));
            Assert.True(keyspace.Delete("item:03"));
            keyspace.Put("item:04", Bytes("mutable-four"));

            KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
            cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = Bytes("item:"),
                PageSize = 1,
            });
            snapshot.Dispose();

            keyspace.Put("item:05", Bytes("after-snapshot"));
        }
        finally
        {
            releaseCheckpoint.Set();
        }

        await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(cursor);
        using (cursor)
        {
            IReadOnlyList<KvEntry> firstPage = cursor.ReadNextPage();
            IReadOnlyList<KvEntry> secondPage = cursor.ReadNextPage();
            IReadOnlyList<KvEntry> end = cursor.ReadNextPage();

            Assert.Equal("item:02", Text(Assert.Single(firstPage).Key));
            Assert.Equal("mutable-two", Text(firstPage[0].Value));
            Assert.Equal("item:04", Text(Assert.Single(secondPage).Key));
            Assert.Equal("mutable-four", Text(secondPage[0].Value));
            Assert.Empty(end);
            Assert.True(cursor.IsExhausted);

            Assert.Equal("mutable-two", Text(firstPage[0].Value));
            Assert.Equal("item:02", Text(firstPage[0].Key));
        }
    }

    [Fact]
    public async Task ReadNextPage_UsesOneDiskEnumeratorAndDoesNotHoldKeyspaceLock()
    {
        using var keyspace = KvKeyspace.Open("forward-cursor", _root, Options());
        for (int i = 0; i < 8; i++)
            keyspace.Put($"item:{i:D2}", [(byte)i]);
        keyspace.Compact();

        int scanStarts = 0;
        var visited = new List<int>();
        keyspace.ConfigureSnapshotDiskScanTestHooks(
            () => Interlocked.Increment(ref scanStarts),
            index => visited.Add(index));

        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = Bytes("item:"),
            PageSize = 2,
        });
        using var pageReadStarted = new ManualResetEventSlim();
        using var releasePageRead = new ManualResetEventSlim();
        cursor.PageReadTestHook = () =>
        {
            pageReadStarted.Set();
            if (!releasePageRead.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the cursor page read");
        };

        Task<IReadOnlyList<KvEntry>> firstPageTask = Task.Run(() => cursor.ReadNextPage());
        Assert.True(pageReadStarted.Wait(TimeSpan.FromSeconds(10)));
        Task<long> concurrentWrite = Task.Run(() => keyspace.Put("item:after", [99]));
        try
        {
            await concurrentWrite.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releasePageRead.Set();
        }

        IReadOnlyList<KvEntry> firstPage = await firstPageTask.WaitAsync(TimeSpan.FromSeconds(10));
        cursor.PageReadTestHook = null;
        IReadOnlyList<KvEntry> secondPage = cursor.ReadNextPage();

        Assert.Equal(["item:00", "item:01"], firstPage.Select(static entry => Text(entry.Key)).ToArray());
        Assert.Equal(["item:02", "item:03"], secondPage.Select(static entry => Text(entry.Key)).ToArray());
        Assert.Equal(1, scanStarts);
        Assert.Equal([0, 1, 2, 3], visited);
    }

    [Fact]
    public void ReadNextPage_UsesSnapshotTimestampForTtlVisibility()
    {
        DateTimeOffset readTimestampUtc = DateTimeOffset.UtcNow.AddHours(-1);
        KeyValuePair<byte[], KvValueEntry>[] values =
        [
            new(Bytes("item:expired"), new KvValueEntry([2], 2, readTimestampUtc.AddMinutes(-30))),
            new(Bytes("item:visible"), new KvValueEntry([1], 1, readTimestampUtc.AddMinutes(30))),
        ];
        var state = new KvReadSnapshotState(
            values,
            frozenValues: [],
            diskLease: null,
            sequence: 2,
            readTimestampUtc);

        using var snapshot = new KvReadSnapshot(state);
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = Bytes("item:"),
            PageSize = 8,
        });

        IReadOnlyList<KvEntry> page = cursor.ReadNextPage();

        KvEntry entry = Assert.Single(page);
        Assert.Equal("item:visible", Text(entry.Key));
        Assert.True(entry.ExpiresAtUtc < DateTimeOffset.UtcNow);
        Assert.Equal(readTimestampUtc, snapshot.ReadTimestampUtc);
    }

    [Fact]
    public void GetEntry_AfterLiveMutation_RemainsStableAndRejectsDisposedSnapshot()
    {
        using var keyspace = KvKeyspace.Open("point-read", _root, Options());
        keyspace.Put("item:stable", [1]);
        using var snapshot = keyspace.AcquireReadSnapshot();

        keyspace.Put("item:stable", [2]);
        keyspace.Put("item:new", [3]);

        KvEntry stable = Assert.IsType<KvEntry>(snapshot.GetEntry(Bytes("item:stable")));
        Assert.Equal([1], stable.Value.ToArray());
        Assert.Null(snapshot.GetEntry(Bytes("item:new")));

        snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.GetEntry(Bytes("item:stable")));
    }

    [Fact]
    public void AcquireReadSnapshot_OverlayLimitFailsWithoutChangingWalOrState()
    {
        using var keyspace = KvKeyspace.Open(
            "snapshot-budget",
            _root,
            Options() with { MaxSnapshotOverlayEntries = 2 });
        keyspace.Put("item:01", [1]);
        keyspace.Put("item:02", [2]);
        keyspace.Put("item:03", [3]);
        long sequence = keyspace.LastSequence;
        long walLength = keyspace.ActiveWalLength;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(keyspace.AcquireReadSnapshot);

        Assert.Contains("MaxSnapshotOverlayEntries", error.Message, StringComparison.Ordinal);
        Assert.Equal(sequence, keyspace.LastSequence);
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal([1], keyspace.Get("item:01"));
        Assert.Equal([2], keyspace.Get("item:02"));
        Assert.Equal([3], keyspace.Get("item:03"));
    }

    [Fact]
    public async Task AcquireReadSnapshot_FrozenOverlayCountsTowardLimitWithoutChangingWalOrState()
    {
        using var keyspace = KvKeyspace.Open(
            "snapshot-frozen-budget",
            _root,
            Options() with { MaxSnapshotOverlayEntries = 2 });
        keyspace.Put("item:01", [1]);
        keyspace.Put("item:02", [2]);
        keyspace.Put("item:03", [3]);

        using var checkpointFrozen = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        keyspace.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;

            checkpointFrozen.Set();
            if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the frozen checkpoint");
        };

        Task<long> checkpoint = Task.Run(keyspace.Compact);
        try
        {
            Assert.True(checkpointFrozen.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, keyspace.MutableOverlayEntryCount);
            Assert.Equal(3, keyspace.PendingOverlayEntryCount);
            long sequence = keyspace.LastSequence;
            long walLength = keyspace.ActiveWalLength;

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                keyspace.AcquireReadSnapshot);

            Assert.Contains("MaxSnapshotOverlayEntries", error.Message, StringComparison.Ordinal);
            Assert.Equal(sequence, keyspace.LastSequence);
            Assert.Equal(walLength, keyspace.ActiveWalLength);
            Assert.Equal([1], keyspace.Get("item:01"));
            Assert.Equal([2], keyspace.Get("item:02"));
            Assert.Equal([3], keyspace.Get("item:03"));
        }
        finally
        {
            releaseCheckpoint.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void ReadNextPage_CanceledMidPageTerminatesCursor()
    {
        using var keyspace = KvKeyspace.Open("canceled-cursor", _root, Options());
        keyspace.Put("item:01", [1]);
        keyspace.Put("item:02", [2]);
        keyspace.Put("item:03", [3]);
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = Bytes("item:"),
            PageSize = 3,
        });
        using var cancellation = new CancellationTokenSource();
        cursor.EntryCopiedTestHook = count =>
        {
            if (count == 1)
                cancellation.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() => cursor.ReadNextPage(cancellation.Token));
        InvalidOperationException terminal = Assert.Throws<InvalidOperationException>(
            () => cursor.ReadNextPage());

        Assert.Contains("取消", terminal.Message, StringComparison.Ordinal);
        snapshot.Dispose();
        keyspace.Compact();
    }

    [Fact]
    public void OpenRangeCursor_NonPositivePageByteBudget_ThrowsArgumentOutOfRangeException()
    {
        using var keyspace = KvKeyspace.Open("invalid-page-byte-budget", _root, Options());
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();

        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.OpenRangeCursor(
            new KvRangeScanOptions { MaxPageBytes = 0 }));
    }

    [Fact]
    public void ReadNextPage_PageByteBudgetStopsBeforeEntryAndPreservesContinuation()
    {
        using var keyspace = KvKeyspace.Open("page-byte-continuation", _root, Options());
        keyspace.Put("item:01", Bytes("12345678"));
        keyspace.Put("item:02", Bytes("abcdefgh"));
        keyspace.Put("item:03", Bytes("ABCDEFGH"));
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            PageSize = 10,
            MaxPageBytes = 30,
        });

        IReadOnlyList<KvEntry> first = cursor.ReadNextPage();
        IReadOnlyList<KvEntry> second = cursor.ReadNextPage();

        Assert.Equal(["item:01", "item:02"], first.Select(static entry => Text(entry.Key)).ToArray());
        Assert.Equal("item:03", Text(Assert.Single(second).Key));
        Assert.Empty(cursor.ReadNextPage());
    }

    [Fact]
    public void ReadNextPage_CanceledWithPendingEntryTerminatesAndReleasesSnapshotLease()
    {
        using var keyspace = KvKeyspace.Open("pending-page-cancel", _root, Options());
        keyspace.Put("item:01", Bytes("12345678"));
        keyspace.Put("item:02", Bytes("abcdefgh"));
        keyspace.Put("item:03", Bytes("ABCDEFGH"));
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            PageSize = 10,
            MaxPageBytes = 15,
        });

        Assert.Equal("item:01", Text(Assert.Single(cursor.ReadNextPage()).Key));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => cursor.ReadNextPage(cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => cursor.ReadNextPage());
        snapshot.Dispose();
        keyspace.Compact();
    }

    [Fact]
    public void ReadNextPage_EntryExceedsPageByteBudget_FaultsBeforeCopy()
    {
        using var keyspace = KvKeyspace.Open("oversized-page-entry", _root, Options());
        keyspace.Put("item:01", Bytes("12"));
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            PageSize = 10,
            MaxPageBytes = 8,
        });
        int copied = 0;
        cursor.EntryCopiedTestHook = _ => copied++;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => cursor.ReadNextPage());

        Assert.Contains("MaxPageBytes=8", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, copied);
        Assert.Throws<InvalidOperationException>(() => cursor.ReadNextPage());
    }

    [Fact]
    public void ReadNextPage_LargeValuesStopsAtByteBudgetWithoutLosingEntry()
    {
        const int valueSize = 128 * 1024;
        byte[] value = new byte[valueSize];
        using var keyspace = KvKeyspace.Open("large-value-page-budget", _root, Options());
        for (int index = 0; index < 20; index++)
            keyspace.Put($"item:{index:D2}", value);
        keyspace.Compact();

        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            PageSize = 512,
            MaxPageBytes = (2 * valueSize) + 32,
        });

        IReadOnlyList<KvEntry> first = cursor.ReadNextPage();
        IReadOnlyList<KvEntry> second = cursor.ReadNextPage();

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal("item:00", Text(first[0].Key));
        Assert.Equal("item:01", Text(first[1].Key));
        Assert.Equal("item:02", Text(second[0].Key));
        Assert.Equal("item:03", Text(second[1].Key));
        Assert.All(first.Concat(second), entry => Assert.Equal(valueSize, entry.Value.Length));
    }

    [Fact]
    public void ReadNextPage_LargeDiskStateAllocatesOnlyBoundedPageMemory()
    {
        using var keyspace = KvKeyspace.Open("bounded-page", _root, Options());
        for (int i = 0; i < 10_000; i++)
            keyspace.Put($"item:{i:D5}", [(byte)i]);
        keyspace.Compact();

        using (KvReadSnapshot warmSnapshot = keyspace.AcquireReadSnapshot())
        using (KvRangeCursor warmCursor = warmSnapshot.OpenRangeCursor(new KvRangeScanOptions { PageSize = 8 }))
            Assert.Equal(8, warmCursor.ReadNextPage().Count);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions { PageSize = 8 });
        IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(8, page.Count);
        Assert.True(
            allocated < 256 * 1024,
            $"Snapshot cursor allocation should be page-bounded. Allocated={allocated:N0} bytes.");
    }

    [Fact]
    public async Task ReadNextPage_LargeFrozenOverlayAllocatesOnlyBoundedPageMemory()
    {
        const int entryCount = 10_000;
        using var keyspace = KvKeyspace.Open(
            "bounded-frozen-page",
            _root,
            Options() with { MaxSnapshotOverlayEntries = entryCount });
        for (int i = 0; i < entryCount; i++)
            keyspace.Put($"item:{i:D5}", [(byte)i]);

        using var checkpointFrozen = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        keyspace.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;

            checkpointFrozen.Set();
            if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the frozen checkpoint");
        };

        Task<long> checkpoint = Task.Run(keyspace.Compact);
        try
        {
            Assert.True(checkpointFrozen.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, keyspace.MutableOverlayEntryCount);
            Assert.Equal(entryCount, keyspace.PendingOverlayEntryCount);
            using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();

            using (KvRangeCursor warmCursor = snapshot.OpenRangeCursor(
                new KvRangeScanOptions { PageSize = 1 }))
            {
                Assert.Single(warmCursor.ReadNextPage());
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(
                new KvRangeScanOptions { PageSize = 1 });
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            KvEntry first = Assert.Single(page);
            Assert.Equal("item:00000", Text(first.Key));
            Assert.True(
                allocated < 256 * 1024,
                $"Frozen-overlay cursor allocation should be page-bounded. Allocated={allocated:N0} bytes.");
        }
        finally
        {
            releaseCheckpoint.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static KvOptions Options()
        => KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        };

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);
}
