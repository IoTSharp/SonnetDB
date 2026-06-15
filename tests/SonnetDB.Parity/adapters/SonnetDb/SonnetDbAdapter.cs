using SonnetDB.Data;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Adapters.SonnetDb;

/// <summary>
/// SonnetDB 后端适配器：以**嵌入式**模式打开一个临时目录数据库（无需 docker），
/// 把 <see cref="IRelationalOps"/> 翻译成 SonnetDB SQL 方言。
/// </summary>
/// <remarks>
/// 连接字符串为 <c>Data Source={tempDir}</c>，<see cref="DisposeAsync"/> 关闭连接并尽力删除临时目录。
/// </remarks>
public sealed class SonnetDbAdapter : IDataPlane, IRelationalOps
{
    private readonly string _root;
    private readonly SndbConnection _connection;

    /// <summary>创建适配器，在系统临时目录下开辟独立子目录并打开嵌入式连接。</summary>
    public SonnetDbAdapter()
    {
        _root = Path.Combine(Path.GetTempPath(), "sonnetdb-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _connection = new SndbConnection($"Data Source={_root}");
        _connection.Open();
    }

    /// <inheritdoc />
    public Capability Capabilities => Capability.Relational;

    /// <inheritdoc />
    public IRelationalOps Relational => this;

    /// <inheritdoc />
    public async Task EnsureDeviceTableAsync(CancellationToken ct)
    {
        await DropDeviceTableAsync(ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertDevicesAsync(IReadOnlyList<RelationalRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO devices (id, name) VALUES (@id, @name)";
            AddParameter(cmd, "@id", row.Id);
            AddParameter(cmd, "@name", row.Name);
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
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddParameter(SndbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
