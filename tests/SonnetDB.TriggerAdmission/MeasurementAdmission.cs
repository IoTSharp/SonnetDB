using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;

namespace SonnetDB.TriggerAdmission;

/// <summary>
/// 为 M39/#339 采集受边界约束的 measurement 原生写入证据。
/// </summary>
internal static class MeasurementAdmission
{
    private const string Measurement = "admission_measurement";
    private const string Field = "value";

    internal static void Run(AdmissionContext context)
    {
        int[] pointCounts = context.Quick ? [48] : [1, 100, 10_000];
        int seriesCount = context.Quick ? 24 : 1_000;
        int highCardinalitySamples = Math.Min(seriesCount, context.Quick ? 8 : 64);

        for (int countIndex = 0; countIndex < pointCounts.Length; countIndex++)
        {
            context.Check();
            int pointCount = pointCounts[countIndex];
            string suffix = pointCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string writeRoot = context.NewDatabasePath("measurement-write-" + suffix);
            context.Measure(
                "measurement", "write-" + suffix, writeRoot, pointCount, 1,
                () => RunOrderedWrite(context, writeRoot, pointCount),
                "single-series WriteMany, flush, query verification; full mode includes the 8192/8193 chunk boundary");

            string outOfOrderRoot = context.NewDatabasePath("measurement-out-of-order-" + suffix);
            context.Measure(
                "measurement", "out-of-order-" + suffix, outOfOrderRoot, pointCount, 1,
                () => RunOutOfOrderWrite(context, outOfOrderRoot, pointCount),
                "reverse timestamp insertion and ascending query verification");

            string duplicateRoot = context.NewDatabasePath("measurement-duplicate-replay-" + suffix);
            context.Measure(
                "measurement", "duplicate-replay-" + suffix, duplicateRoot, pointCount, 1,
                () => RunDuplicateReplay(context, duplicateRoot, pointCount),
                "replaying the same batch records observed duplicate cardinality without normalizing it away");
        }

        string cardinalityRoot = context.NewDatabasePath("measurement-high-cardinality");
        context.Measure(
            "measurement", "high-cardinality", cardinalityRoot, seriesCount, seriesCount,
            () => RunHighCardinality(context, cardinalityRoot, seriesCount, highCardinalitySamples),
            "bounded unique tag series with deterministic sampled reads");
    }

    private static Dictionary<string, double> RunOrderedWrite(
        AdmissionContext context, string root, int pointCount)
    {
        using var db = Tsdb.Open(AdmissionContext.Options(root));
        var points = new Point[pointCount];
        for (int index = 0; index < pointCount; index++)
        {
            context.Check();
            points[index] = CreatePoint(Measurement, "series-ordered", index, index);
        }

        AdmissionContext.Require(db.WriteMany(points) == pointCount, "WriteMany did not accept the complete ordered batch.");
        db.FlushNow();
        var entry = RequireSingleSeries(db, "series-ordered");
        DataPoint[] observed = db.Query.Execute(new PointQuery(entry.Id, Field, new TimeRange(0, pointCount))).ToArray();
        ValidateSequence(context, observed, pointCount, "ordered write");
        return Metrics(("observedPoints", observed.Length), ("series", 1));
    }

    private static Dictionary<string, double> RunOutOfOrderWrite(
        AdmissionContext context, string root, int pointCount)
    {
        using var db = Tsdb.Open(AdmissionContext.Options(root));
        var points = new Point[pointCount];
        for (int index = 0; index < pointCount; index++)
        {
            context.Check();
            int timestamp = pointCount - index - 1;
            points[index] = CreatePoint(Measurement, "series-out-of-order", timestamp, timestamp);
        }

        AdmissionContext.Require(db.WriteMany(points) == pointCount, "WriteMany did not accept the complete out-of-order batch.");
        db.FlushNow();
        var entry = RequireSingleSeries(db, "series-out-of-order");
        DataPoint[] observed = db.Query.Execute(new PointQuery(entry.Id, Field, new TimeRange(0, pointCount))).ToArray();
        ValidateSequence(context, observed, pointCount, "out-of-order write");
        return Metrics(("observedPoints", observed.Length), ("timestampsAscending", 1));
    }

    private static Dictionary<string, double> RunDuplicateReplay(
        AdmissionContext context, string root, int pointCount)
    {
        using var db = Tsdb.Open(AdmissionContext.Options(root));
        var points = new Point[pointCount];
        for (int index = 0; index < pointCount; index++)
        {
            context.Check();
            points[index] = CreatePoint(Measurement, "series-duplicate", index, index);
        }

        const string batchId = "admission-duplicate-replay";
        AdmissionContext.Require(db.WriteMany(points, batchId) == pointCount, "Initial idempotent batch was incomplete.");
        AdmissionContext.Require(db.WriteMany(points, batchId) == 0, "Idempotent replay wrote new points.");
        db.FlushNow();
        db.Dispose();
        using var reopened = Tsdb.Open(AdmissionContext.Options(root));
        AdmissionContext.Require(reopened.WriteMany(points, batchId) == 0, "Reopen idempotent replay wrote new points.");
        var entry = RequireSingleSeries(reopened, "series-duplicate");
        DataPoint[] observed = reopened.Query.Execute(new PointQuery(entry.Id, Field, new TimeRange(0, pointCount))).ToArray();
        AdmissionContext.Require(observed.Length == pointCount,
            "Idempotent replay changed the accepted point multiset.");
        return Metrics(("initialPoints", pointCount), ("observedPoints", observed.Length), ("duplicatePoints", 0), ("idempotentReplay", 1));
    }

    private static Dictionary<string, double> RunHighCardinality(
        AdmissionContext context, string root, int seriesCount, int sampleCount)
    {
        using var db = Tsdb.Open(AdmissionContext.Options(root));
        var points = new Point[seriesCount];
        for (int series = 0; series < seriesCount; series++)
        {
            context.Check();
            points[series] = CreatePoint(Measurement, $"series-{series:D6}", series, series);
        }

        AdmissionContext.Require(db.WriteMany(points) == seriesCount, "High-cardinality WriteMany was incomplete.");
        db.FlushNow();
        AdmissionContext.Require(db.Catalog.Count == seriesCount, "High-cardinality catalog count did not match writes.");

        int verified = 0;
        foreach (int series in EvenlySpacedIndices(seriesCount, sampleCount))
        {
            context.Check();
            var entry = RequireSingleSeries(db, $"series-{series:D6}");
            DataPoint[] observed = db.Query.Execute(new PointQuery(entry.Id, Field, new TimeRange(series, series + 1))).ToArray();
            AdmissionContext.Require(observed.Length == 1
                && observed[0].Timestamp == series
                && observed[0].Value.Type == Storage.Format.FieldType.Int64
                && observed[0].Value.AsLong() == series,
                $"High-cardinality sample {series} failed deterministic verification.");
            verified++;
        }

        return Metrics(("series", seriesCount), ("sampledSeries", verified), ("catalogEntries", db.Catalog.Count));
    }

    private static Point CreatePoint(string measurement, string series, long timestamp, long value)
        => Point.Create(
            measurement,
            timestamp,
            new Dictionary<string, string> { ["series"] = series },
            new Dictionary<string, FieldValue> { [Field] = FieldValue.FromLong(value) });

    private static SonnetDB.Catalog.SeriesEntry RequireSingleSeries(Tsdb db, string series)
    {
        var entries = db.Catalog.Find(Measurement, new Dictionary<string, string> { ["series"] = series });
        return entries.Count == 1
            ? entries[0]
            : throw new InvalidDataException($"Expected exactly one series '{series}', observed {entries.Count}.");
    }

    private static void ValidateSequence(AdmissionContext context, DataPoint[] points, int expectedCount, string scenario)
    {
        AdmissionContext.Require(points.Length == expectedCount, $"{scenario} returned {points.Length} points; expected {expectedCount}.");
        for (int index = 0; index < points.Length; index++)
        {
            context.Check();
            AdmissionContext.Require(points[index].Timestamp == index
                && points[index].Value.Type == Storage.Format.FieldType.Int64
                && points[index].Value.AsLong() == index,
                $"{scenario} returned an unexpected point at index {index}.");
        }
    }

    private static Dictionary<string, double> Metrics(params (string Name, double Value)[] values)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            metrics[name] = value;
        return metrics;
    }

    private static IEnumerable<int> EvenlySpacedIndices(int count, int samples)
    {
        if (samples <= 1)
        {
            yield return 0;
            yield break;
        }

        for (int index = 0; index < samples; index++)
            yield return (int)(((long)index * (count - 1)) / (samples - 1));
    }
}
