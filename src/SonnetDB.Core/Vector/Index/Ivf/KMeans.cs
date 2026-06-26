using System.Numerics.Tensors;
using SonnetDB.Vector.Compute;

namespace SonnetDB.Vector.Index.Ivf;

/// <summary>
/// 基于 K-Means++ 初始化与 Lloyd 迭代的 K-Means 聚类训练器。
/// </summary>
/// <remarks>
/// <para>
/// IVF 和 PQ 子量化器都使用本实现：
/// <list type="bullet">
///   <item>初始化使用 K-Means++（D² 加权），缓解随机初始化造成的"塌缩"问题。</item>
///   <item>迭代采用经典 Lloyd's algorithm：assignment（最近中心）+ update（中心 = 簇均值）。</item>
///   <item>始终使用 L2 平方距离进行聚类（即便上层度量为 Cosine / InnerProduct，
///         IVF 仍按 L2 进行 coarse 聚类，符合 FAISS 的标准做法；用户若要更强的语义聚类，
///         应在外部对向量做归一化预处理）。</item>
///   <item>空簇会用当前最远的点（按到自身中心的距离）重新播种，避免长期为空。</item>
/// </list>
/// </para>
/// </remarks>
internal static class KMeans
{
    /// <summary>
    /// 训练 K-Means 聚类。
    /// </summary>
    /// <param name="data">行优先的训练向量数据，长度 = <paramref name="count"/> × <paramref name="dimensions"/>。</param>
    /// <param name="count">训练向量数。</param>
    /// <param name="dimensions">向量维度。</param>
    /// <param name="k">聚类中心数量，必须 ≤ <paramref name="count"/>。</param>
    /// <param name="maxIterations">最大迭代次数。</param>
    /// <param name="seed">随机种子；<see langword="null"/> 表示非确定性。</param>
    /// <param name="centroids">输出聚类中心，行优先，长度 = <paramref name="k"/> × <paramref name="dimensions"/>。</param>
    /// <param name="assignments">输出每个训练向量的簇编号，长度 = <paramref name="count"/>。</param>
    public static void Train(
        ReadOnlySpan<float> data,
        int count,
        int dimensions,
        int k,
        int maxIterations,
        int? seed,
        out float[] centroids,
        out int[] assignments)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);
        if (k > count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(k), k, $"k（{k}）不能超过训练向量数 count（{count}）。");
        }
        long expected = (long)count * dimensions;
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"data 长度（{data.Length}）与 count × dimensions（{expected}）不一致。",
                nameof(data));
        }

        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        centroids = new float[(long)k * dimensions];
        assignments = new int[count];

        // 1) K-Means++ 初始化。
        InitializePlusPlus(data, count, dimensions, k, rng, centroids);

        // 2) Lloyd 迭代。
        var newCentroids = new float[centroids.Length];
        var counts = new int[k];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            // assignment 阶段
            bool changed = AssignAll(data, count, dimensions, centroids, k, assignments);

            // update 阶段
            Array.Clear(newCentroids);
            Array.Clear(counts);
            for (int i = 0; i < count; i++)
            {
                int c = assignments[i];
                ReadOnlySpan<float> vec = data.Slice(i * dimensions, dimensions);
                Span<float> dst = newCentroids.AsSpan(c * dimensions, dimensions);
                TensorPrimitives.Add(dst, vec, dst);
                counts[c]++;
            }

            for (int c = 0; c < k; c++)
            {
                Span<float> dst = newCentroids.AsSpan(c * dimensions, dimensions);
                if (counts[c] > 0)
                {
                    float inv = 1f / counts[c];
                    TensorPrimitives.Multiply(dst, inv, dst);
                }
                else
                {
                    // 空簇：用当前最远的训练点重新播种。
                    int farthest = FindFarthestPoint(data, count, dimensions, centroids, assignments);
                    data.Slice(farthest * dimensions, dimensions).CopyTo(dst);
                }
            }

            newCentroids.AsSpan().CopyTo(centroids.AsSpan());

            if (!changed)
            {
                break;
            }
        }

        // 最终重新分配一次，确保 assignments 与 centroids 一致。
        AssignAll(data, count, dimensions, centroids, k, assignments);
    }

    /// <summary>
    /// 将向量 <paramref name="vector"/> 分配到距离最近的中心，返回中心索引。
    /// </summary>
    public static int FindNearest(
        ReadOnlySpan<float> vector,
        ReadOnlySpan<float> centroids,
        int k,
        int dimensions)
    {
        int best = 0;
        float bestDist = float.PositiveInfinity;
        for (int c = 0; c < k; c++)
        {
            float d = L2Squared(vector, centroids.Slice(c * dimensions, dimensions));
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }

    private static bool AssignAll(
        ReadOnlySpan<float> data,
        int count,
        int dimensions,
        ReadOnlySpan<float> centroids,
        int k,
        int[] assignments)
    {
        bool changed = false;
        for (int i = 0; i < count; i++)
        {
            int prev = assignments[i];
            int next = FindNearest(data.Slice(i * dimensions, dimensions), centroids, k, dimensions);
            if (next != prev)
            {
                changed = true;
                assignments[i] = next;
            }
        }
        return changed;
    }

    private static void InitializePlusPlus(
        ReadOnlySpan<float> data,
        int count,
        int dimensions,
        int k,
        Random rng,
        Span<float> centroids)
    {
        int first = rng.Next(count);
        data.Slice(first * dimensions, dimensions).CopyTo(centroids[..dimensions]);

        var minDist = new float[count];
        Array.Fill(minDist, float.PositiveInfinity);

        for (int chosen = 1; chosen < k; chosen++)
        {
            ReadOnlySpan<float> last = centroids.Slice((chosen - 1) * dimensions, dimensions);
            double total = 0d;
            for (int i = 0; i < count; i++)
            {
                float d = L2Squared(data.Slice(i * dimensions, dimensions), last);
                if (d < minDist[i])
                {
                    minDist[i] = d;
                }
                total += minDist[i];
            }

            int pickedIndex;
            if (total <= 0d)
            {
                // 极端情况下（数据全部重复）退化为随机选取。
                pickedIndex = rng.Next(count);
            }
            else
            {
                double target = rng.NextDouble() * total;
                double cumulative = 0d;
                pickedIndex = count - 1;
                for (int i = 0; i < count; i++)
                {
                    cumulative += minDist[i];
                    if (cumulative >= target)
                    {
                        pickedIndex = i;
                        break;
                    }
                }
            }
            data.Slice(pickedIndex * dimensions, dimensions)
                .CopyTo(centroids.Slice(chosen * dimensions, dimensions));
        }
    }

    private static int FindFarthestPoint(
        ReadOnlySpan<float> data,
        int count,
        int dimensions,
        ReadOnlySpan<float> centroids,
        int[] assignments)
    {
        int best = 0;
        float bestDist = -1f;
        for (int i = 0; i < count; i++)
        {
            int c = assignments[i];
            float d = L2Squared(
                data.Slice(i * dimensions, dimensions),
                centroids.Slice(c * dimensions, dimensions));
            if (d > bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// 计算 <paramref name="a"/> 与 <paramref name="b"/> 之间的 L2 平方距离（委托给 <see cref="Distance.L2Squared"/>）。
    /// </summary>
    internal static float L2Squared(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Distance.L2Squared(a, b);
}
