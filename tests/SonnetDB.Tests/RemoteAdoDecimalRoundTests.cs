using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>真实嵌入式、REST 与 h2c 的同步/异步参数、关系表达式和零行声明保持精确 Decimal。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task DecimalRound_ParametersColumnsAndEmptyMetadata_PreservePrecision(string protocol, bool useAsync)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string embeddedRoot = Path.Combine(_dataRoot!, "decimal-round-embedded");
        string connectionString = IntegralConnectionString(protocol, embeddedRoot);
        await using (var connection = new SndbConnection(connectionString))
        {
            if (useAsync) await connection.OpenAsync(deadline.Token);
            else connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            var value = new SndbParameter("@value", 0m) { DbType = DbType.Decimal };
            var digits = new SndbParameter("@digits", 0) { DbType = DbType.Int32 };
            command.Parameters.Add(value);
            command.Parameters.Add(digits);
            (decimal Value, int Digits, decimal Expected)[] cases =
            [
                (9007199254740993.125m, 2, 9007199254740993.12m),
                (-9007199254740993.125m, 2, -9007199254740993.12m),
                (1.015m, 2, 1.02m), (-1.015m, 2, -1.02m),
                (2.5m, 0, 2m), (3.5m, 0, 4m), (-2.5m, 0, -2m), (-3.5m, 0, -4m),
                (1.1234567890123456789012345678m, 20, 1.12345678901234567890m),
                (1.1234567890123456789012345678m, 28, 1.1234567890123456789012345678m),
                (decimal.MaxValue, 0, decimal.MaxValue), (decimal.MinValue, 28, decimal.MinValue),
            ];
            foreach (var item in cases)
            {
                value.Value = item.Value;
                digits.Value = item.Digits;
                command.CommandText = "SELECT round(@value,@digits) AS rounded, round(CAST(@value AS DECIMAL),@digits) AS cast_rounded";
                await using (var reader = await ReadAsync())
                {
                    AssertDecimalRoundSchema(reader, 0, false);
                    AssertDecimalRoundSchema(reader, 1, false);
                    Assert.True(await MoveAsync(reader));
                    Assert.Equal(item.Expected, Assert.IsType<decimal>(reader.GetValue(0)));
                    Assert.Equal(item.Expected, reader.GetDecimal(1));
                    Assert.False(await MoveAsync(reader));
                }
                if (item.Digits == 0)
                {
                    command.CommandText = "SELECT round(@value)";
                    Assert.Equal(item.Expected, Assert.IsType<decimal>(await ScalarAsync()));
                }
            }

            command.Parameters.Clear();
            command.CommandText = "CREATE TABLE rounded_ledger(id INT, amount DECIMAL, declared DECIMAL(28,8) NULL, digits INT, PRIMARY KEY(id))";
            await ExecuteAsync();
            command.CommandText = "SELECT round(declared,2) AS rounded, round(amount) AS whole FROM rounded_ledger";
            await using (var reader = await ReadAsync())
            {
                AssertDecimalRoundSchema(reader, 0, true);
                AssertDecimalRoundSchema(reader, 1, true);
                Assert.False(await MoveAsync(reader));
            }

            command.Parameters.Add(value);
            command.Parameters.Add(digits);
            value.Value = 9007199254740993.125m;
            digits.Value = 2;
            command.CommandText = "INSERT INTO rounded_ledger(id,amount,declared,digits) VALUES(1,@value,@value,@digits)";
            Assert.Equal(1, await ExecuteAsync());
            value.Value = 1.015m;
            digits.Value = 29;
            command.CommandText = "INSERT INTO rounded_ledger(id,amount,declared,digits) VALUES(2,@value,@value,@digits)";
            Assert.Equal(1, await ExecuteAsync());
            value.Value = -1.015m;
            digits.Value = 2;
            command.CommandText = "INSERT INTO rounded_ledger(id,amount,declared,digits) VALUES(3,@value,@value,@digits)";
            Assert.Equal(1, await ExecuteAsync());

            value.Value = 9007199254740993.12m;
            command.CommandText = "SELECT round(amount,@digits), round(declared,@digits), round(amount + DECIMAL '0.1',@digits) FROM rounded_ledger WHERE round(amount,@digits)=@value";
            await using (var reader = await ReadAsync())
            {
                AssertDecimalRoundSchema(reader, 0, true);
                AssertDecimalRoundSchema(reader, 1, true);
                Assert.True(await MoveAsync(reader));
                Assert.Equal(9007199254740993.12m, reader.GetDecimal(0));
                Assert.Equal(9007199254740993.12m, reader.GetDecimal(1));
                Assert.Equal(9007199254740993.22m, reader.GetDecimal(2));
                Assert.False(await MoveAsync(reader));
            }

            command.Parameters.Clear();
            command.CommandText = "SELECT round(declared,2) FROM rounded_ledger WHERE id=99";
            await using (var reader = await ReadAsync())
            {
                AssertDecimalRoundSchema(reader, 0, true);
                Assert.False(await MoveAsync(reader));
            }

            command.CommandText = "UPDATE rounded_ledger SET amount=round(amount,digits) WHERE id>=1";
            var invalidUpdate = await Record.ExceptionAsync(async () => await ExecuteAsync());
            Assert.NotNull(invalidUpdate);
            Assert.Contains("0..28", invalidUpdate.Message, StringComparison.Ordinal);
            command.CommandText = "SELECT amount FROM rounded_ledger WHERE id=1";
            Assert.Equal(9007199254740993.125m, Assert.IsType<decimal>(await ScalarAsync()));

            command.CommandText = "UPDATE rounded_ledger SET amount=round(amount,2) WHERE id>=1";
            Assert.Equal(3, await ExecuteAsync());
            command.CommandText = "UPDATE rounded_ledger SET declared=round(DECIMAL '1.015',2) WHERE id=1";
            Assert.Equal(1, await ExecuteAsync());
            command.CommandText = "SELECT declared FROM rounded_ledger WHERE id=1";
            Assert.Equal(1.02m, Assert.IsType<decimal>(await ScalarAsync()));

            command.Parameters.Add(value);
            command.Parameters.Add(digits);
            value.Value = 1.015m;
            digits.DbType = DbType.Decimal;
            foreach (decimal invalidDigits in new[] { -1m, 29m, 1.5m, -0.5m, 28.1m })
            {
                digits.Value = invalidDigits;
                command.CommandText = "SELECT round(@value,@digits)";
                var invalid = await Record.ExceptionAsync(async () => await ScalarAsync());
                Assert.NotNull(invalid);
                Assert.Contains("digits", invalid.Message, StringComparison.Ordinal);
            }

            command.Parameters.Clear();
            command.CommandText = "SELECT round(1.015,2), round(3), round(3.5), round(-2.5)";
            await using (var reader = await ReadAsync())
            {
                Assert.Equal(typeof(double), reader.GetFieldType(0));
                Assert.Equal(typeof(double), reader.GetFieldType(1));
                Assert.True(await MoveAsync(reader));
                Assert.Equal(1.01d, reader.GetDouble(0));
                Assert.Equal(3d, reader.GetDouble(1));
                Assert.Equal(4d, reader.GetDouble(2));
                Assert.Equal(-2d, reader.GetDouble(3));
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

        await using (var reopened = new SndbConnection(connectionString))
        {
            await reopened.OpenAsync(deadline.Token);
            await using var query = reopened.CreateCommand();
            query.CommandText = "SELECT amount FROM rounded_ledger ORDER BY id";
            await using var reader = await query.ExecuteReaderAsync(deadline.Token);
            foreach (decimal expected in new[] { 9007199254740993.12m, 1.02m, -1.02m })
            {
                Assert.True(await reader.ReadAsync(deadline.Token));
                Assert.Equal(expected, reader.GetDecimal(0));
            }
            Assert.False(await reader.ReadAsync(deadline.Token));
        }

        if (protocol != "embedded")
        {
            var requests = _requests.Where(request =>
                request.Path == $"/v1/db/{DatabaseName}/sql" || request.Path == "/v1/frame").ToArray();
            Assert.NotEmpty(requests);
            Assert.All(requests, request => Assert.Equal(
                protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
            Assert.Contains(requests, request => request.Path == $"/v1/db/{DatabaseName}/sql");
            if (protocol == "frame-http2")
                Assert.Contains(requests, request => request.Path == "/v1/frame");
        }
    }

    private static void AssertDecimalRoundSchema(DbDataReader reader, int ordinal, bool nullable)
    {
        Assert.Equal(typeof(decimal), reader.GetFieldType(ordinal));
        Assert.Equal("Decimal", reader.GetDataTypeName(ordinal));
        var column = reader.GetColumnSchema()[ordinal];
        Assert.Equal(typeof(decimal), column.DataType);
        Assert.Equal("Decimal", column.DataTypeName);
        Assert.Equal(nullable, column.AllowDBNull);
        Assert.Null(column.NumericPrecision);
        Assert.Null(column.NumericScale);
        Assert.False(column.IsKey);
        var schema = Assert.IsType<DataTable>(reader.GetSchemaTable());
        Assert.Equal(typeof(decimal), schema.Rows[ordinal][SchemaTableColumn.DataType]);
        Assert.Equal(DBNull.Value, schema.Rows[ordinal][SchemaTableColumn.NumericPrecision]);
        Assert.Equal(DBNull.Value, schema.Rows[ordinal][SchemaTableColumn.NumericScale]);
    }
}
