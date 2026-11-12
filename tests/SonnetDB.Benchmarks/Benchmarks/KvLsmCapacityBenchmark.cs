using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>使用显式规模与墙钟预算测量 KV 双引擎、共享表和打开时迁移，输出可核验的本机基线。</summary>
public static class KvLsmCapacityBenchmark
{
    internal static KvLsmCapacityReport Run(KvLsmCapacityOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        string output = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var reportFile = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var report = new KvLsmCapacityReport { Options = options };
        string root = Path.Combine(Path.GetTempPath(), "SonnetDB.KvLsmCapacity", report.RunId);
        report.DataDirectory = root;
        Directory.CreateDirectory(root);
        string owner = Path.Combine(root, ".benchmark-owner");
        File.WriteAllText(owner, report.RunId);
        using var process = Process.GetCurrentProcess();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var context = new RunContext(options, report, process, deadline.Token);
        int[] gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            report.Drive = $"{drive.Name} {drive.DriveFormat} totalBytes={drive.TotalSize}";
            Console.WriteLine($"run-id={report.RunId} data-directory={root}");
            // 即使外层强杀，已落盘的 UNKNOWN 记录也能定位本次独占目录。
            JsonSerializer.Serialize(reportFile, report, KvLsmCapacityJsonContext.Default.KvLsmCapacityReport);
            reportFile.Flush(flushToDisk: true);
            context.Check();
            switch (options.Scenario)
            {
                case "kv": RunKeyspace(root, context); break;
                case "tables": RunTables(root, context); break;
                case "migration": RunMigration(root, context); break;
                case "memory": RunMemory(root, context); break;
            }
            context.Check();
            context.SampleStorage(root);
            report.Status = "PASS";
        }
        catch (OperationCanceledException exception)
        {
            report.Status = "UNKNOWN";
            report.Error = $"Cancelled or wall-clock budget exhausted: {exception.Message}";
        }
        catch (Exception exception)
        {
            report.Status = "FAIL";
            report.Error = exception.ToString();
        }
        finally
        {
            process.Refresh();
            report.PeakWorkingSetBytes = process.PeakWorkingSet64;
            report.GcCollections = [GC.CollectionCount(0) - gcBefore[0], GC.CollectionCount(1) - gcBefore[1], GC.CollectionCount(2) - gcBefore[2]];
            report.GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
            try
            {
                if (File.ReadAllText(owner) != report.RunId)
                    throw new InvalidOperationException("Benchmark directory ownership changed.");
                Directory.Delete(root, recursive: true);
                report.CleanupComplete = true;
            }
            catch (Exception exception)
            {
                report.Status = "FAIL";
                report.Error += "\nCleanup: " + exception.Message;
            }
            report.FinishedUtc = DateTimeOffset.UtcNow;
            reportFile.Position = 0;
            reportFile.SetLength(0);
            JsonSerializer.Serialize(reportFile, report, KvLsmCapacityJsonContext.Default.KvLsmCapacityReport);
            reportFile.Flush(flushToDisk: true);
        }
        return report;
    }

    private static void RunMemory(string root, RunContext context)
    {
        using KvKeyspace store = KvKeyspace.Open("capacity", Path.Combine(root, "memory"), StoreOptions(context.Options));
        Fill(store, context);
        context.Time("checkpoint", 1, store.CreateSnapshot);
        VerifyAll(store, context);
        context.SampleMemory(seconds: 10);
        context.SampleLsmIo(store);
    }

    private static void RunKeyspace(string root, RunContext context)
    {
        KvLsmCapacityOptions options = context.Options;
        using KvKeyspace store = KvKeyspace.Open("capacity", Path.Combine(root, "kv"), StoreOptions(options));
        Fill(store, context);
        context.Time("checkpoint", 1, store.CreateSnapshot);
        VerifyAll(store, context);
        int[] queries = QueryOrdinals(options);
        byte[][] keys = queries.Select(Key).ToArray();
        byte[]? result = null;
        context.Sample("point-read-first-pass", options.Samples,
            index => result = store.Get(keys[index]), index => VerifyValue(result, queries[index], options));
        context.Sample("point-read-warm", options.Samples,
            index => result = store.Get(keys[index]), index => VerifyValue(result, queries[index], options));

        foreach (bool descending in new[] { false, true })
        {
            IReadOnlyList<KvEntry>? page = null;
            context.Sample(descending ? "seek-scan100-reverse" : "seek-scan100-forward", options.Samples,
                index =>
                {
                    using KvReadSnapshot snapshot = store.AcquireReadSnapshot();
                    using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
                    {
                        StartInclusive = descending ? default : keys[index],
                        EndExclusive = descending ? Key(queries[index] + 1) : default,
                        Descending = descending,
                        PageSize = 100,
                        MaxPageBytes = Math.Max(KvRangeScanOptions.DefaultMaxPageBytes, options.ValueBytes * 100 + 1600),
                    });
                    page = cursor.ReadNextPage();
                },
                index =>
                {
                    int expected = Math.Min(100, descending ? queries[index] + 1 : options.Entries - queries[index]);
                    if (page!.Count != expected) throw new InvalidDataException("Range scan cardinality mismatch.");
                    for (int row = 0; row < expected; row++)
                    {
                        int ordinal = queries[index] + (descending ? -row : row);
                        if (!page[row].Key.Span.SequenceEqual(Key(ordinal))) throw new InvalidDataException("Range scan key order mismatch.");
                        VerifyValue(page[row].Value.Span, ordinal, options);
                    }
                });
        }

        int overlay = Math.Min(options.Entries, 10_000);
        byte[] value = Value(0, options);
        for (int index = 0; index < overlay; index++)
        {
            context.Check();
            SetOrdinal(value, index);
            store.Put(Key(index), value);
        }
        context.Report.SnapshotPrefillWrites = overlay;
        KvReadSnapshot? held = null;
        try
        {
            context.Sample("snapshot-after-write", options.Samples,
                _ => held = store.AcquireReadSnapshot(),
                index =>
                {
                    VerifyValue(held!.GetEntry(keys[index])!.Value.Span, queries[index], options);
                    held.Dispose();
                    held = null;
                },
                index =>
                {
                    SetOrdinal(value, queries[index]);
                    store.Put(keys[index], value);
                });
        }
        finally { held?.Dispose(); }
        context.Time("final-checkpoint", 1, store.CreateSnapshot);
        context.SampleMemory();
        context.SampleLsmIo(store);
    }

    private static void RunMigration(string root, RunContext context)
    {
        string path = Path.Combine(root, "migration");
        long sequence;
        long generation;
        using (KvKeyspace legacy = KvKeyspace.Open("capacity", path, StoreOptions(context.Options) with { StorageEngine = KvStorageEngine.Legacy }))
        {
            Fill(legacy, context);
            legacy.CreateSnapshot();
            sequence = legacy.LastSequence;
            generation = legacy.Generation;
        }
        KvKeyspace? migrated = null;
        context.SampleStorage(root);
        context.Report.MigrationSourceDiskBytes = context.Report.LastObservedDiskBytes;
        KvLegacyMigration.PhaseTestHook = (_, directory) =>
        {
            if (string.Equals(Path.GetFullPath(directory), path, StringComparison.OrdinalIgnoreCase))
                context.SampleStorage(root);
        };
        try
        {
            context.Time("migrate-open", 1,
                () => migrated = KvKeyspace.Open("capacity", path, StoreOptions(context.Options) with { StorageEngine = KvStorageEngine.Lsm }));
            if (migrated!.StorageEngine != KvStorageEngine.Lsm || migrated.LastSequence != sequence || migrated.Generation != generation)
                throw new InvalidDataException("Migration engine/sequence/generation mismatch (including skipped migration).");
            VerifyAll(migrated, context);
            context.Report.MigrationLogicalGiB = (double)context.Options.Entries * (context.Options.ValueBytes + 16) / (1024L * 1024 * 1024);
            context.Report.MigrationSecondsPerLogicalGiB = context.Report.Measurements.Single(item => item.Name == "migrate-open").ElapsedMilliseconds
                / 1000 / context.Report.MigrationLogicalGiB;
            context.SampleMemory();
        }
        finally
        {
            KvLegacyMigration.PhaseTestHook = null;
            migrated?.Dispose();
        }
        using KvKeyspace reopened = KvKeyspace.Open("capacity", path, StoreOptions(context.Options));
        VerifyAll(reopened, context);
    }

    private static void RunTables(string root, RunContext context)
    {
        KvLsmCapacityOptions options = context.Options;
        using Tsdb database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = Path.Combine(root, "tables"),
            Kv = StoreOptions(options),
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        var stores = new TableStore[options.ColdTables + 1];
        byte[] payload = Value(0, options);
        context.Time("create-and-fill-tables", (long)stores.Length * options.RowsPerTable, () =>
        {
            for (int table = 0; table < stores.Length; table++)
            {
                context.Check();
                string name = $"t{table:D4}";
                SqlExecutor.Execute(database, $"CREATE TABLE {name}(id INT, payload BLOB, PRIMARY KEY(id))");
                TableStore store = stores[table] = database.Tables.Open(name);
                for (int start = 0; start < options.RowsPerTable; start += 256)
                {
                    context.Check();
                    int count = Math.Min(256, options.RowsPerTable - start);
                    var rows = new IReadOnlyList<object?>[count];
                    for (int offset = 0; offset < count; offset++) rows[offset] = new object?[] { (long)(start + offset), payload };
                    if (store.InsertMany(rows) != count) throw new InvalidDataException("Table fixture batch was incomplete.");
                }
            }
        });
        database.Tables.CheckpointAll();
        var random = new Random(options.Seed);
        var readTables = new int[options.Samples];
        var readRows = new long[options.Samples];
        TableRow? found = null;
        for (int index = 0; index < options.Samples; index++)
        {
            context.Check();
            readTables[index] = random.Next(1, stores.Length);
            readRows[index] = random.Next(options.RowsPerTable);
            ReadCold(index);
            VerifyCold(index);
        }
        context.Report.ColdTableWarmupReads = options.Samples;
        context.Sample("cold-tables-idle-writer", options.Samples,
            ReadCold, VerifyCold);

        using var stopWriter = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        using var started = new ManualResetEventSlim();
        long writes = 0;
        long writeStarted = Stopwatch.GetTimestamp();
        Task writer = Task.Run(() =>
        {
            for (int index = 0; index < options.Entries && !stopWriter.IsCancellationRequested; index++)
            {
                context.Check();
                stores[0].Insert(new object?[] { (long)options.RowsPerTable + index, payload });
                Interlocked.Increment(ref writes);
                started.Set();
            }
        }, CancellationToken.None);
        try
        {
            if (!started.Wait(TimeSpan.FromSeconds(10), context.Token)) throw new TimeoutException("Hot table writer did not start.");
            context.Sample("cold-tables-with-hot-writer", options.Samples,
                ReadCold, VerifyCold);
        }
        finally
        {
            stopWriter.Cancel();
            if (!writer.Wait(TimeSpan.FromSeconds(35))) throw new TimeoutException("Hot table writer did not stop.");
        }
        context.Report.HotTableWrites = writes;
        context.Report.HotTableWritesPerSecond = writes / Stopwatch.GetElapsedTime(writeStarted).TotalSeconds;
        context.Report.HotWriterReachedIterationLimit = writes == options.Entries;
        if (stores[0].RowCount != options.RowsPerTable + writes) throw new InvalidDataException("Hot table row count mismatch.");
        for (int index = 1; index < stores.Length; index++)
        {
            context.Check();
            if (stores[index].RowCount != options.RowsPerTable) throw new InvalidDataException("Cold table row count changed.");
        }
        database.Tables.CheckpointAll();
        context.SampleMemory();

        void ReadCold(int index) => found = stores[readTables[index]].GetByPrimaryKey(new object?[] { readRows[index] });
        void VerifyCold(int index)
        {
            if (found is null || found.Values[0] is not long id || id != readRows[index]
                || found.Values[1] is not byte[] blob || !blob.AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("Cold table row or payload mismatch.");
        }
    }

    private static void Fill(KvKeyspace store, RunContext context)
    {
        KvLsmCapacityOptions options = context.Options;
        int[] order = Enumerable.Range(0, options.Entries).ToArray();
        if (options.RandomOrder) new Random(options.Seed).Shuffle(order);
        byte[] value = Value(0, options);
        byte[] key = new byte[16];
        context.Time(options.RandomOrder ? "fillrandom" : "fillseq", options.Entries, () =>
        {
            for (int index = 0; index < order.Length; index++)
            {
                context.Check();
                SetKey(key, order[index]);
                SetOrdinal(value, order[index]);
                store.Put(key, value);
                if ((index + 1) % 100_000 == 0) Console.WriteLine($"fill={index + 1}/{options.Entries}");
            }
        });
    }

    private static void VerifyAll(KvKeyspace store, RunContext context)
    {
        using KvReadSnapshot snapshot = store.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions { PageSize = 256 });
        int seen = 0;
        for (int pageIndex = 0; pageIndex <= context.Options.Entries / 256 + 1; pageIndex++)
        {
            context.Check();
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
            foreach (KvEntry entry in page)
            {
                if (seen >= context.Options.Entries || !entry.Key.Span.SequenceEqual(Key(seen))) throw new InvalidDataException("Fixture key count/order mismatch.");
                VerifyValue(entry.Value.Span, seen++, context.Options);
            }
            if (cursor.IsExhausted || page.Count == 0) break;
        }
        if (seen != context.Options.Entries) throw new InvalidDataException("Fixture full readback count mismatch.");
        context.Report.FullReadbacks++;
    }

    private static KvOptions StoreOptions(KvLsmCapacityOptions options) => KvOptions.Default with
    {
        StorageEngine = options.Engine,
        SyncWalOnEveryWrite = options.SyncWal,
        MaxSnapshotOverlayEntries = 200_000,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };

    private static int[] QueryOrdinals(KvLsmCapacityOptions options)
    {
        var random = new Random(options.Seed);
        var values = new int[options.Samples];
        for (int index = 0; index < values.Length; index++) values[index] = random.Next(options.Entries);
        return values;
    }

    private static byte[] Key(int ordinal) { var key = new byte[16]; SetKey(key, ordinal); return key; }
    private static void SetKey(Span<byte> key, int ordinal)
    {
        BinaryPrimitives.WriteInt64BigEndian(key, ordinal);
        BinaryPrimitives.WriteInt64BigEndian(key[8..], ~ordinal);
    }
    private static byte[] Value(int ordinal, KvLsmCapacityOptions options)
    {
        byte[] value = new byte[options.ValueBytes];
        value.AsSpan().Fill((byte)options.Seed);
        SetOrdinal(value, ordinal);
        return value;
    }
    private static void SetOrdinal(Span<byte> value, int ordinal) => BinaryPrimitives.WriteInt64LittleEndian(value, ordinal);
    private static void VerifyValue(ReadOnlySpan<byte> value, int ordinal, KvLsmCapacityOptions options)
    {
        if (value.Length != options.ValueBytes || BinaryPrimitives.ReadInt64LittleEndian(value) != ordinal
            || value[8..].ContainsAnyExcept((byte)options.Seed))
            throw new InvalidDataException("Fixture value mismatch.");
    }

    private sealed class RunContext(KvLsmCapacityOptions options, KvLsmCapacityReport report, Process process, CancellationToken token)
    {
        internal KvLsmCapacityOptions Options => options;
        internal KvLsmCapacityReport Report => report;
        internal CancellationToken Token => token;
        private int _checks;
        internal void Check()
        {
            token.ThrowIfCancellationRequested();
            if ((Interlocked.Increment(ref _checks) & 1023) != 0) return;
            using Process current = Process.GetCurrentProcess();
            if (current.WorkingSet64 > options.MaxRssMiB * 1024L * 1024)
                throw new OperationCanceledException("RSS budget exceeded.", token);
        }
        internal void Time(string name, long operations, Action action)
        {
            Check();
            Console.WriteLine("phase=" + name);
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            action();
            double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            report.Measurements.Add(new KvLsmCapacityMeasurement(name, operations, milliseconds, operations / (milliseconds / 1000),
                null, null, GC.GetTotalAllocatedBytes(precise: true) - allocated));
            Check();
        }
        internal void Time(string name, long operations, Func<long> action) => Time(name, operations, () => { _ = action(); });
        internal void Sample(string name, int count, Action<int> action, Action<int> validate, Action<int>? before = null)
        {
            Console.WriteLine("phase=" + name);
            var samples = new double[count];
            long allocated = 0;
            for (int index = 0; index < count; index++)
            {
                Check();
                before?.Invoke(index);
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                action(index);
                samples[index] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                allocated += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                validate(index);
            }
            Array.Sort(samples);
            double milliseconds = samples.Sum() / 1000;
            report.Measurements.Add(new KvLsmCapacityMeasurement(name, count, milliseconds, count / (milliseconds / 1000),
                samples[(int)Math.Ceiling(count * .50) - 1], samples[(int)Math.Ceiling(count * .99) - 1], allocated));
        }
        internal void SampleMemory(int seconds = 1)
        {
            var observation = Stopwatch.StartNew();
            for (int sample = 0; sample < seconds * 10; sample++)
            {
                Check();
                process.Refresh();
                report.PostCheckpointRssBytes.Add(process.WorkingSet64);
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
            report.RssObservationSeconds = observation.Elapsed.TotalSeconds;
            if (seconds >= 10)
            {
                long[] tail = report.PostCheckpointRssBytes.TakeLast(50).ToArray();
                report.RssStable = tail.Max() - tail.Min() <= Math.Max(4 * 1024 * 1024, tail.Average() * .05);
                if (report.RssStable.Value) report.ObservedStableRssBytes = (long)tail.Average();
            }
        }
        internal void SampleLsmIo(KvKeyspace store)
        {
            if (store.LsmBackend is not { } backend) return;
            var stats = backend.Engine.GetStats();
            report.CompletedLsmIo = new KvLsmCompletedIoMeasurement(
                stats.CompletedFlushOutputBytes, stats.CompletedCompactionInputBytes, stats.CompletedCompactionOutputBytes);
        }

        internal void SampleStorage(string root)
        {
            int count = 0;
            long bytes = 0;
            foreach (string path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
            }))
            {
                Check();
                if (++count > 100_000) throw new InvalidOperationException("Task data inventory exceeded 100000 files.");
                try { bytes += new FileInfo(path).Length; }
                catch (FileNotFoundException) { report.InventoryFilesDisappeared++; }
            }
            report.LastObservedFileCount = count;
            report.LastObservedDiskBytes = bytes;
            report.MaxObservedDiskBytes = Math.Max(report.MaxObservedDiskBytes, bytes);
        }
    }
}

internal sealed record KvLsmCapacityOptions
{
    public string Scenario { get; init; } = "kv";
    public KvStorageEngine Engine { get; init; } = KvStorageEngine.Lsm;
    public int Entries { get; init; } = 100_000;
    public int ValueBytes { get; init; } = 100;
    public int Samples { get; init; } = 200;
    public int ColdTables { get; init; } = 1000;
    public int RowsPerTable { get; init; } = 10;
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxRssMiB { get; init; } = 2048;
    public int Seed { get; init; } = 42;
    public bool SyncWal { get; init; }
    public bool RandomOrder { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;

    internal void Validate()
    {
        if (Scenario is not ("kv" or "tables" or "migration" or "memory")) throw new ArgumentException("scenario must be kv, tables, migration or memory.");
        if (!Enum.IsDefined(Engine)) throw new ArgumentOutOfRangeException(nameof(Engine));
        if (Scenario == "migration" && Engine != KvStorageEngine.Lsm) throw new ArgumentException("migration requires engine Lsm.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Entries);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Entries, 100_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(ValueBytes, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ValueBytes, 65_536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Samples);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Samples, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ColdTables);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ColdTables, 1000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RowsPerTable);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RowsPerTable, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TimeoutSeconds, 3600);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRssMiB, 64);
        ArgumentException.ThrowIfNullOrWhiteSpace(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(Output);
    }
}

internal sealed class KvLsmCapacityReport
{
    public string SchemaVersion { get; init; } = "1.2";
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string Status { get; set; } = "UNKNOWN";
    public string EvidenceScope { get; init; } = "Bounded local measurement; PASS means fixture and selected run completed, not G1-G12 acceptance.";
    public string[] Limitations { get; init; } = ["First-pass reads do not evict the OS or shared block cache.", "RSS stability is assessed only in the memory scenario: ten seconds, last five seconds range <= max(4 MiB, 5% of mean). This is not the long-running G7 gate.", "Disk write amplification, true cold-cache I/O, multi-table fsync count and fixed hardware gates are NOT_RUN.", "Default many-table corpus is reduced; use 1000 cold tables and 10000 rows explicitly for the design-sized workload.", "A hard-killed runner may leave an incomplete report and its uniquely owned data directory; reconcile before rerunning."];
    public KvLsmCapacityOptions Options { get; init; } = new();
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinishedUtc { get; set; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public string[] Command { get; init; } = Environment.GetCommandLineArgs();
    public string Machine { get; init; } = Environment.MachineName;
    public string OS { get; init; } = RuntimeInformation.OSDescription;
    public string Runtime { get; init; } = RuntimeInformation.FrameworkDescription;
    public string Architecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();
    public string Cpu { get; init; } = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "not reported";
    public int VisibleProcessorCount { get; init; } = Environment.ProcessorCount;
    public string CoreBuild { get; init; } = typeof(KvKeyspace).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public string Drive { get; set; } = string.Empty;
    public string DataDirectory { get; set; } = string.Empty;
    public List<KvLsmCapacityMeasurement> Measurements { get; init; } = [];
    public List<long> PostCheckpointRssBytes { get; init; } = [];
    public double RssObservationSeconds { get; set; }
    public bool? RssStable { get; set; }
    public long? ObservedStableRssBytes { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public int LastObservedFileCount { get; set; }
    public long LastObservedDiskBytes { get; set; }
    public long MaxObservedDiskBytes { get; set; }
    public KvLsmCompletedIoMeasurement? CompletedLsmIo { get; set; }
    public int InventoryFilesDisappeared { get; set; }
    public long MigrationSourceDiskBytes { get; set; }
    public int[] GcCollections { get; set; } = [];
    public double GcPauseMilliseconds { get; set; }
    public int FullReadbacks { get; set; }
    public int SnapshotPrefillWrites { get; set; }
    public double MigrationLogicalGiB { get; set; }
    public double MigrationSecondsPerLogicalGiB { get; set; }
    public long HotTableWrites { get; set; }
    public int ColdTableWarmupReads { get; set; }
    public double HotTableWritesPerSecond { get; set; }
    public bool HotWriterReachedIterationLimit { get; set; }
    public bool CleanupComplete { get; set; }
    public string? Error { get; set; }
}

internal sealed record KvLsmCapacityMeasurement(string Name, long Operations, double ElapsedMilliseconds, double OperationsPerSecond,
    double? P50Microseconds, double? P99Microseconds, long AllocatedBytes);

/// <summary>kv/memory 场景当前引擎会话成功发布的 SST 累计大小，区别于目录存量与设备 I/O。</summary>
internal sealed record KvLsmCompletedIoMeasurement(long FlushOutputBytes, long CompactionInputBytes, long CompactionOutputBytes)
{
    public string Scope { get; init; } = "Current open session; completed published SST files only. Trivial moves add zero. Reopen resets counters. Excludes recovery-open work, failed jobs, WAL, manifest and device I/O; not total write amplification. Null for Legacy and scenarios without this measurement.";
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(KvLsmCapacityReport))]
internal partial class KvLsmCapacityJsonContext : JsonSerializerContext;
