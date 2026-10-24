using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证关系事务恢复日志的持久发布和读取路径。</summary>
public sealed class TableTransactionJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-txn-journal-" + Guid.NewGuid().ToString("N"));

    /// <summary>prepare 发布日志后，关闭文件并重新读取仍能得到完整 undo。</summary>
    [Fact]
    public void Prepare_DurableRename_CanBeReadAfterClose()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableTransactionJournal.FileName);
        var undo = new TableTransactionUndo(
            new TableObjectName("main", "items"),
            Generation: 0,
            SchemaFingerprint: new byte[32],
            Actions: [new TableStore.RollbackAction([(byte)'r', 1], [2, 3])]);

        TableTransactionJournal.Prepare(path, [undo]);

        Assert.True(File.Exists(path));
        var pending = Assert.Single(TableTransactionJournal.ReadPending(path));
        Assert.Equal(undo.TableObjectName, pending.TableObjectName);
        Assert.Equal(undo.Generation, pending.Generation);
        Assert.Equal(undo.SchemaFingerprint, pending.SchemaFingerprint);
        var action = Assert.Single(pending.Actions);
        Assert.Equal(undo.Actions[0].Key, action.Key);
        Assert.Equal(undo.Actions[0].Value, action.Value);
    }

    /// <summary>回收测试目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
