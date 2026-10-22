using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>真实 SQL CTAS、类型、事务、备份与恢复合同。</summary>
public sealed class SqlCreateTableAsIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ctas-integration-" + Guid.NewGuid().ToString("N"));

    /// <summary>空结果与全 NULL 行都从声明保留九种类型，普通 CTAS 在重开后保留数据。</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Sql_NineDeclaredTypes_PreservesEmptyNullAndNonNullResults(bool temporary, bool empty, bool nulls)
    {
        TableColumnType[] types = [TableColumnType.Int64, TableColumnType.Decimal, TableColumnType.Time,
            TableColumnType.DateTime, TableColumnType.Boolean, TableColumnType.Json, TableColumnType.Blob,
            TableColumnType.Duration, TableColumnType.DateTimeOffset];
        object?[] values = nulls ? new object?[9] : [1L, 1234567890123456.123456789012m, new TimeOnly(12, 34, 56),
            new DateTime(2026, 10, 19, 12, 34, 56), true, "{\"value\":1}", new byte[] { 1, 0, 255 },
            TimeSpan.FromTicks(-864000000001), new DateTimeOffset(2026, 10, 19, 12, 34, 56, TimeSpan.FromHours(8))];
        using (var database = Open())
        using (var session = new SqlSessionContext(database))
        {
            Run(database, session, "CREATE TABLE Typed(Id INT,Amount DECIMAL(28,12),AtTime TIME,Stamp DATETIME,Enabled BOOL,Payload JSON,Bytes BLOB,Elapsed DURATION,Zoned DATETIMEOFFSET)");
            database.Tables.Open("Typed").Insert(values);
            Run(database, session, $"CREATE {(temporary ? "TEMP " : "")}TABLE Copied AS SELECT Id AS DeviceID,Amount,AtTime,Stamp,Enabled,Payload,Bytes,Elapsed,Zoned FROM Typed{(empty ? " WHERE 1=0" : "")}");
            TableSchema schema = temporary ? Assert.Single(session.GetTemporaryTableSchemas()) : database.Tables.Catalog.TryGet("Copied")!;
            Assert.Equal("DeviceID", schema.Columns[0].Name);
            Assert.Equal(types, schema.Columns.Select(static column => column.DataType));
            Assert.Equal((byte)28, schema.Columns[1].DecimalPrecision);
            Assert.Equal((byte)12, schema.Columns[1].DecimalScale);
            Assert.True(schema.Columns[1].HasExplicitDecimalDeclaration);
            Assert.All(schema.Columns, column => Assert.True(column.IsNullable));
            SelectExecutionResult result = Assert.IsType<SelectExecutionResult>(Run(database, session, "SELECT * FROM Copied"));
            Assert.Equal(types, result.ColumnInfo!.Select(static column => column.DataType!.Value));
            Assert.Equal(empty ? 0 : 1, result.Rows.Count);
            if (!empty)
                for (int index = 0; index < values.Length; index++) Assert.Equal(values[index], result.Rows[0][index]);
        }
        using var reopened = Open();
        if (temporary) Assert.Null(reopened.Tables.Catalog.TryGet("Copied"));
        else Assert.Equal(empty ? 0 : 1, reopened.Tables.Open("Copied").RowCount);
    }

    /// <summary>裸 Decimal 与裸 CAST 不被 CTAS 变成显式 (38,28)，来源显式声明则保留 p/s。</summary>
    [Fact]
    public void Sql_BareAndExplicitDecimal_KeepTheirDistinctValueDomains()
    {
        using var database = Open();
        using var session = new SqlSessionContext(database);
        Run(database, session, "CREATE TABLE Source(Bare DECIMAL,Declared DECIMAL(38,28))");
        Run(database, session, "CREATE TABLE Copied AS SELECT Bare,Declared,CAST(NULL AS DECIMAL) AS Casted FROM Source");
        TableSchema schema = database.Tables.Catalog.TryGet("Copied")!;
        Assert.False(schema.Columns[0].HasExplicitDecimalDeclaration);
        Assert.True(schema.Columns[1].HasExplicitDecimalDeclaration);
        Assert.False(schema.Columns[2].HasExplicitDecimalDeclaration);
    }

    /// <summary>CTAS 源查询真实叠加调用方事务先前的持久与临时 DML；回滚不泄漏目标。</summary>
    [Fact]
    public void Sql_CtasSource_SeesBufferedPersistentAndPrivateTemporaryWrites()
    {
        using var database = Open();
        using var session = new SqlSessionContext(database);
        Run(database, session, "CREATE TABLE Source(Id INT,Value INT,PRIMARY KEY(Id))");
        Run(database, session, "INSERT INTO Source(Id,Value) VALUES(1,10)");
        Run(database, session, "CREATE TEMP TABLE Stage(Id INT,Value INT,PRIMARY KEY(Id))");
        var transaction = new SqlTransactionContext();
        Run(database, session, "UPDATE Source SET Value=30", transaction);
        Run(database, session, "INSERT INTO Stage(Id,Value) VALUES(1,40)", transaction);
        Run(database, session, "CREATE TEMP TABLE Copied AS SELECT S.Value AS Persisted,T.Value AS Temporary FROM Source S JOIN Stage T ON S.Id=T.Id", transaction);
        Assert.Equal(new object?[] { 30L, 40L }, Assert.Single(Assert.IsType<SelectExecutionResult>(Run(database, session, "SELECT * FROM Copied", transaction)).Rows));
        Assert.Equal(10L, Assert.Single(Assert.IsType<SelectExecutionResult>(Run(database, null, "SELECT Value FROM Source")).Rows)[0]);
        Run(database, session, "ROLLBACK", transaction);
        Assert.DoesNotContain(session.GetTemporaryTableSchemas(), static schema => schema.Name == "Copied");
        Assert.Empty(Assert.IsType<SelectExecutionResult>(Run(database, session, "SELECT * FROM Stage")).Rows);
    }

    /// <summary>取消真实到达已同步暂存阶段，且失败不留下目标或阻止重试。</summary>
    [Fact]
    public void Sql_CancelAfterDurableStaging_PublishesNothingAndAllowsSameNameRetry()
    {
        using var database = Open();
        using var deadline = new CancellationTokenSource();
        database.Tables.CreateAsPhaseTestHook = phase => { if (phase == "stage-durable") deadline.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => Run(database, null, "CREATE TABLE Copied AS SELECT CAST(1 AS INT) AS Value",
            options: new SqlExecutionOptions { CancellationToken = deadline.Token }));
        database.Tables.CreateAsPhaseTestHook = null;
        Assert.Null(database.Tables.Catalog.TryGet("Copied"));
        Assert.False(File.Exists(Path.Combine(database.RootDirectory, "tables", TableCreationJournal.FileName)));
        Run(database, null, "CREATE TABLE Copied AS SELECT CAST(2 AS INT) AS Value");
        Assert.Equal(2L, Assert.Single(Assert.IsType<SelectExecutionResult>(Run(database, null, "SELECT Value FROM Copied")).Rows)[0]);
    }

    /// <summary>备份在某个会话内执行也排除所有临时 schema、索引和 scratch 文件。</summary>
    [Fact]
    public void Backup_InsideSession_ContainsOnlyPersistentCatalogAndFiles()
    {
        using var database = Open();
        using var first = new SqlSessionContext(database);
        using var second = new SqlSessionContext(database);
        Run(database, first, "CREATE TABLE Persisted(Id INT,PRIMARY KEY(Id))");
        Run(database, first, "CREATE TEMP TABLE Private(Id INT,PRIMARY KEY(Id))");
        Run(database, second, "CREATE TEMP TABLE Other(Id INT,PRIMARY KEY(Id))");
        using (first.EnterScope())
        {
            BackupManifest manifest = new BackupService().Create(database,
                new BackupCreateOptions { DestinationDirectory = Path.Combine(_root, "backup") });
            Assert.Equal("Persisted", Assert.Single(manifest.Models.Tables).Name);
            Assert.DoesNotContain(manifest.Files, file => file.Path.Contains("sonnetdb-sql-session", StringComparison.Ordinal));
        }
        Assert.Single(first.GetTemporaryTableSchemas());
        Assert.Single(second.GetTemporaryTableSchemas());
    }

    /// <summary>事务保存点恢复临时目录，失败的持久提交不得先发布临时工作副本。</summary>
    [Fact]
    public void Sql_SavepointAndFailedMixedCommit_RestoreCommittedTemporaryRows()
    {
        using var database = Open();
        using var session = new SqlSessionContext(database);
        Run(database, session, "CREATE TABLE Persisted(Id INT,PRIMARY KEY(Id))");
        Run(database, session, "INSERT INTO Persisted(Id) VALUES(1)");
        Run(database, session, "CREATE TEMP TABLE Stage(Id INT,Value INT,PRIMARY KEY(Id))");
        Run(database, session, "INSERT INTO Stage(Id,Value) VALUES(1,10)");
        var transaction = new SqlTransactionContext();
        Run(database, session, "UPDATE Stage SET Value=20", transaction);
        SqlTransactionContext.Savepoint savepoint = transaction.CreateSavepoint();
        Run(database, session, "DROP TABLE Stage", transaction);
        transaction.RollbackTo(savepoint);
        Assert.Equal(20L, Assert.Single(Assert.IsType<SelectExecutionResult>(Run(database, session, "SELECT Value FROM Stage", transaction)).Rows)[0]);
        Run(database, session, "INSERT INTO Persisted(Id) VALUES(1)", transaction);
        Assert.ThrowsAny<Exception>(() => Run(database, session, "COMMIT", transaction));
        Assert.True(transaction.IsCompleted);
        Assert.False(transaction.WasCommitted);
        Assert.Equal(10L, Assert.Single(Assert.IsType<SelectExecutionResult>(Run(database, session, "SELECT Value FROM Stage")).Rows)[0]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, "database") });

    private static object? Run(Tsdb database, SqlSessionContext? session, string sql,
        SqlTransactionContext? transaction = null, SqlExecutionOptions? options = null)
        => SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse(sql), null, transaction,
            (options ?? SqlExecutionOptions.Default) with { SessionContext = session });

    /// <summary>回收此夹具独占目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
