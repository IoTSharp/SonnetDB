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
[JsonSerializable(typeof(SndbFullTextSettingsRequest))]
[JsonSerializable(typeof(SndbFullTextIndexSettings))]
[JsonSerializable(typeof(SndbFullTextTypoPolicy))]
[JsonSerializable(typeof(SndbFullTextAnalyzerDiffRequest))]
[JsonSerializable(typeof(SndbFullTextAnalyzerDiff))]
[JsonSerializable(typeof(SndbFullTextAnalyzerToken))]
[JsonSerializable(typeof(List<SndbFullTextAnalyzerToken>))]
[JsonSerializable(typeof(SndbFullTextRelevanceExplainRequest))]
[JsonSerializable(typeof(SndbFullTextRelevanceExplanation))]
[JsonSerializable(typeof(SndbFullTextTermContribution))]
[JsonSerializable(typeof(List<SndbFullTextTermContribution>))]
[JsonSerializable(typeof(SndbFullTextRebuildRequest))]
[JsonSerializable(typeof(SndbFullTextRebuildStatus))]
internal sealed partial class SndbFullTextClientJsonContext : JsonSerializerContext;
