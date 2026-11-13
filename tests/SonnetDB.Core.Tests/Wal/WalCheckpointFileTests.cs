using SonnetDB.Wal;

namespace SonnetDB.Core.Tests.Wal;

public sealed class WalCheckpointFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SonnetDB-checkpoint-share-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveDuringTryLoadValidation_AtomicallyReplacesPathWhileReaderKeepsOldCompleteState()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, WalSegmentLayout.CheckpointFileName);
        var previous = new WalCheckpointState(10, 1, 1024, 1);
        var replacement = new WalCheckpointState(20, 2, 2048, 2);
        WalCheckpointFile.Save(path, previous);
        bool entered = false;
        bool completed = false;
        Exception? replacementFailure = null;

        WalCheckpointState? observed = WalCheckpointFile.TryLoad(path, state =>
        {
            entered = true;
            Assert.Equal(previous, state);
            try
            {
                // 回调内发布新 state，不得改变本次已读取的完整旧 state。
                WalCheckpointFile.Save(path, replacement);
                completed = true;
            }
            catch (Exception exception)
            {
                replacementFailure = exception;
                throw;
            }
            return true;
        });

        Assert.True(entered);
        Assert.Null(replacementFailure);
        Assert.True(completed);
        Assert.Equal<WalCheckpointState?>(previous, observed);
        Assert.Equal<WalCheckpointState?>(replacement, WalCheckpointFile.TryLoad(path));
        Assert.False(File.Exists(path + WalCheckpointFile.TempSuffix));
    }

    [Fact]
    public void SaveWithAnExternalDeleteSharedReader_PreservesOldBytesAndPublishesNewCompleteState()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, WalSegmentLayout.CheckpointFileName);
        var previous = new WalCheckpointState(10, 1, 1024, 1);
        var replacement = new WalCheckpointState(20, 2, 2048, 2);
        WalCheckpointFile.Save(path, previous);
        byte[] original = File.ReadAllBytes(path);

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        WalCheckpointFile.Save(path, replacement);
        byte[] oldHandleBytes = new byte[original.Length];
        reader.ReadExactly(oldHandleBytes);

        Assert.Equal(original, oldHandleBytes);
        Assert.Equal<WalCheckpointState?>(replacement, WalCheckpointFile.TryLoad(path));
        Assert.False(File.Exists(path + WalCheckpointFile.TempSuffix));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_directory);
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), parent, comparison)
            || !Path.GetFileName(full).StartsWith("SonnetDB-checkpoint-share-", StringComparison.Ordinal)
            || (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Refusing cleanup outside the checkpoint test's owned directory.");
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
