using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>存量主键列重命名的嵌入式、真实 REST/h2c、DbFirst 元数据及宿主重启合同。</summary>
public sealed class RemotePrimaryKeyRenameTests : IAsyncLifetime
{
    private const string DatabaseName = "primary_rename";
    private const string AdminToken = "primary-rename-admin";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-remote-pk-rename-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(90));
    private readonly ConcurrentQueue<(string Path, string Protocol)> _requests = new();
    private WebApplication? _app;
    private string _restUrl = string.Empty;
    private string _frameUrl = string.Empty;

    /// <summary>启动真实宿主并创建独立测试数据库。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await StartServerAsync();
        using var http = new HttpClient { BaseAddress = new Uri(_restUrl), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync("/v1/db", new StringContent(
            "{\"name\":\"" + DatabaseName + "\"}", Encoding.UTF8, "application/json"), _deadline.Token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>关闭宿主并删除本次测试专属目录。</summary>
    public async Task DisposeAsync()
    {
        try { await StopServerAsync(); }
        finally
        {
            _deadline.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task RenamePopulatedPrimaryKey_RefreshesMetadataConstraintsAndDataAcrossRestart(string protocol)
    {
        using (var connection = new SndbConnection(ConnectionString(protocol)))
        {
            await connection.OpenAsync(_deadline.Token);
            Execute(connection, """
                CREATE TABLE parents (
                    id INT AUTO_INCREMENT, label STRING DEFAULT 'base', version INT ROWVERSION,
                    PRIMARY KEY (id), CONSTRAINT ck_positive CHECK (id > 0 AND label <> 'id'))
                """);
            Execute(connection, "CREATE UNIQUE INDEX ux_label ON parents (label)");
            Execute(connection, "CREATE INDEX ix_key_label ON parents (id, label)");
            Execute(connection, "CREATE TABLE children (id INT, parent_id INT, PRIMARY KEY (id), CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES parents (id))");
            Execute(connection, "INSERT INTO parents (label) VALUES ('one'), ('two')");
            Execute(connection, "INSERT INTO children (id, parent_id) VALUES (10, 1)");
            Assert.Equal("id", connection.GetSchema("Columns", [null, null, "parents", null]).Rows[0]["COLUMN_NAME"]);

            Execute(connection, "ALTER TABLE parents RENAME COLUMN id TO Identifier");
            AssertMetadata(connection);
            using (var observer = new SndbConnection(ConnectionString(protocol)))
            {
                await observer.OpenAsync(_deadline.Token);
                AssertMetadata(observer);
            }
            AssertError(connection, "INSERT INTO parents (Identifier, label) VALUES (-1, 'negative')", TableConstraintException.CheckViolation);
            AssertError(connection, "INSERT INTO parents (Identifier, label) VALUES (3, 'id')", TableConstraintException.CheckViolation);
            AssertError(connection, "INSERT INTO parents (Identifier, label) VALUES (1, 'duplicate')", TableConstraintException.UniqueViolation);
            AssertError(connection, "INSERT INTO parents (Identifier, label) VALUES (8, 'two')", TableConstraintException.UniqueViolation);
            AssertError(connection, "INSERT INTO children (id, parent_id) VALUES (11, 99)", TableConstraintException.ForeignKeyViolation);
            AssertError(connection, "DELETE FROM parents WHERE Identifier = 1", TableConstraintException.ForeignKeyViolation);
            Assert.NotNull(Record.Exception(() => Execute(connection, "ALTER TABLE parents RENAME COLUMN Identifier TO LABEL")));
            AssertMetadata(connection);
            Assert.Equal(1, Execute(connection, "UPDATE parents SET label = 'updated' WHERE Identifier = 1 AND version = 1"));

            _requests.Clear();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Identifier, label, version FROM parents WHERE Identifier = 1";
            using var reader = await command.ExecuteReaderAsync(_deadline.Token);
            Assert.Equal("Identifier", reader.GetName(0));
            Assert.True(await reader.ReadAsync(_deadline.Token));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("updated", reader.GetString(1));
            Assert.Equal(2L, reader.GetInt64(2));
            Assert.False(await reader.ReadAsync(_deadline.Token));
            AssertQueryProtocol(protocol);
        }

        // 两种远程协议重启整个 Server，从同一持久化目录重新加载；嵌入式则关闭最后一个连接后重开。
        if (protocol != "embedded")
        {
            await StopServerAsync();
            await StartServerAsync();
        }
        using var reopened = new SndbConnection(ConnectionString(protocol));
        await reopened.OpenAsync(_deadline.Token);
        AssertMetadata(reopened);
        Execute(reopened, "INSERT INTO parents DEFAULT VALUES");
        using var reopenedCommand = reopened.CreateCommand();
        reopenedCommand.CommandText = "SELECT Identifier, version FROM parents WHERE label = 'base'";
        using var reopenedReader = await reopenedCommand.ExecuteReaderAsync(_deadline.Token);
        Assert.True(await reopenedReader.ReadAsync(_deadline.Token));
        Assert.True(reopenedReader.GetInt64(0) > 2L);
        Assert.Equal(1L, reopenedReader.GetInt64(1));
        Assert.False(await reopenedReader.ReadAsync(_deadline.Token));
        AssertError(reopened, "INSERT INTO children (id, parent_id) VALUES (12, 99)", TableConstraintException.ForeignKeyViolation);
    }

    private async Task StartServerAsync()
    {
        _restUrl = string.Empty;
        _frameUrl = string.Empty;
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        }, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
            await next(context);
        });
        await _app.StartAsync(_deadline.Token);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Assert.Equal(2, addresses.Addresses.Count);
        foreach (string address in addresses.Addresses)
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            try
            {
                using var response = await probe.SendAsync(request, _deadline.Token);
                if (response.IsSuccessStatusCode) _restUrl = address;
                else _frameUrl = address;
            }
            catch (HttpRequestException) { _frameUrl = address; }
        }
        Assert.NotEmpty(_restUrl);
        Assert.NotEmpty(_frameUrl);
    }

    private async Task StopServerAsync()
    {
        if (_app is null) return;
        using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _app.StopAsync(stopDeadline.Token); }
        finally { await _app.DisposeAsync(); _app = null; }
    }

    private string ConnectionString(string protocol)
        => protocol == "embedded"
            ? $"Data Source={Path.Combine(_root, "embedded")};Timeout=10"
            : $"Data Source=sonnetdb+http://{new Uri(protocol == "rest" ? _restUrl : _frameUrl).Authority}/{DatabaseName};Token={AdminToken};Protocol={protocol};Timeout=10";

    private static int Execute(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static void AssertMetadata(SndbConnection connection)
    {
        var columns = connection.GetSchema("Columns", [null, null, "parents", null]);
        Assert.Equal(["Identifier", "label", "version"], columns.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]).ToArray());
        DataRow key = columns.Rows[0];
        Assert.True((bool)key["IS_PRIMARY_KEY"]);
        Assert.True((bool)key["IS_AUTO_INCREMENT"]);
        Assert.False((bool)key["IS_NULLABLE"]);
        Assert.True((bool)columns.Rows[2]["IS_ROW_VERSION"]);
        var foreignKey = Assert.Single(connection.GetSchema("ForeignKeys", [null, null, "children", null]).Rows.Cast<DataRow>());
        Assert.Equal("parent_id", foreignKey["COLUMN_NAME"]);
        Assert.Equal("Identifier", foreignKey["PRINCIPAL_COLUMN_NAME"]);
        var indexes = connection.GetSchema("Indexes", [null, null, "parents", "ix_key_label"]);
        Assert.Equal(["Identifier", "label"], indexes.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]).ToArray());
    }

    private static void AssertError(SndbConnection connection, string sql, string expectedCode)
    {
        Exception? exception = Record.Exception(() => Execute(connection, sql));
        string? code = exception switch
        {
            TableConstraintException embedded => embedded.ErrorCode,
            SndbServerException remote => remote.Error,
            _ => null,
        };
        Assert.Equal(expectedCode, code);
    }

    private void AssertQueryProtocol(string protocol)
    {
        if (protocol == "embedded") return;
        string path = protocol == "rest" ? "/v1/db/" + DatabaseName + "/sql" : "/v1/frame";
        var queries = _requests.Where(request => request.Path == path).ToArray();
        Assert.Single(queries);
        Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", queries[0].Protocol);
    }
}
