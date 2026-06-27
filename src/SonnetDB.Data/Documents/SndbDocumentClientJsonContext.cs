using System.Text.Json.Serialization;

namespace SonnetDB.Data.Documents;

/// <summary>
/// 文档客户端 HTTP 契约使用的 JSON 源生成上下文。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(DocumentCollectionCreateRequest))]
[JsonSerializable(typeof(DocumentCollectionOperationResponse))]
[JsonSerializable(typeof(DocumentWriteItem))]
[JsonSerializable(typeof(DocumentInsertManyRequest))]
[JsonSerializable(typeof(DocumentFindRequest))]
[JsonSerializable(typeof(DocumentItemResponse))]
[JsonSerializable(typeof(DocumentFindResponse))]
[JsonSerializable(typeof(DocumentFindOneResponse))]
[JsonSerializable(typeof(DocumentUpdateOneRequest))]
[JsonSerializable(typeof(DocumentUpdateManyRequest))]
[JsonSerializable(typeof(DocumentDeleteOneRequest))]
[JsonSerializable(typeof(DocumentDeleteManyRequest))]
[JsonSerializable(typeof(DocumentWriteResponse))]
[JsonSerializable(typeof(DocumentCountRequest))]
[JsonSerializable(typeof(DocumentCountResponse))]
[JsonSerializable(typeof(DocumentDistinctRequest))]
[JsonSerializable(typeof(DocumentDistinctResponse))]
[JsonSerializable(typeof(JsonElementValue))]
[JsonSerializable(typeof(List<DocumentWriteItem>))]
[JsonSerializable(typeof(List<DocumentItemResponse>))]
[JsonSerializable(typeof(List<JsonElementValue>))]
internal sealed partial class SndbDocumentClientJsonContext : JsonSerializerContext;
