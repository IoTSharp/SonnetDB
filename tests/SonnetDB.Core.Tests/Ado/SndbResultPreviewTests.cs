using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using SonnetDB.Data;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Remote;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Core.Tests.Ado;

public sealed class SndbResultPreviewTests
{
    [Fact]
    public void MaterializedPreview_ExposesTruncationWithoutChangingAffectedCount()
    {
        var result = new SelectExecutionResult(["id"], [[1L]]) { Truncated = true };
        using var reader = new SndbDataReader(
            MaterializedExecutionResult.FromSelect(result, recordsAffected: 3),
            CommandBehavior.Default,
            connection: null);

        Assert.True(reader.Truncated);
        Assert.Equal(3, reader.RecordsAffected);
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.False(reader.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemotePreview_EndMarkerExposesTruncation(bool asynchronous)
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"]}
            [1]
            {"type":"end","rowCount":1,"recordsAffected":3,"truncated":true}
            """);

        Assert.False(reader.Truncated);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.True(reader.Truncated);
        Assert.Equal(3, reader.RecordsAffected);
    }

    [Fact]
    public async Task RemoteResult_LegacyCompleteEnd_PreservesCompleteStatus()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"]}
            [1]
            {"type":"end","rowCount":1,"recordsAffected":-1}
            """);

        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.ReadAsync());
        Assert.False(reader.Truncated);
    }

    [Fact]
    public async Task RemoteResult_LegacyMetaWithoutColumnSchema_UsesValueTypeAndNullableFallback()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"]}
            [42]
            {"type":"end","rowCount":1,"recordsAffected":1}
            """);

        Assert.True(await reader.ReadAsync());
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.True((bool)reader.GetSchemaTable().Rows[0][System.Data.Common.SchemaTableColumn.AllowDBNull]);
        Assert.False(await reader.ReadAsync());
        Assert.Equal(1, reader.RecordsAffected);
    }

    [Fact]
    public async Task RemoteResult_ExplicitObjectColumnType_RemainsObjectAcrossRows()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["sum"],"columnTypes":["object"]}
            [1]
            [1.5]
            {"type":"end","rowCount":2,"recordsAffected":-1}
            """);

        Assert.Equal(typeof(object), reader.GetFieldType(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(typeof(object), reader.GetFieldType(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1.5, reader.GetDouble(0));
        Assert.Equal(typeof(object), reader.GetFieldType(0));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task RemoteResult_LegacyMetaWithoutTypes_InfersEachCurrentRow()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["value"]}
            [1]
            [1.5]
            {"type":"end","rowCount":2,"recordsAffected":-1}
            """);

        Assert.Equal(typeof(object), reader.GetFieldType(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(typeof(double), reader.GetFieldType(0));
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"dataType\":\"int64\",\"isNullable\":\"false\",\"isKey\":true,\"isAutoIncrement\":true,\"isRowVersion\":false}]")]
    public async Task RemoteResult_InvalidColumnSchema_RejectsMeta(string schemas)
    {
        var body = $"{{\"type\":\"meta\",\"columns\":[\"id\"],\"columnSchemas\":{schemas}}}\n"
            + "{\"type\":\"end\",\"rowCount\":0,\"recordsAffected\":0}";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteResult_DisconnectAfterPartialRows_RejectsCompletion(bool asynchronous)
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"]}
            [1]
            """);

        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        if (asynchronous)
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync());
        else
            Assert.Throws<InvalidDataException>(() => reader.Read());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RemoteResult_MismatchedEndCount_RejectsCompletion(int reportedCount)
    {
        using var reader = await CreateReaderAsync(
            "{\"type\":\"meta\",\"columns\":[\"id\"]}\n[1]\n"
            + $"{{\"type\":\"end\",\"rowCount\":{reportedCount},\"recordsAffected\":-1}}");

        Assert.True(await reader.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task RemoteResult_CancellationBeforeRead_DoesNotReportSuccess()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"]}
            [1]
            {"type":"end","rowCount":1,"recordsAffected":-1,"truncated":true}
            """);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
        Assert.False(reader.Truncated);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("null")]
    [InlineData("1")]
    public async Task RemoteResult_InvalidTruncatedMarker_RejectsCompletion(string marker)
    {
        using var reader = await CreateReaderAsync(
            "{\"type\":\"meta\",\"columns\":[\"id\"]}\n[1]\n"
            + $"{{\"type\":\"end\",\"rowCount\":1,\"recordsAffected\":-1,\"truncated\":{marker}}}");

        Assert.True(await reader.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task RemoteResult_NonQueryEnd_IsAlreadyComplete()
    {
        using var reader = await CreateReaderAsync(
            "{\"type\":\"end\",\"rowCount\":0,\"recordsAffected\":3}");

        Assert.Equal(3, reader.RecordsAffected);
        Assert.False(await reader.ReadAsync());
        Assert.False(reader.Truncated);
    }

    [Fact]
    public async Task HasRows_DisconnectedRemoteStream_PropagatesIncompleteResult()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"],"columnTypes":["int64"]}
            """);

        Assert.Throws<InvalidDataException>(() => reader.HasRows);
    }

    [Fact]
    public async Task HasRows_RemoteError_PropagatesServerDiagnostic()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"]}
            {"error":"query_budget_exceeded","message":"budget exhausted"}
            """);

        var exception = Assert.Throws<SndbServerException>(() => reader.HasRows);
        Assert.Equal("query_budget_exceeded", exception.Error);
        Assert.Equal("budget exhausted", exception.ServerMessage);
    }

    [Fact]
    public async Task HasRows_BufferedRemoteRow_CallerCancellationDoesNotConsumeIt()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"],"columnTypes":["int64"]}
            [42]
            {"type":"end","rowCount":1,"recordsAffected":-1}
            """);
        Assert.True(reader.HasRows);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.True(reader.HasRows);
    }

    /// <summary>首行探测后的两种 schema 保留 Int64 与未知 Decimal 声明且不消费缓冲行。</summary>
    [Fact]
    public async Task HasRows_NumericSchemaAfterPrefetch_PreservesDeclarationsAndFirstRow()
    {
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id","amount"],"columnSchemas":[{"dataType":"int64","isNullable":false,"isKey":false,"isAutoIncrement":false,"isRowVersion":false,"numericPrecision":null,"numericScale":null},{"dataType":"decimal","isNullable":true,"isKey":false,"isAutoIncrement":false,"isRowVersion":false,"numericPrecision":null,"numericScale":null}]}
            [42,12.5]
            [43,null]
            {"type":"end","rowCount":2,"recordsAffected":-1}
            """);

        AssertNumericDeclarations(reader);
        Assert.True(reader.HasRows);
        AssertNumericDeclarations(reader);
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal(12.5m, reader.GetDecimal(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(43L, reader.GetInt64(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync());

        static void AssertNumericDeclarations(SndbDataReader reader)
        {
            var columns = reader.GetColumnSchema();
            Assert.Equal(typeof(long), columns[0].DataType);
            Assert.Equal(19, columns[0].NumericPrecision);
            Assert.Equal(0, columns[0].NumericScale);
            Assert.Equal(typeof(decimal), columns[1].DataType);
            Assert.Null(columns[1].NumericPrecision);
            Assert.Null(columns[1].NumericScale);
            using var table = reader.GetSchemaTable();
            Assert.Equal((short)19, table.Rows[0][SchemaTableColumn.NumericPrecision]);
            Assert.Equal((short)0, table.Rows[0][SchemaTableColumn.NumericScale]);
            Assert.Equal(DBNull.Value, table.Rows[1][SchemaTableColumn.NumericPrecision]);
            Assert.Equal(DBNull.Value, table.Rows[1][SchemaTableColumn.NumericScale]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HasRows_CommandCancellation_PreservesTokenBeforeAndAfterPrefetch(bool prefetch)
    {
        using var cancellation = new CancellationTokenSource();
        using var reader = await CreateReaderAsync("""
            {"type":"meta","columns":["id"],"columnTypes":["int64"]}
            [42]
            {"type":"end","rowCount":1,"recordsAffected":-1}
            """, cancellation.Token);
        if (prefetch)
            Assert.True(reader.HasRows);
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() => prefetch ? reader.Read() : reader.HasRows);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var asyncException = await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync());
        Assert.Equal(cancellation.Token, asyncException.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
    }

    private static async Task<SndbDataReader> CreateReaderAsync(string body, CancellationToken commandCancellationToken = default)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        return new SndbDataReader(result, CommandBehavior.Default, connection: null,
            commandCancellationToken: commandCancellationToken);
    }
}
