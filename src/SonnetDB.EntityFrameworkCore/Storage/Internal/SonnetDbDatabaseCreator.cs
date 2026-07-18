using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.Data;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// SonnetDB 数据库创建器。嵌入式模式下数据库等价于连接字符串中的数据目录。
/// </summary>
public sealed class SonnetDbDatabaseCreator : RelationalDatabaseCreator
{
    private readonly IRelationalConnection _connection;

    /// <summary>
    /// 创建 SonnetDB 数据库创建器。
    /// </summary>
    /// <param name="dependencies">关系型数据库创建器依赖。</param>
    public SonnetDbDatabaseCreator(RelationalDatabaseCreatorDependencies dependencies)
        : base(dependencies)
    {
        _connection = dependencies.Connection;
    }

    /// <inheritdoc />
    public override bool Exists()
    {
        if (TryGetRemoteEndpoint(out var endpoint))
            return RemoteExistsAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult();

        try
        {
            var connection = _connection.DbConnection;
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed)
            {
                connection.Open();
                connection.Close();
            }

            return true;
        }
        catch (DbException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override void Create()
    {
        if (TryGetRemoteEndpoint(out var endpoint))
        {
            RemoteCreateAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult();
            return;
        }

        var connection = _connection.DbConnection;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            connection.Open();
            connection.Close();
        }
    }

    /// <inheritdoc />
    public override void Delete()
    {
        if (TryGetRemoteEndpoint(out var endpoint))
        {
            RemoteDeleteAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_connection.ConnectionString))
        {
            var connection = _connection.DbConnection;
            var dataSource = connection.DataSource;
            if (!string.IsNullOrWhiteSpace(dataSource) && Directory.Exists(dataSource))
            {
                Directory.Delete(dataSource, recursive: true);
            }
        }
    }

    /// <inheritdoc />
    public override bool HasTables()
    {
        var connection = _connection.DbConnection;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SHOW TABLES";
        using var reader = command.ExecuteReader();
        var hasTables = reader.Read();
        if (wasClosed)
        {
            connection.Close();
        }

        return hasTables;
    }

    /// <inheritdoc />
    public override Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        => TryGetRemoteEndpoint(out var endpoint)
            ? RemoteExistsAsync(endpoint, cancellationToken)
            : Task.FromResult(Exists());

    /// <inheritdoc />
    public override async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetRemoteEndpoint(out var endpoint))
        {
            await RemoteCreateAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return;
        }

        Create();
    }

    /// <inheritdoc />
    public override async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetRemoteEndpoint(out var endpoint))
        {
            await RemoteDeleteAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return;
        }

        Delete();
    }

    /// <inheritdoc />
    public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetRemoteEndpoint(out _))
        {
            var connection = _connection.DbConnection;
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SHOW TABLES";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var hasTables = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }

            return hasTables;
        }

        return HasTables();
    }

    private bool TryGetRemoteEndpoint(out RemoteDatabaseEndpoint endpoint)
    {
        endpoint = default;
        var connectionString = _connection.DbConnection.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = _connection.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        var builder = new SndbConnectionStringBuilder(connectionString);
        if (builder.ResolveMode() != SndbProviderMode.Remote)
            return false;

        var baseUrl = builder.ResolveBaseUrl();
        var database = builder.ResolveDatabase();
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("远程 SonnetDB EF 连接缺少数据库名。");

        endpoint = new RemoteDatabaseEndpoint(
            baseUrl,
            database,
            builder.Username,
            builder.Password,
            builder.Token,
            builder.Timeout,
            builder.ResolveProtocol());
        return true;
    }

    private static async Task<bool> RemoteExistsAsync(
        RemoteDatabaseEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var http = CreateRemoteHttpClient(endpoint);
        using var request = CreateRemoteRequest(
            http,
            HttpMethod.Get,
            $"v1/db/{Uri.EscapeDataString(endpoint.Database)}/schema");
        using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return true;
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;

        throw await BuildRemoteDatabaseExceptionAsync("exists", response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RemoteCreateAsync(
        RemoteDatabaseEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var http = CreateRemoteHttpClient(endpoint);
        using var request = CreateRemoteRequest(
            http,
            HttpMethod.Post,
            "v1/db",
            new StringContent(
                "{\"name\":\"" + EscapeJson(endpoint.Database) + "\"}",
                Encoding.UTF8,
                "application/json"));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildRemoteDatabaseExceptionAsync("create", response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RemoteDeleteAsync(
        RemoteDatabaseEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var http = CreateRemoteHttpClient(endpoint);
        using var request = CreateRemoteRequest(
            http,
            HttpMethod.Delete,
            $"v1/db/{Uri.EscapeDataString(endpoint.Database)}");
        using var response = await http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        if (!response.IsSuccessStatusCode)
            throw await BuildRemoteDatabaseExceptionAsync("delete", response, cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateRemoteHttpClient(RemoteDatabaseEndpoint endpoint)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(endpoint.BaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds),
        };
        if (endpoint.Protocol == SndbTransportProtocol.FrameHttp2)
        {
            http.DefaultRequestVersion = HttpVersion.Version20;
            http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        if (!string.IsNullOrWhiteSpace(endpoint.Username))
        {
            var raw = $"{endpoint.Username}:{endpoint.Password ?? string.Empty}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }
        else if (!string.IsNullOrWhiteSpace(endpoint.Token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        }
        return http;
    }

    private static HttpRequestMessage CreateRemoteRequest(
        HttpClient http,
        HttpMethod method,
        string requestUri,
        HttpContent? content = null)
        => new(method, requestUri)
        {
            Version = http.DefaultRequestVersion,
            VersionPolicy = http.DefaultVersionPolicy,
            Content = content,
        };

    private static async Task<InvalidOperationException> BuildRemoteDatabaseExceptionAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? response.StatusCode.ToString()
            : body;
        return new InvalidOperationException(
            $"远程 SonnetDB 数据库 {operation} 失败：HTTP {(int)response.StatusCode} {response.StatusCode}; {message}");
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private readonly record struct RemoteDatabaseEndpoint(
        string BaseUrl,
        string Database,
        string? Username,
        string? Password,
        string? Token,
        int TimeoutSeconds,
        SndbTransportProtocol Protocol);
}
