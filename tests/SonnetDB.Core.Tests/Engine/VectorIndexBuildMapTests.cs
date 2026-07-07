using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="VectorIndexBuildMap.BuildForSegment"/> 单元测试：验证 I11 不变式
/// （含向量桶时缺 catalog 必须显式失败，而非返回 null 让段无索引落盘退化为暴力扫）。
/// </summary>
public sealed class VectorIndexBuildMapTests
{
    private const string Measurement = "docs";
    private const string VectorField = "embedding";

    private static MemTableSeries VectorBucket(ulong seriesId)
    {
        var bucket = new MemTableSeries(new SeriesFieldKey(seriesId, VectorField), FieldType.Vector);
        bucket.Append(1000L, FieldValue.FromVector(new[] { 1f, 0f, 0f }));
        bucket.Append(1001L, FieldValue.FromVector(new[] { 0f, 1f, 0f }));
        return bucket;
    }

    private static MemTableSeries ScalarBucket(ulong seriesId)
    {
        var bucket = new MemTableSeries(new SeriesFieldKey(seriesId, "temp"), FieldType.Float64);
        bucket.Append(1000L, FieldValue.FromDouble(21.5));
        return bucket;
    }

    [Fact]
    public void BuildForSegment_VectorBucketMissingCatalog_Throws()
    {
        var buckets = new List<MemTableSeries> { VectorBucket(1UL) };

        Assert.Throws<InvalidOperationException>(() =>
            VectorIndexBuildMap.BuildForSegment(buckets, catalog: null, measurements: null));
    }

    [Fact]
    public void BuildForSegment_VectorBucketMissingOnlyMeasurementCatalog_Throws()
    {
        var buckets = new List<MemTableSeries> { VectorBucket(1UL) };
        var catalog = new SeriesCatalog();

        Assert.Throws<InvalidOperationException>(() =>
            VectorIndexBuildMap.BuildForSegment(buckets, catalog, measurements: null));
    }

    [Fact]
    public void BuildForSegment_NoVectorBucketMissingCatalog_ReturnsNull()
    {
        var buckets = new List<MemTableSeries> { ScalarBucket(1UL) };

        var result = VectorIndexBuildMap.BuildForSegment(buckets, catalog: null, measurements: null);

        Assert.Null(result);
    }

    [Fact]
    public void BuildForSegment_VectorBucketWithCatalog_ResolvesDeclaredIndex()
    {
        var measurements = new MeasurementCatalog();
        measurements.Add(MeasurementSchema.Create(Measurement, new[]
        {
            new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
            new MeasurementColumn(
                VectorField,
                MeasurementColumnRole.Field,
                FieldType.Vector,
                3,
                VectorIndexDefinition.CreateHnsw(4, 8)),
        }));

        var catalog = new SeriesCatalog();
        ulong seriesId = catalog.GetOrAdd(Measurement, new Dictionary<string, string>
        {
            ["source"] = "a",
        }).Id;

        var buckets = new List<MemTableSeries> { VectorBucket(seriesId) };

        var result = VectorIndexBuildMap.BuildForSegment(buckets, catalog, measurements);

        Assert.NotNull(result);
        Assert.True(result!.ContainsKey(new SeriesFieldKey(seriesId, VectorField)));
    }
}
