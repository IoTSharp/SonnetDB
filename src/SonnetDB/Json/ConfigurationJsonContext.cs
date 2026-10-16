using System.Text.Json.Serialization;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;

namespace SonnetDB.Json;

/// <summary>配置快照专用 source-generated 元数据；原始选项只在内存脱敏，不能直接返回 HTTP。</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(ServerOptions))]
[JsonSerializable(typeof(AiOptions))]
internal partial class ConfigurationJsonContext : JsonSerializerContext;
