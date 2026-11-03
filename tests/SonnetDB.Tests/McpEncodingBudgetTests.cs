using System.Text.Json;
using SonnetDB.Exceptions;
using SonnetDB.Json;
using SonnetDB.Mcp;
using Xunit;

namespace SonnetDB.Tests;

public sealed class McpEncodingBudgetTests
{
    [Fact]
    public void SourceGeneratedEncoding_ExcessiveResult_RejectsWithoutGrowingBuffer()
    {
        var buffer = new McpBoundedBufferWriter(4096);
        var payload = new McpDatabaseListResult("db", [new string('<', 10000)]);
        Assert.Throws<PayloadLimitExceededException>(() =>
        {
            using var writer = new Utf8JsonWriter(buffer);
            JsonSerializer.Serialize(writer, payload, ServerJsonContext.Default.McpDatabaseListResult);
            writer.Flush();
        });
        Assert.Equal(4096, buffer.Capacity);
        Assert.InRange(buffer.WrittenMemory.Length, 0, 4096);
        Assert.Throws<PayloadLimitExceededException>(() => buffer.GetMemory(int.MaxValue));
        Assert.Equal(4096, buffer.Capacity);
    }
}
