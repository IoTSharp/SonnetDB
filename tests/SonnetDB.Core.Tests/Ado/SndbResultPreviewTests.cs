using System.Data;
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

    private static async Task<SndbDataReader> CreateReaderAsync(string body)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        return new SndbDataReader(result, CommandBehavior.Default, connection: null);
    }
}
