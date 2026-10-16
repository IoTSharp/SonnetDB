using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>完整 UInt64 文本在三协议同步/异步入口保持精确 Decimal、错误边界、声明和恢复。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task ParseUInt64_TextContractMetadataAtomicityAndReopen_UseExactDecimal(string protocol, bool useAsync)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string embeddedRoot = Path.Combine(_dataRoot!, "uint64-parse");
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            if (useAsync) await connection.OpenAsync(deadline.Token);
            else connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            var text = new SndbParameter("@text", "0") { DbType = DbType.String };
            command.Parameters.Add(text);
            (string Text, decimal Expected)[] accepted =
            [
                ("0", 0m), ("9007199254740993", 9007199254740993m),
                ("9223372036854775808", 9223372036854775808m),
                ("18446744073709551614", 18446744073709551614m),
                ("18446744073709551615", 18446744073709551615m),
                (" \t+00018446744073709551615\r\n", 18446744073709551615m),
                (new string('0', 1004) + "18446744073709551615", 18446744073709551615m),
            ];
            foreach (var item in accepted)
            {
                text.Value = item.Text;
                command.CommandText = "SELECT parse_uint64(@text) AS parsed";
                await using var reader = await ReadAsync();
                AssertUInt64ParseSchema(reader, 0, false);
                Assert.True(await MoveAsync(reader));
                Assert.Equal(item.Expected, Assert.IsType<decimal>(reader.GetValue(0)));
                Assert.Equal(item.Expected, reader.GetDecimal(0));
                Assert.False(await MoveAsync(reader));
                Assert.Same(item.Text, text.Value);
                Assert.Equal(DbType.String, text.DbType);
            }

            foreach (object? missing in new object?[] { null, DBNull.Value })
            {
                text.Value = missing;
                command.CommandText = "SELECT parse_uint64(@text) AS parsed";
                await using var reader = await ReadAsync();
                AssertUInt64ParseSchema(reader, 0, true);
                Assert.True(await MoveAsync(reader));
                Assert.True(reader.IsDBNull(0));
                Assert.False(await MoveAsync(reader));
                Assert.Same(missing, text.Value);
                Assert.Equal(DbType.String, text.DbType);
            }

            foreach (string invalid in new[]
            {
                "", " ", "+", "-0", "-1", "2.9", "2.0", "1e2", "1,000", "0xFF", "NaN", "Infinity",
                "18446744073709551616", "+ 1", "1 2", "１", "١", "\u00a01", "1\0", new string('0', 1025),
            })
            {
                text.Value = invalid;
                command.CommandText = "SELECT parse_uint64(@text)";
                var failure = await Record.ExceptionAsync(async () => await ScalarAsync());
                Assert.NotNull(failure);
                Assert.Contains("parse_uint64", failure.Message, StringComparison.Ordinal);
                Assert.Same(invalid, text.Value);
                Assert.Equal(DbType.String, text.DbType);
            }

            command.CommandText = "CREATE TABLE parsed_u64(id INT, text STRING NULL, required STRING NOT NULL, value DECIMAL(20,0), PRIMARY KEY(id))";
            await ExecuteAsync();
            text.Value = "18446744073709551615";
            foreach (string sql in new[]
            {
                "SELECT parse_uint64(text),parse_uint64(required),parse_uint64(@text) FROM parsed_u64",
                "SELECT parse_uint64(a.text),parse_uint64(a.required),parse_uint64(@text) FROM parsed_u64 a INNER JOIN parsed_u64 b ON a.id=b.id",
            })
            {
                command.CommandText = sql;
                await using var empty = await ReadAsync();
                AssertUInt64ParseSchema(empty, 0, true);
                AssertUInt64ParseSchema(empty, 1, false);
                AssertUInt64ParseSchema(empty, 2, false);
                Assert.False(await MoveAsync(empty));
            }

            command.Parameters.Clear();
            command.CommandText = "INSERT INTO parsed_u64(id,text,required,value) VALUES"
                + "(1,'18446744073709551615','18446744073709551615',parse_uint64('18446744073709551615')),"
                + "(2,'9223372036854775808','9223372036854775808',parse_uint64('9223372036854775808')),"
                + "(3,NULL,'0',parse_uint64(NULL))";
            Assert.Equal(3, await ExecuteAsync());
            command.CommandText = "SELECT parse_uint64(text) FROM parsed_u64 WHERE text IS NULL";
            await using (var allNull = await ReadAsync())
            {
                AssertUInt64ParseSchema(allNull, 0, true);
                Assert.True(await MoveAsync(allNull));
                Assert.True(allNull.IsDBNull(0));
                Assert.False(await MoveAsync(allNull));
            }

            command.CommandText = "INSERT INTO parsed_u64(id,text,required,value) VALUES(4,'1','1',parse_uint64('1')),(5,'2.9','2.9',parse_uint64('2.9'))";
            var failedInsert = await Record.ExceptionAsync(async () => await ExecuteAsync());
            Assert.NotNull(failedInsert);
            Assert.Contains("parse_uint64", failedInsert.Message, StringComparison.Ordinal);
            command.CommandText = "SELECT COUNT(*) FROM parsed_u64";
            Assert.Equal(3L, await ScalarAsync());
            command.CommandText = "INSERT INTO parsed_u64(id,text,required,value) VALUES(4,'2.9','0',44)";
            Assert.Equal(1, await ExecuteAsync());
            command.CommandText = "UPDATE parsed_u64 SET value=parse_uint64(text)+DECIMAL '1'";
            var failedUpdate = await Record.ExceptionAsync(async () => await ExecuteAsync());
            Assert.NotNull(failedUpdate);
            Assert.Contains("parse_uint64", failedUpdate.Message, StringComparison.Ordinal);
            command.CommandText = "SELECT value FROM parsed_u64 ORDER BY id";
            await using (var unchanged = await ReadAsync())
            {
                foreach (decimal? expected in new decimal?[] { 18446744073709551615m, 9223372036854775808m, null, 44m })
                {
                    Assert.True(await MoveAsync(unchanged));
                    if (expected is null) Assert.True(unchanged.IsDBNull(0));
                    else Assert.Equal(expected.Value, unchanged.GetDecimal(0));
                }
                Assert.False(await MoveAsync(unchanged));
            }
            command.CommandText = "UPDATE parsed_u64 SET text='0' WHERE id=4";
            Assert.Equal(1, await ExecuteAsync());
            command.CommandText = "UPDATE parsed_u64 SET value=parse_uint64(text)";
            Assert.Equal(4, await ExecuteAsync());
            command.Parameters.Add(text);
            text.Value = "18446744073709551615";
            command.CommandText = "SELECT id FROM parsed_u64 WHERE value=parse_uint64(@text)";
            Assert.Equal(1L, await ScalarAsync());

            using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO parsed_u64(id,text,required,value) VALUES(5,@text,@text,parse_uint64(@text))";
                Assert.Equal(1, await ExecuteAsync());
                command.CommandText = "UPDATE parsed_u64 SET value=parse_uint64('0') WHERE id=1";
                Assert.Equal(1, await ExecuteAsync());
                command.CommandText = "DELETE FROM parsed_u64 WHERE id=2";
                Assert.Equal(1, await ExecuteAsync());
                transaction.Rollback();
                command.Transaction = null;
            }

            async Task<DbDataReader> ReadAsync()
                => useAsync ? await command.ExecuteReaderAsync(deadline.Token) : command.ExecuteReader();
            async Task<bool> MoveAsync(DbDataReader reader)
                => useAsync ? await reader.ReadAsync(deadline.Token) : reader.Read();
            async Task<object?> ScalarAsync()
                => useAsync ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar();
            async Task<int> ExecuteAsync()
                => useAsync ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery();
        }

        if (protocol != "embedded") await ReopenHostAsync(deadline.Token);
        await using (var reopened = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await reopened.OpenAsync(deadline.Token);
            await using var query = reopened.CreateCommand();
            query.CommandText = "SELECT value,parse_uint64(text) FROM parsed_u64 ORDER BY id";
            await using var reader = await query.ExecuteReaderAsync(deadline.Token);
            AssertUInt64ParseSchema(reader, 1, true);
            foreach (decimal? expected in new decimal?[] { 18446744073709551615m, 9223372036854775808m, null, 0m })
            {
                Assert.True(await reader.ReadAsync(deadline.Token));
                if (expected is null)
                {
                    Assert.True(reader.IsDBNull(0));
                    Assert.True(reader.IsDBNull(1));
                }
                else
                {
                    Assert.Equal(expected.Value, reader.GetDecimal(0));
                    Assert.Equal(expected.Value, reader.GetDecimal(1));
                }
            }
            Assert.False(await reader.ReadAsync(deadline.Token));
        }

        if (protocol != "embedded")
        {
            var requests = _requests.Where(request => request.Path == $"/v1/db/{DatabaseName}/sql" || request.Path == "/v1/frame").ToArray();
            Assert.NotEmpty(requests);
            Assert.All(requests, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
            Assert.Contains(requests, request => request.Path == $"/v1/db/{DatabaseName}/sql");
            if (protocol == "frame-http2") Assert.Contains(requests, request => request.Path == "/v1/frame");
        }
    }

    private static void AssertUInt64ParseSchema(DbDataReader reader, int ordinal, bool nullable)
    {
        Assert.Equal(typeof(decimal), reader.GetFieldType(ordinal));
        Assert.Equal("Decimal", reader.GetDataTypeName(ordinal));
        var column = reader.GetColumnSchema()[ordinal];
        Assert.Equal(typeof(decimal), column.DataType);
        Assert.Equal("Decimal", column.DataTypeName);
        Assert.Equal(20, column.NumericPrecision);
        Assert.Equal(0, column.NumericScale);
        Assert.Equal(nullable, column.AllowDBNull);
        Assert.False(column.IsKey);
        var schema = reader.GetSchemaTable()!;
        Assert.Equal(typeof(decimal), schema.Rows[ordinal][SchemaTableColumn.DataType]);
        Assert.Equal((short)20, schema.Rows[ordinal][SchemaTableColumn.NumericPrecision]);
        Assert.Equal((short)0, schema.Rows[ordinal][SchemaTableColumn.NumericScale]);
        Assert.Equal(nullable, schema.Rows[ordinal][SchemaTableColumn.AllowDBNull]);
    }
}
