using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证独立 DDL 恢复日志的版本、校验、提交决定和恢复前拒绝合同。</summary>
public sealed class TableSchemaMigrationJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ddl-journal-" + Guid.NewGuid().ToString("N"));

    /// <summary>原目录、空值删除和 metadata before-image 往返，提交决定可幂等读取。</summary>
    [Fact]
    public void Journal_PrepareAndComplete_RoundTripsRecoveryDecisionAndRawValues()
    {
        string path = Prepare();
        var pending = Assert.IsType<TableSchemaMigrationRecord>(TableSchemaMigrationJournal.Read(path));
        Assert.False(pending.IsComplete);
        var table = Assert.Single(pending.Tables);
        Assert.Equal("items", table.TableName);
        Assert.Equal(1L, table.Generation);
        Assert.Equal(new byte[32], table.SchemaFingerprint);
        Assert.Equal(new byte[] { 1, 2, 3 }, table.Entries[0].PreviousValue);
        Assert.Null(table.Entries[1].PreviousValue);
        Assert.Equal(new byte[] { 4, 5 }, table.Entries[2].PreviousValue);
        Assert.Throws<InvalidOperationException>(() => TableSchemaMigrationJournal.Prepare(path, pending.OriginalCatalog, pending.Tables));
        TableSchemaMigrationJournal.Complete(path);
        byte[] completedBytes = File.ReadAllBytes(path);
        TableSchemaMigrationJournal.Complete(path);
        Assert.Equal(completedBytes, File.ReadAllBytes(path));
        Assert.True(TableSchemaMigrationJournal.Read(path)!.IsComplete);
        TableSchemaMigrationJournal.Prepare(path, pending.OriginalCatalog, pending.Tables);
        Assert.False(TableSchemaMigrationJournal.Read(path)!.IsComplete);
    }

    /// <summary>所有不完整但合法的完成标记前缀仍要求恢复，不能被当成提交。</summary>
    [Fact]
    public void Journal_PartialCompletionMarker_RemainsPendingUntilComplete()
    {
        string path = Prepare();
        long preparedLength = new FileInfo(path).Length;
        TableSchemaMigrationJournal.Complete(path);
        byte[] completed = File.ReadAllBytes(path);
        for (int markerLength = 0; markerLength < 8; markerLength++)
        {
            File.WriteAllBytes(path, completed.AsSpan(0, checked((int)preparedLength) + markerLength).ToArray());
            Assert.False(TableSchemaMigrationJournal.Read(path)!.IsComplete);
        }
        TableSchemaMigrationJournal.Complete(path);
        Assert.True(TableSchemaMigrationJournal.Read(path)!.IsComplete);
    }

    /// <summary>头、版本、载荷、完成标记损坏与截断均拒绝，不能使用不完整 undo。</summary>
    [Fact]
    public void Journal_CorruptionAndTruncation_RejectBeforeRecovery()
    {
        string path = Prepare();
        TableSchemaMigrationJournal.Complete(path);
        byte[] valid = File.ReadAllBytes(path);
        foreach (int offset in new[] { 0, 8, 12, 16, 20, 24, valid.Length - 1 })
        {
            byte[] corrupt = (byte[])valid.Clone();
            corrupt[offset] ^= 0x40;
            File.WriteAllBytes(path, corrupt);
            Assert.Throws<InvalidDataException>(() => TableSchemaMigrationJournal.Read(path));
            Assert.Throws<InvalidDataException>(() => TableSchemaMigrationJournal.Complete(path));
        }
        foreach (int length in new[] { 0, 23, 24, valid.Length - 9 })
        {
            File.WriteAllBytes(path, valid.AsSpan(0, length).ToArray());
            Assert.Throws<InvalidDataException>(() => TableSchemaMigrationJournal.Read(path));
        }
    }

    /// <summary>重复表、过长名称和非关系键在写日志前拒绝。</summary>
    [Fact]
    public void Journal_InvalidPlan_LeavesNoPublishedJournal()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableSchemaMigrationJournal.FileName);
        byte[] catalog = CatalogBytes();
        var table = Undo();
        Assert.Throws<InvalidOperationException>(() => TableSchemaMigrationJournal.Prepare(path, catalog, [table, table]));
        Assert.Throws<InvalidOperationException>(() => TableSchemaMigrationJournal.Prepare(path, catalog,
            [table with { TableName = new string('x', 1025) }]));
        Assert.Throws<InvalidOperationException>(() => TableSchemaMigrationJournal.Prepare(path, catalog,
            [table with { Entries = [new TableSchemaMigrationEntry([(byte)'x'], [1])] }]));
        Assert.Throws<InvalidOperationException>(() => TableSchemaMigrationJournal.Prepare(path, catalog,
            [table with { Entries = [table.Entries[0], table.Entries[0]] }]));
        Assert.False(File.Exists(path));
        Assert.Null(TableSchemaMigrationJournal.Read(path));
    }

    private string Prepare()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableSchemaMigrationJournal.FileName);
        TableSchemaMigrationJournal.Prepare(path, CatalogBytes(), [Undo()]);
        return path;
    }

    private byte[] CatalogBytes()
    {
        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        TableSchemaCodec.Save(path, []);
        return File.ReadAllBytes(path);
    }

    private static TableSchemaMigrationUndo Undo() => new("items", 1, new byte[32],
    [
        new TableSchemaMigrationEntry([(byte)'r', 1], [1, 2, 3]),
        new TableSchemaMigrationEntry([(byte)'i', 2], null),
        new TableSchemaMigrationEntry([(byte)'m', (byte)'a'], [4, 5]),
    ]);

    /// <summary>回收测试目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
