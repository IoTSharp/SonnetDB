using SonnetDB.Cli;
using Xunit;

namespace SonnetDB.Core.Tests.Cli;

public sealed class CliApplicationTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SonnetDB.Cli.Tests.{Guid.NewGuid():N}");
    private readonly string _profilePath = Path.Combine(
        Path.GetTempPath(),
        $"SonnetDB.Cli.Tests.Profiles.{Guid.NewGuid():N}.json");

    public CliApplicationTests()
    {
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public void Run_WithoutArguments_PrintsHelp()
    {
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run([]);

        Assert.Equal(0, exitCode);
        Assert.Contains("SonnetDB CLI", stdout.ToString());
        Assert.Contains("sndb local", stdout.ToString());
        Assert.Contains("sndb remote", stdout.ToString());
        Assert.Contains("sndb connect", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_SqlCommand_WithEmbeddedConnection_PrintsResultTable()
    {
        var connectionString = $"Data Source={_rootDirectory}";

        var create = RunCommand(connectionString, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");
        Assert.Equal(0, create.ExitCode);
        Assert.Contains("OK", create.Stdout);

        var insert = RunCommand(
            connectionString,
            "INSERT INTO cpu(host, value, time) VALUES ('server-1', 63.2, 1776477601000)");
        Assert.Equal(0, insert.ExitCode);
        Assert.Contains("OK", insert.Stdout);

        var query = RunCommand(connectionString, "SELECT host, value FROM cpu");

        Assert.Equal(0, query.ExitCode);
        Assert.Contains("host", query.Stdout);
        Assert.Contains("value", query.Stdout);
        Assert.Contains("server-1", query.Stdout);
        Assert.Contains("63.2", query.Stdout);
        Assert.Contains("(1 row(s))", query.Stdout);
        Assert.Equal(string.Empty, query.Stderr);
    }

    [Fact]
    public void Run_LocalCommand_WithoutSql_PrintsEmbeddedConnectionString()
    {
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["local", "--path", _rootDirectory]);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Data Source={_rootDirectory}", stdout.ToString());
        Assert.Contains("Mode=Embedded", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_LocalCommand_WithSql_ExecutesAgainstEmbeddedStore()
    {
        var app = CreateApp(out var stdout, out var stderr);

        var createExitCode = app.Run([
            "local", "--path", _rootDirectory,
            "--command", "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)"]);

        Assert.Equal(0, createExitCode);
        Assert.Contains("OK", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var insertExitCode = app.Run([
            "local", "--path", _rootDirectory,
            "--command", "INSERT INTO cpu(host, value, time) VALUES ('server-2', 42.5, 1776477602000)"]);

        Assert.Equal(0, insertExitCode);
        Assert.Contains("OK", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var queryExitCode = app.Run([
            "local", "--path", _rootDirectory,
            "--command", "SELECT host, value FROM cpu"]);

        Assert.Equal(0, queryExitCode);
        Assert.Contains("server-2", stdout.ToString());
        Assert.Contains("42.5", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_BackupCommands_CreateVerifyRestoreEmbeddedDatabase()
    {
        var app = CreateApp(out var stdout, out var stderr);
        string backupPath = Path.Combine(_rootDirectory, "backup");
        string restoredPath = Path.Combine(_rootDirectory, "restored");
        string sourcePath = Path.Combine(_rootDirectory, "source");

        var createMeasurement = app.Run([
            "local", "--path", sourcePath,
            "--command", "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)"]);
        Assert.Equal(0, createMeasurement);

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var insert = app.Run([
            "local", "--path", sourcePath,
            "--command", "INSERT INTO cpu(host, value, time) VALUES ('server-1', 63.2, 1776477601000)"]);
        Assert.Equal(0, insert);

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var backup = app.Run(["backup", "create", "--path", sourcePath, "--output", backupPath]);
        Assert.Equal(0, backup);
        Assert.Contains("备份已创建", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var verify = app.Run(["backup", "verify", "--path", backupPath]);
        Assert.Equal(0, verify);
        Assert.Contains("备份校验通过", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var restore = app.Run(["backup", "restore", "--path", backupPath, "--target", restoredPath]);
        Assert.Equal(0, restore);
        Assert.Contains("备份已恢复", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var query = app.Run([
            "local", "--path", restoredPath,
            "--command", "SELECT host, value FROM cpu"]);

        Assert.Equal(0, query);
        Assert.Contains("server-1", stdout.ToString());
        Assert.Contains("63.2", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_BackupCommands_DryRunRestoreAndRebuildIndexes_CoversMultimodelIndexes()
    {
        var app = CreateApp(out var stdout, out var stderr);
        string backupPath = Path.Combine(_rootDirectory, "mm-backup");
        string restoredPath = Path.Combine(_rootDirectory, "mm-restored");
        string sourcePath = Path.Combine(_rootDirectory, "mm-source");

        Assert.Equal(0, app.Run([
            "local", "--path", sourcePath,
            "--command", "CREATE TABLE devices (id INT, metadata JSON, PRIMARY KEY (id))"]));
        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        Assert.Equal(0, app.Run([
            "local", "--path", sourcePath,
            "--command", """INSERT INTO devices (id, metadata) VALUES (1, '{"site":"north"}'), (2, '{"site":"south"}')"""]));
        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        Assert.Equal(0, app.Run([
            "local", "--path", sourcePath,
            "--command", "CREATE JSON INDEX idx_devices_site ON devices (metadata, '$.site')"]));
        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        Assert.Equal(0, app.Run([
            "local", "--path", sourcePath,
            "--command", "CREATE DOCUMENT COLLECTION docs"]));
        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        Assert.Equal(0, app.Run([
            "local", "--path", sourcePath,
            "--command", """INSERT INTO docs (id, document) VALUES ('d1', '{"body":"pump alarm north"}'), ('d2', '{"body":"fan normal"}')"""]));
        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        Assert.Equal(0, app.Run([
            "local", "--path", sourcePath,
            "--command", "CREATE FULLTEXT INDEX ft_docs_body ON docs ('$.body') USING unicode"]));
        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var backup = app.Run([
            "backup", "create",
            "--path", sourcePath,
            "--output", backupPath,
            "--no-fulltext-indexes"]);
        Assert.Equal(0, backup);
        Assert.Contains("备份已创建", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.False(Directory.Exists(Path.Combine(backupPath, "documents", "fulltext")));

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var inspect = app.Run(["backup", "inspect", "--path", backupPath]);
        Assert.Equal(0, inspect);
        Assert.Contains("table/devices/idx_devices_site: json_path", stdout.ToString());
        Assert.Contains("document/docs/ft_docs_body: fulltext, excluded, rebuildable", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var dryRun = app.Run(["backup", "dry-run", "--path", backupPath, "--target", restoredPath]);
        Assert.Equal(0, dryRun);
        Assert.Contains("恢复 dry-run 通过", stdout.ToString());
        Assert.Contains("indexes=2", stdout.ToString());
        Assert.False(Directory.Exists(restoredPath));
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var restore = app.Run([
            "backup", "restore",
            "--path", backupPath,
            "--target", restoredPath,
            "--rebuild-indexes"]);
        Assert.Equal(0, restore);
        Assert.Contains("备份已恢复", stdout.ToString());
        Assert.Contains("索引补建: total=2, rebuilt=2, planned=0, failed=0", stdout.ToString());
        Assert.Contains("document/docs/ft_docs_body: fulltext, rebuilt, documents=2", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var tableQuery = app.Run([
            "local", "--path", restoredPath,
            "--command", "SELECT id FROM devices WHERE json_value(metadata, '$.site') = 'south'"]);
        Assert.Equal(0, tableQuery);
        Assert.Contains("2", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var fullTextQuery = app.Run([
            "local", "--path", restoredPath,
            "--command", "SELECT id FROM docs WHERE match(ft_docs_body, '$.body', 'pump alarm', 5)"]);
        Assert.Equal(0, fullTextQuery);
        Assert.Contains("d1", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_RemoteCommand_WithoutSql_PrintsRemoteConnectionString()
    {
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run([
            "remote",
            "--url", "http://127.0.0.1:5080",
            "--database", "metrics",
            "--token", "secret-token",
            "--timeout", "30"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Mode=Remote", stdout.ToString());
        Assert.Contains("Data Source=http://127.0.0.1:5080/metrics", stdout.ToString());
        Assert.DoesNotContain("Database=metrics", stdout.ToString());
        Assert.Contains("Token=secret-token", stdout.ToString());
        Assert.Contains("Timeout=30", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_RemoteCommand_WithoutDatabase_FailsValidation()
    {
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["remote", "--url", "http://127.0.0.1:5080"]);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("必须通过 --database 指定数据库名", stderr.ToString());
    }

    [Fact]
    public void Run_RemoteCommand_SaveProfileAndList_PersistsProfile()
    {
        var app = CreateApp(out var stdout, out var stderr);

        var saveExitCode = app.Run([
            "remote",
            "--url", "http://127.0.0.1:5080",
            "--database", "metrics",
            "--token", "secret-token",
            "--timeout", "30",
            "--save-profile", "dev",
            "--default"]);

        Assert.Equal(0, saveExitCode);
        Assert.Contains("Mode=Remote", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var listExitCode = app.Run(["remote", "list"]);

        Assert.Equal(0, listExitCode);
        Assert.Contains("* dev => http://127.0.0.1:5080/metrics (timeout=30)", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_RemoteCommand_Profile_UsesSavedProfile()
    {
        SeedProfile("dev", "http://127.0.0.1:5080", "metrics", "secret-token", 30, isDefault: true);
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["remote", "--profile", "dev"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Data Source=http://127.0.0.1:5080/metrics", stdout.ToString());
        Assert.Contains("Token=secret-token", stdout.ToString());
        Assert.Contains("Timeout=30", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_RemoteCommand_UseDefault_UsesDefaultProfile()
    {
        SeedProfile("prod", "https://server.example.com", "telemetry", "prod-token", 45, isDefault: true);
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["remote", "--use-default"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Data Source=https://server.example.com/telemetry", stdout.ToString());
        Assert.Contains("Token=prod-token", stdout.ToString());
        Assert.Contains("Timeout=45", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_RemoteRemove_RemovesSavedProfile()
    {
        SeedProfile("dev", "http://127.0.0.1:5080", "metrics", "secret-token", 30, isDefault: true);
        var app = CreateApp(out var stdout, out var stderr);

        var removeExitCode = app.Run(["remote", "remove", "--profile", "dev"]);

        Assert.Equal(0, removeExitCode);
        Assert.Contains("已删除 remote profile 'dev'", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var listExitCode = app.Run(["remote", "list"]);

        Assert.Equal(0, listExitCode);
        Assert.Contains("未配置任何 remote profile", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // ── local profile tests ───────────────────────────────────────────────────

    [Fact]
    public void Run_LocalCommand_SaveProfile_PersistsLocalProfile()
    {
        var app = CreateApp(out var stdout, out var stderr);

        var saveExitCode = app.Run([
            "local", "--path", _rootDirectory, "--save-profile", "home", "--default"]);

        Assert.Equal(0, saveExitCode);
        Assert.Contains("Mode=Embedded", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var listExitCode = app.Run(["local", "list"]);

        Assert.Equal(0, listExitCode);
        Assert.Contains($"* home => {_rootDirectory}", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_LocalCommand_ProfileFlag_UsesLocalProfile()
    {
        SeedLocalProfile("home", _rootDirectory, isDefault: false);
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["local", "--profile", "home"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Mode=Embedded", stdout.ToString());
        Assert.Contains(_rootDirectory, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_LocalCommand_UseDefault_UsesDefaultLocalProfile()
    {
        SeedLocalProfile("home", _rootDirectory, isDefault: true);
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["local", "--use-default"]);

        Assert.Equal(0, exitCode);
        Assert.Contains(_rootDirectory, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_LocalRemove_RemovesLocalProfile()
    {
        SeedLocalProfile("home", _rootDirectory, isDefault: true);
        var app = CreateApp(out var stdout, out var stderr);

        var removeExitCode = app.Run(["local", "remove", "--profile", "home"]);

        Assert.Equal(0, removeExitCode);
        Assert.Contains("已删除 local profile 'home'", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var listExitCode = app.Run(["local", "list"]);

        Assert.Equal(0, listExitCode);
        Assert.Contains("未配置任何 local profile", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // ── connect tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Run_Connect_LocalProfile_BuildsEmbeddedConnectionString()
    {
        SeedLocalProfile("home", _rootDirectory, isDefault: false);
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["connect", "home"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Mode=Embedded", stdout.ToString());
        Assert.Contains(_rootDirectory, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_Connect_RemoteProfile_BuildsRemoteConnectionString()
    {
        SeedProfile("dev", "http://127.0.0.1:5080", "metrics", "secret-token", 30, isDefault: false);
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["connect", "dev"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Mode=Remote", stdout.ToString());
        Assert.Contains("Data Source=http://127.0.0.1:5080/metrics", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_Connect_UnknownProfile_ReturnsInvalidArguments()
    {
        var app = CreateApp(out _, out var stderr);

        var exitCode = app.Run(["connect", "nonexistent"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("未找到 profile 'nonexistent'", stderr.ToString());
    }

    [Fact]
    public void Run_Connect_Default_LocalDefault_RoutesCorrectly()
    {
        SeedLocalProfile("home", _rootDirectory, isDefault: true);
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["connect", "--default"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Mode=Embedded", stdout.ToString());
        Assert.Contains(_rootDirectory, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_Connect_Default_RemoteDefault_RoutesCorrectly()
    {
        SeedProfile("dev", "http://127.0.0.1:5080", "metrics", null, 100, isDefault: true);
        var app = CreateApp(out var stdout, out var stderr);

        var exitCode = app.Run(["connect", "--default"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Mode=Remote", stdout.ToString());
        Assert.Contains("Data Source=http://127.0.0.1:5080/metrics", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void CliProfileStore_OldFileWithoutLocalProfiles_DeserializesCleanly()
    {
        var oldJson = """{"defaultProfile":"dev","profiles":[{"name":"dev","baseUrl":"http://127.0.0.1:5080","database":"metrics","timeout":100}]}""";
        File.WriteAllText(_profilePath, oldJson);

        var store = new CliProfileStore(_profilePath);
        var doc = store.Load();

        Assert.Equal("dev", doc.DefaultProfile);
        Assert.Single(doc.Profiles);
        Assert.Equal("dev", doc.Profiles[0].Name);
        Assert.Empty(doc.LocalProfiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }

        if (File.Exists(_profilePath))
        {
            File.Delete(_profilePath);
        }
    }

    private static CommandResult RunCommand(string connectionString, string sql)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApplication(new StringReader(string.Empty), stdout, stderr);

        var exitCode = app.Run(
            ["sql", "--connection", connectionString, "--command", sql]);

        return new CommandResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private CliApplication CreateApp(out StringWriter stdout, out StringWriter stderr)
    {
        stdout = new StringWriter();
        stderr = new StringWriter();
        return new CliApplication(new StringReader(string.Empty), stdout, stderr, new CliProfileStore(_profilePath));
    }

    private void SeedProfile(string name, string baseUrl, string database, string? token, int timeout, bool isDefault)
    {
        var store = new CliProfileStore(_profilePath);
        store.Upsert(new CliRemoteProfile(name, baseUrl, database, token, timeout));
        if (isDefault)
            store.SetDefault(name);
    }

    private void SeedLocalProfile(string name, string path, bool isDefault)
    {
        var store = new CliProfileStore(_profilePath);
        store.UpsertLocal(new CliLocalProfile(name, path));
        if (isDefault)
            store.SetDefault(name);
    }

    private readonly record struct CommandResult(int ExitCode, string Stdout, string Stderr);
}
