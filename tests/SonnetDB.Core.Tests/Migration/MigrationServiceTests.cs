using System.Text;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.ObjectStorage;
using SonnetDB.Query;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Backup;

public sealed class MigrationServiceTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SonnetDB.Migration.Tests.{Guid.NewGuid():N}");

    public MigrationServiceTests()
    {
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public async Task ExportScanChecksumImport_PreservesMultiModelData()
    {
        string sourceRoot = Path.Combine(_rootDirectory, "source");
        string packageRoot = Path.Combine(_rootDirectory, "package");
        string targetRoot = Path.Combine(_rootDirectory, "target");
        var service = new MigrationService();

        using (var source = Open(sourceRoot))
        {
            source.Write(Point.Create(
                "cpu",
                1_000L,
                new Dictionary<string, string> { ["host"] = "edge-01" },
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(42.5) }));
            SqlExecutor.Execute(source, "CREATE TABLE products (id INT, name STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(source, "INSERT INTO products (id, name) VALUES (1, 'sensor')");
            source.Keyspaces.Open("cache").Put("product:1", Encoding.UTF8.GetBytes("ready"));

            var objects = new SndbObjectStore(source);
            objects.CreateBucket("artifacts", SndbBucketPurpose.Artifact);
            await objects.PutObjectAsync(
                "artifacts",
                "firmware/v1.bin",
                new MemoryStream([1, 2, 3, 4]),
                "application/octet-stream");

            var exported = service.Export(source, new MigrationExportOptions
            {
                PackageDirectory = packageRoot,
            });

            Assert.True(exported.Checksum.IsValid);
            Assert.True(exported.Checksum.Verified);
            Assert.False(string.IsNullOrWhiteSpace(exported.Checksum.PackageSha256));
            Assert.Equal(1, exported.Scan.Models.Measurements);
            Assert.Equal(1, exported.Scan.Models.Tables);
            Assert.True(exported.Scan.Models.Keyspaces >= 1);
        }

        var scan = service.Scan(packageRoot);
        var checksum = service.Checksum(packageRoot);
        var dryRun = service.ImportDryRun(new MigrationImportOptions
        {
            PackageDirectory = packageRoot,
            TargetDirectory = targetRoot,
        });

        Assert.True(checksum.IsValid);
        Assert.True(checksum.Verified);
        Assert.True(dryRun.IsValid);
        Assert.False(Directory.Exists(targetRoot));
        Assert.Equal(scan.FileCount, checksum.CheckedFiles);

        var imported = service.Import(new MigrationImportOptions
        {
            PackageDirectory = packageRoot,
            TargetDirectory = targetRoot,
        });
        Assert.Equal(checksum.PackageSha256, imported.Checksum.PackageSha256);

        using var target = Open(targetRoot);
        var series = Assert.Single(target.Catalog.Find("cpu", new Dictionary<string, string> { ["host"] = "edge-01" }));
        var point = Assert.Single(target.Query.Execute(new PointQuery(series.Id, "usage", TimeRange.All)));
        Assert.Equal(42.5, point.Value.AsDouble());

        var tableResult = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(target, "SELECT name FROM products WHERE id = 1"));
        Assert.Equal("sensor", Assert.Single(tableResult.Rows)[0]);
        Assert.Equal("ready", Encoding.UTF8.GetString(target.Keyspaces.Open("cache").Get(Encoding.UTF8.GetBytes("product:1"))!));

        var restoredObject = new SndbObjectStore(target).OpenRead("artifacts", "firmware/v1.bin");
        Assert.NotNull(restoredObject);
        await using var content = restoredObject!.Content;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        Assert.Equal([1, 2, 3, 4], buffer.ToArray());
    }

    [Fact]
    public void ChecksumAndImport_WhenPackageIsModified_RejectCorruption()
    {
        string sourceRoot = Path.Combine(_rootDirectory, "source-corrupt");
        string packageRoot = Path.Combine(_rootDirectory, "package-corrupt");
        string targetRoot = Path.Combine(_rootDirectory, "target-corrupt");
        var service = new MigrationService();

        using (var source = Open(sourceRoot))
        {
            source.Write(Point.Create(
                "cpu",
                1_000L,
                new Dictionary<string, string> { ["host"] = "edge-01" },
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(1) }));
            _ = service.Export(source, new MigrationExportOptions { PackageDirectory = packageRoot });
        }

        var manifest = new BackupService().ReadManifest(packageRoot);
        var file = manifest.Files.First(static entry => entry.Required && entry.SizeBytes > 0);
        string path = Path.Combine(packageRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            stream.WriteByte(0x42);

        var checksum = service.Checksum(packageRoot);
        var dryRun = service.ImportDryRun(new MigrationImportOptions
        {
            PackageDirectory = packageRoot,
            TargetDirectory = targetRoot,
        });

        Assert.False(checksum.IsValid);
        Assert.Contains(checksum.Errors, error => error.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.False(dryRun.IsValid);
        Assert.Throws<InvalidDataException>(() => service.Import(new MigrationImportOptions
        {
            PackageDirectory = packageRoot,
            TargetDirectory = targetRoot,
        }));
        Assert.False(Directory.Exists(targetRoot));
    }

    [Fact]
    public void PackageChecksum_WhenManifestBytesChange_ChangesDigest()
    {
        string sourceRoot = Path.Combine(_rootDirectory, "source-manifest");
        string packageRoot = Path.Combine(_rootDirectory, "package-manifest");
        var service = new MigrationService();

        MigrationChecksumResult original;
        using (var source = Open(sourceRoot))
        {
            source.Write(Point.Create(
                "cpu",
                1_000L,
                new Dictionary<string, string> { ["host"] = "edge-01" },
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(1) }));
            original = service.Export(source, new MigrationExportOptions { PackageDirectory = packageRoot }).Checksum;
        }

        File.AppendAllText(Path.Combine(packageRoot, BackupManifest.FileName), Environment.NewLine);
        var updated = service.Checksum(packageRoot);

        Assert.True(updated.IsValid);
        Assert.True(updated.Verified);
        Assert.NotEqual(original.PackageSha256, updated.PackageSha256);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);
    }

    private static Tsdb Open(string root) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        BackgroundFlush = new() { Enabled = false },
        Compaction = new() { Enabled = false },
    });
}
