using System.Diagnostics;
using SonnetDB.Exceptions;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableStore
{
    internal void EnterReadLock()
    {
        var transaction = SqlTransactionContext.Current;
        if (transaction?.IsSerializableCommitInProgress != true)
        {
            Monitor.Enter(_sync);
            return;
        }
        bool taken = false;
        bool handedOff = false;
        long started = Stopwatch.GetTimestamp();
        try
        {
            for (int attempt = 0; attempt < 2_401; attempt++)
            {
                transaction.CheckCommitDeadline();
                int remaining = 120_000 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remaining <= 0) break;
                Monitor.TryEnter(_sync, Math.Min(50, remaining), ref taken);
                if (!taken) continue;
                transaction.CheckCommitDeadline();
                handedOff = true;
                return;
            }
            throw new RoutineExecutionException(RoutineErrorCodes.CommitTimeout,
                "可串行化事务提交准备等待表锁超时，尚未开始持久化。");
        }
        finally
        {
            if (taken && !handedOff) Monitor.Exit(_sync);
        }
    }

    private ReadLockScope AcquireReadLock()
    {
        EnterReadLock();
        return new ReadLockScope(_sync);
    }

    private readonly struct ReadLockScope(object synchronizationRoot) : IDisposable
    {
        public void Dispose() => Monitor.Exit(synchronizationRoot);
    }

    private void RecordSerializableReadLocked(long? sequence = null)
        => SqlTransactionContext.Current?.RecordSerializableRead(
            this, _schema, _keyspace.Generation, sequence ?? _keyspace.LastSequence);

    internal bool MatchesSerializableRead(SqlTransactionContext.SerializableTableRead read, long expectedSequence)
    {
        // 调用方从校验至最终应用一直持有 manager 和所有 store 锁。
        return !_disposed && !_disposing && ReferenceEquals(read.Store, this)
            && ReferenceEquals(read.Schema, _schema)
            && read.Generation == _keyspace.Generation
            && expectedSequence == _keyspace.LastSequence;
    }
}
