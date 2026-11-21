using SonnetDB.Routines;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableStore
{
    internal bool TryCountCommittedPrefixGroups(TableSchema expectedSchema, object?[] primaryPrefix,
        out IReadOnlyList<IReadOnlyList<object?>> groups)
    {
        groups = [];
        using TableReadSnapshot snapshot = AcquireTableReadSnapshot();
        if (!ReferenceEquals(expectedSchema, snapshot.Schema)) return false;
        TableColumn column = snapshot.Schema.TryGetColumn(snapshot.Schema.PrimaryKey[primaryPrefix.Length])!;
        byte[] prefix = TableIndexCodec.EncodePrimaryRowKey(
            TableKeyCodec.EncodePrimaryKeyPrefix(snapshot.Schema, primaryPrefix));
        CancellationToken token = RoutineExecutionContext.Current?.Options.CancellationToken ?? CancellationToken.None;
        using var reservation = SqlQueryResources.Current?.CreateReservation();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = new List<IReadOnlyList<object?>>();
        byte[]? previous = null;
        object?[]? current = null;
        long count = 0;
        bool supported = snapshot.Snapshot.TryCountLsmPrefixKeys(prefix, snapshot.RowCount, token,
            SqlExecutor.ThrowIfCancellationRequested, out _, remaining =>
            {
                int length = TableKeyCodec.PrefixGroupComponentLength(column, remaining);
                ReadOnlySpan<byte> component = remaining[..length];
                if (previous is not null && component.SequenceEqual(previous))
                {
                    count++;
                    return true;
                }
                if (current is not null) current[1] = count;
                current = [TableKeyCodec.DecodePrefixGroupComponent(column, component), 0L];
                // If groups do not fit the operator budget, discard this candidate
                // and let the original aggregation path use its spill machinery.
                long bytes = checked(SqlSpillRowCodec.EstimateRowBytes(current) + component.Length + 80L);
                if (reservation is not null && !reservation.TryReserve(bytes)) return false;
                rows.Add(current);
                previous = component.ToArray();
                count = 1;
                return true;
            });
        if (!supported) return false;
        if (current is not null) current[1] = count;
        for (int index = 0; index < rows.Count; index++)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(30))
                throw new TimeoutException("主键前缀分组计数超过安全执行时限。");
            SqlRowRetentionBudget.RetainForExecution(rows[index]);
        }
        groups = rows;
        return true;
    }
}
