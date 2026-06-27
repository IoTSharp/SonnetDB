using System.Text.Json;
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
[JsonSerializable(typeof(SndbDocumentValidator))]
[JsonSerializable(typeof(SndbDocumentValidatorRule))]
[JsonSerializable(typeof(DocumentValidatorResponse))]
[JsonSerializable(typeof(DocumentWriteItem))]
[JsonSerializable(typeof(DocumentInsertManyRequest))]
[JsonSerializable(typeof(DocumentFindRequest))]
[JsonSerializable(typeof(SndbDocumentFilter))]
[JsonSerializable(typeof(SndbDocumentProjection))]
[JsonSerializable(typeof(SndbDocumentSort))]
[JsonSerializable(typeof(SndbDocumentUpdate))]
[JsonSerializable(typeof(DocumentItemResponse))]
[JsonSerializable(typeof(DocumentFindResponse))]
[JsonSerializable(typeof(DocumentFindOneResponse))]
[JsonSerializable(typeof(DocumentUpdateOneRequest))]
[JsonSerializable(typeof(DocumentUpdateManyRequest))]
[JsonSerializable(typeof(DocumentDeleteOneRequest))]
[JsonSerializable(typeof(DocumentDeleteManyRequest))]
[JsonSerializable(typeof(DocumentWriteErrorResponse))]
[JsonSerializable(typeof(DocumentWriteResponse))]
[JsonSerializable(typeof(DocumentCountRequest))]
[JsonSerializable(typeof(DocumentCountResponse))]
[JsonSerializable(typeof(DocumentDistinctRequest))]
[JsonSerializable(typeof(DocumentDistinctResponse))]
[JsonSerializable(typeof(DocumentAggregateRequest))]
[JsonSerializable(typeof(DocumentAggregateResponse))]
[JsonSerializable(typeof(JsonElementValue))]
[JsonSerializable(typeof(SndbDocumentAggregateStage))]
[JsonSerializable(typeof(SndbDocumentAggregateGroup))]
[JsonSerializable(typeof(SndbDocumentAggregateGroupKey))]
[JsonSerializable(typeof(SndbDocumentAggregateAccumulator))]
[JsonSerializable(typeof(SndbDocumentAggregateUnwind))]
[JsonSerializable(typeof(SndbDocumentAggregateDistinct))]
[JsonSerializable(typeof(List<DocumentWriteItem>))]
[JsonSerializable(typeof(List<DocumentWriteErrorResponse>))]
[JsonSerializable(typeof(List<DocumentItemResponse>))]
[JsonSerializable(typeof(List<JsonElementValue>))]
[JsonSerializable(typeof(List<SndbDocumentFilter>))]
[JsonSerializable(typeof(List<SndbDocumentProjection>))]
[JsonSerializable(typeof(List<SndbDocumentSort>))]
[JsonSerializable(typeof(List<SndbDocumentValidatorRule>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<SndbDocumentAggregateStage>))]
[JsonSerializable(typeof(List<SndbDocumentAggregateGroupKey>))]
[JsonSerializable(typeof(List<SndbDocumentAggregateAccumulator>))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(SndbDocumentPage))]
internal sealed partial class SndbDocumentClientJsonContext : JsonSerializerContext;
