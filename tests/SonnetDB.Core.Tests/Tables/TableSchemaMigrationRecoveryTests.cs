using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>构造器恢复必须在任何正常表读写前验证日志和确认持久决定。</summary>
public sealed class TableSchemaMigrationRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ddl-recovery-" + Guid.NewGuid().ToString("N"));
    private string TablesRoot => Path.Combine(_root, "tables");
    private string JournalPath => Path.Combine(TablesRoot, TableSchemaMigrationJournal.FileName);

    /// <summary>后一个表的 generation 或指纹不匹配时，前一个表也不能先执行 undo。</summary>
    [Theory]
    [InlineData("generation")]
    [InlineData("fingerprint")]
    [InlineData("corruption")]
    public void Open_InvalidPendingJournal_RejectsWithoutChangingAnyTableFiles(string failure)
    {
        ArrangePending(failure);
        var before = SnapshotFiles();
        Assert.Throws<InvalidDataException>(() => Open());
        var after = SnapshotFiles();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (string path in before.Keys) Assert.Equal(before[path], after[path]);
    }

    /// <summary>完整标记缓存可见时也必须重新 fsync；再次失败不能开放写入。</summary>
    [Fact]
    public void Open_VisibleCompleteMarkerFlushFails_RejectsUntilDecisionIsSynced()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE items(id INT,code INT,PRIMARY KEY(id))");
            SqlExecutor.Execute(db, "INSERT INTO items(id,code) VALUES(1,2)");
            SqlExecutor.Execute(db, "ALTER TABLE items ALTER PRIMARY KEY(code)");
        }
        int flushes = 0;
        TableSchemaMigrationJournal.BeforeCompleteFlushTestHook = path =>
        {
            if (path != JournalPath) return;
            flushes++;
            throw new IOException("injected reopen decision sync failure");
        };
        try
        {
            Assert.Throws<IOException>(() => Open());
            Assert.Equal(1, flushes);
        }
        finally { TableSchemaMigrationJournal.BeforeCompleteFlushTestHook = null; }
        using (var reopened = Open())
        {
            Assert.Equal(["code"], reopened.Tables.Catalog.TryGet("items")!.PrimaryKey);
            SqlExecutor.Execute(reopened, "UPDATE items SET id=9 WHERE code=2");
        }
        using var again = Open();
        Assert.Equal(9L, Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(again,
            "SELECT id FROM items WHERE code=2")).Rows)[0]);
    }

    private void ArrangePending(string failure)
    {
        using (var db = Open())
        {
            foreach (string table in new[] { "alpha", "zeta" })
            {
                SqlExecutor.Execute(db, $"CREATE TABLE {table}(id INT,code INT,PRIMARY KEY(id))");
                SqlExecutor.Execute(db, $"INSERT INTO {table}(id,code) VALUES(1,2),(2,1)");
            }
            var plans = new List<(TableStore Store, TableStore.SchemaMigrationPlan Plan)>();
            foreach (string table in new[] { "alpha", "zeta" })
            {
                var store = db.Tables.Open(table);
                plans.Add((store, store.PrepareSchemaMigration(store.Schema.WithPrimaryKey(["code"]),
                    (_, row) => row.Values, (_, _) => { }, TableRowIdentity.Create, new TableSchemaMigrationBudget())));
            }
            byte[] original = File.ReadAllBytes(db.Tables.SchemaPath);
            TableSchemaCodec.SaveWithRecoveryBarrier(db.Tables.SchemaPath, db.Tables.Catalog.Snapshot());
            TableSchemaMigrationUndo[] undo = plans.Select(static entry => entry.Plan.Undo).ToArray();
            if (failure == "generation") undo[1] = undo[1] with { Generation = undo[1].Generation + 1 };
            if (failure == "fingerprint") undo[1] = undo[1] with { SchemaFingerprint = new byte[32] };
            TableSchemaMigrationJournal.Prepare(JournalPath, original, undo);
            foreach (var entry in plans) entry.Store.ApplySchemaMigrationRaw(entry.Plan);
            TableSchemaCodec.SaveWithRecoveryBarrier(db.Tables.SchemaPath,
                plans.Select(static entry => entry.Plan.UpdatedSchema).ToArray());
        }
        if (failure == "corruption")
        {
            byte[] contents = File.ReadAllBytes(JournalPath);
            contents[24] ^= 1;
            File.WriteAllBytes(JournalPath, contents);
        }
    }

    private Dictionary<string, byte[]> SnapshotFiles()
        => Directory.EnumerateFiles(TablesRoot, "*", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(static path => path, File.ReadAllBytes, StringComparer.Ordinal);

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false },
    });

    /// <summary>删除本测试专属的所有持久文件。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
