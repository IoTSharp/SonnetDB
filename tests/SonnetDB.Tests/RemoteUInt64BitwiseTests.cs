using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>原生 CLR UInt64 参数仍受 Int64 绑定上限限制，完整值域明确使用 Decimal 参数。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task UInt64Bitwise_RawUInt64ParameterAboveInt64_PreservesExistingRejection(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol,
            Path.Combine(_dataRoot!, "uint64-bits-raw")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        var parameter = new SndbParameter("@value", (ulong)long.MaxValue) { DbType = DbType.UInt64 };
        command.Parameters.Add(parameter);
        command.CommandText = "SELECT bit_and_uint64(@value,7)";
        Assert.Equal(7m, await command.ExecuteScalarAsync(deadline.Token));
        object highValue = ulong.MaxValue;
        parameter.Value = highValue;
        var failure = await Record.ExceptionAsync(async () => await command.ExecuteScalarAsync(deadline.Token));
        Assert.NotNull(failure);
        Assert.Same(highValue, parameter.Value);
        Assert.Equal(DbType.UInt64, parameter.DbType);
        parameter.Value = (decimal)ulong.MaxValue;
        parameter.DbType = DbType.Decimal;
        Assert.Equal(7m, await command.ExecuteScalarAsync(deadline.Token));
    }

    /// <summary>完整 UInt64 按位与/或在实际三协议、同步/异步、参数/列和恢复入口保持精确。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task UInt64Bitwise_ExactValuesMetadataAtomicityAndReopen_AgreeAcrossProtocols(string protocol, bool useAsync)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        string embeddedRoot = Path.Combine(_dataRoot!, "uint64-bits");
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            if (useAsync) await connection.OpenAsync(deadline.Token);
            else connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            var left = new SndbParameter("@left", 0m) { DbType = DbType.Decimal };
            var right = new SndbParameter("@right", 0m) { DbType = DbType.Decimal };
            command.Parameters.Add(left);
            command.Parameters.Add(right);
            ulong[] values = [0, 7, 9007199254740993, long.MaxValue, 1UL << 63, ulong.MaxValue];
            ulong[] masks = [0, 7, long.MaxValue, 1UL << 63, ulong.MaxValue];
            foreach (ulong value in values)
                foreach (ulong mask in masks)
                {
                    object leftValue = (decimal)value;
                    object rightValue = (decimal)mask;
                    left.Value = leftValue;
                    right.Value = rightValue;
                    command.CommandText = "SELECT bit_and_uint64(@left,@right),bit_or_uint64(@left,@right)";
                    await using var reader = await ReadAsync();
                    AssertUInt64BitwiseSchema(reader, 0, false);
                    AssertUInt64BitwiseSchema(reader, 1, false);
                    Assert.True(await MoveAsync(reader));
                    Assert.Equal((decimal)(value & mask), Assert.IsType<decimal>(reader.GetValue(0)));
                    Assert.Equal((decimal)(value | mask), Assert.IsType<decimal>(reader.GetValue(1)));
                    Assert.False(await MoveAsync(reader));
                    Assert.Same(leftValue, left.Value);
                    Assert.Same(rightValue, right.Value);
                    Assert.Equal(DbType.Decimal, left.DbType);
                }

            foreach (object? missing in new object?[] { null, DBNull.Value })
            {
                left.Value = missing;
                right.Value = (decimal)ulong.MaxValue;
                command.CommandText = "SELECT bit_and_uint64(@left,@right),bit_or_uint64(@right,@left)";
                await using var reader = await ReadAsync();
                AssertUInt64BitwiseSchema(reader, 0, true);
                AssertUInt64BitwiseSchema(reader, 1, true);
                Assert.True(await MoveAsync(reader));
                Assert.True(reader.IsDBNull(0));
                Assert.True(reader.IsDBNull(1));
                Assert.Same(missing, left.Value);
            }
            command.Parameters.Clear();
            foreach (string operand in new[] { "DECIMAL '-1'", "DECIMAL '0.5'", "DECIMAL '18446744073709551616'", "7.0", "'7'", "TRUE" })
                foreach (string function in new[] { "bit_and_uint64", "bit_or_uint64" })
                {
                    command.CommandText = $"SELECT {function}({operand},1)";
                    var failure = await Record.ExceptionAsync(async () => await ScalarAsync());
                    Assert.NotNull(failure);
                    Assert.Contains(function, failure.Message, StringComparison.Ordinal);
                }
            command.CommandText = "SELECT bit_and_uint64(parse_uint64('18446744073709551615'),parse_uint64('9223372036854775808'))";
            Assert.Equal(9223372036854775808m, await ScalarAsync());

            command.CommandText = "CREATE TABLE bit_rows(id INT, left_value DECIMAL(29,1) NULL, right_value DECIMAL(29,1) NOT NULL, result DECIMAL(20,0), PRIMARY KEY(id))";
            await ExecuteAsync();
            foreach (string sql in new[]
            {
                "SELECT bit_and_uint64(left_value,7),bit_or_uint64(right_value,7) FROM bit_rows",
                "SELECT bit_and_uint64(a.left_value,b.right_value),bit_or_uint64(a.right_value,b.right_value) FROM bit_rows a INNER JOIN bit_rows b ON a.id=b.id",
            })
            {
                command.CommandText = sql;
                await using var empty = await ReadAsync();
                AssertUInt64BitwiseSchema(empty, 0, true);
                AssertUInt64BitwiseSchema(empty, 1, false);
                Assert.False(await MoveAsync(empty));
            }
            command.CommandText = "INSERT INTO bit_rows(id,left_value,right_value,result) VALUES"
                + "(1,DECIMAL '18446744073709551615',DECIMAL '9223372036854775808',bit_and_uint64(DECIMAL '18446744073709551615',DECIMAL '9223372036854775808')),"
                + "(2,DECIMAL '9223372036854775808',7,bit_or_uint64(DECIMAL '9223372036854775808',7)),"
                + "(3,NULL,1,bit_and_uint64(NULL,1))";
            Assert.Equal(3, await ExecuteAsync());
            command.CommandText = "SELECT bit_and_uint64(left_value,right_value),bit_or_uint64(left_value,right_value) FROM bit_rows WHERE left_value IS NULL";
            await using (var allNull = await ReadAsync())
            {
                AssertUInt64BitwiseSchema(allNull, 0, true);
                AssertUInt64BitwiseSchema(allNull, 1, true);
                Assert.True(await MoveAsync(allNull));
                Assert.True(allNull.IsDBNull(0));
                Assert.True(allNull.IsDBNull(1));
            }
            command.CommandText = "INSERT INTO bit_rows(id,left_value,right_value,result) VALUES(4,1,1,bit_or_uint64(1,1)),(5,1,1,bit_and_uint64(DECIMAL '1.5',1))";
            Assert.NotNull(await Record.ExceptionAsync(async () => await ExecuteAsync()));
            command.CommandText = "SELECT COUNT(*) FROM bit_rows";
            Assert.Equal(3L, await ScalarAsync());
            command.CommandText = "INSERT INTO bit_rows(id,left_value,right_value,result) VALUES(4,-1,1,44)";
            Assert.Equal(1, await ExecuteAsync());
            command.CommandText = "UPDATE bit_rows SET result=bit_or_uint64(left_value,right_value)";
            var failedUpdate = await Record.ExceptionAsync(async () => await ExecuteAsync());
            Assert.NotNull(failedUpdate);
            Assert.Contains("bit_or_uint64", failedUpdate.Message, StringComparison.Ordinal);
            command.CommandText = "SELECT result FROM bit_rows ORDER BY id";
            await using (var unchanged = await ReadAsync())
            {
                foreach (decimal? expected in new decimal?[] { 9223372036854775808m, 9223372036854775815m, null, 44m })
                {
                    Assert.True(await MoveAsync(unchanged));
                    if (expected is null) Assert.True(unchanged.IsDBNull(0));
                    else Assert.Equal(expected.Value, unchanged.GetDecimal(0));
                }
                Assert.False(await MoveAsync(unchanged));
            }
            command.CommandText = "UPDATE bit_rows SET left_value=0 WHERE id=4";
            Assert.Equal(1, await ExecuteAsync());
            command.CommandText = "UPDATE bit_rows SET result=bit_or_uint64(left_value,right_value)";
            Assert.Equal(4, await ExecuteAsync());
            command.CommandText = "SELECT id FROM bit_rows WHERE bit_and_uint64(result,DECIMAL '9223372036854775808')=DECIMAL '9223372036854775808' ORDER BY id";
            await using (var filter = await ReadAsync())
            {
                Assert.True(await MoveAsync(filter));
                Assert.Equal(1L, filter.GetInt64(0));
                Assert.True(await MoveAsync(filter));
                Assert.Equal(2L, filter.GetInt64(0));
                Assert.False(await MoveAsync(filter));
            }
            using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE bit_rows SET result=bit_and_uint64(result,0) WHERE id=1";
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
            await using var command = reopened.CreateCommand();
            command.CommandText = "SELECT result,bit_or_uint64(left_value,right_value) FROM bit_rows ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            AssertUInt64BitwiseSchema(reader, 1, true);
            foreach (decimal? expected in new decimal?[] { 18446744073709551615m, 9223372036854775815m, null, 1m })
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

    private static void AssertUInt64BitwiseSchema(DbDataReader reader, int ordinal, bool nullable)
    {
        Assert.Equal(typeof(decimal), reader.GetFieldType(ordinal));
        var column = reader.GetColumnSchema()[ordinal];
        Assert.Equal(typeof(decimal), column.DataType);
        Assert.Equal(20, column.NumericPrecision);
        Assert.Equal(0, column.NumericScale);
        Assert.Equal(nullable, column.AllowDBNull);
        Assert.False(column.IsKey);
        Assert.False(column.IsAutoIncrement);
        var classic = reader.GetSchemaTable()!.Rows[ordinal];
        Assert.Equal(typeof(decimal), classic[SchemaTableColumn.DataType]);
        Assert.Equal((short)20, classic[SchemaTableColumn.NumericPrecision]);
        Assert.Equal((short)0, classic[SchemaTableColumn.NumericScale]);
        Assert.Equal(nullable, classic[SchemaTableColumn.AllowDBNull]);
    }
}
