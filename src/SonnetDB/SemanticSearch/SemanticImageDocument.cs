namespace SonnetDB.SemanticSearch;

/// <summary>
/// 内部图片目录文档。原始内容位于 Object Bucket，本记录只保存引用、治理元数据和派生向量。
/// </summary>
internal sealed record SemanticImageDocument(
    string Id,
    string ObjectKey,
    string? FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? SourceUri,
    string Profile,
    int Dimensions,
    float[] Embedding,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? ObjectBucket = null,
    string? ObjectVersionId = null,
    string? ThumbnailBucket = null,
    string? ThumbnailKey = null,
    Dictionary<string, string>? Metadata = null,
    Dictionary<string, string>? Tags = null);
