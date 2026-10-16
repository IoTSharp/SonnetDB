using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

public sealed partial class SqlTransactionContext
{
    private readonly Dictionary<TableRowLockManager, TableRowLockManager.Owner> _rowLockOwners = [];

    internal TableRowLockManager.Owner GetRowLockOwner(TableRowLockManager manager)
    {
        ThrowIfCompleted();
        if (!_rowLockOwners.TryGetValue(manager, out var owner))
        {
            owner = manager.CreateOwner();
            _rowLockOwners.Add(manager, owner);
        }
        return owner;
    }

    private void ReleaseRowLocks()
    {
        foreach (var owner in _rowLockOwners.Values) owner.Dispose();
        _rowLockOwners.Clear();
    }
}
