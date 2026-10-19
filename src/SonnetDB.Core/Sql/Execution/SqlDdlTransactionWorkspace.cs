using System.Diagnostics;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;
using SonnetDB.Wal;

namespace SonnetDB.Sql.Execution;

/// <summary>显式事务独占的完整关系目录副本；未发布前不改变持久 TableManager。</summary>
internal sealed class SqlDdlTransactionWorkspace : IDisposable
{
    internal const string OwnerFileName = ".ddl-owner";
    private readonly SqlDdlTransactionOptions _options;
    private readonly Tsdb _sourceDatabase;
    private bool _preserveForRecovery;
    private bool _sealed;
    private bool _disposed;

    private SqlDdlTransactionWorkspace(
        Guid owner,
        string directoryPath,
        Tsdb sourceDatabase,
        Tsdb database,
        SqlDdlTransactionOptions options,
        int catalogFloor,
        int rowCount,
        long estimatedBytes)
    {
        Owner = owner;
        DirectoryPath = directoryPath;
        _sourceDatabase = sourceDatabase;
        Database = database;
        _options = options;
        CatalogFloor = catalogFloor;
        RowCount = rowCount;
        EstimatedBytes = estimatedBytes;
    }

    internal Guid Owner { get; }
    internal string DirectoryPath { get; }
    internal Tsdb SourceDatabase => _sourceDatabase;
    internal Tsdb Database { get; }
    internal SqlDdlTransactionOptions Options => _options;
    internal TableManager Tables => Database.Tables;
    internal int CatalogFloor { get; }
    internal int RowCount { get; }
    internal long EstimatedBytes { get; }

    internal static SqlDdlTransactionWorkspace Create(
        Tsdb database,
        SqlTransactionContext transaction,
        SqlDdlTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        options ??= new SqlDdlTransactionOptions();
        options.Validate();
        transaction.ThrowIfCompleted();
        transaction.ValidateDdlWorkspacePromotion();
        database.Tables.AcquireDdlTransactionLease(transaction, options.LockTimeout, cancellationToken);
        database.Tables.ValidateDdlTransactionBaseline(transaction, options.LockTimeout, cancellationToken);

        Guid owner = Guid.NewGuid();
        string parent = Path.Combine(database.RootDirectory, "tables", ".ddl-transactions");
        string directory = Path.Combine(parent, "workspace-" + owner.ToString("N"));
        Directory.CreateDirectory(parent);
        if (Directory.Exists(directory))
            throw new IOException("DDL 事务 workspace 已存在，拒绝接管。");
        Directory.CreateDirectory(directory);
        WriteOwner(directory, owner);
        DirectoryFsync.FlushRequired(parent);

        Tsdb? clone = null;
        try
        {
            // 克隆阶段不属于用户事务的写集。若沿用 ambient 事务，
            // TableStore.InsertMany 会把 workspace 的数据库锁保留到事务结束，
            // 随后的副本 DDL 反而会等待自己的复制锁。
            using IDisposable transactionSuppression = SqlTransactionContext.SuppressCurrent();
            using IDisposable sessionSuppression = SqlSessionContext.SuppressCurrent();
            clone = Tsdb.Open(database.RuntimeOptions with
            {
                RootDirectory = directory,
                SyncWalOnEveryWrite = true,
                BackgroundFlush = new SonnetDB.Engine.BackgroundFlushOptions { Enabled = false },
                Compaction = new SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
                Kv = database.RuntimeOptions.Kv with
                {
                    SyncWalOnEveryWrite = true,
                    AutoCheckpointEnabled = false,
                    ExpirerEnabled = false,
                },
            });
            (int rows, long bytes) = CopyPersistentTables(
                database.Tables, clone.Tables, options, cancellationToken);
            var workspace = new SqlDdlTransactionWorkspace(
                owner,
                directory,
                database,
                clone,
                options,
                TableSchemaCodec.ReadFormatVersion(database.Tables.SchemaPath),
                rows,
                bytes);
            transaction.PromoteBufferedDmlToDdlWorkspace(workspace);
            return workspace;
        }
        catch
        {
            clone?.Dispose();
            DeleteOwnedDirectory(directory, owner, options);
            throw;
        }
    }

    internal void PreserveForRecovery() => _preserveForRecovery = true;

    internal void DiscardAfterDetachedFailure()
    {
        _preserveForRecovery = false;
        Dispose();
    }

    internal object? ExecuteDdl(SqlStatement statement, SqlExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
            throw new InvalidOperationException("DDL 事务 workspace 已封存，不能继续执行语句。");
        if (!SqlDdlTransactionStatement.IsSupported(statement))
            throw new NotSupportedException($"语句 {statement.GetType().Name} 不属于 #227 关系 DDL 事务范围。");
        SqlDdlTransactionGovernance.Validate(_sourceDatabase, statement);
        // 该副本不属于源连接的 SqlSessionContext；否则 TableManager.CatalogFor
        // 会把源连接的临时目录错误地叠加到私有 workspace。
        using IDisposable sessionSuppression = SqlSessionContext.SuppressCurrent();
        using IDisposable transactionSuppression = SqlTransactionContext.SuppressCurrent();
        return SqlExecutor.ExecuteStatement(
            Database,
            databaseName: null,
            statement,
            controlPlane: null,
            transaction: null,
            (options ?? SqlExecutionOptions.Default) with { SessionContext = null });
    }

    internal void SealForCommit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
            return;
        TableSchemaCodec.SaveWithDdlTransactionBarrier(
            Database.Tables.SchemaPath,
            Database.Tables.Catalog.Snapshot());
        Database.Dispose();
        _sealed = true;
        _preserveForRecovery = true;
        DirectoryFsync.FlushRequired(DirectoryPath);
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(DirectoryPath)!);
    }

    private static (int Rows, long Bytes) CopyPersistentTables(
        TableManager source,
        TableManager destination,
        SqlDdlTransactionOptions options,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        IReadOnlyList<TableSchema> schemas = source.PersistentCatalog.Snapshot();
        if (schemas.Count > options.MaximumTables)
            throw new InvalidOperationException("DDL 事务 workspace 表数量超过预算。");
        int totalRows = 0;
        long totalBytes = 0;
        foreach (TableSchema schema in schemas)
        {
            CheckBudget(started, options, cancellationToken);
            destination.Create(schema);
            var batch = new List<IReadOnlyList<object?>>(options.CopyBatchRows);
            foreach (TableRow row in source.OpenPersistentForDdl(schema.Name).EnumerateScan())
            {
                CheckBudget(started, options, cancellationToken);
                if (++totalRows > options.MaximumRows)
                    throw new InvalidOperationException("DDL 事务 workspace 行数超过预算。");
                object?[] values = row.Values.ToArray();
                totalBytes = checked(totalBytes + EstimateRow(values));
                if (totalBytes > options.MaximumBytes)
                    throw new InvalidOperationException("DDL 事务 workspace 字节数超过预算。");
                batch.Add(values);
                if (batch.Count == options.CopyBatchRows)
                {
                    destination.Open(schema.Name).InsertMany(batch);
                    batch.Clear();
                }
            }
            if (batch.Count != 0)
                destination.Open(schema.Name).InsertMany(batch);
        }
        return (totalRows, totalBytes);
    }

    private static long EstimateRow(IReadOnlyList<object?> values)
    {
        long bytes = checked(64L + values.Count * 16L);
        foreach (object? value in values)
        {
            bytes = checked(bytes + (value switch
            {
                string text => 32L + Encoding.UTF8.GetByteCount(text),
                byte[] blob => 32L + blob.Length,
                float[] vector => 32L + vector.Length * sizeof(float),
                decimal or DateTimeOffset => 32L,
                null => 0L,
                _ => 24L,
            }));
        }
        return bytes;
    }

    private static void CheckBudget(
        long started,
        SqlDdlTransactionOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqlExecutor.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= options.OperationTimeout)
            throw new TimeoutException("DDL 事务 workspace 克隆超过执行预算。");
    }

    private static void WriteOwner(string directory, Guid owner)
    {
        string path = Path.Combine(directory, OwnerFileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.ASCII.GetBytes(owner.ToString("N")));
        stream.Flush(flushToDisk: true);
        DirectoryFsync.FlushRequired(directory);
    }

    internal static void DeleteOwnedDirectory(
        string directory,
        Guid owner,
        SqlDdlTransactionOptions options)
    {
        if (!Directory.Exists(directory))
            return;
        string marker = Path.Combine(directory, OwnerFileName);
        if (!File.Exists(marker)
            || new FileInfo(marker).Length != 32
            || File.ReadAllText(marker, Encoding.ASCII) != owner.ToString("N"))
        {
            throw new IOException("DDL 事务 workspace 归属标记不匹配，拒绝删除。");
        }
        long started = Stopwatch.GetTimestamp();
        var pending = new Queue<(string Directory, int Depth)>();
        var directories = new List<string>();
        var files = new List<string>();
        pending.Enqueue((Path.GetFullPath(directory), 0));
        int entries = 0;
        while (pending.Count != 0)
        {
            CheckCleanupBudget(started, options);
            (string current, int depth) = pending.Dequeue();
            if (depth > options.MaximumCleanupDepth)
                throw new IOException("DDL 事务 workspace 清理深度超过预算。");
            directories.Add(current);
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                CheckCleanupBudget(started, options);
                if (++entries > options.MaximumCleanupEntries)
                    throw new IOException("DDL 事务 workspace 清理条目超过预算。");
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("DDL 事务 workspace 包含链接，拒绝跟随。");
                if (Directory.Exists(entry)) pending.Enqueue((entry, depth + 1));
                else files.Add(entry);
            }
        }
        foreach (string file in files)
        {
            CheckCleanupBudget(started, options);
            File.Delete(file);
        }
        for (int index = directories.Count - 1; index >= 0; index--)
        {
            CheckCleanupBudget(started, options);
            Directory.Delete(directories[index]);
        }
        string? parent = Path.GetDirectoryName(directory);
        if (parent is not null && Directory.Exists(parent))
            DirectoryFsync.FlushRequired(parent);
    }

    private static void CheckCleanupBudget(long started, SqlDdlTransactionOptions options)
    {
        if (Stopwatch.GetElapsedTime(started) >= options.CleanupTimeout)
            throw new TimeoutException("DDL 事务 workspace 清理超过执行预算。");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_sealed)
            Database.Dispose();
        if (!_preserveForRecovery)
            DeleteOwnedDirectory(DirectoryPath, Owner, _options);
    }
}
