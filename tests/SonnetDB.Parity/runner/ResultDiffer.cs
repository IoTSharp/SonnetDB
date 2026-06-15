using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Runner;

/// <summary>
/// 跨后端结果容差判定器。按 <c>docs/parity-roadmap.md</c> 的容差合同比较两个结果集：
/// 行数必须精确相等；数值列遵循 <see cref="DiffTolerance"/>；字符串列序数精确相等。
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
        => DiffRows(expected, actual, new DiffTolerance(relTol, 0d));

    /// <summary>
    /// 比较期望（基准后端）与实际（竞品后端）的行集合。
    /// </summary>
    /// <param name="expected">基准后端（通常为 SonnetDB）的行集合。</param>
    /// <param name="actual">竞品后端的行集合。</param>
    /// <param name="tolerance">容差合同。</param>
    /// <returns>容差判定结果，含是否通过与可读差异列表。</returns>
    public static DiffResult DiffRows(
        IReadOnlyList<RelationalRow> expected,
        IReadOnlyList<RelationalRow> actual,
        DiffTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerance);

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

            if (!WithinTolerance(e.Id, a.Id, tolerance))
                diffs.Add($"row {i} id mismatch: expected {e.Id}, actual {a.Id}");

            if (!string.Equals(e.Name, a.Name, StringComparison.Ordinal))
                diffs.Add($"row {i} name mismatch: expected '{e.Name}', actual '{a.Name}'");
        }

        return new DiffResult(diffs.Count == 0, diffs);
    }

    /// <summary>
    /// 比较两个通用 SQL 结果集。
    /// </summary>
    /// <param name="expected">基准结果。</param>
    /// <param name="actual">实际结果。</param>
    /// <param name="relTol">数值列允许的相对误差。</param>
    /// <returns>容差判定结果。</returns>
    public static DiffResult DiffSqlResults(
        RelationalSqlResult expected,
        RelationalSqlResult actual,
        double relTol = 1e-9)
        => DiffSqlResults(expected, actual, new DiffTolerance(relTol, 0d));

    /// <summary>
    /// 比较两个通用 SQL 结果集。
    /// </summary>
    /// <param name="expected">基准结果。</param>
    /// <param name="actual">实际结果。</param>
    /// <param name="tolerance">容差合同。</param>
    /// <returns>容差判定结果。</returns>
    public static DiffResult DiffSqlResults(
        RelationalSqlResult expected,
        RelationalSqlResult actual,
        DiffTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerance);

        var diffs = new List<string>();
        if (expected.Rows.Count != actual.Rows.Count)
        {
            diffs.Add($"row count mismatch: expected {expected.Rows.Count}, actual {actual.Rows.Count}");
            return new DiffResult(false, diffs);
        }

        for (var row = 0; row < expected.Rows.Count; row++)
        {
            var e = expected.Rows[row].Values;
            var a = actual.Rows[row].Values;
            if (e.Count != a.Count)
            {
                diffs.Add($"row {row} column count mismatch: expected {e.Count}, actual {a.Count}");
                continue;
            }

            for (var col = 0; col < e.Count; col++)
            {
                if (!ValuesEqual(e[col], a[col], tolerance))
                    diffs.Add($"row {row} column {col} mismatch: expected '{e[col] ?? "NULL"}', actual '{a[col] ?? "NULL"}'");
            }
        }

        return new DiffResult(diffs.Count == 0, diffs);
    }

    private static bool WithinTolerance(long expected, long actual, DiffTolerance tolerance)
    {
        if (expected == actual) return true;
        if (Math.Abs((double)expected - actual) <= tolerance.Absolute)
            return true;
        var scale = Math.Max(Math.Abs((double)expected), Math.Abs((double)actual));
        if (scale == 0) return true;
        return Math.Abs((double)expected - actual) / scale <= tolerance.Relative;
    }

    private static bool ValuesEqual(object? expected, object? actual, DiffTolerance tolerance)
    {
        if (expected is null || actual is null)
            return expected is null && actual is null;
        if (expected is long expectedLong && actual is long actualLong)
            return WithinTolerance(expectedLong, actualLong, tolerance);
        if (expected is double expectedDouble && actual is double actualDouble)
            return WithinTolerance(expectedDouble, actualDouble, tolerance);
        if (IsNumeric(expected) && IsNumeric(actual))
            return WithinTolerance(
                Convert.ToDouble(expected, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(actual, System.Globalization.CultureInfo.InvariantCulture),
                tolerance);
        return Equals(expected, actual);
    }

    private static bool IsNumeric(object value)
        => value is byte or short or int or long or float or double or decimal;

    private static bool WithinTolerance(double expected, double actual, DiffTolerance tolerance)
    {
        if (double.IsNaN(expected) || double.IsNaN(actual))
            return double.IsNaN(expected) && double.IsNaN(actual);
        if (expected.Equals(actual))
            return true;
        if (Math.Abs(expected - actual) <= tolerance.Absolute)
            return true;
        var scale = Math.Max(Math.Abs(expected), Math.Abs(actual));
        if (scale == 0)
            return true;
        return Math.Abs(expected - actual) / scale <= tolerance.Relative;
    }
}

/// <summary>
/// 跨后端数值容差合同。
/// </summary>
/// <param name="Relative">相对误差上限。</param>
/// <param name="Absolute">绝对误差上限。</param>
public sealed record DiffTolerance(double Relative, double Absolute)
{
    /// <summary>严格数值合同：相对误差 1e-9，无绝对误差放宽。</summary>
    public static DiffTolerance Strict { get; } = new(1e-9, 0d);
}

/// <summary>
/// 容差判定结果。
/// </summary>
/// <param name="WithinTolerance">两个结果集是否在容差内一致。</param>
/// <param name="Differences">人类可读的差异描述（写入 Markdown 报告）。</param>
public sealed record DiffResult(bool WithinTolerance, IReadOnlyList<string> Differences);
