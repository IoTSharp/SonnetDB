namespace SonnetDB.Cli;

internal sealed record DocumentImportReport(
    int SchemaVersion,
    string Operation,
    string Source,
    string SourceFormat,
    string SourceSha256,
    string Collection,
    string Target,
    bool DryRun,
    string Mode,
    bool Ordered,
    int BatchSize,
    string TransactionBoundary,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    long DocumentsRead,
    long DocumentsValidated,
    long DocumentsWritten,
    long Inserted,
    long Matched,
    long Modified,
    int BatchesAttempted,
    int BatchesCommitted,
    int BatchesReplayed,
    bool Resumed,
    bool Success,
    long ErrorCount,
    bool ErrorsTruncated,
    IReadOnlyList<DocumentImportItemError> Errors,
    IReadOnlyList<DocumentImportGap> Gaps,
    IReadOnlyList<DocumentImportIndexSuggestion> IndexSuggestions);

internal sealed record DocumentImportItemError(
    string File,
    long SourceOrdinal,
    string? Id,
    string Code,
    string Message);

internal sealed record DocumentImportGap(
    string Code,
    string Status,
    long Count,
    string Message);

internal sealed record DocumentImportIndexSuggestion(
    string Name,
    IReadOnlyList<string> Paths,
    string Kind,
    bool Supported,
    bool Unique,
    bool Sparse,
    string? TtlPath,
    long? TtlSeconds,
    string Source,
    string? GapReason);

internal sealed record DocumentImportCheckpoint(
    int SchemaVersion,
    string SourceSha256,
    string Target,
    string Collection,
    string Mode,
    bool Ordered,
    int BatchSize,
    long NextDocumentIndex,
    int NextBatchIndex,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<DocumentImportItemError>? Errors = null,
    long? ErrorCount = null);

internal sealed class DocumentImportErrorCollector
{
    private readonly int _sampleLimit;
    private readonly List<DocumentImportItemError> _samples;

    /// <summary>创建带固定样本上限的迁移错误收集器。</summary>
    /// <param name="sampleLimit">机器报告和 checkpoint 保留的最大错误样本数。</param>
    internal DocumentImportErrorCollector(int sampleLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleLimit);
        _sampleLimit = sampleLimit;
        _samples = new List<DocumentImportItemError>(sampleLimit);
    }

    /// <summary>累计错误总数，包括未保留明细的错误。</summary>
    internal long TotalCount { get; private set; }

    /// <summary>有界错误样本。</summary>
    internal IReadOnlyList<DocumentImportItemError> Samples => _samples;

    /// <summary>错误明细是否因样本上限被截断。</summary>
    internal bool IsTruncated => TotalCount > _samples.Count;

    /// <summary>记录一个错误，并在样本达到上限后只增加总数。</summary>
    /// <param name="item">要记录的迁移错误。</param>
    internal void Add(DocumentImportItemError item)
    {
        ArgumentNullException.ThrowIfNull(item);
        TotalCount++;
        if (_samples.Count < _sampleLimit)
            _samples.Add(item);
    }

    /// <summary>恢复 checkpoint 中的累计错误状态。</summary>
    /// <param name="samples">旧 checkpoint 保存的错误明细。</param>
    /// <param name="totalCount">新 checkpoint 保存的累计数；为空时兼容旧格式并使用明细数。</param>
    internal void Restore(IReadOnlyList<DocumentImportItemError>? samples, long? totalCount)
    {
        foreach (var item in samples ?? [])
        {
            if (_samples.Count >= _sampleLimit)
                break;
            _samples.Add(item);
        }

        TotalCount = Math.Max(totalCount ?? _samples.Count, _samples.Count);
    }
}

internal sealed record DocumentImportOptions(
    string InputPath,
    string Collection,
    string Format,
    string Mode,
    bool Ordered,
    int BatchSize,
    string IdPath,
    bool DryRun,
    bool CreateCollection,
    bool Resume,
    bool JsonOutput,
    string? ReportPath,
    string? CheckpointPath,
    string? ConnectionString,
    string? LocalPath,
    string? ProfileName,
    bool UseDefaultProfile,
    string? BaseUrl,
    string? Database,
    string? Token,
    int TimeoutSeconds);

internal sealed record DocumentImportSourceItem(
    string File,
    long SourceOrdinal,
    string Id,
    string Json,
    IReadOnlyList<string> ScalarPaths);

internal sealed record DocumentImportReadResult(
    DocumentImportSourceItem? Item,
    DocumentImportItemError? Error);
