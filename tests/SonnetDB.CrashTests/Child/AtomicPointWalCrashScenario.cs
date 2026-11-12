using System.Diagnostics;
using SonnetDB.Model;
using SonnetDB.Wal;

internal static class AtomicPointWalCrashScenario
{
    internal static void Run(string root, string readyFile, string phase)
    {
        if (phase is not ("before-append" or "after-os-flush"))
            throw new ArgumentException("Unknown atomic Point crash boundary.", nameof(phase));

        using var writer = WalWriter.OpenAtomicPoints(Path.Combine(root, "point.SDBWAL"));
        writer.AppendAtomicPoint(7, 1000, Fields(1));
        writer.Sync();
        if (phase == "before-append")
            Pause();
        writer.AppendAtomicPoint(7, 2000, Fields(2));
        writer.Flush(); // Only OS flush; process kill cannot establish physical power-loss durability.
        Pause();

        void Pause()
        {
            File.WriteAllText(readyFile, phase);
            var deadline = Stopwatch.StartNew();
            for (int poll = 0; poll < 300 && deadline.Elapsed < TimeSpan.FromSeconds(30); poll++)
                Thread.Sleep(100);
            throw new TimeoutException("Parent did not kill atomic Point child at " + phase);
        }
    }

    private static Dictionary<string, FieldValue> Fields(long value) => new()
    {
        ["number"] = FieldValue.FromLong(value),
        ["text"] = FieldValue.FromString("point-" + value),
        ["flag"] = FieldValue.FromBool(value == 2),
    };
}
