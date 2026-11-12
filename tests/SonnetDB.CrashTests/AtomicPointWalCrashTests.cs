using System.Diagnostics;
using SonnetDB.Model;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>确定性进程强杀后只恢复完整 Point；不代表物理掉电或默认 Tsdb 接入。</summary>
    [Theory]
    [InlineData("before-append", 1)]
    [InlineData("after-os-flush", 2)]
    public void AtomicPointWal_HardKill_ReconcilesWholePoints(string phase, int expectedCount)
    {
        string root = NewScenarioRoot();
        try
        {
            string ready = Path.Combine(root, "atomic.ready");
            using (Process child = StartChild("atomic_point_wal_" + phase, root, ready))
            {
                try
                {
                    WaitForReady(child, ready, TimeSpan.FromSeconds(15));
                    Assert.Equal(phase, File.ReadAllText(ready));
                    Console.WriteLine(File.ReadAllText(Path.Combine(root, "child-process.txt")));
                }
                finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
                Assert.NotEqual(0, child.ExitCode);
            }

            string path = Path.Combine(root, "point.SDBWAL");
            using (var reader = WalReader.OpenAtomicPoints(path))
            {
                var records = reader.Replay().Cast<AtomicPointRecord>().ToArray();
                Assert.Equal(expectedCount, records.Length);
                for (int index = 0; index < expectedCount; index++)
                {
                    AtomicPointRecord record = records[index];
                    Assert.Equal(index + 1, record.Lsn);
                    Assert.Equal((index + 1) * 1000, record.PointTimestamp);
                    Assert.Equal(3, record.Fields.Count);
                    Assert.Equal(index + 1, record.Fields["number"].AsLong());
                    Assert.Equal("point-" + (index + 1), record.Fields["text"].AsString());
                    Assert.Equal(index == 1, record.Fields["flag"].AsBool());
                }
            }
            using (var writer = WalWriter.OpenAtomicPoints(path))
            {
                Assert.Equal(expectedCount + 1, writer.NextLsn);
                writer.AppendAtomicPoint(7, 3000, new Dictionary<string, FieldValue> { ["number"] = FieldValue.FromLong(3) });
            }
            using var reopened = WalReader.OpenAtomicPoints(path);
            Assert.Equal(expectedCount + 1, reopened.Replay().Count());
        }
        finally
        {
            // NewScenarioRoot created this exact direct child of this fixture's GUID root.
            Assert.Equal(Path.GetFullPath(_tempDir), Path.GetDirectoryName(Path.GetFullPath(root)));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
