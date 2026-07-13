using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;

namespace SonnetDB.EcosystemSoak;

internal static class SpecializedSoakRunner
{
    private const string FieldName = "value";
    private const long HighCardinalityTimestampBase = 2_200_000_000_000L;
    private const long SmallSegmentsTimestampBase = 2_210_000_000_000L;
    private const long ChaosTimestampBase = 2_220_000_000_000L;
    private const long ChaosExpiredTimestampBase = 1_000_000_000_000L;
    private const long ManyMeasurementsCurrentTimestampBase = 2_230_000_000_000L;
    private const long ManyMeasurementsExpiredTimestampBase = 1_100_000_000_000L;

    /// <summary>按专项 profile 执行单轮负载并返回统一阶段结果。</summary>
    public static Task<SoakCycleResult> RunCycleAsync(SoakOptions options, int cycle)
        => options.Profile switch
        {
            "high-cardinality" => RunHighCardinalityAsync(options, cycle),
            "small-segments" => RunSmallSegmentsAsync(options, cycle),
            "maintenance-chaos" => RunMaintenanceChaosAsync(options, cycle),
            "many-measurements" => RunManyMeasurementsAsync(options, cycle),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Profile, "未知专项 profile。"),
        };

    /// <summary>在子进程中持续写入并开启后台维护，等待父进程注入强杀。</summary>
    public static int RunMaintenanceChaosWorker(string[] args)
    {
        if (args.Length != 7
            || !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out long startSequence)
            || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int seriesCount)
            || !int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out int pointsPerBatch)
            || !int.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out int seed)
            || startSequence < 0
            || seriesCount <= 0
            || pointsPerBatch <= 0)
        {
            return 2;
        }

        string root = args[1];
        string progressPath = args[2];
        Directory.CreateDirectory(root);
        var random = new Random(seed);
        using var db = OpenMaintenanceChaosWorker(root, pointsPerBatch);
        long sequence = startSequence;
        int batch = 0;

        while (true)
        {
            var points = new Point[pointsPerBatch + 1];
            for (int index = 0; index < pointsPerBatch; index++)
            {
                long current = sequence++;
                int series = (int)(current % seriesCount);
                points[index] = CreateSeriesPoint(
                    "maintenance_chaos",
                    series,
                    ChaosTimestampBase + current,
                    current);
            }

            int expiredSeries = batch % seriesCount;
            points[^1] = CreateSeriesPoint(
                "maintenance_chaos",
                expiredSeries,
                ChaosExpiredTimestampBase + batch,
                -batch - 1L);
            db.WriteMany(points);
            WriteProgress(progressPath, sequence - 1);
            batch++;
            Thread.Sleep(random.Next(1, 6));
        }
    }

    private static async Task<SoakCycleResult> RunHighCardinalityAsync(SoakOptions options, int cycle)
    {
        string root = ProfileRoot(options, cycle);
        Directory.CreateDirectory(root);
        var phases = new List<SoakPhaseResult>();
        var startedUtc = DateTimeOffset.UtcNow;

        phases.Add(await Program.MeasureAsync("high_cardinality_write", () =>
        {
            using var db = OpenManual(root);
            const int maximumBatchSize = 4_096;
            for (int offset = 0; offset < options.Series; offset += maximumBatchSize)
            {
                int count = Math.Min(maximumBatchSize, options.Series - offset);
                var points = new Point[count];
                for (int index = 0; index < count; index++)
                {
                    int series = offset + index;
                    points[index] = CreateSeriesPoint(
                        "high_cardinality",
                        series,
                        HighCardinalityTimestampBase + series,
                        series);
                }

                db.WriteMany(points);
            }

            db.FlushNow();
            if (db.Catalog.Count != options.Series)
                throw new InvalidDataException($"Series 数量不一致：期望 {options.Series}，实际 {db.Catalog.Count}。");

            return Task.FromResult(Program.PhaseData(
                options.Series,
                ("series", Format(options.Series)),
                ("measurements", Format(db.Measurements.Count)),
                ("segments", Format(db.Segments.SegmentCount))));
        }).ConfigureAwait(false));

        phases.Add(await Program.MeasureAsync("high_cardinality_recovery", () =>
        {
            var recovery = new List<double>(options.RecoverySamples);
            SoakIntegritySummary? integrity = null;
            var queryLatency = new List<double>(options.QuerySamples);
            int sampleCount = Math.Min(options.Series, Math.Max(1, Math.Min(options.QuerySamples, 1_024)));

            for (int sample = 0; sample < options.RecoverySamples; sample++)
            {
                var watch = Stopwatch.StartNew();
                using var db = OpenManual(root);
                watch.Stop();
                recovery.Add(watch.Elapsed.TotalMilliseconds);
                if (db.Catalog.Count != options.Series)
                    throw new InvalidDataException($"冷启动后 Series 数量不一致：期望 {options.Series}，实际 {db.Catalog.Count}。");

                if (sample == options.RecoverySamples - 1)
                {
                    integrity = ValidateHighCardinalitySample(db, options.Series, sampleCount);
                    queryLatency.AddRange(MeasureSeriesQueries(
                        db,
                        "high_cardinality",
                        options.Series,
                        options.QuerySamples,
                        new TimeRange(HighCardinalityTimestampBase, HighCardinalityTimestampBase + options.Series - 1L),
                        options.RandomSeed));
                }
            }

            EnsureStrictIntegrity(integrity!);
            return Task.FromResult(new PhaseMeasurement(
                options.Series,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["catalogSeries"] = Format(options.Series),
                    ["validatedSeries"] = Format(sampleCount),
                    ["recoverySamples"] = Format(recovery.Count),
                    ["querySamples"] = Format(queryLatency.Count),
                    ["validation"] = "deterministic sampled series/time/value",
                },
                integrity,
                recovery,
                queryLatency));
        }).ConfigureAwait(false));

        return new SoakCycleResult(cycle, startedUtc, DateTimeOffset.UtcNow, phases);
    }

    private static async Task<SoakCycleResult> RunSmallSegmentsAsync(SoakOptions options, int cycle)
    {
        string root = ProfileRoot(options, cycle);
        Directory.CreateDirectory(root);
        var phases = new List<SoakPhaseResult>();
        var startedUtc = DateTimeOffset.UtcNow;
        long totalPoints = checked((long)options.TargetSegments * options.PointsPerSegment);

        phases.Add(await Program.MeasureAsync("small_segments_write", () =>
        {
            using var db = OpenManual(root);
            long sequence = 0;
            for (int segment = 0; segment < options.TargetSegments; segment++)
            {
                var points = new Point[options.PointsPerSegment];
                for (int index = 0; index < points.Length; index++)
                {
                    long current = sequence++;
                    points[index] = CreateSeriesPoint(
                        "small_segments",
                        (int)(current % options.Series),
                        SmallSegmentsTimestampBase + current,
                        current);
                }

                db.WriteMany(points);
                db.FlushNow();
            }

            int segments = db.Segments.SegmentCount;
            if (segments != options.TargetSegments)
                throw new InvalidDataException($"小段数量不一致：期望 {options.TargetSegments}，实际 {segments}。");
            long bytes = db.ListSegments().Sum(static item => new FileInfo(item.Path).Length);
            return Task.FromResult(Program.PhaseData(
                totalPoints,
                ("segments", Format(segments)),
                ("pointsPerSegment", Format(options.PointsPerSegment)),
                ("segmentBytes", Format(bytes))));
        }).ConfigureAwait(false));

        phases.Add(await Program.MeasureAsync("small_segments_recovery_and_integrity", () =>
        {
            var recovery = new List<double>(options.RecoverySamples);
            var queryLatency = new List<double>(options.QuerySamples);
            SoakIntegritySummary? integrity = null;
            for (int sample = 0; sample < options.RecoverySamples; sample++)
            {
                var watch = Stopwatch.StartNew();
                using var db = OpenManual(root);
                watch.Stop();
                recovery.Add(watch.Elapsed.TotalMilliseconds);
                if (db.Segments.SegmentCount != options.TargetSegments)
                    throw new InvalidDataException("冷启动后 segment 数量发生变化。");

                if (sample == options.RecoverySamples - 1)
                {
                    integrity = ValidateModuloSeries(
                        db,
                        "small_segments",
                        options.Series,
                        totalPoints,
                        SmallSegmentsTimestampBase,
                        "all persisted points");
                    queryLatency.AddRange(MeasureSeriesQueries(
                        db,
                        "small_segments",
                        options.Series,
                        options.QuerySamples,
                        new TimeRange(SmallSegmentsTimestampBase, SmallSegmentsTimestampBase + totalPoints - 1),
                        options.RandomSeed));
                }
            }

            EnsureStrictIntegrity(integrity!);
            return Task.FromResult(new PhaseMeasurement(
                totalPoints,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["segments"] = Format(options.TargetSegments),
                    ["recoverySamples"] = Format(recovery.Count),
                    ["querySamples"] = Format(queryLatency.Count),
                },
                integrity,
                recovery,
                queryLatency));
        }).ConfigureAwait(false));

        return new SoakCycleResult(cycle, startedUtc, DateTimeOffset.UtcNow, phases);
    }

    private static async Task<SoakCycleResult> RunMaintenanceChaosAsync(SoakOptions options, int cycle)
    {
        string root = ProfileRoot(options, cycle);
        Directory.CreateDirectory(root);
        var phases = new List<SoakPhaseResult>();
        var startedUtc = DateTimeOffset.UtcNow;

        phases.Add(await Program.MeasureAsync("maintenance_chaos_kill_reopen", () =>
        {
            var random = new Random(options.RandomSeed + cycle);
            var recovery = new List<double>(options.RestartCount);
            long nextSequence = 0;
            long acknowledgedSequence = -1;
            int retentionDroppedSegments = 0;
            int retentionInjectedTombstones = 0;
            long retentionElapsedMicros = 0;
            long minimumSegments = long.MaxValue;
            long maximumSegments = 0;
            SoakIntegritySummary? integrity = null;

            for (int restart = 0; restart < options.RestartCount; restart++)
            {
                string progressPath = Path.Combine(root, $"progress-{restart:D4}.txt");
                using var process = StartMaintenanceChaosWorker(
                    root,
                    progressPath,
                    nextSequence,
                    options.Series,
                    options.PointsPerBatch,
                    options.RandomSeed + restart);
                long minimumPoints = Math.Max(
                    options.Series,
                    checked((long)options.MaintenanceBatches * options.PointsPerBatch));
                long target = checked(nextSequence + minimumPoints - 1L);

                try
                {
                    WaitForProgress(process, progressPath, target, TimeSpan.FromMinutes(2));
                    Thread.Sleep(random.Next(5, 51));
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit((int)TimeSpan.FromSeconds(20).TotalMilliseconds))
                        throw new TimeoutException("maintenance-chaos worker 未在 kill 后退出。");
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                }

                acknowledgedSequence = Math.Max(acknowledgedSequence, ReadProgress(progressPath));
                var watch = Stopwatch.StartNew();
                using var recovered = OpenManual(root);
                watch.Stop();
                recovery.Add(watch.Elapsed.TotalMilliseconds);
                integrity = ValidateChaosSeries(
                    recovered,
                    options.Series,
                    acknowledgedSequence + 1L,
                    out long maximumObservedSequence);
                if (integrity.MissingPoints != 0 || integrity.DuplicatePoints != 0 || integrity.ValueMismatches != 0)
                {
                    throw new InvalidDataException(
                        $"maintenance-chaos 第 {restart + 1} 轮检测失败：expected={integrity.ExpectedPoints}, "
                        + $"observed={integrity.ObservedPoints}, missing={integrity.MissingPoints}, "
                        + $"duplicate={integrity.DuplicatePoints}, unexpected={integrity.UnexpectedPoints}, "
                        + $"valueMismatch={integrity.ValueMismatches}, digest={integrity.DigestMatches}。");
                }

                nextSequence = Math.Max(acknowledgedSequence, maximumObservedSequence) + 1L;
                long segmentCount = recovered.Segments.SegmentCount;
                minimumSegments = Math.Min(minimumSegments, segmentCount);
                maximumSegments = Math.Max(maximumSegments, segmentCount);
            }

            var queryLatency = new List<double>(options.QuerySamples);
            long expiredRemaining = 0;
            using (var final = OpenMaintenanceValidation(root))
            {
                var retention = final.Retention!.RunOnce();
                retentionDroppedSegments = retention.DroppedSegments;
                retentionInjectedTombstones = retention.InjectedTombstones;
                retentionElapsedMicros = retention.ElapsedMicros;
                for (int series = 0; series < options.Series; series++)
                {
                    var entry = FindSeries(final, "maintenance_chaos", series);
                    expiredRemaining += final.Query.Execute(new PointQuery(
                        entry.Id,
                        FieldName,
                        new TimeRange(ChaosExpiredTimestampBase, ChaosTimestampBase - 1))).LongCount();
                }

                queryLatency.AddRange(MeasureSeriesQueries(
                    final,
                    "maintenance_chaos",
                    options.Series,
                    options.QuerySamples,
                    TimeRange.From(ChaosTimestampBase),
                    options.RandomSeed));
            }

            if (expiredRemaining != 0)
                throw new InvalidDataException($"Retention 后仍有 {expiredRemaining} 个过期点可见。");

            return Task.FromResult(new PhaseMeasurement(
                acknowledgedSequence + 1L,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["randomSeed"] = Format(options.RandomSeed),
                    ["restarts"] = Format(options.RestartCount),
                    ["acknowledgedPoints"] = Format(acknowledgedSequence + 1L),
                    ["unacknowledgedButRecovered"] = Format(integrity!.UnexpectedPoints),
                    ["minimumSegmentsAfterRecovery"] = Format(minimumSegments == long.MaxValue ? 0 : minimumSegments),
                    ["maximumSegmentsAfterRecovery"] = Format(maximumSegments),
                    ["retentionRunOnce"] = "true",
                    ["retentionDroppedSegments"] = Format(retentionDroppedSegments),
                    ["retentionInjectedTombstones"] = Format(retentionInjectedTombstones),
                    ["retentionElapsedMicros"] = Format(retentionElapsedMicros),
                    ["expiredPointsVisibleAfterRetention"] = Format(expiredRemaining),
                },
                integrity,
                recovery,
                queryLatency));
        }).ConfigureAwait(false));

        return new SoakCycleResult(cycle, startedUtc, DateTimeOffset.UtcNow, phases);
    }

    private static async Task<SoakCycleResult> RunManyMeasurementsAsync(SoakOptions options, int cycle)
    {
        string root = ProfileRoot(options, cycle);
        string backupRoot = Path.Combine(Path.GetDirectoryName(root)!, $"backup-{cycle:D4}");
        Directory.CreateDirectory(root);
        var phases = new List<SoakPhaseResult>();
        var startedUtc = DateTimeOffset.UtcNow;
        long totalPoints = checked((long)options.Measurements * options.PointsPerMeasurement);

        phases.Add(await Program.MeasureAsync("many_measurements_write", () =>
        {
            using var db = OpenManual(root);
            int measurementsPerSegment = Math.Max(1, (int)Math.Ceiling((double)options.Measurements / options.TargetSegments));
            long written = 0;
            for (int measurement = 0; measurement < options.Measurements; measurement++)
            {
                string name = MeasurementName(measurement);
                var points = new Point[options.PointsPerMeasurement];
                long timestampBase = measurement % 2 == 0
                    ? ManyMeasurementsExpiredTimestampBase
                    : ManyMeasurementsCurrentTimestampBase;
                for (int point = 0; point < points.Length; point++)
                {
                    long value = ((long)measurement * options.PointsPerMeasurement) + point;
                    points[point] = Point.Create(
                        name,
                        timestampBase + point,
                        new Dictionary<string, string> { ["host"] = $"edge-{measurement % 32:D2}" },
                        new Dictionary<string, FieldValue> { [FieldName] = FieldValue.FromLong(value) });
                }

                db.WriteMany(points);
                written += points.Length;
                if ((measurement + 1) % measurementsPerSegment == 0)
                    db.FlushNow();
            }

            db.FlushNow();
            if (db.Measurements.Count != options.Measurements)
                throw new InvalidDataException("大量 measurement 写入后的 schema 数量不一致。");
            return Task.FromResult(Program.PhaseData(
                written,
                ("measurements", Format(options.Measurements)),
                ("segments", Format(db.Segments.SegmentCount)),
                ("series", Format(db.Catalog.Count))));
        }).ConfigureAwait(false));

        phases.Add(await Program.MeasureAsync("many_measurements_cold_start", () =>
        {
            var recovery = new List<double>(options.RecoverySamples);
            var queryLatency = new List<double>(options.QuerySamples);
            for (int sample = 0; sample < options.RecoverySamples; sample++)
            {
                var watch = Stopwatch.StartNew();
                using var db = OpenManual(root);
                watch.Stop();
                recovery.Add(watch.Elapsed.TotalMilliseconds);
                if (db.Measurements.Count != options.Measurements)
                    throw new InvalidDataException("大量 measurement 冷启动后的 schema 数量不一致。");
                if (sample == options.RecoverySamples - 1)
                    queryLatency.AddRange(MeasureMeasurementQueries(db, options));
            }

            return Task.FromResult(new PhaseMeasurement(
                options.Measurements,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["measurements"] = Format(options.Measurements),
                    ["recoverySamples"] = Format(recovery.Count),
                    ["querySamples"] = Format(queryLatency.Count),
                },
                null,
                recovery,
                queryLatency));
        }).ConfigureAwait(false));

        phases.Add(await Program.MeasureAsync("many_measurements_backup_scan", () =>
        {
            using var db = OpenManual(root);
            var manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupRoot,
            });
            long bytes = manifest.Files.Sum(static file => file.SizeBytes);
            return Task.FromResult(Program.PhaseData(
                bytes,
                ("files", Format(manifest.Files.Count)),
                ("bytes", Format(bytes)),
                ("segments", Format(manifest.Consistency.SegmentCount))));
        }).ConfigureAwait(false));

        phases.Add(await Program.MeasureAsync("many_measurements_retention_and_drop", () =>
        {
            int dropCount = Math.Min(options.DropMeasurements, options.Measurements / 2);
            int validated = Math.Min(options.QuerySamples, Math.Max(1, options.Measurements));
            long expectedSamplePoints = 0;
            long observedSamplePoints = 0;
            long missing = 0;
            long duplicates = 0;
            long unexpected = 0;
            long mismatches = 0;
            int validatedMeasurements = 0;

            using (var db = OpenManyMeasurementsMaintenance(root))
            {
                var retention = db.Retention!.RunOnce();
                for (int measurement = 0; measurement < options.Measurements; measurement += Math.Max(1, options.Measurements / validated))
                {
                    if (validatedMeasurements >= validated)
                        break;

                    string name = MeasurementName(measurement);
                    long expected = measurement % 2 == 0 ? 0 : options.PointsPerMeasurement;
                    expectedSamplePoints += expected;
                    validatedMeasurements++;
                    var entries = db.Catalog.Find(name, null);
                    if (entries.Count != 1)
                    {
                        missing += expected;
                        continue;
                    }

                    var points = db.Query.Execute(new PointQuery(entries[0].Id, FieldName, TimeRange.All)).ToArray();
                    observedSamplePoints += points.Length;
                    if (expected == 0 && points.Length != 0)
                        unexpected += points.Length;
                    if (expected != 0)
                    {
                        var seen = new bool[options.PointsPerMeasurement];
                        foreach (var point in points)
                        {
                            long pointIndex = point.Timestamp - ManyMeasurementsCurrentTimestampBase;
                            if (pointIndex < 0 || pointIndex >= options.PointsPerMeasurement)
                            {
                                unexpected++;
                                continue;
                            }

                            int pointOffset = (int)pointIndex;
                            if (seen[pointOffset])
                            {
                                duplicates++;
                                continue;
                            }

                            seen[pointOffset] = true;
                            if (point.Value.Type != FieldType.Int64
                                || point.Value.AsLong() != ((long)measurement * options.PointsPerMeasurement) + pointIndex)
                            {
                                mismatches++;
                            }
                        }

                        missing += seen.LongCount(static item => !item);
                    }
                }

                for (int index = 0; index < dropCount; index++)
                {
                    int measurement = (index * 2) + 1;
                    if (!db.DropMeasurement(MeasurementName(measurement)))
                        throw new InvalidDataException($"DropMeasurement 未找到 {MeasurementName(measurement)}。");
                }

                int expectedMeasurements = options.Measurements - dropCount;
                if (db.Measurements.Count != expectedMeasurements)
                    throw new InvalidDataException("DropMeasurement 后 measurement 数量不一致。");

                var integrity = new SoakIntegritySummary(
                    "sampled measurements after retention",
                    expectedSamplePoints,
                    observedSamplePoints,
                    missing,
                    duplicates,
                    unexpected,
                    mismatches,
                    missing == 0 && duplicates == 0 && unexpected == 0 && mismatches == 0);
                EnsureStrictIntegrity(integrity);
                return Task.FromResult(new PhaseMeasurement(
                    retention.DroppedSegments + retention.InjectedTombstones,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["retentionDroppedSegments"] = Format(retention.DroppedSegments),
                        ["retentionInjectedTombstones"] = Format(retention.InjectedTombstones),
                        ["droppedMeasurements"] = Format(dropCount),
                        ["remainingMeasurements"] = Format(expectedMeasurements),
                        ["backupDirectory"] = backupRoot,
                    },
                    integrity,
                    [],
                    []));
            }
        }).ConfigureAwait(false));

        return new SoakCycleResult(cycle, startedUtc, DateTimeOffset.UtcNow, phases);
    }

    private static SoakIntegritySummary ValidateHighCardinalitySample(Tsdb db, int seriesCount, int samples)
    {
        long missing = 0;
        long duplicates = 0;
        long mismatches = 0;
        long observed = 0;
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var observedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (int series in EvenlySpacedIndices(seriesCount, samples))
        {
            long timestamp = HighCardinalityTimestampBase + series;
            AppendDigest(expectedHash, series, timestamp, series);
            var entries = db.Catalog.Find(
                "high_cardinality",
                new Dictionary<string, string> { ["series"] = SeriesTag(series) });
            if (entries.Count != 1)
            {
                missing++;
                continue;
            }

            DataPoint[] points = db.Query.Execute(new PointQuery(entries[0].Id, FieldName, new TimeRange(timestamp, timestamp))).ToArray();
            observed += points.Length;
            if (points.Length == 0)
            {
                missing++;
                continue;
            }
            if (points.Length > 1)
                duplicates += points.Length - 1;
            long value = points[0].Value.Type == FieldType.Int64 ? points[0].Value.AsLong() : long.MinValue;
            if (value != series)
                mismatches++;
            AppendDigest(observedHash, series, points[0].Timestamp, value);
        }

        bool digest = expectedHash.GetHashAndReset().AsSpan().SequenceEqual(observedHash.GetHashAndReset());
        return new SoakIntegritySummary(
            "deterministic high-cardinality sample",
            samples,
            observed,
            missing,
            duplicates,
            0,
            mismatches,
            digest && missing == 0 && duplicates == 0 && mismatches == 0);
    }

    private static SoakIntegritySummary ValidateModuloSeries(
        Tsdb db,
        string measurement,
        int seriesCount,
        long expectedPoints,
        long timestampBase,
        string scope)
    {
        if (expectedPoints > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedPoints), "完整性位图仅支持不超过 Int32.MaxValue 个点。");

        var seen = new bool[(int)expectedPoints];
        var observedValues = new long[(int)expectedPoints];
        long observed = 0;
        long duplicates = 0;
        long unexpected = 0;
        long mismatches = 0;

        for (int series = 0; series < seriesCount; series++)
        {
            var entry = FindSeries(db, measurement, series);
            foreach (var point in db.Query.Execute(new PointQuery(
                entry.Id,
                FieldName,
                new TimeRange(timestampBase, timestampBase + expectedPoints - 1))))
            {
                observed++;
                long sequence = point.Timestamp - timestampBase;
                if (sequence < 0 || sequence >= expectedPoints || sequence % seriesCount != series)
                {
                    unexpected++;
                    continue;
                }

                int index = (int)sequence;
                if (seen[index])
                {
                    duplicates++;
                    continue;
                }

                seen[index] = true;
                long value = point.Value.Type == FieldType.Int64 ? point.Value.AsLong() : long.MinValue;
                observedValues[index] = value;
                if (value != sequence)
                    mismatches++;
            }
        }

        long missing = seen.LongCount(static item => !item);
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var observedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int sequence = 0; sequence < seen.Length; sequence++)
        {
            int series = sequence % seriesCount;
            long timestamp = timestampBase + sequence;
            AppendDigest(expectedHash, series, timestamp, sequence);
            if (seen[sequence])
                AppendDigest(observedHash, series, timestamp, observedValues[sequence]);
        }

        bool digest = expectedHash.GetHashAndReset().AsSpan().SequenceEqual(observedHash.GetHashAndReset());
        return new SoakIntegritySummary(
            scope,
            expectedPoints,
            observed,
            missing,
            duplicates,
            unexpected,
            mismatches,
            digest && missing == 0 && duplicates == 0 && unexpected == 0 && mismatches == 0);
    }

    private static SoakIntegritySummary ValidateChaosSeries(
        Tsdb db,
        int seriesCount,
        long acknowledgedPoints,
        out long maximumObservedSequence)
    {
        if (acknowledgedPoints > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(acknowledgedPoints), "maintenance-chaos 位图仅支持不超过 Int32.MaxValue 个已确认点。");

        var seen = new bool[(int)acknowledgedPoints];
        var values = new long[(int)acknowledgedPoints];
        long observed = 0;
        long duplicates = 0;
        long unexpected = 0;
        long mismatches = 0;
        maximumObservedSequence = -1;

        for (int series = 0; series < seriesCount; series++)
        {
            var entry = FindSeries(db, "maintenance_chaos", series);
            foreach (var point in db.Query.Execute(new PointQuery(entry.Id, FieldName, TimeRange.From(ChaosTimestampBase))))
            {
                observed++;
                long sequence = point.Timestamp - ChaosTimestampBase;
                maximumObservedSequence = Math.Max(maximumObservedSequence, sequence);
                long value = point.Value.Type == FieldType.Int64 ? point.Value.AsLong() : long.MinValue;
                if (sequence < 0 || sequence >= acknowledgedPoints || sequence % seriesCount != series)
                {
                    unexpected++;
                    continue;
                }

                int index = (int)sequence;
                if (seen[index])
                {
                    duplicates++;
                    continue;
                }

                seen[index] = true;
                values[index] = value;
                if (value != sequence)
                    mismatches++;
            }
        }

        long missing = seen.LongCount(static item => !item);
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var observedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int sequence = 0; sequence < seen.Length; sequence++)
        {
            int series = sequence % seriesCount;
            long timestamp = ChaosTimestampBase + sequence;
            AppendDigest(expectedHash, series, timestamp, sequence);
            if (seen[sequence])
                AppendDigest(observedHash, series, timestamp, values[sequence]);
        }

        bool digest = expectedHash.GetHashAndReset().AsSpan().SequenceEqual(observedHash.GetHashAndReset());
        return new SoakIntegritySummary(
            "acknowledged maintenance-chaos points",
            acknowledgedPoints,
            observed,
            missing,
            duplicates,
            unexpected,
            mismatches,
            digest && missing == 0 && duplicates == 0 && mismatches == 0);
    }

    private static IReadOnlyList<double> MeasureSeriesQueries(
        Tsdb db,
        string measurement,
        int seriesCount,
        int samples,
        TimeRange range,
        int seed)
    {
        var random = new Random(seed);
        var latency = new List<double>(samples);
        for (int sample = 0; sample < samples; sample++)
        {
            int series = random.Next(seriesCount);
            var entry = FindSeries(db, measurement, series);
            var watch = Stopwatch.StartNew();
            _ = db.Query.Execute(new PointQuery(entry.Id, FieldName, range, Limit: 1)
            {
                Direction = QueryDirection.Descending,
            }).Count();
            watch.Stop();
            latency.Add(watch.Elapsed.TotalMilliseconds);
        }

        return latency;
    }

    private static IReadOnlyList<double> MeasureMeasurementQueries(Tsdb db, SoakOptions options)
    {
        var random = new Random(options.RandomSeed);
        var latency = new List<double>(options.QuerySamples);
        for (int sample = 0; sample < options.QuerySamples; sample++)
        {
            int measurement = random.Next(options.Measurements);
            var entries = db.Catalog.Find(MeasurementName(measurement), null);
            if (entries.Count != 1)
                throw new InvalidDataException("大量 measurement 查询样本未找到唯一 series。");
            var watch = Stopwatch.StartNew();
            _ = db.Query.Execute(new PointQuery(entries[0].Id, FieldName, TimeRange.All)).Count();
            watch.Stop();
            latency.Add(watch.Elapsed.TotalMilliseconds);
        }

        return latency;
    }

    private static Tsdb OpenManual(string root) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        FlushWalToOsOnWrite = false,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    });

    private static Tsdb OpenMaintenanceChaosWorker(string root, int pointsPerBatch) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = true,
        FlushPolicy = new MemTableFlushPolicy
        {
            MaxPoints = pointsPerBatch + 1L,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.FromMilliseconds(20),
        },
        BackgroundFlush = new BackgroundFlushOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(10),
        },
        Compaction = new CompactionPolicy
        {
            Enabled = true,
            MinTierSize = 4,
            PollInterval = TimeSpan.FromMilliseconds(20),
        },
        Retention = new RetentionPolicy
        {
            Enabled = true,
            Ttl = TimeSpan.FromHours(1),
            PollInterval = TimeSpan.FromMilliseconds(20),
            MaxTombstonesPerRound = 4_096,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    });

    private static Tsdb OpenMaintenanceValidation(string root) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Retention = new RetentionPolicy
        {
            Enabled = true,
            Ttl = TimeSpan.FromHours(1),
            PollInterval = TimeSpan.FromDays(1),
            MaxTombstonesPerRound = 4_096,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    });

    private static Tsdb OpenManyMeasurementsMaintenance(string root) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Retention = new RetentionPolicy
        {
            Enabled = true,
            Ttl = TimeSpan.FromMilliseconds(10_000),
            TtlInTimestampUnits = 10_000,
            NowFn = static () => ManyMeasurementsCurrentTimestampBase + 100,
            PollInterval = TimeSpan.FromDays(1),
            MaxTombstonesPerRound = 1_000_000,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    });

    private static Process StartMaintenanceChaosWorker(
        string root,
        string progressPath,
        long startSequence,
        int series,
        int pointsPerBatch,
        int seed)
    {
        string assemblyPath = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--maintenance-chaos-worker");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add(progressPath);
        startInfo.ArgumentList.Add(Format(startSequence));
        startInfo.ArgumentList.Add(Format(series));
        startInfo.ArgumentList.Add(Format(pointsPerBatch));
        startInfo.ArgumentList.Add(Format(seed));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 maintenance-chaos worker。");
    }

    private static void WaitForProgress(Process process, string path, long target, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (File.Exists(path) && ReadProgress(path) >= target)
                return;
            if (process.HasExited)
            {
                string error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException(
                    $"maintenance-chaos worker 提前退出，exitCode={process.ExitCode}：{error}");
            }
            Thread.Sleep(10);
        }

        throw new TimeoutException($"等待 maintenance-chaos worker 写到序列 {target} 超时。");
    }

    private static void WriteProgress(string path, long sequence)
    {
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(Format(sequence));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && attempt < 49)
            {
                // Windows 上并发读句柄或实时扫描器可能短暂阻止原子替换。
                Thread.Sleep(2);
            }
        }
    }

    private static long ReadProgress(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string text = reader.ReadToEnd();
                if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                    return value;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && attempt < 19)
            {
            }

            Thread.Sleep(5);
        }

        throw new InvalidDataException($"无法读取 maintenance-chaos progress：{path}");
    }

    private static Point CreateSeriesPoint(
        string measurement,
        int series,
        long timestamp,
        long value)
        => Point.Create(
            measurement,
            timestamp,
            new Dictionary<string, string> { ["series"] = SeriesTag(series) },
            new Dictionary<string, FieldValue> { [FieldName] = FieldValue.FromLong(value) });

    private static SonnetDB.Catalog.SeriesEntry FindSeries(Tsdb db, string measurement, int series)
    {
        var entries = db.Catalog.Find(
            measurement,
            new Dictionary<string, string> { ["series"] = SeriesTag(series) });
        return entries.Count == 1
            ? entries[0]
            : throw new InvalidDataException($"{measurement} series={series} 未找到唯一目录项，实际 {entries.Count}。");
    }

    private static IEnumerable<int> EvenlySpacedIndices(int count, int samples)
    {
        if (samples == 1)
        {
            yield return 0;
            yield break;
        }

        for (int index = 0; index < samples; index++)
            yield return (int)(((long)index * (count - 1)) / (samples - 1));
    }

    private static void AppendDigest(IncrementalHash hash, long series, long timestamp, long value)
    {
        Span<byte> buffer = stackalloc byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, series);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], timestamp);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[16..], value);
        hash.AppendData(buffer);
    }

    private static void EnsureStrictIntegrity(SoakIntegritySummary integrity)
    {
        if (!integrity.DigestMatches
            || integrity.MissingPoints != 0
            || integrity.DuplicatePoints != 0
            || integrity.UnexpectedPoints != 0
            || integrity.ValueMismatches != 0)
        {
            throw new InvalidDataException(
                $"完整性校验失败：missing={integrity.MissingPoints}, duplicate={integrity.DuplicatePoints}, "
                + $"unexpected={integrity.UnexpectedPoints}, mismatch={integrity.ValueMismatches}, digest={integrity.DigestMatches}。");
        }
    }

    private static string ProfileRoot(SoakOptions options, int cycle)
        => Path.Combine(options.WorkRoot, $"cycle-{cycle:D4}", options.Profile);

    private static string MeasurementName(int measurement) => $"many_m_{measurement:D8}";

    private static string SeriesTag(int series) => $"s-{series:D8}";

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
