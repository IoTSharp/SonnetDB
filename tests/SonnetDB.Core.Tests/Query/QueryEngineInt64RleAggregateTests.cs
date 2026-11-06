using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Query.Functions.Aggregates;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Query;

public sealed class QueryEngineInt64RleAggregateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-rle-aggregates-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeRange _partialRange = new(1170, 5010);

    [Theory]
    [InlineData(BlockEncoding.None)]
    [InlineData(BlockEncoding.DeltaTimestamp)]
    public void Execute_FlushedInt64RleSinglePoint_ReturnsValueInsteadOfEncodedRunBytes(BlockEncoding timestampEncoding)
    {
        using var database = Tsdb.Open(CreateOptions(timestampEncoding, useSimd: true));
        for (int index = 0; index < 4; index++)
            database.Write(Point.Create("quality_history", TimestampAt(index),
                new Dictionary<string, string> { ["sensor"] = "1" },
                new Dictionary<string, FieldValue> { ["quality"] = FieldValue.FromLong(index < 2 ? 5 : 9) }));
        database.FlushNow();

        Assert.Equal(0L, database.MemTable.PointCount);
        using var reader = SegmentReader.Open(Assert.Single(database.ListSegments()).Path);
        var block = Assert.Single(reader.Blocks);
        Assert.Equal(BlockEncoding.Int64Rle, block.ValueEncoding);
        Assert.Equal(timestampEncoding, block.TimestampEncoding);
        Assert.Equal(18, block.ValuePayloadLength);
        ulong seriesId = Assert.Single(database.Catalog.Snapshot()).Id;
        var query = new AggregateQuery(seriesId, "quality", new TimeRange(1010, 1010), Aggregator.Sum);
        var actual = Assert.Single(database.Query.Execute(query));
        Assert.Equal(1L, actual.Count);
        Assert.Equal(5d, actual.Value);
    }

    [Theory]
    [InlineData(BlockEncoding.None, false, 0L)]
    [InlineData(BlockEncoding.None, true, 0L)]
    [InlineData(BlockEncoding.DeltaTimestamp, false, 0L)]
    [InlineData(BlockEncoding.DeltaTimestamp, true, 0L)]
    [InlineData(BlockEncoding.None, false, 500L)]
    [InlineData(BlockEncoding.None, true, 500L)]
    [InlineData(BlockEncoding.DeltaTimestamp, false, 500L)]
    [InlineData(BlockEncoding.DeltaTimestamp, true, 500L)]
    public void Execute_FlushedInt64RlePartialRange_ReturnsExpectedAggregatesAcrossReopens(
        BlockEncoding timestampEncoding, bool useSimd, long bucketSize)
    {
        var options = CreateOptions(timestampEncoding, useSimd);
        for (int reopen = 0; reopen < 3; reopen++)
        {
            using var database = Tsdb.Open(options);
            ulong seriesId = PrepareAndVerifyCompressedBlock(database, timestampEncoding, reopen);
            var expected = ExpectedBuckets(bucketSize);
            foreach (var aggregator in new[] { Aggregator.Count, Aggregator.Sum, Aggregator.Min, Aggregator.Max, Aggregator.Avg })
            {
                var query = new AggregateQuery(seriesId, "quality", _partialRange, aggregator, bucketSize);
                AssertBuckets(expected, database.Query.Execute(query).ToArray(), aggregator);
                var many = database.Query.ExecuteMany([seriesId], "quality", _partialRange, aggregator, bucketSize);
                AssertBuckets(expected, many[seriesId], aggregator);
            }
        }
    }

    [Theory]
    [InlineData(BlockEncoding.None, 0L)]
    [InlineData(BlockEncoding.DeltaTimestamp, 0L)]
    [InlineData(BlockEncoding.None, 500L)]
    [InlineData(BlockEncoding.DeltaTimestamp, 500L)]
    public void ExecuteMultiAggregate_FlushedInt64RlePartialRange_ReturnsExpectedStatisticsAcrossReopens(
        BlockEncoding timestampEncoding, long bucketSize)
    {
        var options = CreateOptions(timestampEncoding, useSimd: true);
        for (int reopen = 0; reopen < 3; reopen++)
        {
            using var database = Tsdb.Open(options);
            ulong seriesId = PrepareAndVerifyCompressedBlock(database, timestampEncoding, reopen);
            var query = new AggregateQuery(seriesId, "quality", _partialRange, Aggregator.Sum, bucketSize);
            var actual = database.Query.ExecuteMultiAggregate(query);
            var expected = ExpectedBuckets(bucketSize);
            Assert.Equal(expected.Length, actual.Count);
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index].BucketStart, actual[index].BucketStart);
                Assert.Equal(expected[index].Count, actual[index].Count);
                Assert.Equal(expected[index].Sum, actual[index].Sum);
                Assert.Equal(expected[index].Min, actual[index].Min);
                Assert.Equal(expected[index].Max, actual[index].Max);
            }
        }
    }

    [Theory]
    [InlineData(BlockEncoding.None)]
    [InlineData(BlockEncoding.DeltaTimestamp)]
    public void TryAddExtendedAggregateSketches_FlushedInt64RlePartialRange_UsesDecodedValuesAcrossReopens(
        BlockEncoding timestampEncoding)
    {
        var options = CreateOptions(timestampEncoding, useSimd: true);
        for (int reopen = 0; reopen < 3; reopen++)
        {
            using var database = Tsdb.Open(options);
            ulong seriesId = PrepareAndVerifyCompressedBlock(database, timestampEncoding, reopen);
            var accumulator = new PercentileAccumulator(0.5);
            Assert.True(database.Query.TryAddExtendedAggregateSketches(
                seriesId, "quality", _partialRange, accumulator, out long observedCount));
            Assert.Equal(385L, observedCount);
            Assert.Equal(192d, Assert.IsType<double>(accumulator.Finalize()));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TsdbOptions CreateOptions(BlockEncoding timestampEncoding, bool useSimd) => new()
    {
        RootDirectory = _root,
        UseSimdNumericAggregates = useSimd,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        FlushPolicy = new MemTableFlushPolicy { MaxPoints = 1_000_000, MaxBytes = 64 * 1024 * 1024 },
        SegmentWriterOptions = new SegmentWriterOptions
        {
            TimestampEncoding = timestampEncoding,
            ValueEncoding = BlockEncoding.DeltaValue,
        },
    };

    private static ulong PrepareAndVerifyCompressedBlock(Tsdb database, BlockEncoding timestampEncoding, int reopen)
    {
        if (reopen == 0)
        {
            for (int index = 0; index < 512; index++)
                database.Write(Point.Create("quality_history", TimestampAt(index),
                    new Dictionary<string, string> { ["sensor"] = "1" },
                    new Dictionary<string, FieldValue> { ["quality"] = FieldValue.FromLong(ValueAt(index)) }));
            database.FlushNow();
        }

        Assert.Equal(0L, database.MemTable.PointCount);
        using var reader = SegmentReader.Open(Assert.Single(database.ListSegments()).Path);
        var block = Assert.Single(reader.Blocks);
        Assert.Equal(BlockEncoding.Int64Rle, block.ValueEncoding);
        Assert.Equal(timestampEncoding, block.TimestampEncoding);
        Assert.True(block.ValuePayloadLength < 512 * sizeof(long));
        return Assert.Single(database.Catalog.Snapshot()).Id;
    }

    private static MultiAggregateBucket[] ExpectedBuckets(long bucketSize) => Enumerable.Range(17, 385)
        .GroupBy(index => bucketSize == 0 ? _partialRange.FromInclusive : TimeBucket.Floor(TimestampAt(index), bucketSize))
        .Select(group => new MultiAggregateBucket(
            group.Key,
            bucketSize == 0 ? _partialRange.ToInclusive + 1 : group.Key + bucketSize,
            group.LongCount(),
            group.Sum(index => (double)ValueAt(index)),
            group.Min(index => (double)ValueAt(index)),
            group.Max(index => (double)ValueAt(index))))
        .ToArray();

    private static void AssertBuckets(
        IReadOnlyList<MultiAggregateBucket> expected, IReadOnlyList<AggregateBucket> actual, Aggregator aggregator)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].BucketStart, actual[index].BucketStart);
            Assert.Equal(expected[index].BucketEndExclusive, actual[index].BucketEndExclusive);
            Assert.Equal(expected[index].Count, actual[index].Count);
            Assert.Equal(expected[index].ProjectValue(aggregator), actual[index].Value);
        }
    }

    private static long TimestampAt(int index) => 1000L + index * 10;

    private static long ValueAt(int index) => index < 128 ? -7 : index < 384 ? 192 : 23;
}
