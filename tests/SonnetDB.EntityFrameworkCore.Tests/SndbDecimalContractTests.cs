using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>通过实际 ADO 与真实 Server 验证 #235 的金额、精度、类型和恢复合同。</summary>
public sealed class SndbDecimalContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ado-decimal-" + Guid.NewGuid().ToString("N"));
    private const decimal IssueValue = 123456789.1234567890123456789m;

    /// <summary>嵌入式 ADO 保留参数及原数字文本精度，并实际重开持久化数据库。</summary>
    [Fact]
    public async Task Decimal_Embedded_PreservesExactValuesAndLifecycle()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string connectionString = $"Data Source={_root}";
        await ExerciseAsync(connectionString, deadline.Token);
        await VerifyPersistedAsync(connectionString, deadline.Token);
    }

    /// <summary>真实 REST 与精确 h2c 的 ADO Decimal 合同及宿主停止、释放、重开一致。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decimal_Remote_PreservesExactValuesAndLifecycle(bool frameHttp2)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        const string database = "exact_decimal";
        const string token = "decimal-local-admin";
        var requests = new ConcurrentQueue<(string Path, string Protocol)>();
        var options = new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [token] = ServerRoles.Admin },
        };
        await using (var app = BuildServer(options, frameHttp2, requests))
        {
            try
            {
                await app.StartAsync(deadline.Token);
                Assert.True(app.Services.GetRequiredService<TsdbRegistry>().TryCreate(database, out _));
                await ExerciseAsync(ConnectionString(app, database, token, frameHttp2), deadline.Token);
            }
            finally
            {
                using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(stopping.Token);
            }
        }
        await using (var app = BuildServer(options, frameHttp2, requests))
        {
            try
            {
                await app.StartAsync(deadline.Token);
                await VerifyPersistedAsync(ConnectionString(app, database, token, frameHttp2), deadline.Token);
            }
            finally
            {
                using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(stopping.Token);
            }
        }
        var observed = requests.ToArray();
        Assert.Contains(observed, request => request.Path == $"/v1/db/{database}/sql");
        Assert.All(observed, request => Assert.Equal(frameHttp2 ? "HTTP/2" : "HTTP/1.1", request.Protocol));
        if (frameHttp2)
            Assert.Contains(observed, request => request.Path.Contains("frame", StringComparison.OrdinalIgnoreCase));
    }

    private static WebApplication BuildServer(ServerOptions options, bool frameHttp2, ConcurrentQueue<(string Path, string Protocol)> requests)
    {
        var app = EfTestServerHost.Build(options, extraArgs: frameHttp2 ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
        app.Use(async (context, next) =>
        {
            requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
            await next(context);
        });
        return app;
    }

    private static string ConnectionString(WebApplication app, string database, string token, bool frameHttp2)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 没有监听地址。");
        return $"Data Source=sonnetdb+http://{new Uri(Assert.Single(addresses.Addresses)).Authority}/{database};" +
            $"Token={token};Timeout=15;Protocol={(frameHttp2 ? "frame-http2" : "rest")}";
    }

    private static async Task ExerciseAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "CREATE TABLE amounts (id INT, value DECIMAL(38,28) NULL, PRIMARY KEY (id))", cancellationToken);
        await ExecuteAsync(connection, "CREATE INDEX ix_amounts_value ON amounts (value)", cancellationToken);
        decimal[] values = [IssueValue, -IssueValue, 0.1m, 0.2m, 9007199254740992.125m, 9007199254740993.125m,
            0.1234567890123456789012345678m, decimal.MaxValue, decimal.MinValue];
        for (int index = 0; index < values.Length && index < 16; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteAsync(connection, $"INSERT INTO amounts (id, value) VALUES ({index + 1}, @value)", cancellationToken, values[index]);
            await AssertDecimalAsync(connection, $"SELECT value FROM amounts WHERE id = {index + 1}", values[index], cancellationToken);
            await AssertDecimalAsync(connection, "SELECT @value", values[index], cancellationToken, values[index]);
        }
        await ExecuteAsync(connection, "INSERT INTO amounts (id, value) VALUES (99, @value)", cancellationToken, DBNull.Value);
        await using (var command = Command(connection, "SELECT value FROM amounts WHERE id = 99", cancellationToken))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.True(reader.IsDBNull(0));
            Assert.Equal(typeof(decimal), reader.GetFieldType(0));
        }
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM amounts WHERE value = @value", cancellationToken, DBNull.Value));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM amounts WHERE value = @value", cancellationToken, IssueValue));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM amounts WHERE value = 9007199254740993.125", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM amounts WHERE value > 9007199254740992.125 AND id IN (5,6)", cancellationToken));
        await AssertDecimalAsync(connection, "SELECT value + 0.1 FROM amounts WHERE id = 6", 9007199254740993.225m, cancellationToken);
        await ExecuteAsync(connection, "CREATE TABLE owners (id INT, PRIMARY KEY (id))", cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO owners (id) VALUES (1), (2)", cancellationToken);
        Assert.Equal(2L, await ScalarAsync(connection,
            "SELECT o.id FROM owners o WHERE EXISTS (SELECT 1 FROM amounts a WHERE a.id = o.id + 4 AND a.value = @value)",
            cancellationToken, 9007199254740993.125m));
        await ExecuteAsync(connection,
            "INSERT INTO amounts (id, value) VALUES (20, 123456789.1234567890123456789000), (21, -1.234567891234567890123456789e8)", cancellationToken);
        await AssertDecimalAsync(connection, "SELECT value FROM amounts WHERE id = 20", IssueValue, cancellationToken);
        await AssertDecimalAsync(connection, "SELECT value FROM amounts WHERE id = 21", -IssueValue, cancellationToken);
        await ExecuteAsync(connection, "DELETE FROM amounts WHERE id IN (20,21,99)", cancellationToken);

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await ExecuteAsync(connection, "UPDATE amounts SET value = @value WHERE id = 1", cancellationToken, -IssueValue, transaction);
            await transaction.RollbackAsync(cancellationToken);
        }
        await AssertDecimalAsync(connection, "SELECT value FROM amounts WHERE id = 1", IssueValue, cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await ExecuteAsync(connection, "UPDATE amounts SET value = @value WHERE id = 1", cancellationToken, IssueValue + 1m, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(connection,
            "INSERT INTO amounts (id, value) VALUES (101, 0.1), (102, 1e-29)", cancellationToken));
        Assert.Contains("DECIMAL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9L, await ScalarAsync(connection, "SELECT COUNT(*) FROM amounts", cancellationToken));

        await using (var command = Command(connection, "SELECT @double AS floating, @long AS integral, @single AS small_float", cancellationToken))
        {
            command.Parameters.Add(new SndbParameter("double", 1.25d) { DbType = DbType.Double });
            command.Parameters.Add(new SndbParameter("long", 9007199254740993L) { DbType = DbType.Int64 });
            command.Parameters.Add(new SndbParameter("single", 1.25f) { DbType = DbType.Single });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(1.25d, Assert.IsType<double>(reader.GetValue(0)));
            Assert.Equal(9007199254740993L, Assert.IsType<long>(reader.GetValue(1)));
            Assert.Equal(typeof(double), reader.GetFieldType(0));
            Assert.Equal(typeof(long), reader.GetFieldType(1));
            Assert.Equal(1.25d, Assert.IsType<double>(reader.GetValue(2)));
        }
    }

    private static async Task VerifyPersistedAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await AssertDecimalAsync(connection, "SELECT value FROM amounts WHERE id = 1", IssueValue + 1m, cancellationToken);
        await AssertDecimalAsync(connection, "SELECT value FROM amounts WHERE id = 6", 9007199254740993.125m, cancellationToken);
        Assert.Equal(9L, await ScalarAsync(connection, "SELECT COUNT(*) FROM amounts", cancellationToken));
    }

    private static SndbCommand Command(SndbConnection connection, string sql, CancellationToken cancellationToken, object? value = null, DbTransaction? transaction = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        command.Transaction = transaction is null ? null : Assert.IsType<SndbTransaction>(transaction);
        if (value is not null)
        {
            var parameter = new SndbParameter("value", value) { DbType = DbType.Decimal };
            Assert.Equal(DbType.Decimal, parameter.DbType);
            if (value is not DBNull)
                Assert.IsType<decimal>(parameter.Value);
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static async Task ExecuteAsync(SndbConnection connection, string sql, CancellationToken cancellationToken, object? value = null, DbTransaction? transaction = null)
    {
        await using var command = Command(connection, sql, cancellationToken, value, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SndbConnection connection, string sql, CancellationToken cancellationToken, object? value = null)
    {
        await using var command = Command(connection, sql, cancellationToken, value);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task AssertDecimalAsync(SndbConnection connection, string sql, decimal expected, CancellationToken cancellationToken, object? value = null)
    {
        await using var command = Command(connection, sql, cancellationToken, value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(expected, Assert.IsType<decimal>(reader.GetValue(0)));
        Assert.Equal(expected, reader.GetDecimal(0));
        Assert.Equal(typeof(decimal), reader.GetFieldType(0));
        Assert.False(await reader.ReadAsync(cancellationToken));
    }

    /// <summary>回收本测试独占的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
