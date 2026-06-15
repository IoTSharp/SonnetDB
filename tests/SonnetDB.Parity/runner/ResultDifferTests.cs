using SonnetDB.Parity.Adapters;
using Xunit;

namespace SonnetDB.Parity.Runner;

public sealed class ResultDifferTests
{
    [Fact]
    public void DiffSqlResults_WithTimeBucketContract_NormalizesWindowStopToBucketStart()
    {
        var expected = Result(
            ["time", "avg"],
            Row(1_704_067_200_000L, 29.5),
            Row(1_704_067_260_000L, 89.5));
        var actual = Result(
            ["time", "avg"],
            Row(1_704_067_260_000L, 29.5),
            Row(1_704_067_320_000L, 89.5));

        var diff = ResultDiffer.DiffSqlResults(
            expected,
            actual,
            new DiffTolerance(1e-9, 1e-9) { TimeBucketMs = 60_000L });

        Assert.True(diff.WithinTolerance, string.Join("; ", diff.Differences));
    }

    [Fact]
    public void DiffSqlResults_WithBucketAlias_AcceptsEquivalentTimeColumnName()
    {
        var expected = Result(["bucket", "avg"], Row(1_704_067_200_000L, 29.5));
        var actual = Result(["time", "avg"], Row(1_704_067_260_000L, 29.5));

        var diff = ResultDiffer.DiffSqlResults(
            expected,
            actual,
            new DiffTolerance(1e-9, 1e-9) { TimeBucketMs = 60_000L });

        Assert.True(diff.WithinTolerance, string.Join("; ", diff.Differences));
    }

    [Fact]
    public void DiffSqlResults_WithTimeBucketContract_NormalizesWindowStopOnEitherSide()
    {
        var expected = Result(["time", "avg"], Row(1_704_067_260_000L, 29.5));
        var actual = Result(["time", "avg"], Row(1_704_067_200_000L, 29.5));

        var diff = ResultDiffer.DiffSqlResults(
            expected,
            actual,
            new DiffTolerance(1e-9, 1e-9) { TimeBucketMs = 60_000L });

        Assert.True(diff.WithinTolerance, string.Join("; ", diff.Differences));
    }

    [Fact]
    public void DiffSqlResults_WithTimeBucketContract_KeepsValueColumnsStrict()
    {
        var expected = Result(["time", "avg"], Row(1_704_067_200_000L, 29.5));
        var actual = Result(["time", "avg"], Row(1_704_067_260_000L, 30.5));

        var diff = ResultDiffer.DiffSqlResults(
            expected,
            actual,
            new DiffTolerance(1e-9, 1e-9) { TimeBucketMs = 60_000L });

        Assert.False(diff.WithinTolerance);
    }

    private static RelationalSqlResult Result(IReadOnlyList<string> columns, params RelationalSqlRow[] rows)
        => new(columns, rows, -1);

    private static RelationalSqlRow Row(params object?[] values) => new(values);
}
