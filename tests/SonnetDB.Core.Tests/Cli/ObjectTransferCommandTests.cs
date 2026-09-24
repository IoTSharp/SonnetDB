using SonnetDB.Cli;
using SonnetDB.Data.ObjectStorage;

namespace SonnetDB.Core.Tests.Cli;

public sealed class ObjectTransferCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-cli-object-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Run_Cp_UploadsAndDownloadsFileStreams()
    {
        Directory.CreateDirectory(_root);
        string input = Path.Combine(_root, "input.txt");
        string output = Path.Combine(_root, "nested", "output.txt");
        await File.WriteAllTextAsync(input, "object stream content");
        string connection = $"Data Source={Path.Combine(_root, "db")};Mode=Embedded";
        using (var client = new SndbObjectStorageClient(connection))
            await client.CreateBucketAsync("media");

        Assert.Equal(0, Run("cp", "--connection", connection, input, "s3://media/import/input.txt").ExitCode);
        var download = Run("cp", "--connection", connection, "s3://media/import/input.txt", output);

        Assert.Equal(0, download.ExitCode);
        Assert.Equal("object stream content", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Run_SyncDryRun_ListsPlanWithoutCreatingTarget()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "a");
        string target = Path.Combine(_root, "target");
        string connection = $"Data Source={Path.Combine(_root, "db")};Mode=Embedded";
        using (var client = new SndbObjectStorageClient(connection))
            await client.CreateBucketAsync("media");

        var result = Run("sync", "--connection", connection, source, "s3://media/archive", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("upload", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target));
        Assert.Equal(2, Run("sync", "--connection", connection, source, "s3://media/archive").ExitCode);
    }

    [Fact]
    public async Task Run_SyncDryRun_LocalSource_DoesNotCreateEmbeddedTarget()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "a");
        string database = Path.Combine(_root, "not-created-db");
        string connection = $"Data Source={database};Mode=Embedded";

        var result = Run("sync", "--connection", connection, source, "s3://media/archive", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(database));
    }

    [Fact]
    public async Task Run_SyncDryRun_ObjectSource_WithMissingLocalTarget_WritesPlanWithoutCreatingDirectory()
    {
        Directory.CreateDirectory(_root);
        string target = Path.Combine(_root, "missing-target");
        string connection = $"Data Source={Path.Combine(_root, "db")};Mode=Embedded";
        using (var client = new SndbObjectStorageClient(connection))
        {
            await client.CreateBucketAsync("media");
            await using var content = new MemoryStream("a"u8.ToArray());
            await client.PutObjectAsync("media", "archive/nested/a.txt", content, "text/plain");
        }

        var result = Run("sync", "--connection", connection, "s3://media/archive", target, "--dry-run");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("download media/archive/nested/a.txt", result.Output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(target, "nested", "a.txt"), result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Run_CpDryRun_DoesNotCreateEmbeddedTargetOrTransfer()
    {
        Directory.CreateDirectory(_root);
        string input = Path.Combine(_root, "input.txt");
        await File.WriteAllTextAsync(input, "object stream content");
        string database = Path.Combine(_root, "not-created-db");
        string connection = $"Data Source={database};Mode=Embedded";

        var result = Run("cp", "--connection", connection, input, "s3://media/input.txt", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("dry-run", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(database));
    }

    private static (int ExitCode, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new CliApplication(new StringReader(string.Empty), output, error);
        return (app.Run(args), output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
