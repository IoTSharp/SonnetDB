using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 DDL 事务完整关系 workspace 的隔离、容量和排它 lease。</summary>
public sealed class SqlDdlTransactionWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-ddl-workspace-" + Guid.NewGuid().ToString("N"));

    /// <summary>一万行既有数据和 schema 被克隆，workspace DDL 不对持久目录可见。</summary>
    [Fact]
    public void Create_WithTenThousandRows_ClonesAndKeepsSchemaIsolated()
    {
        using var database = Open();
        TableSchema schema = TableSchema.Create(
            "items",
            [("id", TableColumnType.Int64, false), ("value", TableColumnType.Int64, false)],
            ["id"]);
        database.Tables.Create(schema);
        for (int batch = 0; batch < 10; batch++)
        {
            IReadOnlyList<IReadOnlyList<object?>> rows = Enumerable.Range(1, 1_000)
                .Select(index => (IReadOnlyList<object?>)new object?[] { (long)(batch * 1_000 + index), 5L })
                .ToArray();
            Assert.Equal(1_000, database.Tables.Open("items").InsertMany(rows));
        }
        var transaction = new SqlTransactionContext();
        try
        {
            using var workspace = SqlDdlTransactionWorkspace.Create(database, transaction);
            Assert.Equal(10_000, workspace.RowCount);
            Assert.Equal(10_000, workspace.Tables.Open("items").RowCount);
            workspace.Tables.AlterTableAddColumn(
                "items", "extra", TableColumnType.String, isNullable: true, defaultValue: null);
            Assert.NotNull(workspace.Tables.Catalog.TryGet("items")!.TryGetColumn("extra"));
            Assert.Null(database.Tables.PersistentCatalog.TryGet("items")!.TryGetColumn("extra"));
        }
        finally
        {
            transaction.MarkCompleted();
        }
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(_root, "tables", ".ddl-transactions")));
    }

    /// <summary>持有 database-exclusive lease 时第二事务等待可取消，释放后可继续。</summary>
    [Fact]
    public async Task Create_ConcurrentTransaction_WaitsCancelsAndSucceedsAfterRelease()
    {
        using var database = Open();
        var first = new SqlTransactionContext();
        using var firstWorkspace = SqlDdlTransactionWorkspace.Create(database, first);
        var second = new SqlTransactionContext();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
                SqlDdlTransactionWorkspace.Create(database, second, cancellationToken: cancel.Token)));
            var timeoutOptions = new SqlDdlTransactionOptions { LockTimeout = TimeSpan.FromMilliseconds(50) };
            var timeout = Assert.Throws<SqlRowLockException>(() =>
                SqlDdlTransactionWorkspace.Create(database, second, timeoutOptions));
            Assert.Equal(SqlRowLockException.WaitTimeout, timeout.Code);

            firstWorkspace.Dispose();
            first.MarkCompleted();
            using var secondWorkspace = SqlDdlTransactionWorkspace.Create(database, second);
            Assert.Empty(secondWorkspace.Tables.Catalog.Snapshot());
        }
        finally
        {
            if (!first.IsCompleted) first.MarkCompleted();
            if (!second.IsCompleted) second.MarkCompleted();
        }
    }

    /// <summary>超过克隆行预算时拒绝并清理由本次创建的 workspace。</summary>
    [Fact]
    public void Create_RowBudgetExceeded_RejectsWithoutOwnedRemainder()
    {
        using var database = Open();
        database.Tables.Create(TableSchema.Create(
            "items", [("id", TableColumnType.Int64, false)], ["id"]));
        database.Tables.Open("items").InsertMany(
            Enumerable.Range(1, 3).Select(index => (IReadOnlyList<object?>)new object?[] { (long)index }).ToArray());
        var transaction = new SqlTransactionContext();
        try
        {
            var options = new SqlDdlTransactionOptions { MaximumRows = 2 };
            Assert.Throws<InvalidOperationException>(() =>
                SqlDdlTransactionWorkspace.Create(database, transaction, options));
        }
        finally
        {
            transaction.MarkCompleted();
        }
        string parent = Path.Combine(_root, "tables", ".ddl-transactions");
        Assert.True(!Directory.Exists(parent) || !Directory.EnumerateFileSystemEntries(parent).Any());
    }

    /// <summary>事务复用同一 workspace、拒绝切库；rollback lifecycle 删除独占目录。</summary>
    [Fact]
    public void TransactionLifecycle_ReusesRejectsOtherDatabaseAndRollsBackOwnedDirectory()
    {
        using var database = Open();
        string otherRoot = Path.Combine(Path.GetTempPath(),
            "sndb-ddl-workspace-other-" + Guid.NewGuid().ToString("N"));
        var other = Tsdb.Open(new TsdbOptions { RootDirectory = otherRoot });
        var transaction = new SqlTransactionContext();
        string directory;
        try
        {
            SqlDdlTransactionWorkspace first = transaction.GetOrCreateDdlWorkspace(database);
            directory = first.DirectoryPath;
            Assert.Same(first, transaction.GetOrCreateDdlWorkspace(database));
            Assert.Same(first.Tables, transaction.DdlWorkspaceTables);
            Assert.Throws<InvalidOperationException>(() =>
                transaction.GetOrCreateDdlWorkspace(other));
            transaction.RollbackDdlWorkspace();
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            transaction.RollbackDdlWorkspace();
            transaction.MarkCompleted();
            other.Dispose();
            if (Directory.Exists(otherRoot))
                Directory.Delete(otherRoot, recursive: true);
        }
    }

    /// <summary>首条 DDL 前缓冲的 DML 迁入 workspace，持久表保持未提交状态。</summary>
    [Fact]
    public void Create_WithBufferedDml_PromotesRowsOnlyIntoWorkspace()
    {
        using var database = Open();
        database.Tables.Create(TableSchema.Create(
            "items", [("id", TableColumnType.Int64, false), ("value", TableColumnType.Int64, false)], ["id"]));
        var transaction = new SqlTransactionContext();
        try
        {
            _ = SqlExecutor.ExecuteStatement(
                database,
                databaseName: null,
                SqlParser.Parse("INSERT INTO items(id,value) VALUES(1,10)"),
                controlPlane: null,
                transaction);
            Assert.Single(transaction.SnapshotTableMutations());
            using var workspace = transaction.GetOrCreateDdlWorkspace(database);
            Assert.Empty(transaction.SnapshotTableMutations());
            Assert.Equal(1, workspace.Tables.Open("items").RowCount);
            Assert.Equal(0, database.Tables.OpenPersistentForDdl("items").RowCount);
        }
        finally
        {
            transaction.RollbackDdlWorkspace();
            transaction.MarkCompleted();
        }
        Assert.Equal(0, database.Tables.Open("items").RowCount);
    }

    /// <summary>Serializable 首次读取后发生外部写入时，启动 DDL workspace 前拒绝旧基线。</summary>
    [Fact]
    public void Create_SerializableReadChangedBeforeLease_RejectsWorkspace()
    {
        using var database = Open();
        database.Tables.Create(TableSchema.Create(
            "items", [("id", TableColumnType.Int64, false)], ["id"]));
        database.Tables.Open("items").Insert([1L]);
        var transaction = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);
        try
        {
            _ = SqlExecutor.ExecuteStatement(
                database,
                databaseName: null,
                SqlParser.Parse("SELECT id FROM items"),
                controlPlane: null,
                transaction);
            database.Tables.Open("items").Insert([2L]);
            var error = Assert.Throws<TableConstraintException>(() =>
                transaction.GetOrCreateDdlWorkspace(database));
            Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        }
        finally
        {
            transaction.RollbackDdlWorkspace();
            transaction.MarkCompleted();
        }
    }

    /// <summary>多步 DDL 的第二步失败只改变私有 workspace，持久 schema 与数据保持原状。</summary>
    [Fact]
    public void ExecuteDdl_SecondStepFails_PersistentSchemaAndRowsRemainOriginal()
    {
        using var database = Open();
        database.Tables.Create(TableSchema.Create(
            "items", [("id", TableColumnType.Int64, false), ("value", TableColumnType.Int64, false)], ["id"]));
        database.Tables.Open("items").Insert([1L, 10L]);
        var transaction = new SqlTransactionContext();
        try
        {
            using var workspace = transaction.GetOrCreateDdlWorkspace(database);
            _ = workspace.ExecuteDdl(SqlParser.Parse(
                "ALTER TABLE items ADD COLUMN extra STRING NULL DEFAULT 'x'"));
            _ = workspace.ExecuteDdl(SqlParser.Parse(
                "CREATE INDEX ix_items_extra ON items(extra)"));
            Assert.Throws<InvalidOperationException>(() => workspace.ExecuteDdl(SqlParser.Parse(
                "CREATE INDEX ix_items_extra ON items(extra)")));
            TableSchema staged = workspace.Tables.Catalog.TryGet("items")!;
            Assert.NotNull(staged.TryGetColumn("extra"));
            Assert.Single(staged.Indexes);
            Assert.Equal("x", workspace.Tables.Open("items").Scan()[0].Values[2]);

            TableSchema durable = database.Tables.PersistentCatalog.TryGet("items")!;
            Assert.Null(durable.TryGetColumn("extra"));
            Assert.Empty(durable.Indexes);
            Assert.Equal(new object?[] { 1L, 10L }, database.Tables.OpenPersistentForDdl("items").Scan()[0].Values);
        }
        finally
        {
            transaction.RollbackDdlWorkspace();
            transaction.MarkCompleted();
        }
    }

    /// <summary>真实长数据库根下 workspace owner、catalog 与目录 fsync 均可完成。</summary>
    [Fact]
    public void Create_LongDatabaseRoot_DoesNotShortenOrBypassDurabilityPath()
    {
        string ownedRoot = Path.Combine(Path.GetTempPath(),
            "sndb-ddl-long-" + Guid.NewGuid().ToString("N"));
        string longRoot = Path.Combine(ownedRoot,
            new string('a', 88), new string('b', 88), new string('c', 88));
        Assert.True(longRoot.Length > 260);
        try
        {
            using var database = Tsdb.Open(new TsdbOptions { RootDirectory = longRoot });
            var transaction = new SqlTransactionContext();
            try
            {
                using var workspace = transaction.GetOrCreateDdlWorkspace(database);
                Assert.StartsWith(Path.GetFullPath(longRoot), Path.GetFullPath(workspace.DirectoryPath),
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath,
                    SqlDdlTransactionWorkspace.OwnerFileName)));
            }
            finally
            {
                transaction.RollbackDdlWorkspace();
                transaction.MarkCompleted();
            }
        }
        finally
        {
            if (Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: true);
        }
    }

    /// <summary>私有 Tsdb 不克隆其它模型，但 staged DDL 仍按原数据库拒绝跨模型同名。</summary>
    [Fact]
    public void ExecuteDdl_CrossModelNameConflict_UsesSourceGovernanceBeforeWorkspaceMutation()
    {
        using var database = Open();
        _ = SqlExecutor.Execute(database,
            "CREATE MEASUREMENT SharedName(host TAG,value FIELD FLOAT)");
        var transaction = new SqlTransactionContext();
        try
        {
            using var workspace = transaction.GetOrCreateDdlWorkspace(database);
            Assert.Empty(workspace.Database.Measurements.Snapshot());
            Assert.Throws<InvalidOperationException>(() => workspace.ExecuteDdl(SqlParser.Parse(
                "CREATE TABLE SharedName(id INT,PRIMARY KEY(id))")));
            Assert.Null(workspace.Tables.Catalog.TryGet("SharedName"));
        }
        finally
        {
            transaction.RollbackDdlWorkspace();
            transaction.MarkCompleted();
        }
        Assert.NotNull(database.Measurements.TryGet("SharedName"));
    }

    /// <summary>原数据库 view/routine/graph 等依赖守卫在私有 workspace DDL 前执行。</summary>
    [Fact]
    public void ExecuteDdl_SourceViewDependent_RejectsBeforeStagedAlter()
    {
        using var database = Open();
        _ = SqlExecutor.Execute(database,
            "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))");
        _ = SqlExecutor.Execute(database,
            "CREATE VIEW item_view AS SELECT id,value FROM items");
        var transaction = new SqlTransactionContext();
        try
        {
            using var workspace = transaction.GetOrCreateDdlWorkspace(database);
            var error = Assert.Throws<InvalidOperationException>(() => workspace.ExecuteDdl(SqlParser.Parse(
                "ALTER TABLE items ADD COLUMN extra STRING NULL")));
            Assert.Contains("item_view", error.Message, StringComparison.Ordinal);
            Assert.Null(workspace.Tables.Catalog.TryGet("items")!.TryGetColumn("extra"));
        }
        finally
        {
            transaction.RollbackDdlWorkspace();
            transaction.MarkCompleted();
        }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    /// <summary>只回收本测试创建的唯一数据库目录。</summary>
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporary, full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-ddl-workspace-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
