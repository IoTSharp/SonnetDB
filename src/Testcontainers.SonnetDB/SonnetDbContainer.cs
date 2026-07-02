using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Testcontainers.SonnetDB;

/// <summary>
/// SonnetDB Server 的 Testcontainers 容器实例。
/// </summary>
public sealed class SonnetDbContainer : DockerContainer, IDatabaseContainer
{
    private readonly SonnetDbConfiguration _configuration;

    /// <summary>
    /// 使用指定配置创建 SonnetDB 容器实例。
    /// </summary>
    /// <param name="configuration">SonnetDB 容器配置。</param>
    public SonnetDbContainer(SonnetDbConfiguration configuration)
        : base(configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// 获取默认数据库的 ADO.NET 远程连接字符串。
    /// </summary>
    /// <returns>可传给 <c>SonnetDB.Data.SndbConnection</c> 的连接字符串。</returns>
    public string GetConnectionString()
        => GetConnectionString(_configuration.Database ?? SonnetDbBuilder.DefaultDatabase);

    /// <summary>
    /// 获取指定数据库的 ADO.NET 远程连接字符串。
    /// </summary>
    /// <param name="database">目标数据库名。</param>
    /// <returns>可传给 <c>SonnetDB.Data.SndbConnection</c> 的连接字符串。</returns>
    public string GetConnectionString(string database)
    {
        if (string.IsNullOrWhiteSpace(database))
            throw new ArgumentException("数据库名不能为空。", nameof(database));

        var builder = new DbConnectionStringBuilder
        {
            ["Data Source"] = "sonnetdb+" + new Uri(GetBaseAddress(), Uri.EscapeDataString(database)),
            ["Token"] = _configuration.AdminToken ?? SonnetDbBuilder.DefaultAdminToken,
            ["Timeout"] = (_configuration.TimeoutSeconds ?? SonnetDbBuilder.DefaultTimeoutSeconds).ToString(CultureInfo.InvariantCulture),
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// 获取宿主机可访问的 SonnetDB HTTP 基地址。
    /// </summary>
    /// <returns>宿主机可访问的 SonnetDB HTTP 基地址。</returns>
    public Uri GetBaseAddress()
        => new Uri("http://" + Hostname + ":" + GetMappedPublicPort(SonnetDbBuilder.SonnetDbPort).ToString(CultureInfo.InvariantCulture) + "/");

    /// <summary>
    /// 启动容器，等待健康检查通过后创建默认数据库。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示启动过程的任务。</returns>
    public override async Task StartAsync(CancellationToken ct = default)
    {
        await base.StartAsync(ct).ConfigureAwait(false);
        await CreateDatabaseAsync(_configuration.Database ?? SonnetDbBuilder.DefaultDatabase, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 在当前 SonnetDB Server 中创建数据库。数据库已存在时视为成功。
    /// </summary>
    /// <param name="database">数据库名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示创建过程的任务。</returns>
    public async Task CreateDatabaseAsync(string database, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(database))
            throw new ArgumentException("数据库名不能为空。", nameof(database));

        using var client = new HttpClient { BaseAddress = GetBaseAddress() };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _configuration.AdminToken ?? SonnetDbBuilder.DefaultAdminToken);

        using var content = new StringContent(
            "{\"name\":\"" + EscapeJsonString(database) + "\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("v1/db", content, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created)
            return;

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            "创建 SonnetDB 数据库 '" + database + "' 失败："
            + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
            + " "
            + response.ReasonPhrase
            + " / "
            + body);
    }

    private static string EscapeJsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                    break;
            }
        }

        return builder.ToString();
    }
}
