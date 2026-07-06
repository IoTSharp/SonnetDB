using SonnetDB.Catalog;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Catalog;

/// <summary>
/// PR #58 b：<see cref="MeasurementSchema"/> + <see cref="MeasurementSchemaCodec"/>
/// 对 VECTOR 列的校验与持久化测试。
/// </summary>
public class MeasurementSchemaVectorTests
{
    [Fact]
    public void Create_VectorFieldWithDim_Succeeds()
    {
        var schema = MeasurementSchema.Create("docs", new[]
        {
            new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
            new MeasurementColumn("embedding", MeasurementColumnRole.Field, FieldType.Vector, 384),
        });

        var col = schema.TryGetColumn("embedding")!;
        Assert.Equal(FieldType.Vector, col.DataType);
        Assert.Equal(384, col.VectorDimension);
    }

    [Fact]
    public void Create_VectorFieldWithHnswIndex_Succeeds()
    {
        var schema = MeasurementSchema.Create("docs", new[]
        {
            new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
            new MeasurementColumn(
                "embedding",
                MeasurementColumnRole.Field,
                FieldType.Vector,
                384,
                VectorIndexDefinition.CreateHnsw(16, 200)),
        });

        var col = schema.TryGetColumn("embedding")!;
        Assert.NotNull(col.VectorIndex);
        Assert.Equal(VectorIndexKind.Hnsw, col.VectorIndex!.Kind);
        var hnsw = col.VectorIndex.Hnsw!;
        Assert.Equal(16, hnsw.M);
        Assert.Equal(200, hnsw.Ef);
    }

    [Fact]
    public void Create_VectorFieldWithIvfAndVamanaIndexes_Succeeds()
    {
        var ivfSchema = MeasurementSchema.Create("docs_ivf", new[]
        {
            new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
            new MeasurementColumn(
                "embedding",
                MeasurementColumnRole.Field,
                FieldType.Vector,
                384,
                VectorIndexDefinition.CreateIvfFlat(32, 8, 12)),
        });
        var vamanaSchema = MeasurementSchema.Create("docs_vamana", new[]
        {
            new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
            new MeasurementColumn(
                "embedding",
                MeasurementColumnRole.Field,
                FieldType.Vector,
                384,
                VectorIndexDefinition.CreateVamana(32, 75, 1.2f, 4)),
        });

        Assert.Equal(VectorIndexKind.IvfFlat, ivfSchema.TryGetColumn("embedding")!.VectorIndex!.Kind);
        Assert.Equal(32, ivfSchema.TryGetColumn("embedding")!.VectorIndex!.Ivf!.NList);
        Assert.Equal(VectorIndexKind.Vamana, vamanaSchema.TryGetColumn("embedding")!.VectorIndex!.Kind);
        Assert.Equal(75, vamanaSchema.TryGetColumn("embedding")!.VectorIndex!.Vamana!.SearchListSize);
    }

    [Fact]
    public void Create_VectorFieldWithIvfPqIndex_Succeeds()
    {
        var schema = MeasurementSchema.Create("docs", new[]
        {
            new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
            new MeasurementColumn(
                "embedding",
                MeasurementColumnRole.Field,
                FieldType.Vector,
                384,
                VectorIndexDefinition.CreateIvfPq(32, 8, 12, 8, 8)),
        });

        var index = schema.TryGetColumn("embedding")!.VectorIndex!;
        Assert.Equal(VectorIndexKind.IvfPq, index.Kind);
        Assert.Equal(32, index.IvfPq!.NList);
        Assert.Equal(8, index.IvfPq.M);
    }

    [Fact]
    public void Create_VectorIndexOnNonVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => MeasurementSchema.Create("m", new[]
        {
            new MeasurementColumn(
                "v",
                MeasurementColumnRole.Field,
                FieldType.Float64,
                null,
                VectorIndexDefinition.CreateHnsw(16, 200)),
        }));
    }

    [Fact]
    public void Create_VectorWithoutDim_Throws()
    {
        Assert.Throws<ArgumentException>(() => MeasurementSchema.Create("m", new[]
        {
            new MeasurementColumn("e", MeasurementColumnRole.Field, FieldType.Vector),
        }));
    }

    [Fact]
    public void Create_VectorWithZeroDim_Throws()
    {
        Assert.Throws<ArgumentException>(() => MeasurementSchema.Create("m", new[]
        {
            new MeasurementColumn("e", MeasurementColumnRole.Field, FieldType.Vector, 0),
        }));
    }

    [Fact]
    public void Create_VectorAsTag_Throws()
    {
        Assert.Throws<ArgumentException>(() => MeasurementSchema.Create("m", new[]
        {
            new MeasurementColumn("t", MeasurementColumnRole.Tag, FieldType.Vector, 4),
            new MeasurementColumn("v", MeasurementColumnRole.Field, FieldType.Float64),
        }));
    }

    [Fact]
    public void Create_NonVectorWithDim_Throws()
    {
        Assert.Throws<ArgumentException>(() => MeasurementSchema.Create("m", new[]
        {
            new MeasurementColumn("v", MeasurementColumnRole.Field, FieldType.Float64, 4),
        }));
    }

    // ── Codec round-trip ──────────────────────────────────────────────────

    [Fact]
    public void Codec_VectorColumn_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "sndb-vec-codec-" + Guid.NewGuid().ToString("N") + ".tslschema");
        try
        {
            var original = MeasurementSchema.Create("docs", new[]
            {
                new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
                new MeasurementColumn("embedding", MeasurementColumnRole.Field, FieldType.Vector, 384),
                new MeasurementColumn("score", MeasurementColumnRole.Field, FieldType.Float64),
            });

            MeasurementSchemaCodec.Save(path, new[] { original });

            var loaded = MeasurementSchemaCodec.Load(path);
            Assert.Single(loaded);
            var s = loaded[0];
            Assert.Equal("docs", s.Name);
            Assert.Equal(3, s.Columns.Count);

            var emb = s.TryGetColumn("embedding")!;
            Assert.Equal(FieldType.Vector, emb.DataType);
            Assert.Equal(384, emb.VectorDimension);
            Assert.Null(emb.VectorIndex);

            var score = s.TryGetColumn("score")!;
            Assert.Equal(FieldType.Float64, score.DataType);
            Assert.Null(score.VectorDimension);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Codec_MultipleSchemasWithMixedVectorDims_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "sndb-vec-multi-" + Guid.NewGuid().ToString("N") + ".tslschema");
        try
        {
            var schemas = new[]
            {
                MeasurementSchema.Create("a", new[]
                {
                    new MeasurementColumn("e", MeasurementColumnRole.Field, FieldType.Vector, 3),
                }),
                MeasurementSchema.Create("b", new[]
                {
                    new MeasurementColumn("v", MeasurementColumnRole.Field, FieldType.Float64),
                    new MeasurementColumn("e", MeasurementColumnRole.Field, FieldType.Vector, 1024),
                }),
            };

            MeasurementSchemaCodec.Save(path, schemas);
            var loaded = MeasurementSchemaCodec.Load(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal(3, loaded[0].TryGetColumn("e")!.VectorDimension);
            Assert.Equal(1024, loaded[1].TryGetColumn("e")!.VectorDimension);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Codec_VectorColumnWithHnswIndex_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "sndb-vec-hnsw-" + Guid.NewGuid().ToString("N") + ".tslschema");
        try
        {
            var original = MeasurementSchema.Create("docs", new[]
            {
                new MeasurementColumn("source", MeasurementColumnRole.Tag, FieldType.String),
                new MeasurementColumn(
                    "embedding",
                    MeasurementColumnRole.Field,
                    FieldType.Vector,
                    384,
                    VectorIndexDefinition.CreateHnsw(16, 200)),
            });

            MeasurementSchemaCodec.Save(path, new[] { original });
            var loaded = MeasurementSchemaCodec.Load(path);

            var emb = loaded[0].TryGetColumn("embedding")!;
            Assert.NotNull(emb.VectorIndex);
            Assert.Equal(VectorIndexKind.Hnsw, emb.VectorIndex!.Kind);
            var hnsw = emb.VectorIndex.Hnsw!;
            Assert.Equal(16, hnsw.M);
            Assert.Equal(200, hnsw.Ef);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Codec_VectorColumnWithAdvancedIndexes_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "sndb-vec-advanced-" + Guid.NewGuid().ToString("N") + ".tslschema");
        try
        {
            var schemas = new[]
            {
                MeasurementSchema.Create("ivf_docs", new[]
                {
                    new MeasurementColumn("embedding", MeasurementColumnRole.Field, FieldType.Vector, 384,
                        VectorIndexDefinition.CreateIvfFlat(32, 8, 12)),
                }),
                MeasurementSchema.Create("ivfpq_docs", new[]
                {
                    new MeasurementColumn("embedding", MeasurementColumnRole.Field, FieldType.Vector, 384,
                        VectorIndexDefinition.CreateIvfPq(32, 8, 12, 8, 8)),
                }),
                MeasurementSchema.Create("vamana_docs", new[]
                {
                    new MeasurementColumn("embedding", MeasurementColumnRole.Field, FieldType.Vector, 384,
                        VectorIndexDefinition.CreateVamana(32, 75, 1.2f, 4)),
                }),
            };

            MeasurementSchemaCodec.Save(path, schemas);
            var loaded = MeasurementSchemaCodec.Load(path);

            var ivf = loaded[0].TryGetColumn("embedding")!.VectorIndex!;
            Assert.Equal(VectorIndexKind.IvfFlat, ivf.Kind);
            Assert.Equal(32, ivf.Ivf!.NList);
            Assert.Equal(8, ivf.Ivf.NProbe);
            Assert.Equal(12, ivf.Ivf.MaxIterations);

            var ivfPq = loaded[1].TryGetColumn("embedding")!.VectorIndex!;
            Assert.Equal(VectorIndexKind.IvfPq, ivfPq.Kind);
            Assert.Equal(8, ivfPq.IvfPq!.M);
            Assert.Equal(8, ivfPq.IvfPq.NBits);

            var vamana = loaded[2].TryGetColumn("embedding")!.VectorIndex!;
            Assert.Equal(VectorIndexKind.Vamana, vamana.Kind);
            Assert.Equal(32, vamana.Vamana!.MaxDegree);
            Assert.Equal(75, vamana.Vamana.SearchListSize);
            Assert.Equal(1.2f, vamana.Vamana.Alpha);
            Assert.Equal(4, vamana.Vamana.BeamWidth);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── #223：度量（metric）与 efConstruction 贯通 + 持久化 ──────────────────────

    [Fact]
    public void CreateHnsw_DefaultEfConstruction_IsMaxEf200()
    {
        // efConstruction 缺省与 ef 解耦：取 max(ef, 200)，避免小 ef 烤进低质量图（I9）。
        Assert.Equal(200, VectorIndexDefinition.CreateHnsw(16, 50).Hnsw!.EfConstruction);
        Assert.Equal(400, VectorIndexDefinition.CreateHnsw(16, 400).Hnsw!.EfConstruction);
        Assert.Equal(300, VectorIndexDefinition.CreateHnsw(16, 50, efConstruction: 300).Hnsw!.EfConstruction);
    }

    [Fact]
    public void CreateFactories_DefaultMetric_IsCosine()
    {
        Assert.Equal(SonnetDB.Query.KnnMetric.Cosine, VectorIndexDefinition.CreateHnsw(16, 200).Metric);
        Assert.Equal(SonnetDB.Query.KnnMetric.L2, VectorIndexDefinition.CreateHnsw(16, 200, SonnetDB.Query.KnnMetric.L2).Metric);
        Assert.Equal(SonnetDB.Query.KnnMetric.InnerProduct, VectorIndexDefinition.CreateIvfFlat(32, 8, 12, SonnetDB.Query.KnnMetric.InnerProduct).Metric);
    }

    [Fact]
    public void Codec_HnswWithMetricAndEfConstruction_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "sndb-vec-v5-hnsw-" + Guid.NewGuid().ToString("N") + ".tslschema");
        try
        {
            var original = MeasurementSchema.Create("docs", new[]
            {
                new MeasurementColumn(
                    "embedding",
                    MeasurementColumnRole.Field,
                    FieldType.Vector,
                    384,
                    VectorIndexDefinition.CreateHnsw(16, 64, SonnetDB.Query.KnnMetric.L2, efConstruction: 256)),
            });

            MeasurementSchemaCodec.Save(path, new[] { original });
            var loaded = MeasurementSchemaCodec.Load(path);

            var index = loaded[0].TryGetColumn("embedding")!.VectorIndex!;
            Assert.Equal(VectorIndexKind.Hnsw, index.Kind);
            Assert.Equal(SonnetDB.Query.KnnMetric.L2, index.Metric);
            Assert.Equal(16, index.Hnsw!.M);
            Assert.Equal(64, index.Hnsw.Ef);
            Assert.Equal(256, index.Hnsw.EfConstruction);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Codec_NonCosineMetricOnAllKinds_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "sndb-vec-v5-metric-" + Guid.NewGuid().ToString("N") + ".tslschema");
        try
        {
            var schemas = new[]
            {
                MeasurementSchema.Create("ivf_l2", new[]
                {
                    new MeasurementColumn("e", MeasurementColumnRole.Field, FieldType.Vector, 8,
                        VectorIndexDefinition.CreateIvfFlat(32, 8, 12, SonnetDB.Query.KnnMetric.L2)),
                }),
                MeasurementSchema.Create("ivfpq_ip", new[]
                {
                    new MeasurementColumn("e", MeasurementColumnRole.Field, FieldType.Vector, 8,
                        VectorIndexDefinition.CreateIvfPq(32, 8, 12, 8, 8, SonnetDB.Query.KnnMetric.InnerProduct)),
                }),
                MeasurementSchema.Create("vamana_l2", new[]
                {
                    new MeasurementColumn("e", MeasurementColumnRole.Field, FieldType.Vector, 8,
                        VectorIndexDefinition.CreateVamana(32, 75, 1.2f, 4, SonnetDB.Query.KnnMetric.L2)),
                }),
            };

            MeasurementSchemaCodec.Save(path, schemas);
            var loaded = MeasurementSchemaCodec.Load(path);

            Assert.Equal(SonnetDB.Query.KnnMetric.L2, loaded[0].TryGetColumn("e")!.VectorIndex!.Metric);
            Assert.Equal(SonnetDB.Query.KnnMetric.InnerProduct, loaded[1].TryGetColumn("e")!.VectorIndex!.Metric);
            Assert.Equal(SonnetDB.Query.KnnMetric.L2, loaded[2].TryGetColumn("e")!.VectorIndex!.Metric);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
