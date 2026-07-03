using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlExecutorTableTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorTableTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-table-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void ParseCreateTable_WithPrimaryKey_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateTableStatement>(SqlParser.Parse(
            "CREATE TABLE devices (id INT NOT NULL, name STRING, enabled BOOL, PRIMARY KEY (id))"));

        Assert.Equal("devices", stmt.Name);
        Assert.Equal(3, stmt.Columns.Count);
        Assert.Equal(SqlDataType.Int64, stmt.Columns[0].DataType);
        Assert.Equal(ColumnNullability.NotNull, stmt.Columns[0].Nullability);
        Assert.Equal(["id"], stmt.PrimaryKey);
    }

    [Fact]
    public void ParseCreateTable_WithForeignKeyAndRowVersion_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateTableStatement>(SqlParser.Parse(
            "CREATE TABLE devices (id INT, site_id INT, version INT ROWVERSION, PRIMARY KEY (id), FOREIGN KEY (site_id) REFERENCES sites (id))"));

        Assert.True(stmt.Columns.Single(c => c.Name == "version").IsRowVersion);
        var foreignKey = Assert.Single(stmt.ForeignKeyClauses);
        Assert.Equal(["site_id"], foreignKey.Columns);
        Assert.Equal("sites", foreignKey.PrincipalTable);
        Assert.Equal(["id"], foreignKey.PrincipalColumns);
    }

    [Fact]
    public void ParseCreateIndex_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateTableIndexStatement>(SqlParser.Parse(
            "CREATE UNIQUE INDEX ux_devices_serial ON devices (tenant, serial)"));

        Assert.Equal("ux_devices_serial", stmt.IndexName);
        Assert.Equal("devices", stmt.TableName);
        Assert.True(stmt.IsUnique);
        Assert.Equal(["tenant", "serial"], stmt.Columns);
    }

    [Fact]
    public void ParseCreateJsonIndex_OnTable_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateTableJsonPathIndexStatement>(SqlParser.Parse(
            "CREATE JSON INDEX idx_devices_site ON devices (metadata, '$.site')"));

        Assert.Equal("idx_devices_site", stmt.IndexName);
        Assert.Equal("devices", stmt.TableName);
        Assert.Equal("metadata", stmt.JsonColumnName);
        Assert.Equal("$.site", stmt.Path);
    }

    [Fact]
    public void ParseAlterTableAddDropRename_ReturnsAst()
    {
        var add = Assert.IsType<AlterTableAddColumnStatement>(SqlParser.Parse(
            "ALTER TABLE devices ADD COLUMN site STRING NOT NULL DEFAULT 'north'"));
        Assert.Equal("devices", add.TableName);
        Assert.Equal("site", add.ColumnName);
        Assert.Equal(SqlDataType.String, add.DataType);
        Assert.Equal(ColumnNullability.NotNull, add.Nullability);
        Assert.IsType<LiteralExpression>(add.DefaultExpression);

        var drop = Assert.IsType<AlterTableDropColumnStatement>(SqlParser.Parse(
            "ALTER TABLE devices DROP COLUMN site"));
        Assert.Equal("site", drop.ColumnName);

        var renameColumn = Assert.IsType<AlterTableRenameColumnStatement>(SqlParser.Parse(
            "ALTER TABLE devices RENAME COLUMN name TO display_name"));
        Assert.Equal("name", renameColumn.OldColumnName);
        Assert.Equal("display_name", renameColumn.NewColumnName);

        var renameTable = Assert.IsType<AlterTableRenameTableStatement>(SqlParser.Parse(
            "ALTER TABLE devices RENAME TO assets"));
        Assert.Equal("devices", renameTable.OldTableName);
        Assert.Equal("assets", renameTable.NewTableName);
    }

    [Fact]
    public void CreateShowDescribeTable_PersistsAcrossReopen()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db,
                "CREATE TABLE devices (id INT, name STRING NOT NULL, metadata JSON NULL, PRIMARY KEY (id))");
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var show = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, "SHOW TABLES"));
            Assert.Equal(new[] { "name" }, show.Columns);
            Assert.Equal("devices", show.Rows.Single()[0]);

            var describe = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "DESCRIBE TABLE devices"));
            Assert.Equal(new[] { "column_name", "data_type", "is_nullable", "is_primary_key", "ordinal" }, describe.Columns);
            Assert.Equal(new object?[] { "id", "int64", false, true, 0L }, describe.Rows[0]);
            Assert.Equal(new object?[] { "metadata", "json", true, false, 2L }, describe.Rows[2]);
        }
    }

    [Fact]
    public void InsertSelectUpdateDelete_TableRows_WorkEndToEnd()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE devices (id INT, name STRING NOT NULL, enabled BOOL, temp FLOAT NULL, PRIMARY KEY (id))");

        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name, enabled, temp) VALUES (1, 'pump', TRUE, 12.5), (2, 'fan', FALSE, NULL)"));
        Assert.Equal(2, inserted.RowsInserted);

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, name, enabled, temp FROM devices WHERE id = 1"));
        Assert.Equal(new[] { "id", "name", "enabled", "temp" }, selected.Columns);
        Assert.Equal(new object?[] { 1L, "pump", true, 12.5 }, selected.Rows.Single());

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db,
            "UPDATE devices SET name = 'pump-2', temp = 13.25 WHERE id = 1"));
        Assert.Equal(1, updated.RowsAffected);

        var afterUpdate = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT name, temp FROM devices WHERE id = 1"));
        Assert.Equal(new object?[] { "pump-2", 13.25 }, afterUpdate.Rows.Single());

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db,
            "DELETE FROM devices WHERE id = 2"));
        Assert.Equal(1, deleted.TombstonesAdded);

        var all = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices ORDER BY id"));
        Assert.Equal(1L, all.Rows.Single()[0]);
    }

    [Fact]
    public void Insert_DuplicatePrimaryKey_Throws()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'a')");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'b')"));
    }

    [Fact]
    public void Insert_MissingNotNullColumn_Throws()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING NOT NULL, PRIMARY KEY (id))");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO devices (id) VALUES (1)"));
    }

    [Fact]
    public void Select_WithNonPrimaryKeyPredicate_ScansAndFilters()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name, enabled) VALUES (1, 'a', TRUE), (2, 'b', FALSE), (3, 'c', TRUE)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE enabled = TRUE AND id > 1 ORDER BY id DESC"));

        Assert.Equal([3L], result.Rows.Select(r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void Select_WithNumericConstantWhere_EvaluatesAsBoolean()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name) VALUES (1, 'pump'), (2, 'fan')");

        var falseResult = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE 0"));
        Assert.Empty(falseResult.Rows);

        var trueResult = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE 1 ORDER BY id"));
        Assert.Equal([1L, 2L], trueResult.Rows.Select(row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void Select_WithLowerFunction_FiltersRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name) VALUES (1, 'Pump'), (2, 'fan')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE LOWER(name) = 'pump'"));

        Assert.Equal(1L, result.Rows.Single()[0]);
    }

    [Fact]
    public void Select_OrderByMultipleColumns_SortsRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, tenant STRING, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            INSERT INTO devices (id, tenant, name)
            VALUES (1, 'north', 'pump'),
                   (2, 'south', 'fan'),
                   (3, 'north', 'valve'),
                   (4, 'south', 'meter')
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, tenant FROM devices ORDER BY tenant ASC, id DESC"));

        Assert.Equal([3L, 1L, 4L, 2L], result.Rows.Select(row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void Select_EfStyleIsNotNullPredicate_ReturnsRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, """
            CREATE TABLE Produces (
                Id STRING,
                ProduceToken STRING NULL,
                DefaultIdentityType INT,
                Deleted BOOL,
                PRIMARY KEY (Id))
            """);
        SqlExecutor.Execute(db, """
            INSERT INTO Produces (Id, ProduceToken, DefaultIdentityType, Deleted)
            VALUES ('p1', 'token-1', 1, FALSE),
                   ('p2', NULL, 1, FALSE),
                   ('p3', 'token-3', 1, TRUE)
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT "p"."Id", "p"."ProduceToken", "p"."DefaultIdentityType"
            FROM "Produces" AS "p"
            WHERE NOT ("p"."Deleted") AND "p"."ProduceToken" IS NOT NULL
            """));

        Assert.Single(result.Rows);
        Assert.Equal(new object?[] { "p1", "token-1", 1L }, result.Rows[0]);
    }

    [Fact]
    public void Select_EfStyleInSubqueryWithLeftJoin_ReturnsRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE Tenant (Id STRING, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "CREATE TABLE Device (Id STRING, TenantId STRING, Deleted BOOL, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "CREATE TABLE DeviceIdentities (Id STRING, DeviceId STRING, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "INSERT INTO Tenant (Id) VALUES ('tenant-1'), ('tenant-2')");
        SqlExecutor.Execute(db, """
            INSERT INTO Device (Id, TenantId, Deleted)
            VALUES ('dev-1', 'tenant-1', FALSE),
                   ('dev-2', 'tenant-1', TRUE),
                   ('dev-3', 'tenant-2', FALSE)
            """);
        SqlExecutor.Execute(db, "INSERT INTO DeviceIdentities (Id, DeviceId) VALUES ('i1', 'dev-1'), ('i2', 'dev-3')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT COUNT(*)
            FROM "Device" AS "d"
            LEFT JOIN "Tenant" AS "t" ON "d"."TenantId" = "t"."Id"
            WHERE "t"."Id" = 'tenant-1' AND NOT ("d"."Deleted") AND "d"."Id" IN (
                SELECT "d0"."DeviceId"
                FROM "DeviceIdentities" AS "d0")
            """));

        Assert.Equal(1L, result.Rows.Single()[0]);
    }

    [Fact]
    public void Select_WithLikePredicate_MatchesSqlPatterns()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name) VALUES (1, 'pump-001'), (2, 'pump-002'), (3, 'fan-001'), (4, 'p_mp-003')");

        var startsWith = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE name LIKE 'pump%' ORDER BY id"));
        Assert.Equal([1L, 2L], startsWith.Rows.Select(r => (long)r[0]!).ToArray());

        var endsWith = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE name LIKE '%001' ORDER BY id"));
        Assert.Equal([1L, 3L], endsWith.Rows.Select(r => (long)r[0]!).ToArray());

        var contains = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE name LIKE '%ump-0%' ORDER BY id"));
        Assert.Equal([1L, 2L], contains.Rows.Select(r => (long)r[0]!).ToArray());

        var singleCharacterWildcard = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE name LIKE 'p_mp%' ORDER BY id"));
        Assert.Equal([1L, 2L, 4L], singleCharacterWildcard.Rows.Select(r => (long)r[0]!).ToArray());

        var escapedWildcard = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE name LIKE 'p\\_mp%' ORDER BY id"));
        Assert.Equal([4L], escapedWildcard.Rows.Select(r => (long)r[0]!).ToArray());

        var notLike = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE name NOT LIKE 'pump%' ORDER BY id"));
        Assert.Equal([3L, 4L], notLike.Rows.Select(r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void Select_TableJoinAcrossThreeTables_ReturnsQualifiedRows()
    {
        using var db = Tsdb.Open(Options());
        CreateJoinFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT d.name AS device, s.name AS site, o.name AS owner
            FROM devices d
            JOIN sites s ON d.site_id = s.id
            JOIN owners o ON s.owner_id = o.id
            WHERE o.name = 'ops'
            ORDER BY device
            """));

        Assert.Equal(["device", "site", "owner"], result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { "boiler", "north", "ops" }, result.Rows[0]);
        Assert.Equal(new object?[] { "pump", "north", "ops" }, result.Rows[1]);
    }

    [Fact]
    public void Select_TableJoinWithSubquerySource_ReturnsRows()
    {
        using var db = Tsdb.Open(Options());
        CreateJoinFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT active.name AS device, s.name AS site
            FROM (SELECT id, name, site_id FROM devices WHERE enabled = TRUE) active
            JOIN sites s ON active.site_id = s.id
            ORDER BY device
            """));

        Assert.Equal(["device", "site"], result.Columns);
        Assert.Equal([new object?[] { "boiler", "north" }, new object?[] { "pump", "north" }], result.Rows);
    }

    [Fact]
    public void Select_TableScalarSubqueryInWhere_ReturnsRows()
    {
        using var db = Tsdb.Open(Options());
        CreateJoinFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT name FROM devices WHERE site_id = (SELECT id FROM sites WHERE name = 'south')"));

        Assert.Equal(["name"], result.Columns);
        Assert.Equal(new object?[] { "fan" }, result.Rows.Single());
    }

    [Fact]
    public void Select_TableGroupByAggregate_ReturnsBuckets()
    {
        using var db = Tsdb.Open(Options());
        CreateJoinFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT s.name AS site, count(*) AS device_count, avg(d.temp) AS avg_temp
            FROM devices d
            JOIN sites s ON d.site_id = s.id
            GROUP BY s.name
            ORDER BY site
            """));

        Assert.Equal(["site", "device_count", "avg_temp"], result.Columns);
        Assert.Equal(new object?[] { "north", 2L, 15.25 }, result.Rows[0]);
        Assert.Equal(new object?[] { "south", 1L, 20.0 }, result.Rows[1]);
    }

    [Fact]
    public void Select_TableGroupByHaving_FiltersGroupsByAggregate()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_sales (id INT, region STRING, amount INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_sales (id, region, amount) VALUES "
            + "(1, 'north', 70), (2, 'north', 50), (3, 'south', 20), (4, 'west', 200)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT region, count(*) AS order_count, sum(amount) AS total_amount
            FROM rel_sales
            GROUP BY region
            HAVING sum(amount) >= 100
            ORDER BY region
            """));

        Assert.Equal(["region", "order_count", "total_amount"], result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { "north", 2L, 120L }, result.Rows[0]);
        Assert.Equal(new object?[] { "west", 1L, 200L }, result.Rows[1]);
    }

    [Fact]
    public void Select_FullTextFuzzySearch_FindsTypoedQuery()
    {
        // Parity #133 TypoTolerantQueryScenario 同构：查询 'pmp alrm' 在 fuzzy 模式下
        // 应展开到 'pump' / 'alarm' 的编辑距离邻域并命中 typo-1。
        using var db = Tsdb.Open(Options());
        const string idx = "rel_typo_idx";
        SqlExecutor.Execute(db, $"CREATE DOCUMENT COLLECTION {idx}");
        SqlExecutor.Execute(db,
            $"CREATE FULLTEXT INDEX ft_{idx} ON {idx} ('$.title', '$.body', '$.category', '$.tags') USING unicode");
        SqlExecutor.Execute(db,
            $"INSERT INTO {idx} (id, document) VALUES "
            + "('typo-1', '{\"title\":\"pump alarm\",\"body\":\"pump alarm pressure station north\",\"category\":\"pump\",\"tags\":[\"north\"]}'),"
            + "('typo-2', '{\"title\":\"fan normal\",\"body\":\"fan airflow normal station south\",\"category\":\"fan\",\"tags\":[\"south\"]}')");

        // 精确模式应 0 命中（'pmp alrm' 在索引里都不存在）。
        var exact = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            $"SELECT id FROM {idx} WHERE match(ft_{idx}, *, 'pmp alrm', 5)"));
        Assert.Empty(exact.Rows);

        // fuzzy 模式应命中 typo-1。
        var fuzzy = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            $"SELECT id FROM {idx} WHERE match(ft_{idx}, *, 'pmp alrm', 5, 'fuzzy')"));
        Assert.Contains(fuzzy.Rows, row => Equals(row[0], "typo-1"));
    }

    [Fact]
    public void Select_FullTextCjkSearch_FindsExpectedChineseDocument()
    {
        // 与 parity #133 CjkTokenizeCorrectnessScenario 同构：CJK bigram 索引 + AND-of-tokens
        // 查询 "水泵 报警" 应只命中包含两个 bigram 的 cjk-1 文档。
        using var db = Tsdb.Open(Options());
        const string idx = "rel_cjk_idx";
        SqlExecutor.Execute(db, $"CREATE DOCUMENT COLLECTION {idx}");
        SqlExecutor.Execute(db,
            $"CREATE FULLTEXT INDEX ft_{idx} ON {idx} ('$.title', '$.body', '$.category', '$.tags') USING cjk");
        SqlExecutor.Execute(db,
            $"INSERT INTO {idx} (id, document) VALUES "
            + "('cjk-1', '{\"title\":\"水泵报警\",\"body\":\"北站水泵压力报警需要检修\",\"category\":\"pump\",\"tags\":[\"north\",\"critical\"]}'),"
            + "('cjk-2', '{\"title\":\"风机正常\",\"body\":\"南站风机运行正常没有报警\",\"category\":\"fan\",\"tags\":[\"south\",\"info\"]}'),"
            + "('cjk-3', '{\"title\":\"水泵维护\",\"body\":\"东站水泵震动升高安排维护\",\"category\":\"pump\",\"tags\":[\"east\",\"warning\"]}')");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            $"SELECT id FROM {idx} WHERE match(ft_{idx}, *, '水泵 报警', 10)"));

        Assert.Single(r.Rows);
        Assert.Equal("cjk-1", r.Rows[0][0]);
    }

    [Fact]
    public void Delete_Parent_WithOnDeleteCascade_RemovesReferencingChildren()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE customers (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE orders (id INT, customer_id INT, total FLOAT, PRIMARY KEY (id), "
            + "FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE)");
        SqlExecutor.Execute(db,
            "INSERT INTO customers (id, name) VALUES (1, 'alice'), (2, 'bob')");
        SqlExecutor.Execute(db,
            "INSERT INTO orders (id, customer_id, total) VALUES (10, 1, 12.5), (11, 1, 18.0), (20, 2, 7.0)");

        SqlExecutor.Execute(db, "DELETE FROM customers WHERE id = 1");

        var orders = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, customer_id FROM orders ORDER BY id"));

        Assert.Single(orders.Rows);
        Assert.Equal(20L, orders.Rows[0][0]);
        Assert.Equal(2L, orders.Rows[0][1]);
    }

    [Fact]
    public void Delete_Parent_WithoutCascade_StillThrowsOnReferencingChild()
    {
        // 回归：默认 NoAction 行为不变，仍然拒绝删除有引用的父行。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE customers (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE orders (id INT, customer_id INT, PRIMARY KEY (id), "
            + "FOREIGN KEY (customer_id) REFERENCES customers (id))");
        SqlExecutor.Execute(db, "INSERT INTO customers (id, name) VALUES (1, 'alice')");
        SqlExecutor.Execute(db, "INSERT INTO orders (id, customer_id) VALUES (10, 1)");

        Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "DELETE FROM customers WHERE id = 1"));
    }

    [Fact]
    public void Delete_Parent_WithCascade_PropagatesToGrandchildren()
    {
        // 多级级联：A → B (CASCADE) → C (CASCADE)；删 A 应一次性清掉 B、C。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE a (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE b (id INT, a_id INT, PRIMARY KEY (id), "
            + "FOREIGN KEY (a_id) REFERENCES a (id) ON DELETE CASCADE)");
        SqlExecutor.Execute(db,
            "CREATE TABLE c (id INT, b_id INT, PRIMARY KEY (id), "
            + "FOREIGN KEY (b_id) REFERENCES b (id) ON DELETE CASCADE)");
        SqlExecutor.Execute(db, "INSERT INTO a (id) VALUES (1), (2)");
        SqlExecutor.Execute(db, "INSERT INTO b (id, a_id) VALUES (10, 1), (11, 1), (20, 2)");
        SqlExecutor.Execute(db, "INSERT INTO c (id, b_id) VALUES (100, 10), (101, 11), (200, 20)");

        SqlExecutor.Execute(db, "DELETE FROM a WHERE id = 1");

        var b = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM b ORDER BY id"));
        var c = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM c ORDER BY id"));
        Assert.Single(b.Rows);
        Assert.Equal(20L, b.Rows[0][0]);
        Assert.Single(c.Rows);
        Assert.Equal(200L, c.Rows[0][0]);
    }

    [Fact]
    public void Delete_Parent_WithCascade_PersistsAcrossReopen()
    {
        // 回归：FK ON DELETE 动作必须写入 schema 文件，重新打开 DB 后仍生效。
        var opts = Options();
        using (var db = Tsdb.Open(opts))
        {
            SqlExecutor.Execute(db, "CREATE TABLE p (id INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(db,
                "CREATE TABLE c (id INT, p_id INT, PRIMARY KEY (id), "
                + "FOREIGN KEY (p_id) REFERENCES p (id) ON DELETE CASCADE)");
            SqlExecutor.Execute(db, "INSERT INTO p (id) VALUES (1)");
            SqlExecutor.Execute(db, "INSERT INTO c (id, p_id) VALUES (10, 1), (11, 1)");
        }
        using (var db = Tsdb.Open(opts))
        {
            SqlExecutor.Execute(db, "DELETE FROM p WHERE id = 1");
            var c = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM c"));
            Assert.Empty(c.Rows);
        }
    }

    [Fact]
    public void Select_TableGroupBy_MinMaxResultTypes_ConsistentAcrossGroups()
    {
        // M3 回归：MIN/MAX 的返回类型应在整个结果集上一致。
        // 旧实现按 *每组* 看输入：纯整数组返 long，混类型组返 double，
        // 同一查询的不同行得到 long / double 异质类型——会让上层 ORDER BY、
        // 跨后端 parity diff 失败。新实现按整个结果集做一次性判定。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_mix (id INT, g STRING, v FLOAT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_mix (id, g, v) VALUES "
            + "(1, 'a', 1.5), (2, 'a', 2.5), "    // 组 a：含 double
            + "(3, 'b', 10.0), (4, 'b', 20.0)");  // 组 b：全 double，但同列

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT g, max(v) FROM rel_mix GROUP BY g ORDER BY g"));

        Assert.Equal(2, r.Rows.Count);
        // 两行 max(v) 应当类型相同——这里都是 double。
        Assert.IsType<double>(r.Rows[0][1]);
        Assert.IsType<double>(r.Rows[1][1]);
    }

    [Fact]
    public void Select_TableGroupBy_MinMaxAllIntegral_StaysLongAcrossGroups()
    {
        // 对偶回归：全表全 int 时所有组都返 long。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_ints (id INT, g STRING, v INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_ints (id, g, v) VALUES (1, 'a', 5), (2, 'a', 7), (3, 'b', 10)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT g, max(v), sum(v) FROM rel_ints GROUP BY g ORDER BY g"));

        Assert.Equal(2, r.Rows.Count);
        Assert.IsType<long>(r.Rows[0][1]);
        Assert.IsType<long>(r.Rows[1][1]);
        Assert.IsType<long>(r.Rows[0][2]);
        Assert.IsType<long>(r.Rows[1][2]);
    }

    [Fact]
    public void Select_TableGroupBy_SumLongsOverflow_PromotesToDouble()
    {
        // M4 回归：long 累加溢出应自动提升为 double，不再抛 OverflowException。
        // long.MaxValue + 1 会溢出，旧实现 longs.Sum() 抛 checked OverflowException。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_big (id INT, v INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            $"INSERT INTO rel_big (id, v) VALUES (1, {long.MaxValue}), (2, 1)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT sum(v) FROM rel_big"));

        Assert.Single(r.Rows);
        // 升级为 double，值约等于 long.MaxValue + 1。
        var v = Assert.IsType<double>(r.Rows[0][0]);
        Assert.True(v > 9.0e18, $"溢出后应升级为 ≈ long.MaxValue+1，实际 {v}");
    }

    [Fact]
    public void Select_TableJoinOnReferencesOuterColumn_ResolvesViaOuterScope()
    {
        // M2 回归：相关子查询里 JOIN ON 引用外层列时，
        // 旧实现的 Join() 不传 outerScope —— ON 条件解析"外层标识符"会抛"未知列"。
        // 这里构造一个子查询：从 orders JOIN line_items，ON 引用外层 customers.id。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE customers (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE orders (id INT, customer_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE line_items (id INT, order_id INT, qty INT, PRIMARY KEY (id))");

        SqlExecutor.Execute(db,
            "INSERT INTO customers (id, name) VALUES (1, 'alice'), (2, 'bob')");
        SqlExecutor.Execute(db,
            "INSERT INTO orders (id, customer_id) VALUES (10, 1), (20, 2)");
        SqlExecutor.Execute(db,
            "INSERT INTO line_items (id, order_id, qty) VALUES (100, 10, 5), (200, 20, 1)");

        // 子查询 JOIN ON 用了 c.id —— 来自外层 customers 别名 c。
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT c.name
            FROM customers c
            WHERE EXISTS (
                SELECT 1
                FROM orders o
                JOIN line_items l ON l.order_id = o.id AND o.customer_id = c.id
                WHERE l.qty >= 3
            )
            """));

        Assert.Single(result.Rows);
        Assert.Equal("alice", result.Rows[0][0]);
    }

    [Fact]
    public void Select_TableExistsCorrelatedSubquery_FiltersByOuterColumn()
    {
        // ROADMAP #129 后续：EXISTS 中引用外层表列（o.customer_id = c.id）应过滤出
        // "至少有一笔满足条件订单的客户"。与 Postgres 同语义。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_customers (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_orders (id INT, customer_id INT, total INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_customers (id, name) VALUES (1, 'alice'), (2, 'bob'), (3, 'cora')");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_orders (id, customer_id, total) VALUES (10, 1, 50), (20, 1, 200), (30, 2, 80)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT c.name
            FROM rel_customers c
            WHERE EXISTS (
                SELECT 1
                FROM rel_orders o
                WHERE o.customer_id = c.id AND o.total >= 100
            )
            ORDER BY c.name
            """));

        Assert.Single(result.Rows);
        Assert.Equal("alice", result.Rows[0][0]);
    }

    [Fact]
    public void Select_TableExistsNonCorrelated_StillWorks()
    {
        // 回归：非相关 EXISTS 在加了 outer scope 链后仍按整张表存在性判定。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_customers (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_orders (id INT, total INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_customers (id, name) VALUES (1, 'alice'), (2, 'bob')");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_orders (id, total) VALUES (10, 200)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT name FROM rel_customers WHERE EXISTS (SELECT 1 FROM rel_orders WHERE total >= 100) ORDER BY name"));

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Select_TableHavingWithWrappedAggregate_EvaluatesAggregateInline()
    {
        // 回归：HAVING 里出现在算术或外层标量函数中的聚合也必须可以求值——
        // 旧实现只识别顶层裸聚合（HAVING sum(x) >= 100），
        // HAVING sum(x)+1 > 10 / HAVING abs(sum(x))*2 > 5 都会抛"聚合函数只能出现在投影中"。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_sales (id INT, region STRING, amount INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_sales (id, region, amount) VALUES "
            + "(1, 'a', 50), (2, 'a', 50), (3, 'b', 200)");

        // sum(amount)+1 → a:101, b:201；筛 > 150 → 仅 b。
        var arith = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT region FROM rel_sales GROUP BY region HAVING sum(amount) + 1 > 150 ORDER BY region
            """));
        Assert.Single(arith.Rows);
        Assert.Equal("b", arith.Rows[0][0]);

        // sum(amount) * 2 > sum(amount) → 两组都成立（正值），但 sum(amount)*2 > 350 → 仅 b。
        var twoSides = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT region FROM rel_sales GROUP BY region HAVING sum(amount) * 2 > 350 ORDER BY region
            """));
        Assert.Single(twoSides.Rows);
        Assert.Equal("b", twoSides.Rows[0][0]);
    }

    [Fact]
    public void Select_TableHavingWithAndOr_CombinedPredicates()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE rel_sales (id INT, region STRING, amount INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_sales (id, region, amount) VALUES "
            + "(1, 'a', 10), (2, 'a', 90), (3, 'b', 50), (4, 'c', 200), (5, 'c', 10)");

        // 'a' → sum=100 count=2 (满足); 'b' → sum=50 count=1 (sum 不够); 'c' → sum=210 count=2 (满足)
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT region, sum(amount) AS total
            FROM rel_sales
            GROUP BY region
            HAVING sum(amount) >= 100 AND count(*) >= 2
            ORDER BY region
            """));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { "a", 100L }, result.Rows[0]);
        Assert.Equal(new object?[] { "c", 210L }, result.Rows[1]);

        // 严格版：要求 sum >= 120 → 仅 'c'
        var strict = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            """
            SELECT region, sum(amount) AS total
            FROM rel_sales
            GROUP BY region
            HAVING sum(amount) >= 120 AND count(*) >= 2
            ORDER BY region
            """));

        Assert.Single(strict.Rows);
        Assert.Equal(new object?[] { "c", 210L }, strict.Rows[0]);
    }

    [Fact]
    public void Delete_WithPrimaryKeyAndExtraPredicate_RespectsAllPredicates()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name, enabled) VALUES (1, 'pump', TRUE)");

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db,
            "DELETE FROM devices WHERE id = 1 AND enabled = FALSE"));
        Assert.Equal(0, deleted.TombstonesAdded);

        var remaining = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE id = 1"));
        Assert.Single(remaining.Rows);
    }

    private static void CreateJoinFixture(Tsdb db)
    {
        SqlExecutor.Execute(db,
            "CREATE TABLE owners (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE sites (id INT, name STRING, owner_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE devices (id INT, name STRING, site_id INT, enabled BOOL, temp FLOAT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO owners (id, name) VALUES (1, 'ops'), (2, 'qa')");
        SqlExecutor.Execute(db,
            "INSERT INTO sites (id, name, owner_id) VALUES (10, 'north', 1), (20, 'south', 2)");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name, site_id, enabled, temp) VALUES (100, 'pump', 10, TRUE, 12.5), (101, 'fan', 20, FALSE, 20.0), (102, 'boiler', 10, TRUE, 18.0)");
    }

    [Fact]
    public void DropTable_RemovesSchemaAndRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'a')");

        var dropped = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "DROP TABLE devices"));
        Assert.Equal(1, dropped.RowsAffected);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SHOW TABLES")).Rows);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "SELECT * FROM devices"));
    }

    [Fact]
    public void DropTable_MissingTable_Throws()
    {
        using var db = Tsdb.Open(Options());
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "DROP TABLE ghost"));
    }

    [Fact]
    public void DropTableIfExists_MissingTable_NoOp()
    {
        using var db = Tsdb.Open(Options());
        var dropped = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "DROP TABLE IF EXISTS ghost"));
        Assert.Equal(0, dropped.RowsAffected);
    }

    [Fact]
    public void DropTableIfExists_ExistingTable_Drops()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");

        var dropped = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "DROP TABLE IF EXISTS devices"));
        Assert.Equal(1, dropped.RowsAffected);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SHOW TABLES")).Rows);
    }

    [Fact]
    public void AlterTable_AddDropRenameColumn_RewritesRowsAndPersists()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, enabled BOOL, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "INSERT INTO devices (id, name, enabled) VALUES (1, 'pump', TRUE), (2, 'fan', FALSE)");

            SqlExecutor.Execute(db, "ALTER TABLE devices ADD COLUMN site STRING NOT NULL DEFAULT 'north'");
            var afterAdd = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT id, name, site FROM devices ORDER BY id"));
            Assert.Equal(new object?[] { 1L, "pump", "north" }, afterAdd.Rows[0]);
            Assert.Equal(new object?[] { 2L, "fan", "north" }, afterAdd.Rows[1]);

            SqlExecutor.Execute(db, "ALTER TABLE devices RENAME COLUMN name TO display_name");
            var afterRename = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT display_name, site FROM devices WHERE id = 1"));
            Assert.Equal(new object?[] { "pump", "north" }, afterRename.Rows.Single());

            SqlExecutor.Execute(db, "ALTER TABLE devices DROP COLUMN enabled");
            var describe = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "DESCRIBE TABLE devices"));
            Assert.Equal(["id", "display_name", "site"], describe.Rows.Select(static r => (string)r[0]!).ToArray());
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened,
                "SELECT id, display_name, site FROM devices ORDER BY id"));
            Assert.Equal(new object?[] { 1L, "pump", "north" }, result.Rows[0]);
            Assert.Equal(new object?[] { 2L, "fan", "north" }, result.Rows[1]);
        }
    }

    [Fact]
    public void AlterTable_RenameTable_MovesRowstoreAndPersists()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump')");
            SqlExecutor.Execute(db, "ALTER TABLE devices RENAME TO assets");

            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "SELECT id FROM devices"));
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id, name FROM assets"));
            Assert.Equal(new object?[] { 1L, "pump" }, result.Rows.Single());
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, "SELECT id, name FROM assets"));
            Assert.Equal(new object?[] { 1L, "pump" }, result.Rows.Single());
            Assert.DoesNotContain(
                Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, "SHOW TABLES")).Rows,
                row => string.Equals((string?)row[0], "devices", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AlterTable_RejectsPrimaryKeyAndIndexedColumnChanges()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, serial STRING, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_serial ON devices (serial)");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "ALTER TABLE devices DROP COLUMN id"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "ALTER TABLE devices RENAME COLUMN id TO device_id"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "ALTER TABLE devices DROP COLUMN serial"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "ALTER TABLE devices ADD COLUMN required STRING NOT NULL"));
    }

    [Fact]
    public void AlterTable_DropColumnIfExists_MissingColumnNoOps()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");

        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db,
            "ALTER TABLE devices DROP COLUMN IF EXISTS authorized_key_id"));

        Assert.Equal(0, result.RowsAffected);
        var schema = db.Tables.Catalog.TryGet("devices")!;
        Assert.NotNull(schema.TryGetColumn("name"));
    }

    [Fact]
    public void CreateIndex_PersistsAndSelectUsesIndex()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, tenant STRING, name STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "INSERT INTO devices (id, tenant, name) VALUES (1, 'north', 'pump'), (2, 'south', 'fan'), (3, 'north', 'meter')");
            SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");

            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT id, name FROM devices WHERE tenant = 'north' ORDER BY id"));
            Assert.Equal([1L, 3L], result.Rows.Select(static r => (long)r[0]!).ToArray());

            var indexes = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SHOW INDEXES ON devices"));
            Assert.Equal(new[] { "index_name", "is_unique", "columns", "created_utc" }, indexes.Columns);
            Assert.Equal("idx_devices_tenant", indexes.Rows.Single()[0]);

            var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "EXPLAIN SELECT id FROM devices WHERE tenant = 'north'"));
            var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
            Assert.Equal("secondary_index", values["access_path"]);
            Assert.Equal("idx_devices_tenant", values["index_name"]);
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened,
                "SELECT id FROM devices WHERE tenant = 'south'"));
            Assert.Equal(2L, result.Rows.Single()[0]);
        }
    }

    [Fact]
    public void CreateIndex_IfNotExists_IsIdempotent()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_name ON devices (name)");

        var result = Assert.IsType<TableIndex>(SqlExecutor.Execute(db,
            "CREATE INDEX IF NOT EXISTS idx_devices_name ON devices (name)"));

        Assert.Equal("idx_devices_name", result.Name);
        Assert.Equal(new[] { "name" }, result.Columns);
    }

    [Fact]
    public void AlterTable_DropConstraint_RemovesForeignKeyAndAllowsColumnDrop()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE AuthorizedKeys (Id STRING, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, """
            CREATE TABLE Device (
                Id STRING,
                AuthorizedKeyId STRING,
                PRIMARY KEY (Id),
                FOREIGN KEY (AuthorizedKeyId) REFERENCES AuthorizedKeys (Id)
            )
            """);

        SqlExecutor.Execute(db, "ALTER TABLE Device DROP CONSTRAINT FK_Device_AuthorizedKeys_AuthorizedKeyId");
        SqlExecutor.Execute(db, "ALTER TABLE Device DROP COLUMN AuthorizedKeyId");

        var schema = db.Tables.Catalog.TryGet("Device")!;
        Assert.Empty(schema.ForeignKeys);
        Assert.Null(schema.TryGetColumn("AuthorizedKeyId"));
    }

    [Fact]
    public void CreateJsonPathIndex_OnTable_PersistsAndSelectUsesIndex()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, metadata JSON, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, """
                INSERT INTO devices (id, metadata)
                VALUES (1, '{"site":"north","metrics":{"temp":21.5}}'),
                       (2, '{"site":"south","metrics":{"temp":18}}'),
                       (3, '{"site":"north","metrics":{"temp":20}}')
                """);
            SqlExecutor.Execute(db, "CREATE JSON INDEX idx_devices_site ON devices (metadata, '$.site')");

            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
                SELECT id, json_value(metadata, '$.metrics.temp') AS temp
                FROM devices
                WHERE json_value(metadata, '$.site') = 'north'
                ORDER BY id
                """));
            Assert.Equal([1L, 3L], result.Rows.Select(static r => (long)r[0]!).ToArray());

            var indexes = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SHOW INDEXES ON devices"));
            Assert.Equal("idx_devices_site", indexes.Rows.Single()[0]);
            Assert.Equal("metadata->$.site", indexes.Rows.Single()[2]);

            var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
                EXPLAIN SELECT id FROM devices WHERE json_value(metadata, '$.site') = 'north'
                """));
            var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
            Assert.Equal("json_path_index", values["access_path"]);
            Assert.Equal("idx_devices_site", values["index_name"]);
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, """
                SELECT id FROM devices WHERE json_value(metadata, '$.site') = 'south'
                """));
            Assert.Equal(2L, result.Rows.Single()[0]);
        }
    }

    [Fact]
    public void UniqueIndex_RejectsDuplicateAndLeavesRowsUnchanged()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, serial STRING, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_devices_serial ON devices (serial)");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, serial, name) VALUES (1, 'A-1', 'pump')");

        Assert.ThrowsAny<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO devices (id, serial, name) VALUES (2, 'A-1', 'fan')"));

        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, name FROM devices WHERE serial = 'A-1'"));
        Assert.Equal(new object?[] { 1L, "pump" }, rows.Rows.Single());
    }

    [Fact]
    public void UpdateAndDelete_MaintainSecondaryIndexes()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, tenant STRING, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, tenant, enabled) VALUES (1, 'north', TRUE), (2, 'south', TRUE)");

        SqlExecutor.Execute(db, "UPDATE devices SET tenant = 'south' WHERE id = 1");

        var north = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE tenant = 'north'"));
        Assert.Empty(north.Rows);

        var south = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE tenant = 'south' ORDER BY id"));
        Assert.Equal([1L, 2L], south.Rows.Select(static r => (long)r[0]!).ToArray());

        SqlExecutor.Execute(db, "DELETE FROM devices WHERE tenant = 'south' AND id = 1");
        var afterDelete = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE tenant = 'south' ORDER BY id"));
        Assert.Equal([2L], afterDelete.Rows.Select(static r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void MultipleIndexes_OnDifferentColumns_DoNotCrossContaminate()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, tenant STRING, site STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_site ON devices (site)");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, tenant, site) VALUES (1, 'north', 'a'), (2, 'south', 'north'), (3, 'north', 'b')");

        var tenant = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE tenant = 'north' ORDER BY id"));
        Assert.Equal([1L, 3L], tenant.Rows.Select(static r => (long)r[0]!).ToArray());

        var site = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE site = 'north' ORDER BY id"));
        Assert.Equal([2L], site.Rows.Select(static r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void Select_JoinMeasurementWithDimensionTable_ReturnsEnrichedRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT temperature (device_id TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id STRING, tenant STRING, name STRING, site STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, tenant, name, site) VALUES ('dev-1', 'tenant-1', 'Pump A', 'north'), ('dev-2', 'tenant-2', 'Fan B', 'south')");
        SqlExecutor.Execute(db,
            "INSERT INTO temperature (time, device_id, value) VALUES (1000, 'dev-1', 20.5), (2000, 'dev-2', 25.0), (3000, 'dev-1', 21.0)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT t.time, d.name, d.site, t.value
            FROM temperature AS t
            JOIN devices AS d ON t.device_id = d.id
            WHERE d.tenant = 'tenant-1' AND t.time >= 1000 AND t.time <= 3000
            ORDER BY t.time DESC
            """));

        Assert.Equal(new[] { "t.time", "d.name", "d.site", "t.value" }, result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { 3000L, "Pump A", "north", 21.0 }, result.Rows[0]);
        Assert.Equal(new object?[] { 1000L, "Pump A", "north", 20.5 }, result.Rows[1]);
    }

    [Fact]
    public void Select_JoinWithoutQualifiedAmbiguousColumn_Throws()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT temperature (device_id TAG, name TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id STRING, name STRING, PRIMARY KEY (id))");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            SELECT name
            FROM temperature t
            JOIN devices d ON t.device_id = d.id
            """));
    }

    [Fact]
    public void Select_JoinOnMeasurementField_Throws()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT temperature (device_id TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE thresholds (id INT, value FLOAT, PRIMARY KEY (id))");

        var ex = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            SELECT t.time, t.value
            FROM temperature t
            JOIN thresholds d ON t.value = d.value
            """));
        Assert.Contains("TAG", ex.Message);
    }

    [Fact]
    public void Select_Join_TableSideResidualPredicate_IsAppliedAfterIndexLookup()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT temperature (device_id TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id STRING, tenant STRING, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, tenant, enabled) VALUES ('dev-1', 'tenant-1', FALSE), ('dev-2', 'tenant-1', TRUE)");
        SqlExecutor.Execute(db,
            "INSERT INTO temperature (time, device_id, value) VALUES (1000, 'dev-1', 20.5), (2000, 'dev-2', 25.0)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT t.time, d.id
            FROM temperature t
            JOIN devices d ON t.device_id = d.id
            WHERE d.tenant = 'tenant-1' AND d.enabled = TRUE
            """));

        Assert.Single(result.Rows);
        Assert.Equal(new object?[] { 2000L, "dev-2" }, result.Rows[0]);
    }


    [Fact]
    public void ExecuteScript_CommitAndRollback_LightTransaction()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");

        var commitResults = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (1, 'pump');
            INSERT INTO devices (id, name) VALUES (2, 'fan');
            COMMIT;
            """);
        Assert.IsType<RowsAffectedExecutionResult>(commitResults[^1]);

        var committed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices ORDER BY id"));
        Assert.Equal([1L, 2L], committed.Rows.Select(static r => (long)r[0]!).ToArray());

        SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (3, 'meter');
            ROLLBACK;
            """);

        var afterRollback = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices ORDER BY id"));
        Assert.Equal([1L, 2L], afterRollback.Rows.Select(static r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void LightTransaction_MeasurementInsert_IsRejected()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");

        // measurement 写入无法被轻事务 ROLLBACK 撤销，必须在事务上下文内显式拒绝，
        // 而不是静默写入造成"ROLLBACK 后数据仍在"的假回滚。
        Assert.ThrowsAny<NotSupportedException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 1.0);
            COMMIT;
            """));

        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT * FROM cpu"));
        // 拒绝后不应有任何时序数据落库。
        Assert.Empty(rows.Rows);
    }

    [Fact]
    public void LightTransaction_MeasurementDelete_IsRejected()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 1.0)");

        Assert.ThrowsAny<NotSupportedException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            DELETE FROM cpu WHERE host = 'h1';
            COMMIT;
            """));

        // 被拒绝的删除不应生效。
        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT count(*) FROM cpu"));
        Assert.Equal(1L, rows.Rows.Single()[0]);
    }

    [Fact]
    public void LightTransaction_MeasurementInsertRejection_DoesNotCommitBufferedTableWrites()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");

        // 事务内先排队一条表写入，再触发被拒绝的 measurement 写入：整批不得提交。
        Assert.ThrowsAny<NotSupportedException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (1, 'pump');
            INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 1.0);
            COMMIT;
            """));

        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices")).Rows);
    }

    [Fact]
    public void ExecuteScript_CommitFailure_RollsBackBatch()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, serial STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_devices_serial ON devices (serial)");

        Assert.ThrowsAny<InvalidOperationException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, serial) VALUES (1, 'A-1');
            INSERT INTO devices (id, serial) VALUES (2, 'A-1');
            COMMIT;
            """));

        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices"));
        Assert.Empty(rows.Rows);
    }

    [Fact]
    public void ExecuteScript_CrossTableTransaction_CommitsAtomically()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE sites (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, site_id INT, name STRING, PRIMARY KEY (id), FOREIGN KEY (site_id) REFERENCES sites (id))");

        SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO sites (id, name) VALUES (1, 'north');
            INSERT INTO devices (id, site_id, name) VALUES (1, 1, 'pump');
            COMMIT;
            """);

        Assert.Equal(1L, Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM sites")).Rows.Single()[0]);
        Assert.Equal(1L, Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices")).Rows.Single()[0]);
    }

    [Fact]
    public void ExecuteScript_CrossTableConstraintFailure_RollsBackAllTables()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE sites (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, site_id INT, serial STRING, PRIMARY KEY (id), FOREIGN KEY (site_id) REFERENCES sites (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_devices_serial ON devices (serial)");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, site_id, serial) VALUES (1, NULL, 'A-1')");

        var ex = Assert.Throws<TableConstraintException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO sites (id, name) VALUES (1, 'north');
            INSERT INTO devices (id, site_id, serial) VALUES (2, 1, 'A-1');
            COMMIT;
            """));
        Assert.Equal(TableConstraintException.UniqueViolation, ex.ErrorCode);

        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM sites")).Rows);
        Assert.Equal([1L], Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices")).Rows.Select(static r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void ForeignKey_InsertMissingPrincipal_ReturnsStableErrorCode()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE sites (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, site_id INT, PRIMARY KEY (id), FOREIGN KEY (site_id) REFERENCES sites (id))");

        var ex = Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO devices (id, site_id) VALUES (1, 404)"));

        Assert.Equal(TableConstraintException.ForeignKeyViolation, ex.ErrorCode);
        Assert.Equal("devices", ex.TableName);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices")).Rows);
    }

    [Fact]
    public void RowVersion_UpdateIncrementsAndStalePredicateReturnsConflictCode()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, version INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump')");

        var inserted = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT version FROM devices WHERE id = 1"));
        Assert.Equal(1L, inserted.Rows.Single()[0]);

        SqlExecutor.Execute(db, "UPDATE devices SET name = 'pump-2' WHERE id = 1 AND version = 1");
        var updated = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT version FROM devices WHERE id = 1"));
        Assert.Equal(2L, updated.Rows.Single()[0]);

        var ex = Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "UPDATE devices SET name = 'pump-3' WHERE id = 1 AND version = 1"));
        Assert.Equal(TableConstraintException.ConcurrencyConflict, ex.ErrorCode);
    }

    private void SeedNullSemanticsTable(Tsdb db)
    {
        SqlExecutor.Execute(db, """
            CREATE TABLE t (
                id INT,
                v INT NULL,
                s STRING NULL,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, """
            INSERT INTO t (id, v, s)
            VALUES (1, 5, 'a'),
                   (2, NULL, NULL),
                   (3, 10, 'b')
            """);
    }

    [Fact]
    public void Where_NullEqualsLiteral_IsUnknown_ExcludesRow()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        // v = 5 matches only row 1; row 2 (v IS NULL) is UNKNOWN, not TRUE.
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE v = 5"));
        Assert.Equal([1L], result.Rows.Select(r => r[0]));
    }

    [Fact]
    public void Where_NullNotEqualsLiteral_IsUnknown_ExcludesRow()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        // v != 5 must NOT return row 2 (NULL). Old buggy behavior returned it.
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE v != 5"));
        Assert.Equal([3L], result.Rows.Select(r => r[0]));
    }

    [Fact]
    public void Where_NullEqualsNull_IsUnknown_ExcludesRow()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        // v = s compares two nullable columns; row 2 has both NULL but NULL = NULL is UNKNOWN.
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE v = v"));
        Assert.Equal([1L, 3L], result.Rows.Select(r => r[0]).OrderBy(x => x));
    }

    [Fact]
    public void Where_IsNull_MatchesOnlyNullRows()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE v IS NULL"));
        Assert.Equal([2L], result.Rows.Select(r => r[0]));
    }

    [Fact]
    public void Where_IsNotNull_ExcludesNullRows()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE v IS NOT NULL"));
        Assert.Equal([1L, 3L], result.Rows.Select(r => r[0]).OrderBy(x => x));
    }

    [Fact]
    public void Where_NotOfNullComparison_StaysUnknown_ExcludesRow()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        // NOT (v = 5): row 1 -> NOT TRUE = FALSE; row 2 -> NOT UNKNOWN = UNKNOWN (excluded);
        // row 3 -> NOT FALSE = TRUE. Only row 3 survives.
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE NOT (v = 5)"));
        Assert.Equal([3L], result.Rows.Select(r => r[0]));
    }

    [Fact]
    public void Where_OrWithUnknown_TrueBranchStillMatches()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        // (v = 5) OR (id = 2): row 2's v=5 is UNKNOWN but id=2 is TRUE, so row 2 matches.
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE v = 5 OR id = 2"));
        Assert.Equal([1L, 2L], result.Rows.Select(r => r[0]).OrderBy(x => x));
    }

    [Fact]
    public void Where_NullInList_NoMatch_IsUnknown()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        // v IN (1, 2): row 2 (v NULL) is UNKNOWN; no row matches those literals.
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE v IN (1, 2)"));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Where_NotInWithNullValue_IsUnknown_ExcludesRow()
    {
        using var db = Tsdb.Open(Options());
        SeedNullSemanticsTable(db);

        // v NOT IN (5): row 2 (v NULL) must be excluded (UNKNOWN), row 3 (v=10) included.
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM t WHERE v NOT IN (5)"));
        Assert.Equal([3L], result.Rows.Select(r => r[0]));
    }

    // ── 哈希连接（#215）─────────────────────────────────────────────────────

    [Fact]
    public void HashJoin_OneToMany_DuplicateKeys_EmitsAllMatches()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE sites (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, site_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO sites (id, name) VALUES (1, 'north'), (2, 'south')");
        // 多台设备指向同一 site（一对多）：北 3 台、南 1 台。
        SqlExecutor.Execute(db, "INSERT INTO devices (id, site_id) VALUES (10, 1), (11, 1), (12, 1), (13, 2)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT d.id, s.name
            FROM devices d JOIN sites s ON d.site_id = s.id
            ORDER BY d.id
            """));

        Assert.Equal(4, r.Rows.Count);
        Assert.Equal(new object?[] { 10L, "north" }, r.Rows[0]);
        Assert.Equal(new object?[] { 11L, "north" }, r.Rows[1]);
        Assert.Equal(new object?[] { 12L, "north" }, r.Rows[2]);
        Assert.Equal(new object?[] { 13L, "south" }, r.Rows[3]);
    }

    [Fact]
    public void HashJoin_NullKey_DoesNotMatch()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE sites (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, site_id INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO sites (id, name) VALUES (1, 'north')");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, site_id) VALUES (10, 1), (11, NULL)");

        // INNER JOIN：NULL 键不匹配，只返回 device 10。
        var inner = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT d.id FROM devices d JOIN sites s ON d.site_id = s.id ORDER BY d.id
            """));
        Assert.Equal([10L], inner.Rows.Select(r => (long)r[0]!));

        // LEFT JOIN：NULL 键的 device 11 仍出现，右侧列为 NULL。
        var left = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT d.id, s.name FROM devices d LEFT JOIN sites s ON d.site_id = s.id ORDER BY d.id
            """));
        Assert.Equal(2, left.Rows.Count);
        Assert.Equal(new object?[] { 10L, "north" }, left.Rows[0]);
        Assert.Equal(new object?[] { 11L, null }, left.Rows[1]);
    }

    [Fact]
    public void HashJoin_WithResidualNonEquiPredicate_FiltersMatches()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE a (id INT, k INT, v INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE b (id INT, k INT, w INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO a (id, k, v) VALUES (1, 5, 100), (2, 5, 10)");
        SqlExecutor.Execute(db, "INSERT INTO b (id, k, w) VALUES (1, 5, 50)");

        // ON a.k = b.k AND a.v > b.w：等值键 k 走哈希，残差 a.v > b.w 在候选对上再过滤。
        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT a.id FROM a JOIN b ON a.k = b.k AND a.v > b.w ORDER BY a.id
            """));
        // a1 (v=100 > 50) 命中；a2 (v=10 > 50 假) 被残差过滤。
        Assert.Equal([1L], r.Rows.Select(row => (long)row[0]!));
    }

    [Fact]
    public void HashJoin_MultiColumnKey_MatchesOnBothColumns()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE a (id INT, x INT, y INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE b (id INT, x INT, y INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO a (id, x, y) VALUES (1, 1, 2), (2, 1, 3)");
        SqlExecutor.Execute(db, "INSERT INTO b (id, x, y) VALUES (10, 1, 2), (11, 1, 9)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT a.id, b.id FROM a JOIN b ON a.x = b.x AND a.y = b.y ORDER BY a.id
            """));
        // 仅 (a1,b10) 在 (x,y)=(1,2) 上双列匹配。
        Assert.Single(r.Rows);
        Assert.Equal(new object?[] { 1L, 10L }, r.Rows[0]);
    }

    // ── 子查询记忆化（#216）─────────────────────────────────────────────────

    [Fact]
    public void NonCorrelatedInSubquery_OverManyOuterRows_ReturnsCorrectRows()
    {
        // 非相关 IN 子查询整段外层扫描只执行一次（记忆化）；结果对所有外层行必须正确。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE items (id INT, kind STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE allowed (kind STRING, PRIMARY KEY (kind))");
        SqlExecutor.Execute(db, "INSERT INTO allowed (kind) VALUES ('a'), ('c')");
        SqlExecutor.Execute(db, """
            INSERT INTO items (id, kind) VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'a'), (5, 'd')
            """);

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM items WHERE kind IN (SELECT kind FROM allowed) ORDER BY id
            """));
        Assert.Equal([1L, 3L, 4L], r.Rows.Select(row => (long)row[0]!));
    }

    [Fact]
    public void NonCorrelatedScalarSubquery_ReusedAcrossRows_IsCorrect()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE nums (id INT, v INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO nums (id, v) VALUES (1, 10), (2, 20), (3, 30), (4, 40)");

        // v > (SELECT avg-ish 常量子查询)：非相关标量子查询，跨行复用同一结果。
        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM nums WHERE v > (SELECT v FROM nums WHERE id = 2) ORDER BY id
            """));
        // v > 20 → id 3,4。
        Assert.Equal([3L, 4L], r.Rows.Select(row => (long)row[0]!));
    }

    [Fact]
    public void CorrelatedScalarSubquery_NotMemoized_PerRowResultCorrect()
    {
        // 相关标量子查询（引用外层列）绝不能被记忆化——每行须独立求值。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE dept (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE emp (id INT, dept_id INT, salary INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO dept (id, name) VALUES (1, 'eng'), (2, 'ops')");
        SqlExecutor.Execute(db, """
            INSERT INTO emp (id, dept_id, salary) VALUES
                (10, 1, 100), (11, 1, 300), (12, 2, 50), (13, 2, 40)
            """);

        // 每个部门内 salary 高于本部门首个员工 salary 的员工。
        // 相关：子查询 WHERE e2.dept_id = e.dept_id 引用外层 e。
        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT e.id
            FROM emp e
            WHERE e.salary > (SELECT min(e2.salary) FROM emp e2 WHERE e2.dept_id = e.dept_id)
            ORDER BY e.id
            """));
        // eng 最低=100 → 11(300); ops 最低=40 → 12(50)。
        Assert.Equal([11L, 12L], r.Rows.Select(row => (long)row[0]!));
    }

    [Fact]
    public void CorrelatedExists_NotMemoized_FiltersPerOuterRow()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE cust (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE ord (id INT, cust_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO cust (id, name) VALUES (1, 'a'), (2, 'b'), (3, 'c')");
        SqlExecutor.Execute(db, "INSERT INTO ord (id, cust_id) VALUES (10, 1), (20, 3)");

        // 有订单的客户：相关 EXISTS 引用外层 c.id，须逐行求值。
        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT c.name FROM cust c WHERE EXISTS (SELECT 1 FROM ord o WHERE o.cust_id = c.id) ORDER BY c.name
            """));
        Assert.Equal(["a", "c"], r.Rows.Select(row => (string)row[0]!));
    }

    // ── #218 事务隔离 / read-your-writes ──────────────────────────────────────

    [Fact]
    public void LightTransaction_SelectSeesBufferedInsert()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump')");

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (2, 'fan');
            SELECT id FROM devices ORDER BY id;
            COMMIT;
            """);

        // 事务内的 SELECT 应看到已提交的行 + 本事务缓冲的插入行。
        var inTxn = Assert.IsType<SelectExecutionResult>(results[2]);
        Assert.Equal([1L, 2L], inTxn.Rows.Select(static r => (long)r[0]!));
    }

    [Fact]
    public void LightTransaction_SelectSeesBufferedUpdate()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump')");

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            UPDATE devices SET name = 'renamed' WHERE id = 1;
            SELECT name FROM devices WHERE id = 1;
            COMMIT;
            """);

        var inTxn = Assert.IsType<SelectExecutionResult>(results[2]);
        Assert.Equal("renamed", inTxn.Rows.Single()[0]);
    }

    [Fact]
    public void LightTransaction_SelectSeesBufferedDelete()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump'), (2, 'fan')");

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            DELETE FROM devices WHERE id = 1;
            SELECT id FROM devices ORDER BY id;
            COMMIT;
            """);

        var inTxn = Assert.IsType<SelectExecutionResult>(results[2]);
        Assert.Equal([2L], inTxn.Rows.Select(static r => (long)r[0]!));
    }

    [Fact]
    public void LightTransaction_BufferedWritesInvisibleOutsideTransaction()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump')");

        // 事务缓冲的插入在 ROLLBACK 后不可见——read-your-writes 只在事务内叠加，不落库。
        SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (2, 'fan');
            ROLLBACK;
            """);

        var after = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices ORDER BY id"));
        Assert.Equal([1L], after.Rows.Select(static r => (long)r[0]!));
    }

    [Fact]
    public void LightTransaction_ReadYourWrites_VisibleThroughAggregateAndSubquery()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump')");

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (2, 'fan'), (3, 'valve');
            SELECT count(*) AS n FROM devices;
            SELECT id FROM devices WHERE id IN (SELECT id FROM devices WHERE id > 1) ORDER BY id;
            COMMIT;
            """);

        // 聚合走关系路径，也应看到缓冲写：3 行。
        var count = Assert.IsType<SelectExecutionResult>(results[2]);
        Assert.Equal(3L, count.Rows.Single()[0]);

        // 子查询同样在事务作用域内，缓冲写对内外层都可见。
        var sub = Assert.IsType<SelectExecutionResult>(results[3]);
        Assert.Equal([2L, 3L], sub.Rows.Select(static r => (long)r[0]!));
    }

    [Fact]
    public void NonTransactionalSelect_UnaffectedByOverlay()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump'), (2, 'fan')");

        // 无事务的普通 SELECT 走既有 PK / 索引 / scan 快路径，不受 overlay 影响。
        var byPk = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT name FROM devices WHERE id = 2"));
        Assert.Equal("fan", byPk.Rows.Single()[0]);

        var all = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices ORDER BY id"));
        Assert.Equal([1L, 2L], all.Rows.Select(static r => (long)r[0]!));
    }

    // ── #219 Q11：SELECT DISTINCT ───────────────────────────────────────────

    [Fact]
    public void SelectDistinct_RelationalTable_DeduplicatesRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE rel_dd (id INT, region STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_dd (id, region) VALUES (1, 'cn'), (2, 'cn'), (3, 'us'), (4, 'cn')");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT DISTINCT region FROM rel_dd ORDER BY region"));

        Assert.Equal(["cn", "us"], r.Rows.Select(static row => (string)row[0]!));
    }

    [Fact]
    public void SelectDistinct_MultiColumn_DeduplicatesOnTuple()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE rel_dd2 (id INT, a STRING, b INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_dd2 (id, a, b) VALUES (1, 'x', 1), (2, 'x', 1), (3, 'x', 2), (4, 'y', 1)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT DISTINCT a, b FROM rel_dd2"));

        Assert.Equal(3, r.Rows.Count);
    }

    [Fact]
    public void SelectDistinct_WithLimit_DedupesBeforePaging()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE rel_dd3 (id INT, region STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_dd3 (id, region) VALUES (1, 'a'), (2, 'a'), (3, 'b'), (4, 'c')");

        // 去重后有 3 个 region（a,b,c）；LIMIT 2 施加在去重之后 → 恰 2 行。
        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT DISTINCT region FROM rel_dd3 ORDER BY region LIMIT 2"));

        Assert.Equal(["a", "b"], r.Rows.Select(static row => (string)row[0]!));
    }

    [Fact]
    public void SelectDistinct_Star_MeasurementPath_Deduplicates()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        // 同一 (time, host, usage) 只会存一份；用两个 host 制造重复 usage 值行去重于投影列。
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 1.0), (2000, 'h2', 1.0), (3000, 'h1', 2.0)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT DISTINCT usage FROM cpu"));

        Assert.Equal(2, r.Rows.Count);
    }

    // ── #219 Q12：关系路径未加引号标识符大小写不敏感 ───────────────────────

    [Fact]
    public void RelationalSelect_ColumnReference_IsCaseInsensitive()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE rel_case (id INT, Amount INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO rel_case (id, Amount) VALUES (1, 10), (2, 20), (3, 30)");

        // 走关系聚合路径（sum 触发 NeedsRelationalPath）；用不同大小写引用 Amount。
        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT sum(AMOUNT) FROM rel_case WHERE amount > 10"));

        Assert.Equal(50L, r.Rows[0][0]);
    }

    [Fact]
    public void RelationalJoin_KeyColumn_IsCaseInsensitive()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE rel_c (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE rel_o (id INT, Customer_Id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO rel_c (id, name) VALUES (1, 'alice'), (2, 'bob')");
        SqlExecutor.Execute(db, "INSERT INTO rel_o (id, Customer_Id) VALUES (10, 1), (20, 1), (30, 2)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT c.name, count(*) FROM rel_c c JOIN rel_o o ON c.ID = o.customer_id GROUP BY c.name ORDER BY c.name"));

        Assert.Equal(2, r.Rows.Count);
        Assert.Equal("alice", r.Rows[0][0]);
        Assert.Equal(2L, r.Rows[0][1]);
    }

    // ── #219 Q15：聚合返回类型由 schema 静态类型决定 ──────────────────────

    [Fact]
    public void RelationalAggregate_IntColumn_ReturnsLongWithoutPrescan()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE rel_q15 (id INT, v INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO rel_q15 (id, v) VALUES (1, 5), (2, 7), (3, 11)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT sum(v), min(v), max(v) FROM rel_q15"));

        Assert.IsType<long>(r.Rows[0][0]);
        Assert.IsType<long>(r.Rows[0][1]);
        Assert.IsType<long>(r.Rows[0][2]);
        Assert.Equal(23L, r.Rows[0][0]);
    }

    [Fact]
    public void RelationalAggregate_FloatColumn_ReturnsDouble()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE rel_q15f (id INT, v FLOAT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO rel_q15f (id, v) VALUES (1, 1.5), (2, 2.5)");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT sum(v), max(v) FROM rel_q15f"));

        Assert.IsType<double>(r.Rows[0][0]);
        Assert.IsType<double>(r.Rows[0][1]);
    }

    [Fact]
    public void RelationalAggregate_BigLongColumn_KeepsIntegralPrecision()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE rel_q15big (id INT, v INT, PRIMARY KEY (id))");
        // 两个大 long 之和仍在 long 范围内：静态类型判定应保持整型累加，不经 double 丢精度。
        long a = 9_000_000_000_000_000_001L;
        long b = 2L;
        SqlExecutor.Execute(db, $"INSERT INTO rel_q15big (id, v) VALUES (1, {a}), (2, {b})");

        var r = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT sum(v) FROM rel_q15big"));

        var sum = Assert.IsType<long>(r.Rows[0][0]);
        Assert.Equal(a + b, sum);
    }
}
