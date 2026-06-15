using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Runner;

/// <summary>
/// 跨后端结果容差判定器。按 <c>docs/parity-roadmap.md</c> 的容差合同比较两个结果集：
/// 行数必须精确相等；数值列相对误差 ≤ <paramref name="relTol"/>（默认 1e-9）；字符串列序数精确相等。
/// </summary>
public static class ResultDiffer
{
    /// <summary>
    /// 比较期望（基准后端）与实际（竞品后端）的行集合。
    /// </summary>
    /// <param name="expected">基准后端（通常为 SonnetDB）的行集合。</param>
    /// <param name="actual">竞品后端的行集合。</param>
    /// <param name="relTol">数值列允许的相对误差，默认 1e-9（IEEE 754 双精度）。</param>
    /// <returns>容差判定结果，含是否通过与可读差异列表。</returns>
    public static DiffResult DiffRows(
        IReadOnlyList<RelationalRow> expected,
        IReadOnlyList<RelationalRow> actual,
        double relTol = 1e-9)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var diffs = new List<string>();
        if (expected.Count != actual.Count)
        {
            diffs.Add($"row count mismatch: expected {expected.Count}, actual {actual.Count}");
            return new DiffResult(false, diffs);
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];

            if (!WithinRelative(e.Id, a.Id, relTol))
                diffs.Add($"row {i} id mismatch: expected {e.Id}, actual {a.Id}");

            if (!string.Equals(e.Name, a.Name, StringComparison.Ordinal))
                diffs.Add($"row {i} name mismatch: expected '{e.Name}', actual '{a.Name}'");
        }

        return new DiffResult(diffs.Count == 0, diffs);
    }

    private static bool WithinRelative(long expected, long actual, double relTol)
    {
        if (expected == actual) return true;
        var scale = Math.Max(Math.Abs((double)expected), Math.Abs((double)actual));
        if (scale == 0) return true;
        return Math.Abs((double)expected - actual) / scale <= relTol;
    }
}

/// <summary>
/// 容差判定结果。
/// </summary>
/// <param name="WithinTolerance">两个结果集是否在容差内一致。</param>
/// <param name="Differences">人类可读的差异描述（写入 Markdown 报告）。</param>
public sealed record DiffResult(bool WithinTolerance, IReadOnlyList<string> Differences);
