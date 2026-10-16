using System.Net;
using System.Text;
using SonnetDB.Data.Remote;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests;

/// <summary>REST 原生时长必须经 v5 确认且使用精确有符号 ticks。</summary>
public sealed class RemoteDurationResponseTests
{
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(972000000001L)]
    [InlineData(long.MaxValue)]
    public async Task NativeDuration_EntireRange_PreservesTimeSpan(long ticks)
    {
        using var response = Response("5");
        using var stream = Payload($"{{\"$type\":\"duration\",\"ticks\":{ticks}}}");
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Equal(typeof(TimeSpan), result.GetFieldType(0));
        Assert.True(result.ReadNextRow());
        Assert.Equal(TimeSpan.FromTicks(ticks), result.GetValue(0));
        Assert.False(result.ReadNextRow());
    }

    [Theory]
    [InlineData("{\"$type\":\"duration\",\"ticks\":9223372036854775808}")]
    [InlineData("{\"$type\":\"duration\",\"ticks\":-9223372036854775809}")]
    [InlineData("{\"$type\":\"duration\",\"ticks\":\"0\"}")]
    [InlineData("{\"$type\":\"duration\",\"ticks\":0.5}")]
    [InlineData("{\"$type\":\"duration\",\"ticks\":0,\"ticks\":1}")]
    [InlineData("{\"$type\":\"duration\",\"ticks\":0,\"extra\":1}")]
    [InlineData("{\"$type\":\"duration\"}")]
    [InlineData("\"01:00:00\"")]
    [InlineData("0")]
    public async Task NativeDuration_InvalidEnvelopes_Reject(string value)
    {
        using var response = Response("5");
        using var stream = Payload(value);
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => result.ReadNextRow());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("4")]
    public async Task DurationDeclaration_WithoutV5Acknowledgement_RejectsBeforeRows(string? version)
    {
        using var response = Response(version);
        using var stream = Payload(null);
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None));
    }

    [Fact]
    public async Task NativeDuration_EmptyAndAllNull_PreserveDeclaration()
    {
        foreach (bool empty in new[] { false, true })
        {
            using var response = Response("5");
            using var stream = Payload(empty ? null : "null");
            using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
            Assert.Equal(typeof(TimeSpan), result.GetFieldType(0));
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
        => new(Encoding.UTF8.GetBytes("{\"type\":\"meta\",\"columns\":[\"value\"],\"columnTypes\":[\"duration\"]}\n"
            + (value is null ? string.Empty : $"[{value}]\n")
            + $"{{\"type\":\"end\",\"rowCount\":{(value is null ? 0 : 1)},\"recordsAffected\":-1}}\n"));
}
