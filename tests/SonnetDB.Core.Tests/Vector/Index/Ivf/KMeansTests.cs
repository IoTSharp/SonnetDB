using SonnetDB.Vector.Index.Ivf;

namespace SonnetDB.Core.Tests.Vector.Index.Ivf;

public sealed class KMeansTests
{
    [Fact]
    public void Train_ThreeWellSeparatedClusters_AssignsCorrectly()
    {
        const int Dim = 2;
        const int K = 3;

        // 三个明显分离的簇：(0,0), (10,10), (-10,10)
        var centers = new (float X, float Y)[]
        {
            (0, 0),
            (10, 10),
            (-10, 10),
        };
        var rng = new Random(42);
        const int PerCluster = 30;
        int n = K * PerCluster;
        var data = new float[n * Dim];
        var truth = new int[n];
        for (int c = 0; c < K; c++)
        {
            for (int i = 0; i < PerCluster; i++)
            {
                int row = c * PerCluster + i;
                data[row * Dim + 0] = centers[c].X + (float)(rng.NextDouble() - 0.5) * 0.5f;
                data[row * Dim + 1] = centers[c].Y + (float)(rng.NextDouble() - 0.5) * 0.5f;
                truth[row] = c;
            }
        }

        KMeans.Train(data, n, Dim, K, maxIterations: 25, seed: 7,
            out float[] centroids, out int[] assignments);

        Assert.Equal(K * Dim, centroids.Length);
        Assert.Equal(n, assignments.Length);

        // 同一原始簇的点 K-Means 之后应映射到相同的 cluster id。
        for (int c = 0; c < K; c++)
        {
            int first = assignments[c * PerCluster];
            for (int i = 1; i < PerCluster; i++)
            {
                Assert.Equal(first, assignments[c * PerCluster + i]);
            }
        }

        // 三个原始簇分到三个不同的 cluster id。
        var distinct = new HashSet<int>
        {
            assignments[0],
            assignments[PerCluster],
            assignments[2 * PerCluster],
        };
        Assert.Equal(3, distinct.Count);
    }

    [Fact]
    public void FindNearest_ReturnsCenterIndexWithSmallestL2()
    {
        var centroids = new float[]
        {
            0, 0,
            10, 10,
            -5, 5,
        };
        Assert.Equal(0, KMeans.FindNearest(new float[] { 0.1f, -0.1f }, centroids, 3, 2));
        Assert.Equal(1, KMeans.FindNearest(new float[] { 9, 11 }, centroids, 3, 2));
        Assert.Equal(2, KMeans.FindNearest(new float[] { -4, 6 }, centroids, 3, 2));
    }
}
