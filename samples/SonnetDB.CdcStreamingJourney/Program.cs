using SonnetDB.Samples;

if (args.Length > 2 || args.Any(static argument => argument is not ("--keep" or "--bridge" or "--budgets" or "--topology"))
    || args.Distinct(StringComparer.Ordinal).Count() != args.Length
    || args.Count(static argument => argument is "--bridge" or "--budgets" or "--topology") > 1)
{
    Console.Error.WriteLine("Usage: SonnetDB.CdcStreamingJourney [--bridge | --budgets | --topology] [--keep]");
    return 2;
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancelHandler;
string directory = Directory.CreateTempSubdirectory("sonnetdb-cdc-stream-journey-").FullName;
bool keep = args.Contains("--keep", StringComparer.Ordinal);
try
{
    if (args.Contains("--topology", StringComparer.Ordinal))
    {
        CdcStreamingTaskJourneyResult result = await CdcStreamingTaskJourney.RunAsync(directory, cancellation.Token);
        Console.WriteLine($"PASS_LOCAL_ONLY topology partitions={result.Partitions} events={result.Events} " +
            $"window_count={result.WindowCount} redelivery_attempt={result.RedeliveryAttempt} sorted_time={result.SortedTime} reopened=true");
    }
    else if (args.Contains("--budgets", StringComparer.Ordinal))
    {
        SqlBudgetJourneyResult result = await SqlBudgetJourney.RunAsync(directory, cancellation.Token);
        Console.WriteLine($"PASS_LOCAL_ONLY budgets imported={result.ImportedRows} vector_id={result.VectorId} " +
            $"knn_time={result.KnnTime} rejected={result.RejectedQueries} reopened=true");
    }
    else if (args.Contains("--bridge", StringComparer.Ordinal))
    {
        CdcStreamingBridgeJourneyResult result = await CdcStreamingBridgeJourney.RunAsync(directory, cancellation.Token);
        Console.WriteLine($"PASS_LOCAL_ONLY bridge source_offset={result.SourceOffset} target_sequence={result.TargetSequence} " +
            $"window_count={result.WindowCount} redelivery_attempt={result.RedeliveryAttempt} pending={result.PendingEvents}");
    }
    else
    {
        CdcStreamingJourneyResult result = await CdcStreamingRecoveryJourney.RunAsync(directory, cancellation.Token);
        Console.WriteLine($"PASS_LOCAL_ONLY partitions={result.Partitions} rows={result.ReconciledRows} " +
            $"window_count={result.WindowCount} redelivery_attempt={result.RedeliveryAttempt} " +
            $"pending={result.PendingEvents} closed={result.WindowClosed}");
    }
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
