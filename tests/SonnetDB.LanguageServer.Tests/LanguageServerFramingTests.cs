using System.Text;
using SonnetDB.LanguageServer;
using Xunit;

namespace SonnetDB.LanguageServer.Tests;

public sealed class LanguageServerFramingTests
{
    [Fact]
    public void ReadMessage_WithUtf8Payload_UsesByteLength()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"message\":\"诊断\"}");
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        using var input = new MemoryStream([.. header, .. payload]);

        byte[]? actual = LanguageServerFraming.ReadMessage(input);

        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task WriteMessageAsync_WithPayload_WritesLspFrame()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}");
        await using var output = new MemoryStream();

        await LanguageServerFraming.WriteMessageAsync(output, payload);

        string frame = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal($"Content-Length: {payload.Length}\r\n\r\n{Encoding.UTF8.GetString(payload)}", frame);
    }

    [Fact]
    public void ReadMessage_WithoutContentLength_RejectsFrame()
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes("X-Test: 1\r\n\r\n{}"));

        Assert.Throws<InvalidDataException>(() => LanguageServerFraming.ReadMessage(input));
    }
}
