using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine.Compaction;

/// <summary>
/// <see cref="SegmentCompactor"/> 单元测试：验证多段合并逻辑、时间戳排序、FieldType 冲突检测。
/// </summary>
public sealed class SegmentCompactorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });
    private readonly SegmentCompactor _compactor = new(new SegmentWriterOptions { FsyncOnCommit = false });
    private readonly SegmentReaderOptions _readerOpts = new() { VerifyIndexCrc = true, VerifyBlockCrc = true };

    public SegmentCompactorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, TsdbPaths.SegmentsDirName));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string SegPath(long segId) => TsdbPaths.SegmentPath(_tempDir, segId);

    private SegmentReader WriteSegment(long segId, ulong seriesId, string field,
        FieldType fieldType, long startTs, int count, long step = 1)
    {
        var mt = new MemTable();
        for (int i = 0; i < count; i++)
        {
            long ts = startTs + i * step;
            FieldValue v = fieldType switch
            {
                FieldType.Float64 => FieldValue.FromDouble(ts),
                FieldType.Int64 => FieldValue.FromLong(ts),
                FieldType.Boolean => FieldValue.FromBool(i % 2 == 0),
                FieldType.String => FieldValue.FromString($"s{ts}"),
                _ => throw new ArgumentOutOfRangeException(),
            };
            mt.Append(seriesId, ts, field, v, i + 1L);
        }
        string path = SegPath(segId);
        _writer.WriteFrom(mt, segId, path);
        return SegmentReader.Open(path, _readerOpts);
    }

    private SegmentReader WriteVectorSegment(
        long segId,
        ulong seriesId,
        string field,
        params (long Timestamp, float[] Vector)[] points)
    {
        var mt = new MemTable();
        long lsn = 1L;
        foreach (var point in points)
            mt.Append(seriesId, point.Timestamp, field, FieldValue.FromVector(point.Vector), lsn++);

        string path = SegPath(segId);
        _writer.WriteFrom(mt, segId, path);
        return SegmentReader.Open(path, _readerOpts);
    }

    // ── 2 段 × 100 点 → 1 段 200 点，时间戳升序 ─────────────────────────────

    [Fact]
    public void Execute_TwoSegments100Points_Merges200PointsInOrder()
    {
        using var r1 = WriteSegment(1, 0xABCDUL, "val", FieldType.Float64, startTs: 1000, count: 100, step: 2);
        using var r2 = WriteSegment(2, 0xABCDUL, "val", FieldType.Float64, startTs: 1001, count: 100, step: 2);

        var plan = new CompactionPlan(0, new long[] { 1, 2 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2 };
        string outPath = Path.Combine(_tempDir, "out.SDBSEG");

        var result = _compactor.Execute(plan, readerDict, 100, outPath);

        Assert.Equal(100L, result.NewSegmentId);
        Assert.Equal(outPath, result.NewSegmentPath);
        Assert.Equal([1L, 2L], result.RemovedSegmentIds);
        Assert.Equal(1, result.OutputBlockCount);
        Assert.True(result.OutputBytes > 0);

        // 验证内容
        using var merged = SegmentReader.Open(outPath, _readerOpts);
        var points = merged.DecodeBlock(merged.Blocks[0]);
        Assert.Equal(200, points.Length);

        // 时间戳升序
        for (int i = 1; i < points.Length; i++)
            Assert.True(points[i].Timestamp >= points[i - 1].Timestamp);
    }

    // ── 跨 series：3 段，每段含 2 series → 输出按 (SeriesId, FieldName) 升序 ─

    [Fact]
    public void Execute_MultiSeries_OutputSortedBySeriesIdAndField()
    {
        // Series A (ID=1) 和 Series B (ID=2)，每个段都包含两个 series
        void WriteMultiSeries(long segId, long tsStart, SegmentWriter w)
        {
            var mt = new MemTable();
            for (int i = 0; i < 10; i++)
            {
                mt.Append(1UL, tsStart + i, "v", FieldValue.FromDouble(i), i + 1L);
                mt.Append(2UL, tsStart + i, "v", FieldValue.FromDouble(i * 10), i + 100L);
            }
            w.WriteFrom(mt, segId, SegPath(segId));
        }

        WriteMultiSeries(1, 1000, _writer);
        WriteMultiSeries(2, 1010, _writer);
        WriteMultiSeries(3, 1020, _writer);

        using var r1 = SegmentReader.Open(SegPath(1), _readerOpts);
        using var r2 = SegmentReader.Open(SegPath(2), _readerOpts);
        using var r3 = SegmentReader.Open(SegPath(3), _readerOpts);

        var plan = new CompactionPlan(0, new long[] { 1, 2, 3 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2, [3] = r3 };
        string outPath = Path.Combine(_tempDir, "out_multi.SDBSEG");

        var result = _compactor.Execute(plan, readerDict, 99, outPath);

        Assert.Equal(2, result.OutputBlockCount); // series1/v + series2/v

        using var merged = SegmentReader.Open(outPath, _readerOpts);
        Assert.Equal(2, merged.BlockCount);

        // SeriesId 升序
        Assert.Equal(1UL, merged.Blocks[0].SeriesId);
        Assert.Equal(2UL, merged.Blocks[1].SeriesId);

        // 每个 block 30 点（3 段 × 10 点）
        Assert.Equal(30, merged.Blocks[0].Count);
        Assert.Equal(30, merged.Blocks[1].Count);
    }

    // ── 同 timestamp 多源 → v1 全部保留 ─────────────────────────────────────

    [Fact]
    public void Execute_SameTimestamp_AllPointsPreserved()
    {
        // 两个段，每个段包含 ts=1000 的同字段同 series 数据点
        using var r1 = WriteSegment(1, 1UL, "val", FieldType.Int64, startTs: 1000, count: 5);
        using var r2 = WriteSegment(2, 1UL, "val", FieldType.Int64, startTs: 1000, count: 5);

        var plan = new CompactionPlan(0, new long[] { 1, 2 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2 };
        string outPath = Path.Combine(_tempDir, "out_dup.SDBSEG");

        var result = _compactor.Execute(plan, readerDict, 50, outPath);

        using var merged = SegmentReader.Open(outPath, _readerOpts);
        var points = merged.DecodeBlock(merged.Blocks[0]);

        // v1：保留全部点（5+5=10），不去重
        Assert.Equal(10, points.Length);
    }

    // ── FieldType 冲突 → 抛 InvalidOperationException ─────────────────────

    [Fact]
    public void Execute_FieldTypeConflict_ThrowsInvalidOperation()
    {
        using var r1 = WriteSegment(1, 1UL, "val", FieldType.Float64, startTs: 1000, count: 5);
        using var r2 = WriteSegment(2, 1UL, "val", FieldType.Int64, startTs: 2000, count: 5);

        var plan = new CompactionPlan(0, new long[] { 1, 2 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2 };
        string outPath = Path.Combine(_tempDir, "out_conflict.SDBSEG");

        Assert.Throws<InvalidOperationException>(() =>
            _compactor.Execute(plan, readerDict, 50, outPath));
    }

    [Fact]
    public void Execute_WithCanceledToken_ThrowsOperationCanceledAndDoesNotWriteOutput()
    {
        using var r1 = WriteSegment(1, 1UL, "val", FieldType.Float64, startTs: 1000, count: 5);
        using var r2 = WriteSegment(2, 1UL, "val", FieldType.Float64, startTs: 2000, count: 5);

        var plan = new CompactionPlan(0, new long[] { 1, 2 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2 };
        string outPath = Path.Combine(_tempDir, "out_canceled.SDBSEG");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _compactor.Execute(plan, readerDict, 50, outPath, cancellationToken: cts.Token));
        Assert.False(File.Exists(outPath));
    }

    // ── 4 种 FieldType round-trip ────────────────────────────────────────────

    [Theory]
    [InlineData(FieldType.Float64)]
    [InlineData(FieldType.Int64)]
    [InlineData(FieldType.Boolean)]
    [InlineData(FieldType.String)]
    public void Execute_AllFieldTypes_RoundTrip(FieldType fieldType)
    {
        using var r1 = WriteSegment(1, 1UL, "f", fieldType, startTs: 1000, count: 20);
        using var r2 = WriteSegment(2, 1UL, "f", fieldType, startTs: 2000, count: 20);

        var plan = new CompactionPlan(0, new long[] { 1, 2 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2 };
        string outPath = Path.Combine(_tempDir, $"out_{fieldType}.SDBSEG");

        var result = _compactor.Execute(plan, readerDict, 200, outPath);
        Assert.Equal(1, result.OutputBlockCount);

        using var merged = SegmentReader.Open(outPath, _readerOpts);
        var points = merged.DecodeBlock(merged.Blocks[0]);
        Assert.Equal(40, points.Length);
        Assert.Equal(fieldType, merged.Blocks[0].FieldType);

        // 时间戳升序
        for (int i = 1; i < points.Length; i++)
            Assert.True(points[i].Timestamp >= points[i - 1].Timestamp);
    }

    [Fact]
    public void Execute_VectorFieldWithHnswIndex_EmbedsIndexForCompactedSegment()
    {
        const string measurement = "docs";
        const string fieldName = "embedding";

        var measurements = new MeasurementCatalog();
        measurements.Add(MeasurementSchema.Create(measurement, new[]
        {
            new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
            new MeasurementColumn(
                fieldName,
                MeasurementColumnRole.Field,
                FieldType.Vector,
                3,
                VectorIndexDefinition.CreateHnsw(4, 8)),
        }));

        var seriesCatalog = new SeriesCatalog();
        ulong seriesId = seriesCatalog.GetOrAdd(measurement, new Dictionary<string, string>
        {
            ["source"] = "a",
        }).Id;

        using var r1 = WriteVectorSegment(
            1,
            seriesId,
            fieldName,
            (1000L, new[] { 1f, 0f, 0f }),
            (1002L, new[] { 0f, 1f, 0f }));
        using var r2 = WriteVectorSegment(
            2,
            seriesId,
            fieldName,
            (1001L, new[] { 0.9f, 0.1f, 0f }),
            (1003L, new[] { -1f, 0f, 0f }));

        var plan = new CompactionPlan(0, new long[] { 1, 2 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2 };
        string outPath = Path.Combine(_tempDir, "out_vector.SDBSEG");

        _compactor.Execute(
            plan,
            readerDict,
            300,
            outPath,
            seriesCatalog: seriesCatalog,
            measurementCatalog: measurements);

        Assert.False(File.Exists(TsdbPaths.VectorIndexPathForSegment(outPath)));

        using var merged = SegmentReader.Open(outPath, _readerOpts);
        var block = Assert.Single(merged.Blocks);
        Assert.False(merged.VectorIndexOffsetsLoaded);
        Assert.Equal(0, merged.VectorIndexCacheEntryCountForSegment);

        Assert.True(merged.TryGetVectorIndexReader(block, out var vectorIndex));
        Assert.Equal(block.Count, vectorIndex.Count);
        Assert.True(merged.VectorIndexOffsetsLoaded);
        Assert.True(merged.VectorIndexOffsetsEmbedded);
        Assert.Equal(1, merged.VectorIndexCacheEntryCountForSegment);

        var data = merged.ReadBlock(block);
        var timestamps = BlockDecoder.DecodeTimestamps(block, data.TimestampPayload);
        var hits = vectorIndex.Search([1f, 0f, 0f], data.ValuePayload, timestamps, 2, KnnMetric.Cosine);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1000L, hits[0].Timestamp);
        Assert.Equal(1001L, hits[1].Timestamp);
    }
}
