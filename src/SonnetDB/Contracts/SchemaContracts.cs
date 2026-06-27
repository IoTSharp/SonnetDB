namespace SonnetDB.Contracts;

/// <summary>数据库 schema 响应（供前端 SQL 自动补全和 AI 系统提示使用）。</summary>
public sealed record SchemaResponse(
    List<MeasurementInfo> Measurements,
    List<TableInfo>? Tables = null,
    List<DocumentCollectionInfo>? DocumentCollections = null,
    List<IndexLifecycleInfo>? Indexes = null,
    BackupStatusInfo? BackupStatus = null);

/// <summary>一个 Measurement 的 schema 信息。</summary>
public sealed record MeasurementInfo(string Name, List<ColumnInfo> Columns);

/// <summary>一列的 schema 信息。</summary>
public sealed record ColumnInfo(
    string Name,
    string Role,
    string DataType,
    int? VectorDimension = null,
    VectorIndexInfo? VectorIndex = null);

/// <summary>向量索引定义摘要。</summary>
public sealed record VectorIndexInfo(string Kind, List<KeyValueInfo> Options);

/// <summary>键值摘要项。</summary>
public sealed record KeyValueInfo(string Key, string Value);

/// <summary>一个关系表的 schema 信息。</summary>
public sealed record TableInfo(
    string Name,
    List<TableColumnInfo> Columns,
    List<string> PrimaryKey,
    List<TableIndexInfo> Indexes,
    DateTimeOffset CreatedUtc);

/// <summary>关系表列信息。</summary>
public sealed record TableColumnInfo(
    string Name,
    string DataType,
    bool IsPrimaryKey,
    bool IsNullable,
    int Ordinal);

/// <summary>关系表二级索引信息。</summary>
public sealed record TableIndexInfo(
    string Name,
    List<string> Columns,
    bool IsUnique,
    DateTimeOffset CreatedUtc,
    bool Rebuildable,
    string? JsonPath = null);

/// <summary>一个 JSON 文档集合的 schema 信息。</summary>
public sealed record DocumentCollectionInfo(
    string Name,
    List<DocumentJsonIndexInfo> JsonIndexes,
    List<DocumentFullTextIndexInfo> FullTextIndexes,
    DateTimeOffset CreatedUtc);

/// <summary>JSON path 索引信息。</summary>
public sealed record DocumentJsonIndexInfo(
    string Name,
    string Path,
    DateTimeOffset CreatedUtc,
    bool Rebuildable,
    List<string>? Paths = null,
    bool IsUnique = false,
    bool IsSparse = false,
    bool IsPartial = false,
    string? PartialFilter = null,
    bool IsTtl = false,
    long? TtlSeconds = null);

/// <summary>文档全文索引信息。</summary>
public sealed record DocumentFullTextIndexInfo(
    string Name,
    List<string> Fields,
    string Tokenizer,
    DateTimeOffset CreatedUtc,
    bool IncludedInBackup,
    bool Rebuildable);

/// <summary>索引生命周期信息。</summary>
public sealed record IndexLifecycleInfo(
    string Id,
    string Model,
    string Owner,
    string Name,
    string Kind,
    string State,
    bool IncludedInBackup,
    bool Rebuildable,
    DateTimeOffset? CreatedUtc,
    List<string> Columns,
    string? Detail = null);

/// <summary>当前数据库备份可观测状态。</summary>
public sealed record BackupStatusInfo(
    bool BackupCapable,
    bool HasRestoreManifest,
    DateTimeOffset? RestoreManifestCreatedUtc,
    int SegmentCount,
    int WalFileCount,
    long TotalBytes,
    long MemTablePointCount,
    long CheckpointLsn,
    long NextSegmentId);

/// <summary>数据库维护请求。</summary>
public sealed record MaintenanceRequest(
    string Operation,
    string? TargetModel = null,
    string? TargetOwner = null,
    string? TargetName = null,
    string? BackupDirectory = null,
    string? RestoreTargetDirectory = null,
    bool Overwrite = false);

/// <summary>数据库维护响应。</summary>
public sealed record MaintenanceResponse(
    string Operation,
    string Status,
    bool Success,
    string Message,
    DateTimeOffset CompletedUtc,
    List<MaintenanceCheckInfo> Checks,
    BackupVerificationInfo? BackupVerification = null,
    RestoreDryRunInfo? RestoreDryRun = null,
    IndexMaintenanceInfo? Index = null,
    QualityAnalysisInfo? QualityAnalysis = null);

/// <summary>维护检查项。</summary>
public sealed record MaintenanceCheckInfo(
    string Name,
    string Status,
    string Message,
    long? Count = null);

/// <summary>备份校验摘要。</summary>
public sealed record BackupVerificationInfo(
    bool IsValid,
    int CheckedFiles,
    List<string> Errors);

/// <summary>恢复 dry-run 摘要。</summary>
public sealed record RestoreDryRunInfo(
    bool IsValid,
    int FileCount,
    long TotalBytes,
    int IndexCount,
    bool TargetDirectoryExists,
    bool TargetDirectoryEmpty);

/// <summary>索引维护操作摘要。</summary>
public sealed record IndexMaintenanceInfo(
    string Model,
    string Owner,
    string Name,
    string Kind,
    string Mode,
    bool Planned,
    bool Rebuildable,
    long? DocumentCount = null);

/// <summary>索引质量分析摘要。</summary>
public sealed record QualityAnalysisInfo(
    int TotalIndexes,
    int RebuildableIndexes,
    int PlannedIndexes,
    int IncludedInBackupIndexes,
    int WarningCount,
    List<QualityIndexInfo> Indexes,
    List<MaintenanceCheckInfo> Issues);

/// <summary>单个索引的质量状态。</summary>
public sealed record QualityIndexInfo(
    string Id,
    string Model,
    string Owner,
    string Name,
    string Kind,
    string State,
    bool IncludedInBackup,
    bool Rebuildable,
    long? DocumentCount = null,
    string? Detail = null);
