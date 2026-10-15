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

/// <summary>通过实际嵌入式 ADO、REST 与 h2c 会话验证 #244 的 INSERT VALUES 合同。</summary>
public sealed class SndbInsertExpressionContractTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "i244a-" + Guid.NewGuid().ToString("N")));
    private const decimal IssueAmount = 123456789.1234567890123456789m;

    /// <summary>嵌入式 ADO 的表达式、事务、触发器与重开后精确值一致。</summary>
    [Fact]
    public async Task Insert_Embedded_PreservesExpressionAndAtomicityContract()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string connectionString = $"Data Source={_root}";
        await ExerciseAsync(connectionString, deadline.Token);
        await VerifyPersistedAsync(connectionString, deadline.Token);
    }

    /// <summary>实际 REST/h2c SQL 会话与嵌入式使用相同语句，且明确核实真实协议路由。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_Remote_PreservesExpressionAndAtomicityContract(bool http2)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        const string database = "insert244";
        const string token = "insert244-local-admin";
        var requests = new ConcurrentQueue<(string Path, string Protocol)>();
        var options = new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [token] = ServerRoles.Admin },
        };
        await using (var app = BuildServer(options, http2, requests))
        {
            try
            {
                await app.StartAsync(deadline.Token);
                Assert.True(app.Services.GetRequiredService<TsdbRegistry>().TryCreate(database, out _));
                await ExerciseAsync(ConnectionString(app, database, token, http2), deadline.Token);
            }
            finally
            {
                using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(stopping.Token);
            }
        }
        await using (var app = BuildServer(options, http2, requests))
        {
            try
            {
                await app.StartAsync(deadline.Token);
                await VerifyPersistedAsync(ConnectionString(app, database, token, http2), deadline.Token);
            }
            finally
            {
                using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(stopping.Token);
            }
        }
        var observed = requests.ToArray();
        Assert.Contains(observed, request => request.Path == $"/v1/db/{database}/sql");
        Assert.All(observed, request => Assert.Equal(http2 ? "HTTP/2" : "HTTP/1.1", request.Protocol));
        if (http2)
            Assert.Contains(observed, request => request.Path.Contains("frame", StringComparison.OrdinalIgnoreCase));
    }

    private static WebApplication BuildServer(ServerOptions options, bool http2, ConcurrentQueue<(string Path, string Protocol)> requests)
    {
        var app = EfTestServerHost.Build(options, extraArgs: http2 ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
        app.Use(async (context, next) =>
        {
            requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
            await next(context);
        });
        return app;
    }

    private static string ConnectionString(WebApplication app, string database, string token, bool http2)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 没有监听地址。");
        return $"Data Source=sonnetdb+http://{new Uri(Assert.Single(addresses.Addresses)).Authority}/{database};"
            + $"Token={token};Timeout=15;Protocol={(http2 ? "frame-http2" : "rest")}";
    }

    private static async Task ExerciseAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection,
            "CREATE TABLE items (id INT AUTO_INCREMENT, amount DECIMAL NULL, label STRING DEFAULT 'fallback', created DATETIME NULL, PRIMARY KEY (id))", cancellationToken);
        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        await using (var command = Command(connection, """
            INSERT INTO items (amount, label, created) VALUES
                (CAST(@amount AS DECIMAL), UPPER('pump'), CURRENT_UTC_DATETIME()),
                (CASE WHEN TRUE THEN CAST(@amount AS DECIMAL) + 0.1 ELSE CAST('bad' AS DECIMAL) END, DEFAULT, NULL)
                RETURNING id, amount, label, created
            """, cancellationToken, amount: IssueAmount))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(IssueAmount, Assert.IsType<decimal>(reader.GetValue(1)));
            Assert.Equal(typeof(decimal), reader.GetFieldType(1));
            Assert.Equal("PUMP", reader.GetString(2));
            Assert.InRange(reader.GetDateTime(3), before, DateTime.UtcNow.AddSeconds(1));
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(IssueAmount + 0.1m, reader.GetDecimal(1));
            Assert.Equal("fallback", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));
            Assert.False(await reader.ReadAsync(cancellationToken));
        }
        await ExecuteAsync(connection, """
            INSERT INTO items (amount) VALUES
                (CASE WHEN TRUE THEN 0.1234567890123456789012345678 ELSE 1e-29 END),
                (COALESCE(NULL, 9007199254740993.125, CAST('bad' AS DECIMAL)))
            """, cancellationToken);
        Assert.Equal(0.1234567890123456789012345678m, await ScalarAsync(connection, "SELECT amount FROM items WHERE id = 3", cancellationToken));
        Assert.Equal(9007199254740993.125m, await ScalarAsync(connection, "SELECT amount FROM items WHERE id = 4", cancellationToken));
        await AssertRejectedAsync(connection,
            "INSERT INTO items (amount) VALUES (0.2 + 0.3), (CAST('bad' AS DECIMAL))", cancellationToken);
        string[] prohibited = ["CASE WHEN TRUE THEN 1 ELSE amount END", "COUNT(*)", "(SELECT 1)"];
        for (int index = 0; index < prohibited.Length && index < 3; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AssertRejectedAsync(connection, $"INSERT INTO items (amount) VALUES ({prohibited[index]})", cancellationToken);
        }
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM items", cancellationToken));

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await ExecuteAsync(connection, "INSERT INTO items (amount) VALUES (CAST('4.5' AS DECIMAL))", cancellationToken, transaction);
            await AssertRejectedAsync(connection,
                "INSERT INTO items (amount) VALUES (6.5), (CAST('bad' AS DECIMAL))", cancellationToken, transaction);
            Assert.Equal(5L, await ScalarAsync(connection, "SELECT COUNT(*) FROM items", cancellationToken, transaction));
            await transaction.CommitAsync(cancellationToken);
        }
        Assert.Equal(5L, await ScalarAsync(connection, "SELECT COUNT(*) FROM items", cancellationToken));
        await ExecuteAsync(connection, "CREATE TABLE audit_rows (id INT, amount DECIMAL, PRIMARY KEY (id))", cancellationToken);
        await ExecuteAsync(connection, """
            CREATE TRIGGER audit_items AFTER INSERT ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit_rows (id, amount) VALUES (NEW.id, NEW.amount);
            END
            """, cancellationToken);
        Assert.Equal(6L, await ScalarAsync(connection,
            "INSERT INTO items (amount) VALUES (CAST(NULL AS DECIMAL)) RETURNING id", cancellationToken));
        await using (var command = Command(connection, "SELECT amount FROM items WHERE id = 6", cancellationToken))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.True(reader.IsDBNull(0));
            Assert.Equal(typeof(decimal), reader.GetFieldType(0));
            Assert.False(await reader.ReadAsync(cancellationToken));
        }
        await AssertRejectedAsync(connection,
            "INSERT INTO items (amount) VALUES (7.0), (CAST('bad' AS DECIMAL))", cancellationToken);
        Assert.Equal(6L, await ScalarAsync(connection, "SELECT COUNT(*) FROM items", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM audit_rows", cancellationToken));
        Assert.Equal(7L, await ScalarAsync(connection, """
            INSERT INTO items (id, amount) VALUES (6, CAST('9.25' AS DECIMAL)), (7, COALESCE(NULL, 9.5))
                ON CONFLICT (id) DO NOTHING RETURNING id
            """, cancellationToken));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM audit_rows", cancellationToken));
    }

    private static async Task VerifyPersistedAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        Assert.Equal(7L, await ScalarAsync(connection, "SELECT COUNT(*) FROM items", cancellationToken));
        Assert.Equal(IssueAmount, await ScalarAsync(connection, "SELECT amount FROM items WHERE id = 1", cancellationToken));
        Assert.Equal(0.1234567890123456789012345678m, await ScalarAsync(connection, "SELECT amount FROM items WHERE id = 3", cancellationToken));
        Assert.Equal(9.5m, await ScalarAsync(connection, "SELECT amount FROM items WHERE id = 7", cancellationToken));
    }

    private static SndbCommand Command(SndbConnection connection, string sql, CancellationToken cancellationToken,
        DbTransaction? transaction = null, decimal? amount = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        command.Transaction = transaction is null ? null : Assert.IsType<SndbTransaction>(transaction);
        if (amount is not null)
            command.Parameters.Add(new SndbParameter("amount", amount.Value) { DbType = DbType.Decimal });
        return command;
    }

    private static async Task ExecuteAsync(SndbConnection connection, string sql, CancellationToken cancellationToken, DbTransaction? transaction = null)
    {
        await using var command = Command(connection, sql, cancellationToken, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SndbConnection connection, string sql, CancellationToken cancellationToken, DbTransaction? transaction = null)
    {
        await using var command = Command(connection, sql, cancellationToken, transaction);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task AssertRejectedAsync(SndbConnection connection, string sql, CancellationToken cancellationToken, DbTransaction? transaction = null)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(connection, sql, cancellationToken, transaction));
        Assert.False(error is OperationCanceledException, error.ToString());
        Assert.False(error is IOException, error.ToString());
        Assert.False(error is TimeoutException, error.ToString());
        Assert.False(cancellationToken.IsCancellationRequested);
    }

    /// <summary>只回收本用例创建的唯一绝对路径。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
