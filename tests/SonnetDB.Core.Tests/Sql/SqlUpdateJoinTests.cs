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
}
