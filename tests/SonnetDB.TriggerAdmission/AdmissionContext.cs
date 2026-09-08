using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Storage.Segments;

namespace SonnetDB.TriggerAdmission;

internal sealed class AdmissionContext : IDisposable
{
    private readonly HashSet<string> _databaseNames = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationToken _cancellationToken;
    private bool _disposed;

    internal AdmissionContext(bool quick, CancellationToken cancellationToken)
    {
        Quick = quick;
        _cancellationToken = cancellationToken;
        Root = Path.Combine(Path.GetTempPath(), "sndb339-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }
    internal bool Quick { get; }
    internal CancellationToken CancellationToken => _cancellationToken;
    internal List<AdmissionSample> Samples { get; } = [];
    internal List<AdmissionProcess> Processes { get; } = [];
    internal bool TemporaryDataRemoved { get; private set; }

    internal void Check()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_clock.Elapsed > TimeSpan.FromMinutes(10))
            throw new TimeoutException("#339 evidence exceeded its ten minute cooperative deadline.");
    }

    internal string NewDatabasePath(string name)
    {
        Check();
        if (_databaseNames.Count >= 128 || !_databaseNames.Add(name)
            || name.Length > 100 || name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Expected a unique, simple scenario name (maximum 128).", nameof(name));
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    internal void Measure(
        string model, string scenario, string root, int items, int cardinality,
        Func<Dictionary<string, double>> action, string detail)
    {
        Check();
        Require(Samples.Count < 256, "Evidence sample limit exceeded.");
        var before = DiskBytes(root);
        long allocation = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        Dictionary<string, double> metrics = action();
        timer.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocation;
        Check();
        var after = DiskBytes(root);
        Samples.Add(new AdmissionSample(model, scenario, items, cardinality,
            timer.Elapsed.TotalMilliseconds, allocated, after.Wal - before.Wal,
            after.Data - before.Data, metrics, detail));
        Console.WriteLine($"{model}/{scenario}: verified, items={items}, elapsed-ms={timer.Elapsed.TotalMilliseconds:F2}");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    internal static TsdbOptions Options(string root) => new()
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = true,
        Kv = new KvOptions { SyncWalOnEveryWrite = true, AutoCheckpointEnabled = false, ExpirerEnabled = false },
        FlushPolicy = new MemTableFlushPolicy
        {
            MaxPoints = long.MaxValue,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.MaxValue,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = true },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    };

    internal (long Wal, long Data) DiskBytes(string root)
    {
        string full = Path.GetFullPath(root);
        Require(full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Disk accounting must stay inside this run's database directory.");
        long wal = 0, data = 0;
        var directories = new Queue<(string Path, int Depth)>();
        directories.Enqueue((full, 0));
        int entryCount = 0;
        var timeout = Stopwatch.StartNew();
        for (int index = 0; index < 4096 && directories.Count > 0; index++)
        {
            Check();
            Require(timeout.Elapsed < TimeSpan.FromSeconds(10), "Disk accounting timed out.");
            var directory = directories.Dequeue();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                Check();
                Require(++entryCount <= 16384 && timeout.Elapsed < TimeSpan.FromSeconds(10),
                    "Disk accounting exceeded its entry/time budget.");
                FileAttributes attributes = File.GetAttributes(entry);
                Require((attributes & FileAttributes.ReparsePoint) == 0, "Unexpected link in owned evidence data.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Require(directory.Depth < 12, "Disk accounting depth limit exceeded.");
                    directories.Enqueue((entry, directory.Depth + 1));
                }
                else
                {
                    long size = new FileInfo(entry).Length;
                    string relative = Path.GetRelativePath(full, entry);
                    if (relative.Contains("wal", StringComparison.OrdinalIgnoreCase))
                        wal += size;
                    else
                        data += size;
                }
            }
        }
        Require(directories.Count == 0, "Disk accounting directory limit exceeded.");
        return (wal, data);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        string expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string full = Path.GetFullPath(Root);
        Require(string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("sndb339-", StringComparison.Ordinal),
            "Refusing cleanup outside this run's unique temporary directory.");
        // Every descendant was created under this private, unpredictable run directory.
        // Reject links before recursive cleanup; never follow a changed path to user data.
        var paths = new List<string> { full };
        var timeout = Stopwatch.StartNew();
        for (int index = 0; index < paths.Count && index < 16384; index++)
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(15), "Cleanup verification timed out.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(paths[index]))
            {
                Require(paths.Count < 16384 && timeout.Elapsed < TimeSpan.FromSeconds(15), "Cleanup limit exceeded.");
                var attributes = File.GetAttributes(entry);
                Require((attributes & FileAttributes.ReparsePoint) == 0, "Refusing cleanup through a link.");
                if ((attributes & FileAttributes.Directory) != 0)
                    paths.Add(entry);
            }
        }
        Directory.Delete(full, recursive: true);
        TemporaryDataRemoved = true;
    }
}
