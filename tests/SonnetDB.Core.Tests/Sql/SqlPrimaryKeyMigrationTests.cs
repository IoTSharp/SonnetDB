using System.Buffers.Binary;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>存量主键定义与类型迁移的预检、外键、生成状态和持久恢复合同。</summary>
public sealed class SqlPrimaryKeyMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-pk-migration-" + Guid.NewGuid().ToString("N"));

    /// <summary>交换编码键且保留默认值、CHECK、索引、自增高水位与现有版本值。</summary>
    [Fact]
    public void AlterPrimaryKey_PopulatedTable_PreservesRowsIndexesAndGeneratedStateAcrossReopen()
    {
        using (var db = Open())
        {
            Setup(db);
            SqlExecutor.Execute(db, "UPDATE parents SET label='updated' WHERE id=1 AND version=1");
            SqlExecutor.Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY (code)");
            Assert.Equal(["code"], db.Tables.Catalog.TryGet("parents")!.PrimaryKey);
            Assert.Equal(2L, Row(db, "SELECT version FROM parents WHERE code=2")[0]);
            Assert.Equal(1L, Row(db, "SELECT version FROM parents WHERE code=1")[0]);
            Assert.Equal(2L, Row(db, "SELECT parent_id FROM children WHERE id=10")[0]);
            Assert.Equal("updated", Row(db, "SELECT label FROM parents WHERE code=2")[0]);
            SqlExecutor.Execute(db, "INSERT INTO parents(code) VALUES(3)");
            Assert.Equal(new object?[] { 3L, "base", 1L }, Row(db, "SELECT id,label,version FROM parents WHERE code=3"));
            AssertError(db, "INSERT INTO parents(id,code,label) VALUES(9,1,'duplicate-key')", TableConstraintException.UniqueViolation);
            AssertError(db, "INSERT INTO parents(id,code,label) VALUES(9,4,'two')", TableConstraintException.UniqueViolation);
            AssertError(db, "INSERT INTO parents(id,code,label) VALUES(9,-1,'negative')", TableConstraintException.CheckViolation);
            AssertError(db, "INSERT INTO children(id,parent_id) VALUES(11,99)", TableConstraintException.ForeignKeyViolation);
        }
        using var reopened = Open();
        var schema = reopened.Tables.Catalog.TryGet("parents")!;
        Assert.Equal(["code"], schema.PrimaryKey);
        Assert.True(schema.TryGetColumn("id")!.IsAutoIncrement);
        Assert.True(schema.TryGetColumn("version")!.IsRowVersion);
        Assert.Equal(["code"], Assert.Single(reopened.Tables.Catalog.TryGet("children")!.ForeignKeys).PrincipalColumns);
        SqlExecutor.Execute(reopened, "INSERT INTO parents(code,label) VALUES(4,'after-reopen')");
        object?[] inserted = Row(reopened, "SELECT id,label,version FROM parents WHERE code=4");
        Assert.Equal(10L, inserted[0]);
        Assert.Equal("after-reopen", inserted[1]);
        Assert.Equal(1L, inserted[2]);
        Assert.Equal(13, ReadCatalogVersion());
    }

    /// <summary>父主键严格转型时，真实子表 FK 值、类型、默认表达式和索引一并演进。</summary>
    [Fact]
    public void AlterColumn_PrimaryKeyType_UpdatesForeignKeyRowsDefaultsAndSchema()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE parents(id STRING, label STRING, PRIMARY KEY(id))");
            SqlExecutor.Execute(db, "CREATE INDEX ix_parent ON parents(id,label)");
            SqlExecutor.Execute(db, "CREATE TABLE children(id INT,parent_id STRING DEFAULT '1',PRIMARY KEY(id),CONSTRAINT fk_parent FOREIGN KEY(parent_id) REFERENCES parents(id))");
            SqlExecutor.Execute(db, "CREATE INDEX ix_fk ON children(parent_id)");
            SqlExecutor.Execute(db, "INSERT INTO parents(id,label) VALUES('1','one'),('2','two')");
            SqlExecutor.Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,'1')");
            SqlExecutor.Execute(db, "ALTER TABLE parents ALTER COLUMN id TYPE INT");
            Assert.Equal(TableColumnType.Int64, db.Tables.Catalog.TryGet("parents")!.TryGetColumn("id")!.DataType);
            Assert.Equal(TableColumnType.Int64, db.Tables.Catalog.TryGet("children")!.TryGetColumn("parent_id")!.DataType);
            Assert.Equal(1L, Row(db, "SELECT parent_id FROM children WHERE id=10")[0]);
            SqlExecutor.Execute(db, "INSERT INTO children(id) VALUES(11)");
            Assert.Equal(1L, Row(db, "SELECT parent_id FROM children WHERE id=11")[0]);
            AssertError(db, "DELETE FROM parents WHERE id=1", TableConstraintException.ForeignKeyViolation);
        }
        using var reopened = Open();
        Assert.Equal(2, Select(reopened, "SELECT id FROM children WHERE parent_id=1").Rows.Count);
        Assert.Equal("one", Row(reopened, "SELECT label FROM parents WHERE id=1")[0]);
        AssertError(reopened, "INSERT INTO children(id,parent_id) VALUES(12,99)", TableConstraintException.ForeignKeyViolation);
    }

    /// <summary>列数保持一致的主键替换可按旧行身份重映射跨类型 FK。</summary>
    [Fact]
    public void AlterPrimaryKey_DifferentColumnType_MapsChildValuesToTheSamePrincipalRow()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE parents(id INT,code STRING,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id))");
        SqlExecutor.Execute(db, "INSERT INTO parents(id,code) VALUES(1,'alpha'),(2,'beta')");
        SqlExecutor.Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1),(11,2),(12,NULL)");
        SqlExecutor.Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        Assert.Equal("alpha", Row(db, "SELECT parent_id FROM children WHERE id=10")[0]);
        Assert.Equal("beta", Row(db, "SELECT parent_id FROM children WHERE id=11")[0]);
        Assert.Null(Row(db, "SELECT parent_id FROM children WHERE id=12")[0]);
        Assert.Equal(TableColumnType.String, db.Tables.Catalog.TryGet("children")!.TryGetColumn("parent_id")!.DataType);
        AssertError(db, "INSERT INTO children(id,parent_id) VALUES(13,'unknown')", TableConstraintException.ForeignKeyViolation);
    }

    /// <summary>新增复合主键和移除主键均保留已有物理行，无主键行身份不合并重复值。</summary>
    [Fact]
    public void AlterPrimaryKey_KeylessAndCompositeTransitions_PreserveRows()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE items(tenant INT,code STRING,label STRING)");
            SqlExecutor.Execute(db, "INSERT INTO items(tenant,code,label) VALUES(1,'x','one'),(2,'x','two')");
            SqlExecutor.Execute(db, "ALTER TABLE items ALTER PRIMARY KEY(tenant,code)");
            AssertError(db, "INSERT INTO items(tenant,code,label) VALUES(1,'x','duplicate')", TableConstraintException.UniqueViolation);
            db.Tables.AlterTablePrimaryKey("items", [], null);
            SqlExecutor.Execute(db, "INSERT INTO items(tenant,code,label) VALUES(1,'x','one')");
            Assert.Equal(3, Select(db, "SELECT tenant FROM items").Rows.Count);
        }
        using var reopened = Open();
        Assert.Empty(reopened.Tables.Catalog.TryGet("items")!.PrimaryKey);
        Assert.Equal(3, Select(reopened, "SELECT tenant FROM items").Rows.Count);
        Assert.Equal(1, Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(reopened,
            "DELETE FROM items WHERE tenant=1 LIMIT 1")).SeriesAffected);
        Assert.Equal(2, Select(reopened, "SELECT tenant FROM items").Rows.Count);
    }

    /// <summary>重复、NULL 和严格转型失败都在目录屏障或任何源写入之前拒绝。</summary>
    [Theory]
    [InlineData("INT", "1", "1", "ALTER TABLE items ALTER PRIMARY KEY(code)")]
    [InlineData("INT", "NULL", "2", "ALTER TABLE items ALTER PRIMARY KEY(code)")]
    [InlineData("STRING", "'bad'", "'2'", "ALTER TABLE items ALTER COLUMN code TYPE INT")]
    [InlineData("STRING", "'9223372036854775808'", "'2'", "ALTER TABLE items ALTER COLUMN code TYPE INT")]
    [InlineData("FLOAT", "1.5", "2.0", "ALTER TABLE items ALTER COLUMN code TYPE INT")]
    public void PrimaryKeyMigration_InvalidTarget_LeavesOriginalCatalogWalAndRowsUntouched(
        string codeType, string first, string second, string migration)
    {
        using var db = Open();
        bool typeChange = migration.Contains("ALTER COLUMN", StringComparison.Ordinal);
        SqlExecutor.Execute(db, $"CREATE TABLE items(id INT,code {codeType},PRIMARY KEY({(typeChange ? "code" : "id")}))");
        SqlExecutor.Execute(db, $"INSERT INTO items(id,code) VALUES(1,{first}),(2,{second})");
        TableStore store = db.Tables.Open("items");
        byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
        long wal = store.ActiveWalLengthForEvidence;
        Assert.NotNull(Record.Exception(() => SqlExecutor.Execute(db, migration)));
        Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
        Assert.Equal(wal, store.ActiveWalLengthForEvidence);
        Assert.Equal(2, Select(db, "SELECT id FROM items").Rows.Count);
        Assert.False(File.Exists(JournalPath));
    }

    /// <summary>迁移跨页完成后出现错误时同步恢复旧语义，并保留防旧引擎写入的目录版本。</summary>
    [Theory]
    [InlineData("after-table:parents")]
    [InlineData("after-table:children")]
    [InlineData("after-catalog")]
    public void PrimaryKeyMigration_FailureBeforeComplete_RollsBackAndPermitsNewWrites(string phase)
    {
        using (var db = Open())
        {
            Setup(db);
            db.Tables.SchemaMigrationPhaseTestHook = actual =>
            {
                if (actual == phase) throw new IOException("injected migration failure");
            };
            Assert.Throws<IOException>(() => SqlExecutor.Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)"));
            db.Tables.SchemaMigrationPhaseTestHook = null;
            Assert.Equal(["id"], db.Tables.Catalog.TryGet("parents")!.PrimaryKey);
            Assert.Equal(1L, Row(db, "SELECT parent_id FROM children WHERE id=10")[0]);
            Assert.True(TableSchemaMigrationJournal.Read(JournalPath)!.IsComplete);
            SqlExecutor.Execute(db, "UPDATE parents SET label='after-rollback' WHERE id=1");
        }
        using var reopened = Open();
        Assert.Equal("after-rollback", Row(reopened, "SELECT label FROM parents WHERE id=1")[0]);
        Assert.Equal(13, ReadCatalogVersion());
    }

    /// <summary>Prepare 的 rename 已生效但同步结果未知时，管理器禁止新写直到重开。</summary>
    [Fact]
    public void PrimaryKeyMigration_PreparePublishedThenThrows_PoisonsUntilRecovery()
    {
        using (var db = Open())
        {
            Setup(db);
            TableSchemaMigrationJournal.AfterPreparePublishedTestHook = path =>
            {
                if (path == JournalPath) throw new IOException("injected journal directory sync failure");
            };
            try
            {
                Assert.Throws<TableTransactionRecoveryException>(() => SqlExecutor.Execute(db,
                    "ALTER TABLE parents ALTER PRIMARY KEY(code)"));
                Assert.Throws<IOException>(() => SqlExecutor.Execute(db,
                    "UPDATE parents SET label='must-not-write' WHERE id=1"));
                Assert.False(TableSchemaMigrationJournal.Read(JournalPath)!.IsComplete);
            }
            finally { TableSchemaMigrationJournal.AfterPreparePublishedTestHook = null; }
        }
        using var reopened = Open();
        Assert.Equal(["id"], reopened.Tables.Catalog.TryGet("parents")!.PrimaryKey);
        Assert.Equal("one", Row(reopened, "SELECT label FROM parents WHERE id=1")[0]);
        Assert.Equal(13, ReadCatalogVersion());
    }

    /// <summary>完整提交标记已写但 flush 抛异常时不能反向 undo，重开读取持久决定。</summary>
    [Fact]
    public void PrimaryKeyMigration_CompleteFlushThrows_PoisonsAndReopensCommittedState()
    {
        using (var db = Open())
        {
            Setup(db);
            TableSchemaMigrationJournal.BeforeCompleteFlushTestHook = path =>
            {
                if (path == JournalPath) throw new IOException("injected commit sync failure");
            };
            try
            {
                Assert.Throws<TableTransactionRecoveryException>(() => SqlExecutor.Execute(db,
                    "ALTER TABLE parents ALTER PRIMARY KEY(code)"));
                Assert.Throws<IOException>(() => db.Tables.Open("parents").Scan());
            }
            finally { TableSchemaMigrationJournal.BeforeCompleteFlushTestHook = null; }
        }
        using var reopened = Open();
        Assert.Equal(["code"], reopened.Tables.Catalog.TryGet("parents")!.PrimaryKey);
        Assert.Equal(2L, Row(reopened, "SELECT parent_id FROM children WHERE id=10")[0]);
        Assert.Equal(2, Select(reopened, "SELECT id FROM parents").Rows.Count);
    }

    /// <summary>生成列类型变更和无法可靠映射的 FK 拓扑始终在源修改前明确拒绝。</summary>
    [Fact]
    public void PrimaryKeyMigration_UnsupportedGeneratedTypeOrForeignKeyArity_PreservesSource()
    {
        using var db = Open();
        Setup(db);
        byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
        Assert.NotNull(Record.Exception(() => SqlExecutor.Execute(db, "ALTER TABLE parents ALTER COLUMN id TYPE FLOAT")));
        AssertError(db, "ALTER TABLE parents ALTER PRIMARY KEY(id,code)", TableConstraintException.SchemaEvolutionUnsupported);
        Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
        Assert.Equal(2, Select(db, "SELECT id FROM parents").Rows.Count);
        Assert.Equal(1L, Row(db, "SELECT parent_id FROM children WHERE id=10")[0]);
    }

    /// <summary>单个超 Decimal 范围的文本键也不能靠 FLOAT 舍入通过，原索引和 FK 完全保留。</summary>
    [Theory]
    [InlineData("79228162514264337593543950337")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("7.9228162514264337593543950337e28")]
    public void PrimaryKeyMigration_InexactNumericText_RejectsBeforeCatalogOrForeignKeyChanges(string value)
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE parents(id STRING,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_key ON parents(id)");
        SqlExecutor.Execute(db, "CREATE TABLE children(id INT,parent_id STRING,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id))");
        SqlExecutor.Execute(db, $"INSERT INTO parents(id) VALUES('{value}')");
        SqlExecutor.Execute(db, $"INSERT INTO children(id,parent_id) VALUES(1,'{value}')");
        byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
        Assert.NotNull(Record.Exception(() => SqlExecutor.Execute(db, "ALTER TABLE parents ALTER COLUMN id TYPE FLOAT")));
        Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
        Assert.Equal(value, Row(db, $"SELECT id FROM parents WHERE id='{value}'")[0]);
        Assert.Equal(value, Row(db, "SELECT parent_id FROM children WHERE id=1")[0]);
        Assert.Equal(TableColumnType.String, db.Tables.Catalog.TryGet("parents")!.TryGetColumn("id")!.DataType);
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, $"DELETE FROM parents WHERE id='{value}'")).ErrorCode);
    }

    /// <summary>取消发生在预检或 raw 表写入后，均恢复原语义；已经建立的屏障不得降级。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimaryKeyMigration_Cancelled_PreservesSourceAndAllowsSubsequentWrites(bool afterRaw)
    {
        using (var db = Open())
        {
            Setup(db);
            byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
            long wal = db.Tables.Open("parents").ActiveWalLengthForEvidence;
            using var stop = new CancellationTokenSource();
            if (afterRaw)
                db.Tables.SchemaMigrationPhaseTestHook = phase =>
                {
                    if (phase == "after-table:parents") stop.Cancel();
                };
            else stop.Cancel();
            using (RoutineExecutionContext.EnterRoot(new SqlExecutionOptions { CancellationToken = stop.Token }))
                Assert.Throws<RoutineExecutionException>(() => db.Tables.AlterTablePrimaryKey("parents", ["code"], null));
            db.Tables.SchemaMigrationPhaseTestHook = null;
            if (afterRaw) Assert.Equal(13, ReadCatalogVersion());
            else
            {
                Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
                Assert.Equal(wal, db.Tables.Open("parents").ActiveWalLengthForEvidence);
            }
            Assert.Equal(["id"], db.Tables.Catalog.TryGet("parents")!.PrimaryKey);
            Assert.Equal(1L, Row(db, "SELECT parent_id FROM children WHERE id=10")[0]);
            SqlExecutor.Execute(db, "UPDATE parents SET label='after-cancel' WHERE id=1");
        }
        using var reopened = Open();
        Assert.Equal("after-cancel", Row(reopened, "SELECT label FROM parents WHERE id=1")[0]);
    }

    private string JournalPath => Path.Combine(_root, "tables", TableSchemaMigrationJournal.FileName);
    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private int ReadCatalogVersion() => BinaryPrimitives.ReadInt32LittleEndian(
        File.ReadAllBytes(Path.Combine(_root, "tables", TableSchemaCodec.FileName)).AsSpan(8));

    private static void Setup(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE parents(id INT AUTO_INCREMENT,code INT NOT NULL,label STRING DEFAULT 'base',version INT ROWVERSION,PRIMARY KEY(id),CONSTRAINT ck_code CHECK(code>0))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_label ON parents(label)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_code ON parents(code)");
        SqlExecutor.Execute(db, "CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),CONSTRAINT fk_parent FOREIGN KEY(parent_id) REFERENCES parents(id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_parent ON children(parent_id)");
        SqlExecutor.Execute(db, "INSERT INTO parents(code,label) VALUES(2,'one'),(1,'two')");
        SqlExecutor.Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1)");
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
    private static object?[] Row(Tsdb db, string sql) => Assert.Single(Select(db, sql).Rows).ToArray();
    private static void AssertError(Tsdb db, string sql, string code)
        => Assert.Equal(code, Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db, sql)).ErrorCode);

    /// <summary>清理独占数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
