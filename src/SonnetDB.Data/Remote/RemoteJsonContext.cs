using System.Text.Json.Serialization;

namespace SonnetDB.Data.Remote;

/// <summary>
/// 远程客户端使用的 <see cref="System.Text.Json"/> 源生成器上下文。
/// 仅包含发起请求与解析头/尾的 DTO；行数据通过流式 <see cref="System.Text.Json.JsonDocument"/> 解析，
/// 避免与服务端任意标量类型耦合。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SqlRequestBody))]
[JsonSerializable(typeof(SqlBatchRequestBody))]
[JsonSerializable(typeof(ResultMetaLine))]
[JsonSerializable(typeof(ResultEndLine))]
[JsonSerializable(typeof(ServerErrorBody))]
[JsonSerializable(typeof(BulkIngestResponseBody))]
internal sealed partial class RemoteJsonContext : JsonSerializerContext;
