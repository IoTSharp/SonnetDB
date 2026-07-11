using SonnetDB.Contracts;
using SonnetDB.Diagnostics;
using Xunit;

namespace SonnetDB.Tests.Diagnostics;

/// <summary>
/// M17 #95 慢查询 SQL 指纹与环形缓冲测试。
/// </summary>
public sealed class SlowQueryRingTests
{
    [Fact]
    public void Normalize_WithDifferentLiteralsAndComments_ReturnsSameFingerprint()
    {
        var first = SqlFingerprint.Normalize("select * from cpu where host = 'edge-01' and time >= 1000 -- sample");
        var second = SqlFingerprint.Normalize("SELECT * FROM cpu WHERE host = 'edge-99' AND time >= 9000");

        Assert.Equal(first, second);
        Assert.DoesNotContain("edge-01", first, StringComparison.Ordinal);
        Assert.Equal(SqlFingerprint.Compute(first), SqlFingerprint.Compute(second));
    }

    [Fact]
    public void Add_WhenCapacityExceeded_OverwritesOldestEntry()
    {
        var ring = new SlowQueryRing(3);
        for (var index = 1; index <= 4; index++)
            ring.Add(CreateEntry(index, $"SELECT {index}", $"shape-{index}", index));

        var snapshot = ring.Snapshot(static _ => true);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(["SELECT 4", "SELECT 3", "SELECT 2"], snapshot.Select(static item => item.Sql));
    }

    [Fact]
    public void Top_WithRepeatedFingerprint_ComputesNearestRankPercentiles()
    {
        var ring = new SlowQueryRing(8);
        ring.Add(CreateEntry(1, "SELECT 1", "shape-a", 10));
        ring.Add(CreateEntry(2, "SELECT 2", "shape-a", 20, failed: true));
        ring.Add(CreateEntry(3, "SELECT 3", "shape-a", 100));
        ring.Add(CreateEntry(4, "SELECT 4", "shape-b", 50));

        var (items, sampleCount) = ring.Top(static _ => true, 10);

        Assert.Equal(4, sampleCount);
        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("shape-a", first.Fingerprint);
        Assert.Equal(3, first.Count);
        Assert.Equal(1, first.FailedCount);
        Assert.Equal(20, first.P50Ms);
        Assert.Equal(100, first.P95Ms);
        Assert.Equal(100, first.MaxMs);
    }

    private static SlowQueryDiagnosticEntry CreateEntry(
        long timestamp,
        string sql,
        string fingerprint,
        double elapsedMs,
        bool failed = false)
        => new(
            timestamp,
            "factory",
            sql,
            fingerprint,
            fingerprint,
            elapsedMs,
            0,
            -1,
            failed,
            SlowQuerySeverity.Slow);
}
