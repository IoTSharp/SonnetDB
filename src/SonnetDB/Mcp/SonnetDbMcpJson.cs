using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SonnetDB.Json;

namespace SonnetDB.Mcp;

/// <summary>合并 MCP 协议与 SonnetDB 合同的生成式元数据，不允许反射回退。</summary>
internal static class SonnetDbMcpJson
{
    /// <summary>工具参数、协议返回体和结构化输出 schema 共用的只读配置。</summary>
    internal static JsonSerializerOptions ToolOptions { get; } = CreateToolOptions();

    private static JsonSerializerOptions CreateToolOptions()
    {
        // SDK defaults can also contain reflection resolvers/converters in non-AOT hosts.
        JsonSerializerContext protocolContext = McpJsonUtilities.DefaultOptions.TypeInfoResolverChain
            .OfType<JsonSerializerContext>()
            .FirstOrDefault(context => context.GetTypeInfo(typeof(CallToolResult)) is not null)
            ?? throw new InvalidOperationException("MCP SDK 未提供协议类型的生成式 JSON 元数据。");
        var options = new JsonSerializerOptions(ServerJsonContext.Default.Options)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ServerJsonContext.Default, protocolContext),
        };
        options.MakeReadOnly();
        return options;
    }
}
