using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>GH-Issue #187 的嵌入式和真实 REST ADO.NET schema 演进合同。</summary>
public sealed class RemoteDdlEvolutionTests : IAsyncLifetime
{
    private const string DatabaseName = "ddl_evolution";
    private const string AdminToken = "ddl-evolution-admin";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-ddl-evolution-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _baseUrl = string.Empty;

    /// <summary>启动真实 Kestrel 服务并创建测试数据库。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        });
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync(
            "/v1/db",
            new StringContent("{\"name\":\"" + DatabaseName + "\"}",
                System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>关闭测试服务并清理专属数据库目录。</summary>
    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void AddGeneratedColumns_EmbeddedAndRemote_RefreshesAdoMetadataAndAffectedCounts(string mode)
    {
        string connectionString = mode == "embedded"
            ? $"Data Source={Path.Combine(_root, "embedded-" + Guid.NewGuid().ToString("N"))}"
            : $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{DatabaseName};Token={AdminToken};Timeout=30";
        using var connection = new SndbConnection(connectionString);
        connection.Open();

        Assert.Equal(0, Execute(connection, "CREATE TABLE devices (code STRING, name STRING, PRIMARY KEY (code))"));
        Assert.Equal(2, Execute(connection,
            "INSERT INTO devices (code, name) VALUES ('one', 'first'), ('two', 'second')"));
        Assert.Equal(1, Execute(connection, "ALTER TABLE devices ADD COLUMN id INT AUTO_INCREMENT"));
        Assert.Equal(1, Execute(connection, "ALTER TABLE devices ADD COLUMN version INT ROWVERSION"));

        var columns = connection.GetSchema("Columns", [null, null, "devices", null]);
        var id = columns.Rows.Cast<DataRow>().Single(row => (string)row["COLUMN_NAME"] == "id");
        var version = columns.Rows.Cast<DataRow>().Single(row => (string)row["COLUMN_NAME"] == "version");
        Assert.True((bool)id["IS_AUTO_INCREMENT"]);
        Assert.False((bool)id["IS_NULLABLE"]);
        Assert.True((bool)version["IS_ROW_VERSION"]);
        Assert.False((bool)version["IS_NULLABLE"]);

        Assert.Equal(1, Execute(connection, "INSERT INTO devices (code, name) VALUES ('three', 'third')"));
        Assert.Equal(1, Execute(connection, "UPDATE devices SET name = 'updated' WHERE code = 'one' AND version = 1"));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, version FROM devices WHERE code = 'three'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void EmptyKeylessTable_EmbeddedAndRemote_AddsKeyAndGeneratedColumns(string mode)
    {
        string connectionString = mode == "embedded"
            ? $"Data Source={Path.Combine(_root, "keyless-" + Guid.NewGuid().ToString("N"))}"
            : $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{DatabaseName};Token={AdminToken};Timeout=30";
        using var connection = new SndbConnection(connectionString);
        connection.Open();

        Assert.Equal(0, Execute(connection, "CREATE TABLE pending_devices (code STRING NOT NULL)"));
        Assert.Equal(1, Execute(connection, "ALTER TABLE pending_devices ADD COLUMN id INT AUTO_INCREMENT"));
        var blocked = Record.Exception(() => Execute(connection,
            "INSERT INTO pending_devices (code) VALUES ('blocked')"));
        var code = blocked switch
        {
            TableConstraintException embedded => embedded.ErrorCode,
            SndbServerException remote => remote.Error,
            _ => throw new Xunit.Sdk.XunitException($"意外的无主键写入错误类型：{blocked?.GetType().Name}"),
        };
        Assert.Equal(TableConstraintException.SchemaEvolutionUnsupported, code);
        Assert.Equal(1, Execute(connection,
            "ALTER TABLE pending_devices ADD CONSTRAINT pk_pending_devices PRIMARY KEY (id)"));
        Assert.Equal(1, Execute(connection, "ALTER TABLE pending_devices ADD COLUMN version INT ROWVERSION"));
        Assert.Equal(1, Execute(connection, "INSERT INTO pending_devices (code) VALUES ('one')"));

        var columns = connection.GetSchema("Columns", [null, null, "pending_devices", null]);
        var id = columns.Rows.Cast<DataRow>().Single(row => (string)row["COLUMN_NAME"] == "id");
        var version = columns.Rows.Cast<DataRow>().Single(row => (string)row["COLUMN_NAME"] == "version");
        Assert.True((bool)id["IS_PRIMARY_KEY"]);
        Assert.True((bool)id["IS_AUTO_INCREMENT"]);
        Assert.True((bool)version["IS_ROW_VERSION"]);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, version FROM pending_devices WHERE code = 'one'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    private static int Execute(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }
}
