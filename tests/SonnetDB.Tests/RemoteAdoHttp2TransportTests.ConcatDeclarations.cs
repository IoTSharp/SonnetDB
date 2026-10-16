using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>真实 REST/h2c 中普通及物化 CONCAT 视图在空、NULL、非空和重开后保留字符串声明。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConcatViewDeclarations_EmptyNullTextAndReopen_UseRealTransport(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string url = protocol == "rest" ? _http11Url : _frameH2Url;
        await using (var connection = new SndbConnection(ConnectionString(url, protocol)))
        {
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            foreach (string sql in new[]
            {
                "CREATE TABLE ConcatItems(id INT,name STRING,PRIMARY KEY(id))",
                "CREATE VIEW ConcatView AS SELECT CONCAT(name,NULL) AS Joined, CONCAT(NULL,NULL) AS AllNull, "
                    + "CONCAT('id=',CAST(id AS STRING)) AS Identifier, CONCAT(CAST(CONCAT(name,'!') AS STRING),'!') AS Nested FROM ConcatItems",
                "CREATE VIEW NestedConcat AS SELECT * FROM ConcatView",
                "CREATE MATERIALIZED VIEW ConcatSnapshot AS SELECT * FROM NestedConcat",
                "REFRESH MATERIALIZED VIEW ConcatSnapshot",
            })
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
            await VerifyConcatViewsAsync(connection, populated: false, deadline.Token);
            command.CommandText = "INSERT INTO ConcatItems(id,name) VALUES(1,NULL),(2,'文字')";
            await command.ExecuteNonQueryAsync(deadline.Token);
            command.CommandText = "REFRESH MATERIALIZED VIEW ConcatSnapshot";
            await command.ExecuteNonQueryAsync(deadline.Token);
            await VerifyConcatViewsAsync(connection, populated: true, deadline.Token);
        }
        await ReopenHostAsync(deadline.Token);
        url = protocol == "rest" ? _http11Url : _frameH2Url;
        await using var reopened = new SndbConnection(ConnectionString(url, protocol));
        await reopened.OpenAsync(deadline.Token);
        await VerifyConcatViewsAsync(reopened, populated: true, deadline.Token);
        string expectedProtocol = protocol == "rest" ? "HTTP/1.1" : "HTTP/2";
        Assert.All(_requests.Where(request => request.Path.StartsWith("/v1/db/", StringComparison.Ordinal)
            || request.Path == "/v1/frame"), request => Assert.Equal(expectedProtocol, request.Protocol));
        if (protocol == "frame-http2")
            Assert.Contains(_requests, request => request.Path == "/v1/frame" && request.ResultVersion == SqlFrameCodec.NativeDateTimeOffsetResultVersion);
    }

    private static async Task VerifyConcatViewsAsync(SndbConnection connection, bool populated, CancellationToken token)
    {
        foreach (string source in new[] { "ConcatView", "NestedConcat", "ConcatSnapshot" })
        {
            foreach (string suffix in new[] { " LIMIT 0", " WHERE 1=0", " ORDER BY Identifier" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM " + source + suffix;
                await using var reader = await command.ExecuteReaderAsync(token);
                AssertRemoteConcatDeclarations(reader);
                if (populated && suffix.StartsWith(" ORDER", StringComparison.Ordinal))
                {
                    foreach (string[] expected in new[] { new[] { "", "", "id=1", "!!" }, new[] { "文字", "", "id=2", "文字!!" } })
                    {
                        Assert.True(await reader.ReadAsync(token));
                        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], reader.GetString(i));
                        AssertRemoteConcatDeclarations(reader);
                    }
                }
                Assert.False(await reader.ReadAsync(token));
                AssertRemoteConcatDeclarations(reader);
            }
        }
    }

    private static void AssertRemoteConcatDeclarations(DbDataReader reader)
    {
        Assert.Equal(["Joined", "AllNull", "Identifier", "Nested"], Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        var columns = reader.GetColumnSchema();
        using var table = reader.GetSchemaTable()!;
        for (int i = 0; i < columns.Count; i++)
        {
            Assert.Equal(typeof(string), reader.GetFieldType(i));
            Assert.Equal("String", reader.GetDataTypeName(i));
            Assert.False(columns[i].AllowDBNull);
            Assert.Null(columns[i].ColumnSize);
            Assert.Null(columns[i].NumericPrecision);
            Assert.Null(columns[i].NumericScale);
            Assert.Equal(false, table.Rows[i][SchemaTableColumn.AllowDBNull]);
            Assert.Equal(-1, table.Rows[i][SchemaTableColumn.ColumnSize]);
        }
    }
}
