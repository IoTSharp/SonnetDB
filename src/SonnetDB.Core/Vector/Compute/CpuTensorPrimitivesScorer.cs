using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Compute;

/// <summary>
/// 默认 CPU 实现的 <see cref="IBatchScorer"/>，逐行委托给 <see cref="Distance"/>。
/// 与 <see cref="Distance.Compute(ReadOnlySpan{float}, ReadOnlySpan{float}, Metric)"/> 在数值上完全一致（bit-identical）。
/// </summary>
/// <remarks>
/// 实例无状态，可通过 <see cref="Instance"/> 共享单例。
/// </remarks>
public sealed class CpuTensorPrimitivesScorer : IBatchScorer
{
    /// <summary>
    /// 全局共享单例。
    /// </summary>
    public static CpuTensorPrimitivesScorer Instance { get; } = new();

    /// <inheritdoc />
    public void Score(
        ReadOnlySpan<float> query,
        ReadOnlySpan<float> dataset,
        Span<float> scores,
        Metric metric)
    {
        if (metric == Metric.Hamming)
        {
            throw new NotSupportedException(
                "CpuTensorPrimitivesScorer 不支持 Hamming 度量；二值向量请直接调用 Distance.Hamming。");
        }

        int n = scores.Length;
        if (n == 0)
        {
            if (dataset.Length != 0)
            {
                throw new ArgumentException(
                    $"dataset 长度 ({dataset.Length}) 与 scores 长度 (0) 不一致。",
                    nameof(dataset));
            }
            return;
        }

        int dim = query.Length;
        long expected = (long)n * dim;
        if (dataset.Length != expected)
        {
            throw new ArgumentException(
                $"dataset 长度不匹配：期望 {expected} (= scores.Length × query.Length)，实际 {dataset.Length}。",
                nameof(dataset));
        }

        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<float> row = dataset.Slice(i * dim, dim);
            scores[i] = Distance.Compute(query, row, metric);
        }
    }
}
