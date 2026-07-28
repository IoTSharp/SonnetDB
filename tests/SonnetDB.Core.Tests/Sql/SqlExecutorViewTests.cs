using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Views;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlExecutorViewTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-view-" + Guid.NewGuid().ToString("N"));

    public SqlExecutorViewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void ParseCreateView_WithIfNotExists_CapturesSelectDefinition()
    {
        var statement = Assert.IsType<CreateViewStatement>(SqlParser.Parse(
            "CREATE VIEW IF NOT EXISTS active_devices AS SELECT id, name FROM devices WHERE enabled = TRUE"));

        Assert.Equal("active_devices", statement.Name);
        Assert.True(statement.IfNotExists);
        Assert.Equal("devices", statement.Query.Measurement);
        Assert.Equal("SELECT id, name FROM devices WHERE enabled = TRUE", statement.DefinitionSql);
        Assert.IsType<DropViewStatement>(SqlParser.Parse("DROP VIEW IF EXISTS active_devices"));
        Assert.IsType<ShowViewsStatement>(SqlParser.Parse("SHOW VIEWS"));
        Assert.IsType<DescribeViewStatement>(SqlParser.Parse("DESCRIBE VIEW active_devices"));

        var tokenParser = new SqlParser(SqlLexer.Tokenize(
            "CREATE VIEW quoted_values AS SELECT json_value(document, '$.name') AS value FROM readings"));
        var tokenStatement = Assert.IsType<CreateViewStatement>(tokenParser.ParseStatement());
        Assert.IsType<SelectStatement>(SqlParser.Parse(tokenStatement.DefinitionSql));
    }

    [Fact]
    public void CreateView_QueryAndMetadata_PersistAcrossReopen()
    {
        using (var database = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(database,
                "CREATE TABLE devices (id INT, name STRING, enabled BOOL, PRIMARY KEY (id))");
            SqlExecutor.Execute(database,
                "INSERT INTO devices (id, name, enabled) VALUES (1, 'pump', TRUE), (2, 'fan', FALSE)");
            SqlExecutor.Execute(database,
                "CREATE VIEW active_devices AS SELECT id, name FROM devices WHERE enabled = TRUE");
        }

        using var reopened = Tsdb.Open(Options());
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            reopened,
            "SELECT name FROM active_devices WHERE id = 1"));
        Assert.Equal(new object?[] { "pump" }, selected.Rows.Single());

        var show = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, "SHOW VIEWS"));
        Assert.Equal(["name", "created_utc"], show.Columns);
        Assert.Equal("active_devices", show.Rows.Single()[0]);

        var describe = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            reopened,
            "DESCRIBE VIEW active_devices"));
        Assert.Equal(["name", "definition", "dependencies", "created_utc"], describe.Columns);
        Assert.Equal("devices", describe.Rows.Single()[2]);

        var informationSchema = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            reopened,
            "SELECT table_name, table_type FROM information_schema.tables WHERE table_name = 'active_devices'"));
        Assert.Equal(new object?[] { "active_devices", "VIEW" }, informationSchema.Rows.Single());
    }

    [Fact]
    public void Select_FromNestedViews_ExpandsRecursively()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO devices (id, name, enabled) VALUES (1, 'pump', TRUE), (2, 'fan', FALSE), (3, 'boiler', TRUE)");
        SqlExecutor.Execute(database,
            "CREATE VIEW active_devices AS SELECT id, name FROM devices WHERE enabled = TRUE");
        SqlExecutor.Execute(database,
            "CREATE VIEW named_devices AS SELECT id, name FROM active_devices WHERE id > 1");

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            "SELECT name FROM named_devices ORDER BY name"));

        Assert.Equal(["boiler"], selected.Rows.Select(static row => (string)row[0]!).ToArray());
    }

    [Fact]
    public void Select_ViewParticipatingInJoin_ReturnsRows()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE sites (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, site_id INT, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO sites (id, name) VALUES (10, 'north'), (20, 'south')");
        SqlExecutor.Execute(database,
            "INSERT INTO devices (id, name, site_id, enabled) VALUES (1, 'pump', 10, TRUE), (2, 'fan', 20, FALSE)");
        SqlExecutor.Execute(database,
            "CREATE VIEW active_devices AS SELECT id, name, site_id FROM devices WHERE enabled = TRUE");

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, """
            SELECT d.name AS device, s.name AS site
            FROM active_devices d
            JOIN sites s ON d.site_id = s.id
            """));

        Assert.Equal(new object?[] { "pump", "north" }, selected.Rows.Single());
    }

    [Fact]
    public void Select_ViewOverDocumentCollection_UsesExistingDocumentExecutor()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION readings");
        SqlExecutor.Execute(database, """
            INSERT INTO readings (id, document)
            VALUES ('r1', '{"value":7}'), ('r2', '{"value":3}')
            """);
        SqlExecutor.Execute(database, """
            CREATE VIEW reading_values AS
            SELECT id, json_value(document, '$.value') AS value FROM readings
            """);

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            "SELECT id, value FROM reading_values WHERE value > 3"));

        Assert.Equal(new object?[] { "r1", 7.0 }, selected.Rows.Single());
    }

    [Fact]
    public void DropOrAlterReferencedObject_ThrowsUntilDependentViewsAreRemoved()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE VIEW device_names AS SELECT id, name FROM devices");
        SqlExecutor.Execute(database,
            "CREATE VIEW public_devices AS SELECT name FROM device_names");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "DROP TABLE devices"));
        Assert.Throws<InvalidOperationException>(() => database.Tables.Drop("devices"));
        Assert.Throws<InvalidOperationException>(() =>
            database.Tables.AlterTableAddColumn(
                "devices",
                "enabled",
                SonnetDB.Tables.TableColumnType.Boolean,
                isNullable: true,
                defaultValue: null));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "ALTER TABLE devices ADD COLUMN enabled BOOL"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "DROP VIEW device_names"));

        SqlExecutor.Execute(database, "DROP VIEW public_devices");
        SqlExecutor.Execute(database, "DROP VIEW device_names");
        var dropped = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(database, "DROP TABLE devices"));
        Assert.Equal(1, dropped.RowsAffected);
    }

    [Fact]
    public void CreateView_WithUnknownSelfOrParameterSource_Throws()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "CREATE VIEW missing_view AS SELECT * FROM missing"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "CREATE VIEW recursive_view AS SELECT * FROM recursive_view"));
        Assert.Throws<ArgumentException>(() =>
            SqlExecutor.Execute(database, "CREATE VIEW parameterized_view AS SELECT * FROM devices WHERE id = @id"));

        var first = Assert.IsType<ViewDefinition>(SqlExecutor.Execute(
            database,
            "CREATE VIEW device_names AS SELECT name FROM devices"));
        var second = Assert.IsType<ViewDefinition>(SqlExecutor.Execute(
            database,
            "CREATE VIEW IF NOT EXISTS device_names AS SELECT id FROM devices"));
        Assert.Same(first, second);
    }

    [Fact]
    public void Select_WithCyclicPersistedDefinitions_ThrowsCycleDiagnostic()
    {
        using var database = Tsdb.Open(Options());
        database.Views.Create(ViewDefinition.Create("view_a", "SELECT * FROM view_b"));
        database.Views.Create(ViewDefinition.Create("view_b", "SELECT * FROM view_a"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "SELECT * FROM view_a"));

        Assert.Contains("view_a -> view_b -> view_a", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewDefinitionCodec_CorruptedPayload_ThrowsInvalidDataException()
    {
        string path = Path.Combine(_root, "codec", ViewDefinitionCodec.FileName);
        var definition = ViewDefinition.Create("device_names", "SELECT name FROM devices");
        ViewDefinitionCodec.Save(path, [definition]);

        var loaded = ViewDefinitionCodec.Load(path);
        Assert.Equal("device_names", loaded.Single().Name);
        Assert.Equal(["devices"], loaded.Single().Dependencies);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[40] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => ViewDefinitionCodec.Load(path));
    }
}
