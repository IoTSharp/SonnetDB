using System.Text;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Memory;

/// <summary>
/// <see cref="MemTable"/> 单元测试。
/// </summary>
public sealed class MemTableTests
{
    [Fact]
    public void Append_SameSeriesId_DifferentFieldName_CreatesSeparateBuckets()
    {
        var table = new MemTable();
        const ulong sid = 1UL;

        table.Append(sid, 1000L, "cpu", FieldValue.FromDouble(50.0), 1L);
        table.Append(sid, 2000L, "mem", FieldValue.FromLong(4096L), 2L);

        Assert.Equal(2, table.SeriesCount);
        Assert.Equal(2L, table.PointCount);
    }

    [Fact]
    public void Append_SameKey_DifferentFieldType_ThrowsInvalidOperationException()
    {
        var table = new MemTable();
        const ulong sid = 42UL;

        table.Append(sid, 1000L, "v", FieldValue.FromDouble(1.0), 1L);

        Assert.Throws<InvalidOperationException>(() =>
            table.Append(sid, 2000L, "v", FieldValue.FromLong(2L), 2L));
    }

    [Fact]
    public void GetBySeries_ReturnsBucketsOrderedByFieldName()
    {
        var table = new MemTable();
        const ulong sid = 10UL;

        table.Append(sid, 1000L, "z_field", FieldValue.FromDouble(1.0), 1L);
        table.Append(sid, 1000L, "a_field", FieldValue.FromDouble(2.0), 2L);
        table.Append(sid, 1000L, "m_field", FieldValue.FromDouble(3.0), 3L);

        var buckets = table.GetBySeries(sid);

        Assert.Equal(3, buckets.Count);
        Assert.Equal("a_field", buckets[0].Key.FieldName);
        Assert.Equal("m_field", buckets[1].Key.FieldName);
        Assert.Equal("z_field", buckets[2].Key.FieldName);
    }

    [Fact]
    public void GetBySeries_WrongSeriesId_ReturnsEmpty()
    {
        var table = new MemTable();

        table.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);

        var buckets = table.GetBySeries(99UL);
        Assert.Empty(buckets);
    }

    [Fact]
    public void SnapshotAll_CountMatchesSeriesCount()
    {
        var table = new MemTable();

        table.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);
        table.Append(2UL, 1000L, "v", FieldValue.FromDouble(2.0), 2L);
        table.Append(3UL, 1000L, "v", FieldValue.FromDouble(3.0), 3L);

        var snap = table.SnapshotAll();
        Assert.Equal(table.SeriesCount, snap.Count);
        Assert.Equal(3, snap.Count);
    }

    [Fact]
    public void ReplayFrom_PopulatesCorrectly()
    {
        var records = new[]
        {
            new SonnetDB.Wal.WritePointRecord(1L, 0L, 1UL, 1000L, "cpu", FieldValue.FromDouble(10.0)),
            new SonnetDB.Wal.WritePointRecord(2L, 0L, 1UL, 2000L, "cpu", FieldValue.FromDouble(20.0)),
            new SonnetDB.Wal.WritePointRecord(3L, 0L, 2UL, 1000L, "mem", FieldValue.FromLong(1024L)),
        };

        var table = new MemTable();
        int replayed = table.ReplayFrom(records);

        Assert.Equal(3, replayed);
        Assert.Equal(3L, table.PointCount);
        Assert.Equal(2, table.SeriesCount);
        Assert.Equal(1L, table.FirstLsn);
        Assert.Equal(3L, table.LastLsn);
    }

    [Fact]
    public void ReplayFrom_UpdatesIncrementalStatistics()
    {
        var records = new[]
        {
            new SonnetDB.Wal.WritePointRecord(10L, 0L, 1UL, 5000L, "cpu", FieldValue.FromDouble(10.0)),
            new SonnetDB.Wal.WritePointRecord(11L, 0L, 2UL, -100L, "ok", FieldValue.FromBool(true)),
            new SonnetDB.Wal.WritePointRecord(12L, 0L, 3UL, 8000L, "label", FieldValue.FromString("abc")),
            new SonnetDB.Wal.WritePointRecord(13L, 0L, 4UL, 3000L, "embedding", FieldValue.FromVector(new[] { 1.0f, 2.0f, 3.0f })),
        };

        var table = new MemTable();
        int replayed = table.ReplayFrom(records);

        Assert.Equal(4, replayed);
        Assert.Equal(4L, table.PointCount);
        Assert.Equal(4, table.SeriesCount);
        Assert.Equal(72L, table.EstimatedBytes);
        Assert.Equal(-100L, table.MinTimestamp);
        Assert.Equal(8000L, table.MaxTimestamp);
    }

    [Fact]
    public void ReplayFrom_EmptyInput_ReturnsZero()
    {
        var table = new MemTable();
        int replayed = table.ReplayFrom([]);

        Assert.Equal(0, replayed);
        Assert.Equal(0, table.SeriesCount);
    }

    [Fact]
    public void Reset_ClearsAllData()
    {
        var table = new MemTable();

        table.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);
        table.Append(2UL, 2000L, "v", FieldValue.FromDouble(2.0), 2L);

        table.Reset();

        Assert.Equal(0, table.SeriesCount);
        Assert.Equal(0L, table.PointCount);
        Assert.Equal(0L, table.EstimatedBytes);
        Assert.Equal(long.MaxValue, table.MinTimestamp);
        Assert.Equal(long.MinValue, table.MaxTimestamp);
        Assert.Equal(long.MinValue, table.FirstLsn);
        Assert.Equal(long.MinValue, table.LastLsn);
    }

    [Fact]
    public void TryGet_ExistingKey_ReturnsBucket()
    {
        var table = new MemTable();
        const ulong sid = 5UL;

        table.Append(sid, 1000L, "temp", FieldValue.FromDouble(25.0), 1L);

        var key = new SeriesFieldKey(sid, "temp");
        var bucket = table.TryGet(in key);

        Assert.NotNull(bucket);
        Assert.Equal(1, bucket.Count);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsNull()
    {
        var table = new MemTable();
        var key = new SeriesFieldKey(999UL, "nonexistent");

        Assert.Null(table.TryGet(in key));
    }

    [Fact]
    public void FirstLsn_LastLsn_TrackWalLsns()
    {
        var table = new MemTable();

        table.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 100L);
        table.Append(1UL, 2000L, "v", FieldValue.FromDouble(2.0), 200L);
        table.Append(1UL, 3000L, "v", FieldValue.FromDouble(3.0), 300L);

        Assert.Equal(100L, table.FirstLsn);
        Assert.Equal(300L, table.LastLsn);
    }

    [Fact]
    public async Task Append_ConcurrentAppend_MaintainsIncrementalStatistics()
    {
        var table = new MemTable();
        const int tasks = 8;
        const int pointsPerTask = 500;
        const long firstTimestamp = -1_000_000L;

        var appendTasks = Enumerable.Range(0, tasks)
            .Select(taskIndex => Task.Run(() =>
            {
                for (int i = 0; i < pointsPerTask; i++)
                {
                    long offset = taskIndex * pointsPerTask + i;
                    table.Append(
                        (ulong)(taskIndex + 1),
                        firstTimestamp + offset,
                        "v",
                        FieldValue.FromDouble(offset),
                        offset + 1);
                }
            }))
            .ToArray();

        await Task.WhenAll(appendTasks);

        long expectedCount = tasks * pointsPerTask;
        Assert.Equal(expectedCount, table.PointCount);
        Assert.Equal(tasks, table.SeriesCount);
        Assert.Equal(expectedCount * 16L, table.EstimatedBytes);
        Assert.Equal(firstTimestamp, table.MinTimestamp);
        Assert.Equal(firstTimestamp + expectedCount - 1, table.MaxTimestamp);
    }

    [Fact]
    public async Task Append_ConcurrentReadersDuringAppend_RemainLockFreeSafe()
    {
        // C10 回归：移除内部 ReaderWriterLockSlim 后，读者（SnapshotAll/TryGet/PointCount/GetBySeries）
        // 与 Append 并发仍须无锁安全，不抛 InvalidOperationException（枚举被修改的集合）等异常。
        var table = new MemTable();
        const int pointsPerSeries = 2000;
        const int seriesCount = 16;
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < pointsPerSeries; i++)
            {
                for (ulong s = 1; s <= seriesCount; s++)
                    table.Append(s, 1000L + i, "v", FieldValue.FromDouble(i), i + 1);
            }
            stop.Cancel();
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            long sink = 0;
            while (!stop.IsCancellationRequested)
            {
                // 枚举全部桶快照 + 逐 series 读取 + 读取统计量，全部应无异常。
                foreach (var bucket in table.SnapshotAll())
                    sink += bucket.Count;
                sink += table.GetBySeries(3).Count;
                sink += table.TryGet(new SeriesFieldKey(5, "v"))?.Count ?? 0;
                sink += table.PointCount;
                sink += table.EstimatedBytes;
            }
            return sink;
        })).ToArray();

        await writer;
        await Task.WhenAll(readers);

        Assert.Equal((long)pointsPerSeries * seriesCount, table.PointCount);
        Assert.Equal(seriesCount, table.SeriesCount);
    }

    [Fact]
    public void ShouldFlush_MaxBytes_ReturnsTrue()
    {
        var table = new MemTable();
        var policy = new MemTableFlushPolicy { MaxBytes = 0, MaxPoints = long.MaxValue, MaxAge = TimeSpan.MaxValue };

        table.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);

        Assert.True(table.ShouldFlush(policy));
    }

    [Fact]
    public void ShouldFlush_MaxPoints_ReturnsTrue()
    {
        var table = new MemTable();
        var policy = new MemTableFlushPolicy { MaxBytes = long.MaxValue, MaxPoints = 1, MaxAge = TimeSpan.MaxValue };

        table.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);
        table.Append(1UL, 2000L, "v", FieldValue.FromDouble(2.0), 2L);

        Assert.True(table.ShouldFlush(policy));
    }

    [Fact]
    public void ShouldFlush_MaxAge_ReturnsTrue()
    {
        var table = new MemTable();
        var policy = new MemTableFlushPolicy
        {
            MaxBytes = long.MaxValue,
            MaxPoints = long.MaxValue,
            MaxAge = TimeSpan.Zero
        };

        Assert.True(table.ShouldFlush(policy));
    }

    [Fact]
    public void ShouldFlush_NoThresholdMet_ReturnsFalse()
    {
        var table = new MemTable();
        var policy = new MemTableFlushPolicy
        {
            MaxBytes = long.MaxValue,
            MaxPoints = long.MaxValue,
            MaxAge = TimeSpan.MaxValue
        };

        table.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);

        Assert.False(table.ShouldFlush(policy));
    }

    [Fact]
    public void MinMaxTimestamp_AggregatesAcrossBuckets()
    {
        var table = new MemTable();

        table.Append(1UL, 5000L, "a", FieldValue.FromDouble(1.0), 1L);
        table.Append(2UL, 1000L, "b", FieldValue.FromDouble(2.0), 2L);
        table.Append(3UL, 9000L, "c", FieldValue.FromDouble(3.0), 3L);

        Assert.Equal(1000L, table.MinTimestamp);
        Assert.Equal(9000L, table.MaxTimestamp);
    }

    [Fact]
    public void EstimatedBytes_SumOfAllBuckets()
    {
        var table = new MemTable();

        // 2 points in Float64 bucket = 2 * 16 = 32 bytes
        table.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);
        table.Append(1UL, 2000L, "v", FieldValue.FromDouble(2.0), 2L);

        Assert.Equal(32L, table.EstimatedBytes);
    }

    [Fact]
    public void EstimatedBytes_StringAndNonStringBuckets_MatchesLegacyFormula()
    {
        var table = new MemTable();
        string[] strings = ["", "ok", "温度"];

        long expected = 0;
        for (int i = 0; i < strings.Length; i++)
        {
            table.Append(1UL, i, "label", FieldValue.FromString(strings[i]), i + 1);
            expected += 16L + Encoding.UTF8.GetByteCount(strings[i]);
        }

        table.Append(1UL, 10L, "value", FieldValue.FromDouble(1.5), 10L);
        expected += 16L;
        table.Append(1UL, 11L, "ok", FieldValue.FromBool(true), 11L);
        expected += 9L;
        table.Append(1UL, 12L, "embedding", FieldValue.FromVector(new[] { 1.0f, 2.0f }), 12L);
        expected += 24L;
        table.Append(1UL, 13L, "location", FieldValue.FromGeoPoint(31.23, 121.47), 13L);
        expected += 24L;

        Assert.Equal(7L, table.PointCount);
        Assert.Equal(expected, table.EstimatedBytes);
    }

    [Fact]
    public void Append_NullFieldName_ThrowsAndDoesNotChangeIncrementalStatistics()
    {
        var table = new MemTable();
        table.Append(1UL, 1000L, "label", FieldValue.FromString("ok"), 1L);
        long expectedBytes = 16L + Encoding.UTF8.GetByteCount("ok");

        Assert.Throws<ArgumentNullException>(() =>
            table.Append(1UL, 2000L, null!, FieldValue.FromString("bad"), 2L));

        Assert.Equal(1L, table.PointCount);
        Assert.Equal(1, table.SeriesCount);
        Assert.Equal(expectedBytes, table.EstimatedBytes);
        Assert.Equal(1000L, table.MinTimestamp);
        Assert.Equal(1000L, table.MaxTimestamp);
    }
}
