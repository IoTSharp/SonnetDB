using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Backup;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SonnetDB.Backup.Tests.{Guid.NewGuid():N}");

    public BackupServiceTests()
    {
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public void RestoreDryRun_ForMissingTarget_ReportsEmptyTargetWithoutCreatingDirectory()
    {
        string backupDirectory = CreateBackupWithSingleFile("data/catalog.SDBCAT");
        string restoreTarget = Path.Combine(_rootDirectory, "restored");

        var result = new BackupService().RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
        });

        Assert.True(result.IsValid);
        Assert.False(result.TargetDirectoryExists);
        Assert.True(result.TargetDirectoryEmpty);
        Assert.False(Directory.Exists(restoreTarget));
    }

    [Fact]
    public void RestoreDryRun_WithNoVerify_RejectsManifestPathTraversal()
    {
        string backupDirectory = CreateBackupWithSingleFile("../outside.SDBCAT");
        string restoreTarget = Path.Combine(_rootDirectory, "restored");

        var result = new BackupService().RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
            VerifyBeforeRestore = false,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("不安全路径", StringComparison.Ordinal));
        Assert.False(Directory.Exists(restoreTarget));
    }

    [Fact]
    public void Restore_RejectsManifestPathTraversalWithoutCopyingOutsideTarget()
    {
        string backupDirectory = CreateBackupWithSingleFile("../outside.SDBCAT");
        string restoreTarget = Path.Combine(_rootDirectory, "restored");

        var exception = Assert.Throws<InvalidDataException>(() => new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
            VerifyBeforeRestore = false,
        }));

        Assert.Contains("恢复预检失败", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_rootDirectory, "outside.SDBCAT")));
        Assert.False(Directory.Exists(restoreTarget));
    }

    [Fact]
    public void Create_WithLayeredSegments_RecordsNestedSegmentPath()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-layered");

        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        }))
        {
            db.Write(Point.Create(
                "cpu",
                1000L,
                new Dictionary<string, string> { ["host"] = "a" },
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(42.0) }));
            db.FlushNow();

            var manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });

            var segment = Assert.Single(manifest.Files, static file => file.Kind == BackupFileKind.Segment);
            Assert.StartsWith("segments/v2/", segment.Path, StringComparison.Ordinal);
            Assert.EndsWith(".SDBSEG", segment.Path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(backupDirectory, segment.Path.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);
    }

    private string CreateBackupWithSingleFile(string manifestPath)
    {
        string backupDirectory = Path.Combine(_rootDirectory, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);

        string hash = "not-checked-in-no-verify";
        if (!manifestPath.Contains("..", StringComparison.Ordinal))
        {
            string filePath = Path.Combine(backupDirectory, manifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "catalog");
            hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
        }

        var manifest = new BackupManifest(
            BackupManifest.CurrentFormatVersion,
            "SonnetDB/MM9",
            DateTimeOffset.UtcNow,
            _rootDirectory,
            new BackupConsistency(0, 0, 0, 0),
            new BackupModelSummary([], [], [], []),
            [new BackupFileEntry(manifestPath, 7, hash, BackupFileKind.Catalog, Required: true)],
            []);

        string json = JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest);
        File.WriteAllText(Path.Combine(backupDirectory, BackupManifest.FileName), json);
        return backupDirectory;
    }
}
