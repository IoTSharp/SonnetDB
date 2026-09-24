using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlUpdateJoinTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-update-join-{Guid.NewGuid():N}");

    [Fact]
    public void UpdateJoin_DuplicateSourceMatches_UpdateTargetOnceAndReturnOnce()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE devices (id INT, tenant_id INT, status STRING, PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, """
            CREATE TABLE tenants (id INT, sequence INT, enabled BOOL, status STRING, PRIMARY KEY (id, sequence))
            """);
        SqlExecutor.Execute(db, "INSERT INTO devices (id, tenant_id, status) VALUES (1, 10, 'active')");
        SqlExecutor.Execute(db, """
            INSERT INTO tenants (id, sequence, enabled, status) VALUES
                (10, 1, FALSE, 'first'), (10, 2, FALSE, 'second')
            """);

        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            """
            UPDATE devices AS d
            JOIN tenants AS t ON d.tenant_id = t.id
            SET status = t.status
            WHERE t.enabled = FALSE
            RETURNING id, status
            """));

        Assert.Equal(1, result.RowsAffected);
        Assert.Equal(["id", "status"], result.Returning!.Columns);
        Assert.Equal(new object?[] { 1L, "first" }, Assert.Single(result.Returning.Rows));
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT status FROM devices WHERE id = 1"));
        Assert.Equal("first", Assert.Single(selected.Rows)[0]);
    }

    [Fact]
    public void UpdateJoin_MultipleInnerJoins_UsesFirstDeclaredSourceMatch()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, join_key INT, picked STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE left_sources (id INT, join_key INT, rank INT, value STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE right_sources (id INT, join_key INT, rank INT, value STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id, join_key, picked) VALUES (1, 10, 'unset')");
        SqlExecutor.Execute(db, """
            INSERT INTO left_sources (id, join_key, rank, value) VALUES
                (1, 10, 1, 'left-rank1'),
                (2, 10, 2, 'left-rank2'),
                (3, 10, 3, 'left-rank3')
            """);
        SqlExecutor.Execute(db, """
            INSERT INTO right_sources (id, join_key, rank, value) VALUES
                (1, 10, 2, 'right-rank2'),
                (2, 10, 1, 'right-rank1')
            """);

        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            """
            UPDATE targets AS t
            JOIN left_sources AS l ON t.join_key = l.join_key
            JOIN right_sources AS r ON l.rank = r.rank
            SET picked = r.value
            WHERE t.id = 1
            RETURNING picked
            """));

        Assert.Equal(1, result.RowsAffected);
        Assert.Equal(new object?[] { "right-rank1" }, Assert.Single(result.Returning!.Rows));
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT picked FROM targets WHERE id = 1"));
        Assert.Equal("right-rank1", Assert.Single(selected.Rows)[0]);
    }

    [Fact]
    public void UpdateFrom_WithParameters_UpdatesOnlyMatchedTargets()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE devices (id INT, tenant_id INT, status STRING, PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, """
            CREATE TABLE tenants (id INT, enabled BOOL, PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, "INSERT INTO devices (id, tenant_id, status) VALUES (1, 10, 'active'), (2, 20, 'active')");
        SqlExecutor.Execute(db, "INSERT INTO tenants (id, enabled) VALUES (10, FALSE)");

        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            sql: """
            UPDATE devices AS d
            SET status = 'disabled'
            FROM tenants AS t
            WHERE d.tenant_id = t.id AND t.enabled = @enabled
            """,
            parameters: new SqlParameters().AddNamed("enabled", true),
            controlPlane: null));

        Assert.Equal(0, result.RowsAffected);
        var unchanged = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, status FROM devices ORDER BY id"));
        Assert.Equal(
            new object?[] { new object?[] { 1L, "active" }, new object?[] { 2L, "active" } },
            unchanged.Rows);

        result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            sql: "UPDATE devices AS d SET status = 'disabled' FROM tenants AS t WHERE d.tenant_id = t.id AND t.enabled = @enabled",
            parameters: new SqlParameters().AddNamed("enabled", false),
            controlPlane: null));
        Assert.Equal(1, result.RowsAffected);
    }

    [Fact]
    public void UpdateJoin_LeftJoin_CanUpdateUnmatchedTargetsOnce()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE devices (id INT, tenant_id INT, status STRING, PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, "CREATE TABLE tenants (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, tenant_id, status) VALUES (1, 10, 'active'), (2, 20, 'active')");
        SqlExecutor.Execute(db, "INSERT INTO tenants (id) VALUES (10)");

        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            """
            UPDATE devices AS d
            LEFT JOIN tenants AS t ON d.tenant_id = t.id
            SET status = 'orphan'
            WHERE t.id IS NULL
            """));

        Assert.Equal(1, result.RowsAffected);
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, status FROM devices ORDER BY id"));
        Assert.Equal(
            new object?[] { new object?[] { 1L, "active" }, new object?[] { 2L, "orphan" } },
            selected.Rows);
    }

    [Fact]
    public void UpdateJoin_StaleRowVersionPredicate_ReturnsConcurrencyConflict()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, status STRING, version INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE device_flags (device_id INT, enabled BOOL, PRIMARY KEY (device_id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, status) VALUES (1, 'active')");
        SqlExecutor.Execute(db, "INSERT INTO device_flags (device_id, enabled) VALUES (1, TRUE)");

        var exception = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db, """
            UPDATE devices AS d
            JOIN device_flags AS f ON d.id = f.device_id
            SET status = 'stale-write'
            WHERE d.id = 1 AND d.version = 0
            """));

        Assert.Equal(TableConstraintException.ConcurrencyConflict, exception.ErrorCode);
        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT status, version FROM devices WHERE id = 1"));
        Assert.Equal(new object?[] { "active", 1L }, Assert.Single(row.Rows));
    }

    [Fact]
    public void UpdateJoin_MultiTableChainAndTriggers_ReturnsFinalTargetImage()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE jobs (tenant INT, id INT, source_id INT, picked INT, rv INT ROWVERSION, PRIMARY KEY (tenant, id))");
        SqlExecutor.Execute(db, "CREATE TABLE prices (tenant INT, id INT, base_value INT, PRIMARY KEY (tenant, id))");
        SqlExecutor.Execute(db, "CREATE TABLE adjustments (tenant INT, source_id INT, delta INT, PRIMARY KEY (tenant, source_id))");
        SqlExecutor.Execute(db, "CREATE TABLE job_audit (tenant INT, id INT, picked INT, rv INT, PRIMARY KEY (tenant, id))");
        SqlExecutor.Execute(db, "INSERT INTO jobs (tenant, id, source_id, picked) VALUES (7, 1, 10, 0), (8, 1, 10, 0)");
        SqlExecutor.Execute(db, "INSERT INTO prices (tenant, id, base_value) VALUES (7, 10, 10), (8, 10, 50)");
        SqlExecutor.Execute(db, "INSERT INTO adjustments (tenant, source_id, delta) VALUES (7, 10, 4), (8, 10, 8)");
        SqlExecutor.Execute(db, "CREATE TRIGGER normalize_job BEFORE UPDATE ON jobs FOR EACH ROW LANGUAGE SQL AS BEGIN SET NEW.picked = NEW.picked + 1; END");
        SqlExecutor.Execute(db, "CREATE TRIGGER audit_job AFTER UPDATE ON jobs FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO job_audit (tenant, id, picked, rv) VALUES (NEW.tenant, NEW.id, NEW.picked, NEW.rv); END");

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db, """
            UPDATE jobs AS j
            JOIN prices AS p ON j.tenant = p.tenant AND j.source_id = p.id
            JOIN adjustments AS a ON p.tenant = a.tenant AND p.id = a.source_id
            SET picked = p.base_value + a.delta
            WHERE j.tenant = 7
            RETURNING tenant, id, picked, rv
            """));

        Assert.Equal(1, updated.RowsAffected);
        Assert.Equal(new object?[] { 7L, 1L, 15L, 2L }, Assert.Single(updated.Returning!.Rows));
        Assert.Equal(new object?[] { 7L, 1L, 15L, 2L }, Assert.Single(Select(db, "SELECT tenant, id, picked, rv FROM job_audit").Rows));
        Assert.Equal(new object?[] { 8L, 1L, 0L, 1L }, Assert.Single(Select(db, "SELECT tenant, id, picked, rv FROM jobs WHERE tenant = 8").Rows));
        Assert.Equal(10L, Assert.Single(Select(db, "SELECT base_value FROM prices WHERE tenant = 7").Rows)[0]);
    }

    [Fact]
    public void UpdateJoin_LeftJoinNullAndCompositeKey_MatchesOnlyNonNullSources()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE targets (tenant INT, id INT, key_part INT, picked STRING, PRIMARY KEY (tenant, id))");
        SqlExecutor.Execute(db, "CREATE TABLE sources (tenant INT, id INT, key_part INT, label STRING, PRIMARY KEY (tenant, id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (tenant, id, key_part, picked) VALUES (1, 1, NULL, 'old'), (2, 1, 5, 'old')");
        SqlExecutor.Execute(db, "INSERT INTO sources (tenant, id, key_part, label) VALUES (1, 1, NULL, 'wrong'), (2, 1, 5, 'matched')");

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db, """
            UPDATE targets AS t
            LEFT JOIN sources AS s ON t.tenant = s.tenant AND t.key_part = s.key_part
            SET picked = COALESCE(s.label, 'missing')
            WHERE t.id >= 1
            RETURNING tenant, id, picked
            """));

        Assert.Equal(2, updated.RowsAffected);
        Assert.Equal(new object?[] { new object?[] { 1L, 1L, "missing" }, new object?[] { 2L, 1L, "matched" } },
            Select(db, "SELECT tenant, id, picked FROM targets ORDER BY tenant, id").Rows);
    }

    [Fact]
    public void UpdateJoin_UniqueAndForeignKeyConflicts_RollBackWholeStatement()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE parents (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, code INT, parent_id INT, PRIMARY KEY (id), FOREIGN KEY (parent_id) REFERENCES parents (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_targets_code ON targets (code)");
        SqlExecutor.Execute(db, "CREATE TABLE sources (id INT, new_code INT, new_parent INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1)");
        SqlExecutor.Execute(db, "INSERT INTO targets (id, code, parent_id) VALUES (1, 10, 1), (2, 20, 1)");
        SqlExecutor.Execute(db, "INSERT INTO sources (id, new_code, new_parent) VALUES (1, 30, 1), (2, 30, 1)");

        Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db,
            "UPDATE targets AS t JOIN sources AS s ON t.id = s.id SET code = s.new_code WHERE t.id >= 1 RETURNING id, code"));
        Assert.Equal(new object?[] { new object?[] { 1L, 10L, 1L }, new object?[] { 2L, 20L, 1L } },
            Select(db, "SELECT id, code, parent_id FROM targets ORDER BY id").Rows);

        SqlExecutor.Execute(db, "UPDATE sources SET new_code = 40, new_parent = 999 WHERE id = 2");
        Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db,
            "UPDATE targets AS t JOIN sources AS s ON t.id = s.id SET code = s.new_code, parent_id = s.new_parent WHERE t.id >= 1 RETURNING id, code"));
        Assert.Equal(new object?[] { new object?[] { 1L, 10L, 1L }, new object?[] { 2L, 20L, 1L } },
            Select(db, "SELECT id, code, parent_id FROM targets ORDER BY id").Rows);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
