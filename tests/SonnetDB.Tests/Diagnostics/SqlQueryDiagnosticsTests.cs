using System.Diagnostics.Metrics;
using SonnetDB.Contracts;
using SonnetDB.Diagnostics;
using Xunit;

namespace SonnetDB.Tests.Diagnostics;

/// <summary>SQL 查询进程指标的完整性与低基数信号测试。</summary>
public sealed class SqlQueryDiagnosticsTests
{
    /// <summary>降级物理读快照应计数但不得向物理读直方图写入可被误读的零值。</summary>
    [Fact]
    public void Record_DegradedPhysicalReadSnapshot_EmitsCounterWithoutPhysicalReadHistograms()
    {
        var measurements = new List<(string Name, long Value)>();
        var captureCurrentContext = new AsyncLocal<bool>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == "SonnetDB.Server"
                && instrument.Name.StartsWith("sonnetdb.sql.", StringComparison.Ordinal))
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (captureCurrentContext.Value)
                measurements.Add((instrument.Name, value));
        });
        listener.Start();

        var entry = new SlowQueryDiagnosticEntry(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "metrics",
            "SELECT 1",
            "SELECT ?",
            "bounded-snapshot",
            1,
            0,
            0,
            false,
            SlowQuerySeverity.Slow)
        {
            PhysicalReadSnapshotComplete = false,
        };

        captureCurrentContext.Value = true;
        try
        {
            SqlQueryDiagnostics.Record(entry);
        }
        finally
        {
            captureCurrentContext.Value = false;
        }

        Assert.Contains(
            measurements,
            static measurement => measurement is ("sonnetdb.sql.physical.read.snapshot.degraded.count", 1));
        Assert.DoesNotContain(
            measurements,
            static measurement => measurement.Name is "sonnetdb.sql.physical.reads"
                or "sonnetdb.sql.physical.read.bytes");
    }
}
