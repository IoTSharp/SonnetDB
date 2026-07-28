using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Views;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlExecutorMaterializedViewTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-materialized-view-" + Guid.NewGuid().ToString("N"));

    public SqlExecutorMaterializedViewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试临时目录由系统后续清理。 */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void ParseMaterializedViewStatements_WithSupportedSyntax_ReturnsTypedAst()
    {
        var create = Assert.IsType<CreateMaterializedViewStatement>(SqlParser.Parse(
            "CREATE MATERIALIZED VIEW IF NOT EXISTS active_devices AS SELECT id, name FROM devices"));

        Assert.Equal("active_devices", create.Name);
        Assert.True(create.IfNotExists);
        Assert.Equal("devices", create.Query.Measurement);
        Assert.Equal("SELECT id, name FROM devices", create.DefinitionSql);
        Assert.Equal(
            "active_devices",
            Assert.IsType<RefreshMaterializedViewStatement>(
                SqlParser.Parse("REFRESH MATERIALIZED VIEW active_devices")).Name);
        Assert.True(Assert.IsType<DropMaterializedViewStatement>(
            SqlParser.Parse("DROP MATERIALIZED VIEW IF EXISTS active_devices")).IfExists);
        Assert.IsType<ShowMaterializedViewsStatement>(SqlParser.Parse("SHOW MATERIALIZED VIEWS"));
        Assert.IsType<DescribeMaterializedViewStatement>(
            SqlParser.Parse("DESCRIBE MATERIALIZED VIEW active_devices"));
        Assert.IsType<ExplainStatement>(
            SqlParser.Parse("EXPLAIN SHOW MATERIALIZED VIEWS"));

        Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "CREATE OR REPLACE MATERIALIZED VIEW active_devices AS SELECT * FROM devices"));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "DROP MATERIALIZED VIEW active_devices CASCADE"));
    }

    [Fact]
    public void MaterializedView_ExplicitRefreshAndReopen_PreservesSnapshotIsolation()
    {
        using (var database = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(database,
                "CREATE TABLE devices (id INT, name STRING, enabled BOOL, PRIMARY KEY (id))");
            SqlExecutor.Execute(database,
                "INSERT INTO devices (id, name, enabled) VALUES (1, 'pump', TRUE), (2, 'fan', FALSE)");
            var definition = Assert.IsType<MaterializedViewDefinition>(SqlExecutor.Execute(database, """
                CREATE MATERIALIZED VIEW active_devices AS
                SELECT id, name FROM devices WHERE enabled = TRUE
                """));

            Assert.Equal(MaterializedViewRefreshStatus.Uninitialized, definition.Status);
            Assert.Throws<InvalidOperationException>(() =>
                SqlExecutor.Execute(database, "SELECT * FROM active_devices"));

            var firstRefresh = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
                database,
                "REFRESH MATERIALIZED VIEW active_devices"));
            Assert.Equal(1, firstRefresh.RowsAffected);
            Assert.Equal(
                new object?[] { 1L, "pump" },
                Select(database, "SELECT id, name FROM active_devices").Rows.Single());

            SqlExecutor.Execute(database,
                "INSERT INTO devices (id, name, enabled) VALUES (3, 'boiler', TRUE)");
            Assert.Single(Select(database, "SELECT id FROM active_devices").Rows);

            var secondRefresh = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
                database,
                "REFRESH MATERIALIZED VIEW active_devices"));
            Assert.Equal(2, secondRefresh.RowsAffected);
            Assert.Equal(
                [1L, 3L],
                Select(database, "SELECT id FROM active_devices ORDER BY id")
                    .Rows.Select(static row => (long)row[0]!).ToArray());

            var show = Select(database, "SHOW MATERIALIZED VIEWS");
            Assert.Equal(
                ["name", "status", "definition_version", "active_generation", "row_count", "refreshed_utc", "error"],
                show.Columns);
            Assert.Equal("ready", show.Rows.Single()[1]);
            Assert.Equal(2L, show.Rows.Single()[4]);

            var informationSchema = Select(database, """
                SELECT table_name, status, row_count
                FROM information_schema.materialized_views
                WHERE table_name = 'active_devices'
                """);
            Assert.Equal(new object?[] { "active_devices", "ready", 2L }, informationSchema.Rows.Single());

            var tableMetadata = Select(database, """
                SELECT table_name, table_type
                FROM information_schema.tables
                WHERE table_name = 'active_devices'
                """);
            Assert.Equal(new object?[] { "active_devices", "MATERIALIZED VIEW" }, tableMetadata.Rows.Single());
        }

        using var reopened = Tsdb.Open(Options());
        Assert.Equal(
            ["pump", "boiler"],
            Select(reopened, "SELECT name FROM active_devices ORDER BY id")
                .Rows.Select(static row => (string)row[0]!).ToArray());
        var describe = Select(reopened, "DESCRIBE MATERIALIZED VIEW active_devices");
        Assert.Equal("devices", describe.Rows.Single()[2]);
        Assert.Equal("ready", describe.Rows.Single()[4]);

        var explain = Select(reopened, "EXPLAIN SELECT * FROM active_devices");
        Assert.Contains(
            explain.Rows,
            static row => Equals(row[0], "access_path") && Equals(row[1], "materialized_view_snapshot"));
    }

    [Fact]
    public void RefreshMaterializedView_WhenNewGenerationFails_KeepsPreviousGenerationReadable()
    {
        using (var database = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(database,
                "CREATE TABLE divisors (id INT, divisor INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(database,
                "INSERT INTO divisors (id, divisor) VALUES (1, 4)");
            SqlExecutor.Execute(database, """
                CREATE MATERIALIZED VIEW quotients AS
                SELECT id, 100 / divisor AS quotient FROM divisors
                """);
            SqlExecutor.Execute(database, "REFRESH MATERIALIZED VIEW quotients");
            long previousGeneration = database.MaterializedViews.Catalog.TryGet("quotients")!.ActiveGeneration;

            SqlExecutor.Execute(database,
                "INSERT INTO divisors (id, divisor) VALUES (2, 0)");
            Assert.Throws<InvalidOperationException>(() =>
                SqlExecutor.Execute(database, "REFRESH MATERIALIZED VIEW quotients"));

            var failed = database.MaterializedViews.Catalog.TryGet("quotients")!;
            Assert.Equal(MaterializedViewRefreshStatus.Failed, failed.Status);
            Assert.Equal(previousGeneration, failed.ActiveGeneration);
            Assert.Contains("除数不能为 0", failed.LastError, StringComparison.Ordinal);
            Assert.Equal(
                new object?[] { 1L, 25.0 },
                Select(database, "SELECT id, quotient FROM quotients").Rows.Single());
        }

        using var reopened = Tsdb.Open(Options());
        Assert.Equal(
            new object?[] { 1L, 25.0 },
            Select(reopened, "SELECT id, quotient FROM quotients").Rows.Single());
        Assert.Equal(
            MaterializedViewRefreshStatus.Failed,
            reopened.MaterializedViews.Catalog.TryGet("quotients")!.Status);
    }

    [Fact]
    public async Task RefreshMaterializedView_WhileBuildingNewGeneration_ReadsPreviousGeneration()
    {
        string managerRoot = Path.Combine(_root, "manager");
        var manager = new MaterializedViewManager(managerRoot);
        manager.Create(MaterializedViewDefinition.Create("snapshot", "SELECT 1 AS value"));
        manager.Refresh("snapshot", static () => Result(1L));

        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task refresh = Task.Run(() => manager.Refresh("snapshot", () =>
        {
            started.Set();
            release.Wait();
            return Result(2L);
        }));

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(MaterializedViewRefreshStatus.Refreshing, manager.Catalog.TryGet("snapshot")!.Status);
        Assert.Equal(1L, manager.ReadSnapshot("snapshot").Rows.Single()[0]);
        Assert.Throws<InvalidOperationException>(() =>
            manager.Refresh("snapshot", static () => Result(3L)));

        release.Set();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2L, manager.ReadSnapshot("snapshot").Rows.Single()[0]);
    }

    [Fact]
    public void RefreshMaterializedView_InsideLightTransaction_IsRejectedWithoutPublishingGeneration()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO devices (id, name) VALUES (1, 'pump')");
        SqlExecutor.Execute(database,
            "CREATE MATERIALIZED VIEW device_cache AS SELECT id, name FROM devices");
        SqlExecutor.Execute(database, "REFRESH MATERIALIZED VIEW device_cache");
        var before = database.MaterializedViews.Catalog.TryGet("device_cache")!;

        SqlExecutor.Execute(database,
            "INSERT INTO devices (id, name) VALUES (2, 'fan')");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.ExecuteScript(database, """
                BEGIN;
                REFRESH MATERIALIZED VIEW device_cache;
                COMMIT;
                """));

        Assert.Contains("不能在活动轻事务内执行", exception.Message, StringComparison.Ordinal);
        var after = database.MaterializedViews.Catalog.TryGet("device_cache")!;
        Assert.Equal(before.ActiveGeneration, after.ActiveGeneration);
        Assert.Equal(MaterializedViewRefreshStatus.Ready, after.Status);
        Assert.Equal([1L], Select(database, "SELECT id FROM device_cache")
            .Rows.Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void MaterializedViewManager_OnRestart_RecoversInterruptedRefreshAndRemovesUnpublishedArtifacts()
    {
        string managerRoot = Path.Combine(_root, "recovery-manager");
        var original = new MaterializedViewManager(managerRoot);
        original.Create(MaterializedViewDefinition.Create("snapshot", "SELECT 1 AS value"));
        original.Refresh("snapshot", static () => Result(1L));
        var ready = original.Catalog.TryGet("snapshot")!;
        var interrupted = ready.WithRefreshStarted();
        MaterializedViewDefinitionCodec.Save(original.CatalogPath, [interrupted]);

        string unpublishedGeneration = original.GetGenerationPath(ready.StorageId, 2);
        MaterializedViewSnapshotCodec.Save(unpublishedGeneration, Result(2L));
        string temporaryArtifact = unpublishedGeneration + ".tmp-crash";
        File.WriteAllText(temporaryArtifact, "partial");

        var recovered = new MaterializedViewManager(managerRoot);
        var definition = recovered.Catalog.TryGet("snapshot")!;

        Assert.Equal(MaterializedViewRefreshStatus.Failed, definition.Status);
        Assert.Equal(ready.ActiveGeneration, definition.ActiveGeneration);
        Assert.Contains("被进程终止", definition.LastError, StringComparison.Ordinal);
        Assert.Equal(1L, recovered.ReadSnapshot("snapshot").Rows.Single()[0]);
        Assert.False(File.Exists(unpublishedGeneration));
        Assert.False(File.Exists(temporaryArtifact));
    }

    [Fact]
    public void Select_MaterializedViewWithJoinAggregationAndLogicalView_ReturnsRows()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE sites (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, site_id INT, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO sites (id, name) VALUES (10, 'north'), (20, 'south')");
        SqlExecutor.Execute(database, """
            INSERT INTO devices (id, site_id, enabled)
            VALUES (1, 10, TRUE), (2, 10, FALSE), (3, 20, TRUE)
            """);
        SqlExecutor.Execute(database, """
            CREATE MATERIALIZED VIEW device_cache AS
            SELECT id, site_id, enabled FROM devices
            """);
        SqlExecutor.Execute(database, "REFRESH MATERIALIZED VIEW device_cache");
        SqlExecutor.Execute(database, """
            CREATE VIEW active_device_cache AS
            SELECT id, site_id FROM device_cache WHERE enabled = TRUE
            """);

        var selected = Select(database, """
            SELECT s.name AS site, COUNT(*) AS device_count
            FROM active_device_cache d
            JOIN sites s ON d.site_id = s.id
            GROUP BY s.name
            ORDER BY site
            """);

        Assert.Equal(
            [new object?[] { "north", 1L }, new object?[] { "south", 1L }],
            selected.Rows);
    }

    [Fact]
    public void MaterializedViewDependencies_BlockMutationAndDropUntilDependentsAreRemoved()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE VIEW device_names AS SELECT id, name FROM devices");
        SqlExecutor.Execute(database,
            "CREATE MATERIALIZED VIEW cached_names AS SELECT id, name FROM device_names");
        SqlExecutor.Execute(database, "REFRESH MATERIALIZED VIEW cached_names");
        SqlExecutor.Execute(database,
            "CREATE VIEW public_names AS SELECT name FROM cached_names");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, "DROP TABLE devices"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "ALTER TABLE devices ADD COLUMN enabled BOOL"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, "DROP VIEW device_names"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "DROP MATERIALIZED VIEW cached_names"));

        SqlExecutor.Execute(database, "DROP VIEW public_names");
        SqlExecutor.Execute(database, "DROP MATERIALIZED VIEW cached_names");
        SqlExecutor.Execute(database, "DROP VIEW device_names");
        Assert.Equal(
            1,
            Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(database, "DROP TABLE devices")).RowsAffected);
    }

    [Fact]
    public void CreateMaterializedView_WithUnknownSelfParameterOrConflictingName_Throws()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            database,
            "CREATE MATERIALIZED VIEW missing_cache AS SELECT * FROM missing"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            database,
            "CREATE MATERIALIZED VIEW recursive_cache AS SELECT * FROM recursive_cache"));
        Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(
            database,
            "CREATE MATERIALIZED VIEW parameterized_cache AS SELECT * FROM devices WHERE id = @id"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            database,
            "CREATE MATERIALIZED VIEW devices AS SELECT * FROM devices"));

        var first = Assert.IsType<MaterializedViewDefinition>(SqlExecutor.Execute(
            database,
            "CREATE MATERIALIZED VIEW device_names AS SELECT name FROM devices"));
        var second = Assert.IsType<MaterializedViewDefinition>(SqlExecutor.Execute(
            database,
            "CREATE MATERIALIZED VIEW IF NOT EXISTS device_names AS SELECT id FROM devices"));
        Assert.Same(first, second);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            database,
            "CREATE VIEW device_names AS SELECT name FROM devices"));
    }

    [Fact]
    public void MaterializedViewSnapshotCodec_AllSupportedTypes_RoundTripsAndDetectsCorruption()
    {
        string path = Path.Combine(_root, "codec", "snapshot.sdbmvsnap");
        var dateTime = new DateTime(638900000000000000, DateTimeKind.Utc);
        var dateTimeOffset = new DateTimeOffset(638900000000000000, TimeSpan.FromHours(8));
        var result = new SelectExecutionResult(
            [
                "null", "int64", "uint64", "float64", "decimal", "bool", "string",
                "datetime", "datetimeoffset", "blob", "vector", "geopoint"
            ],
            [
                new object?[]
                {
                    null,
                    -42L,
                    ulong.MaxValue,
                    1.25d,
                    123.456m,
                    true,
                    "设备-A",
                    dateTime,
                    dateTimeOffset,
                    new byte[] { 0, 1, 255 },
                    new float[] { 1.5f, -2.25f },
                    GeoPoint.Create(39.9, 116.4),
                },
            ]);

        MaterializedViewSnapshotCodec.Save(path, result);
        var loaded = MaterializedViewSnapshotCodec.Load(path);

        Assert.Equal(result.Columns, loaded.Columns);
        var row = loaded.Rows.Single();
        Assert.Null(row[0]);
        Assert.Equal(-42L, row[1]);
        Assert.Equal(ulong.MaxValue, row[2]);
        Assert.Equal(1.25d, row[3]);
        Assert.Equal(123.456m, row[4]);
        Assert.Equal(true, row[5]);
        Assert.Equal("设备-A", row[6]);
        Assert.Equal(dateTime, row[7]);
        Assert.Equal(dateTimeOffset, row[8]);
        Assert.Equal(new byte[] { 0, 1, 255 }, Assert.IsType<byte[]>(row[9]));
        Assert.Equal(new float[] { 1.5f, -2.25f }, Assert.IsType<float[]>(row[10]));
        Assert.Equal(GeoPoint.Create(39.9, 116.4), row[11]);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[HeaderOffset(bytes)] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => MaterializedViewSnapshotCodec.Load(path));
    }

    [Fact]
    public void MaterializedViewDefinitionCodec_CorruptedPayload_ThrowsInvalidDataException()
    {
        string path = Path.Combine(_root, "catalog", MaterializedViewDefinitionCodec.FileName);
        var definition = MaterializedViewDefinition.Create("device_names", "SELECT name FROM devices");
        MaterializedViewDefinitionCodec.Save(path, [definition]);

        var loaded = MaterializedViewDefinitionCodec.Load(path).Single();
        Assert.Equal("device_names", loaded.Name);
        Assert.Equal(["devices"], loaded.Dependencies);
        Assert.Equal(1, loaded.DefinitionVersion);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[48] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => MaterializedViewDefinitionCodec.Load(path));
    }

    private static SelectExecutionResult Select(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));

    private static SelectExecutionResult Result(long value)
        => new(["value"], [new object?[] { value }]);

    private static int HeaderOffset(byte[] bytes)
    {
        Assert.True(bytes.Length > 48);
        return 40;
    }
}
