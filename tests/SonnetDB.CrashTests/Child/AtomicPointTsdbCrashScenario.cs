using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;

internal static class AtomicPointTsdbCrashScenario
{
    internal static void Run(string root, string readyFile, string phase)
    {
        if (phase is not ("before-append" or "after-os-flush" or "after-ack" or "after-checkpoint"))
            throw new ArgumentException("Unknown public Tsdb atomic Point boundary.", nameof(phase));
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = root,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Kv = KvOptions.Default with { ExpirerEnabled = false, CleanupEnabled = false },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        });
        db.Write(Point(1));
        if (phase == "before-append")
            db.BeforeAtomicPointAppendTestHook = Pause;
        if (phase == "after-os-flush")
            db.AfterAtomicPointWalWriteTestHook = FlushAndPause;
        db.Write(Point(2));
        if (phase == "after-checkpoint")
        {
            db.FlushNow();
            db.AfterAtomicPointWalWriteTestHook = FlushAndPause;
            db.Write(Point(3));
        }
        Pause();

        void FlushAndPause()
        {
            db.FlushUnacknowledgedPointBytesToOsForTest();
            Pause();
        }

        void Pause()
        {
            File.WriteAllText(readyFile, phase);
            var elapsed = Stopwatch.StartNew();
            for (int poll = 0; poll < 300 && elapsed.Elapsed < TimeSpan.FromSeconds(30); poll++)
                Thread.Sleep(100);
            throw new TimeoutException("Parent did not kill public Tsdb child at " + phase);
        }
    }

    private static Point Point(int value) => SonnetDB.Model.Point.Create("atomic", value,
        new Dictionary<string, string> { ["host"] = "test" },
        new Dictionary<string, FieldValue>
        {
            ["number"] = FieldValue.FromLong(value),
            ["text"] = FieldValue.FromString("point-" + value),
            ["flag"] = FieldValue.FromBool(value % 2 == 0),
        });
}
