using SonnetDB.Vector.Primitives;

namespace SonnetDB.Core.Tests.Vector.Primitives;

public sealed class VectorDistanceFacadeTests
{
    [Fact]
    public void Compute_Cosine_ReturnsOneMinusSimilarity()
    {
        float distance = VectorDistance.Compute(KnnMetric.Cosine, [1f, 0f], [0f, 1f]);

        Assert.Equal(1f, distance, 5);
    }

    [Fact]
    public void Compute_L2_ReturnsEuclideanDistance()
    {
        float distance = VectorDistance.Compute(KnnMetric.L2, [0f, 0f], [3f, 4f]);

        Assert.Equal(5f, distance, 5);
    }

    [Fact]
    public void Compute_InnerProduct_ReturnsNegativeInnerProduct()
    {
        float distance = VectorDistance.Compute(KnnMetric.InnerProduct, [1f, 2f, 3f], [4f, 5f, 6f]);

        Assert.Equal(-32f, distance, 5);
    }

    [Fact]
    public void Compute_InnerProduct_LargerDotProductSortsFirst()
    {
        float closer = VectorDistance.Compute(KnnMetric.InnerProduct, [1f, 0f], [3f, 0f]);
        float farther = VectorDistance.Compute(KnnMetric.InnerProduct, [1f, 0f], [1f, 0f]);

        Assert.True(closer < farther);
    }
}
