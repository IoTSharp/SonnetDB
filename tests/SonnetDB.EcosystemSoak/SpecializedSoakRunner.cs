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

    private readonly record struct BatchSequenceReservation(long StartInclusive, long EndInclusive);

    private readonly record struct IndexedChaosReservation(
        long StartInclusive,
        long EndInclusive,
        long AcknowledgedThroughInclusive,
        int FirstAcknowledgedIndex);

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
        if (args.Length != 8
            || !long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out long startSequence)
            || !int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out int seriesCount)
            || !int.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out int pointsPerBatch)
            || !int.TryParse(args[7], NumberStyles.None, CultureInfo.InvariantCulture, out int seed)
            || startSequence < 0
            || seriesCount <= 0
            || pointsPerBatch <= 0)
        {
            return 2;
        }

        string root = args[1];
        string progressPath = args[2];
        string reservationPath = args[3];
        Directory.CreateDirectory(root);
        var random = new Random(seed);
        using var db = OpenMaintenanceChaosWorker(root, pointsPerBatch);
        long sequence = startSequence;
        int batch = 0;

        while (true)
        {
            long batchStart = sequence;
            long batchEnd = checked(batchStart + pointsPerBatch - 1L);
            // 先把本批可见序列租约原子发布。父进程只能按该末端启动下一 worker，
            // 所以 kill 位于 WriteMany/progress 之间时不会重用可能已经写入 WAL 的点。
            WriteReservation(reservationPath, batchStart, batchEnd);
            var points = new Point[pointsPerBatch + 1];
            for (int index = 0; index < pointsPerBatch; index++)
            {
                long current = checked(batchStart + index);
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
            WriteProgress(progressPath, batchEnd);
            sequence = checked(batchEnd + 1L);
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
        const int ExpectedSegments = 1;

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

            if (db.FlushNow() is null)
                throw new InvalidDataException("高基数写入未生成预期的 segment。");
            if (db.Catalog.Count != options.Series)
                throw new InvalidDataException($"Series 数量不一致：期望 {options.Series}，实际 {db.Catalog.Count}。");
            if (db.Segments.SegmentCount != ExpectedSegments)
            {
                throw new InvalidDataException(
                    $"高基数写入 segment 数量不一致：期望 {ExpectedSegments}，实际 {db.Segments.SegmentCount}。");
            }

            return Task.FromResult(Program.PhaseData(
                options.Series,
                ("series", Format(options.Series)),
                ("measurements", Format(db.Measurements.Count)),
                ("segments", Format(db.Segments.SegmentCount)),
                ("expectedSegments", Format(ExpectedSegments))) with
            {
                EffectiveConfiguration = [CreateManualConfiguration()],
            });
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
                    throw new InvalidDataException($"重开后 Series 数量不一致：期望 {options.Series}，实际 {db.Catalog.Count}。");
                if (db.Segments.SegmentCount != ExpectedSegments)
                {
                    throw new InvalidDataException(
                        $"高基数重开后 segment 数量不一致：期望 {ExpectedSegments}，实际 {db.Segments.SegmentCount}。");
                }

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
                    ["segments"] = Format(ExpectedSegments),
                    ["validatedSeries"] = Format(sampleCount),
                    ["recoverySamples"] = Format(recovery.Count),
                    ["querySamples"] = Format(queryLatency.Count),
                    ["validation"] = "deterministic sampled series/time/value",
                },
                integrity,
                recovery,
                queryLatency)
            {
                EffectiveConfiguration = [CreateManualConfiguration()],
            });
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
                ("segmentBytes", Format(bytes))) with
            {
                EffectiveConfiguration = [CreateManualConfiguration()],
            });
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
                    throw new InvalidDataException("重开后 segment 数量发生变化。");

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
                queryLatency)
            {
                EffectiveConfiguration = [CreateManualConfiguration()],
            });
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
            long acknowledgedPoints = 0;
            int retentionDroppedSegments = 0;
            int retentionInjectedTombstones = 0;
            long retentionElapsedMicros = 0;
            long minimumSegments = long.MaxValue;
            long maximumSegments = 0;
            long acknowledgedWorkerBatches = 0;
            long acknowledgedWorkerWrites = 0;
            var restartReservations = new List<MaintenanceChaosReservationEvidence>(options.RestartCount);
            var processResources = new List<SoakProcessResourceContribution>(options.RestartCount);
            SoakIntegritySummary integrity;

            for (int restart = 0; restart < options.RestartCount; restart++)
            {
                string progressPath = Path.Combine(root, $"progress-{restart:D4}.txt");
                string reservationPath = progressPath + ".reservation";
                using var process = StartMaintenanceChaosWorker(
                    root,
                    progressPath,
                    reservationPath,
                    nextSequence,
                    options.Series,
                    options.PointsPerBatch,
                    options.RandomSeed + restart);
                using var monitor = new ProcessResourceMonitor(process);
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

                    monitor.Stop();
                    processResources.Add(SoakProcessResourceContribution.FromSnapshot(
                        "maintenance-chaos-worker",
                        monitor.Snapshot));
                }

                long workerAcknowledgedSequence = ReadProgress(progressPath);
                BatchSequenceReservation batchReservation = ReadReservation(reservationPath);
                ValidateBatchReservation(
                    batchReservation,
                    nextSequence,
                    options.PointsPerBatch,
                    workerAcknowledgedSequence,
                    restart + 1);
                TryDeleteMaintenanceChaosTemporaryFiles(progressPath, reservationPath);
                if (workerAcknowledgedSequence < target)
                {
                    throw new InvalidDataException(
                        $"maintenance-chaos 第 {restart + 1} 轮 progress 未达到强杀前目标："
                        + $"需要至少 {target}，实际 {workerAcknowledgedSequence}。");
                }

                long workerAcknowledgedCurrentPoints = checked(workerAcknowledgedSequence - nextSequence + 1L);
                if (workerAcknowledgedCurrentPoints < minimumPoints)
                {
                    throw new InvalidDataException(
                        $"maintenance-chaos 第 {restart + 1} 轮已确认点不足："
                        + $"需要至少 {minimumPoints}，实际 {workerAcknowledgedCurrentPoints}。");
                }
                if (workerAcknowledgedCurrentPoints % options.PointsPerBatch != 0)
                {
                    throw new InvalidDataException(
                        $"maintenance-chaos 第 {restart + 1} 轮 progress 未对齐完整批次：{workerAcknowledgedCurrentPoints} / {options.PointsPerBatch}。");
                }

                long workerBatches = workerAcknowledgedCurrentPoints / options.PointsPerBatch;
                acknowledgedWorkerBatches = checked(acknowledgedWorkerBatches + workerBatches);
                acknowledgedWorkerWrites = checked(
                    acknowledgedWorkerWrites + workerAcknowledgedCurrentPoints + workerBatches);
                restartReservations.Add(new MaintenanceChaosReservationEvidence(
                    restart + 1,
                    nextSequence,
                    batchReservation.EndInclusive,
                    workerAcknowledgedSequence));
                acknowledgedPoints = checked(acknowledgedPoints + workerAcknowledgedCurrentPoints);
                var watch = Stopwatch.StartNew();
                using var recovered = OpenManual(root);
                watch.Stop();
                recovery.Add(watch.Elapsed.TotalMilliseconds);
                integrity = ValidateChaosSeries(recovered, options.Series, restartReservations);
                EnsureAcknowledgedChaosIntegrity(integrity, restart + 1);

                // 续写只能依赖 worker 在 WriteMany 前落盘的 reservation，不得根据查询到的
                // 最大序列推断边界；这样范围外数据会在完整性验证中直接失败，而不是被跳过。
                nextSequence = checked(batchReservation.EndInclusive + 1L);
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
                integrity = ValidateChaosSeries(final, options.Series, restartReservations);
                EnsureAcknowledgedChaosIntegrity(integrity, options.RestartCount);
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
            if (integrity.ExpectedPoints != acknowledgedPoints)
            {
                throw new InvalidDataException(
                    $"maintenance-chaos 已确认点统计不一致：reservations={integrity.ExpectedPoints}, "
                    + $"workers={acknowledgedPoints}。");
            }

            // 最终一次全范围验证覆盖所有重开轮次；不要按轮求和，否则同一恢复 tail 会被重复计数。
            long recoveredUnacknowledgedPoints = integrity.UnexpectedPoints;
            long reservedCurrentPoints = 0;
            long reservedButUnacknowledgedPoints = 0;
            foreach (MaintenanceChaosReservationEvidence reservation in restartReservations)
            {
                reservedCurrentPoints = checked(
                    reservedCurrentPoints + reservation.EndInclusive - reservation.StartInclusive + 1L);
                reservedButUnacknowledgedPoints = checked(
                    reservedButUnacknowledgedPoints
                    + reservation.EndInclusive - reservation.AcknowledgedThroughInclusive);
            }

            return Task.FromResult(new PhaseMeasurement(
                acknowledgedPoints,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["randomSeed"] = Format(options.RandomSeed),
                    ["restarts"] = Format(options.RestartCount),
                    ["acknowledgedPoints"] = Format(acknowledgedPoints),
                    ["restartReservations"] = Format(restartReservations.Count),
                    ["acknowledgedWorkerBatches"] = Format(acknowledgedWorkerBatches),
                    ["acknowledgedWorkerWrites"] = Format(acknowledgedWorkerWrites),
                    ["acknowledgedExpiredPoints"] = Format(acknowledgedWorkerBatches),
                    ["reservedCurrentPoints"] = Format(reservedCurrentPoints),
                    ["reservedButUnacknowledgedPoints"] = Format(reservedButUnacknowledgedPoints),
                    ["unacknowledgedButRecovered"] = Format(recoveredUnacknowledgedPoints),
                    ["reservationWritePoint"] = "before-WriteMany",
                    ["reservationPublication"] = "content-flush-then-atomic-file-replace",
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
                queryLatency)
            {
                EffectiveConfiguration =
                [
                    CreateMaintenanceChaosWorkerConfiguration(options.PointsPerBatch),
                    CreateManualConfiguration(),
                    CreateMaintenanceValidationConfiguration(),
                ],
                ProcessResourceContributions = processResources.AsReadOnly(),
                MaintenanceChaosReservations = restartReservations.AsReadOnly(),
            });
        }).ConfigureAwait(false));

        return new SoakCycleResult(cycle, startedUtc, DateTimeOffset.UtcNow, phases);
    }

    private static async Task<SoakCycleResult> RunManyMeasurementsAsync(SoakOptions options, int cycle)
    {
        if (options.Measurements < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "many-measurements profile 至少需要两个 measurement，以同时验证过期和保留语义。");
        }

        string root = ProfileRoot(options, cycle);
        string backupRoot = Path.Combine(Path.GetDirectoryName(root)!, $"backup-{cycle:D4}");
        Directory.CreateDirectory(root);
        var phases = new List<SoakPhaseResult>();
        var startedUtc = DateTimeOffset.UtcNow;
        long totalPoints = checked((long)options.Measurements * options.PointsPerMeasurement);
        int measurementsPerSegment = CeilingDivide(options.Measurements, options.TargetSegments);
        int expectedInitialSegments = CeilingDivide(options.Measurements, measurementsPerSegment);

        phases.Add(await Program.MeasureAsync("many_measurements_write", () =>
        {
            using var db = OpenManual(root);
            long written = 0;
            int flushes = 0;
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
                {
                    if (db.FlushNow() is null)
                    {
                        throw new InvalidDataException(
                            $"第 {flushes + 1} 个 many-measurements flush 未生成 segment。");
                    }

                    flushes++;
                }
            }

            if (db.FlushNow() is not null)
                flushes++;
            if (db.Measurements.Count != options.Measurements)
                throw new InvalidDataException("大量 measurement 写入后的 schema 数量不一致。");
            if (db.Catalog.Count != options.Measurements)
                throw new InvalidDataException("大量 measurement 写入后的 series 数量不一致。");
            if (flushes != expectedInitialSegments || db.Segments.SegmentCount != expectedInitialSegments)
            {
                throw new InvalidDataException(
                    $"大量 measurement 写入 segment 数量不一致：期望 {expectedInitialSegments}，"
                    + $"flush={flushes}，实际 {db.Segments.SegmentCount}。");
            }

            return Task.FromResult(Program.PhaseData(
                written,
                ("measurements", Format(options.Measurements)),
                ("segments", Format(db.Segments.SegmentCount)),
                ("expectedSegments", Format(expectedInitialSegments)),
                ("series", Format(db.Catalog.Count))) with
            {
                EffectiveConfiguration = [CreateManualConfiguration()],
            });
        }).ConfigureAwait(false));

        phases.Add(await Program.MeasureAsync("many_measurements_reopen", () =>
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
                    throw new InvalidDataException("大量 measurement 重开后的 schema 数量不一致。");
                if (db.Catalog.Count != options.Measurements)
                    throw new InvalidDataException("大量 measurement 重开后的 series 数量不一致。");
                if (db.Segments.SegmentCount != expectedInitialSegments)
                {
                    throw new InvalidDataException(
                        $"大量 measurement 重开后 segment 数量不一致：期望 {expectedInitialSegments}，"
                        + $"实际 {db.Segments.SegmentCount}。");
                }
                if (sample == options.RecoverySamples - 1)
                    queryLatency.AddRange(MeasureMeasurementQueries(db, options));
            }

            return Task.FromResult(new PhaseMeasurement(
                options.Measurements,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["measurements"] = Format(options.Measurements),
                    ["segments"] = Format(expectedInitialSegments),
                    ["recoverySamples"] = Format(recovery.Count),
                    ["querySamples"] = Format(queryLatency.Count),
                },
                null,
                recovery,
                queryLatency)
            {
                EffectiveConfiguration = [CreateManualConfiguration()],
            });
        }).ConfigureAwait(false));

        phases.Add(await Program.MeasureAsync("many_measurements_backup_scan", () =>
        {
            using var db = OpenManual(root);
            var backupService = new BackupService();
            var manifest = backupService.Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupRoot,
            });
            var verification = backupService.Verify(backupRoot);
            if (!verification.IsValid)
            {
                throw new InvalidDataException(
                    "大量 measurement 备份校验失败：" + string.Join("; ", verification.Errors));
            }
            if (db.Segments.SegmentCount != expectedInitialSegments
                || manifest.Consistency.SegmentCount != expectedInitialSegments)
            {
                throw new InvalidDataException(
                    $"大量 measurement 备份前 segment 数量不一致：期望 {expectedInitialSegments}，"
                    + $"数据库 {db.Segments.SegmentCount}，manifest {manifest.Consistency.SegmentCount}。");
            }

            long bytes = manifest.Files.Sum(static file => file.SizeBytes);
            return Task.FromResult(Program.PhaseData(
                bytes,
                ("files", Format(manifest.Files.Count)),
                ("bytes", Format(bytes)),
                ("segments", Format(manifest.Consistency.SegmentCount)),
                ("expectedSegments", Format(expectedInitialSegments)),
                ("backupVerified", "true"),
                ("backupCheckedFiles", Format(verification.CheckedFiles))) with
            {
                EffectiveConfiguration = [CreateManualConfiguration()],
            });
        }).ConfigureAwait(false));

        phases.Add(await Program.MeasureAsync("many_measurements_retention_and_drop", () =>
        {
            int dropCount = Math.Min(options.DropMeasurements, options.Measurements / 2);
            IReadOnlyList<int> droppedMeasurements = SelectMeasurementsToDrop(options.Measurements, dropCount);
            var droppedMeasurementSet = droppedMeasurements.ToHashSet();
            IReadOnlyList<int> retentionSamples = SelectRetentionValidationMeasurements(
                options.Measurements,
                options.QuerySamples);
            IReadOnlyList<int> survivingSamples = SelectRetainedMeasurementSamples(
                options.Measurements,
                droppedMeasurementSet,
                options.QuerySamples);
            SoakIntegritySummary retentionIntegrity;
            SoakIntegritySummary postDropIntegrity;
            SoakIntegritySummary reopenIntegrity;
            int segmentsAfterDrop;
            RetentionExecutionStats retention;
            Dictionary<int, ulong> droppedSeriesIds;

            using (var db = OpenManyMeasurementsMaintenance(root))
            {
                retention = db.Retention!.RunOnce();
                retentionIntegrity = ValidateManyMeasurementSamples(
                    db,
                    retentionSamples,
                    options.PointsPerMeasurement,
                    "retention samples: expired even and surviving odd measurements");
                EnsureStrictIntegrity(retentionIntegrity);

                droppedSeriesIds = new Dictionary<int, ulong>(droppedMeasurements.Count);
                foreach (int measurement in droppedMeasurements)
                {
                    string name = MeasurementName(measurement);
                    var entries = db.Catalog.Find(name, null);
                    if (entries.Count != 1)
                        throw new InvalidDataException($"DropMeasurement 前未找到唯一 series：{name}。");

                    droppedSeriesIds.Add(measurement, entries[0].Id);
                    if (!db.DropMeasurement(name))
                        throw new InvalidDataException($"DropMeasurement 未找到 {name}。");
                }

                int expectedMeasurements = options.Measurements - dropCount;
                if (db.Measurements.Count != expectedMeasurements)
                    throw new InvalidDataException("DropMeasurement 后 measurement 数量不一致。");
                if (db.Catalog.Count != expectedMeasurements)
                    throw new InvalidDataException("DropMeasurement 后 series 数量不一致。");

                AssertDroppedMeasurementsAbsent(db, droppedSeriesIds, "drop 后");
                postDropIntegrity = ValidateManyMeasurementSamples(
                    db,
                    survivingSamples,
                    options.PointsPerMeasurement,
                    "surviving measurements after drop");
                EnsureStrictIntegrity(postDropIntegrity);
                segmentsAfterDrop = db.Segments.SegmentCount;
            }

            using (var reopened = OpenManual(root))
            {
                int expectedMeasurements = options.Measurements - dropCount;
                if (reopened.Measurements.Count != expectedMeasurements || reopened.Catalog.Count != expectedMeasurements)
                {
                    throw new InvalidDataException(
                        "DropMeasurement 重开后的 measurement 或 series 数量不一致。");
                }
                if (reopened.Segments.SegmentCount != segmentsAfterDrop)
                {
                    throw new InvalidDataException(
                        $"DropMeasurement 重开后 segment 数量发生变化：期望 {segmentsAfterDrop}，"
                        + $"实际 {reopened.Segments.SegmentCount}。");
                }

                AssertDroppedMeasurementsAbsent(reopened, droppedSeriesIds, "drop 后重开");
                reopenIntegrity = ValidateManyMeasurementSamples(
                    reopened,
                    survivingSamples,
                    options.PointsPerMeasurement,
                    "surviving measurements after drop and reopen");
                EnsureStrictIntegrity(reopenIntegrity);
            }

            SoakIntegritySummary integrity = CombineIntegrity(
                "many-measurements retention, drop, and reopen samples",
                retentionIntegrity,
                postDropIntegrity,
                reopenIntegrity);
            EnsureStrictIntegrity(integrity);
            return Task.FromResult(new PhaseMeasurement(
                checked((long)retention.DroppedSegments + retention.InjectedTombstones + dropCount),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["retentionDroppedSegments"] = Format(retention.DroppedSegments),
                    ["retentionInjectedTombstones"] = Format(retention.InjectedTombstones),
                    ["retentionValidatedMeasurements"] = Format(retentionSamples.Count),
                    ["retentionExpiredMeasurementSamples"] = Format(retentionSamples.Count(static measurement => measurement % 2 == 0)),
                    ["retentionSurvivingOddMeasurementSamples"] = Format(retentionSamples.Count(static measurement => measurement % 2 != 0)),
                    ["droppedMeasurements"] = Format(dropCount),
                    ["remainingMeasurements"] = Format(options.Measurements - dropCount),
                    ["postDropValidatedMeasurements"] = Format(survivingSamples.Count),
                    ["postDropSurvivingOddMeasurementSamples"] = Format(survivingSamples.Count(static measurement => measurement % 2 != 0)),
                    ["postDropSegments"] = Format(segmentsAfterDrop),
                    ["postDropReopen"] = "true",
                    ["backupDirectory"] = backupRoot,
                },
                integrity,
                [],
                [])
            {
                EffectiveConfiguration =
                [
                    CreateManyMeasurementsMaintenanceConfiguration(),
                    CreateManualConfiguration(),
                ],
            });
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
        IReadOnlyList<MaintenanceChaosReservationEvidence> restartReservations)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(restartReservations);
        if (seriesCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(seriesCount));
        if (restartReservations.Count == 0)
            throw new InvalidDataException("maintenance-chaos 未收集到任何重启 reservation。");

        var indexedReservations = new List<IndexedChaosReservation>(restartReservations.Count);
        long acknowledgedPoints = 0;
        long previousEnd = -1;
        for (int index = 0; index < restartReservations.Count; index++)
        {
            MaintenanceChaosReservationEvidence reservation = restartReservations[index];
            if (reservation.Restart != index + 1)
            {
                throw new InvalidDataException(
                    "maintenance-chaos reservation restart 必须从 1 起连续编号。");
            }
            if (reservation.StartInclusive < 0
                || reservation.EndInclusive < reservation.StartInclusive
                || reservation.AcknowledgedThroughInclusive < reservation.StartInclusive
                || reservation.AcknowledgedThroughInclusive > reservation.EndInclusive)
            {
                throw new InvalidDataException("maintenance-chaos reservation 包含无效的闭区间或 progress 边界。");
            }
            if (index == 0)
            {
                if (reservation.StartInclusive != 0)
                    throw new InvalidDataException("maintenance-chaos 首个 reservation 必须从序列 0 开始。");
            }
            else if (previousEnd == long.MaxValue
                || reservation.StartInclusive != checked(previousEnd + 1L))
            {
                throw new InvalidDataException("maintenance-chaos reservation 必须连续且不得重叠。");
            }

            long acknowledgedPointCount = checked(
                reservation.AcknowledgedThroughInclusive - reservation.StartInclusive + 1L);
            if (acknowledgedPoints > int.MaxValue - acknowledgedPointCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(restartReservations),
                    "maintenance-chaos 位图仅支持不超过 Int32.MaxValue 个已确认点。");
            }

            indexedReservations.Add(new IndexedChaosReservation(
                reservation.StartInclusive,
                reservation.EndInclusive,
                reservation.AcknowledgedThroughInclusive,
                (int)acknowledgedPoints));
            acknowledgedPoints = checked(acknowledgedPoints + acknowledgedPointCount);
            previousEnd = reservation.EndInclusive;
        }

        var seen = new bool[(int)acknowledgedPoints];
        var values = new long[(int)acknowledgedPoints];
        var recoveredTail = new HashSet<long>();
        long observed = 0;
        long duplicates = 0;
        long unexpected = 0;
        long mismatches = 0;

        for (int series = 0; series < seriesCount; series++)
        {
            var entry = FindSeries(db, "maintenance_chaos", series);
            foreach (var point in db.Query.Execute(new PointQuery(entry.Id, FieldName, TimeRange.From(ChaosTimestampBase))))
            {
                observed++;
                if (point.Timestamp < ChaosTimestampBase)
                {
                    throw new InvalidDataException(
                        "maintenance-chaos 查询到早于 current 时间基准的非过期点。");
                }

                long sequence = point.Timestamp - ChaosTimestampBase;
                long value = point.Value.Type == FieldType.Int64 ? point.Value.AsLong() : long.MinValue;
                if (sequence % seriesCount != series)
                {
                    throw new InvalidDataException(
                        $"maintenance-chaos 点 {sequence} 落在错误的 series {series}。");
                }

                int reservationIndex = FindChaosReservationIndex(indexedReservations, sequence);
                if (reservationIndex < 0)
                {
                    throw new InvalidDataException(
                        $"maintenance-chaos 查询到 reservation 范围外序列 {sequence}。");
                }

                IndexedChaosReservation reservation = indexedReservations[reservationIndex];
                if (value != sequence)
                    mismatches++;

                if (sequence > reservation.AcknowledgedThroughInclusive)
                {
                    if (!recoveredTail.Add(sequence))
                        duplicates++;
                    unexpected++;
                    continue;
                }

                int acknowledgedIndex = checked(
                    reservation.FirstAcknowledgedIndex
                    + (int)(sequence - reservation.StartInclusive));
                if (seen[acknowledgedIndex])
                {
                    duplicates++;
                    continue;
                }

                seen[acknowledgedIndex] = true;
                values[acknowledgedIndex] = value;
            }
        }

        long missing = seen.LongCount(static item => !item);
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var observedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (IndexedChaosReservation reservation in indexedReservations)
        {
            for (long sequence = reservation.StartInclusive; ; sequence++)
            {
                int index = checked(
                    reservation.FirstAcknowledgedIndex
                    + (int)(sequence - reservation.StartInclusive));
                int series = (int)(sequence % seriesCount);
                long timestamp = checked(ChaosTimestampBase + sequence);
                AppendDigest(expectedHash, series, timestamp, sequence);
                if (seen[index])
                    AppendDigest(observedHash, series, timestamp, values[index]);
                if (sequence == reservation.AcknowledgedThroughInclusive)
                    break;
            }
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

    private static int FindChaosReservationIndex(
        IReadOnlyList<IndexedChaosReservation> reservations,
        long sequence)
    {
        int low = 0;
        int high = reservations.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            IndexedChaosReservation reservation = reservations[middle];
            if (sequence < reservation.StartInclusive)
            {
                high = middle - 1;
                continue;
            }
            if (sequence > reservation.EndInclusive)
            {
                low = middle + 1;
                continue;
            }

            return middle;
        }

        return -1;
    }

    private static IReadOnlyList<int> SelectRetentionValidationMeasurements(
        int measurementCount,
        int querySamples)
    {
        if (measurementCount < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(measurementCount),
                "retention 验证至少需要一个过期偶数 measurement 和一个保留奇数 measurement。");
        }
        if (querySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(querySamples));

        var evenMeasurements = new List<int>((measurementCount + 1) / 2);
        var oddMeasurements = new List<int>(measurementCount / 2);
        for (int measurement = 0; measurement < measurementCount; measurement++)
        {
            if (measurement % 2 == 0)
                evenMeasurements.Add(measurement);
            else
                oddMeasurements.Add(measurement);
        }

        int sampleCount = Math.Min(measurementCount, Math.Max(2, querySamples));
        int evenSampleCount = Math.Min(evenMeasurements.Count, (sampleCount + 1) / 2);
        int oddSampleCount = Math.Min(oddMeasurements.Count, sampleCount - evenSampleCount);
        int remaining = sampleCount - evenSampleCount - oddSampleCount;
        if (remaining > 0)
        {
            int additionalEven = Math.Min(remaining, evenMeasurements.Count - evenSampleCount);
            evenSampleCount += additionalEven;
            remaining -= additionalEven;
            oddSampleCount = checked(oddSampleCount + remaining);
        }

        var result = new List<int>(sampleCount);
        AddEvenlySpacedSamples(result, evenMeasurements, evenSampleCount);
        AddEvenlySpacedSamples(result, oddMeasurements, oddSampleCount);
        result.Sort();
        return result.AsReadOnly();
    }

    private static IReadOnlyList<int> SelectMeasurementsToDrop(int measurementCount, int dropCount)
    {
        if (measurementCount < 2)
            throw new ArgumentOutOfRangeException(nameof(measurementCount));
        if (dropCount < 0 || dropCount >= measurementCount)
            throw new ArgumentOutOfRangeException(nameof(dropCount));

        var oddMeasurements = new List<int>(measurementCount / 2);
        var evenMeasurements = new List<int>((measurementCount + 1) / 2);
        for (int measurement = 0; measurement < measurementCount; measurement++)
        {
            if (measurement % 2 == 0)
                evenMeasurements.Add(measurement);
            else
                oddMeasurements.Add(measurement);
        }

        // 永远保留一个奇数 measurement，以便 drop/reopen 后仍验证非过期数据的值语义。
        int oddDropCount = Math.Min(dropCount, oddMeasurements.Count - 1);
        var result = new List<int>(dropCount);
        result.AddRange(oddMeasurements.Take(oddDropCount));
        int remaining = dropCount - oddDropCount;
        result.AddRange(evenMeasurements.Take(remaining));
        if (result.Count != dropCount)
            throw new InvalidDataException("无法在保留奇数 measurement 的前提下完成 DropMeasurement 样本。");

        result.Sort();
        return result.AsReadOnly();
    }

    private static IReadOnlyList<int> SelectRetainedMeasurementSamples(
        int measurementCount,
        IReadOnlySet<int> droppedMeasurements,
        int querySamples)
    {
        if (measurementCount < 2)
            throw new ArgumentOutOfRangeException(nameof(measurementCount));
        ArgumentNullException.ThrowIfNull(droppedMeasurements);
        if (querySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(querySamples));

        var evenMeasurements = new List<int>((measurementCount + 1) / 2);
        var oddMeasurements = new List<int>(measurementCount / 2);
        for (int measurement = 0; measurement < measurementCount; measurement++)
        {
            if (droppedMeasurements.Contains(measurement))
                continue;
            if (measurement % 2 == 0)
                evenMeasurements.Add(measurement);
            else
                oddMeasurements.Add(measurement);
        }

        if (oddMeasurements.Count == 0)
        {
            throw new InvalidDataException(
                "drop 后没有可用于验证保留数据语义的奇数 measurement。");
        }

        int retainedCount = checked(evenMeasurements.Count + oddMeasurements.Count);
        int minimumSamples = evenMeasurements.Count == 0 ? 1 : 2;
        int sampleCount = Math.Min(retainedCount, Math.Max(minimumSamples, querySamples));
        int oddSampleCount = Math.Min(oddMeasurements.Count, Math.Max(1, (sampleCount + 1) / 2));
        int evenSampleCount = Math.Min(evenMeasurements.Count, sampleCount - oddSampleCount);
        int remaining = sampleCount - oddSampleCount - evenSampleCount;
        if (remaining > 0)
        {
            int additionalOdd = Math.Min(remaining, oddMeasurements.Count - oddSampleCount);
            oddSampleCount += additionalOdd;
            remaining -= additionalOdd;
            evenSampleCount = checked(evenSampleCount + remaining);
        }

        var result = new List<int>(sampleCount);
        AddEvenlySpacedSamples(result, evenMeasurements, evenSampleCount);
        AddEvenlySpacedSamples(result, oddMeasurements, oddSampleCount);
        result.Sort();
        return result.AsReadOnly();
    }

    private static void AddEvenlySpacedSamples(
        ICollection<int> destination,
        IReadOnlyList<int> candidates,
        int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(candidates);
        if (sampleCount < 0 || sampleCount > candidates.Count)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (sampleCount == 0)
            return;

        foreach (int index in EvenlySpacedIndices(candidates.Count, sampleCount))
            destination.Add(candidates[index]);
    }

    private static SoakIntegritySummary ValidateManyMeasurementSamples(
        Tsdb db,
        IReadOnlyList<int> measurements,
        int pointsPerMeasurement,
        string scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (measurements.Count == 0)
            throw new ArgumentException("至少需要一个 measurement 样本。", nameof(measurements));
        if (pointsPerMeasurement <= 0)
            throw new ArgumentOutOfRangeException(nameof(pointsPerMeasurement));

        var uniqueMeasurements = new HashSet<int>();
        long expected = 0;
        long observed = 0;
        long missing = 0;
        long duplicates = 0;
        long unexpected = 0;
        long mismatches = 0;
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var observedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (int measurement in measurements)
        {
            if (measurement < 0 || !uniqueMeasurements.Add(measurement))
                throw new ArgumentException("measurement 样本必须是唯一的非负编号。", nameof(measurements));

            string name = MeasurementName(measurement);
            if (!db.Measurements.Contains(name))
                throw new InvalidDataException($"{scope} 缺少 measurement schema：{name}。");

            IReadOnlyList<SonnetDB.Catalog.SeriesEntry> entries = db.Catalog.Find(name, null);
            if (entries.Count != 1)
                throw new InvalidDataException($"{scope} 未找到唯一目录 series：{name}，实际 {entries.Count}。");

            DataPoint[] points = db.Query.Execute(new PointQuery(entries[0].Id, FieldName, TimeRange.All)).ToArray();
            observed = checked(observed + points.Length);
            if (measurement % 2 == 0)
            {
                // 过期 measurement 的 catalog/schema 应保留，但数据在 retention 后不可见。
                unexpected = checked(unexpected + points.Length);
                continue;
            }

            expected = checked(expected + pointsPerMeasurement);
            var seen = new bool[pointsPerMeasurement];
            var observedValues = new long[pointsPerMeasurement];
            foreach (DataPoint point in points)
            {
                long pointIndex = point.Timestamp - ManyMeasurementsCurrentTimestampBase;
                if (pointIndex < 0 || pointIndex >= pointsPerMeasurement)
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
                long value = point.Value.Type == FieldType.Int64 ? point.Value.AsLong() : long.MinValue;
                observedValues[pointOffset] = value;
                long expectedValue = checked(((long)measurement * pointsPerMeasurement) + pointIndex);
                if (point.Value.Type != FieldType.Int64 || value != expectedValue)
                    mismatches++;
            }

            for (int point = 0; point < pointsPerMeasurement; point++)
            {
                long timestamp = checked(ManyMeasurementsCurrentTimestampBase + point);
                long expectedValue = checked(((long)measurement * pointsPerMeasurement) + point);
                AppendDigest(expectedHash, measurement, timestamp, expectedValue);
                if (!seen[point])
                {
                    missing++;
                    continue;
                }

                AppendDigest(observedHash, measurement, timestamp, observedValues[point]);
            }
        }

        if (expected == 0)
        {
            throw new InvalidDataException(
                $"{scope} 未包含任何应保留的奇数 measurement 样本。");
        }

        bool digest = expectedHash.GetHashAndReset().AsSpan().SequenceEqual(observedHash.GetHashAndReset());
        return new SoakIntegritySummary(
            scope,
            expected,
            observed,
            missing,
            duplicates,
            unexpected,
            mismatches,
            digest && missing == 0 && duplicates == 0 && unexpected == 0 && mismatches == 0);
    }

    private static void AssertDroppedMeasurementsAbsent(
        Tsdb db,
        IReadOnlyDictionary<int, ulong> droppedSeriesIds,
        string scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(droppedSeriesIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        foreach ((int measurement, ulong seriesId) in droppedSeriesIds.OrderBy(static pair => pair.Key))
        {
            string name = MeasurementName(measurement);
            if (db.Measurements.Contains(name))
                throw new InvalidDataException($"{scope} 仍保留 measurement schema：{name}。");
            if (db.Catalog.Find(name, null).Count != 0)
                throw new InvalidDataException($"{scope} 仍保留 catalog series：{name}。");
            if (db.Query.Execute(new PointQuery(seriesId, FieldName, TimeRange.All)).Any())
                throw new InvalidDataException($"{scope} 仍可查询到已 drop measurement 的数据：{name}。");
        }
    }

    private static SoakIntegritySummary CombineIntegrity(
        string scope,
        params SoakIntegritySummary[] summaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(summaries);
        if (summaries.Length == 0)
            throw new ArgumentException("至少需要一个完整性摘要。", nameof(summaries));

        long expected = 0;
        long observed = 0;
        long missing = 0;
        long duplicates = 0;
        long unexpected = 0;
        long mismatches = 0;
        bool digestMatches = true;
        foreach (SoakIntegritySummary summary in summaries)
        {
            ArgumentNullException.ThrowIfNull(summary);
            expected = checked(expected + summary.ExpectedPoints);
            observed = checked(observed + summary.ObservedPoints);
            missing = checked(missing + summary.MissingPoints);
            duplicates = checked(duplicates + summary.DuplicatePoints);
            unexpected = checked(unexpected + summary.UnexpectedPoints);
            mismatches = checked(mismatches + summary.ValueMismatches);
            digestMatches &= summary.DigestMatches;
        }

        return new SoakIntegritySummary(
            scope,
            expected,
            observed,
            missing,
            duplicates,
            unexpected,
            mismatches,
            digestMatches);
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

    private static Tsdb OpenManual(string root) => Tsdb.Open(CreateManualOptions(root));

    private static TsdbOptions CreateManualOptions(string root) => new()
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = false,
        FlushWalToOsOnWrite = false,
        FlushPolicy = new MemTableFlushPolicy
        {
            // 专项 runner 只允许显式 FlushNow 决定段边界，不能让默认阈值悄然改变容量形状。
            MaxPoints = long.MaxValue,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.MaxValue,
        },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    };

    private static SoakConfigurationEvidence CreateManualConfiguration()
        => SoakConfigurationEvidence.FromTsdbOptions(
            "manual-embedded",
            "SpecializedSoakRunner.OpenManual",
            CreateManualOptions("<report-root>"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["segmentBoundaryMode"] = "explicit-FlushNow-only",
                ["manualFlushIsolation"] = "true",
            });

    private static Tsdb OpenMaintenanceChaosWorker(string root, int pointsPerBatch)
        => Tsdb.Open(CreateMaintenanceChaosWorkerOptions(root, pointsPerBatch));

    private static TsdbOptions CreateMaintenanceChaosWorkerOptions(string root, int pointsPerBatch)
    {
        if (pointsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(pointsPerBatch));

        return new TsdbOptions
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
        };
    }

    private static SoakConfigurationEvidence CreateMaintenanceChaosWorkerConfiguration(int pointsPerBatch)
        => SoakConfigurationEvidence.FromTsdbOptions(
            "maintenance-chaos-worker",
            "SpecializedSoakRunner.OpenMaintenanceChaosWorker",
            CreateMaintenanceChaosWorkerOptions("<report-root>", pointsPerBatch),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["progressWritePoint"] = "after-WriteMany-returns",
                ["reservationWritePoint"] = "before-WriteMany",
                ["reservationRecord"] = "batch-start-and-end-inclusive",
                ["reservationContentFlush"] = "FileStream.Flush(true)",
                ["reservationReplace"] = "File.Move-overwrite-same-directory",
                ["restartReservationEvidence"] = "worker-start-through-last-reserved-end",
                ["expiredPointsPerBatch"] = "1",
                ["currentPointsPerBatch"] = Format(pointsPerBatch),
            });

    private static Tsdb OpenMaintenanceValidation(string root)
        => Tsdb.Open(CreateMaintenanceValidationOptions(root));

    private static TsdbOptions CreateMaintenanceValidationOptions(string root) => new()
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
    };

    private static SoakConfigurationEvidence CreateMaintenanceValidationConfiguration()
        => SoakConfigurationEvidence.FromTsdbOptions(
            "maintenance-validation",
            "SpecializedSoakRunner.OpenMaintenanceValidation",
            CreateMaintenanceValidationOptions("<report-root>"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["retentionExecution"] = "synchronous-RunOnce",
            });

    private static Tsdb OpenManyMeasurementsMaintenance(string root)
        => Tsdb.Open(CreateManyMeasurementsMaintenanceOptions(root));

    private static TsdbOptions CreateManyMeasurementsMaintenanceOptions(string root) => new()
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
    };

    private static SoakConfigurationEvidence CreateManyMeasurementsMaintenanceConfiguration()
        => SoakConfigurationEvidence.FromTsdbOptions(
            "many-measurements-maintenance",
            "SpecializedSoakRunner.OpenManyMeasurementsMaintenance",
            CreateManyMeasurementsMaintenanceOptions("<report-root>"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["retentionExecution"] = "synchronous-RunOnce",
                ["retentionVirtualNowTimestamp"] = Format(ManyMeasurementsCurrentTimestampBase + 100),
            });

    private static Process StartMaintenanceChaosWorker(
        string root,
        string progressPath,
        string reservationPath,
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
        startInfo.ArgumentList.Add(reservationPath);
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
        => WriteAtomicText(path, Format(sequence));

    private static void WriteReservation(
        string path,
        long startInclusive,
        long endInclusive)
    {
        if (startInclusive < 0 || endInclusive < startInclusive)
            throw new ArgumentOutOfRangeException(nameof(startInclusive));

        WriteAtomicText(path, string.Concat(
            Format(startInclusive),
            "\n",
            Format(endInclusive)));
    }

    private static void WriteAtomicText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
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

    private static void TryDeleteMaintenanceChaosTemporaryFiles(string progressPath, string reservationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progressPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationPath);

        TryDeleteMaintenanceChaosTemporaryFile(progressPath + ".tmp");
        TryDeleteMaintenanceChaosTemporaryFile(reservationPath + ".tmp");
    }

    private static void TryDeleteMaintenanceChaosTemporaryFile(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Delete(path);
                if (!File.Exists(path))
                    return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 已确认 worker 退出后再清理；短暂文件锁留给有限重试，不影响已验证的结果。
            }

            Thread.Sleep(5);
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

    private static BatchSequenceReservation ReadReservation(string path)
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
                string[] fields = reader.ReadToEnd().Split(
                    '\n',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2
                    && long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long startInclusive)
                    && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long endInclusive)
                    && startInclusive >= 0
                    && endInclusive >= startInclusive)
                {
                    return new BatchSequenceReservation(startInclusive, endInclusive);
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && attempt < 19)
            {
            }

            Thread.Sleep(5);
        }

        throw new InvalidDataException($"无法读取 maintenance-chaos reservation：{path}");
    }

    private static void ValidateBatchReservation(
        BatchSequenceReservation reservation,
        long workerStartSequence,
        int pointsPerBatch,
        long acknowledgedThroughInclusive,
        int restart)
    {
        if (workerStartSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(workerStartSequence));
        if (pointsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(pointsPerBatch));
        if (restart <= 0)
            throw new ArgumentOutOfRangeException(nameof(restart));
        if (reservation.StartInclusive < workerStartSequence)
        {
            throw new InvalidDataException(
                $"maintenance-chaos 第 {restart} 轮 reservation 起点 {reservation.StartInclusive} "
                + $"早于 worker 起点 {workerStartSequence}。");
        }

        long offset = checked(reservation.StartInclusive - workerStartSequence);
        if (offset % pointsPerBatch != 0)
        {
            throw new InvalidDataException(
                $"maintenance-chaos 第 {restart} 轮 reservation 起点未按 batch 对齐：{reservation.StartInclusive}。");
        }

        long expectedEnd = checked(reservation.StartInclusive + pointsPerBatch - 1L);
        if (reservation.EndInclusive != expectedEnd)
        {
            throw new InvalidDataException(
                $"maintenance-chaos 第 {restart} 轮 reservation 不是单一完整 batch："
                + $"{reservation.StartInclusive}..{reservation.EndInclusive}。");
        }

        if (acknowledgedThroughInclusive < workerStartSequence
            || acknowledgedThroughInclusive > reservation.EndInclusive)
        {
            throw new InvalidDataException(
                $"maintenance-chaos 第 {restart} 轮 progress {acknowledgedThroughInclusive} "
                + $"不在 worker/reservation 边界内。");
        }
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
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (samples <= 0 || samples > count)
            throw new ArgumentOutOfRangeException(nameof(samples));

        if (samples == 1)
        {
            yield return 0;
            yield break;
        }

        for (int index = 0; index < samples; index++)
            yield return (int)(((long)index * (count - 1)) / (samples - 1));
    }

    private static int CeilingDivide(int dividend, int divisor)
    {
        if (dividend <= 0)
            throw new ArgumentOutOfRangeException(nameof(dividend));
        if (divisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(divisor));

        long quotient = ((long)dividend + divisor - 1L) / divisor;
        return checked((int)quotient);
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

    private static void EnsureAcknowledgedChaosIntegrity(SoakIntegritySummary integrity, int restart)
    {
        if (!integrity.DigestMatches
            || integrity.MissingPoints != 0
            || integrity.DuplicatePoints != 0
            || integrity.ValueMismatches != 0)
        {
            throw new InvalidDataException(
                $"maintenance-chaos 第 {restart} 轮已确认范围检测失败：expected={integrity.ExpectedPoints}, "
                + $"observed={integrity.ObservedPoints}, missing={integrity.MissingPoints}, "
                + $"duplicate={integrity.DuplicatePoints}, unexpected={integrity.UnexpectedPoints}, "
                + $"valueMismatch={integrity.ValueMismatches}, digest={integrity.DigestMatches}。");
        }
    }

    private static string ProfileRoot(SoakOptions options, int cycle)
        => Path.Combine(options.WorkRoot, $"cycle-{cycle:D4}", options.Profile);

    private static string MeasurementName(int measurement) => $"many_m_{measurement:D8}";

    private static string SeriesTag(int series) => $"s-{series:D8}";

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
