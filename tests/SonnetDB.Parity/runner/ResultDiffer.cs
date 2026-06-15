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
                var expectedColumnName = expected.Columns.Count > col ? expected.Columns[col] : string.Empty;
                var actualColumnName = actual.Columns.Count > col ? actual.Columns[col] : string.Empty;
                if (!ValuesEqual(e[col], a[col], tolerance, expectedColumnName, actualColumnName))
                    diffs.Add($"row {row} column {col} ({expectedColumnName}) mismatch: expected '{e[col] ?? "NULL"}', actual '{a[col] ?? "NULL"}'");
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

    private static bool ValuesEqual(
        object? expected,
        object? actual,
        DiffTolerance tolerance,
        string expectedColumnName,
        string actualColumnName)
    {
        if (expected is null || actual is null)
            return expected is null && actual is null;
        if ((IsTimeBucketColumn(expectedColumnName) || IsTimeBucketColumn(actualColumnName))
            && tolerance.TimeBucketMs is { } bucketSizeMs
            && TryConvertToInt64(expected, out var expectedTime)
            && TryConvertToInt64(actual, out var actualTime))
        {
            return TimeBucketStartsEqual(expectedTime, actualTime, bucketSizeMs);
        }

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

    private static bool IsTimeBucketColumn(string columnName)
        => string.Equals(columnName, "time", StringComparison.OrdinalIgnoreCase)
            || string.Equals(columnName, "bucket", StringComparison.OrdinalIgnoreCase);

    private static bool TimeBucketStartsEqual(long expected, long actual, long bucketSizeMs)
    {
        var expectedStart = FloorToBucketStart(expected, bucketSizeMs);
        var actualStart = FloorToBucketStart(actual, bucketSizeMs);
        if (expectedStart == actualStart)
            return true;

        // InfluxDB aggregateWindow and PromQL query_range commonly stamp a window
        // at its stop/evaluation time. Normalize either side so this contract is
        // independent from which backend is used as the baseline.
        return expectedStart == FloorToBucketStart(actual - bucketSizeMs, bucketSizeMs)
            || FloorToBucketStart(expected - bucketSizeMs, bucketSizeMs) == actualStart;
    }

    private static long FloorToBucketStart(long timestampMs, long bucketSizeMs)
    {
        var remainder = timestampMs % bucketSizeMs;
        return remainder >= 0
            ? timestampMs - remainder
            : timestampMs - remainder - bucketSizeMs;
    }

    private static bool TryConvertToInt64(object value, out long result)
    {
        switch (value)
        {
            case byte b:
                result = b;
                return true;
            case short s:
                result = s;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case float f when IsInteger(f):
                result = (long)f;
                return true;
            case double d when IsInteger(d):
                result = (long)d;
                return true;
            case decimal m when m == decimal.Truncate(m):
                result = (long)m;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool IsInteger(double value)
        => !double.IsNaN(value)
            && !double.IsInfinity(value)
            && Math.Abs(value - Math.Round(value)) < 1e-9;

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

    /// <summary>当列名为 time / bucket 时，用于把窗口 stop/evaluation 时间规范化为 bucket 起始时间。</summary>
    public long? TimeBucketMs { get; init; }
}

/// <summary>
/// 容差判定结果。
/// </summary>
/// <param name="WithinTolerance">两个结果集是否在容差内一致。</param>
/// <param name="Differences">人类可读的差异描述（写入 Markdown 报告）。</param>
public sealed record DiffResult(bool WithinTolerance, IReadOnlyList<string> Differences);
