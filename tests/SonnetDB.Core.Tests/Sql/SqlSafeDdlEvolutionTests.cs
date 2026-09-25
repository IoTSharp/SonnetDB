using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlSafeDdlEvolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-safe-ddl-" + Guid.NewGuid().ToString("N"));

    public SqlSafeDdlEvolutionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理不覆盖断言。 */ }
    }

    [Theory]
    [InlineData("ALTER TABLE items ADD COLUMN version STRING ROWVERSION")]
    [InlineData("ALTER TABLE items ADD COLUMN version INT ROWVERSION NULL")]
    [InlineData("ALTER TABLE items ADD COLUMN version INT ROWVERSION DEFAULT 1")]
    [InlineData("ALTER TABLE items ADD COLUMN sequence STRING AUTO_INCREMENT")]
    [InlineData("ALTER TABLE items ADD COLUMN sequence INT AUTO_INCREMENT NULL")]
    [InlineData("ALTER TABLE items ADD COLUMN sequence INT AUTO_INCREMENT DEFAULT 1")]
    [InlineData("ALTER TABLE items ADD COLUMN sequence INT AUTO_INCREMENT ROWVERSION")]
    public void ParseAddGeneratedColumn_InvalidDefinition_IsRejected(string sql)
        => Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));

    [Fact]
    public void AddRowVersion_WithExistingRows_BackfillsAndPersistsOptimisticVersion()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE items (id INT, name STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "INSERT INTO items (id, name) VALUES (1, 'first'), (2, 'second')");

            var statement = Assert.IsType<AlterTableAddColumnStatement>(
                SqlParser.Parse("ALTER TABLE items ADD COLUMN version INT ROWVERSION"));
            Assert.True(statement.IsRowVersion);
            SqlExecutor.Execute(db, "ALTER TABLE items ADD COLUMN version INT ROWVERSION");

            Assert.Equal([1L, 1L], Select(db, "SELECT version FROM items ORDER BY id")
                .Rows.Select(static row => (long)row[0]!).ToArray());
            SqlExecutor.Execute(db, "UPDATE items SET name = 'updated' WHERE id = 1 AND version = 1");
            Assert.Equal([2L, 1L], Select(db, "SELECT version FROM items ORDER BY id")
                .Rows.Select(static row => (long)row[0]!).ToArray());
        }

        using var reopened = Open();
        Assert.True(reopened.Tables.Catalog.TryGet("items")!.TryGetColumn("version")!.IsRowVersion);
        Assert.Equal([2L, 1L], Select(reopened, "SELECT version FROM items ORDER BY id")
            .Rows.Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void AddAutoIncrement_WithExistingRows_BackfillsAndContinuesAfterRestart()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE items (id INT, name STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "INSERT INTO items (id, name) VALUES (20, 'later'), (10, 'first')");

            var statement = Assert.IsType<AlterTableAddColumnStatement>(
                SqlParser.Parse("ALTER TABLE items ADD COLUMN sequence INT AUTO_INCREMENT"));
            Assert.True(statement.IsAutoIncrement);
            SqlExecutor.Execute(db, "ALTER TABLE items ADD COLUMN sequence INT AUTO_INCREMENT");

            Assert.Equal([1L, 2L], Select(db, "SELECT sequence FROM items ORDER BY id")
                .Rows.Select(static row => (long)row[0]!).ToArray());
            Assert.False(db.Tables.Catalog.TryGet("items")!.TryGetColumn("sequence")!.IsNullable);
        }

        using var reopened = Open();
        SqlExecutor.Execute(reopened, "INSERT INTO items (id, name) VALUES (30, 'next')");
        Assert.Equal([1L, 2L, 3L], Select(reopened, "SELECT sequence FROM items ORDER BY id")
            .Rows.Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void AddGeneratedColumn_WhenTableAlreadyHasOne_LeavesRowsAndSchemaUntouched()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE items (id INT, version INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO items (id) VALUES (1)");

        Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(
            db, "ALTER TABLE items ADD COLUMN second_version INT ROWVERSION"));

        Assert.Null(db.Tables.Catalog.TryGet("items")!.TryGetColumn("second_version"));
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT version FROM items").Rows)[0]);
    }

    [Fact]
    public void AddGeneratedColumns_PreservesDefaultsChecksIndexesAndDecimalMetadata()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE items (
                id INT,
                name STRING DEFAULT 'base',
                amount DECIMAL(8,2),
                PRIMARY KEY (id),
                CONSTRAINT ck_amount CHECK (amount >= 0))
            """);
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ix_items_name ON items (name)");
        SqlExecutor.Execute(db, "INSERT INTO items (id, name, amount) VALUES (1, 'first', 1.25)");

        SqlExecutor.Execute(db, "ALTER TABLE items ADD COLUMN sequence INT AUTO_INCREMENT");
        SqlExecutor.Execute(db, "ALTER TABLE items ADD COLUMN version INT ROWVERSION");

        var schema = db.Tables.Catalog.TryGet("items")!;
        Assert.Equal((byte)8, schema.TryGetColumn("amount")!.DecimalPrecision);
        Assert.Equal((byte)2, schema.TryGetColumn("amount")!.DecimalScale);
        Assert.Equal("'base'", schema.TryGetColumn("name")!.DefaultExpressionSql);
        Assert.Single(schema.CheckConstraints);
        Assert.Single(schema.Indexes);
        Assert.Equal(new object?[] { 1L, 1L },
            Assert.Single(Select(db, "SELECT sequence, version FROM items").Rows));
    }

    [Fact]
    public void AlterPrimaryKey_OnEmptyTable_ReplacesKeyAndPersists()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE items (id INT, code STRING, PRIMARY KEY (id))");
            var statement = Assert.IsType<AlterTableAlterPrimaryKeyStatement>(
                SqlParser.Parse("ALTER TABLE items ALTER PRIMARY KEY (code)"));
            Assert.Equal(["code"], statement.Columns);
            SqlExecutor.Execute(db, "ALTER TABLE items ADD CONSTRAINT pk_items PRIMARY KEY (code)");
            SqlExecutor.Execute(db, "INSERT INTO items (id, code) VALUES (1, 'one'), (2, 'two')");
            var duplicate = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(
                db, "INSERT INTO items (id, code) VALUES (3, 'one')"));
            Assert.Equal(TableConstraintException.UniqueViolation, duplicate.ErrorCode);
        }

        using var reopened = Open();
        Assert.Equal(["code"], reopened.Tables.Catalog.TryGet("items")!.PrimaryKey);
        Assert.Equal(2, Select(reopened, "SELECT id FROM items").Rows.Count);
    }

    [Fact]
    public void AlterPrimaryKey_WithRowsOrForeignKey_RejectsWithoutSchemaChange()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE parent (id INT, code STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE child (id INT, parent_id INT, PRIMARY KEY (id), FOREIGN KEY (parent_id) REFERENCES parent (id))");
        var referenced = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(
            db, "ALTER TABLE parent ALTER PRIMARY KEY (code)"));
        Assert.Equal(TableConstraintException.SchemaEvolutionUnsupported, referenced.ErrorCode);
        Assert.Equal(["id"], db.Tables.Catalog.TryGet("parent")!.PrimaryKey);

        SqlExecutor.Execute(db, "CREATE TABLE isolated (id INT, code STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO isolated (id, code) VALUES (1, 'one')");
        var populated = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(
            db, "ALTER TABLE isolated ALTER PRIMARY KEY (code)"));
        Assert.Equal(TableConstraintException.SchemaEvolutionUnsupported, populated.ErrorCode);
        Assert.Equal(["id"], db.Tables.Catalog.TryGet("isolated")!.PrimaryKey);
    }

    [Fact]
    public void AlterPrimaryKey_WithCompositeKey_UpdatesEmptyTableAndRejectsCustomName()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE items (tenant INT, code STRING, id INT, PRIMARY KEY (id))");
        var customName = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(
            db, "ALTER TABLE items ADD CONSTRAINT custom_pk PRIMARY KEY (tenant, code)"));
        Assert.Equal(TableConstraintException.SchemaEvolutionUnsupported, customName.ErrorCode);

        SqlExecutor.Execute(db, "ALTER TABLE items ADD CONSTRAINT pk_items PRIMARY KEY (tenant, code)");
        Assert.Equal(["tenant", "code"], db.Tables.Catalog.TryGet("items")!.PrimaryKey);
        SqlExecutor.Execute(db, "INSERT INTO items (tenant, code, id) VALUES (1, 'x', 1)");
        var duplicate = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(
            db, "INSERT INTO items (tenant, code, id) VALUES (1, 'x', 2)"));
        Assert.Equal(TableConstraintException.UniqueViolation, duplicate.ErrorCode);
    }

    [Fact]
    public void CreateTable_WithoutPrimaryKey_EvolvesThroughGeneratedColumnsAndPrimaryKey()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE devices (code STRING NOT NULL)");
            Assert.Empty(db.Tables.Catalog.TryGet("devices")!.PrimaryKey);
            var error = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(
                db, "INSERT INTO devices (code) VALUES ('blocked')"));
            Assert.Equal(TableConstraintException.SchemaEvolutionUnsupported, error.ErrorCode);
            Assert.Equal(error.ErrorCode, SqlErrorMapper.Map(error, "insert").Code);
            Assert.Empty(Select(db, "SELECT code FROM devices").Rows);
        }

        using (var db = Open())
        {
            SqlExecutor.Execute(db, "ALTER TABLE devices ADD COLUMN id INT AUTO_INCREMENT");
            var error = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(
                db, "INSERT INTO devices (code) VALUES ('blocked')"));
            Assert.Equal(TableConstraintException.SchemaEvolutionUnsupported, error.ErrorCode);
            SqlExecutor.Execute(db, "ALTER TABLE devices ADD CONSTRAINT pk_devices PRIMARY KEY (id)");
            SqlExecutor.Execute(db, "ALTER TABLE devices ADD COLUMN version INT ROWVERSION");
            SqlExecutor.Execute(db, "INSERT INTO devices (code) VALUES ('first'), ('second')");
            Assert.Equal([1L, 2L], Select(db, "SELECT id FROM devices ORDER BY id")
                .Rows.Select(static row => (long)row[0]!).ToArray());
            SqlExecutor.Execute(db, "UPDATE devices SET code = 'updated' WHERE id = 1 AND version = 1");
        }

        using var reopened = Open();
        var schema = reopened.Tables.Catalog.TryGet("devices")!;
        Assert.Equal(["id"], schema.PrimaryKey);
        Assert.True(schema.TryGetColumn("id")!.IsAutoIncrement);
        Assert.True(schema.TryGetColumn("version")!.IsRowVersion);
        Assert.Equal([2L, 1L], Select(reopened, "SELECT version FROM devices ORDER BY id")
            .Rows.Select(static row => (long)row[0]!).ToArray());
        SqlExecutor.Execute(reopened, "INSERT INTO devices (code) VALUES ('third')");
        Assert.Equal(3L, Assert.Single(Select(reopened, "SELECT id FROM devices WHERE code = 'third'").Rows)[0]);
    }

    [Fact]
    public void AlterPrimaryKey_OnKeylessTable_RollsBackFailedChangeAndPreservesConstraints()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE draft (
                code STRING NOT NULL,
                name STRING DEFAULT 'base',
                CONSTRAINT ck_name CHECK (name <> 'bad'))
            """);
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ix_draft_name ON draft (name)");

        Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(
            db, "ALTER TABLE draft ALTER PRIMARY KEY (missing)"));
        Assert.Empty(db.Tables.Catalog.TryGet("draft")!.PrimaryKey);
        Assert.Single(db.Tables.Catalog.TryGet("draft")!.Indexes);
        Assert.Single(db.Tables.Catalog.TryGet("draft")!.CheckConstraints);

        SqlExecutor.Execute(db, "ALTER TABLE draft ALTER PRIMARY KEY (code)");
        SqlExecutor.Execute(db, "INSERT INTO draft (code) VALUES ('one')");
        Assert.Equal("base", Assert.Single(Select(db, "SELECT name FROM draft").Rows)[0]);
        var check = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(
            db, "INSERT INTO draft (code, name) VALUES ('two', 'bad')"));
        Assert.Equal(TableConstraintException.CheckViolation, check.ErrorCode);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
