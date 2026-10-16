using System.Text.Json;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>动态 JSON 的同步异步值、精确对象参数与重开在嵌入式及两个真实远程协议一致。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task DynamicJson_RowPathsAndObjectParameters_PersistAcrossHostRestart(string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string embeddedRoot = Path.Combine(_dataRoot!, "dynamic-json-contract");
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await OpenPrimaryKeyMigrationAsync(connection, asynchronous, deadline.Token);
            foreach (string sql in new[]
            {
                "CREATE TABLE JsonRows(Id INT,Payload JSON NULL,PathText STRING NULL,FieldName STRING,Amount DECIMAL(28,12),PRIMARY KEY(Id))",
                """
                INSERT INTO JsonRows(Id,Payload,PathText,FieldName,Amount) VALUES
                (1,'{"name":"one","objects":[{"amount":1234567890123456.123456789012,"nil":null}]}','$.name','amount',DECIMAL '1234567890123456.123456789012'),
                (2,'{"nested":{"value":"two"}}','$.nested.value','amount',DECIMAL '0'),
                (3,NULL,'$.name','amount',DECIMAL '0'),
                (4,'{"name":"four"}',NULL,'amount',DECIMAL '0')
                """,
                "CREATE JSON INDEX IxJsonName ON JsonRows(Payload,'$.name')",
            })
                await ExecutePrimaryKeyMigrationAsync(connection, sql, asynchronous, deadline.Token);
            await VerifyAsync(connection);
        }

        if (protocol != "embedded") await ReopenHostAsync(deadline.Token);
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await OpenPrimaryKeyMigrationAsync(connection, asynchronous, deadline.Token);
            await VerifyAsync(connection);
        }
        AssertPrimaryKeyMigrationTransport(protocol);

        async Task VerifyAsync(SndbConnection connection)
        {
            await AssertPrimaryKeyMigrationRowsAsync(connection,
                "SELECT Id,JSON_VALUE(Payload,PathText) AS selected FROM JsonRows ORDER BY Id",
                [typeof(long), typeof(object)], [[1L, "one"], [2L, "two"], [3L, null], [4L, null]], asynchronous, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(connection,
                "SELECT JSON_CONTAINS(Payload,'$.objects',JSON_OBJECT(FieldName,Amount,'nil',NULL)) FROM JsonRows WHERE Id=1",
                [typeof(object)], [[true]], asynchronous, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(connection,
                "SELECT JSON_OBJECT('value',Id) FROM JsonRows WHERE Id<0", [typeof(string)], [], asynchronous, deadline.Token);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT JSON_CONTAINS(Payload,'$.objects',JSON_OBJECT(@key,@amount)) FROM JsonRows WHERE Id=1";
            command.Parameters.Add(new SndbParameter("key", "amount"));
            var amount = new SndbParameter("amount", 1234567890123456.123456789012m);
            command.Parameters.Add(amount);
            Assert.Equal(true, asynchronous ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar());
            amount.Value = 1234567890123456.123456789013m;
            Assert.Equal(false, asynchronous ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar());

            command.CommandText = "SELECT JSON_OBJECT(@key,@text,'amount',@amount) FROM JsonRows WHERE Id=1";
            command.Parameters.Clear();
            const string escaped = "'\"\\\n中文{\"x\":1}";
            command.Parameters.Add(new SndbParameter("key", "quote'\"key"));
            command.Parameters.Add(new SndbParameter("text", escaped));
            command.Parameters.Add(amount);
            string json = Assert.IsType<string>(asynchronous
                ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar());
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(escaped, parsed.RootElement.GetProperty("quote'\"key").GetString());
            Assert.Equal(1234567890123456.123456789013m, parsed.RootElement.GetProperty("amount").GetDecimal());

            command.CommandText = "SELECT JSON_VALUE(Payload,CONCAT('$','[-1]')) FROM JsonRows WHERE Id=1";
            command.Parameters.Clear();
            var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                if (asynchronous) await command.ExecuteScalarAsync(deadline.Token);
                else command.ExecuteScalar();
            });
            Assert.Contains("JSON path", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
