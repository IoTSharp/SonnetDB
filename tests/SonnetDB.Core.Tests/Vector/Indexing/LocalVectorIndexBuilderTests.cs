using SonnetDB.Vector.Indexing;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.Core.Tests.Vector.Indexing;

public sealed class LocalVectorIndexBuilderTests
{
    [Fact]
    public void Build_FlatIndex_SearchesContinuousPayload()
    {
        using var reader = LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.Flat,
            KnnMetric.Cosine,
            new float[]
            {
                1f, 0f,
                0f, 1f,
                -1f, 0f,
            },
            Count: 3,
            Dimension: 2));

        var results = reader.Search(new VectorSearchRequest(new float[] { 1f, 0f }, TopK: 3, KnnMetric.Cosine));

        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[0].PointIndex);
        Assert.InRange(results[0].Distance, 0f, 1e-5f);
        Assert.True(results[0].Distance <= results[1].Distance);
        Assert.True(results[1].Distance <= results[2].Distance);
    }

    [Fact]
    public void Build_FlatIndex_InnerProductUsesLowerIsBetterDistance()
    {
        using var reader = LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.Flat,
            KnnMetric.InnerProduct,
            new float[]
            {
                1f, 0f,
                3f, 0f,
                -2f, 0f,
            },
            Count: 3,
            Dimension: 2));

        var results = reader.Search(new VectorSearchRequest(new float[] { 1f, 0f }, TopK: 3, KnnMetric.InnerProduct));

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].PointIndex);
        Assert.Equal(-3f, results[0].Distance, 5);
        Assert.True(results[0].Distance <= results[1].Distance);
        Assert.True(results[1].Distance <= results[2].Distance);
    }

    [Fact]
    public void Build_FlatIndex_L2ReturnsEuclideanDistance()
    {
        using var reader = LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.Flat,
            KnnMetric.L2,
            new float[]
            {
                3f, 4f,
                6f, 8f,
            },
            Count: 2,
            Dimension: 2));

        var results = reader.Search(new VectorSearchRequest(new float[] { 0f, 0f }, TopK: 1, KnnMetric.L2));

        Assert.Single(results);
        Assert.Equal(0, results[0].PointIndex);
        Assert.Equal(5f, results[0].Distance, 5);
    }

    [Fact]
    public void Build_HnswIndex_SearchesContinuousPayload()
    {
        using var reader = LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.Hnsw,
            KnnMetric.Cosine,
            new float[]
            {
                1f, 0f, 0f,
                0.9f, 0.1f, 0f,
                0f, 1f, 0f,
                -1f, 0f, 0f,
            },
            Count: 4,
            Dimension: 3,
            Hnsw: new VectorIndexHnswOptions(M: 4, EfConstruction: 8, EfSearch: 8, Seed: 7)));

        var results = reader.Search(new VectorSearchRequest(new float[] { 1f, 0f, 0f }, TopK: 2, KnnMetric.Cosine));

        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].PointIndex);
        Assert.True(results[0].Distance <= results[1].Distance);
    }

    [Fact]
    public void Build_IvfFlatIndex_SearchesContinuousPayload()
    {
        var vectors = CreateClusteredVectors(count: 80, dimension: 4);
        using var reader = LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.IvfFlat,
            KnnMetric.L2,
            vectors,
            Count: 80,
            Dimension: 4,
            Ivf: new VectorIndexIvfOptions(NList: 8, NProbe: 8, MaxIterations: 10, Seed: 11)));

        var results = reader.Search(new VectorSearchRequest(new[] { 0f, 0f, 0f, 0f }, TopK: 3, KnnMetric.L2));

        Assert.Equal(3, results.Count);
        Assert.True(results[0].Distance <= results[1].Distance);
        Assert.True(results[1].Distance <= results[2].Distance);
    }

    [Fact]
    public void Build_IvfPqIndex_SearchesQuantizedPayload()
    {
        var vectors = CreateClusteredVectors(count: 300, dimension: 8);
        using var reader = LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.IvfPq,
            KnnMetric.L2,
            vectors,
            Count: 300,
            Dimension: 8,
            IvfPq: new VectorIndexIvfPqOptions(NList: 12, NProbe: 12, MaxIterations: 10, M: 4, NBits: 8, Seed: 13)));

        var results = reader.Search(new VectorSearchRequest(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }, TopK: 5, KnnMetric.L2));

        Assert.Equal(5, results.Count);
        Assert.True(results[0].Distance <= results[1].Distance);
    }

    [Fact]
    public void Build_VamanaIndex_SearchesContinuousPayload()
    {
        var vectors = CreateClusteredVectors(count: 80, dimension: 4);
        using var reader = LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.Vamana,
            KnnMetric.L2,
            vectors,
            Count: 80,
            Dimension: 4,
            Vamana: new VectorIndexVamanaOptions(MaxDegree: 8, SearchListSize: 16, Alpha: 1.2f, BeamWidth: 4, Seed: 17)));

        var results = reader.Search(new VectorSearchRequest(new[] { 0f, 0f, 0f, 0f }, TopK: 3, KnnMetric.L2));

        Assert.Equal(3, results.Count);
        Assert.True(results[0].Distance <= results[1].Distance);
        Assert.True(results[1].Distance <= results[2].Distance);
    }

    [Fact]
    public void Build_InvalidPayloadLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.Flat,
            KnnMetric.Cosine,
            new float[] { 1f, 2f, 3f },
            Count: 2,
            Dimension: 2)));
    }

    [Fact]
    public void Search_WithDifferentMetric_Throws()
    {
        using var reader = LocalVectorIndexBuilder.Instance.Build(new VectorIndexBuildInput(
            VectorIndexAlgorithm.Flat,
            KnnMetric.Cosine,
            new float[] { 1f, 0f },
            Count: 1,
            Dimension: 2));

        Assert.Throws<ArgumentException>(() =>
            reader.Search(new VectorSearchRequest(new float[] { 1f, 0f }, TopK: 1, KnnMetric.L2)));
    }

    private static float[] CreateClusteredVectors(int count, int dimension)
    {
        var vectors = new float[count * dimension];
        for (int row = 0; row < count; row++)
        {
            float cluster = row < count / 2 ? 0f : 10f;
            for (int col = 0; col < dimension; col++)
                vectors[(row * dimension) + col] = cluster + ((row % 7) * 0.01f) + (col * 0.001f);
        }

        return vectors;
    }
}
