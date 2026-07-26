namespace SonnetDB.SemanticSearch;

/// <summary>
/// Bucket 对象派生任务。任务先写入数据库 KV，再进入进程内有界队列。
/// </summary>
internal sealed record SemanticObjectProcessingJob(
    string Id,
    string Bucket,
    string Key,
    string VersionId,
    string ContentType,
    string Operation,
    bool SemanticRequested,
    bool ThumbnailRequested,
    int ThumbnailMaxWidth,
    int ThumbnailMaxHeight,
    int ThumbnailQuality,
    string Profile,
    string Status,
    int Attempts,
    string? Error,
    string? SemanticImageId,
    string? ThumbnailKey,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? NextAttemptUtc);
