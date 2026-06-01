using System.Text;
using SonnetDB.Engine;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvKeyspaceTests : IDisposable
{
    private readonly string _root;

    public KvKeyspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void PutGetDelete_WithBytes_RoundTripsCurrentValue()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("devices");

        long v1 = kv.Put("device:1", Encoding.UTF8.GetBytes("online"));
        long v2 = kv.Put("device:1", Encoding.UTF8.GetBytes("offline"));

        Assert.True(v2 > v1);
        Assert.Equal("offline", Encoding.UTF8.GetString(kv.Get("device:1")!));
        Assert.True(kv.Delete("device:1"));
        Assert.Null(kv.Get("device:1"));
        Assert.False(kv.Delete("device:1"));
    }

    [Fact]
    public void ScanPrefix_ReturnsSortedLimitedSnapshot()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("assets");

        kv.Put("device:2", [2]);
        kv.Put("site:1", [9]);
        kv.Put("device:1", [1]);
        kv.Put("device:3", [3]);

        var rows = kv.ScanPrefix("device:", limit: 2);

        Assert.Equal(2, rows.Count);
        Assert.Equal("device:1", Encoding.UTF8.GetString(rows[0].Key.Span));
        Assert.Equal("device:2", Encoding.UTF8.GetString(rows[1].Key.Span));
        Assert.Equal([1], rows[0].Value.ToArray());
        Assert.Equal([2], rows[1].Value.ToArray());
    }

    [Fact]
    public void Reopen_AfterWalOnlyWrites_ReplaysPutAndDelete()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("metadata");
            kv.Put("tenant:1", Encoding.UTF8.GetBytes("alpha"));
            kv.Put("tenant:2", Encoding.UTF8.GetBytes("beta"));
            Assert.True(kv.Delete("tenant:1"));
        }

        using (var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = reopened.Keyspaces.Open("metadata");
            Assert.Null(kv.Get("tenant:1"));
            Assert.Equal("beta", Encoding.UTF8.GetString(kv.Get("tenant:2")!));
            Assert.Equal(1, kv.Count);
        }
    }

    [Fact]
    public void CreateSnapshot_Reopen_LoadsSnapshotAndReplaysLaterWal()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("docs");
            kv.Put("doc:1", Encoding.UTF8.GetBytes("one"));
            long snapshotSequence = kv.CreateSnapshot();
            Assert.True(snapshotSequence > 0);

            kv.Put("doc:2", Encoding.UTF8.GetBytes("two"));
            Assert.True(kv.Delete("doc:1"));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var restored = reopened.Keyspaces.Open("docs");
        Assert.Null(restored.Get("doc:1"));
        Assert.Equal("two", Encoding.UTF8.GetString(restored.Get("doc:2")!));
        Assert.Equal(1, restored.Count);
    }

    [Fact]
    public void Compact_Reopen_LoadsSegmentAndTruncatesWal()
    {
        string walPath;
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("small-objects");
            kv.Put("obj:a", [1, 2, 3]);
            kv.Put("obj:b", [4, 5]);
            long compactedSequence = kv.Compact();
            Assert.True(compactedSequence > 0);
            walPath = Path.Combine(_root, "kv", "keyspaces", "small-objects", "wal", "active.SDBKVWAL");
            Assert.True(File.Exists(walPath));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv2 = reopened.Keyspaces.Open("small-objects");
        Assert.Equal([1, 2, 3], kv2.Get("obj:a"));
        Assert.Equal([4, 5], kv2.Get("obj:b"));
        Assert.Equal(2, kv2.Count);
    }

    [Fact]
    public void KeyspaceManager_List_ReturnsExistingKeyspaces()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        db.Keyspaces.Open("a");
        db.Keyspaces.Open("b");

        Assert.Equal(["a", "b"], db.Keyspaces.List());
    }

    [Fact]
    public void Open_WithInvalidName_ThrowsArgumentException()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Throws<ArgumentException>(() => db.Keyspaces.Open("../bad"));
        Assert.Throws<ArgumentException>(() => db.Keyspaces.Open(""));
    }
}
