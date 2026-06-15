using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: SonnetDB.CrashTests.Child <scenario> <root> <ready-file>");
    return 2;
}

string scenario = args[0];
string root = args[1];
string readyFile = args[2];

Directory.CreateDirectory(root);

switch (scenario)
{
    case "crash_kill9_during_fsync":
        RunKillDuringFsync(root, readyFile);
        return 0;
    case "crash_kill9_mid_compaction":
        RunKillMidCompaction(root, readyFile);
        return 0;
    default:
        Console.Error.WriteLine($"Unknown scenario '{scenario}'.");
        return 3;
}

static void RunKillDuringFsync(string root, string readyFile)
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = true,
        FlushPolicy = new MemTableFlushPolicy
        {
            MaxPoints = long.MaxValue,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.MaxValue,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    File.WriteAllText(readyFile, "ready");

    long timestamp = 1_000;
    while (true)
    {
        db.Write(MakePoint("fsync", timestamp, "kill9", timestamp));
        timestamp++;
    }
}

static void RunKillMidCompaction(string root, string readyFile)
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = true,
        FlushPolicy = new MemTableFlushPolicy
        {
            MaxPoints = long.MaxValue,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.MaxValue,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy
        {
            Enabled = true,
            MinTierSize = 2,
            FirstTierMaxBytes = 1024 * 1024,
            PollInterval = TimeSpan.FromMilliseconds(10),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        },
    });

    for (int segment = 0; segment < 4; segment++)
    {
        for (int i = 0; i < 32; i++)
            db.Write(MakePoint("compact", segment * 1_000L + i, $"h{segment}", i));

        db.FlushNow();
    }

    File.WriteAllText(readyFile, "ready");
    Thread.Sleep(Timeout.Infinite);
}

static Point MakePoint(string measurement, long timestamp, string host, double value)
    => Point.Create(
        measurement,
        timestamp,
        new Dictionary<string, string> { ["host"] = host },
        new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(value) });
