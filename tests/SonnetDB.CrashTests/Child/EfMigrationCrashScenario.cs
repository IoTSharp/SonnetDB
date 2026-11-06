using System.Data.Common;
using InventoryMigrationApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SonnetDB.EntityFrameworkCore.Extensions;
using SonnetDB.Tables;

internal static class EfMigrationCrashScenario
{
    internal static async Task RunAsync(string root, string readyFile, string phase)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string connection = "Data Source=" + Path.Combine(root, "database");
        if (phase is "verify-old" or "verify-new" or "roundtrip" or "initialize")
        {
            await InventoryWorkflow.RunAsync(phase, connection, deadline.Token);
            return;
        }
        if (phase.StartsWith("recover-", StringComparison.Ordinal))
        {
            TableDdlTransactionRecovery.AfterBackendTestHook = _ => Pause(phase);
            await InventoryWorkflow.RunAsync(phase == "recover-old" ? "verify-old" : "verify-new", connection, deadline.Token);
            throw new InvalidOperationException("Recovery boundary not reached.");
        }

        await InventoryWorkflow.RunAsync("initialize", connection, deadline.Token);
        var observer = new MigrationBoundaryObserver();
        var options = new DbContextOptionsBuilder<InventoryContext>().UseSonnetDB(connection)
            .AddInterceptors(observer).Options;
        await using var context = new InventoryContext(options);
        TableDdlTransactionCommitCoordinator.PhaseTestHook = current =>
        {
            if (observer.HistoryWritten && current == phase) Pause(phase);
        };
        observer.BeforeCommit = () =>
        {
            if (observer.HistoryWritten && phase == "before-commit") Pause(phase);
        };
        await context.GetService<IMigrator>().MigrateAsync(InventoryShipping.Id, deadline.Token);
        if (phase == "after-ack") Pause(phase);
        throw new InvalidOperationException("Migration kill boundary not reached: " + phase);

        void Pause(string boundary)
        {
            // File is atomically published only after this precise real migration/recovery phase.
            string temporary = readyFile + ".tmp";
            using (var marker = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(marker, leaveOpen: true))
            {
                writer.Write(boundary);
                writer.Flush();
                marker.Flush(flushToDisk: true);
            }
            File.Move(temporary, readyFile);
            using var stopped = new ManualResetEventSlim(false);
            if (!stopped.Wait(TimeSpan.FromSeconds(60), deadline.Token))
                throw new TimeoutException("Parent did not kill migration child within 60 seconds.");
        }
    }

    private sealed class MigrationBoundaryObserver : DbCommandInterceptor, IDbTransactionInterceptor
    {
        internal bool HistoryWritten { get; private set; }
        internal Action? BeforeCommit { get; set; }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal)
                && command.CommandText.Contains(InventoryShipping.Id, StringComparison.Ordinal)) HistoryWritten = true;
            return ValueTask.FromResult(result);
        }

        ValueTask<InterceptionResult> IDbTransactionInterceptor.TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken)
        {
            BeforeCommit?.Invoke();
            return ValueTask.FromResult(result);
        }
    }
}
