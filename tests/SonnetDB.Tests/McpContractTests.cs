using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SonnetDB.Auth;
using SonnetDB.Copilot;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Mcp;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// MCP wire contract 的兼容性测试。
/// </summary>
public sealed class McpContractTests
{
    [Fact]
    public void RegisterTools_WithSourceGeneratedMetadata_AdvertisesAllReadOnlySchemas()
    {
        // Use only the production source-generated resolver, even when the test host enables reflection.
        var options = SonnetDbMcpJson.ToolOptions;
        Assert.Null(options.TypeInfoResolver!.GetTypeInfo(typeof(UnregisteredPayload), options));
        var services = new ServiceCollection();
        services.AddSingleton<SonnetDbMcpContextAccessor>();
        services.AddSingleton<TsdbRegistry>();
        services.AddSingleton<GrantsStore>();
        services.AddSingleton<SonnetDbMcpSchemaCache>();
        services.AddSingleton<SonnetDbMcpExplainSqlService>();
        services.AddSingleton<DocsSearchService>();
        services.AddSingleton<SkillSearchService>();
        services.AddSingleton<SkillRegistry>();
        services.AddMcpServer().WithTools<SonnetDbMcpTools>(options);
        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();
        Assert.Equal(9, tools.Length);
        Assert.All(tools, tool =>
        {
            var contract = tool.ProtocolTool;
            Assert.True(contract.Annotations?.ReadOnlyHint);
            Assert.False(contract.Annotations?.DestructiveHint);
            Assert.True(contract.Annotations?.IdempotentHint);
            Assert.False(contract.Annotations?.OpenWorldHint);
            Assert.True(contract.OutputSchema.HasValue);
            var output = contract.OutputSchema.GetValueOrDefault();
            Assert.Equal(JsonValueKind.Object, output.ValueKind);
            Assert.True(output.GetProperty("properties").TryGetProperty("contractVersion", out _));
        });
    }

    [Fact]
    public void ToolOptions_SdkProtocolAndServerPayload_RoundTripWithGeneratedMetadata()
    {
        var options = SonnetDbMcpJson.ToolOptions;
        var typeInfo = Assert.IsAssignableFrom<JsonTypeInfo<CallToolResult>>(
            options.GetTypeInfo(typeof(CallToolResult)));
        var result = SonnetDbMcpResults.Success(
            new McpDatabaseListResult("current", ["current"]),
            ServerJsonContext.Default.McpDatabaseListResult);

        string json = JsonSerializer.Serialize(result, typeInfo);
        var restored = JsonSerializer.Deserialize(json, typeInfo);

        Assert.NotNull(restored);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(restored.Content));
        using var payload = JsonDocument.Parse(text.Text);
        Assert.Equal("1.0", payload.RootElement.GetProperty("contractVersion").GetString());
        Assert.Equal("current", payload.RootElement.GetProperty("currentDatabase").GetString());
        Assert.NotNull(restored.StructuredContent);
        Assert.IsAssignableFrom<JsonTypeInfo<McpDatabaseListResult>>(
            options.GetTypeInfo(typeof(McpDatabaseListResult)));
    }

    [Fact]
    public void ToolOptions_UnregisteredPayload_RejectsMetadataInsteadOfUsingReflection()
    {
        var options = SonnetDbMcpJson.ToolOptions;

        Assert.True(options.IsReadOnly);
        Assert.Null(options.TypeInfoResolver!.GetTypeInfo(typeof(UnregisteredPayload), options));
        Assert.Throws<NotSupportedException>(() => options.GetTypeInfo(typeof(UnregisteredPayload)));
    }

    private sealed record UnregisteredPayload(string Value);

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
