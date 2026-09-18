using SonnetDB.Data.Documents;

namespace SonnetDB.Data.FullText;

/// <summary>全文检索选项。</summary>
/// <param name="PageSize">页大小。</param>
/// <param name="Skip">起始偏移。</param>
/// <param name="Mode">exact 或 fuzzy。</param>
/// <param name="QueryKind">all、any 或 phrase。</param>
/// <param name="Filter">复用 Document 过滤表达式。</param>
/// <param name="Sort">复用 Document 排序字段。</param>
/// <param name="Facets">facet 字段。</param>
/// <param name="Highlight">高亮配置。</param>
/// <param name="ContinuationToken">下一页 token。</param>
public sealed record SndbFullTextSearchOptions(
    int? PageSize = null,
    int Skip = 0,
    string? Mode = null,
    string? QueryKind = null,
    SndbDocumentFilter? Filter = null,
    IReadOnlyList<SndbDocumentSort>? Sort = null,
    IReadOnlyList<SndbFullTextFacetRequest>? Facets = null,
    SndbFullTextHighlightRequest? Highlight = null,
    string? ContinuationToken = null);

/// <summary>全文检索请求的 JSON DTO。</summary>
internal sealed record SndbFullTextSearchRequest(
    string Collection,
    string Index,
    string Field,
    string Query,
    int? PageSize,
    int Skip,
    string? Mode,
    string? QueryKind,
    SndbDocumentFilter? Filter,
    IReadOnlyList<SndbDocumentSort>? Sort,
    IReadOnlyList<SndbFullTextFacetRequest>? Facets,
    SndbFullTextHighlightRequest? Highlight,
    string? ContinuationToken);

/// <summary>全文 facet 请求。</summary>
/// <param name="Field">用于聚合的 JSON path。</param><param name="Limit">最多返回桶数量。</param>
public sealed record SndbFullTextFacetRequest(string Field, int Limit = 10);

/// <summary>全文高亮请求。</summary>
/// <param name="FragmentSize">片段最大字符数。</param><param name="MaxFragments">每条命中最多片段数。</param>
public sealed record SndbFullTextHighlightRequest(int FragmentSize = 160, int MaxFragments = 3);

/// <summary>全文检索分页结果。</summary>
/// <param name="Collection">文档集合名称。</param><param name="Hits">命中列表。</param>
/// <param name="Facets">facet 分布。</param><param name="NextContinuationToken">下一页 token。</param>
/// <param name="HasMore">是否还有下一页。</param><param name="PageSize">实际页大小。</param><param name="TotalHits">命中总数。</param>
public sealed record SndbFullTextSearchResult(
    string Collection,
    IReadOnlyList<SndbFullTextHit> Hits,
    IReadOnlyList<SndbFullTextFacetResult> Facets,
    string? NextContinuationToken,
    bool HasMore,
    int PageSize,
    int TotalHits);

/// <summary>全文检索命中。</summary>
/// <param name="DocumentId">文档 ID。</param><param name="Score">BM25 评分。</param><param name="ScoreMetadata">评分元数据。</param>
/// <param name="MatchedTerms">命中词元。</param><param name="MatchedOffsets">命中偏移。</param><param name="Highlights">高亮片段。</param>
public sealed record SndbFullTextHit(
    string DocumentId,
    double Score,
    SndbFullTextScoreMetadata ScoreMetadata,
    IReadOnlyList<string> MatchedTerms,
    IReadOnlyList<SndbFullTextMatchedOffset> MatchedOffsets,
    IReadOnlyList<string> Highlights);

/// <summary>稳定评分元数据。</summary>
/// <param name="Kind">评分算法。</param><param name="Version">元数据版本。</param><param name="QueryTerms">查询词元。</param>
public sealed record SndbFullTextScoreMetadata(string Kind, int Version, IReadOnlyList<string> QueryTerms);

/// <summary>命中词元的字符偏移。</summary>
/// <param name="Field">来源字段。</param><param name="Term">词元。</param><param name="Start">起始偏移。</param><param name="End">结束偏移。</param>
public sealed record SndbFullTextMatchedOffset(string Field, string Term, int Start, int End);

/// <summary>facet 结果。</summary>
/// <param name="Field">facet 字段。</param><param name="Buckets">桶列表。</param>
public sealed record SndbFullTextFacetResult(string Field, IReadOnlyList<SndbFullTextFacetBucket> Buckets);

/// <summary>facet 桶。</summary>
/// <param name="Value">facet 值。</param><param name="Count">数量。</param>
public sealed record SndbFullTextFacetBucket(string Value, int Count);
