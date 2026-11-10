using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

public sealed class KvMemTableTests
{
    [Fact]
    public void Add_ThenIterateBothDirections_YieldsInternalKeyOrder()
    {
        var table = new KvMemTable();
        var random = new Random(1);
        var keys = new List<byte[]>();
        for (int i = 0; i < 20_000; i++)
        {
            byte[] userKey = Encoding.UTF8.GetBytes("k" + random.Next(0, 5_000));
            byte[] key = KvInternalKey.Encode(userKey, i + 1, random.Next(4) == 0 ? KvEntryKind.Delete : KvEntryKind.Put);
            keys.Add(key);
            table.Add(key, KvInternalKey.Kind(key) == KvEntryKind.Put ? key.AsSpan(0, 3) : []);
        }
        keys.Sort(static (a, b) => KvInternalKey.Compare(a, b));

        Assert.Equal(keys.Count, table.EntryCount);
        Assert.Equal(1, table.MinSequence);
        Assert.Equal(20_000, table.MaxSequence);
        Assert.True(table.ApproximateMemoryUsage > 0);

        using KvMemTableIterator iterator = table.CreateIterator();
        int index = 0;
        for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
            Assert.Equal(keys[index++], iterator.Key.ToArray());
        Assert.Equal(keys.Count, index);

        for (iterator.SeekToLast(); iterator.Valid; iterator.MovePrev())
            Assert.Equal(keys[--index], iterator.Key.ToArray());
        Assert.Equal(0, index);
    }

    [Fact]
    public void SeekAndSeekForPrev_MatchReferenceBounds()
    {
        var table = new KvMemTable();
        var keys = new List<byte[]>();
        for (int i = 0; i < 2_000; i++)
        {
            byte[] key = KvInternalKey.Encode(Encoding.UTF8.GetBytes($"user:{i * 3:D6}"), i + 1, KvEntryKind.Put);
            keys.Add(key);
            table.Add(key, "v"u8);
        }
        keys.Sort(static (a, b) => KvInternalKey.Compare(a, b));

        using KvMemTableIterator iterator = table.CreateIterator();
        var random = new Random(5);
        for (int i = 0; i < 3_000; i++)
        {
            byte[] target = KvInternalKey.EncodeSeekKey(Encoding.UTF8.GetBytes($"user:{random.Next(-10, 6_100):D6}"), random.Next(0, 2_500));

            int lower = keys.FindIndex(k => KvInternalKey.Compare(k, target) >= 0);
            iterator.Seek(target);
            Assert.Equal(lower >= 0, iterator.Valid);
            if (lower >= 0)
                Assert.Equal(keys[lower], iterator.Key.ToArray());

            int upper = keys.FindLastIndex(k => KvInternalKey.Compare(k, target) <= 0);
            iterator.SeekForPrev(target);
            Assert.Equal(upper >= 0, iterator.Valid);
            if (upper >= 0)
                Assert.Equal(keys[upper], iterator.Key.ToArray());
        }
    }

    [Fact]
    public void TryGet_ReturnsNewestVersionVisibleAtSnapshot()
    {
        var table = new KvMemTable();
        table.Add(KvInternalKey.Encode("k"u8, 10, KvEntryKind.Put), "v10"u8, expiresAtUtcTicks: 99);
        table.Add(KvInternalKey.Encode("k"u8, 20, KvEntryKind.Delete), []);
        table.Add(KvInternalKey.Encode("k"u8, 30, KvEntryKind.Put), "v30"u8);
        table.Add(KvInternalKey.Encode("ka"u8, 5, KvEntryKind.Put), "other"u8);

        Assert.True(table.TryGet("k"u8, 100, out KvSstEntry latest));
        Assert.Equal("v30", Encoding.UTF8.GetString(latest.Value.Span));
        Assert.True(table.TryGet("k"u8, 25, out KvSstEntry tombstone));
        Assert.Equal(KvEntryKind.Delete, tombstone.Kind);
        Assert.True(table.TryGet("k"u8, 15, out KvSstEntry old));
        Assert.Equal(99, old.ExpiresAtUtcTicks);
        Assert.False(table.TryGet("k"u8, 9, out _));
        Assert.False(table.TryGet("j"u8, 100, out _));
        Assert.True(table.TryGet("ka"u8, 5, out _));
    }

    [Fact]
    public void Add_RejectsDuplicatesInvalidKeysAndTombstonePayload()
    {
        var table = new KvMemTable();
        byte[] key = KvInternalKey.Encode("k"u8, 1, KvEntryKind.Put);
        table.Add(key, "v"u8);

        Assert.Throws<ArgumentException>(() => table.Add(key, "v"u8));
        Assert.Throws<ArgumentException>(() => table.Add(KvInternalKey.EncodeSeekKey("k"u8, 2), []));
        Assert.Throws<ArgumentException>(() => table.Add(new byte[3], []));
        Assert.Throws<ArgumentException>(() => table.Add(KvInternalKey.Encode("d"u8, 2, KvEntryKind.Delete), "x"u8));
        Assert.Throws<ArgumentException>(() => table.Add(KvInternalKey.Encode("d"u8, 2, KvEntryKind.Delete), [], 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Add(KvInternalKey.Encode("d"u8, 2, KvEntryKind.Put), [], -1));
        Assert.Equal(1, table.EntryCount);
    }

    [Fact]
    public void LargeAndEmptyValues_RoundTripAcrossArenaChunks()
    {
        var table = new KvMemTable();
        byte[] large = new byte[600 * 1024];
        new Random(2).NextBytes(large);
        byte[] medium = new byte[200 * 1024];
        new Random(3).NextBytes(medium);
        table.Add(KvInternalKey.Encode("large"u8, 1, KvEntryKind.Put), large);
        for (int i = 0; i < 12; i++)
            table.Add(KvInternalKey.Encode(Encoding.UTF8.GetBytes("medium" + i), i + 2, KvEntryKind.Put), medium);
        table.Add(KvInternalKey.Encode("empty"u8, 50, KvEntryKind.Put), []);

        Assert.True(table.TryGet("large"u8, 100, out KvSstEntry entry));
        Assert.Equal(large, entry.Value.ToArray());
        Assert.True(table.TryGet("medium11"u8, 100, out KvSstEntry mediumEntry));
        Assert.Equal(medium, mediumEntry.Value.ToArray());
        Assert.True(table.TryGet("empty"u8, 100, out KvSstEntry empty));
        Assert.True(empty.Value.IsEmpty);
        Assert.True(table.ApproximateMemoryUsage >= large.Length + (12L * medium.Length));
    }

    [Fact]
    public void EmptyTable_IteratorsAreInvalid()
    {
        var table = new KvMemTable();
        using KvMemTableIterator iterator = table.CreateIterator();
        iterator.SeekToFirst();
        Assert.False(iterator.Valid);
        iterator.SeekToLast();
        Assert.False(iterator.Valid);
        iterator.SeekForPrev(KvInternalKey.EncodeLastKey("z"u8));
        Assert.False(iterator.Valid);
        Assert.Throws<InvalidOperationException>(() => iterator.MoveNext());
        Assert.False(table.TryGet("k"u8, 1, out _));
        Assert.Equal(long.MaxValue, table.MinSequence);
    }

    [Fact]
    public async Task ConcurrentReaders_AlwaysSeeSortedCompleteEntriesWhileWriterInserts()
    {
        const int total = 150_000;
        var table = new KvMemTable();
        int writerDone = 0;
        long readerScans = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var start = new Barrier(5);
        using var firstWrite = new ManualResetEventSlim();
        using var readersObserved = new CountdownEvent(4);

        Task Actor(Action action) => Task.Factory.StartNew(() =>
        {
            try
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10), deadline.Token))
                    throw new TimeoutException("Concurrent actors did not start.");
                action();
            }
            catch { deadline.Cancel(); throw; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Dedicated actors ensure DOTNET_PROCESSOR_COUNT=2 cannot serialize the writer
        // ahead of every reader. Every reader observes a published row before writing continues.
        Task writer = Actor(() =>
        {
            try
            {
                int[] order = Enumerable.Range(0, total).ToArray();
                new Random(9).Shuffle(order);
                for (int i = 0; i < total; i++)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    byte[] userKey = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(userKey, order[i]);
                    byte[] key = KvInternalKey.Encode(userKey, i + 1, KvEntryKind.Put);
                    table.Add(key, ValueFor(userKey));
                    if (i == 0)
                    {
                        firstWrite.Set();
                        if (!readersObserved.Wait(TimeSpan.FromSeconds(10), deadline.Token))
                            throw new TimeoutException("Concurrent readers did not observe the initial row.");
                    }
                }
            }
            finally { Volatile.Write(ref writerDone, 1); firstWrite.Set(); }
        });

        Task[] readers = Enumerable.Range(0, 4).Select(seed => Actor(() =>
        {
            if (!firstWrite.Wait(TimeSpan.FromSeconds(10), deadline.Token))
                throw new TimeoutException("Concurrent writer did not publish the initial row.");
            var random = new Random(seed);
            using KvMemTableIterator iterator = table.CreateIterator();
            for (int scan = 0; scan < 10_000 && Volatile.Read(ref writerDone) == 0; scan++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                byte[]? previous = null;
                int seen = 0;
                byte[] seek = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(seek, random.Next(total));
                for (iterator.Seek(KvInternalKey.EncodeSeekKey(seek, KvInternalKey.MaxSequence));
                    iterator.Valid && seen < 2_000;
                    iterator.MoveNext(), seen++)
                {
                    byte[] key = iterator.Key.ToArray();
                    if (previous is not null)
                        Assert.True(KvInternalKey.Compare(previous, key) < 0);
                    Assert.Equal(ValueFor(KvInternalKey.UserKey(key)), iterator.Value.ToArray());
                    previous = key;
                }

                for (iterator.SeekToLast(), seen = 0; iterator.Valid && seen < 200; iterator.MovePrev(), seen++)
                    Assert.Equal(ValueFor(KvInternalKey.UserKey(iterator.Key)), iterator.Value.ToArray());
                Interlocked.Increment(ref readerScans);
                if (scan == 0)
                {
                    Assert.True(seen > 0);
                    readersObserved.Signal();
                }
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(125));

        Assert.Equal(total, table.EntryCount);
        Assert.True(Interlocked.Read(ref readerScans) > 0);
        using KvMemTableIterator final = table.CreateIterator();
        int count = 0;
        for (final.SeekToFirst(); final.Valid && count <= total; final.MoveNext())
            count++;
        Assert.Equal(total, count);
    }

    private static byte[] ValueFor(ReadOnlySpan<byte> userKey)
    {
        byte[] value = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(value, XxHash64.HashToUInt64(userKey));
        return value;
    }
}
