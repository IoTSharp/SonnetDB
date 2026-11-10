using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证原生非主键列迁移保留自引用、外部入站外键及持久恢复边界。</summary>
public sealed class TableSelfReferenceSchemaMigrationTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
        "sndb-self-ref-native-migration-" + Guid.NewGuid().ToString("N")));

    /// <summary>已有自引用和外部引用时，非主键日期升级保留索引、约束及完整精度写入。</summary>
    [Fact]
    public void AlterColumn_NonKeyDateTimeWithSelfAndInboundForeignKeys_PreservesRowsIndexesAndReopens()
    {
        using (var db = Open())
        {
            Setup(db);
            AssertForeignKeyRejections(db);
            var inbound = db.Tables.Open("links");
            long inboundWal = inbound.ActiveWalLengthForEvidence;
            long inboundSequence = inbound.RowLockSequence;
            SqlExecutor.Execute(db, "ALTER TABLE nodes ALTER COLUMN stamp TYPE DATETIME2");
            // 入向引用表没有任何提交；共享 LSM 实例中各表共用一个 WAL，只能以本表 sequence 作为证据。
            Assert.Equal(inboundSequence, inbound.RowLockSequence);
            if (!inbound.UsesSharedStorageForEvidence)
                Assert.Equal(inboundWal, inbound.ActiveWalLengthForEvidence);
            AssertMigratedRows(db, DateTime.UnixEpoch);
            SqlExecutor.Execute(db,
                "UPDATE nodes SET stamp=CAST('1970-01-01T00:00:00.0000001Z' AS DATETIME2) WHERE id=2");
            AssertMigratedRows(db, DateTime.UnixEpoch.AddTicks(1));
        }
        using (var reopened = Open())
        {
            AssertMigratedRows(reopened, DateTime.UnixEpoch.AddTicks(1));
            SqlExecutor.Execute(reopened,
                "INSERT INTO nodes(id,code,parent_id,stamp,label) VALUES(3,103,2,CAST(2 AS DATETIME2),'leaf')");
            SqlExecutor.Execute(reopened, "INSERT INTO links(id,node_id) VALUES(11,3)");
        }
        using var again = Open();
        AssertMigratedRows(again, DateTime.UnixEpoch.AddTicks(1));
        Assert.Equal(2L, Row(again, "SELECT parent_id FROM nodes WHERE id=3")[0]);
        Assert.Equal(3L, Row(again, "SELECT node_id FROM links WHERE id=11")[0]);
    }

    /// <summary>真实主键列替换或无损类型变化统一迁移自引用和外部引用，包括空表。</summary>
    [Theory]
    [InlineData(false, "ALTER TABLE nodes ALTER PRIMARY KEY(code)")]
    [InlineData(true, "ALTER TABLE nodes ALTER PRIMARY KEY(code)")]
    [InlineData(false, "ALTER TABLE nodes ALTER COLUMN id TYPE DECIMAL(18,0)")]
    [InlineData(true, "ALTER TABLE nodes ALTER COLUMN id TYPE DECIMAL(18,0)")]
    public void AlterPrimaryKey_WithSelfReference_UpdatesDefinitionAndReferences(bool populated, string sql)
    {
        using var db = Open();
        Setup(db, populated);
        SqlExecutor.Execute(db, sql);
        bool replacesKey = sql.Contains("ALTER PRIMARY KEY", StringComparison.Ordinal);
        string key = replacesKey ? "code" : "id";
        var schema = db.Tables.Catalog.TryGet("nodes")!;
        Assert.Equal([key], schema.PrimaryKey);
        Assert.Equal([key], Assert.Single(schema.ForeignKeys).PrincipalColumns);
        Assert.Equal([key], Assert.Single(db.Tables.Catalog.TryGet("links")!.ForeignKeys).PrincipalColumns);
        Assert.True(TableSchemaMigrationJournal.Read(JournalPath)!.IsComplete);
        if (populated)
        {
            Assert.Equal(replacesKey ? (object)101L : 1m, Row(db, "SELECT parent_id FROM nodes WHERE code=102")[0]);
            Assert.Equal(replacesKey ? (object)102L : 2m, Row(db, "SELECT node_id FROM links WHERE id=10")[0]);
            Assert.Equal("child", Row(db, "SELECT label FROM nodes WHERE code=102")[0]);
        }
    }

    /// <summary>非主键转换仍经过持久 journal；raw 应用失败后恢复旧日期、索引和外键。</summary>
    [Fact]
    public void AlterColumn_NonKeyDateTimeFailureAfterRawApply_RollsBackAndCanRetryAcrossReopen()
    {
        using (var db = Open())
        {
            Setup(db);
            db.Tables.SchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == "after-table:nodes") throw new IOException("injected non-key migration failure");
            };
            try
            {
                Assert.Throws<IOException>(() => SqlExecutor.Execute(db,
                    "ALTER TABLE nodes ALTER COLUMN stamp TYPE DATETIME2"));
            }
            finally { db.Tables.SchemaMigrationPhaseTestHook = null; }
            Assert.Equal(TableColumnType.DateTime, db.Tables.Catalog.TryGet("nodes")!.TryGetColumn("stamp")!.DataType);
            Assert.Equal(DateTime.UnixEpoch, Row(db, "SELECT stamp FROM nodes WHERE id=2")[0]);
            Assert.True(TableSchemaMigrationJournal.Read(JournalPath)!.IsComplete);
            var store = db.Tables.Open("nodes");
            Assert.Equal(2L, Assert.Single(store.GetByIndex(store.Schema.TryGetIndex("ix_stamp")!, [DateTime.UnixEpoch])).Values[0]);
            AssertForeignKeyRejections(db);
        }
        using (var reopened = Open())
        {
            Assert.Equal(DateTime.UnixEpoch, Row(reopened, "SELECT stamp FROM nodes WHERE id=2")[0]);
            SqlExecutor.Execute(reopened, "ALTER TABLE nodes ALTER COLUMN stamp TYPE DATETIME2");
            AssertMigratedRows(reopened, DateTime.UnixEpoch);
        }
        using var again = Open();
        AssertMigratedRows(again, DateTime.UnixEpoch);
    }

    /// <summary>主键不变也不能改变自引用外键源列类型，原 schema、WAL 和数据保持不变。</summary>
    [Fact]
    public void AlterColumn_SelfForeignKeySourceTypeChange_RejectsBeforeWrites()
    {
        using var db = Open();
        Setup(db);
        byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
        long wal = db.Tables.Open("nodes").ActiveWalLengthForEvidence;
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "ALTER TABLE nodes ALTER COLUMN parent_id TYPE STRING"));
        Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
        Assert.Equal(wal, db.Tables.Open("nodes").ActiveWalLengthForEvidence);
        Assert.Equal(TableColumnType.Int64, db.Tables.Catalog.TryGet("nodes")!.TryGetColumn("parent_id")!.DataType);
        Assert.Equal(1L, Row(db, "SELECT parent_id FROM nodes WHERE id=2")[0]);
        Assert.Equal(2L, Row(db, "SELECT node_id FROM links WHERE id=10")[0]);
        AssertForeignKeyRejections(db);
        Assert.False(File.Exists(JournalPath));
    }

    /// <summary>纯旧日期的 GUID 主键表首次进入原生编码时保留现有键字节、引用和完整日期精度。</summary>
    [Fact]
    public void AlterColumn_LegacyGuidTableFirstNativeDateTime_PreservesKeyBytesReferencesAndReopens()
    {
        string rootId = Guid.Parse("23af3bb9-9f0d-4c60-8a0b-6c640009a100").ToString("D");
        string childId = Guid.Parse("54a5f70c-7fb8-4ac2-9f67-4e86ca456200").ToString("D");
        string leafId = Guid.Parse("803ba1e0-e982-464a-8d0a-813a9376e300").ToString("D");
        string orphanId = Guid.Parse("f596c0e6-013b-4578-89d7-8e673869f400").ToString("D");
        string rejectedId = Guid.Parse("c0e79026-41dd-4d57-b804-a44ae0ebf500").ToString("D");
        byte[] rootKey = [];
        byte[] childKey = [];
        using (var db = Open())
        {
            // ABP Guid 映射为 STRING；三个日期列均为旧 DATETIME，尚无任何原生日期列。
            SqlExecutor.Execute(db, "CREATE TABLE AbpOrganizationUnits(Id STRING,ParentId STRING,CreationTime DATETIME NOT NULL,LastModificationTime DATETIME,DeletionTime DATETIME,DisplayName STRING,CONSTRAINT PK_AbpOrganizationUnits PRIMARY KEY(Id),CONSTRAINT FK_AbpOrganizationUnits_AbpOrganizationUnits_ParentId FOREIGN KEY(ParentId) REFERENCES AbpOrganizationUnits(Id))");
            SqlExecutor.Execute(db, "CREATE INDEX ix_org_creation ON AbpOrganizationUnits(CreationTime) INCLUDE(DisplayName,ParentId)");
            SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_org_name ON AbpOrganizationUnits(DisplayName)");
            SqlExecutor.Execute(db, "CREATE TABLE OrgLinks(Id INT,OrganizationUnitId STRING,PRIMARY KEY(Id),CONSTRAINT fk_org_link FOREIGN KEY(OrganizationUnitId) REFERENCES AbpOrganizationUnits(Id))");
            ExecuteGuid(db, "INSERT INTO AbpOrganizationUnits(Id,ParentId,CreationTime,LastModificationTime,DisplayName) VALUES(@root,NULL,-1,10,'root'),(@child,@root,0,20,'child')",
                new SqlParameters().AddNamed("root", rootId).AddNamed("child", childId));
            ExecuteGuid(db, "INSERT INTO OrgLinks(Id,OrganizationUnitId) VALUES(1,@child)",
                new SqlParameters().AddNamed("child", childId));
            var store = db.Tables.Open("AbpOrganizationUnits");
            Assert.DoesNotContain(store.Schema.Columns, static column => column.DataType is
                TableColumnType.DateTime2 or TableColumnType.DateTimeOffset or TableColumnType.Duration);
            rootKey = store.GetByPrimaryKey([rootId])!.PrimaryKey.ToArray();
            childKey = store.GetByPrimaryKey([childId])!.PrimaryKey.ToArray();
            AssertGuidReferences(db);
            SqlExecutor.Execute(db, "ALTER TABLE AbpOrganizationUnits ALTER COLUMN CreationTime TYPE DATETIME2");
            AssertGuidRows(db, DateTime.UnixEpoch);
            ExecuteGuid(db, "UPDATE AbpOrganizationUnits SET CreationTime=@stamp WHERE Id=@child",
                new SqlParameters().AddNamed("stamp", DateTime.UnixEpoch.AddTicks(1)).AddNamed("child", childId));
            AssertGuidRows(db, DateTime.UnixEpoch.AddTicks(1));
        }
        using (var reopened = Open())
        {
            AssertGuidRows(reopened, DateTime.UnixEpoch.AddTicks(1));
            ExecuteGuid(reopened, "INSERT INTO AbpOrganizationUnits(Id,ParentId,CreationTime,DisplayName) VALUES(@leaf,@child,@stamp,'leaf')",
                new SqlParameters().AddNamed("leaf", leafId).AddNamed("child", childId)
                    .AddNamed("stamp", DateTime.UnixEpoch.AddTicks(2)));
            ExecuteGuid(reopened, "INSERT INTO OrgLinks(Id,OrganizationUnitId) VALUES(2,@leaf)",
                new SqlParameters().AddNamed("leaf", leafId));
        }
        using var again = Open();
        AssertGuidRows(again, DateTime.UnixEpoch.AddTicks(1));
        Assert.Equal(childId, Assert.Single(Assert.IsType<SelectExecutionResult>(ExecuteGuid(again,
            "SELECT ParentId FROM AbpOrganizationUnits WHERE Id=@leaf", new SqlParameters().AddNamed("leaf", leafId))).Rows)[0]);
        Assert.Equal(leafId, Row(again, "SELECT OrganizationUnitId FROM OrgLinks WHERE Id=2")[0]);

        void AssertGuidRows(Tsdb db, DateTime childStamp)
        {
            var store = db.Tables.Open("AbpOrganizationUnits");
            Assert.Equal(rootKey, store.GetByPrimaryKey([rootId])!.PrimaryKey.ToArray());
            Assert.Equal(childKey, store.GetByPrimaryKey([childId])!.PrimaryKey.ToArray());
            Assert.Equal(TableColumnType.DateTime2, store.Schema.TryGetColumn("CreationTime")!.DataType);
            Assert.Equal(TableColumnType.DateTime, store.Schema.TryGetColumn("LastModificationTime")!.DataType);
            Assert.Equal(TableColumnType.DateTime, store.Schema.TryGetColumn("DeletionTime")!.DataType);
            Assert.Equal("PK_AbpOrganizationUnits", store.Schema.PrimaryKeyConstraintName);
            Assert.Equal("FK_AbpOrganizationUnits_AbpOrganizationUnits_ParentId", Assert.Single(store.Schema.ForeignKeys).Name);
            Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(-1), store.GetByPrimaryKey([rootId])!.Values[2]);
            var child = store.GetByPrimaryKey([childId])!;
            Assert.Equal(childStamp, child.Values[2]);
            Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(20), child.Values[3]);
            Assert.Null(child.Values[4]);
            Assert.Equal(rootId, child.Values[1]);
            Assert.Equal(childId, Row(db, "SELECT OrganizationUnitId FROM OrgLinks WHERE Id=1")[0]);
            Assert.Equal(childId, Assert.Single(store.GetByIndex(store.Schema.TryGetIndex("ix_org_creation")!, [childStamp])).Values[0]);
            Assert.True(TableSchemaMigrationJournal.Read(JournalPath)!.IsComplete);
            AssertGuidReferences(db);
        }

        void AssertGuidReferences(Tsdb db)
        {
            Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
                ExecuteGuid(db, "INSERT INTO AbpOrganizationUnits(Id,ParentId,CreationTime,DisplayName) VALUES(@candidate,@missing,@stamp,'orphan')",
                    new SqlParameters().AddNamed("candidate", rejectedId).AddNamed("missing", orphanId)
                        .AddNamed("stamp", DateTime.UnixEpoch))).ErrorCode);
            Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
                ExecuteGuid(db, "INSERT INTO OrgLinks(Id,OrganizationUnitId) VALUES(99,@missing)",
                    new SqlParameters().AddNamed("missing", orphanId))).ErrorCode);
            Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
                ExecuteGuid(db, "DELETE FROM AbpOrganizationUnits WHERE Id=@root",
                    new SqlParameters().AddNamed("root", rootId))).ErrorCode);
            Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
                ExecuteGuid(db, "DELETE FROM AbpOrganizationUnits WHERE Id=@child",
                    new SqlParameters().AddNamed("child", childId))).ErrorCode);
        }

        static object? ExecuteGuid(Tsdb db, string sql, SqlParameters parameters)
            => SqlExecutor.Execute(db, databaseName: null, sql, parameters);
    }

    private static void Setup(Tsdb db, bool populated = true)
    {
        SqlExecutor.Execute(db, "CREATE TABLE nodes(id INT,code INT,parent_id INT,stamp DATETIME,native DATETIMEOFFSET,label STRING,CONSTRAINT pk_nodes PRIMARY KEY(id),CONSTRAINT fk_self FOREIGN KEY(parent_id) REFERENCES nodes(id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_stamp ON nodes(stamp) INCLUDE(label,parent_id)");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_label ON nodes(label)");
        SqlExecutor.Execute(db, "CREATE TABLE links(id INT,node_id INT,PRIMARY KEY(id),CONSTRAINT fk_node FOREIGN KEY(node_id) REFERENCES nodes(id))");
        if (!populated) return;
        SqlExecutor.Execute(db, "INSERT INTO nodes(id,code,parent_id,stamp,native,label) VALUES(1,101,NULL,-1,CAST(1 AS DATETIMEOFFSET),'root')");
        SqlExecutor.Execute(db, "INSERT INTO nodes(id,code,parent_id,stamp,native,label) VALUES(2,102,1,0,CAST(2 AS DATETIMEOFFSET),'child')");
        SqlExecutor.Execute(db, "INSERT INTO links(id,node_id) VALUES(10,2)");
    }

    private static void AssertMigratedRows(Tsdb db, DateTime expectedChildStamp)
    {
        var store = db.Tables.Open("nodes");
        Assert.Equal(TableColumnType.DateTime2, store.Schema.TryGetColumn("stamp")!.DataType);
        Assert.Equal("pk_nodes", store.Schema.PrimaryKeyConstraintName);
        Assert.Equal(["id"], store.Schema.PrimaryKey);
        Assert.Equal("fk_self", Assert.Single(store.Schema.ForeignKeys).Name);
        Assert.Equal("fk_node", Assert.Single(db.Tables.Catalog.TryGet("links")!.ForeignKeys).Name);
        Assert.Equal(new object?[] { 1L, "child", new DateTimeOffset(2, TimeSpan.Zero) },
            Row(db, "SELECT parent_id,label,native FROM nodes WHERE id=2"));
        Assert.Equal(2L, Row(db, "SELECT node_id FROM links WHERE id=10")[0]);
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(-1), Row(db, "SELECT stamp FROM nodes WHERE id=1")[0]);
        Assert.Equal(expectedChildStamp, Row(db, "SELECT stamp FROM nodes WHERE id=2")[0]);
        Assert.Equal(2L, Assert.Single(store.GetByIndex(store.Schema.TryGetIndex("ix_stamp")!, [expectedChildStamp])).Values[0]);
        Assert.True(TableSchemaCodec.ReadFormatVersion(db.Tables.SchemaPath) >= 16);
        Assert.True(TableSchemaMigrationJournal.Read(Path.Combine(Path.GetDirectoryName(db.Tables.SchemaPath)!,
            TableSchemaMigrationJournal.FileName))!.IsComplete);
        AssertForeignKeyRejections(db);
        var duplicate = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db,
            "INSERT INTO nodes(id,code,parent_id,label) VALUES(99,199,NULL,'child')"));
        Assert.Equal(TableConstraintException.UniqueViolation, duplicate.ErrorCode);
    }

    private static void AssertForeignKeyRejections(Tsdb db)
    {
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO nodes(id,code,parent_id,label) VALUES(98,198,999,'orphan')")).ErrorCode);
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO links(id,node_id) VALUES(98,999)")).ErrorCode);
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "DELETE FROM nodes WHERE id=1")).ErrorCode);
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "DELETE FROM nodes WHERE id=2")).ErrorCode);
    }

    private static IReadOnlyList<object?> Row(Tsdb db, string sql)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql)).Rows);

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private string JournalPath => Path.Combine(_root, "tables", TableSchemaMigrationJournal.FileName);

    /// <summary>回收本测试独占的绝对路径持久目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
