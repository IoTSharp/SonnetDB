using SonnetDB.Catalog;
using SonnetDB.Model;
using Xunit;

namespace SonnetDB.Core.Tests.Catalog;

/// <summary>
/// <see cref="SeriesCatalog"/> 单元测试。
/// </summary>
public sealed class SeriesCatalogTests
{
    private static SeriesCatalog CreateCatalog() => new();

    private static IReadOnlyDictionary<string, string> Tags(params (string Key, string Value)[] tags)
    {
        var dict = new Dictionary<string, string>(tags.Length, StringComparer.Ordinal);
        foreach (var (key, value) in tags)
            dict[key] = value;
        return dict;
    }

    // ── GetOrAdd 幂等性 ───────────────────────────────────────────────────────

    [Fact]
    public void GetOrAdd_SameKey_ReturnsSameInstance()
    {
        var catalog = CreateCatalog();
        var tags = new Dictionary<string, string> { ["host"] = "srv1" };
        var e1 = catalog.GetOrAdd("cpu", tags);
        var e2 = catalog.GetOrAdd("cpu", tags);
        Assert.Same(e1, e2);
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void GetOrAdd_DifferentMeasurements_DifferentEntries()
    {
        var catalog = CreateCatalog();
        var e1 = catalog.GetOrAdd("cpu", null);
        var e2 = catalog.GetOrAdd("mem", null);
        Assert.NotSame(e1, e2);
        Assert.NotEqual(e1.Id, e2.Id);
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void GetOrAdd_DifferentTags_DifferentEntries()
    {
        var catalog = CreateCatalog();
        var e1 = catalog.GetOrAdd("cpu", new Dictionary<string, string> { ["host"] = "a" });
        var e2 = catalog.GetOrAdd("cpu", new Dictionary<string, string> { ["host"] = "b" });
        Assert.NotSame(e1, e2);
        Assert.NotEqual(e1.Id, e2.Id);
    }

    [Fact]
    public void GetOrAdd_UnorderedTags_ReturnsSameEntry()
    {
        var catalog = CreateCatalog();
        var tagsAB = new Dictionary<string, string> { ["alpha"] = "1", ["beta"] = "2" };
        var tagsBA = new Dictionary<string, string> { ["beta"] = "2", ["alpha"] = "1" };
        var e1 = catalog.GetOrAdd("cpu", tagsAB);
        var e2 = catalog.GetOrAdd("cpu", tagsBA);
        Assert.Same(e1, e2);
        Assert.Equal(1, catalog.Count);
    }

    // ── GetOrAdd(Point) ───────────────────────────────────────────────────────

    [Fact]
    public void GetOrAdd_FromPoint_ReturnsSameEntryAsFromKey()
    {
        var catalog = CreateCatalog();
        var tags = new Dictionary<string, string> { ["region"] = "us" };
        var point = Point.Create("temp", 1000L, tags,
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(42.0) });

        var e1 = catalog.GetOrAdd(point);
        var e2 = catalog.GetOrAdd("temp", tags);
        Assert.Same(e1, e2);
    }

    [Fact]
    public void GetOrAdd_FromPoint_NullThrows()
        => Assert.Throws<ArgumentNullException>(() => CreateCatalog().GetOrAdd(null!));

    // ── TryGet ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_ById_HitAndMiss()
    {
        var catalog = CreateCatalog();
        var entry = catalog.GetOrAdd("cpu", null);
        Assert.Same(entry, catalog.TryGet(entry.Id));
        Assert.Null(catalog.TryGet(0xDEADBEEFDEADBEEFUL));
    }

    [Fact]
    public void TryGet_ByKey_HitAndMiss()
    {
        var catalog = CreateCatalog();
        var entry = catalog.GetOrAdd("cpu", new Dictionary<string, string> { ["host"] = "h" });
        var key = new SeriesKey("cpu", new Dictionary<string, string> { ["host"] = "h" });
        Assert.Same(entry, catalog.TryGet(in key));

        var missing = new SeriesKey("cpu", null);
        Assert.Null(catalog.TryGet(in missing));
    }

    // ── Find ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Find_ByMeasurement_ReturnsAllMatchingEntries()
    {
        var catalog = CreateCatalog();
        catalog.GetOrAdd("cpu", new Dictionary<string, string> { ["host"] = "a" });
        catalog.GetOrAdd("cpu", new Dictionary<string, string> { ["host"] = "b" });
        catalog.GetOrAdd("mem", new Dictionary<string, string> { ["host"] = "a" });

        var cpuEntries = catalog.Find("cpu", null);
        Assert.Equal(2, cpuEntries.Count);
        Assert.All(cpuEntries, e => Assert.Equal("cpu", e.Measurement));
    }

    [Fact]
    public void Find_ByMeasurementAndTag_ReturnsOnlyMatching()
    {
        var catalog = CreateCatalog();
        catalog.GetOrAdd("cpu", new Dictionary<string, string> { ["host"] = "server-1" });
        catalog.GetOrAdd("cpu", new Dictionary<string, string> { ["host"] = "server-2" });

        var results = catalog.Find("cpu", new Dictionary<string, string> { ["host"] = "server-1" });
        Assert.Single(results);
        Assert.Equal("server-1", results[0].Tags["host"]);
    }

    [Fact]
    public void Find_NoMatch_ReturnsEmpty()
    {
        var catalog = CreateCatalog();
        catalog.GetOrAdd("cpu", null);
        Assert.Empty(catalog.Find("disk", null));
    }

    [Fact]
    public void Find_AfterAdditionalSeriesAdded_PublishesUpdatedTagSnapshot()
    {
        var catalog = CreateCatalog();
        var first = catalog.GetOrAdd("cpu", Tags(("host", "a"), ("region", "east")));

        Assert.Empty(catalog.Find("cpu", Tags(("host", "b"))));

        var second = catalog.GetOrAdd("cpu", Tags(("host", "b"), ("region", "east")));

        Assert.Same(first, Assert.Single(catalog.Find("cpu", Tags(("host", "a")))));
        Assert.Same(second, Assert.Single(catalog.Find("cpu", Tags(("host", "b")))));
        Assert.Equal(2, catalog.Find("cpu", Tags(("region", "east"))).Count);
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_ReturnsAllEntries()
    {
        var catalog = CreateCatalog();
        catalog.GetOrAdd("cpu", null);
        catalog.GetOrAdd("mem", null);
        var snap = catalog.Snapshot();
        Assert.Equal(2, snap.Count);
    }

    [Fact]
    public void Snapshot_BeforeUpdate_RemainsStableAndNewSnapshotSeesEntry()
    {
        var catalog = CreateCatalog();
        var first = catalog.GetOrAdd("cpu", Tags(("host", "a")));
        var before = catalog.Snapshot();

        var second = catalog.GetOrAdd("cpu", Tags(("host", "b")));
        var after = catalog.Snapshot();

        Assert.Single(before);
        Assert.Same(first, before[0]);
        Assert.Equal(2, after.Count);
        Assert.Contains(after, e => e.Id == second.Id);
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var catalog = CreateCatalog();
        catalog.GetOrAdd("cpu", null);
        catalog.Clear();
        Assert.Equal(0, catalog.Count);
        Assert.Empty(catalog.Snapshot());
    }

    // ── Tags 不可变性 ─────────────────────────────────────────────────────────

    [Fact]
    public void Tags_Immutability_OriginalDictDoesNotAffectEntry()
    {
        var catalog = CreateCatalog();
        var originalTags = new Dictionary<string, string> { ["host"] = "original" };
        var entry = catalog.GetOrAdd("cpu", originalTags);

        // 修改原始字典
        originalTags["host"] = "mutated";

        // entry.Tags 应保持不变
        Assert.Equal("original", entry.Tags["host"]);
    }

    // ── 并发测试 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Concurrent_SameKey_AllReturnSameInstance()
    {
        var catalog = CreateCatalog();
        var tags = new Dictionary<string, string> { ["host"] = "srv" };

        var results = new SeriesEntry[1000];
        Parallel.For(0, 1000, i =>
        {
            results[i] = catalog.GetOrAdd("cpu", tags);
        });

        Assert.Equal(1, catalog.Count);
        var first = results[0];
        Assert.All(results, e => Assert.Same(first, e));
    }

    [Fact]
    public void Concurrent_DifferentKeys_AllUnique()
    {
        var catalog = CreateCatalog();
        const int count = 500;

        Parallel.For(0, count, i =>
        {
            catalog.GetOrAdd("series", new Dictionary<string, string> { ["id"] = i.ToString() });
        });

        Assert.Equal(count, catalog.Count);

        // 确保没有重复 ID
        var ids = catalog.Snapshot().Select(e => e.Id).ToHashSet();
        Assert.Equal(count, ids.Count);
    }

    [Fact]
    public async Task Concurrent_ReadsWhileAddingSeries_AreSafeAndFinalVisible()
    {
        var catalog = CreateCatalog();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var stop = new System.Threading.CancellationTokenSource();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var snapshot = catalog.Snapshot();
                    foreach (var entry in snapshot)
                    {
                        Assert.Same(entry, catalog.TryGet(entry.Id));
                        var key = entry.Key;
                        Assert.Same(entry, catalog.TryGet(in key));
                    }
                    GC.KeepAlive(catalog.Find("cpu", Tags(("bucket", "3"))));
                    GC.KeepAlive(catalog.Find("cpu", null));
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    break;
                }
            }
        })).ToArray();

        const int count = 120;
        for (int i = 0; i < count; i++)
        {
            catalog.GetOrAdd("cpu", Tags(
                ("host", "h" + i),
                ("bucket", (i % 6).ToString())));
        }

        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(errors);
        Assert.Equal(count, catalog.Count);
        Assert.Equal(count / 6, catalog.Find("cpu", Tags(("bucket", "3"))).Count);
    }

    [Fact]
    public void GetOrAdd_HighCardinality_EachEntryImmediatelyVisibleById_KeyAndFind()
    {
        // I5：改增量并发字典后，每新增一条 series 必须对随后读立即可见（不能因防抖/批延迟丢可见性）。
        var catalog = CreateCatalog();
        const int count = 5000;

        for (int i = 0; i < count; i++)
        {
            var tags = Tags(("host", "h" + i), ("dc", (i % 4).ToString()));
            var entry = catalog.GetOrAdd("cpu", tags);

            // 刚加入即刻按 id / key / Find 三条读路径都应命中同一实例。
            Assert.Same(entry, catalog.TryGet(entry.Id));
            var key = entry.Key;
            Assert.Same(entry, catalog.TryGet(in key));
            Assert.Contains(entry, catalog.Find("cpu", tags));
        }

        Assert.Equal(count, catalog.Count);
        Assert.Equal(count / 4, catalog.Find("cpu", Tags(("dc", "2"))).Count);
    }
}
