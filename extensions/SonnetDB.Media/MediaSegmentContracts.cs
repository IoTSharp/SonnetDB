using System.Text.Json.Serialization;
using SonnetDB.SemanticContent;

namespace SonnetDB.Media;

/// <summary>媒体分段查询；时间范围采用毫秒半开区间，并匹配相交片段。</summary>
public sealed record MediaSegmentQuery
{
    /// <summary>创建默认查询。</summary>
    public MediaSegmentQuery() { }

    /// <summary>可选的 transcript/OCR 子串，采用不区分大小写的序数匹配。</summary>
    public string? Text { get; init; }

    /// <summary>查询起点，包含该时刻。</summary>
    public long FromMs { get; init; }

    /// <summary>查询终点，不包含该时刻。</summary>
    public long ToMs { get; init; } = long.MaxValue;

    /// <summary>是否仅返回有关键帧引用的片段。</summary>
    public bool KeyFramesOnly { get; init; }

    /// <summary>最多返回的片段数，范围为 1 至 1000。</summary>
    public int Limit { get; init; } = 100;
}

/// <summary>包含原对象和时间定位的媒体命中。</summary>
/// <param name="ContentId">内容稳定标识。</param>
/// <param name="Source">业务来源。</param>
/// <param name="ObjectRef">原始媒体的固定版本引用。</param>
/// <param name="Segment">包含 timecode、transcript 和可选关键帧引用的片段。</param>
public sealed record MediaSegmentHit(
    string ContentId,
    string? Source,
    SemanticObjectReference ObjectRef,
    SemanticContentSegment Segment);

/// <summary>媒体查询结果；不存在清单时查询返回 null。</summary>
/// <param name="IsStale">原对象或命中的关键帧已替换或删除，禁止交付陈旧引用。</param>
/// <param name="Hits">按起始时间、序号和稳定标识排序的命中。</param>
/// <param name="HasMore">是否有超出结果预算的其他匹配。</param>
public sealed record MediaSegmentQueryResult(
    bool IsStale,
    IReadOnlyList<MediaSegmentHit> Hits,
    bool HasMore);

/// <summary>媒体扩展的公开 AOT JSON 合同，调用方应使用此上下文而非反射重载。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SemanticContentManifest))]
[JsonSerializable(typeof(SemanticContentSegment[]))]
[JsonSerializable(typeof(MediaSegmentQuery))]
[JsonSerializable(typeof(MediaSegmentQueryResult))]
public sealed partial class MediaSegmentJsonContext : JsonSerializerContext;
