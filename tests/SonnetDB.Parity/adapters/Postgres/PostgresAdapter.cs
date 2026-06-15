using System.Net.Sockets;
using Npgsql;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Adapters.Postgres;

/// <summary>
/// PostgreSQL 后端适配器（竞品），通过官方 <c>Npgsql</c> 客户端连接由
/// <c>docker-compose.parity.yml</c> 启动的 Postgres 服务，把 <see cref="IRelationalOps"/>
/// 翻译成 Postgres 方言（<c>BIGINT</c> / <c>TEXT</c>）。
/// </summary>
/// <remarks>
/// 连接参数来自 <c>PARITY_PG_*</c> 环境变量，默认值与 <c>.env</c> 的端口偏移方案一致
/// （host=127.0.0.1, port=25432）。服务不可达时由 <see cref="TryConnectAsync"/> 快速探测，
/// 上层 runner 将场景标记为 SKIPPED 而非 FAIL。
/// </remarks>
public sealed class PostgresAdapter : IDataPlane, IRelationalOps
{
    private readonly NpgsqlConnection _connection;

    /// <summary>使用 <c>PARITY_PG_*</c> 环境变量打开 Postgres 连接（需 <see cref="OpenAsync"/>）。</summary>
    public PostgresAdapter()
    {
        _connection = new NpgsqlConnection(BuildConnectionString());
    }

    /// <inheritdoc />
    public Capability Capabilities => Capability.Relational;

    /// <inheritdoc />
    public IRelationalOps Relational => this;

    /// <summary>打开底层连接。</summary>
    /// <param name="ct">取消令牌。</param>
    public Task OpenAsync(CancellationToken ct) => _connection.OpenAsync(ct);

    /// <summary>
    /// 快速探测 Postgres 是否可达：用 3 秒超时尝试打开一个连接，失败即返回 false。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可达返回 true；网络 / 超时 / 协议错误返回 false。</returns>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            await using var probe = new NpgsqlConnection(BuildConnectionString());
            await probe.OpenAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException or TimeoutException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task EnsureDeviceTableAsync(CancellationToken ct)
    {
        await ExecuteAsync("DROP TABLE IF EXISTS devices", ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE TABLE devices (id BIGINT PRIMARY KEY, name TEXT NOT NULL)", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertDevicesAsync(IReadOnlyList<RelationalRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO devices (id, name) VALUES (@id, @name)";
            cmd.Parameters.AddWithValue("@id", row.Id);
            cmd.Parameters.AddWithValue("@name", row.Name);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationalRow>> SelectDevicesOrderByIdAsync(CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM devices ORDER BY id";
        var rows = new List<RelationalRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new RelationalRow(reader.GetInt64(0), reader.GetString(1)));
        return rows;
    }

    /// <inheritdoc />
    public Task DropDeviceTableAsync(CancellationToken ct)
        => ExecuteAsync("DROP TABLE IF EXISTS devices", ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort close */ }
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildConnectionString()
    {
        var host = Env("PARITY_PG_HOST", "127.0.0.1");
        var port = Env("PARITY_PG_PORT", "25432");
        var user = Env("PARITY_PG_USER", "parity");
        var password = Env("PARITY_PG_PASSWORD", "parity");
        var database = Env("PARITY_PG_DB", "parity");
        return $"Host={host};Port={port};Username={user};Password={password};Database={database};" +
               "Timeout=3;Command Timeout=10;Pooling=false";
    }

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
