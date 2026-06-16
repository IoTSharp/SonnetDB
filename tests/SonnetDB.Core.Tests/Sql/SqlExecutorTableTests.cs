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
}
