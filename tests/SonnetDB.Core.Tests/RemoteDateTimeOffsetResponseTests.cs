using System.Net;
using System.Text;
using SonnetDB.Data.Remote;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests;

/// <summary>REST 原生时刻必须经 v6 确认且使用精确 UTC ticks。</summary>
public sealed class RemoteDateTimeOffsetResponseTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(639270596961230001L)]
    [InlineData(3155378975999999999L)]
    public async Task NativeDateTimeOffset_EntireRange_PreservesUtcTicks(long ticks)
    {
        using var response = Response("6");
        using var stream = Payload($"{{\"$type\":\"datetimeoffset\",\"ticks\":{ticks}}}");
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Equal(typeof(DateTimeOffset), result.GetFieldType(0));
        Assert.True(result.ReadNextRow());
        Assert.Equal(new DateTimeOffset(ticks, TimeSpan.Zero), result.GetValue(0));
        Assert.False(result.ReadNextRow());
    }

    [Theory]
    [InlineData("{\"$type\":\"datetimeoffset\",\"ticks\":3155378976000000000}")]
    [InlineData("{\"$type\":\"datetimeoffset\",\"ticks\":-1}")]
    [InlineData("{\"$type\":\"datetimeoffset\",\"ticks\":\"0\"}")]
    [InlineData("{\"$type\":\"datetimeoffset\",\"ticks\":0.5}")]
    [InlineData("{\"$type\":\"datetimeoffset\",\"ticks\":0,\"ticks\":1}")]
    [InlineData("{\"$type\":\"datetimeoffset\",\"ticks\":0,\"extra\":1}")]
    [InlineData("{\"$type\":\"datetimeoffset\"}")]
    [InlineData("{\"$type\":\"duration\",\"ticks\":0}")]
    [InlineData("\"2026-10-08T12:34:56.1230001Z\"")]
    [InlineData("0")]
    public async Task NativeDateTimeOffset_InvalidEnvelopes_Reject(string value)
    {
        using var response = Response("6");
        using var stream = Payload(value);
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => result.ReadNextRow());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("4")]
    [InlineData("5")]
    public async Task NativeDeclaration_WithoutV6Acknowledgement_RejectsBeforeRows(string? version)
    {
        using var response = Response(version);
        using var stream = Payload(null);
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None));
    }

    [Fact]
    public async Task NativeDateTimeOffset_EmptyAndAllNull_PreserveDeclaration()
    {
        foreach (bool empty in new[] { false, true })
        {
            using var response = Response("6");
            using var stream = Payload(empty ? null : "null");
            using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
            Assert.Equal(typeof(DateTimeOffset), result.GetFieldType(0));
            Assert.Equal(!empty, result.ReadNextRow());
            if (!empty) Assert.Null(result.GetValue(0));
        }
    }

    private static HttpResponseMessage Response(string? version)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (version is not null) response.Headers.Add(SqlFrameCodec.ResultVersionHeader, version);
        return response;
    }

    private static MemoryStream Payload(string? value)
        => new(Encoding.UTF8.GetBytes("{\"type\":\"meta\",\"columns\":[\"value\"],\"columnTypes\":[\"datetimeoffset\"]}\n"
            + (value is null ? string.Empty : $"[{value}]\n")
            + $"{{\"type\":\"end\",\"rowCount\":{(value is null ? 0 : 1)},\"recordsAffected\":-1}}\n"));
}
