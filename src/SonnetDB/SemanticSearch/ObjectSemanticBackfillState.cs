namespace SonnetDB.SemanticSearch;

/// <summary>Bucket 语义回填的持久游标；每次只推进一页，重启后从该页继续。</summary>
internal sealed record ObjectSemanticBackfillState(
    string Bucket,
    string? ContinuationToken,
    int ScannedObjects,
    int QueuedObjects,
    int SkippedObjects,
    bool Completed,
    DateTimeOffset UpdatedUtc);
