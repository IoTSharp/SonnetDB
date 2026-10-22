using System.Text;
using SonnetDB.Sql.Execution;
using SonnetDB.Wal;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    private const int DdlCatalogFloor = 19;
    private const int DdlCopyEntryLimit = 1_000_000;
    private const int DdlCopyDepthLimit = 64;

    /// <summary>为关系 DDL 事务取得数据库排它写 lease，并保留到事务完成。</summary>
    internal void AcquireDdlTransactionLease(
        SqlTransactionContext transaction,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        transaction.ThrowIfCompleted();
        var owner = transaction.GetRowLockOwner(RowLocks);
        using var lease = RowLocks.Acquire(
            owner,
            [TableRowLockManager.Resource.DatabaseExclusive],
            noWait: false,
            timeout,
            cancellationToken);
        lease.Retain();
    }

    /// <summary>忽略连接 TEMP 联合目录，打开持久关系表供 DDL workspace 克隆。</summary>
    internal TableStore OpenPersistentForDdl(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            TableSchema schema = _catalog.TryGet(name)
                ?? throw new InvalidOperationException($"table '{name}' 不存在。");
            return OpenStoreLocked(schema);
        }
    }

    /// <summary>取得排它 lease 后复验事务首次读取和写集 schema，冻结 workspace 克隆基线。</summary>
    internal void ValidateDdlTransactionBaseline(
        SqlTransactionContext transaction,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        transaction.ThrowIfCompleted();
        lock (_sync)
        {
            ThrowIfDisposed();
            var stores = new List<TableStore>();
            foreach (SqlTransactionContext.SerializableTableRead read in transaction.SerializableReads
                .OrderBy(static read => read.Schema.Name, StringComparer.Ordinal))
            {
                if (!_stores.ContainsKey(read.Schema.Name))
                    _ = OpenStoreLocked(read.Schema);
                TableStore store = _stores[read.Schema.Name];
                if (!stores.Contains(store)) stores.Add(store);
            }
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                foreach (TableStore store in stores)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TimeSpan remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero || !Monitor.TryEnter(store.SynchronizationRoot, remaining))
                        throw new TimeoutException("DDL 事务基线复验等待关系表锁超时。");
                }
                transaction.ValidateMutationSchemas(_catalog);
                ValidateSerializableReadsLocked(transaction);
            }
            finally
            {
                for (int index = stores.Count - 1; index >= 0; index--)
                    if (Monitor.IsEntered(stores[index].SynchronizationRoot))
                        Monitor.Exit(stores[index].SynchronizationRoot);
            }
        }
    }

    /// <summary>把私有 DDL workspace 按 durable journal 发布为当前关系目录。</summary>
    internal void CommitDdlWorkspace(
        SqlDdlTransactionWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!ReferenceEquals(workspace.SourceDatabase.Tables, this))
            throw new InvalidOperationException("DDL workspace 不属于当前关系目录。");
        workspace.SealForCommit();

        string journalPath = Path.Combine(_rootDirectory, TableDdlTransactionJournal.FileName);
        string workspaceRelative = Path.GetRelativePath(_rootDirectory, workspace.DirectoryPath);
        string undoRelative = Path.Combine(".ddl-transactions", "undo-" + workspace.Owner.ToString("N"));
        var changedObjects = BuildDdlChangedObjects(workspace);
        var journal = new TableDdlTransactionJournal(
            workspace.Owner,
            TableDdlTransactionDecision.Prepared,
            workspaceRelative,
            undoRelative,
            DdlCatalogFloor,
            changedObjects);
        var backend = new DdlTransactionBackend(this, workspace.Options);
        TableDdlTransactionCommitCoordinator.Commit(journalPath, journal, backend, cancellationToken);
    }

    private IReadOnlyList<TableDdlObjectIdentity> BuildDdlChangedObjects(SqlDdlTransactionWorkspace workspace)
    {
        IReadOnlyList<TableSchema> before = _catalog.Snapshot();
        IReadOnlyList<TableSchema> after = workspace.Tables.Catalog.Snapshot();
        var names = new HashSet<TableObjectName>(TableObjectName.ExactComparer);
        foreach (TableSchema schema in before)
            names.Add(schema.ObjectName);
        foreach (TableSchema schema in after)
            names.Add(schema.ObjectName);
        return names
            .OrderBy(static name => name.SchemaName, StringComparer.Ordinal)
            .ThenBy(static name => name.Name, StringComparer.Ordinal)
            .Select(static name => new TableDdlObjectIdentity(name.SchemaName, name.Name))
            .ToArray();
    }

    /// <summary>启动时在任何 rowstore 打开前按 journal durable 决策恢复 DDL 目录。</summary>
    private void RecoverDdlTransactionBeforeOpeningCatalog()
    {
        string journalPath = Path.Combine(_rootDirectory, TableDdlTransactionJournal.FileName);
        if (TableDdlTransactionJournal.Read(journalPath) is null)
            return;

        var backend = new DdlTransactionBackend(this, new SqlDdlTransactionOptions());
        _ = TableDdlTransactionRecovery.Recover(journalPath, backend);
    }

    private sealed class DdlTransactionBackend(
        TableManager manager,
        SqlDdlTransactionOptions options) : ITableDdlTransactionCommitBackend
    {
        private readonly TableManager _manager = manager;
        private readonly SqlDdlTransactionOptions _options = options;

        public void PrepareUndo(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var publication = _manager.EnterDdlPublication();
            string undo = ResolveRelative(journal.UndoRelativePath);
            if (Directory.Exists(undo) || File.Exists(undo))
                throw new IOException("DDL undo 目录已存在，拒绝覆盖。");
            Directory.CreateDirectory(undo);
            WriteOwner(undo, journal.Owner);
            CopyOptionalEntry(_manager.SchemaPath, Path.Combine(undo, TableSchemaCodec.FileName), cancellationToken);
            CopyOptionalEntry(
                Path.Combine(_manager._rootDirectory, "rowstore"),
                Path.Combine(undo, "rowstore"),
                cancellationToken);
            DirectoryFsync.FlushRequired(undo);
            DirectoryFsync.FlushRequired(Path.GetDirectoryName(undo)!);
        }

        public void PublishPrepared(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string workspace = ResolveRelative(journal.WorkspaceRelativePath);
            string undo = ResolveRelative(journal.UndoRelativePath);
            string workspaceTables = Path.Combine(workspace, "tables");
            string oldSchema = Path.Combine(undo, "published-old.tblschema");
            string oldRowstore = Path.Combine(undo, "published-old-rowstore");
            using var publication = _manager.EnterDdlPublication();
            MoveOptionalEntry(_manager.SchemaPath, oldSchema);
            MoveOptionalEntry(Path.Combine(_manager._rootDirectory, "rowstore"), oldRowstore);
            MoveOptionalEntry(Path.Combine(workspaceTables, TableSchemaCodec.FileName), _manager.SchemaPath);
            MoveOptionalEntry(Path.Combine(workspaceTables, "rowstore"), Path.Combine(_manager._rootDirectory, "rowstore"));
            DirectoryFsync.FlushRequired(_manager._rootDirectory);
            _manager.ReloadPublishedCatalogLocked();
        }

        public void RollbackPrepared(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string undo = ResolveRelative(journal.UndoRelativePath);
            string workspace = ResolveRelative(journal.WorkspaceRelativePath);
            using var publication = _manager.EnterDdlPublication();
            DeleteOptionalEntry(_manager.SchemaPath);
            DeleteOptionalEntry(Path.Combine(_manager._rootDirectory, "rowstore"));
            CopyOptionalEntry(Path.Combine(undo, TableSchemaCodec.FileName), _manager.SchemaPath, cancellationToken);
            CopyOptionalEntry(Path.Combine(undo, "rowstore"), Path.Combine(_manager._rootDirectory, "rowstore"), cancellationToken);
            DeleteOptionalEntry(Path.Combine(undo, "published-old.tblschema"));
            DeleteOptionalEntry(Path.Combine(undo, "published-old-rowstore"));
            _manager.ReloadPublishedCatalogLocked();
            CleanupOwnedDirectory(undo, journal.Owner);
            CleanupOwnedDirectory(workspace, journal.Owner);
        }

        public void CompleteCommitted(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupOwnedDirectory(ResolveRelative(journal.UndoRelativePath), journal.Owner);
            CleanupOwnedDirectory(ResolveRelative(journal.WorkspaceRelativePath), journal.Owner);
        }

        private string ResolveRelative(string relative)
        {
            string root = Path.GetFullPath(_manager._rootDirectory);
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DDL journal 路径越过 tables 根目录。");
            return path;
        }

        private static void WriteOwner(string directory, Guid owner)
        {
            using var marker = new FileStream(
                Path.Combine(directory, SqlDdlTransactionWorkspace.OwnerFileName),
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            marker.Write(Encoding.ASCII.GetBytes(owner.ToString("N")));
            marker.Flush(flushToDisk: true);
        }

        private static void CopyOptionalEntry(string source, string destination, CancellationToken cancellationToken)
        {
            if (!File.Exists(source) && !Directory.Exists(source))
                return;
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            var pending = new Queue<(string Source, string Destination, int Depth)>();
            pending.Enqueue((source, destination, 0));
            int entries = 0;
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string currentSource, string currentDestination, int depth) = pending.Dequeue();
                if (depth > DdlCopyDepthLimit)
                    throw new IOException("DDL 目录复制深度超过预算。");
                Directory.CreateDirectory(currentDestination);
                foreach (string entry in Directory.EnumerateFileSystemEntries(currentSource))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++entries > DdlCopyEntryLimit)
                        throw new IOException("DDL 目录复制条目超过预算。");
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("DDL 目录包含链接，拒绝复制。");
                    string target = Path.Combine(currentDestination, Path.GetFileName(entry));
                    if (Directory.Exists(entry))
                        pending.Enqueue((entry, target, depth + 1));
                    else
                        File.Copy(entry, target, overwrite: false);
                }
            }
        }

        private static void MoveOptionalEntry(string source, string destination)
        {
            if (!File.Exists(source) && !Directory.Exists(source))
                return;
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"DDL 发布目标已存在: {destination}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(source))
                Directory.Move(source, destination);
            else
                File.Move(source, destination);
        }

        private static void DeleteOptionalEntry(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }

        private static void CleanupOwnedDirectory(string path, Guid owner)
        {
            if (!Directory.Exists(path))
                return;
            SqlDdlTransactionWorkspace.DeleteOwnedDirectory(path, owner, new SqlDdlTransactionOptions());
        }
    }

    private IDisposable EnterDdlPublication()
    {
        Monitor.Enter(_schemaSync);
        bool syncTaken = false;
        try
        {
            Monitor.Enter(_sync);
            syncTaken = true;
            CloseStoresForDdlPublicationLocked();
            return new DdlPublicationScope(this);
        }
        catch
        {
            if (syncTaken)
                Monitor.Exit(_sync);
            Monitor.Exit(_schemaSync);
            throw;
        }
    }

    private void CloseStoresForDdlPublicationLocked()
    {
        List<Exception>? failures = null;
        foreach (TableStore store in _stores.Values)
        {
            try { store.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        _stores.Clear();
        _tableLockTokens.Clear();
        if (failures is not null)
            throw new AggregateException("关闭 DDL 发布前关系表存储失败。", failures);
    }

    private void ReloadPublishedCatalogLocked()
    {
        IReadOnlyList<TableSchema> schemas = TableSchemaCodec.Load(SchemaPath);
        _catalog.PrepareReplacement(schemas)();
    }

    private sealed class DdlPublicationScope(TableManager manager) : IDisposable
    {
        private readonly TableManager _manager = manager;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Monitor.Exit(_manager._sync);
            Monitor.Exit(_manager._schemaSync);
        }
    }
}
