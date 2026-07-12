using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;

namespace SonnetDB.Backup;

/// <summary>
/// 组合备份、扫描、校验和恢复能力的通用迁移原语。
/// </summary>
/// <remarks>
/// 迁移包是与 <see cref="BackupManifest.DatabaseFormat"/> 绑定的一致性目录包，
/// 不承诺跨数据库产品或跨不兼容格式版本的逻辑数据转换。
/// </remarks>
public sealed class MigrationService
{
    private readonly BackupService _backupService;

    /// <summary>
    /// 创建使用默认备份服务的迁移服务。
    /// </summary>
    public MigrationService()
        : this(new BackupService())
    {
    }

    internal MigrationService(BackupService backupService)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
    }

    /// <summary>
    /// 导出当前数据库的一致迁移包，并立即执行逐文件校验。
    /// </summary>
    /// <param name="tsdb">已打开的源数据库。</param>
    /// <param name="options">迁移包输出选项。</param>
    /// <returns>manifest、扫描摘要和校验和结果。</returns>
    public MigrationExportResult Export(Tsdb tsdb, MigrationExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PackageDirectory);

        var manifest = _backupService.Create(tsdb, new BackupCreateOptions
        {
            DestinationDirectory = options.PackageDirectory,
            Overwrite = options.Overwrite,
            IncludeFullTextIndexes = options.IncludeFullTextIndexes,
        });
        var scan = CreateScan(options.PackageDirectory, manifest);
        var checksum = Checksum(options.PackageDirectory);
        if (!checksum.IsValid)
            throw new InvalidDataException("迁移包导出后校验失败：" + string.Join("; ", checksum.Errors));

        return new MigrationExportResult(manifest, scan, checksum);
    }

    /// <summary>
    /// 只读扫描迁移包 manifest，不读取用户数据文件内容。
    /// </summary>
    /// <param name="packageDirectory">迁移包目录。</param>
    /// <returns>模型、文件、索引和一致性点摘要。</returns>
    public MigrationPackageScanResult Scan(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        return CreateScan(packageDirectory, _backupService.ReadManifest(packageDirectory));
    }

    /// <summary>
    /// 校验迁移包逐文件 SHA-256，并计算稳定的包级 SHA-256。
    /// </summary>
    /// <param name="packageDirectory">迁移包目录。</param>
    /// <returns>校验状态、包级摘要和错误列表。</returns>
    public MigrationChecksumResult Checksum(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        return CreateVerifiedChecksum(packageDirectory, _backupService.Verify(packageDirectory));
    }

    /// <summary>
    /// 校验迁移包和目标目录策略，但不写入目标目录。
    /// </summary>
    /// <param name="options">迁移包导入选项。</param>
    /// <returns>可用于审批和自动化门禁的预检结果。</returns>
    public MigrationImportDryRunResult ImportDryRun(MigrationImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PackageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetDirectory);

        var backupDryRun = _backupService.RestoreDryRun(ToRestoreOptions(options));
        MigrationPackageScanResult? scan = null;
        var errors = new List<string>(backupDryRun.Errors);
        try
        {
            scan = Scan(options.PackageDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            errors.Add(ex.Message);
        }

        var checksum = options.VerifyBeforeImport
            ? CreateVerifiedChecksum(options.PackageDirectory, backupDryRun.Verification)
            : CreateUncheckedChecksum(options.PackageDirectory);
        if (options.VerifyBeforeImport && !checksum.IsValid)
            errors.AddRange(checksum.Errors);

        var distinctErrors = errors.Distinct(StringComparer.Ordinal).ToArray();
        return new MigrationImportDryRunResult(
            backupDryRun.IsValid && scan is not null && (!options.VerifyBeforeImport || checksum.IsValid),
            scan,
            checksum,
            backupDryRun.TargetDirectoryExists,
            backupDryRun.TargetDirectoryEmpty,
            distinctErrors);
    }

    /// <summary>
    /// 校验并导入迁移包到新的数据库目录。
    /// </summary>
    /// <param name="options">迁移包导入选项。</param>
    /// <returns>导入后的 manifest、扫描摘要和校验结果。</returns>
    public MigrationImportResult Import(MigrationImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dryRun = ImportDryRun(options);
        if (!dryRun.IsValid || dryRun.Scan is null)
            throw new InvalidDataException("迁移包导入预检失败：" + string.Join("; ", dryRun.Errors));

        var manifest = _backupService.Restore(ToRestoreOptions(options));
        return new MigrationImportResult(
            manifest,
            dryRun.Scan,
            dryRun.Checksum,
            Path.GetFullPath(options.TargetDirectory));
    }

    private static BackupRestoreOptions ToRestoreOptions(MigrationImportOptions options) => new()
    {
        BackupDirectory = options.PackageDirectory,
        TargetDirectory = options.TargetDirectory,
        Overwrite = options.Overwrite,
        VerifyBeforeRestore = options.VerifyBeforeImport,
    };

    private MigrationChecksumResult CreateUncheckedChecksum(string packageDirectory)
    {
        try
        {
            var manifest = _backupService.ReadManifest(packageDirectory);
            return new MigrationChecksumResult(true, false, ComputePackageSha256(packageDirectory, manifest), 0, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new MigrationChecksumResult(false, false, string.Empty, 0, [ex.Message]);
        }
    }

    private MigrationChecksumResult CreateVerifiedChecksum(
        string packageDirectory,
        BackupVerificationResult verification)
    {
        try
        {
            var manifest = _backupService.ReadManifest(packageDirectory);
            return new MigrationChecksumResult(
                verification.IsValid,
                true,
                ComputePackageSha256(packageDirectory, manifest),
                verification.CheckedFiles,
                verification.Errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            var errors = verification.Errors.Concat([ex.Message]).Distinct(StringComparer.Ordinal).ToArray();
            return new MigrationChecksumResult(false, true, string.Empty, verification.CheckedFiles, errors);
        }
    }

    private static MigrationPackageScanResult CreateScan(string packageDirectory, BackupManifest manifest)
    {
        var fileKinds = manifest.Files
            .GroupBy(static file => file.Kind)
            .OrderBy(static group => group.Key)
            .ToDictionary(static group => group.Key, static group => group.Count());
        return new MigrationPackageScanResult(
            Path.GetFullPath(packageDirectory),
            manifest.FormatVersion,
            manifest.DatabaseFormat,
            manifest.CreatedUtc,
            manifest.Files.Count,
            manifest.Files.Count(static file => file.Required),
            manifest.Files.Sum(static file => file.SizeBytes),
            manifest.Indexes.Count,
            new MigrationModelCounts(
                manifest.Models.Measurements.Count,
                manifest.Models.Tables.Count,
                manifest.Models.Keyspaces.Count,
                manifest.Models.DocumentCollections.Count),
            manifest.Consistency,
            fileKinds);
    }

    private static string ComputePackageSha256(string packageDirectory, BackupManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "sonnetdb-migration-package-v1");
        string manifestPath = Path.Combine(Path.GetFullPath(packageDirectory), BackupManifest.FileName);
        using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash.AppendData(SHA256.HashData(stream));

        foreach (var file in manifest.Files.OrderBy(static file => file.Path, StringComparer.Ordinal))
        {
            AppendString(hash, file.Path.Replace('\\', '/'));
            AppendInt64(hash, file.SizeBytes);
            AppendString(hash, file.Sha256.ToLowerInvariant());
            AppendInt32(hash, (int)file.Kind);
            hash.AppendData([file.Required ? (byte)1 : (byte)0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
