using System.Text;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

public sealed class KvLsmEngineTests : IDisposable
{
    private static readonly KvSstWriterOptions FastSst = new() { BlockSize = 1024, SyncDirectory = false };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-engine-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// 关闭后不再持有任何文件句柄：没有存活快照时立即关闭全部 SST 读取器与 manifest，目录可以改名
    /// （表 RENAME、DDL 发布与备份恢复都依赖这一点）；关闭时仍存活的快照可以读完，释放后句柄随之关闭。
    /// </summary>
    [Fact]
    public void Dispose_ClosesAllFileHandles_ExceptThosePinnedByLiveSnapshots()
    {
        KvLsmEngine engine = KvLsmEngine.Open(_root, Options(backgroundFlush: false));
        engine.Apply([Put("a", "1"), Put("b", "2")]);
        engine.Flush();
        engine.Apply([Put("c", "3")]);
        engine.Flush();
        Assert.Equal("1", Text(engine.Get(Bytes("a"))!.Value));
        KvLsmSnapshot pinned = engine.AcquireSnapshot();
        engine.Dispose();

        Assert.Equal("3", Text(pinned.Get(Bytes("c"))!.Value));
        pinned.Dispose();

        string moved = _root + "-moved";
        Directory.Move(_root, moved);
        Directory.Move(moved, _root);
        using KvLsmEngine reopened = KvLsmEngine.Open(_root, Options());
        Assert.Equal("2", Text(reopened.Get(Bytes("b"))!.Value));
    }

    [Fact]
    public void PutGetDeleteAndTtl_FollowSnapshotRules()
    {
        using KvLsmEngine engine = KvLsmEngine.Open(_root, Options());
        long s1 = engine.Apply([Put("a", "1"), Put("b", "2", expires: 1_000)]);
        long s2 = engine.Apply([KvBatchMutation.Delete(Bytes("a"))]);
        Assert.Equal(s1 + 1, s2);

        Assert.Null(engine.Get(Bytes("a")));
        using (KvLsmSnapshot before = engine.AcquireSnapshot(new DateTimeOffset(999, TimeSpan.Zero)))
        {
            Assert.Null(before.Get(Bytes("a")));
            KvEntry? b = before.Get(Bytes("b"));
            Assert.NotNull(b);
            Assert.Equal(s1, b!.Version);
            Assert.Equal(1_000, b.ExpiresAtUtc!.Value.UtcTicks);
        }
        using (KvLsmSnapshot after = engine.AcquireSnapshot(new DateTimeOffset(1_000, TimeSpan.Zero)))
            Assert.Null(after.Get(Bytes("b")));
        Assert.Null(engine.Get(Bytes("b")));
    }

    [Fact]
    public void Apply_CanonicalizesDuplicateKeysAndRejectsInvalidBatchesWithoutFaulting()
    {
        using KvLsmEngine engine = KvLsmEngine.Open(_root, Options());
        long sequence = engine.Apply([Put("k", "first"), Put("other", "x"), Put("k", "last")]);
        KvEntry? entry = engine.Get(Bytes("k"));
        Assert.Equal("last", Text(entry!.Value));
        Assert.Equal(sequence, entry.Version);

        Assert.Throws<ArgumentException>(() => engine.Apply([]));
        Assert.Throws<ArgumentException>(() => engine.Apply([new KvBatchMutation([], [1])]));
        Assert.Throws<ArgumentException>(() => engine.Apply([new KvBatchMutation(Bytes("d"), null, DateTimeOffset.UnixEpoch)]));
        Assert.False(engine.IsFaulted);
        Assert.Equal(sequence + 1, engine.Apply([Put("k", "again")]));
    }

    [Fact]
    public void Snapshot_IsStableAcrossLaterWritesAndFlushes()
    {
        using KvLsmEngine engine = KvLsmEngine.Open(_root, Options());
        engine.Apply([Put("a", "1"), Put("b", "1")]);
        using KvLsmSnapshot snapshot = engine.AcquireSnapshot();

        engine.Apply([Put("a", "2"), KvBatchMutation.Delete(Bytes("b")), Put("c", "2")]);
        engine.Flush();
        engine.Apply([Put("d", "3")]);
        engine.Flush();

        Assert.Equal("1", Text(snapshot.Get(Bytes("a"))!.Value));
        Assert.Equal("1", Text(snapshot.Get(Bytes("b"))!.Value));
        Assert.Null(snapshot.Get(Bytes("c")));
        Assert.Equal(["a=1", "b=1"], Scan(snapshot));
        using KvLsmSnapshot latest = engine.AcquireSnapshot();
        Assert.Equal(["a=2", "c=2", "d=3"], Scan(latest));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ManyWrites_RotateAndFlushToLevel0_AndSurviveReopen(bool backgroundFlush)
    {
        var options = Options(backgroundFlush: backgroundFlush, writeBuffer: 64 * 1024);
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (KvLsmEngine engine = KvLsmEngine.Open(_root, options))
        {
            for (int i = 0; i < 6_000; i++)
            {
                string key = $"key:{i % 2_500:D5}";
                string value = $"value-{i}-" + new string('x', 40);
                engine.Apply([Put(key, value)]);
                expected[key] = value;
            }
            engine.Flush();
            KvLsmEngineStats stats = engine.GetStats();
            Assert.True(stats.FileCountByLevel[0] >= 3, $"L0 files: {stats.FileCountByLevel[0]}");
            Assert.Equal(0, stats.ImmutableMemTables);
            Assert.True(stats.FlushCount >= 3);
            AssertMatches(engine, expected);
            Assert.Single(Directory.GetFiles(KvLsmPaths.WalDirectory(_root)));
        }

        using KvLsmEngine reopened = KvLsmEngine.Open(_root, options);
        AssertMatches(reopened, expected);
        Assert.Equal(6_000, reopened.LastSequence);
    }

    [Fact]
    public void Reopen_ReplaysUnflushedWalIntoLevel0_AndIgnoresTornTail()
    {
        using (KvLsmEngine engine = KvLsmEngine.Open(_root, Options(generation: 4)))
        {
            engine.Apply([Put("a", "1")]);
            engine.Apply([Put("b", "2"), KvBatchMutation.Delete(Bytes("a"))]);
        }

        string wal = Directory.GetFiles(KvLsmPaths.WalDirectory(_root)).Single();
        using (var stream = new FileStream(wal, FileMode.Append))
            stream.Write([1, 2, 3, 4, 5, 6, 7]);

        using KvLsmEngine reopened = KvLsmEngine.Open(_root, Options());
        Assert.Equal(4, reopened.Generation);
        Assert.Equal(2, reopened.LastSequence);
        Assert.Null(reopened.Get(Bytes("a")));
        Assert.Equal("2", Text(reopened.Get(Bytes("b"))!.Value));
        Assert.Equal(1, reopened.GetStats().FileCountByLevel[0]);
        Assert.False(File.Exists(wal));
        Assert.Equal(3, reopened.Apply([Put("c", "3")]));
    }

    [Theory]
    [InlineData((int)KvLsmEnginePhase.AfterWalAppend)]
    [InlineData((int)KvLsmEnginePhase.FlushAfterSstWritten)]
    [InlineData((int)KvLsmEnginePhase.FlushAfterManifestCommit)]
    public void CrashInjection_ReopenRecoversEveryCommittedWriteExactlyOnce(int phaseValue)
    {
        var phase = (KvLsmEnginePhase)phaseValue;
        bool armed = false;
        var crashing = Options(backgroundFlush: false) with
        {
            TestHook = p => { if (armed && p == phase) throw new IOException("injected crash"); },
        };

        using (KvLsmEngine engine = KvLsmEngine.Open(_root, crashing))
        {
            engine.Apply([Put("a", "1")]);
            engine.Apply([Put("b", "1")]);
            armed = true;
            if (phase == KvLsmEnginePhase.AfterWalAppend)
            {
                Assert.Throws<IOException>(() => engine.Apply([Put("a", "2")]));
            }
            else
            {
                engine.Apply([Put("a", "2")]);
                Assert.Throws<IOException>(() => engine.Flush());
            }
            Assert.True(engine.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => engine.Apply([Put("z", "z")]));
        }

        using KvLsmEngine reopened = KvLsmEngine.Open(_root, Options());
        Assert.Equal(3, reopened.LastSequence);
        using KvLsmSnapshot snapshot = reopened.AcquireSnapshot();
        Assert.Equal(["a=2", "b=1"], Scan(snapshot));
        // 三个崩溃点最终都只留下一个 L0：要么重放 WAL 生成，要么沿用已提交的 flush，不会重复落盘。
        Assert.Equal(1, reopened.GetStats().FileCountByLevel[0]);
        Assert.Single(Directory.GetFiles(KvLsmPaths.SstDirectory(_root)));
        Assert.Empty(Directory.GetFiles(KvLsmPaths.SstDirectory(_root), "*.tmp"));
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    [InlineData(13, true)]
    public void RandomWorkloadWithReopensAndHeldSnapshots_MatchesModel(int seed, bool backgroundFlush)
    {
        var random = new Random(seed);
        var model = new Model();
        var options = Options(backgroundFlush: backgroundFlush, writeBuffer: 64 * 1024);
        var held = new List<(KvLsmSnapshot Snapshot, long Sequence)>();
        KvLsmEngine engine = KvLsmEngine.Open(_root, options);
        try
        {
            for (int step = 0; step < 2_000; step++)
            {
                int action = random.Next(100);
                if (action < 80)
                {
                    var batch = new List<KvBatchMutation>();
                    int size = random.Next(1, 6);
                    for (int i = 0; i < size; i++)
                    {
                        string key = "k" + random.Next(0, 300).ToString("D3");
                        if (random.Next(5) == 0)
                            batch.Add(KvBatchMutation.Delete(Bytes(key)));
                        else
                            batch.Add(Put(key, $"v{step}.{i}" + new string('p', random.Next(0, 200)), expires: random.Next(6) == 0 ? random.Next(1, 400) : null));
                    }
                    long sequence = engine.Apply(batch);
                    model.Apply(sequence, batch);
                }
                else if (action < 85)
                {
                    engine.Flush();
                }
                else if (action < 90 && held.Count < 4)
                {
                    KvLsmSnapshot snapshot = engine.AcquireSnapshot(new DateTimeOffset(random.Next(0, 500), TimeSpan.Zero));
                    held.Add((snapshot, snapshot.Sequence));
                }
                else if (action < 95)
                {
                    foreach ((KvLsmSnapshot snapshot, long sequence) in held)
                    {
                        Assert.Equal(model.Visible(sequence, snapshot.ReadTimestampUtcTicks), Scan(snapshot));
                        snapshot.Dispose();
                    }
                    held.Clear();
                }
                else if (action < 97)
                {
                    foreach ((KvLsmSnapshot snapshot, _) in held)
                        snapshot.Dispose();
                    held.Clear();
                    engine.Dispose();
                    engine = KvLsmEngine.Open(_root, options);
                    Assert.Equal(model.LastSequence, engine.LastSequence);
                }

                if (step % 250 == 0)
                {
                    long readTicks = random.Next(0, 500);
                    using KvLsmSnapshot current = engine.AcquireSnapshot(new DateTimeOffset(readTicks, TimeSpan.Zero));
                    List<string> expected = model.Visible(current.Sequence, readTicks);
                    Assert.Equal(expected, Scan(current));
                    List<string> reverse = ScanReverse(current);
                    reverse.Reverse();
                    Assert.Equal(expected, reverse);
                    for (int probe = 0; probe < 50; probe++)
                    {
                        string key = "k" + random.Next(0, 300).ToString("D3");
                        string? want = expected.FirstOrDefault(e => e.StartsWith(key + "=", StringComparison.Ordinal));
                        KvEntry? got = current.Get(Bytes(key));
                        Assert.Equal(want, got is null ? null : key + "=" + Text(got.Value));
                    }
                }
            }
        }
        finally
        {
            foreach ((KvLsmSnapshot snapshot, _) in held)
                snapshot.Dispose();
            engine.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentWritersReadersAndFlush_KeepBatchesAtomicInEverySnapshot()
    {
        using KvLsmEngine engine = KvLsmEngine.Open(_root, Options(writeBuffer: 64 * 1024));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        long checks = 0;

        Task[] writers = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            var random = new Random(w);
            while (!stop.IsCancellationRequested)
            {
                int pair = random.Next(0, 400);
                string value = $"w{w}-{random.Next()}" + new string('v', 80);
                engine.Apply([Put($"a:{pair:D3}", value), Put($"b:{pair:D3}", value)]);
            }
        })).ToArray();

        Task[] readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using KvLsmSnapshot snapshot = engine.AcquireSnapshot();
                var a = new Dictionary<string, string>();
                var b = new Dictionary<string, string>();
                using (KvSnapshotIterator iterator = snapshot.CreateIterator())
                {
                    for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
                    {
                        string key = Text(iterator.Key);
                        (key[0] == 'a' ? a : b)[key[2..]] = Text(iterator.Value);
                    }
                }
                Assert.Equal(a.Count, b.Count);
                foreach ((string pair, string value) in a)
                    Assert.Equal(value, b[pair]);
                Interlocked.Increment(ref checks);
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers)).WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(Interlocked.Read(ref checks) > 0);
        Assert.False(engine.IsFaulted);
        Assert.True(engine.GetStats().FlushCount > 0);
    }

    [Fact]
    public void Backpressure_TimesOutWithRetryableClassificationWhileFlushIsStalled()
    {
        using var release = new ManualResetEventSlim(false);
        var options = Options(writeBuffer: 64 * 1024) with
        {
            MaxImmutableMemTables = 1,
            WriteBackpressureTimeout = TimeSpan.FromMilliseconds(200),
            TestHook = p => { if (p == KvLsmEnginePhase.FlushAfterSstWritten) release.Wait(TimeSpan.FromSeconds(30)); },
        };
        using KvLsmEngine engine = KvLsmEngine.Open(_root, options);

        IOException? rejected = null;
        for (int i = 0; i < 20_000 && rejected is null; i++)
        {
            try
            {
                engine.Apply([Put($"k{i:D6}", new string('x', 200))]);
            }
            catch (IOException exception)
            {
                rejected = exception;
            }
        }

        Assert.NotNull(rejected);
        Assert.True(KvAtomicBatchErrors.IsRetryableCheckpointPressure(rejected!));
        Assert.False(engine.IsFaulted);
        release.Set();
        engine.Flush();
        engine.Apply([Put("after", "ok")]);
        Assert.Equal("ok", Text(engine.Get(Bytes("after"))!.Value));
    }

    [Fact]
    public void Dispose_IsIdempotentAndRejectsLaterUse()
    {
        KvLsmEngine engine = KvLsmEngine.Open(_root, Options());
        engine.Apply([Put("a", "1")]);
        engine.Dispose();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.Apply([Put("a", "2")]));
        Assert.Throws<ObjectDisposedException>(() => engine.AcquireSnapshot());
    }

    private static KvLsmEngineOptions Options(bool backgroundFlush = true, long writeBuffer = 1024 * 1024, long generation = 0)
        => new()
        {
            BackgroundFlush = backgroundFlush,
            WriteBufferBytes = writeBuffer,
            SyncWalOnEveryWrite = false,
            Generation = generation,
            MaxImmutableMemTables = 4,
            SstWriterOptions = FastSst,
            // 这些测试用很小的 tick 作 TTL 与快照读取时刻；固定 compaction 时钟为 0，避免按真实时钟物理回收。
            UtcNowTicks = static () => 0,
        };

    private static void AssertMatches(KvLsmEngine engine, SortedDictionary<string, string> expected)
    {
        using KvLsmSnapshot snapshot = engine.AcquireSnapshot();
        Assert.Equal(expected.Select(static p => p.Key + "=" + p.Value), Scan(snapshot));
        foreach ((string key, string value) in expected.Take(200))
            Assert.Equal(value, Text(snapshot.Get(Bytes(key))!.Value));
    }

    private static List<string> Scan(KvLsmSnapshot snapshot)
    {
        var result = new List<string>();
        using KvSnapshotIterator iterator = snapshot.CreateIterator();
        for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
            result.Add(Text(iterator.Key) + "=" + Text(iterator.Value));
        return result;
    }

    private static List<string> ScanReverse(KvLsmSnapshot snapshot)
    {
        var result = new List<string>();
        using KvSnapshotIterator iterator = snapshot.CreateIterator();
        for (iterator.SeekToLast(); iterator.Valid; iterator.MovePrev())
            result.Add(Text(iterator.Key) + "=" + Text(iterator.Value));
        return result;
    }

    private static KvBatchMutation Put(string key, string value, long? expires = null)
        => new(Bytes(key), Bytes(value), expires is null ? null : new DateTimeOffset(expires.Value, TimeSpan.Zero));

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);

    private sealed class Model
    {
        private readonly List<(long Sequence, string Key, string? Value, long Expires)> _versions = [];

        internal long LastSequence { get; private set; }

        internal void Apply(long sequence, IReadOnlyList<KvBatchMutation> batch)
        {
            var last = new Dictionary<string, KvBatchMutation>(StringComparer.Ordinal);
            foreach (KvBatchMutation mutation in batch)
                last[Text(mutation.Key)] = mutation;
            foreach ((string key, KvBatchMutation mutation) in last)
            {
                _versions.Add((sequence, key, mutation.Value is null ? null : Text(mutation.Value),
                    mutation.ExpiresAtUtc?.UtcTicks ?? 0));
            }
            LastSequence = sequence;
        }

        internal List<string> Visible(long sequence, long readTicks)
            => _versions
                .Where(v => v.Sequence <= sequence)
                .GroupBy(static v => v.Key)
                .Select(static g => g.MaxBy(static v => v.Sequence))
                .Where(v => v.Value is not null && (v.Expires == 0 || v.Expires > readTicks))
                .OrderBy(static v => v.Key, StringComparer.Ordinal)
                .Select(static v => v.Key + "=" + v.Value)
                .ToList();
    }
}
