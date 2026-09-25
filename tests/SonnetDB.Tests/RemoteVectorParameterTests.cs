using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// GH-Issue #186：强制 REST/NDJSON 时，VECTOR 参数和 float[] 结果的远程闭环。
/// </summary>
public sealed class RemoteVectorParameterTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private const string AdminToken = "remote-vector-admin";
    private const string DatabaseName = "remote_vector";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-remote-vector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        });
        await _app.StartAsync();

        _baseUrl = (_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。")).Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync(
            "/v1/db",
            new StringContent("{\"name\":\"" + DatabaseName + "\"}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NdjsonRowWriter_WritesFiniteFloatArray_AndRejectsInvalidVector()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            NdjsonRowWriter.WriteValue(writer, new float[] { 1.25f, -0.5f, 3f });
            writer.Flush();
        }

        Assert.Equal("[1.25,-0.5,3]", Encoding.UTF8.GetString(stream.ToArray()));

        using var invalidStream = new MemoryStream();
        using var invalidWriter = new Utf8JsonWriter(invalidStream);
        Assert.Throws<InvalidDataException>(
            () => NdjsonRowWriter.WriteValue(invalidWriter, new float[] { float.NaN }));

        using var emptyStream = new MemoryStream();
        using var emptyWriter = new Utf8JsonWriter(emptyStream);
        Assert.Throws<InvalidDataException>(
            () => NdjsonRowWriter.WriteValue(emptyWriter, Array.Empty<float>()));
    }

    [Fact]
    public void Remote_Rest_VectorParameterAndFloatArray_RoundTrip()
    {
        using var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{DatabaseName};Token={AdminToken};Protocol=rest;Timeout=30");
        connection.Open();

        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))";
            Assert.Equal(0, ddl.ExecuteNonQuery());
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO docs (time, source, embedding) VALUES (1000, @source, @embedding)";
            insert.Parameters.AddWithValue("@source", "a");
            insert.Parameters.AddWithValue("@embedding", new float[] { 1.25f, -0.5f, 3f });
            Assert.Equal(1, insert.ExecuteNonQuery());
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT embedding FROM docs WHERE source = @source";
        select.Parameters.AddWithValue("@source", "a");
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(typeof(float[]), reader.GetFieldType(0));
        Assert.Equal(new float[] { 1.25f, -0.5f, 3f }, Assert.IsType<float[]>(reader.GetValue(0)));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Remote_Rest_VectorUpdateWithoutReplacementContract_RejectsAndPreservesValue()
    {
        using var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{DatabaseName};Token={AdminToken};Protocol=rest;Timeout=30");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))";
            setup.ExecuteNonQuery();
            setup.CommandText = "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])";
            Assert.Equal(1, setup.ExecuteNonQuery());
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE docs SET embedding = @embedding WHERE source = 'a'";
            update.Parameters.AddWithValue("@embedding", new float[] { 0f, 1f, 0f });
            var error = Assert.Throws<SndbServerException>(() => update.ExecuteNonQuery());
            Assert.Equal("sql_error", error.Error);
            Assert.Contains("measurement UPDATE 尚不支持", error.ServerMessage, StringComparison.Ordinal);
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT embedding FROM docs WHERE source = 'a'";
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(new float[] { 1f, 0f, 0f }, Assert.IsType<float[]>(reader.GetValue(0)));
        Assert.False(reader.Read());
    }
}
