using System.Text.Json.Serialization;
using SonnetDB.Data.Kv;
using SonnetDB.Data.Mq;

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
[JsonSerializable(typeof(KvGetRequest))]
[JsonSerializable(typeof(KvSetRequest))]
[JsonSerializable(typeof(KvDeleteRequest))]
[JsonSerializable(typeof(KvGetManyRequest))]
[JsonSerializable(typeof(KvSetManyRequest))]
[JsonSerializable(typeof(KvSetManyEntry))]
[JsonSerializable(typeof(KvDeleteManyRequest))]
[JsonSerializable(typeof(KvPrefixRequest))]
[JsonSerializable(typeof(KvCleanExpiredRequest))]
[JsonSerializable(typeof(KvIncrementRequest))]
[JsonSerializable(typeof(KvCasRequest))]
[JsonSerializable(typeof(KvExpireRequest))]
[JsonSerializable(typeof(KvValueResponse))]
[JsonSerializable(typeof(KvGetManyResponse))]
[JsonSerializable(typeof(KvValueItemResponse))]
[JsonSerializable(typeof(KvSetResponse))]
[JsonSerializable(typeof(KvSetManyResponse))]
[JsonSerializable(typeof(KvDeleteResponse))]
[JsonSerializable(typeof(KvIncrementResponse))]
[JsonSerializable(typeof(KvCasResponse))]
[JsonSerializable(typeof(KvBooleanResponse))]
[JsonSerializable(typeof(KvTtlResponse))]
[JsonSerializable(typeof(KvScanResponse))]
[JsonSerializable(typeof(KvEntryResponse))]
[JsonSerializable(typeof(List<KvSetManyEntry>))]
[JsonSerializable(typeof(List<KvValueItemResponse>))]
[JsonSerializable(typeof(List<KvEntryResponse>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(KvStatsResponse))]
[JsonSerializable(typeof(MqPublishRequest))]
[JsonSerializable(typeof(MqPublishResponse))]
[JsonSerializable(typeof(MqPullRequest))]
[JsonSerializable(typeof(MqMessageResponse))]
[JsonSerializable(typeof(MqPullResponse))]
[JsonSerializable(typeof(MqAckRequest))]
[JsonSerializable(typeof(MqAckResponse))]
[JsonSerializable(typeof(MqStatsResponse))]
[JsonSerializable(typeof(List<MqMessageResponse>))]
internal sealed partial class RemoteJsonContext : JsonSerializerContext;
