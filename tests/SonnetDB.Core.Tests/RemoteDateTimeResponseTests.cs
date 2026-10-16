using System.Net;
using System.Text;
using SonnetDB.Data.Remote;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests;

/// <summary>REST 原生日期解码只在协商后启用，不依赖机器时区或字符串日期猜测。</summary>
public sealed class RemoteDateTimeResponseTests
{
    [Theory]
    [InlineData(0, "datetime")]
    [InlineData(1, "datetime")]
    [InlineData(2, "datetime")]
    [InlineData(0, "object")]
    [InlineData(1, "object")]
    [InlineData(2, "object")]
    public async Task NativeDateTime_RawTicksAndKind_ArePreserved(int kind, string declared)
    {
        long ticks = new DateTime(2024, 2, 29, 0, 0, 0).AddTicks(1).Ticks;
        using var response = Response(nativeDateTime: true);
        using var stream = Payload(declared, $"{{\"$type\":\"datetime\",\"ticks\":{ticks},\"kind\":{kind}}}");
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Equal(declared == "datetime" ? typeof(DateTime) : typeof(object), result.GetFieldType(0));
        Assert.True(await result.ReadNextRowAsync(CancellationToken.None));
        var actual = Assert.IsType<DateTime>(result.GetValue(0));
        Assert.Equal(ticks, actual.Ticks);
        Assert.Equal((DateTimeKind)kind, actual.Kind);
        Assert.False(await result.ReadNextRowAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":-1,\"kind\":0}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":3155378976000000000,\"kind\":0}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":0,\"kind\":3}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":0,\"kind\":-1}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":\"0\",\"kind\":0}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":0.5,\"kind\":0}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":0,\"kind\":0,\"kind\":2}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":0}")]
    [InlineData("\"2024-02-29T00:00:00Z\"")]
    public async Task NativeDateTime_InvalidOrLegacyPayload_Rejects(string value)
    {
        using var response = Response(nativeDateTime: true);
        using var stream = Payload("datetime", value);
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => result.ReadNextRow());
    }

    [Fact]
    public async Task LegacyResponse_WithoutAcknowledgement_PreservesLegacyParsing()
    {
        using var response = Response(nativeDateTime: false);
        using var stream = Payload("datetime", "\"2024-02-29T00:00:00Z\"");
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.True(result.ReadNextRow());
        var actual = Assert.IsType<DateTime>(result.GetValue(0));
        Assert.Equal(new DateTime(2024, 2, 29).Ticks, actual.Ticks);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
    }

    [Fact]
    public async Task NativeDateTime_NullAndEmpty_MetadataPrecedesRows()
    {
        foreach (bool empty in new[] { false, true })
        {
            using var response = Response(nativeDateTime: true);
            using var stream = Payload("datetime", empty ? null : "null");
            using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
            Assert.Equal(typeof(DateTime), result.GetFieldType(0));
            Assert.Equal(!empty, result.ReadNextRow());
            if (!empty) Assert.Null(result.GetValue(0));
        }
    }

    private static HttpResponseMessage Response(bool nativeDateTime)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (nativeDateTime) response.Headers.Add(SqlFrameCodec.ResultVersionHeader, SqlFrameCodec.NativeDateTimeResultVersion);
        return response;
    }

    private static MemoryStream Payload(string type, string? value)
    {
        string json = $"{{\"type\":\"meta\",\"columns\":[\"value\"],\"columnTypes\":[\"{type}\"]}}\n"
            + (value is null ? string.Empty : $"[{value}]\n")
            + $"{{\"type\":\"end\",\"rowCount\":{(value is null ? 0 : 1)},\"recordsAffected\":-1}}\n";
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }
}
