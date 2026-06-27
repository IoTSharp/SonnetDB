using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Backup;
using SonnetDB.Documents;
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

    [Fact]
    public void CreateRestore_WithDocumentCollection_UsesOrderedKvSegmentAndRestoresIndexes()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-documents");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-documents");
        string restoreRoot = Path.Combine(_rootDirectory, "restored-documents");

        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            db.Documents.Create(DocumentCollectionSchema.Create("devices"));
            db.Documents.CreateIndex(
                "devices",
                new DocumentPathIndexDefinition("idx_type", ["$.type"]));
            var store = db.Documents.Open("devices");
            store.Insert("b", """{"type":"sensor","site":"west"}""");
            store.Insert("a", """{"type":"sensor","site":"east"}""");
            store.Insert("c", """{"type":"gateway","site":"west"}""");

            var manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });

            Assert.Contains(manifest.Files, static file =>
                file.Kind == BackupFileKind.Document &&
                file.Path.Contains("documents/collections/", StringComparison.Ordinal) &&
                file.Path.EndsWith(".SDBKVSEG", StringComparison.OrdinalIgnoreCase));
        }

        new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreRoot,
        });

        using var restored = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = restoreRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        var restoredStore = restored.Documents.Open("devices");
        var rows = restoredStore.Scan();

        Assert.Equal(["a", "b", "c"], rows.Select(static row => row.Id).ToArray());
        Assert.Equal(["a", "b"], restoredStore.GetByIndex(
                restoredStore.Schema.TryGetIndex("idx_type")!,
                "sensor")
            .Select(static row => row.Id)
            .ToArray());
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
