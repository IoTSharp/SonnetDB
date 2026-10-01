using SonnetDB.Samples;

if (args.Length > 1 || (args.Length == 1 && args[0] != "--keep"))
{
    Console.Error.WriteLine("Usage: SonnetDB.CdcStreamingJourney [--keep]");
    return 2;
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancelHandler;
string directory = Directory.CreateTempSubdirectory("sonnetdb-cdc-stream-journey-").FullName;
bool keep = args.Length == 1;
try
{
    CdcStreamingJourneyResult result = await CdcStreamingRecoveryJourney.RunAsync(directory, cancellation.Token);
    Console.WriteLine($"PASS_LOCAL_ONLY partitions={result.Partitions} rows={result.ReconciledRows} " +
        $"window_count={result.WindowCount} redelivery_attempt={result.RedeliveryAttempt} " +
        $"pending={result.PendingEvents} closed={result.WindowClosed}");
    if (keep)
        Console.WriteLine($"state={directory}");
    return 0;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    if (!keep)
    {
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(directory), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(directory).StartsWith("sonnetdb-cdc-stream-journey-", StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary journey directory ownership mismatch.");
        Directory.Delete(directory, recursive: true);
    }
}
