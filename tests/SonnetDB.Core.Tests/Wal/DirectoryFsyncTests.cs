using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Wal;

/// <summary>
/// 目录 fsync（#189）功能测试：验证 <see cref="DirectoryFsync.FlushBestEffort"/> 在当前平台
/// （尤其 Windows，经 P/Invoke <c>CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)</c> + <c>FlushFileBuffers</c>）
/// 对真实目录不抛异常且能成功执行。
/// </summary>
public sealed class DirectoryFsyncTests : IDisposable
{
    private readonly string _tempDir;

    public DirectoryFsyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void FlushBestEffort_ExistingDirectory_DoesNotThrow()
    {
        // 在目录中做一次原子改名，再 flush 目录——应无异常（Windows 上走真实 FlushFileBuffers）。
        string tmp = Path.Combine(_tempDir, "x.tmp");
        string dst = Path.Combine(_tempDir, "x.dat");
        File.WriteAllText(tmp, "hello");
        File.Move(tmp, dst, overwrite: true);

        var ex = Record.Exception(() => DirectoryFsync.FlushBestEffort(_tempDir));
        Assert.Null(ex);
    }

    [Fact]
    public void FlushBestEffort_MissingOrEmptyPath_DoesNotThrow()
    {
        Assert.Null(Record.Exception(() => DirectoryFsync.FlushBestEffort(string.Empty)));
        Assert.Null(Record.Exception(() => DirectoryFsync.FlushBestEffort(
            Path.Combine(_tempDir, "does-not-exist"))));
    }

    [Fact]
    public void FlushRequired_ExistingDirectory_CompletesDurableMetadataFlush()
    {
        string temporary = Path.Combine(_tempDir, "required.tmp");
        string published = Path.Combine(_tempDir, "required.dat");
        File.WriteAllText(temporary, "durable");
        File.Move(temporary, published, overwrite: true);

        DirectoryFsync.FlushRequired(_tempDir);
    }

    [Fact]
    public void FlushRequired_NestedWalDirectory_CompletesDurableMetadataFlush()
    {
        string walDirectory = Path.Combine(_tempDir, "tables", "rowstore", "table", "wal");
        Directory.CreateDirectory(walDirectory);
        string activeWal = Path.Combine(walDirectory, "active.SDBKVWAL");
        using (var stream = new FileStream(activeWal, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(new byte[64]);
            stream.Flush(flushToDisk: true);
        }

        DirectoryFsync.FlushRequired(walDirectory);
    }

    [Fact]
    public void FlushRequired_NonAsciiDirectoryName_UsesUtf8Path()
    {
        string directory = Path.Combine(_tempDir, "目录-测试");
        Directory.CreateDirectory(directory);

        DirectoryFsync.FlushRequired(directory);
    }

    [Fact]
    public void FlushRequired_MissingDirectory_FailsClosed()
    {
        Assert.Throws<DirectoryNotFoundException>(() => DirectoryFsync.FlushRequired(
            Path.Combine(_tempDir, "does-not-exist")));
    }
}
