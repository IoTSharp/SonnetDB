using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Vector;

/// <summary>
/// 向量 parity 场景基类。
/// </summary>
public abstract class VectorScenarioBase : IScenario
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract Capability Required { get; }

    /// <summary>本场景的准确度容差合同。</summary>
    public virtual DiffTolerance Tolerance => DiffTolerance.Strict;

    /// <inheritdoc />
    public async Task<ScenarioResult> RunAsync(IDataPlane plane, ScenarioContext ctx)
    {
        ArgumentNullException.ThrowIfNull(plane);
        ArgumentNullException.ThrowIfNull(ctx);

        if ((plane.Capabilities & Required) != Required)
        {
            return new ScenarioResult
            {
                Pass = true,
                GapReason = $"backend '{plane.BackendName}' lacks required capabilities: {Required & ~plane.Capabilities}",
            };
        }

        return await RunVectorAsync(plane.Vector, ctx).ConfigureAwait(false);
    }

    /// <summary>执行当前向量场景。</summary>
    protected abstract Task<ScenarioResult> RunVectorAsync(IVectorOps ops, ScenarioContext ctx);

    /// <summary>生成本次 run 独占 collection。</summary>
    protected string Collection(ScenarioContext ctx, string suffix)
        => ("p130_" + ctx.RunId.Replace("-", "_", StringComparison.Ordinal) + "_" + suffix).ToLowerInvariant();

    /// <summary>读取正整数环境变量。</summary>
    protected static int EnvInt(string key, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    /// <summary>构造单行 SQL 结果。</summary>
    protected static ScenarioResult MetricRow(params object?[] values)
    {
        var result = new RelationalSqlResult(
            Enumerable.Range(0, values.Length).Select(i => "c" + i).ToArray(),
            [new RelationalSqlRow(values)],
            -1);
        var scenario = new ScenarioResult { Pass = true, SqlResult = result };
        scenario.Metrics["row_count"] = 1;
        return scenario;
    }

    /// <summary>生成确定性向量数据集。</summary>
    protected static IReadOnlyList<VectorRecord> BuildRecords(int count, int dimension)
    {
        var records = new VectorRecord[count];
        for (int i = 0; i < records.Length; i++)
        {
            var vector = new float[dimension];
            for (int d = 0; d < dimension; d++)
                vector[d] = MathF.Sin((i + 1) * (d + 3) * 0.017f) + MathF.Cos((i + 7) * (d + 1) * 0.013f);
            Normalize(vector);
            records[i] = new VectorRecord((ulong)(i + 1), vector, i % 3 == 0 ? "hot" : "cold");
        }

        return records;
    }

    /// <summary>计算精确余弦 TopK。</summary>
    protected static IReadOnlySet<ulong> ExactTopK(IEnumerable<VectorRecord> records, float[] query, int topK, string? category = null)
        => records
            .Where(r => category is null || string.Equals(r.Category, category, StringComparison.Ordinal))
            .Select(r => new { r.Id, Distance = CosineDistance(r.Vector, query) })
            .OrderBy(static x => x.Distance)
            .ThenBy(static x => x.Id)
            .Take(topK)
            .Select(static x => x.Id)
            .ToHashSet();

    private static double CosineDistance(float[] left, float[] right)
    {
        double dot = 0d;
        double l2 = 0d;
        double r2 = 0d;
        for (int i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            l2 += left[i] * left[i];
            r2 += right[i] * right[i];
        }

        return 1d - dot / Math.Sqrt(l2 * r2);
    }

    private static void Normalize(float[] vector)
    {
        double norm = 0d;
        for (int i = 0; i < vector.Length; i++)
            norm += vector[i] * vector[i];
        norm = Math.Sqrt(norm);
        if (norm <= 0d)
            return;
        for (int i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }
}
