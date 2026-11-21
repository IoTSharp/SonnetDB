using SonnetDB.Routines;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableStore
{
    internal bool TryCountCommittedRows(TableSchema expectedSchema, object?[]? primaryPrefix, out long count)
    {
        count = 0;
        SqlExecutor.ThrowIfCancellationRequested();
        if (primaryPrefix is null)
        {
            using (AcquireReadLock())
            {
                ThrowIfDisposedLocked();
                if (!ReferenceEquals(expectedSchema, _schema)) return false;
                RecordSerializableReadLocked();
                count = _rowCount;
            }
            SqlExecutor.ThrowIfCancellationRequested();
            return true;
        }

        using TableReadSnapshot snapshot = AcquireTableReadSnapshot();
        if (!ReferenceEquals(expectedSchema, snapshot.Schema)) return false;
        byte[] prefix = TableIndexCodec.EncodePrimaryRowKey(
            TableKeyCodec.EncodePrimaryKeyPrefix(snapshot.Schema, primaryPrefix));
        CancellationToken token = RoutineExecutionContext.Current?.Options.CancellationToken ?? CancellationToken.None;
        return snapshot.Snapshot.TryCountLsmPrefixKeys(prefix, snapshot.RowCount, token,
            SqlExecutor.ThrowIfCancellationRequested, out count);
    }
}
