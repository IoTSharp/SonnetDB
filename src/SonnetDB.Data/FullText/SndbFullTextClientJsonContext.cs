using System.Text.Json.Serialization;
using SonnetDB.Data.Documents;

namespace SonnetDB.Data.FullText;

/// <summary>全文客户端的 source-generated JSON 上下文。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SndbFullTextSearchRequest))]
[JsonSerializable(typeof(SndbDocumentFilter))]
[JsonSerializable(typeof(SndbDocumentSort))]
[JsonSerializable(typeof(List<SndbDocumentFilter>))]
[JsonSerializable(typeof(List<SndbDocumentSort>))]
[JsonSerializable(typeof(SndbFullTextFacetRequest))]
[JsonSerializable(typeof(List<SndbFullTextFacetRequest>))]
[JsonSerializable(typeof(SndbFullTextHighlightRequest))]
[JsonSerializable(typeof(SndbFullTextSearchResult))]
[JsonSerializable(typeof(SndbFullTextHit))]
[JsonSerializable(typeof(List<SndbFullTextHit>))]
[JsonSerializable(typeof(SndbFullTextScoreMetadata))]
[JsonSerializable(typeof(SndbFullTextMatchedOffset))]
[JsonSerializable(typeof(List<SndbFullTextMatchedOffset>))]
[JsonSerializable(typeof(SndbFullTextFacetResult))]
[JsonSerializable(typeof(List<SndbFullTextFacetResult>))]
[JsonSerializable(typeof(SndbFullTextFacetBucket))]
[JsonSerializable(typeof(List<SndbFullTextFacetBucket>))]
internal sealed partial class SndbFullTextClientJsonContext : JsonSerializerContext;
