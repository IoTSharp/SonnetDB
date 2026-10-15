using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>存量主键列重命名的键编码、关联约束和单目录提交合同。</summary>
public sealed class SqlPrimaryKeyRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-pk-rename-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void RenamePrimaryKey_WithRows_PreservesKeysIndexesChecksDefaultsAndGeneratedState()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, """
                CREATE TABLE items (
                    id INT AUTO_INCREMENT,
                    label STRING DEFAULT 'base',
                    version INT ROWVERSION,
                    PRIMARY KEY (id),
                    CONSTRAINT ck_items CHECK (
                        CASE WHEN id IN (1, 2, 3, 4) THEN id > 0 ELSE NOT (id <= 0) END
                        AND id IS NOT NULL AND lower(label) <> 'id'))
                """);
            SqlExecutor.Execute(db, "CREATE INDEX ix_items_key ON items (id, label)");
            SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_items_label ON items (label)");
            SqlExecutor.Execute(db, "INSERT INTO items (label) VALUES ('one'), ('two')");
            var store = db.Tables.Open("items");
            byte[] originalKey = store.GetByPrimaryKey([1L])!.PrimaryKey.ToArray();

            SqlExecutor.Execute(db, "ALTER TABLE items RENAME COLUMN id TO Identifier");
            TableSchema schema = store.Schema;
            Assert.Equal(["Identifier"], schema.PrimaryKey);
            Assert.True(schema.TryGetColumn("Identifier")!.IsAutoIncrement);
            Assert.Equal("'base'", schema.TryGetColumn("label")!.DefaultExpressionSql);
            Assert.True(schema.TryGetColumn("version")!.IsRowVersion);
            Assert.Contains("\"Identifier\"", Assert.Single(schema.CheckConstraints).ExpressionSql, StringComparison.Ordinal);
            Assert.Contains("'id'", schema.CheckConstraints[0].ExpressionSql, StringComparison.Ordinal);
            Assert.Equal(["Identifier", "label"], schema.Indexes[0].Columns);
            Assert.Equal(originalKey, store.GetByPrimaryKey([1L])!.PrimaryKey.ToArray());
            Assert.Equal(new object?[] { 1L, "one", 1L }, Assert.Single(
                Select(db, "SELECT identifier, label, version FROM items WHERE Identifier = 1").Rows));

            SqlExecutor.Execute(db, "INSERT INTO items DEFAULT VALUES");
            SqlExecutor.Execute(db, "UPDATE items SET label = 'updated' WHERE Identifier = 1 AND version = 1");
            Assert.Equal(2L, Assert.Single(Select(db, "SELECT version FROM items WHERE Identifier = 1").Rows)[0]);
            Assert.Equal(TableConstraintException.CheckViolation, Assert.Throws<TableConstraintException>(() =>
                SqlExecutor.Execute(db, "INSERT INTO items (Identifier, label) VALUES (9, 'id')")).ErrorCode);
            Assert.Equal(TableConstraintException.UniqueViolation, Assert.Throws<TableConstraintException>(() =>
                SqlExecutor.Execute(db, "INSERT INTO items (Identifier, label) VALUES (10, 'two')")).ErrorCode);
            Assert.Equal(TableConstraintException.UniqueViolation, Assert.Throws<TableConstraintException>(() =>
                SqlExecutor.Execute(db, "INSERT INTO items (Identifier, label) VALUES (1, 'duplicate-key')")).ErrorCode);
        }

        using var reopened = Open();
        Assert.Equal(["Identifier"], reopened.Tables.Catalog.TryGet("items")!.PrimaryKey);
        Assert.Equal(new object?[] { 3L, "base", 1L }, Assert.Single(
            Select(reopened, "SELECT Identifier, label, version FROM items WHERE label = 'base'").Rows));
        SqlExecutor.Execute(reopened, "INSERT INTO items (label) VALUES ('after-reopen')");
        Assert.True((long)Assert.Single(Select(reopened, "SELECT Identifier FROM items WHERE label = 'after-reopen'").Rows)[0]! > 3L);
    }

    [Fact]
    public void RenameCompositePrimaryKey_WithInboundAndSelfForeignKeys_PreservesCascadeAndSetNull()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, """
                CREATE TABLE parents (
                    tenant INT, id INT, parent_tenant INT, parent_id INT,
                    PRIMARY KEY (tenant, id),
                    CONSTRAINT fk_self FOREIGN KEY (parent_tenant, parent_id)
                        REFERENCES parents (tenant, id) ON DELETE CASCADE)
                """);
            SqlExecutor.Execute(db, """
                CREATE TABLE children (id INT, tenant INT, parent_id INT, PRIMARY KEY (id),
                    CONSTRAINT fk_parent FOREIGN KEY (tenant, parent_id)
                        REFERENCES parents (tenant, id) ON DELETE SET NULL)
                """);
            SqlExecutor.Execute(db, "INSERT INTO parents (tenant, id, parent_tenant, parent_id) VALUES (7, 1, NULL, NULL), (7, 2, 7, 1)");
            SqlExecutor.Execute(db, "INSERT INTO children (id, tenant, parent_id) VALUES (10, 7, 2)");
            SqlExecutor.Execute(db, "ALTER TABLE parents RENAME COLUMN id TO Identifier");
            SqlExecutor.Execute(db, "ALTER TABLE parents RENAME COLUMN tenant TO TenantID");
            Assert.Equal(["TenantID", "Identifier"], db.Tables.Catalog.TryGet("parents")!.PrimaryKey);
            Assert.Equal(["TenantID", "Identifier"], Assert.Single(db.Tables.Catalog.TryGet("parents")!.ForeignKeys).PrincipalColumns);
            Assert.Equal(["TenantID", "Identifier"], Assert.Single(db.Tables.Catalog.TryGet("children")!.ForeignKeys).PrincipalColumns);
            Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
                SqlExecutor.Execute(db, "INSERT INTO children (id, tenant, parent_id) VALUES (11, 7, 99)")).ErrorCode);
        }

        using var reopened = Open();
        Assert.Equal(2, Select(reopened, "SELECT Identifier FROM parents").Rows.Count);
        SqlExecutor.Execute(reopened, "DELETE FROM parents WHERE TenantID = 7 AND Identifier = 1");
        Assert.Empty(Select(reopened, "SELECT Identifier FROM parents").Rows);
        Assert.Equal(new object?[] { 10L, null, null }, Assert.Single(Select(reopened, "SELECT * FROM children").Rows));
    }

    [Fact]
    public void RenamePrimaryKey_WithOutgoingForeignKey_ChangesOnlyLocalReference()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE principals (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE dependents (id INT, PRIMARY KEY (id), FOREIGN KEY (id) REFERENCES principals (id))");
        SqlExecutor.Execute(db, "INSERT INTO principals (id) VALUES (1), (2)");
        SqlExecutor.Execute(db, "INSERT INTO dependents (id) VALUES (1)");
        SqlExecutor.Execute(db, "ALTER TABLE dependents RENAME COLUMN id TO Identifier");
        var foreignKey = Assert.Single(db.Tables.Catalog.TryGet("dependents")!.ForeignKeys);
        Assert.Equal(["Identifier"], foreignKey.Columns);
        Assert.Equal(["id"], foreignKey.PrincipalColumns);
        SqlExecutor.Execute(db, "INSERT INTO dependents (Identifier) VALUES (2)");
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO dependents (Identifier) VALUES (3)")).ErrorCode);
    }

    [Fact]
    public void RenamePrimaryKey_WithQuotedAndCaseOnlyNames_PreservesExactIdentifierContract()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE Devices (DeviceID INT, label STRING, PRIMARY KEY (DeviceID), CHECK (DeviceID > 0))");
        SqlExecutor.Execute(db, "INSERT INTO Devices (DeviceID, label) VALUES (1, 'one')");
        SqlExecutor.Execute(db, "ALTER TABLE devices RENAME COLUMN deviceid TO deviceid");
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT DEVICEID FROM devices").Rows)[0]);
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT \"DeviceID\" FROM devices"));
        SqlExecutor.Execute(db, "ALTER TABLE devices RENAME COLUMN deviceid TO \"device\"\"ID\"");
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT \"device\"\"ID\" FROM devices").Rows)[0]);
        Assert.Contains("\"device\"\"ID\"", db.Tables.Catalog.TryGet("Devices")!.CheckConstraints[0].ExpressionSql, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "ALTER TABLE devices RENAME COLUMN \"device\"\"ID\" TO LABEL"));
    }

    [Fact]
    public void RenamePrimaryKey_WhenSchemaSaveFails_RestoresHeldStoreAndAllowsRetry()
    {
        using var db = Open();
        SetupParentAndChild(db);
        var held = db.Tables.Open("parents");
        byte[] originalCatalog = File.ReadAllBytes(db.Tables.SchemaPath);
        Directory.CreateDirectory(db.Tables.SchemaPath + ".tmp");
        Exception? error = Record.Exception(() => SqlExecutor.Execute(db, "ALTER TABLE parents RENAME COLUMN id TO Identifier"));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(originalCatalog, File.ReadAllBytes(db.Tables.SchemaPath));
        AssertOldContract(db);
        Assert.Equal(["id"], held.Schema.PrimaryKey);
        Directory.Delete(db.Tables.SchemaPath + ".tmp");
        SqlExecutor.Execute(db, "ALTER TABLE parents RENAME COLUMN id TO Identifier");
        AssertNewContract(db);
    }

    [Fact]
    public void RenamePrimaryKey_WhenPublicationFails_RestoresWholePersistedCatalogBeforeReopen()
    {
        using (var db = Open())
        {
            SetupParentAndChild(db);
            db.Tables.AfterCatalogPersistedBeforePublishTestHook = () =>
            {
                AssertOldContract(db);
                var persisted = TableSchemaCodec.Load(db.Tables.SchemaPath);
                Assert.Equal(["Identifier"], persisted.Single(schema => schema.Name == "parents").PrimaryKey);
                Assert.Equal(["Identifier"], Assert.Single(persisted.Single(schema => schema.Name == "children").ForeignKeys).PrincipalColumns);
                throw new IOException("injected publication failure");
            };
            Assert.Throws<IOException>(() => SqlExecutor.Execute(db, "ALTER TABLE parents RENAME COLUMN id TO Identifier"));
            db.Tables.AfterCatalogPersistedBeforePublishTestHook = null;
            AssertOldContract(db);
        }
        using var reopened = Open();
        AssertOldContract(reopened);
        SqlExecutor.Execute(reopened, "ALTER TABLE parents RENAME COLUMN id TO Identifier");
        AssertNewContract(reopened);
    }

    [Fact]
    public void RenamePrimaryKey_WhenCompensationFails_RequiresReopenAndRecoversCompleteNewContract()
    {
        using (var db = Open())
        {
            SetupParentAndChild(db);
            var held = db.Tables.Open("parents");
            db.Tables.AfterCatalogPersistedBeforePublishTestHook = () =>
            {
                Directory.CreateDirectory(db.Tables.SchemaPath + ".rollback.tmp");
                throw new IOException("injected publication failure");
            };
            Assert.Throws<TableTransactionRecoveryException>(() =>
                SqlExecutor.Execute(db, "ALTER TABLE parents RENAME COLUMN id TO Identifier"));
            Assert.Throws<TableTransactionRecoveryException>(() => held.GetByPrimaryKey([1L]));
            Assert.Throws<IOException>(() => db.Tables.Open("parents"));
        }

        using var reopened = Open();
        AssertNewContract(reopened);
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(reopened, "INSERT INTO children (id, parent_id) VALUES (11, 99)")).ErrorCode);
    }

    [Fact]
    public async Task RenamePrimaryKey_DuringPublication_PublishesAllReferencesTogetherAndSerializesHeldStoreWrite()
    {
        using var db = Open();
        SetupParentAndChild(db);
        var held = db.Tables.Open("parents");
        using var persisted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writeStarted = new ManualResetEventSlim();
        db.Tables.AfterCatalogPersistedBeforePublishTestHook = () =>
        {
            persisted.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var rename = Task.Run(() => SqlExecutor.Execute(db, "ALTER TABLE parents RENAME COLUMN id TO Identifier"));
        Task? write = null;
        try
        {
            Assert.True(persisted.Wait(TimeSpan.FromSeconds(10)));
            // 单次 Snapshot 必须包含同一代父子定义，不能混合观察新主键和旧外键。
            var snapshot = db.Tables.Catalog.Snapshot();
            Assert.Equal(["id"], snapshot.Single(schema => schema.Name == "parents").PrimaryKey);
            Assert.Equal(["id"], Assert.Single(snapshot.Single(schema => schema.Name == "children").ForeignKeys).PrincipalColumns);
            write = Task.Run(() => { writeStarted.Set(); held.Insert([2L]); });
            Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(write.IsCompleted);
        }
        finally
        {
            release.Set();
            await rename.WaitAsync(TimeSpan.FromSeconds(10));
            if (write is not null) await write.WaitAsync(TimeSpan.FromSeconds(10));
            db.Tables.AfterCatalogPersistedBeforePublishTestHook = null;
        }
        AssertNewContract(db);
        Assert.Equal(2L, Assert.Single(Select(db, "SELECT Identifier FROM parents WHERE Identifier = 2").Rows)[0]);
    }

    [Fact]
    public void RenamePrimaryKey_WithViewDependency_RejectsBeforeChangingPersistedSchema()
    {
        using var db = Open();
        SetupParentAndChild(db);
        SqlExecutor.Execute(db, "CREATE VIEW parent_keys AS SELECT id FROM parents");
        byte[] before = File.ReadAllBytes(db.Tables.SchemaPath);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "ALTER TABLE parents RENAME COLUMN id TO Identifier"));
        Assert.Equal(before, File.ReadAllBytes(db.Tables.SchemaPath));
        AssertOldContract(db);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    private static void SetupParentAndChild(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE parents (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE children (id INT, parent_id INT, PRIMARY KEY (id), FOREIGN KEY (parent_id) REFERENCES parents (id))");
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1)");
        SqlExecutor.Execute(db, "INSERT INTO children (id, parent_id) VALUES (10, 1)");
    }

    private static void AssertOldContract(Tsdb db)
    {
        Assert.Equal(["id"], db.Tables.Catalog.TryGet("parents")!.PrimaryKey);
        Assert.Equal(["id"], Assert.Single(db.Tables.Catalog.TryGet("children")!.ForeignKeys).PrincipalColumns);
    }

    private static void AssertNewContract(Tsdb db)
    {
        Assert.Equal(["Identifier"], db.Tables.Catalog.TryGet("parents")!.PrimaryKey);
        Assert.Equal(["Identifier"], Assert.Single(db.Tables.Catalog.TryGet("children")!.ForeignKeys).PrincipalColumns);
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT Identifier FROM parents WHERE Identifier = 1").Rows)[0]);
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT parent_id FROM children WHERE id = 10").Rows)[0]);
    }
}
