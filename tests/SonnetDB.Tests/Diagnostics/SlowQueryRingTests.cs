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

    [Fact]
    public void Top_WhenSampleRingWraps_PreservesLifetimeFingerprintCounts()
    {
        var ring = new SlowQueryRing(capacity: 2, aggregateCapacity: 4);
        for (int index = 1; index <= 7; index++)
            ring.Add(CreateEntry(index, $"SELECT {index}", "shape-a", index));

        var snapshot = ring.Snapshot(static _ => true);
        var (items, sampleCount) = ring.Top(static _ => true, 10);

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(7, sampleCount);
        var item = Assert.Single(items);
        Assert.Equal(7, item.Count);
        Assert.Equal(7, item.LifetimeCount);
    }

    [Fact]
    public void Observe_WhenFingerprintCapacityIsFull_CountsUnattributedSamplesWithoutGrowth()
    {
        var ring = new SlowQueryRing(capacity: 2, aggregateCapacity: 1);
        ring.Observe(CreateEntry(1, "SELECT 1", "shape-a", 1));
        ring.Observe(CreateEntry(2, "SELECT 2", "shape-b", 2));
        ring.Observe(CreateEntry(3, "SELECT 3", "shape-c", 3));

        var (items, sampleCount) = ring.Top(static _ => true, 10);

        Assert.Single(items);
        Assert.Equal(1, sampleCount);
        Assert.Equal(2, ring.UnattributedSampleCount);
        Assert.Equal(1, ring.AggregateCapacity);
    }

    [Fact]
    public void Top_WithExecutionEvidence_AggregatesRowsWaitsAndAllocations()
    {
        var ring = new SlowQueryRing(4);
        ring.Add(CreateEntry(1, "SELECT 1", "shape-a", 10) with
        {
            CandidateRows = 4,
            ExaminedRows = 3,
            LogicalReads = 4,
            QueueWaitMs = 2,
            TableLockWaitMs = 1,
            PhysicalReadBytes = 10,
            PhysicalWriteBytes = 20,
            WalFsyncMs = 0.5,
            WalFsyncCount = 1,
            ExecutionElapsedMs = 8,
            Gen0Collections = 1,
            AllocatedBytes = 128,
            AccessPath = "secondary_index",
        });
        ring.Add(CreateEntry(2, "SELECT 2", "shape-a", 20) with
        {
            CandidateRows = 2,
            ExaminedRows = 2,
            LogicalReads = 2,
            QueueWaitMs = 3,
            KvLockWaitMs = 4,
            PhysicalReadBytes = 30,
            PhysicalWriteBytes = 40,
            WalFsyncMs = 1.5,
            WalFsyncCount = 2,
            ExecutionElapsedMs = 16,
            Gen1Collections = 1,
            AllocatedBytes = 256,
            AccessPath = "secondary_index",
        });

        var (items, _) = ring.Top(static _ => true, 10);
        var item = Assert.Single(items);

        Assert.Equal(6, item.CandidateRows);
        Assert.Equal(5, item.ExaminedRows);
        Assert.Equal(6, item.LogicalReads);
        Assert.Equal(5, item.QueueWaitMs);
        Assert.Equal(5, item.LockWaitMs);
        Assert.Equal(1, item.TableLockWaitMs);
        Assert.Equal(4, item.KvLockWaitMs);
        Assert.Equal(40, item.PhysicalReadBytes);
        Assert.Equal(60, item.PhysicalWriteBytes);
        Assert.Equal(2, item.WalFsyncMs);
        Assert.Equal(3, item.WalFsyncCount);
        Assert.Equal(24, item.ExecutionElapsedMs);
        Assert.Equal(1, item.Gen0Collections);
        Assert.Equal(1, item.Gen1Collections);
        Assert.Equal(384, item.AllocatedBytes);
        Assert.Equal("secondary_index", item.AccessPath);
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
