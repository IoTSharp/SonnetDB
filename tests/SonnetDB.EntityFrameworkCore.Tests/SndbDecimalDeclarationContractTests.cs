using System.Collections.Concurrent;
using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>#241：真实 ADO、REST、精确 h2c 的 Decimal 迁移和零 scale 元数据。</summary>
public sealed class SndbDecimalDeclarationContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s241-ado-" + Guid.NewGuid().ToString("N"));

    /// <summary>嵌入式元数据、值和失败后的持久化声明一致。</summary>
    [Fact]
    public async Task Declaration_Embedded_PreservesMetadataAndFailureAtomicity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string connectionString = $"Data Source={_root}";
        await ExerciseAsync(connectionString, deadline.Token);
        await VerifyPersistedAsync(connectionString, deadline.Token);
    }

    /// <summary>实际 Server 按指定协议运行，关闭释放后重新加载同一数据库验证。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declaration_Remote_PreservesMetadataAndFailureAtomicity(bool frameHttp2)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        const string database = "decimal_declarations";
        const string token = "decimal-ddl-local-admin";
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
        Assert.InRange(observed.Length, 1, 128);
        Assert.Contains(observed, request => request.Path.EndsWith("/schema", StringComparison.Ordinal));
        Assert.All(observed, request => Assert.Equal(frameHttp2 ? "HTTP/2" : "HTTP/1.1", request.Protocol));
        if (frameHttp2)
            Assert.Contains(observed, request => request.Path.Contains("frame", StringComparison.OrdinalIgnoreCase));
        else
            Assert.Contains(observed, request => request.Path.EndsWith("/sql", StringComparison.Ordinal));
    }

    private static WebApplication BuildServer(ServerOptions options, bool frameHttp2,
        ConcurrentQueue<(string Path, string Protocol)> requests)
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
        await ExecuteAsync(connection, "CREATE TABLE ledger(id INT, amount DECIMAL(18,4), untouched NUMERIC(9,0), PRIMARY KEY(id))", cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO ledger(id,amount,untouched) VALUES(1,@value,7)", cancellationToken, 1.2300m);
        await ExecuteAsync(connection, "INSERT INTO ledger(id,amount,untouched) VALUES(2,@value,8)", cancellationToken, -999.9900m);
        await ExecuteAsync(connection, "INSERT INTO ledger(id,amount,untouched) VALUES(3,NULL,9)", cancellationToken);
        await ExecuteAsync(connection, "CREATE UNIQUE INDEX ix_amount ON ledger(amount)", cancellationToken);
        await ExecuteAsync(connection, "ALTER TABLE ledger ADD fee DECIMAL(12,2) NOT NULL DEFAULT '1.25'", cancellationToken);
        await ExecuteAsync(connection, "ALTER TABLE ledger ADD whole NUMERIC(12,0) DEFAULT '7.0000'", cancellationToken);
        await ExecuteAsync(connection, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(15,2)", cancellationToken);
        AssertMetadata(connection, "amount", 15, 2);
        await ExecuteAsync(connection, "ALTER TABLE ledger ALTER amount SET DATA TYPE NUMERIC(14,2)", cancellationToken);
        AssertMetadata(connection, "amount", 14, 2);
        await ExecuteAsync(connection, "ALTER TABLE ledger ALTER COLUMN amount DECIMAL(15,2) NULL", cancellationToken);
        AssertMetadata(connection, "amount", 15, 2);
        AssertMetadata(connection, "fee", 12, 2);
        AssertMetadata(connection, "whole", 12, 0);
        AssertMetadata(connection, "untouched", 9, 0);

        // #264 持续写入约束独立实施；本测试用晚到的越界值证实后续 DDL 会预检所有行。
        await ExecuteAsync(connection, "INSERT INTO ledger(id,amount,untouched) VALUES(4,@value,10)", cancellationToken, 1.234m);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(connection,
            "ALTER TABLE ledger ALTER amount TYPE DECIMAL(5,2)", cancellationToken));
        Assert.Contains("DECIMAL", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertMetadata(connection, "amount", 15, 2);
        AssertMetadata(connection, "fee", 12, 2);
        AssertMetadata(connection, "untouched", 9, 0);
        await AssertValuesAsync(connection, cancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(connection,
            "ALTER TABLE ledger ADD broken DECIMAL DEFAULT CAST('1.00000000000000000000000000001' AS DECIMAL)", cancellationToken));
        Assert.Empty(connection.GetSchema("Columns", [null, null, "ledger", "broken"]).Rows.Cast<DataRow>());
        AssertMetadata(connection, "whole", 12, 0);
    }

    private static async Task VerifyPersistedAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        AssertMetadata(connection, "amount", 15, 2);
        AssertMetadata(connection, "fee", 12, 2);
        AssertMetadata(connection, "whole", 12, 0);
        AssertMetadata(connection, "untouched", 9, 0);
        Assert.Empty(connection.GetSchema("Columns", [null, null, "ledger", "broken"]).Rows.Cast<DataRow>());
        await AssertValuesAsync(connection, cancellationToken);
    }

    private static void AssertMetadata(SndbConnection connection, string columnName, short precision, short scale)
    {
        var row = Assert.Single(connection.GetSchema("Columns", [null, null, "ledger", columnName]).Rows.Cast<DataRow>());
        Assert.Equal(precision, Assert.IsType<short>(row["NUMERIC_PRECISION"]));
        Assert.Equal(scale, Assert.IsType<short>(row["NUMERIC_SCALE"]));
    }

    private static async Task AssertValuesAsync(SndbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = "SELECT amount,fee,whole FROM ledger ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        decimal?[] expected = [1.23m, -999.99m, null, 1.234m];
        int index = 0;
        while (index < 4 && await reader.ReadAsync(cancellationToken))
        {
            if (expected[index] is { } value)
                Assert.Equal(value, reader.GetDecimal(0));
            else
                Assert.True(reader.IsDBNull(0));
            Assert.Equal(1.25m, reader.GetDecimal(1));
            Assert.Equal(7m, reader.GetDecimal(2));
            index++;
        }
        Assert.Equal(4, index);
        Assert.False(await reader.ReadAsync(cancellationToken));
        await using var lookup = connection.CreateCommand();
        lookup.CommandTimeout = 15;
        lookup.CommandText = "SELECT id FROM ledger WHERE amount = 1.23";
        Assert.Equal(1L, await lookup.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExecuteAsync(SndbConnection connection, string sql, CancellationToken cancellationToken, decimal? value = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        if (value is { } number)
            command.Parameters.Add(new SndbParameter("value", number) { DbType = DbType.Decimal });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>回收本测试独占的数据目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
