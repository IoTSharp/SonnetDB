using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests;

public sealed class TsdbRegistryMountedDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-mounted-db", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MountExisting_WithEmbeddedDatabase_ReadsOriginalRowsAndProtectsDirectory()
    {
        var databasePath = Path.Combine(_root, "source", "inspection");
        using (var embedded = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            SqlExecutor.Execute(embedded, "CREATE TABLE inspections (id INT, note STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(embedded, "INSERT INTO inspections (id, note) VALUES (1, 'offline')");
        }

        using (var registry = new TsdbRegistry(Path.Combine(_root, "control")))
        {
            registry.MountExisting("inspection", databasePath);
            Assert.Equal(["inspection"], registry.ListDatabases());
            Assert.True(registry.IsMounted("inspection"));
            Assert.True(registry.TryGet("inspection", out var mounted));
            var result = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(mounted, "SELECT note FROM inspections WHERE id = 1"));
            Assert.Equal("offline", Assert.Single(result.Rows)[0]);
            Assert.False(registry.Drop("inspection"));
            Assert.True(Directory.Exists(databasePath));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        Assert.Single(Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(reopened, "SELECT note FROM inspections")).Rows);
    }

    [Fact]
    public void MountExisting_WithEmptyDirectory_DoesNotCreateDatabase()
    {
        var emptyPath = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyPath);
        using var registry = new TsdbRegistry(Path.Combine(_root, "control"));

        Assert.Throws<DirectoryNotFoundException>(() => registry.MountExisting("empty", emptyPath));
        Assert.Empty(registry.ListDatabases());
        Assert.Empty(Directory.EnumerateFileSystemEntries(emptyPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
