using System.Text.Json.Serialization;

namespace SonnetDB.Backup;

/// <summary>
/// SonnetDB 多模型备份 manifest。记录一致性点、数据模型摘要和逐文件校验信息。
/// </summary>
public sealed record BackupManifest(
    int FormatVersion,
    string DatabaseFormat,
    DateTimeOffset CreatedUtc,
    string SourceRoot,
    BackupConsistency Consistency,
    BackupModelSummary Models,
    IReadOnlyList<BackupFileEntry> Files,
    IReadOnlyList<BackupIndexEntry> Indexes)
{
    /// <summary>当前 manifest 格式版本。</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>当前 reader 可读取的最早 manifest 格式版本。</summary>
    public const int MinimumSupportedFormatVersion = 1;

    /// <summary>当前备份与迁移包绑定的数据库格式标识。</summary>
    public const string CurrentDatabaseFormat = "SonnetDB/MM9";

    /// <summary>备份 manifest 文件名。</summary>
    public const string FileName = "sonnetdb.backup.json";
}

/// <summary>备份一致性点信息。</summary>
public sealed record BackupConsistency(
    long CheckpointLsn,
    long NextSegmentId,
    int SegmentCount,
    long TotalBytes);

/// <summary>备份包含的数据模型摘要。</summary>
public sealed record BackupModelSummary(
    IReadOnlyList<BackupMeasurementEntry> Measurements,
    IReadOnlyList<BackupTableEntry> Tables,
    IReadOnlyList<BackupKeyspaceEntry> Keyspaces,
    IReadOnlyList<BackupDocumentCollectionEntry> DocumentCollections)
{
    /// <summary>原生属性图目录摘要；旧版 manifest 中不存在时为空。</summary>
    public BackupGraphCatalogEntry? GraphCatalog { get; init; }

    /// <summary>SQL/PGQ 关系映射图目录摘要；旧版 manifest 中不存在时为空。</summary>
    public BackupPropertyGraphCatalogEntry? PropertyGraphCatalog { get; init; }
}

/// <summary>原生属性图目录摘要。</summary>
public sealed record BackupGraphCatalogEntry(
    long Revision,
    IReadOnlyList<BackupGraphEntry> Graphs);

/// <summary>单个原生属性图的备份摘要。</summary>
public sealed record BackupGraphEntry(
    string Name,
    Guid StorageId,
    int RecordFormatVersion,
    bool CheckpointedDuringBackup,
    IReadOnlyList<BackupGraphIndexEntry> Indexes);

/// <summary>原生属性图内嵌派生索引摘要。</summary>
public sealed record BackupGraphIndexEntry(
    string Kind,
    bool Included,
    bool Rebuildable);

/// <summary>SQL/PGQ 关系映射图目录摘要。</summary>
public sealed record BackupPropertyGraphCatalogEntry(
    long Revision,
    IReadOnlyList<BackupPropertyGraphEntry> Graphs);

/// <summary>单个 SQL/PGQ 关系映射图的完整声明摘要。</summary>
public sealed record BackupPropertyGraphEntry(
    string Name,
    long CreatedAtUtcTicks,
    IReadOnlyList<BackupPropertyGraphVertexTableEntry> VertexTables,
    IReadOnlyList<BackupPropertyGraphEdgeTableEntry> EdgeTables);

/// <summary>关系映射图中的顶点表声明摘要。</summary>
public sealed record BackupPropertyGraphVertexTableEntry(
    string TableName,
    IReadOnlyList<string> KeyColumns,
    string Label,
    IReadOnlyList<string> PropertyColumns);

/// <summary>关系映射图中的边表声明摘要。</summary>
public sealed record BackupPropertyGraphEdgeTableEntry(
    string TableName,
    IReadOnlyList<string> KeyColumns,
    string SourceTable,
    IReadOnlyList<string> SourceColumns,
    IReadOnlyList<string> SourceReferenceColumns,
    string DestinationTable,
    IReadOnlyList<string> DestinationColumns,
    IReadOnlyList<string> DestinationReferenceColumns,
    string Label,
    IReadOnlyList<string> PropertyColumns);

/// <summary>时序 measurement 摘要。</summary>
public sealed record BackupMeasurementEntry(
    string Name,
    int TagColumnCount,
    int FieldColumnCount,
    IReadOnlyList<BackupVectorIndexEntry> VectorIndexes);

/// <summary>关系表摘要。</summary>
public sealed record BackupTableEntry(
    string Name,
    IReadOnlyList<string> PrimaryKey,
    int ColumnCount,
    IReadOnlyList<BackupSecondaryIndexEntry> Indexes);

/// <summary>KV keyspace 摘要。</summary>
public sealed record BackupKeyspaceEntry(
    string Name,
    bool OpenedDuringBackup);

/// <summary>文档集合摘要。</summary>
public sealed record BackupDocumentCollectionEntry(
    string Name,
    int JsonPathIndexCount,
    IReadOnlyList<BackupFullTextIndexEntry> FullTextIndexes);

/// <summary>向量索引摘要。</summary>
public sealed record BackupVectorIndexEntry(
    string Measurement,
    string Field,
    string Kind,
    bool Rebuildable);

/// <summary>关系表二级索引摘要。</summary>
public sealed record BackupSecondaryIndexEntry(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    bool Rebuildable,
    string? JsonPath = null);

/// <summary>全文索引摘要。</summary>
public sealed record BackupFullTextIndexEntry(
    string Name,
    IReadOnlyList<string> Fields,
    string Tokenizer,
    bool Included,
    bool Rebuildable);

/// <summary>文件校验条目。</summary>
public sealed record BackupFileEntry(
    string Path,
    long SizeBytes,
    string Sha256,
    BackupFileKind Kind,
    bool Required);

/// <summary>索引生命周期条目。</summary>
public sealed record BackupIndexEntry(
    string Model,
    string Owner,
    string Name,
    string Kind,
    bool Included,
    bool Rebuildable,
    string? RelativePath);

/// <summary>备份文件类别。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BackupFileKind>))]
public enum BackupFileKind
{
    Catalog = 0,
    Schema = 1,
    Wal = 2,
    Segment = 3,
    Tombstone = 4,
    Kv = 5,
    Table = 6,
    Document = 7,
    FullTextIndex = 8,
    VectorIndex = 9,
    AggregateIndex = 10,
    Other = 11,
    GraphCatalog = 12,
    GraphData = 13,
    PropertyGraphCatalog = 14,
}

/// <summary>备份创建选项。</summary>
public sealed record BackupCreateOptions
{
    /// <summary>备份目标目录。</summary>
    public required string DestinationDirectory { get; init; }

    /// <summary>是否覆盖已有空备份目录。默认 false。</summary>
    public bool Overwrite { get; init; }

    /// <summary>是否把派生全文索引目录也纳入备份。默认 true。</summary>
    public bool IncludeFullTextIndexes { get; init; } = true;
}

/// <summary>恢复选项。</summary>
public sealed record BackupRestoreOptions
{
    /// <summary>备份目录。</summary>
    public required string BackupDirectory { get; init; }

    /// <summary>恢复目标数据库目录。</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>是否允许覆盖已存在但为空的目标目录。</summary>
    public bool Overwrite { get; init; }

    /// <summary>恢复前是否校验备份 manifest。</summary>
    public bool VerifyBeforeRestore { get; init; } = true;
}

/// <summary>校验结果。</summary>
public sealed record BackupVerificationResult(
    bool IsValid,
    int CheckedFiles,
    IReadOnlyList<string> Errors);

/// <summary>恢复 dry-run 结果。</summary>
public sealed record BackupRestoreDryRunResult(
    bool IsValid,
    BackupVerificationResult Verification,
    int FileCount,
    long TotalBytes,
    int IndexCount,
    bool TargetDirectoryExists,
    bool TargetDirectoryEmpty,
    IReadOnlyList<string> Errors);

/// <summary>索引补建结果。</summary>
public sealed record BackupIndexRebuildResult(
    int TotalIndexes,
    int RebuiltIndexes,
    int PlannedIndexes,
    int FailedIndexes,
    IReadOnlyList<BackupIndexRebuildEntry> Entries);

/// <summary>单个索引补建结果。</summary>
public sealed record BackupIndexRebuildEntry(
    string Model,
    string Owner,
    string Name,
    string Kind,
    string Status,
    string Message,
    long? DocumentCount = null);
