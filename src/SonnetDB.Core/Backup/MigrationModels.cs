namespace SonnetDB.Backup;

/// <summary>
/// 通用迁移包导出选项。
/// </summary>
public sealed record MigrationExportOptions
{
    /// <summary>迁移包输出目录。</summary>
    public required string PackageDirectory { get; init; }

    /// <summary>是否允许使用已存在但为空的输出目录。</summary>
    public bool Overwrite { get; init; }

    /// <summary>是否包含可从主数据重建的全文索引。</summary>
    public bool IncludeFullTextIndexes { get; init; } = true;
}

/// <summary>
/// 通用迁移包导入选项。
/// </summary>
public sealed record MigrationImportOptions
{
    /// <summary>迁移包目录。</summary>
    public required string PackageDirectory { get; init; }

    /// <summary>目标数据库目录。</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>是否允许使用已存在但为空的目标目录。</summary>
    public bool Overwrite { get; init; }

    /// <summary>导入前是否校验逐文件 SHA-256。</summary>
    public bool VerifyBeforeImport { get; init; } = true;
}

/// <summary>
/// 迁移包模型数量摘要。
/// </summary>
public sealed record MigrationModelCounts(
    int Measurements,
    int Tables,
    int Keyspaces,
    int DocumentCollections);

/// <summary>
/// 迁移包只读扫描结果。
/// </summary>
public sealed record MigrationPackageScanResult(
    string PackageDirectory,
    int FormatVersion,
    string DatabaseFormat,
    DateTimeOffset CreatedUtc,
    int FileCount,
    int RequiredFileCount,
    long TotalBytes,
    int IndexCount,
    MigrationModelCounts Models,
    BackupConsistency Consistency,
    IReadOnlyDictionary<BackupFileKind, int> FileKinds);

/// <summary>
/// 迁移包校验和结果。
/// </summary>
public sealed record MigrationChecksumResult(
    bool IsValid,
    bool Verified,
    string PackageSha256,
    int CheckedFiles,
    IReadOnlyList<string> Errors);

/// <summary>
/// 迁移包导出结果。
/// </summary>
public sealed record MigrationExportResult(
    BackupManifest Manifest,
    MigrationPackageScanResult Scan,
    MigrationChecksumResult Checksum);

/// <summary>
/// 迁移包导入预检结果。
/// </summary>
public sealed record MigrationImportDryRunResult(
    bool IsValid,
    MigrationPackageScanResult? Scan,
    MigrationChecksumResult Checksum,
    bool TargetDirectoryExists,
    bool TargetDirectoryEmpty,
    IReadOnlyList<string> Errors);

/// <summary>
/// 迁移包导入结果。
/// </summary>
public sealed record MigrationImportResult(
    BackupManifest Manifest,
    MigrationPackageScanResult Scan,
    MigrationChecksumResult Checksum,
    string TargetDirectory);
