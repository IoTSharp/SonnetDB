using System.Collections.Concurrent;
using System.Data;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed class SndbDerivedUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb246-ado-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Update_RealProtocols_BatchesOverlayFailureRollbackAndReopen(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        if (protocol == "embedded")
        {
            await ExerciseAsync("Data Source=" + _root, deadline.Token);
            await VerifyAsync("Data Source=" + _root, deadline.Token);
            return;
        }
        bool frame = protocol == "frame-http2";
        var requests = new ConcurrentQueue<(string Path, string Protocol)>();
        var options = new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { ["derived-local-admin"] = ServerRoles.Admin },
        };
        for (int phase = 0; phase < 2; phase++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await using var app = EfTestServerHost.Build(options,
                extraArgs: frame ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
            app.Use(async (context, next) =>
            {
                requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
                await next(context);
            });
            try
            {
                await app.StartAsync(deadline.Token);
                if (phase == 0) Assert.True(app.Services.GetRequiredService<TsdbRegistry>().TryCreate("derived", out _));
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                string cs = $"Data Source=sonnetdb+http://{new Uri(Assert.Single(addresses.Addresses)).Authority}/derived;Token=derived-local-admin;Timeout=60;Protocol={protocol}";
                if (phase == 0) await ExerciseAsync(cs, deadline.Token);
                else await VerifyAsync(cs, deadline.Token);
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(stop.Token);
            }
        }
        Assert.InRange(requests.Count, 1, 100);
        Assert.All(requests, request => Assert.Equal(frame ? "HTTP/2" : "HTTP/1.1", request.Protocol));
        Assert.Contains(requests, request => frame ? request.Path.Contains("frame", StringComparison.OrdinalIgnoreCase)
            : request.Path.EndsWith("/sql", StringComparison.Ordinal));
    }

    private static async Task ExerciseAsync(string cs, CancellationToken cancellation)
    {
        await using var connection = new SndbConnection(cs);
        await connection.OpenAsync(cancellation);
        await ExecuteAsync(connection, "CREATE TABLE typed(id INT,amount DECIMAL,clock TIME,stamp DATETIME,note STRING,PRIMARY KEY(id))", cancellation);
        await ExecuteAsync(connection, "INSERT INTO typed(id) VALUES(1),(2)", cancellation);
        Assert.Equal(2, await ExecuteAsync(connection, """
            UPDATE typed AS t SET amount=s."Money",clock=s."Clock",stamp=s."Stamp",note=s."a""b"
            FROM (VALUES(1,@amount,@clock,@stamp,NULL),(2,NULL,@clock,@stamp,'O''Brien')) AS s(id,"Money","Clock","Stamp","a""b")
            WHERE t.id=s.id
            """, cancellation, parameters:
            [new SndbParameter("amount", 12345678901234567890.12345678m) { DbType = DbType.Decimal },
             new SndbParameter("clock", TimeSpan.FromTicks(123456789)) { DbType = DbType.Time },
             new SndbParameter("stamp", new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc)) { DbType = DbType.DateTime }]));
        await ExecuteAsync(connection, "CREATE TABLE target(id INT,value INT,amount DECIMAL(5,2),PRIMARY KEY(id))", cancellation);
        var insert = new StringBuilder("INSERT INTO target(id,value,amount) VALUES");
        for (int i = 1; i <= 10_000; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (i > 1) insert.Append(',');
            insert.Append('(').Append(i).Append(",0,'1.25')");
        }
        Assert.Equal(10_000, await ExecuteAsync(connection, insert.ToString(), cancellation));
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 90;
            var sql = new StringBuilder("UPDATE target AS t SET value=s.value FROM (VALUES");
            for (int i = 1; i <= 10_000; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (i > 1) sql.Append(',');
                sql.Append("(@id").Append(i).Append(",@v").Append(i).Append(')');
                command.Parameters.Add(new SndbParameter("id" + i, i) { DbType = DbType.Int64 });
                command.Parameters.Add(new SndbParameter("v" + i, i + 6000) { DbType = DbType.Int64 });
            }
            command.CommandText = sql.Append(") AS s(id,value) WHERE t.id=s.id").ToString();
            Assert.Equal(10_000, await command.ExecuteNonQueryAsync(cancellation));
        }
        Assert.Equal(1, await ExecuteAsync(connection,
            "UPDATE target AS t JOIN (SELECT @id AS id,@value AS value) AS s ON t.id=s.id SET value=s.value WHERE t.id=1",
            cancellation, parameters: [new SndbParameter("id", 1L), new SndbParameter("value", 7001L)]));
        await using (var transaction = await connection.BeginTransactionAsync(cancellation))
        {
            var tx = Assert.IsType<SndbTransaction>(transaction);
            await ExecuteAsync(connection, "UPDATE target SET value=8001 WHERE id=1", cancellation, tx);
            await ExecuteAsync(connection, "UPDATE target AS t SET value=t.value+s.value FROM (VALUES(1,2)) AS s(id,value) WHERE t.id=s.id", cancellation, tx);
            await ExecuteAsync(connection, "UPDATE target AS t JOIN (SELECT id,value FROM target WHERE id=1) AS s ON t.id=s.id SET value=s.value+3 WHERE t.id=1", cancellation, tx);
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(connection,
                "UPDATE target AS t SET amount=s.amount FROM (VALUES(1,'9.25'),(2,'1.234')) AS s(id,amount) WHERE t.id=s.id", cancellation, tx));
            await ExecuteAsync(connection, "UPDATE target AS t SET value=t.value+s.value FROM (VALUES(1,1)) AS s(id,value) WHERE t.id=s.id", cancellation, tx);
            await transaction.CommitAsync(cancellation);
        }
        await using (var transaction = await connection.BeginTransactionAsync(cancellation))
        {
            await ExecuteAsync(connection, "UPDATE target AS t SET value=s.value FROM (VALUES(1,9999)) AS s(id,value) WHERE t.id=s.id", cancellation, Assert.IsType<SndbTransaction>(transaction));
            await transaction.RollbackAsync(cancellation);
        }
        await VerifyRowsAsync(connection, cancellation);
        await ExecuteAsync(connection, "CREATE TABLE shadow_target(id INT,value INT,PRIMARY KEY(id))", cancellation);
        await ExecuteAsync(connection, "CREATE TABLE source(id INT,PRIMARY KEY(id))", cancellation);
        await ExecuteAsync(connection, "CREATE TABLE v(id INT,value INT,PRIMARY KEY(id))", cancellation);
        await ExecuteAsync(connection, "CREATE TABLE lookup(id INT,PRIMARY KEY(id))", cancellation);
        await ExecuteAsync(connection, "INSERT INTO shadow_target(id,value) VALUES(1,0)", cancellation);
        await ExecuteAsync(connection, "INSERT INTO source(id) VALUES(1)", cancellation);
        await ExecuteAsync(connection, "INSERT INTO v(id,value) VALUES(1,999)", cancellation);
        await ExecuteAsync(connection, "INSERT INTO lookup(id) VALUES(1)", cancellation);
        Assert.Equal(1, await ExecuteAsync(connection, """
            UPDATE shadow_target AS t SET value=d.value FROM (
                SELECT a.id,v.value FROM source AS a
                JOIN (VALUES(1,50)) AS v(id,value) ON a.id=v.id
                JOIN lookup AS b ON a.id=b.id
            ) AS d WHERE t.id=d.id
            """, cancellation));
        await VerifyAliasShadowAsync(connection, cancellation);
    }

    private static async Task VerifyAsync(string cs, CancellationToken cancellation)
    {
        await using var connection = new SndbConnection(cs);
        await connection.OpenAsync(cancellation);
        await VerifyRowsAsync(connection, cancellation);
        await VerifyAliasShadowAsync(connection, cancellation);
    }

    private static async Task VerifyAliasShadowAsync(SndbConnection connection, CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = "SELECT value FROM shadow_target WHERE id=1";
        Assert.Equal(50L, await command.ExecuteScalarAsync(cancellation));
        command.CommandText = "SELECT value FROM v WHERE id=1";
        Assert.Equal(999L, await command.ExecuteScalarAsync(cancellation));
    }

    private static async Task VerifyRowsAsync(SndbConnection connection, CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "SELECT id,value,amount FROM target ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        for (int i = 1; i <= 10_000; i++)
        {
            Assert.True(await reader.ReadAsync(cancellation));
            Assert.Equal(i, reader.GetInt64(0));
            Assert.Equal(i == 1 ? 8007 : i + 6000, reader.GetInt64(1));
            Assert.Equal(1.25m, reader.GetDecimal(2));
        }
        Assert.False(await reader.ReadAsync(cancellation));
        await reader.DisposeAsync();
        await using var typed = connection.CreateCommand();
        typed.CommandTimeout = 15;
        typed.CommandText = "SELECT amount,clock,stamp,note FROM typed ORDER BY id";
        await using var values = await typed.ExecuteReaderAsync(cancellation);
        Assert.True(await values.ReadAsync(cancellation));
        Assert.Equal(12345678901234567890.12345678m, values.GetDecimal(0));
        Assert.Equal(123456789, Assert.IsType<TimeOnly>(values.GetValue(1)).Ticks);
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(values.GetValue(2)).Kind);
        Assert.Equal(new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc), values.GetDateTime(2));
        Assert.True(values.IsDBNull(3));
        Assert.True(await values.ReadAsync(cancellation));
        Assert.True(values.IsDBNull(0));
        Assert.Equal("O'Brien", values.GetString(3));
        Assert.False(await values.ReadAsync(cancellation));
    }

    private static async Task<int> ExecuteAsync(SndbConnection connection, string sql, CancellationToken cancellation,
        SndbTransaction? tx = null, SndbParameter[]? parameters = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60; command.CommandText = sql; command.Transaction = tx;
        if (parameters is not null) foreach (var parameter in parameters) command.Parameters.Add(parameter);
        return await command.ExecuteNonQueryAsync(cancellation);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
