using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class MeasurementOrderedStreamingTests : IDisposable
{
    private const string Query = "SELECT time, host, usage, count FROM cpu";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-measurement-order-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false, 3, false)]
    [InlineData(true, 3, false)]
    [InlineData(false, 21, false)]
    [InlineData(true, 21, true)]
    public void CompleteAndPagedOrder_MatchesStableNaturalRows(bool descending, int seriesCount, bool forceSpill)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var db = Open();
        for (int index = 0; index < seriesCount; index++)
        {
            string host = index.ToString(CultureInfo.InvariantCulture);
            Write(db, 100, host, index, index);
            Write(db, 300, host, index + 300, null);
        }
        db.FlushNow();
        for (int index = 0; index < seriesCount; index++)
        {
            string host = index.ToString(CultureInfo.InvariantCulture);
            Write(db, 100, host, index + 1000, null);
            Write(db, 200, host, null, index + 200);
        }

        var naturalRows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, Query)).Rows;
        var expected = (descending
            ? naturalRows.OrderByDescending(static row => (long)row[0]!)
            : naturalRows.OrderBy(static row => (long)row[0]!)).ToArray();
        Assert.Equal(seriesCount * 3, expected.Length);
        string sql = Query + (descending ? " ORDER BY time DESC" : " ORDER BY time ASC");
        var metrics = new SqlExecutionMetrics();
        var options = new SqlExecutionOptions
        {
            StreamResults = true,
            CancellationToken = deadline.Token,
            Metrics = metrics,
            BlockingOperatorMemoryLimitBytes = forceSpill ? 1024 : null,
        };

        using (var stream = OpenStream(db, sql, options))
        {
            Assert.Equal(["time", "host", "usage", "count"], stream.Columns);
            AssertRows(stream, expected);
        }
        var snapshot = metrics.Complete();
        if (forceSpill)
            Assert.True(snapshot.SpillCount > 0);
        Assert.True(snapshot.SortComparisons > 0);
        Assert.InRange(snapshot.PeakSortReaders, 1, 32);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);

        using (var page = OpenStream(db, sql + " LIMIT 7 OFFSET 2", options with { Metrics = null }))
            AssertRows(page, expected.Skip(2).Take(7));
        using (var empty = OpenStream(db, sql + " LIMIT 1 OFFSET 9223372036854775807", options with { Metrics = null }))
            Assert.False(empty.MoveNext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenStatement_PinsActiveFieldsSegmentsTombstonesAndCatalogAcrossWrites(bool forceSpill)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var db = Open();
        Write(db, 100, "a", 1, 10);
        Write(db, 200, "b", 2, 20);
        db.FlushNow();
        Write(db, 300, "a", 3, 30);
        Write(db, 400, "b", 4, 40);

        string sql = Query + " ORDER BY time";
        using var stream = OpenStream(db, sql, new SqlExecutionOptions
        {
            StreamResults = true,
            CancellationToken = deadline.Token,
            BlockingOperatorMemoryLimitBytes = forceSpill ? 160 : null,
        });
        // These publications occur after the statement opens and before any point stream is initialized.
        Assert.True(db.Delete("cpu", new Dictionary<string, string> { ["host"] = "b" }, "usage", 200, 200));
        Write(db, 400, "b", 44, 440);
        Write(db, 250, "new", 25, 250);
        db.FlushNow();
        Assert.True(stream.MoveNext());
        Assert.Equal(new object?[] { 100L, "a", 1d, 10L }, stream.Current);
        Write(db, 500, "a", 5, null);

        AssertRows(stream,
        [
            new object?[] { 200L, "b", 2d, 20L },
            new object?[] { 300L, "a", 3d, 30L },
            new object?[] { 400L, "b", 4d, 40L },
        ]);
        var latest = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
        Assert.Equal(new object?[] { 200L, "b", null, 20L }, latest.Rows[1]);
        Assert.Equal(new object?[] { 250L, "new", 25d, 250L }, latest.Rows[2]);
        Assert.Equal(new object?[] { 400L, "b", 44d, 440L }, latest.Rows[4]);
        Assert.Equal(new object?[] { 500L, "a", 5d, null }, latest.Rows[5]);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    [Fact]
    public void OpenStatement_PinsReadersAndTombstonesAcrossCompaction()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var db = Open();
        Write(db, 100, "a", 1, 10);
        Write(db, 200, "a", 2, null);
        db.FlushNow();
        Write(db, 300, "b", 3, 30);
        Write(db, 400, "b", 4, 40);
        db.FlushNow();
        var tag = new Dictionary<string, string> { ["host"] = "a" };
        Assert.True(db.Delete("cpu", tag, "usage", 200, 200));

        using var stream = OpenStream(db, Query + " ORDER BY time", new SqlExecutionOptions
        {
            StreamResults = true,
            CancellationToken = deadline.Token,
        });
        db.RunUnderMaintenanceLock(() =>
        {
            // The statement still holds the old readers. Release the compaction's own lease
            // before reading, and simulate the worker retiring its consumed tombstone.
            using var lease = db.AcquireReadSnapshot();
            var readers = lease.Readers.ToDictionary(static reader => reader.Header.SegmentId);
            var ids = readers.Keys.Order().ToArray();
            Assert.Equal(2, ids.Length);
            long id = db.AllocateSegmentId();
            string path = TsdbPaths.SegmentPath(db.RootDirectory, id);
            SegmentReplacementManifest.RecordPendingReplacement(db.RootDirectory, id, ids);
            _ = new SegmentCompactor().Execute(
                new CompactionPlan(0, ids), readers, id, path, db.Tombstones,
                cancellationToken: deadline.Token);
            SegmentReplacementManifest.CommitReplacement(db.RootDirectory, id, ids);
            db.Segments.SwapSegments(ids, path);
            db.Tombstones.RemoveAll(db.Tombstones.All);
        });

        Assert.Equal(1, db.Segments.SegmentCount);
        AssertRows(stream,
        [
            new object?[] { 100L, "a", 1d, 10L },
            new object?[] { 300L, "b", 3d, 30L },
            new object?[] { 400L, "b", 4d, 40L },
        ]);
    }

    [Fact]
    public void FullRange_OrdersEpochAndMaximumTimestamp()
    {
        using var db = Open();
        Write(db, 0, "a", 1, 10);
        Write(db, long.MaxValue, "a", 2, 20);
        Write(db, long.MaxValue, "b", 3, null);
        using var stream = OpenStream(
            db, Query + " ORDER BY time DESC", new SqlExecutionOptions { StreamResults = true });
        var timestamps = new List<long>();
        for (int index = 0; index < 3; index++)
        {
            Assert.True(stream.MoveNext());
            timestamps.Add(Assert.IsType<long>(stream.Current[0]));
        }
        Assert.False(stream.MoveNext());
        Assert.Equal([long.MaxValue, long.MaxValue, 0L], timestamps);
    }

    [Fact]
    public void SpillCodec_RoundTripsMeasurementVectorAndGeoPoint()
    {
        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        var vector = new float[] { -0.0f, 3.75f, -17.125f };
        var geo = new GeoPoint(42.625, -72.875);
        SqlSpillRowCodec.WriteRow(writer, [vector, geo]);
        writer.Flush();
        file.Position = 0;
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        var row = SqlSpillRowCodec.ReadRow(reader);
        Assert.Equal(vector, Assert.IsType<float[]>(row[0]));
        Assert.Equal(geo, Assert.IsType<GeoPoint>(row[1]));
        Assert.Equal(file.Length, file.Position);
    }

    private Tsdb Open()
    {
        var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Compaction = new CompactionPolicy { Enabled = false },
        });
        try
        {
            SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, count FIELD INT)");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static void Write(Tsdb db, long timestamp, string host, double? usage, long? count)
    {
        var fields = new Dictionary<string, FieldValue>();
        if (usage is double measurement)
            fields.Add("usage", FieldValue.FromDouble(measurement));
        if (count is long samples)
            fields.Add("count", FieldValue.FromLong(samples));
        db.Write(Point.Create("cpu", timestamp, new Dictionary<string, string> { ["host"] = host }, fields));
    }

    private static SqlStreamingSelectResult OpenStream(Tsdb db, string sql, SqlExecutionOptions options)
        => Assert.IsType<SqlStreamingSelectResult>(SqlExecutor.Execute(db, null, sql, null, null, options));

    private static void AssertRows(SqlStreamingSelectResult stream, IEnumerable<IReadOnlyList<object?>> expected)
    {
        foreach (var row in expected)
        {
            Assert.True(stream.MoveNext());
            Assert.Equal(row, stream.Current);
        }
        Assert.False(stream.MoveNext());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
