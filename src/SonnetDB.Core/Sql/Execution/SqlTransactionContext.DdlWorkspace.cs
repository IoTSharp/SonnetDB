using SonnetDB.Engine;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

public sealed partial class SqlTransactionContext
{
    private Tsdb? _ddlWorkspaceDatabase;
    private SqlDdlTransactionWorkspace? _ddlWorkspace;

    internal TableManager? DdlWorkspaceTables => _ddlWorkspace?.Tables;
    internal SqlDdlTransactionWorkspace? DdlWorkspace => _ddlWorkspace;
    internal bool HasDdlWorkspace => _ddlWorkspace is not null;

    internal SqlDdlTransactionWorkspace GetOrCreateDdlWorkspace(
        Tsdb database,
        SqlDdlTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ThrowIfCompleted();
        if (_ddlWorkspace is not null)
        {
            if (!ReferenceEquals(_ddlWorkspaceDatabase, database))
                throw new InvalidOperationException("同一事务不能切换 DDL workspace 所属数据库。");
            return _ddlWorkspace;
        }
        SqlDdlTransactionWorkspace created = SqlDdlTransactionWorkspace.Create(
            database, this, options, cancellationToken);
        _ddlWorkspaceDatabase = database;
        _ddlWorkspace = created;
        return created;
    }

    internal SqlDdlTransactionWorkspace DetachDdlWorkspaceForCommit()
    {
        ThrowIfCompleted();
        SqlDdlTransactionWorkspace workspace = _ddlWorkspace
            ?? throw new InvalidOperationException("当前事务没有 DDL workspace。");
        workspace.PreserveForRecovery();
        _ddlWorkspace = null;
        _ddlWorkspaceDatabase = null;
        return workspace;
    }

    internal void RollbackDdlWorkspace()
    {
        SqlDdlTransactionWorkspace? workspace = _ddlWorkspace;
        _ddlWorkspace = null;
        _ddlWorkspaceDatabase = null;
        workspace?.Dispose();
    }

    internal void PromoteBufferedDmlToDdlWorkspace(SqlDdlTransactionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ThrowIfCompleted();
        IReadOnlyDictionary<TableObjectName, IReadOnlyList<TableRowMutation>> mutations = SnapshotTableMutations();
        if (mutations.Count != 0)
        {
            using var transactionScope = EnterScope(null);
            _ = workspace.Tables.ApplyTransaction(mutations);
        }
        _tableMutations.Clear();
        _mutationSchemas.Clear();
        _undo.Clear();
        _autoIncrementReservationGenerations.Clear();
        _serializableReads.Clear();
        _serializableReservationAdvances.Clear();
    }

    internal void ValidateDdlWorkspacePromotion()
    {
        ThrowIfCompleted();
        if (_deferredTriggers.Count != 0)
            throw new NotSupportedException(
                "已有 deferred trigger 的事务不能再启动持久 DDL workspace；请先提交或回滚当前事务。");
    }
}
