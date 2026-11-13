using System.Diagnostics;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Query;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>真实公共 Tsdb API 的确定性进程强杀恢复；不证明物理掉电耐久。</summary>
    [Theory]
    [InlineData("before-append", 1)]
    [InlineData("after-os-flush", 2)]
    [InlineData("after-ack", 2)]
    [InlineData("after-checkpoint", 3)]
    public void AtomicPointTsdb_HardKill_ReconcilesEveryFieldOnce(string phase, int expectedCount)
    {
        string root = NewScenarioRoot();
        try
        {
            string ready = Path.Combine(root, "atomic-tsdb.ready");
            using (Process child = StartChild("atomic_point_tsdb_" + phase, root, ready))
            {
                try
                {
                    WaitForReady(child, ready, TimeSpan.FromSeconds(15));
                    Assert.Equal(phase, File.ReadAllText(ready));
                    Console.WriteLine(File.ReadAllText(Path.Combine(root, "child-process.txt")));
                }
                finally
                {
                    TerminateAndWait(child, TimeSpan.FromSeconds(10));
                }
                Assert.NotEqual(0, child.ExitCode);
            }

            for (int reopen = 0; reopen < 3; reopen++)
            {
                using var db = Tsdb.Open(new TsdbOptions
                {
                    RootDirectory = root,
                    BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
                    Compaction = new CompactionPolicy { Enabled = false },
                    Kv = KvOptions.Default with { ExpirerEnabled = false, CleanupEnabled = false },
                });
                SeriesEntry series = Assert.Single(db.Catalog.Find("atomic", new Dictionary<string, string> { ["host"] = "test" }));
                long[] expected = Enumerable.Range(1, expectedCount).Select(value => (long)value).ToArray();
                Assert.Equal(expected, db.Query.Execute(new PointQuery(series.Id, "number", TimeRange.All)).Select(point => point.Value.AsLong()));
                Assert.Equal(expected.Select(value => "point-" + value), db.Query.Execute(new PointQuery(series.Id, "text", TimeRange.All)).Select(point => point.Value.AsString()));
                Assert.Equal(expected.Select(value => value % 2 == 0), db.Query.Execute(new PointQuery(series.Id, "flag", TimeRange.All)).Select(point => point.Value.AsBool()));
            }
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(_tempDir), Path.GetDirectoryName(Path.GetFullPath(root)));
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
