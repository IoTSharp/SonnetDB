using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>自引用主键真实重映射、持久恢复和失败前预检的回归。</summary>
public sealed class TableSelfPrimaryKeyMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-self-pk-" + Guid.NewGuid().ToString("N"));

    /// <summary>按旧键映射父行，循环、自环、NULL、外部子表与索引共同迁移并重复重开。</summary>
    [Theory]
    [InlineData("ALTER TABLE nodes ALTER PRIMARY KEY(code)", true)]
    [InlineData("ALTER TABLE nodes ALTER COLUMN id TYPE DECIMAL(18,0)", false)]
    public void AlterPrimaryKey_CyclicReferences_MapsAllRowsAndReopensTwice(string sql, bool replacesKey)
    {
        using (var db = Open())
        {
            Setup(db);
            SqlExecutor.Execute(db, sql);
            AssertMigrated(db, replacesKey);
            AssertForeignKey(db, replacesKey ? "INSERT INTO links(id,node_id) VALUES(99,'missing')"
                : "INSERT INTO links(id,node_id) VALUES(99,99)");
            string key = replacesKey ? "code='two'" : "id=2";
            AssertForeignKey(db, $"DELETE FROM nodes WHERE {key}");
        }
        using (var reopened = Open())
        {
            AssertMigrated(reopened, replacesKey);
            string target = replacesKey ? "'three'" : "3";
            SqlExecutor.Execute(reopened, $"INSERT INTO nodes(id,code,parent_id,label) VALUES(3,'three',{target},'third')");
            SqlExecutor.Execute(reopened, $"INSERT INTO links(id,node_id) VALUES(12,{target})");
        }
        using var again = Open();
        AssertMigrated(again, replacesKey);
        Assert.Equal(replacesKey ? (object)"three" : 3m, Row(again, "SELECT node_id FROM links WHERE id=12")[0]);
        Assert.Equal(replacesKey ? (object)"three" : 3m, Row(again, "SELECT parent_id FROM nodes WHERE code='three'")[0]);
    }

    /// <summary>复合键中的部分 NULL 按 MATCH SIMPLE 保留，完整循环引用按新列次序映射。</summary>
    [Fact]
    public void AlterPrimaryKey_CompositeAndPartialNull_MapsOrderedPairs()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE pairs(tenant INT,id INT,code INT,parent_tenant INT,parent_id INT,PRIMARY KEY(tenant,id),CONSTRAINT fk_self FOREIGN KEY(parent_tenant,parent_id) REFERENCES pairs(tenant,id))");
            SqlExecutor.Execute(db, "INSERT INTO pairs(tenant,id,code,parent_tenant,parent_id) VALUES(1,10,100,NULL,10),(1,20,200,1,10)");
            SqlExecutor.Execute(db, "UPDATE pairs SET parent_tenant=1,parent_id=20 WHERE id=10");
            SqlExecutor.Execute(db, "INSERT INTO pairs(tenant,id,code,parent_tenant,parent_id) VALUES(2,30,300,NULL,20)");
            SqlExecutor.Execute(db, "ALTER TABLE pairs ALTER PRIMARY KEY(tenant,code)");
            Assert.Equal(new object?[] { 1L, 200L }, Row(db, "SELECT parent_tenant,parent_id FROM pairs WHERE id=10"));
            Assert.Equal(new object?[] { 1L, 100L }, Row(db, "SELECT parent_tenant,parent_id FROM pairs WHERE id=20"));
            Assert.Equal(new object?[] { null, 20L }, Row(db, "SELECT parent_tenant,parent_id FROM pairs WHERE id=30"));
        }
        using (var reopened = Open())
        {
            Assert.Equal(new object?[] { 1L, 200L }, Row(reopened, "SELECT parent_tenant,parent_id FROM pairs WHERE id=10"));
            AssertForeignKey(reopened, "INSERT INTO pairs(tenant,id,code,parent_tenant,parent_id) VALUES(3,40,400,1,999)");
        }
        using var again = Open();
        Assert.Equal(3, Select(again, "SELECT id FROM pairs").Rows.Count);
    }

    /// <summary>任意持久提交前阶段出错时，整个自引用/外部引用计划回滚并可重试。</summary>
    [Theory]
    [InlineData("after-journal")]
    [InlineData("after-table:nodes")]
    [InlineData("after-table:links")]
    [InlineData("after-catalog")]
    public void AlterPrimaryKey_InterruptedBeforeDecision_RestoresAllTables(string failurePhase)
    {
        using (var db = Open())
        {
            Setup(db);
            db.Tables.SchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == failurePhase) throw new IOException("injected self-pk migration failure");
            };
            try
            {
                Assert.Throws<IOException>(() => SqlExecutor.Execute(db, "ALTER TABLE nodes ALTER PRIMARY KEY(code)"));
            }
            finally { db.Tables.SchemaMigrationPhaseTestHook = null; }
            AssertOriginal(db);
        }
        using (var reopened = Open())
        {
            AssertOriginal(reopened);
            SqlExecutor.Execute(reopened, "ALTER TABLE nodes ALTER PRIMARY KEY(code)");
            AssertMigrated(reopened, true);
        }
        using var again = Open();
        AssertMigrated(again, true);
    }

    /// <summary>提交决定写入后出现 I/O 未知结果时禁止继续访问，重复重开采用已提交状态。</summary>
    [Fact]
    public void AlterPrimaryKey_CommitDecisionFlushThrows_ReopensCommittedMapping()
    {
        using (var db = Open())
        {
            Setup(db);
            TableSchemaMigrationJournal.BeforeCompleteFlushTestHook = path =>
            {
                if (path == JournalPath) throw new IOException("injected self-pk decision flush failure");
            };
            try
            {
                Assert.Throws<TableTransactionRecoveryException>(() => SqlExecutor.Execute(db,
                    "ALTER TABLE nodes ALTER PRIMARY KEY(code)"));
                Assert.Throws<IOException>(() => db.Tables.Open("nodes").Scan());
            }
            finally { TableSchemaMigrationJournal.BeforeCompleteFlushTestHook = null; }
        }
        using (var reopened = Open()) AssertMigrated(reopened, true);
        using var again = Open();
        AssertMigrated(again, true);
    }

    /// <summary>新键碰撞、NULL、溢出或无损转换失败均不写目录、WAL 或 journal。</summary>
    [Theory]
    [InlineData("UPDATE nodes SET code='one' WHERE id=2", "ALTER TABLE nodes ALTER PRIMARY KEY(code)")]
    [InlineData("UPDATE nodes SET code=NULL WHERE id=2", "ALTER TABLE nodes ALTER PRIMARY KEY(code)")]
    [InlineData("UPDATE nodes SET code='9223372036854775808' WHERE id=2", "ALTER TABLE nodes ALTER PRIMARY KEY(code); ALTER TABLE nodes ALTER COLUMN code TYPE INT")]
    public void AlterPrimaryKey_InvalidTarget_RejectsBeforePersistentWrites(string change, string sql)
    {
        using var db = Open();
        Setup(db, uniqueCode: false);
        SqlExecutor.Execute(db, change);
        // 溢出场景先建立 STRING 主键，第二次迁移的预检不得修改该新基线。
        if (sql.Contains(';'))
        {
            SqlExecutor.Execute(db, "UPDATE nodes SET code='1' WHERE id=1");
            SqlExecutor.Execute(db, "ALTER TABLE nodes ALTER PRIMARY KEY(code)");
            sql = "ALTER TABLE nodes ALTER COLUMN code TYPE INT";
        }
        byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
        long nodesWal = db.Tables.Open("nodes").ActiveWalLengthForEvidence;
        long linksWal = db.Tables.Open("links").ActiveWalLengthForEvidence;
        byte[]? journal = File.Exists(JournalPath) ? File.ReadAllBytes(JournalPath) : null;
        Assert.NotNull(Record.Exception(() => SqlExecutor.Execute(db, sql)));
        Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
        Assert.Equal(nodesWal, db.Tables.Open("nodes").ActiveWalLengthForEvidence);
        Assert.Equal(linksWal, db.Tables.Open("links").ActiveWalLengthForEvidence);
        Assert.Equal(journal, File.Exists(JournalPath) ? File.ReadAllBytes(JournalPath) : null);
    }

    /// <summary>自引用源列参与旧/新主键的复杂拓扑在任何写入前明确拒绝。</summary>
    [Fact]
    public void AlterPrimaryKey_OverlappingSelfForeignKey_RejectsBeforeWrites()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE identity_ref(id INT,code INT,PRIMARY KEY(id),FOREIGN KEY(id) REFERENCES identity_ref(id))");
        SqlExecutor.Execute(db, "INSERT INTO identity_ref(id,code) VALUES(1,2)");
        byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
        long wal = db.Tables.Open("identity_ref").ActiveWalLengthForEvidence;
        var error = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db,
            "ALTER TABLE identity_ref ALTER PRIMARY KEY(code)"));
        Assert.Equal(TableConstraintException.SchemaEvolutionUnsupported, error.ErrorCode);
        Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
        Assert.Equal(wal, db.Tables.Open("identity_ref").ActiveWalLengthForEvidence);
        Assert.False(File.Exists(JournalPath));
    }

    /// <summary>取消预检或持久表应用均不留下部分映射，后续 DML 和重开仍可使用。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlterPrimaryKey_CancelledSelfMigration_RestoresReferences(bool afterRaw)
    {
        using (var db = Open())
        {
            Setup(db);
            byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
            using var stop = new CancellationTokenSource();
            if (afterRaw)
                db.Tables.SchemaMigrationPhaseTestHook = phase =>
                {
                    if (phase == "after-table:nodes") stop.Cancel();
                };
            else stop.Cancel();
            try
            {
                using (RoutineExecutionContext.EnterRoot(new SqlExecutionOptions { CancellationToken = stop.Token }))
                    Assert.Throws<RoutineExecutionException>(() => db.Tables.AlterTablePrimaryKey("nodes", ["code"], null));
            }
            finally { db.Tables.SchemaMigrationPhaseTestHook = null; }
            if (!afterRaw) Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
            AssertOriginal(db);
            SqlExecutor.Execute(db, "UPDATE nodes SET label='after-cancel' WHERE id=1");
        }
        using (var reopened = Open())
        {
            AssertOriginal(reopened);
            Assert.Equal("after-cancel", Row(reopened, "SELECT label FROM nodes WHERE id=1")[0]);
        }
        using var again = Open();
        AssertOriginal(again);
    }

    /// <summary>恢复 journal 尚无跨 schema 短名消歧时，提前拒绝不安全的迁移目录。</summary>
    [Fact]
    public void AlterPrimaryKey_DuplicateShortNamesAcrossSchemas_RejectsBeforeRecoveryBarrier()
    {
        using (var db = Open())
        {
            Setup(db);
            SqlExecutor.Execute(db, "CREATE TABLE main.extra(id INT,PRIMARY KEY(id))");
            SqlExecutor.Execute(db, "CREATE TABLE sales.extra(id INT,PRIMARY KEY(id))");
            byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
            long wal = db.Tables.Open("nodes").ActiveWalLengthForEvidence;
            var error = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db,
                "ALTER TABLE nodes ALTER PRIMARY KEY(code)"));
            Assert.Equal(TableConstraintException.SchemaEvolutionUnsupported, error.ErrorCode);
            Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
            Assert.Equal(wal, db.Tables.Open("nodes").ActiveWalLengthForEvidence);
            Assert.False(File.Exists(JournalPath));
        }
        using (var reopened = Open()) AssertOriginal(reopened);
        using var again = Open();
        AssertOriginal(again);
    }

    private void Setup(Tsdb db, bool uniqueCode = true)
    {
        SqlExecutor.Execute(db, "CREATE TABLE nodes(id INT,code STRING,parent_id INT,label STRING,PRIMARY KEY(id),CONSTRAINT fk_self FOREIGN KEY(parent_id) REFERENCES nodes(id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_parent ON nodes(parent_id) INCLUDE(label)");
        if (uniqueCode) SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_code ON nodes(code)");
        SqlExecutor.Execute(db, "CREATE TABLE links(id INT,node_id INT,PRIMARY KEY(id),CONSTRAINT fk_node FOREIGN KEY(node_id) REFERENCES nodes(id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_node ON links(node_id)");
        SqlExecutor.Execute(db, "INSERT INTO nodes(id,code,parent_id,label) VALUES(1,'one',NULL,'first'),(2,'two',1,'second')");
        SqlExecutor.Execute(db, "UPDATE nodes SET parent_id=2 WHERE id=1");
        SqlExecutor.Execute(db, "INSERT INTO links(id,node_id) VALUES(10,1),(11,2)");
    }

    private static void AssertOriginal(Tsdb db)
    {
        Assert.Equal(["id"], db.Tables.Catalog.TryGet("nodes")!.PrimaryKey);
        Assert.Equal(2L, Row(db, "SELECT parent_id FROM nodes WHERE id=1")[0]);
        Assert.Equal(1L, Row(db, "SELECT parent_id FROM nodes WHERE id=2")[0]);
        Assert.Equal(2L, Row(db, "SELECT node_id FROM links WHERE id=11")[0]);
        AssertForeignKey(db, "INSERT INTO links(id,node_id) VALUES(99,99)");
    }

    private static void AssertMigrated(Tsdb db, bool replacesKey)
    {
        string key = replacesKey ? "code" : "id";
        Assert.Equal([key], db.Tables.Catalog.TryGet("nodes")!.PrimaryKey);
        Assert.Equal([key], Assert.Single(db.Tables.Catalog.TryGet("nodes")!.ForeignKeys).PrincipalColumns);
        Assert.Equal([key], Assert.Single(db.Tables.Catalog.TryGet("links")!.ForeignKeys).PrincipalColumns);
        Assert.Equal(replacesKey ? (object)"two" : 2m, Row(db, "SELECT parent_id FROM nodes WHERE code='one'")[0]);
        Assert.Equal(replacesKey ? (object)"one" : 1m, Row(db, "SELECT parent_id FROM nodes WHERE code='two'")[0]);
        Assert.Equal(replacesKey ? (object)"two" : 2m, Row(db, "SELECT node_id FROM links WHERE id=11")[0]);
        var store = db.Tables.Open("nodes");
        Assert.Equal("first", Assert.Single(store.GetByIndex(store.Schema.TryGetIndex("ix_parent")!,
            [replacesKey ? (object)"two" : 2m])).Values[3]);
    }

    private static void AssertForeignKey(Tsdb db, string sql)
        => Assert.Equal(TableConstraintException.ForeignKeyViolation,
            Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db, sql)).ErrorCode);

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private string JournalPath => Path.Combine(_root, "tables", TableSchemaMigrationJournal.FileName);
    private static SelectExecutionResult Select(Tsdb db, string sql) => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
    private static IReadOnlyList<object?> Row(Tsdb db, string sql) => Assert.Single(Select(db, sql).Rows);

    /// <summary>释放本测试独占的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
