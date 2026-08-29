using SonnetDB.Mcp;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// MCP wire contract 的兼容性测试。
/// </summary>
public sealed class McpContractTests
{
    [Theory]
    [InlineData(null, 5)]
    [InlineData(-1, 5)]
    [InlineData(0, 5)]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    public void NormalizeSearchLimit_LegacyValues_PreservesFallbackAndCap(int? requested, int expected)
    {
        Assert.Equal(expected, SonnetDbMcpTools.NormalizeSearchLimit(requested));
    }
}
