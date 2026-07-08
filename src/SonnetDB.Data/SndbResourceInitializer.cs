using System.Net;
using System.Text;
using System.Text.Json;
using SonnetDB.Data.Remote;

namespace SonnetDB.Data;

/// <summary>
/// 提供 SonnetDB 连接字符串对应资源的轻量准备能力，供各组件在自己的注册或构造阶段调用。
/// </summary>
public static class SndbResourceInitializer
{
    /// <summary>
    /// 同步准备连接字符串对应的嵌入式目录或远程数据库。
    /// </summary>
    /// <param name="connectionString">SonnetDB.Data 连接字符串。</param>
    /// <param name="purpose">调用组件的用途说明，用于错误信息。</param>
    public static void EnsureDatabase(string connectionString, string? purpose = null)
        => EnsureDatabaseAsync(connectionString, purpose).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 异步准备连接字符串对应的嵌入式目录或远程数据库。
    /// </summary>
    /// <param name="connectionString">SonnetDB.Data 连接字符串。</param>
    /// <param name="purpose">调用组件的用途说明，用于错误信息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task EnsureDatabaseAsync(
        string connectionString,
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new SndbConnectionStringBuilder(connectionString);
        if (builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            EnsureEmbeddedDatabase(builder, purpose);
            return;
        }

        await EnsureRemoteDatabaseAsync(builder, purpose, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureEmbeddedDatabase(SndbConnectionStringBuilder builder, string? purpose)
    {
        var dataSource = NormalizeEmbeddedDataSource(builder.DataSource);
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new InvalidOperationException($"SonnetDB{FormatPurpose(purpose)} 缺少 Data Source。");
        }

        Directory.CreateDirectory(Path.GetFullPath(dataSource));
    }

    private static async Task EnsureRemoteDatabaseAsync(
        SndbConnectionStringBuilder builder,
        string? purpose,
        CancellationToken cancellationToken)
    {
        var database = builder.ResolveDatabase();
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException($"SonnetDB{FormatPurpose(purpose)} 远程连接缺少数据库名。");
        }

        using var client = RemoteHttpClientFactory.Create(
            new Uri(builder.ResolveBaseUrl(), UriKind.Absolute),
            builder.Username,
            builder.Password,
            builder.Token,
            TimeSpan.FromSeconds(Math.Max(1, builder.Timeout)));
        if (await RemoteDatabaseExistsAsync(client, database, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        using var content = new StringContent(
            JsonSerializer.Serialize(new { name = database }),
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("v1/db", content, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"SonnetDB{FormatPurpose(purpose)} database '{database}' could not be created. HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}. 请确认连接字符串 Token 具备 admin 建库权限，或提前创建该数据库。");
    }

    private static async Task<bool> RemoteDatabaseExistsAsync(
        HttpClient client,
        string database,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("v1/db", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!TryGetDatabaseArray(document.RootElement, out var databases))
        {
            return false;
        }

        foreach (var item in databases.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), database, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetDatabaseArray(JsonElement root, out JsonElement databases)
    {
        if (root.TryGetProperty("databases", out databases)
            && databases.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.TryGetProperty("Databases", out databases)
            && databases.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        databases = default;
        return false;
    }

    private static string NormalizeEmbeddedDataSource(string dataSource)
    {
        const string prefix = "sonnetdb://";
        return dataSource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? dataSource[prefix.Length..]
            : dataSource;
    }

    private static string FormatPurpose(string? purpose)
        => string.IsNullOrWhiteSpace(purpose) ? string.Empty : $" {purpose}";
}
