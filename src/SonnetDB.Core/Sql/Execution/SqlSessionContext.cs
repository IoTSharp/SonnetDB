using System.Diagnostics;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>由单个逻辑连接拥有、绑定一个数据库实例的临时关系对象会话。</summary>
/// <remarks>临时对象不写入数据库持久 catalog；提交事务保留对象，关闭会话丢弃全部临时状态。</remarks>
public sealed class SqlSessionContext : IDisposable
{
    private static readonly AsyncLocal<SqlSessionContext?> Ambient = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<SqlTemporaryTableWorkspace> _workspaces = [];
    private readonly string _ownerToken = Guid.NewGuid().ToString("N");
    private readonly string _scratchDirectory;
    private SqlTemporaryTableWorkspace? _committed;
    private SqlTemporaryTableWorkspace? _working;
    private SqlTemporaryTableWorkspace? _executing;
    private SqlTransactionContext? _transaction;
    private TableCatalog? _catalogView;
    private bool _scratchCreated;
    private volatile bool _disposed;
    private bool _cleaned;
    private Exception? _deferredCleanupFailure;

    /// <summary>创建绑定指定数据库的新逻辑连接会话。</summary>
    /// <param name="database">会话唯一所属的数据库实例。</param>
    /// <param name="options">临时对象的容量、复制和回收预算。</param>
    public SqlSessionContext(Tsdb database, SqlSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database;
        Options = options ?? new SqlSessionOptions();
        Options.Validate();
        Id = Guid.NewGuid();
        string root = Path.GetFullPath(Options.ScratchRootDirectory ?? Path.GetTempPath());
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("会话临时目录的父目录必须已经存在。");
        _scratchDirectory = Path.Combine(root, "sonnetdb-sql-session-" + Id.ToString("N"));
        string databaseRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(database.RootDirectory));
        if (_scratchDirectory.Equals(databaseRoot, StringComparison.OrdinalIgnoreCase)
            || _scratchDirectory.StartsWith(databaseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("会话临时目录必须位于持久数据库目录之外。", nameof(options));
    }

    /// <summary>不可由表名推导的会话身份。</summary>
    public Guid Id { get; }

    /// <summary>会话是否已经关闭。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>当前执行或元数据作用域内的连接会话。</summary>
    public static SqlSessionContext? Current => Ambient.Value;

    internal Tsdb Database { get; }
    internal SqlSessionOptions Options { get; }
    internal string ScratchDirectory => _scratchDirectory;
    internal TableManager? VisibleTables => (_executing ?? _working ?? _committed)?.Tables;

    internal static bool OwnsPersistentManager(TableManager manager)
        => Current is { } session && ReferenceEquals(session.Database.Tables, manager);

    internal static TableManager? TemporaryManagerFor(TableManager manager)
        => OwnsPersistentManager(manager) ? Current!.VisibleTables : null;

    internal static TableCatalog CatalogFor(TableManager manager, TableCatalog persistent)
        => OwnsPersistentManager(manager)
            ? Current!._catalogView ??= new TableCatalog(persistent, Current!)
            : persistent;

    internal bool IsMutatingTemporaryObjects => _executing is not null;

    /// <summary>进入会话的串行执行或元数据作用域；异步调用可在另一线程释放。</summary>
    /// <returns>恢复先前会话并释放本次准入的作用域。</returns>
    public IDisposable EnterScope() => EnterScope(CancellationToken.None);

    /// <summary>在指定取消令牌和会话超时内进入执行或元数据作用域。</summary>
    /// <param name="cancellationToken">取消等待准入的令牌。</param>
    /// <returns>恢复先前会话并释放本次准入的作用域。</returns>
    public IDisposable EnterScope(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SqlSessionContext? previous = Ambient.Value;
        if (ReferenceEquals(previous, this))
            return new Scope(this, previous, ownsGate: false);
        if (previous is not null)
            throw new InvalidOperationException("同一 SQL 调用链不能切换到另一连接会话。");
        if (!_gate.Wait(Options.OperationTimeout, cancellationToken))
            throw new TimeoutException("等待连接会话执行准入超时。");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RetireInactiveWorkspaces();
            Ambient.Value = this;
            return new Scope(this, previous, ownsGate: true);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>返回本连接当前可见的临时表声明，保持原始名称拼写。</summary>
    /// <returns>包含事务内目录变化的有界声明快照。</returns>
    public IReadOnlyList<TableSchema> GetTemporaryTableSchemas()
    {
        using IDisposable scope = EnterScope();
        return VisibleTables?.Catalog.Snapshot() ?? [];
    }

    internal void ValidateDatabase(Tsdb database)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(Database, database))
            throw new InvalidOperationException("SQL 会话不属于当前数据库实例。");
    }

    internal void AttachTransaction(SqlTransactionContext transaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        transaction.ThrowIfCompleted();
        if (_transaction is { IsCompleted: false } existing && !ReferenceEquals(existing, transaction))
            throw new InvalidOperationException("连接会话已有另一个活动事务。");
        _transaction = transaction;
    }

    internal MutationScope BeginTemporaryMutation(SqlTransactionContext? transaction,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(Current, this))
            throw new InvalidOperationException("临时对象变更必须位于所属连接会话作用域。");
        if (_executing is not null)
            throw new NotSupportedException("同一语句的临时对象变更不能重入另一临时 DDL/DML。");
        if (transaction is not null)
            transaction.AttachSession(this);
        SqlTemporaryTableWorkspace? previous = _working ?? _committed;
        SqlTemporaryTableWorkspace candidate = CreateWorkspace(previous, cancellationToken);
        _executing = candidate;
        return new MutationScope(this, transaction, previous, candidate, cancellationToken);
    }

    private SqlTemporaryTableWorkspace CreateWorkspace(SqlTemporaryTableWorkspace? source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_workspaces.Count >= Options.MaximumWorkspaces)
            throw new InvalidOperationException("会话临时工作目录代际超出预算；请提交或回滚事务并关闭连接回收。");
        long retained = 0;
        foreach (SqlTemporaryTableWorkspace workspace in _workspaces)
            retained = checked(retained + workspace.EstimatedBytes);
        if (source is not null && source.EstimatedBytes > Options.MaximumRetainedBytes - retained)
            throw new InvalidOperationException("会话保留的临时副本超出累计字节预算。");
        EnsureScratchDirectory();
        string directory = Path.Combine(_scratchDirectory, "workspace-" + Guid.NewGuid().ToString("N"));
        var candidate = new SqlTemporaryTableWorkspace(Database, directory);
        _workspaces.Add(candidate);
        try
        {
            if (source is not null)
                candidate.CopyFrom(source, Options, Id, cancellationToken);
            return candidate;
        }
        catch
        {
            DiscardWorkspace(candidate);
            throw;
        }
    }

    private void EnsureScratchDirectory()
    {
        if (_scratchCreated)
            return;
        if (Directory.Exists(_scratchDirectory))
            throw new IOException("新会话的独占临时目录已经存在，拒绝接管。");
        Directory.CreateDirectory(_scratchDirectory);
        _scratchCreated = true;
        using var marker = new FileStream(Path.Combine(_scratchDirectory, "session.owner"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        marker.Write(Encoding.ASCII.GetBytes(_ownerToken));
    }

    internal void CompleteTransaction(SqlTransactionContext transaction, bool committed)
    {
        if (!ReferenceEquals(_transaction, transaction))
            return;
        // 工作副本在临时语句成功时已完成所有 I/O、类型与容量校验。
        // 持久提交成功后只有引用发布，不执行可失败的文件操作或重新分配目录。
        if (committed && _working is not null)
            _committed = _working;
        _working = null;
        _transaction = null;
    }

    private void RetireInactiveWorkspaces()
    {
        if (_transaction is { IsCompleted: false } || _executing is not null)
            return;
        long started = Stopwatch.GetTimestamp();
        for (int index = _workspaces.Count - 1; index >= 0; index--)
        {
            SqlTemporaryTableWorkspace workspace = _workspaces[index];
            if (ReferenceEquals(workspace, _committed))
                continue;
            try
            {
                CheckCleanupDeadline(started);
                workspace.Dispose();
                DeleteOwnedDirectory(workspace.DirectoryPath, started);
                _workspaces.RemoveAt(index);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                // 已提交状态不能被回收错误改称失败；保留对象及异常，在 Close/Dispose 时显式报告。
                _deferredCleanupFailure = exception;
                break;
            }
        }
    }

    private void DiscardWorkspace(SqlTemporaryTableWorkspace workspace)
    {
        try
        {
            workspace.Dispose();
            DeleteOwnedDirectory(workspace.DirectoryPath, Stopwatch.GetTimestamp());
            _workspaces.Remove(workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            _deferredCleanupFailure = exception;
        }
    }

    private void ValidatePhysicalCapacity(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        long bytes = 0;
        int entries = 0;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((_scratchDirectory, 0));
        while (pending.Count != 0 && entries <= Options.MaximumCleanupEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= Options.OperationTimeout)
                throw new TimeoutException("核对会话 scratch 物理字节预算超时。");
            (string directory, int depth) = pending.Dequeue();
            if (depth > 16) throw new IOException("会话 scratch 深度超过预算。");
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > Options.MaximumCleanupEntries || Stopwatch.GetElapsedTime(started) >= Options.OperationTimeout)
                    throw new IOException("会话 scratch 对象数或扫描时间超过预算。");
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("会话 scratch 中出现链接，拒绝扫描。");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Enqueue((entry, depth + 1));
                else
                {
                    bytes = checked(bytes + new FileInfo(entry).Length);
                    if (bytes > Options.MaximumScratchBytes)
                        throw new InvalidOperationException("会话 scratch 实际文件字节数超过预算。");
                }
            }
        }
    }

    /// <summary>丢弃活动事务的临时状态并回收本会话独占的临时对象；失败时保留归属标记供再次回收。</summary>
    public void Dispose()
    {
        if (_cleaned)
            return;
        long cleanupStarted = Stopwatch.GetTimestamp();
        bool ownsGate = !ReferenceEquals(Current, this);
        if (ownsGate && !_gate.Wait(Options.CleanupTimeout))
            throw new TimeoutException("会话仍有活动执行，拒绝并发删除临时状态。");
        try
        {
            _disposed = true;
            if (_transaction is { IsCompleted: false } transaction)
            {
                transaction.WasCommitted = false;
                transaction.MarkCompleted();
            }
            _working = null;
            _executing = null;
            _committed = null;
            var failures = new List<Exception>();
            foreach (SqlTemporaryTableWorkspace workspace in _workspaces)
            {
                try
                {
                    CheckCleanupDeadline(cleanupStarted);
                    workspace.Dispose();
                }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count != 0)
                throw new AggregateException("会话临时存储关闭失败，未删除目录。", failures);
            try { DeleteOwnedScratch(cleanupStarted); }
            catch (Exception exception) when (_deferredCleanupFailure is { } previousFailure)
            {
                throw new AggregateException("会话临时目录回收失败，包含先前延迟回收错误。", previousFailure, exception);
            }
            _workspaces.Clear();
            _deferredCleanupFailure = null;
            _cleaned = true;
        }
        finally
        {
            if (ownsGate)
                _gate.Release();
        }
    }

    private void DeleteOwnedScratch(long started)
    {
        if (!_scratchCreated || !Directory.Exists(_scratchDirectory))
            return;
        string marker = Path.Combine(_scratchDirectory, "session.owner");
        if (!string.Equals(File.ReadAllText(marker, Encoding.ASCII), _ownerToken, StringComparison.Ordinal))
            throw new IOException("会话临时目录归属标记不匹配，拒绝删除。");
        DeleteOwnedDirectory(_scratchDirectory, started, marker);
    }

    private void DeleteOwnedDirectory(string ownedDirectory, long started, string? ownerMarker = null)
    {
        if (!Directory.Exists(ownedDirectory))
            return;
        string root = Path.GetFullPath(ownedDirectory);
        if (!root.Equals(_scratchDirectory, StringComparison.OrdinalIgnoreCase)
            && !root.StartsWith(_scratchDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("待回收目录不属于当前会话。");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("临时工作目录已变成链接，拒绝回收。");
        string sessionMarker = Path.Combine(_scratchDirectory, "session.owner");
        if ((File.GetAttributes(sessionMarker) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("会话归属标记是链接，拒绝回收。");
        if (!string.Equals(File.ReadAllText(sessionMarker, Encoding.ASCII), _ownerToken, StringComparison.Ordinal))
            throw new IOException("会话归属标记已变化，拒绝回收临时工作目录。");
        var pending = new Queue<(string Path, int Depth)>();
        var files = new List<string>();
        var directories = new List<string>();
        pending.Enqueue((root, 0));
        int entries = 0;
        while (pending.Count != 0 && entries <= Options.MaximumCleanupEntries)
        {
            CheckCleanupDeadline(started);
            (string directory, int depth) = pending.Dequeue();
            if (depth > 16)
                throw new IOException("会话临时目录深度超出回收预算。");
            directories.Add(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckCleanupDeadline(started);
                if (++entries > Options.MaximumCleanupEntries)
                    throw new IOException("会话临时目录对象数超出回收预算。");
                string full = Path.GetFullPath(entry);
                if (!full.StartsWith(_scratchDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("会话临时路径越过独占目录，拒绝删除。");
                FileAttributes attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("会话临时目录包含链接，拒绝跟随或删除。");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Enqueue((full, depth + 1));
                else if (!string.Equals(full, ownerMarker, StringComparison.OrdinalIgnoreCase))
                    files.Add(full);
            }
        }
        foreach (string file in files)
        {
            CheckCleanupDeadline(started);
            File.Delete(file);
        }
        for (int index = directories.Count - 1; index > 0; index--)
        {
            CheckCleanupDeadline(started);
            Directory.Delete(directories[index]);
        }
        CheckCleanupDeadline(started);
        if (ownerMarker is not null)
            File.Delete(ownerMarker);
        Directory.Delete(root);
    }

    private void CheckCleanupDeadline(long started)
    {
        if (Stopwatch.GetElapsedTime(started) >= Options.CleanupTimeout)
            throw new TimeoutException("回收会话临时目录超时，保留其余归属对象。");
    }

    private sealed class Scope(SqlSessionContext session, SqlSessionContext? previous, bool ownsGate) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            Ambient.Value = previous;
            if (ownsGate)
                session._gate.Release();
        }
    }

    internal sealed class MutationScope(SqlSessionContext session, SqlTransactionContext? transaction,
        SqlTemporaryTableWorkspace? previous, SqlTemporaryTableWorkspace candidate, CancellationToken cancellationToken) : IDisposable
    {
        private bool _completed;

        internal TableManager Tables => candidate.Tables;

        internal void Complete()
        {
            candidate.ValidateCapacity(session.Options, session.Id, cancellationToken);
            session.ValidatePhysicalCapacity(cancellationToken);
            long bytes = 0;
            foreach (SqlTemporaryTableWorkspace workspace in session._workspaces)
                bytes = checked(bytes + workspace.EstimatedBytes);
            if (bytes > session.Options.MaximumRetainedBytes)
                throw new InvalidOperationException("会话所有临时副本超出累计字节预算。");
            if (transaction is null)
                session._committed = candidate;
            else
            {
                transaction.RecordSessionUndo(() => session._working = previous);
                session._working = candidate;
            }
            _completed = true;
        }

        public void Dispose()
        {
            session._executing = null;
            if (!_completed)
                session.DiscardWorkspace(candidate);
        }
    }
}
