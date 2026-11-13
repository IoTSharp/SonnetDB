using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using SonnetDB.Model;
using SonnetDB.Query;

if (args.Length != 2)
    throw new ArgumentException("Expected <mode> <isolated-path>.");
string mode = args[0];
string path = Path.GetFullPath(args[1]);
Dictionary<string, string>? before = mode.StartsWith("reject-", StringComparison.Ordinal)
    || mode.StartsWith("tsdb-reject-", StringComparison.Ordinal) ? Snapshot(path) : null;
Assembly core = typeof(KvKeyspace).Assembly;
Console.WriteLine("core=" + core.Location);
Console.WriteLine("coreSha256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(core.Location))).ToLowerInvariant());
Console.WriteLine("coreVersion=" + core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
Console.WriteLine("mode=" + mode);
Console.WriteLine("path=" + path);

try
{
    switch (mode)
    {
        case "seed-legacy":
            SeedKeyspace(path, KvStorageEngine.Legacy);
            Console.WriteLine("result=seeded-legacy");
            return 0;
        case "seed-lsm":
            SeedKeyspace(path, KvStorageEngine.Legacy);
            using (KvKeyspace migrated = KvKeyspace.Open("compat", path, Options(KvStorageEngine.Lsm)))
                Verify(migrated);
            Console.WriteLine("result=seeded-lsm");
            return 0;
        case "migrate-and-verify":
        case "verify-lsm-current":
            using (KvKeyspace migrated = KvKeyspace.Open("compat", path, Options(KvStorageEngine.Lsm)))
            {
                if (migrated.StorageEngine != KvStorageEngine.Lsm)
                    throw new InvalidDataException("Expected migrated LSM storage.");
                Verify(migrated);
            }
            Console.WriteLine("result=migrated-and-verified");
            return 0;
        case "reject-directory-read":
        case "reject-directory-write":
            using (KvKeyspace opened = KvKeyspace.Open("compat", path, Options(KvStorageEngine.Lsm)))
            {
                if (mode.EndsWith("write", StringComparison.Ordinal))
                    opened.Put("unwanted-old-write", [99]);
                else
                    _ = opened.Get("key-0000");
            }
            Console.WriteLine("result=UNEXPECTED_SUPPORTED");
            return 3;
        case "reject-sst-read":
            using (KvSstReader.Open(path)) { }
            Console.WriteLine("result=UNEXPECTED_SUPPORTED");
            return 3;
        case "reject-sst-replace":
            using (new KvSstWriter(path)) { }
            Console.WriteLine("result=UNEXPECTED_OVERWRITE_ALLOWED");
            return 3;
        case "verify-legacy":
            using (KvKeyspace legacy = KvKeyspace.Open("compat", path, Options(KvStorageEngine.Legacy)))
                Verify(legacy);
            Console.WriteLine("result=verified-legacy");
            return 0;
        case "tsdb-reject-read":
        case "tsdb-reject-write":
            using (Tsdb rejected = Tsdb.Open(TsdbOptionsFor(path)))
            {
                if (mode == "tsdb-reject-write")
                    rejected.Write(Point.Create("compat-probe", 1,
                        fields: new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromLong(99) }));
                else
                    _ = rejected.Catalog.Count;
            }
            Console.WriteLine("result=UNEXPECTED_SUPPORTED");
            return 3;
        case "seed-tsdb-legacy":
            SeedTsdb(path);
            Console.WriteLine("result=seeded-tsdb-legacy");
            return 0;
#if CANDIDATE
        case "seed-shared":
            using (var budget = new KvDiskReadBudget(2))
            using (KvLsmStore store = KvLsmStore.Open(Path.Combine(path, "store"), Options(KvStorageEngine.Lsm), budget))
            {
                long id = store.Create("compat");
                using (KvKeyspace shared = KvKeyspace.OpenShared("compat", Path.Combine(path, "sidecar"), store, id, Options(KvStorageEngine.Lsm), budget))
                {
                    WriteEntries(shared);
                    Verify(shared);
                }
                store.Engine.Flush();
            }
            Console.WriteLine("result=seeded-shared-v3");
            return 0;
        case "verify-shared-current":
            using (var budget = new KvDiskReadBudget(2))
            using (KvLsmStore store = KvLsmStore.Open(Path.Combine(path, "store"), Options(KvStorageEngine.Lsm), budget))
            {
                long id = store.List().Single().StorageId;
                using KvKeyspace shared = KvKeyspace.OpenShared("compat", Path.Combine(path, "sidecar"), store, id, Options(KvStorageEngine.Lsm), budget);
                Verify(shared);
            }
            Console.WriteLine("result=verified-shared-current");
            return 0;
        case "seed-tsdb":
            SeedTsdb(path);
            Console.WriteLine("result=seeded-tsdb-v2");
            return 0;
        case "verify-tsdb-current":
            using (Tsdb current = Tsdb.Open(TsdbOptionsFor(path)))
                VerifyTsdb(current);
            Console.WriteLine("result=verified-tsdb-current");
            return 0;
#endif
        default:
            throw new ArgumentException("Unknown probe mode: " + mode);
    }
}
catch (InvalidDataException exception) when (mode.StartsWith("reject-", StringComparison.Ordinal)
    || mode.StartsWith("tsdb-reject-", StringComparison.Ordinal)
        && exception.Message.Contains("Catalog", StringComparison.Ordinal)
        && exception.Message.Contains("format version", StringComparison.Ordinal)
        && exception.Message.Contains("got 2", StringComparison.Ordinal))
{
    VerifyUnchanged(before!, path);
    Console.WriteLine("result=rejected");
    Console.WriteLine("exception=" + exception.GetType().FullName);
    Console.WriteLine("message=" + exception.Message);
    return 0;
}
catch (IOException exception) when (mode == "reject-sst-replace")
{
    VerifyUnchanged(before!, path);
    Console.WriteLine("result=rejected-existing-file");
    Console.WriteLine("exception=" + exception.GetType().FullName);
    Console.WriteLine("message=" + exception.Message);
    return 0;
}

static void SeedKeyspace(string path, KvStorageEngine engine)
{
    if (Directory.Exists(path))
        throw new IOException("Seed target already exists.");
    using KvKeyspace store = KvKeyspace.Open("compat", path, Options(engine));
    WriteEntries(store);
    store.CreateSnapshot();
    Verify(store);
}

static void WriteEntries(KvKeyspace store)
{
    var deadline = Stopwatch.StartNew();
    for (int index = 0; index < 65; index++)
    {
        if (deadline.Elapsed > TimeSpan.FromSeconds(15))
            throw new TimeoutException("Probe seed deadline exceeded.");
        store.Put($"key-{index:D4}", Encoding.UTF8.GetBytes("value-" + index));
    }
}

static void Verify(KvKeyspace store)
{
    if (store.Count != 65)
        throw new InvalidDataException("Compatibility key count mismatch.");
    var deadline = Stopwatch.StartNew();
    for (int index = 0; index < 65; index++)
    {
        if (deadline.Elapsed > TimeSpan.FromSeconds(15))
            throw new TimeoutException("Probe verification deadline exceeded.");
        if (!store.Get($"key-{index:D4}")!.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("value-" + index)))
            throw new InvalidDataException("Compatibility full-value mismatch.");
    }
}

static KvOptions Options(KvStorageEngine engine) => KvOptions.Default with
{
    StorageEngine = engine,
    SyncWalOnEveryWrite = true,
    ExpirerEnabled = false,
    CleanupEnabled = false,
    AutoCheckpointEnabled = false,
};

static TsdbOptions TsdbOptionsFor(string root) => new()
{
    RootDirectory = root,
    BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
    Compaction = new CompactionPolicy { Enabled = false },
    Kv = Options(KvStorageEngine.Lsm),
};

static Point TimeSeriesPoint(int index) => Point.Create("compat-probe", 1_700_000_000_000L + index,
    tags: new Dictionary<string, string> { ["host"] = "compat" },
    fields: new Dictionary<string, FieldValue>
    {
        ["integer"] = FieldValue.FromLong(17 + index),
        ["number"] = FieldValue.FromDouble(0.25 + index),
        ["text"] = FieldValue.FromString("point-" + index),
    });

static void SeedTsdb(string root)
{
    if (Directory.Exists(root))
        throw new IOException("Seed target already exists.");
    using (Tsdb database = Tsdb.Open(TsdbOptionsFor(root)))
    {
        var deadline = Stopwatch.StartNew();
        for (int index = 0; index < 3; index++)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("Time-series seed deadline exceeded.");
            database.Write(TimeSeriesPoint(index));
        }
        database.FlushNow();
        VerifyTsdb(database);
    }
    using Tsdb reopened = Tsdb.Open(TsdbOptionsFor(root));
    VerifyTsdb(reopened);
}

static void VerifyTsdb(Tsdb database)
{
    var series = database.Catalog.Snapshot().Single(entry => entry.Measurement == "compat-probe");
    var deadline = Stopwatch.StartNew();
    foreach (string field in new[] { "integer", "number", "text" })
    {
        if (deadline.Elapsed > TimeSpan.FromSeconds(15))
            throw new TimeoutException("Time-series verification deadline exceeded.");
        DataPoint[] points = database.Query.Execute(new PointQuery(series.Id, field,
            new TimeRange(1_700_000_000_000L, 1_700_000_000_002L))).ToArray();
        if (points.Length != 3)
            throw new InvalidDataException("Time-series point count mismatch.");
        for (int index = 0; index < points.Length; index++)
        {
            Point expected = TimeSeriesPoint(index);
            if (points[index].Timestamp != expected.Timestamp || points[index].Value != expected.Fields[field])
                throw new InvalidDataException("Time-series complete value mismatch: " + field);
        }
    }
    Console.WriteLine("verifiedTimeSeriesFields=9");
}

static Dictionary<string, string> Snapshot(string root)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var deadline = Stopwatch.StartNew();
    IEnumerable<string> paths = File.Exists(root) ? new[] { root }
        : Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 16,
            AttributesToSkip = FileAttributes.ReparsePoint,
        });
    foreach (string file in paths)
    {
        if (result.Count >= 1024 || deadline.Elapsed > TimeSpan.FromSeconds(15))
            throw new TimeoutException("Compatibility snapshot exceeded its file/deadline limit.");
        if (new FileInfo(file).Length > 64L * 1024 * 1024)
            throw new IOException("Compatibility fixture file exceeds 64 MiB.");
        result.Add(File.Exists(root) ? Path.GetFileName(file) : Path.GetRelativePath(root, file),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant());
    }
    return result;
}

static void VerifyUnchanged(IReadOnlyDictionary<string, string> before, string root)
{
    Dictionary<string, string> after = Snapshot(root);
    if (before.Count != after.Count || before.Any(item => !after.TryGetValue(item.Key, out string? sha) || sha != item.Value))
        throw new InvalidOperationException("Rejected historical operation changed fixture files.");
    Console.WriteLine("filesUnchanged=true");
    Console.WriteLine("fileCount=" + after.Count);
}
