using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using SonnetDB.Buffers;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.IO;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Storage.Segments;

/// <summary>
/// PR #58 c：<see cref="BlockEncoding.VectorRaw"/> + Segment v3 单元测试。
/// 覆盖：Raw 编解码 round-trip、SegmentWriter→SegmentReader 端到端、维度不一致的写入校验、
/// 段格式版本号升级到 v3 及对 v2 的只读兼容入口。
/// </summary>
public sealed class VectorSegmentTests : IDisposable
{
    private readonly string _tempDir;

    public VectorSegmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── ValuePayloadCodec：VectorRaw 编解码 ─────────────────────────────────

    [Fact]
    public void Vector_Measure_EqualsCountTimesDimTimesFour()
    {
        var points = MakeVectorPoints(dim: 4, count: 3, seed: 1);
        int measured = ValuePayloadCodec.MeasureValuePayload(FieldType.Vector, points);
        Assert.Equal(3 * 4 * sizeof(float), measured);
    }

    [Fact]
    public void Vector_WritePayload_ProducesLittleEndianFloat32Sequence()
    {
        float[][] vectors = [[1f, 2f, 3f], [4f, 5f, 6f]];
        var points = MakeVectorPoints(vectors);

        int len = ValuePayloadCodec.MeasureValuePayload(FieldType.Vector, points);
        byte[] buf = new byte[len];
        ValuePayloadCodec.WritePayload(FieldType.Vector, points, buf);

        Assert.Equal(2 * 3 * sizeof(float), len);
        for (int p = 0; p < 2; p++)
        {
            for (int i = 0; i < 3; i++)
            {
                int offset = (p * 3 + i) * sizeof(float);
                int rawBits = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(offset, 4));
                float read = BitConverter.Int32BitsToSingle(rawBits);
                Assert.Equal(vectors[p][i], read);
            }
        }
    }

    [Fact]
    public void Vector_WritePayload_DimensionMismatch_Throws()
    {
        float[][] vectors = [[1f, 2f, 3f], [4f, 5f]]; // 第二个 dim=2，与首个 dim=3 不一致
        var points = MakeVectorPoints(vectors);
        Assert.Throws<InvalidOperationException>(() =>
            ValuePayloadCodec.MeasureValuePayload(FieldType.Vector, points));
    }

    // ── SegmentWriter / SegmentReader：端到端 round-trip ───────────────────

    [Fact]
    public void Segment_WriteAndRead_VectorBlock_RoundTripPreservesAllValues()
    {
        const int dim = 4;
        const int count = 5;
        ulong seriesId = 9001UL;
        const string fieldName = "embedding";

        var mt = new MemTable();
        var expected = new List<DataPoint>(count);
        long lsn = 1L;
        for (int i = 0; i < count; i++)
        {
            float[] vec = new float[dim];
            for (int d = 0; d < dim; d++)
                vec[d] = i * 10f + d * 0.5f;
            long ts = 1_000L + i * 10L;
            mt.Append(seriesId, ts, fieldName, FieldValue.FromVector(vec), lsn++);
            expected.Add(new DataPoint(ts, FieldValue.FromVector(vec)));
        }

        string path = Path.Combine(_tempDir, "vector.SDBSEG");
        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        writer.WriteFrom(mt, segmentId: 42L, path);

        using var reader = SegmentReader.Open(path);
        Assert.Equal(1, reader.BlockCount);

        var blocks = reader.FindBySeries(seriesId);
        Assert.Single(blocks);
        var block = blocks[0];

        Assert.Equal(FieldType.Vector, block.FieldType);
        Assert.Equal(BlockEncoding.VectorRaw, block.ValueEncoding);
        Assert.Equal(count, block.Count);
        Assert.Equal(count * dim * sizeof(float), block.ValuePayloadLength);

        DataPoint[] decoded = reader.DecodeBlock(block);
        Assert.Equal(count, decoded.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(expected[i].Timestamp, decoded[i].Timestamp);
            Assert.True(expected[i].Value.AsVector().Span.SequenceEqual(decoded[i].Value.AsVector().Span));
        }
    }

    [Fact]
    public void Segment_WriteWithHnswVectorIndex_EmbedsIndexAndReaderLoadsIt()
    {
        ulong seriesId = 4242UL;
        const string fieldName = "embedding";

        var mt = new MemTable();
        long lsn = 1L;
        for (int i = 0; i < 8; i++)
            mt.Append(seriesId, 1_000L + i, fieldName, FieldValue.FromVector(new[] { (float)i, 0f, 0f }), lsn++);

        string path = Path.Combine(_tempDir, "vector-index.SDBSEG");
        var vectorIndexes = new Dictionary<SeriesFieldKey, VectorIndexDefinition>
        {
            [new SeriesFieldKey(seriesId, fieldName)] = VectorIndexDefinition.CreateHnsw(4, 8),
        };

        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        writer.WriteFrom(mt, segmentId: 7L, path, vectorIndexes);

        string sidecarPath = TsdbPaths.VectorIndexPathForSegment(path);
        Assert.False(File.Exists(sidecarPath));

        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.FindBySeries(seriesId));
        Assert.True(reader.TryGetVectorIndexReader(block, out var vectorIndex));
        Assert.True(reader.VectorIndexOffsetsEmbedded);
        Assert.Equal(block.Index, vectorIndex.BlockIndex);
        Assert.Equal(8, vectorIndex.Count);
        Assert.Equal(3, vectorIndex.Dimension);
    }

    [Fact]
    public void Segment_WriteWithHnswVectorIndex_EmbedsPersistentSonnetDbVectorBlobManifest()
    {
        ulong seriesId = 4243UL;
        const string fieldName = "embedding";

        var mt = new MemTable();
        long lsn = 1L;
        for (int i = 0; i < 8; i++)
            mt.Append(seriesId, 1_000L + i, fieldName, FieldValue.FromVector(new[] { (float)i, 0f, 0f }), lsn++);

        string path = Path.Combine(_tempDir, "vector-index-blob.SDBSEG");
        var vectorIndexes = new Dictionary<SeriesFieldKey, VectorIndexDefinition>
        {
            [new SeriesFieldKey(seriesId, fieldName)] = VectorIndexDefinition.CreateHnsw(4, 8),
        };

        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        writer.WriteFrom(mt, segmentId: 17L, path, vectorIndexes);

        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.FindBySeries(seriesId));
        var manifest = reader.VectorIndexManifest;
        var metadata = Assert.Single(manifest).Value;

        Assert.Equal(block.Index, metadata.BlockIndex);
        Assert.True(metadata.HasPersistentBlob);
        Assert.True(metadata.CanRebuildFromBlockPayload);
        Assert.True(metadata.BlobOffset > 0);
        Assert.True(metadata.BlobLength > block.ValuePayloadLength);
        Assert.NotEqual(0u, metadata.BlobCrc32);
        Assert.True(reader.TryGetVectorIndexReader(block, out var vectorIndex));
        Assert.Equal(metadata.Count, vectorIndex.Count);
        Assert.Equal(metadata.Dimension, vectorIndex.Dimension);
    }

    [Fact]
    public void Segment_WriteWithIvfVectorIndex_EmbedsRebuildOnlyManifest()
    {
        ulong seriesId = 4244UL;
        const string fieldName = "embedding";

        var mt = new MemTable();
        long lsn = 1L;
        for (int i = 0; i < 80; i++)
            mt.Append(seriesId, 1_000L + i, fieldName, FieldValue.FromVector(new[] { (float)i, 1f, 0f }), lsn++);

        string path = Path.Combine(_tempDir, "vector-index-ivf.SDBSEG");
        var vectorIndexes = new Dictionary<SeriesFieldKey, VectorIndexDefinition>
        {
            [new SeriesFieldKey(seriesId, fieldName)] = VectorIndexDefinition.CreateIvfFlat(8, 8, 10),
        };

        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        writer.WriteFrom(mt, segmentId: 18L, path, vectorIndexes);

        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.FindBySeries(seriesId));
        var metadata = Assert.Single(reader.VectorIndexManifest).Value;

        Assert.Equal((int)VectorIndexKind.IvfFlat, metadata.IndexKind);
        Assert.False(metadata.HasPersistentBlob);
        Assert.True(metadata.CanRebuildFromBlockPayload);
        Assert.Equal(8, metadata.M);
        Assert.Equal(8, metadata.Ef);
        Assert.Equal(10, metadata.Extra1);
        Assert.True(reader.TryGetVectorIndexReader(block, out var vectorIndex));
        Assert.Equal(metadata.Count, vectorIndex.Count);
        Assert.Equal(metadata.Dimension, vectorIndex.Dimension);
    }

    [Fact]
    public void SegmentReader_Open_WithEmbeddedHnswIndex_DoesNotLoadVectorIndexUntilTryGet()
    {
        ulong seriesId = 5252UL;
        const string fieldName = "embedding";

        var mt = new MemTable();
        long lsn = 1L;
        for (int i = 0; i < 10; i++)
            mt.Append(seriesId, 2_000L + i, fieldName, FieldValue.FromVector(new[] { (float)i, 1f, 0f }), lsn++);

        string path = Path.Combine(_tempDir, "vector-index-lazy.SDBSEG");
        var vectorIndexes = new Dictionary<SeriesFieldKey, VectorIndexDefinition>
        {
            [new SeriesFieldKey(seriesId, fieldName)] = VectorIndexDefinition.CreateHnsw(4, 8),
        };

        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        writer.WriteFrom(mt, segmentId: 8L, path, vectorIndexes);

        using var reader = SegmentReader.Open(path, new SegmentReaderOptions
        {
            VectorIndexCacheMaxBytes = 1024 * 1024,
        });

        Assert.False(File.Exists(TsdbPaths.VectorIndexPathForSegment(path)));
        Assert.False(reader.VectorIndexOffsetsLoaded);
        Assert.Equal(0, reader.VectorIndexCacheEntryCountForSegment);
        Assert.Equal(0L, reader.VectorIndexCacheCurrentBytesForSegment);

        var block = Assert.Single(reader.FindBySeries(seriesId));
        Assert.True(reader.TryGetVectorIndexReader(block, out var vectorIndex));

        Assert.True(reader.VectorIndexOffsetsLoaded);
        Assert.True(reader.VectorIndexOffsetsEmbedded);
        Assert.Equal(block.Index, vectorIndex.BlockIndex);
        Assert.Equal(1, reader.VectorIndexCacheEntryCountForSegment);
        Assert.True(reader.VectorIndexCacheCurrentBytesForSegment > 0);
    }

    [Fact]
    public void TryGetVectorIndex_WithManyBlocks_EvictsWithinMemoryBudget()
    {
        const string fieldName = "embedding";
        const long BudgetBytes = 16384L;

        var mt = new MemTable();
        var vectorIndexes = new Dictionary<SeriesFieldKey, VectorIndexDefinition>();
        long lsn = 1L;
        for (int series = 0; series < 12; series++)
        {
            ulong seriesId = (ulong)(10_000 + series);
            vectorIndexes[new SeriesFieldKey(seriesId, fieldName)] = VectorIndexDefinition.CreateHnsw(4, 8);
            for (int point = 0; point < 24; point++)
            {
                mt.Append(
                    seriesId,
                    3_000L + point,
                    fieldName,
                    FieldValue.FromVector(new[] { (float)series, (float)point, 1f }),
                    lsn++);
            }
        }

        string path = Path.Combine(_tempDir, "vector-index-budget.SDBSEG");
        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        writer.WriteFrom(mt, segmentId: 9L, path, vectorIndexes);

        using var reader = SegmentReader.Open(path, new SegmentReaderOptions
        {
            VectorIndexCacheMaxBytes = BudgetBytes,
        });

        foreach (var block in reader.Blocks)
        {
            Assert.True(reader.TryGetVectorIndexReader(block, out _));
            Assert.True(
                reader.VectorIndexCacheCurrentBytesForSegment <= BudgetBytes,
                $"Vector index cache exceeded budget: {reader.VectorIndexCacheCurrentBytesForSegment} > {BudgetBytes}.");
        }

        Assert.True(reader.VectorIndexCacheEntryCountForSegment > 0);
        Assert.True(reader.VectorIndexCacheCurrentBytesForSegment <= BudgetBytes);
    }

    // ── #223：度量（metric）与 efConstruction 贯通到段级向量索引 ────────────────

    [Fact]
    public void Segment_HnswWithL2Metric_PersistsMetricAndEfConstruction_ReaderReportsL2()
    {
        ulong seriesId = 4245UL;
        const string fieldName = "embedding";

        var mt = new MemTable();
        long lsn = 1L;
        for (int i = 0; i < 8; i++)
            mt.Append(seriesId, 1_000L + i, fieldName, FieldValue.FromVector(new[] { (float)i, 0f, 0f }), lsn++);

        string path = Path.Combine(_tempDir, "vector-index-l2.SDBSEG");
        var vectorIndexes = new Dictionary<SeriesFieldKey, VectorIndexDefinition>
        {
            // ef=8、efConstruction=64、metric=L2：全部要贯通到 SDBVIDX + blob。
            [new SeriesFieldKey(seriesId, fieldName)] =
                VectorIndexDefinition.CreateHnsw(4, 8, SonnetDB.Query.KnnMetric.L2, efConstruction: 64),
        };

        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        writer.WriteFrom(mt, segmentId: 71L, path, vectorIndexes);

        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.FindBySeries(seriesId));
        var metadata = Assert.Single(reader.VectorIndexManifest).Value;

        // SDBVIDX record 持久化了 metric 与 efConstruction。
        Assert.Equal((int)SonnetDB.Query.KnnMetric.L2, metadata.Metric);
        Assert.Equal(64, metadata.EfConstruction);

        // reader adapter 报告的建图度量是 L2（取自持久化 blob 头）。
        Assert.True(reader.TryGetVectorIndexReader(block, out var vectorIndex));
        Assert.Equal(SonnetDB.Query.KnnMetric.L2, vectorIndex.Metric);
    }

    [Fact]
    public void Segment_IvfWithInnerProductMetric_RebuildFromPayloadReportsInnerProduct()
    {
        ulong seriesId = 4246UL;
        const string fieldName = "embedding";

        var mt = new MemTable();
        long lsn = 1L;
        for (int i = 0; i < 80; i++)
            mt.Append(seriesId, 1_000L + i, fieldName, FieldValue.FromVector(new[] { (float)i, 1f, 0f }), lsn++);

        string path = Path.Combine(_tempDir, "vector-index-ivf-ip.SDBSEG");
        var vectorIndexes = new Dictionary<SeriesFieldKey, VectorIndexDefinition>
        {
            [new SeriesFieldKey(seriesId, fieldName)] =
                VectorIndexDefinition.CreateIvfFlat(8, 8, 10, SonnetDB.Query.KnnMetric.InnerProduct),
        };

        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        writer.WriteFrom(mt, segmentId: 72L, path, vectorIndexes);

        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.FindBySeries(seriesId));
        var metadata = Assert.Single(reader.VectorIndexManifest).Value;

        // IVF 无持久化 blob，靠 SDBVIDX 持久化的 metric 从 payload 重建为 InnerProduct（否则退化为 cosine，I7）。
        Assert.False(metadata.HasPersistentBlob);
        Assert.Equal((int)SonnetDB.Query.KnnMetric.InnerProduct, metadata.Metric);
        Assert.True(reader.TryGetVectorIndexReader(block, out var vectorIndex));
        Assert.Equal(SonnetDB.Query.KnnMetric.InnerProduct, vectorIndex.Metric);
    }

    [Fact]
    public void Segment_L2Index_ServesL2Search_ButCosineQueryReturnsEmptyFromAnn()
    {
        ulong seriesId = 4247UL;
        const string fieldName = "embedding";

        var mt = new MemTable();
        long lsn = 1L;
        // 沿 x 轴排布，[i,0,0]；L2 最近邻语义明确。
        for (int i = 0; i < 16; i++)
            mt.Append(seriesId, 1_000L + i, fieldName, FieldValue.FromVector(new[] { (float)i, 0f, 0f }), lsn++);

        string path = Path.Combine(_tempDir, "vector-index-l2-search.SDBSEG");
        var vectorIndexes = new Dictionary<SeriesFieldKey, VectorIndexDefinition>
        {
            [new SeriesFieldKey(seriesId, fieldName)] =
                VectorIndexDefinition.CreateHnsw(8, 16, SonnetDB.Query.KnnMetric.L2, efConstruction: 64),
        };
        new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false })
            .WriteFrom(mt, segmentId: 73L, path, vectorIndexes);

        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.FindBySeries(seriesId));
        Assert.True(reader.TryGetVectorIndexReader(block, out var vectorIndex));

        var data = reader.ReadBlock(block);
        var timestamps = new long[16];
        for (int i = 0; i < 16; i++) timestamps[i] = 1_000L + i;
        float[] query = { 3f, 0f, 0f };

        // L2 查询与 L2 索引度量一致 → ANN 返回命中，最近邻是索引 3。
        var l2Hits = vectorIndex.Search(query, data.ValuePayload, timestamps, resultLimit: 3,
            SonnetDB.Query.KnnMetric.L2);
        Assert.NotEmpty(l2Hits);
        Assert.Equal(3, l2Hits[0].PointIndex);

        // Cosine 查询与 L2 索引度量不一致 → 适配器直接返回空（I7：由上层回退暴力扫描）。
        var cosineHits = vectorIndex.Search(query, data.ValuePayload, timestamps, resultLimit: 3,
            SonnetDB.Query.KnnMetric.Cosine);
        Assert.Empty(cosineHits);
    }

    // ── Segment 文件头：版本号升级到 v6 + 历史版本只读兼容 ──────────────────

    [Fact]
    public void TsdbMagic_SegmentFormatVersion_IsSix()
        => Assert.Equal(6, TsdbMagic.SegmentFormatVersion);

    [Fact]
    public void TsdbMagic_SupportedSegmentFormatVersions_ContainsV2ThroughV6()
    {
        int[] supported = TsdbMagic.SupportedSegmentFormatVersions.ToArray();
        Assert.Contains(2, supported);
        Assert.Contains(3, supported);
        Assert.Contains(4, supported);
        Assert.Contains(5, supported);
        Assert.Contains(6, supported);
    }

    [Fact]
    public void SegmentHeader_IsCompatibleForRead_AcceptsV2ThroughV6_RejectsOthers()
    {
        var h = SegmentHeader.CreateNew(1L);

        for (int version = 2; version <= 6; version++)
        {
            h.FormatVersion = version;
            Assert.True(h.IsCompatibleForRead());
        }

        h.FormatVersion = 1;
        Assert.False(h.IsCompatibleForRead());

        h.FormatVersion = 99;
        Assert.False(h.IsCompatibleForRead());
    }

    [Fact]
    public void SegmentReader_Open_AcceptsV5SegmentWithoutEmbeddedExtensions()
    {
        // 写一个 VECTOR 段且不传入 HNSW 定义，使 v6 extension area 为空；
        // 再把文件里的版本字段改为 v5，验证当前 Reader 仍能读取旧主格式。
        ulong seriesId = 7777UL;
        var mt = new MemTable();
        long lsn = 1L;
        for (int i = 0; i < 4; i++)
            mt.Append(seriesId, 1_000L + i, "embedding", FieldValue.FromVector(new[] { (float)i, 0f, 1f }), lsn++);

        string path = Path.Combine(_tempDir, "v5-compatible.SDBSEG");
        new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false }).WriteFrom(mt, segmentId: 1L, path);
        RewriteSegmentVersion(path, 5);

        using var reader = SegmentReader.Open(path);
        Assert.Equal(1, reader.BlockCount);
        var blocks = reader.FindBySeries(seriesId);
        var decoded = reader.DecodeBlock(blocks[0]);
        Assert.Equal(4, decoded.Length);
        Assert.Equal(FieldType.Vector, blocks[0].FieldType);
    }

    [Fact]
    public void SegmentReader_Open_AcceptsEmptyV4Segment()
    {
        string path = Path.Combine(_tempDir, "v4-empty.SDBSEG");
        new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false })
            .WriteFrom(new MemTable(), segmentId: 2L, path);
        RewriteSegmentVersion(path, 4);

        using var reader = SegmentReader.Open(path);

        Assert.Equal(0, reader.BlockCount);
        Assert.Empty(reader.Blocks);
    }

    [Fact]
    public void SegmentReader_Open_AcceptsNonEmptyV4Segment()
    {
        string path = Path.Combine(_tempDir, "v4-float64.SDBSEG");
        WriteLegacyV4Float64Segment(path);

        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.FindBySeriesAndField(8080UL, "v"));
        var decoded = reader.DecodeBlock(block);

        Assert.Equal(FormatSizes.LegacyBlockHeaderSizeV4, block.HeaderSize);
        Assert.Single(decoded);
        Assert.Equal(1234L, decoded[0].Timestamp);
        Assert.Equal(42.5d, decoded[0].Value.AsDouble(), precision: 10);
    }

    [Fact]
    public void SegmentReader_TryGetVectorIndex_WithLegacyV5Sidecar_IgnoresOldFormat()
    {
        ulong seriesId = 6060UL;
        const string fieldName = "embedding";
        var mt = new MemTable();
        var points = new DataPoint[8];
        long lsn = 1L;
        for (int i = 0; i < points.Length; i++)
        {
            var value = FieldValue.FromVector(new[] { (float)i, 0f, 0f });
            mt.Append(seriesId, 4_000L + i, fieldName, value, lsn++);
            points[i] = new DataPoint(4_000L + i, value);
        }

        string path = Path.Combine(_tempDir, "legacy-vector-sidecar.SDBSEG");
        new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false })
            .WriteFrom(mt, segmentId: 3L, path);
        RewriteSegmentVersion(path, 5);

        byte[] legacyHeader = new byte[32];
        "SDBVIDX1"u8.CopyTo(legacyHeader);
        BinaryPrimitives.WriteInt32LittleEndian(legacyHeader.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(legacyHeader.AsSpan(12, 4), 32);
        BinaryPrimitives.WriteInt32LittleEndian(legacyHeader.AsSpan(16, 4), 0);
        File.WriteAllBytes(TsdbPaths.VectorIndexPathForSegment(path), legacyHeader);

        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.FindBySeries(seriesId));

        Assert.False(reader.TryGetVectorIndexReader(block, out _));
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────

    private static ReadOnlyMemory<DataPoint> MakeVectorPoints(int dim, int count, int seed)
    {
        var arr = new DataPoint[count];
        for (int i = 0; i < count; i++)
        {
            float[] vec = new float[dim];
            for (int d = 0; d < dim; d++)
                vec[d] = (seed + i) * 0.1f + d;
            arr[i] = new DataPoint(i * 1000L, FieldValue.FromVector(vec));
        }
        return arr;
    }

    private static ReadOnlyMemory<DataPoint> MakeVectorPoints(float[][] vectors)
    {
        var arr = new DataPoint[vectors.Length];
        for (int i = 0; i < vectors.Length; i++)
            arr[i] = new DataPoint(i * 1000L, FieldValue.FromVector(vectors[i]));
        return arr;
    }

    private static void RewriteSegmentVersion(string path, int version)
    {
        byte[] bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), version);
        if (version < 6)
            bytes.AsSpan(36, FormatSizes.SegmentHeaderSize - 36).Clear();

        int footerStart = bytes.Length - FormatSizes.SegmentFooterSize;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(footerStart + 8, 4), version);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteLegacyV4Float64Segment(string path)
    {
        const long SegmentId = 404L;
        const ulong SeriesId = 8080UL;
        const long Timestamp = 1234L;
        const double Value = 42.5d;
        byte[] fieldName = "v"u8.ToArray();

        byte[] tsPayload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(tsPayload, Timestamp);

        byte[] valuePayload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(valuePayload, BitConverter.DoubleToInt64Bits(Value));

        var blockCrc = new Crc32();
        blockCrc.Append(fieldName);
        blockCrc.Append(tsPayload);
        blockCrc.Append(valuePayload);
        uint blockCrc32 = blockCrc.GetCurrentHashAsUInt32();

        byte[] legacyBlockHeader = new byte[FormatSizes.LegacyBlockHeaderSizeV4];
        BinaryPrimitives.WriteUInt64LittleEndian(legacyBlockHeader.AsSpan(0, 8), SeriesId);
        BinaryPrimitives.WriteInt64LittleEndian(legacyBlockHeader.AsSpan(8, 8), Timestamp);
        BinaryPrimitives.WriteInt64LittleEndian(legacyBlockHeader.AsSpan(16, 8), Timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(legacyBlockHeader.AsSpan(24, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(legacyBlockHeader.AsSpan(28, 4), tsPayload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(legacyBlockHeader.AsSpan(32, 4), valuePayload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(legacyBlockHeader.AsSpan(36, 4), fieldName.Length);
        legacyBlockHeader[40] = (byte)BlockEncoding.None;
        legacyBlockHeader[41] = (byte)FieldType.Float64;
        BinaryPrimitives.WriteInt16LittleEndian(
            legacyBlockHeader.AsSpan(42, 2),
            BlockHeader.HasSumCount | BlockHeader.HasMinMax);
        BinaryPrimitives.WriteInt64LittleEndian(legacyBlockHeader.AsSpan(44, 8), BitConverter.DoubleToInt64Bits(Value));
        BinaryPrimitives.WriteInt64LittleEndian(legacyBlockHeader.AsSpan(52, 8), BitConverter.DoubleToInt64Bits(Value));
        BinaryPrimitives.WriteInt64LittleEndian(legacyBlockHeader.AsSpan(60, 8), BitConverter.DoubleToInt64Bits(Value));
        BinaryPrimitives.WriteUInt32LittleEndian(legacyBlockHeader.AsSpan(68, 4), blockCrc32);

        int blockLength = legacyBlockHeader.Length + fieldName.Length + tsPayload.Length + valuePayload.Length;
        long blockOffset = FormatSizes.SegmentHeaderSize;
        long indexOffset = blockOffset + blockLength;
        long fileLength = indexOffset + FormatSizes.BlockIndexEntrySize + FormatSizes.SegmentFooterSize;

        var indexEntry = new BlockIndexEntry
        {
            SeriesId = SeriesId,
            MinTimestamp = Timestamp,
            MaxTimestamp = Timestamp,
            FileOffset = blockOffset,
            BlockLength = blockLength,
            FieldNameHash = FieldNameHash.Compute(fieldName),
        };
        Span<byte> indexBytes = stackalloc byte[FormatSizes.BlockIndexEntrySize];
        MemoryMarshal.Write(indexBytes, in indexEntry);
        uint indexCrc32 = Crc32.HashToUInt32(indexBytes);

        var header = SegmentHeader.CreateNew(SegmentId);
        header.FormatVersion = 4;
        header.BlockCount = 1;

        var footer = SegmentFooter.CreateNew(1, indexOffset, fileLength);
        footer.FormatVersion = 4;
        footer.Crc32 = indexCrc32;

        byte[] bytes = new byte[checked((int)fileLength)];
        var writer = new SpanWriter(bytes);
        writer.WriteStruct(in header);
        writer.WriteBytes(legacyBlockHeader);
        writer.WriteBytes(fieldName);
        writer.WriteBytes(tsPayload);
        writer.WriteBytes(valuePayload);
        writer.WriteStruct(in indexEntry);
        writer.WriteStruct(in footer);

        File.WriteAllBytes(path, bytes);
    }
}
