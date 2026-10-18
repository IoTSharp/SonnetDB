using System.Security.Cryptography;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>CTAS 创建日志的归属、校验与重入恢复边界。</summary>
public sealed class TableCreationJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-creation-journal-" + Guid.NewGuid().ToString("N"));

    /// <summary>已存在且未知的准备文件必须保留，不能在 CreateNew 失败的 finally 中删除。</summary>
    [Fact]
    public void Prepare_ExistingUnknownTemporary_PreservesOriginalBytesAcrossReopenAndCreateFailure()
    {
        string tables;
        using (var database = Open())
        {
            SqlExecutor.Execute(database, "CREATE TABLE Source(Id INT,PRIMARY KEY(Id))");
            tables = Path.GetDirectoryName(database.Tables.SchemaPath)!;
        }
        byte[] sentinel = "foreign-preparation-evidence"u8.ToArray();
        string temporary = Path.Combine(tables, TableCreationJournal.FileName + ".tmp");
        File.WriteAllBytes(temporary, sentinel);
        using var reopened = Open();
        Assert.Equal(sentinel, File.ReadAllBytes(temporary));
        Assert.Throws<IOException>(() => SqlExecutor.Execute(reopened, "CREATE TABLE Copied AS SELECT Id FROM Source"));
        Assert.Equal(sentinel, File.ReadAllBytes(temporary));
        Assert.Null(reopened.Tables.Catalog.TryGet("Copied"));
    }

    /// <summary>损坏的 pending 校验失败，不能修改日志或自有目录。</summary>
    [Fact]
    public void Recover_CorruptPendingJournal_RejectsWithoutChangingEvidence()
    {
        string tables;
        using (var database = Open()) tables = Path.GetDirectoryName(database.Tables.SchemaPath)!;
        var journal = new TableCreationJournal(Guid.NewGuid(), "Copied");
        string path = Path.Combine(tables, TableCreationJournal.FileName);
        journal.Prepare(path);
        journal.CreateStage(tables);
        byte[] contents = File.ReadAllBytes(path);
        contents[12] ^= 1;
        File.WriteAllBytes(path, contents);
        byte[] before = SHA256.HashData(contents);
        Assert.Throws<InvalidDataException>(() => Open());
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.True(Directory.Exists(journal.StageDirectory(tables)));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    /// <summary>只回收本夹具创建的精确目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
