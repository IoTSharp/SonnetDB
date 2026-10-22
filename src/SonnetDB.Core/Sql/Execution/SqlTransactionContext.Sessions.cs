namespace SonnetDB.Sql.Execution;

public sealed partial class SqlTransactionContext
{
    private SqlSessionContext? _session;

    internal SqlSessionContext? SessionContext => _session;

    internal void AttachSession(SqlSessionContext session)
    {
        ThrowIfCompleted();
        if (_session is not null && !ReferenceEquals(_session, session))
            throw new InvalidOperationException("事务不能跨连接会话使用。");
        session.AttachTransaction(this);
        _session = session;
    }

    internal void RecordSessionUndo(Action undo)
    {
        ThrowIfCompleted();
        _undo.Add(undo);
    }

    internal void CompleteSession()
    {
        SqlSessionContext? session = _session;
        _session = null;
        session?.CompleteTransaction(this, WasCommitted);
    }
}
