using System.Collections.Concurrent;
using SonnetMQ;

namespace SonnetDB.Data.Mq;

internal static class SharedSndbMqRegistry
{
    private static readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _sync = new();

    public static SonnetMqStore Acquire(SonnetMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = NormalizeKey(options.Path);

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.RefCount++;
                return entry.Store;
            }

            var store = SonnetMqStore.Open(options);
            _entries[key] = new Entry(store);
            return store;
        }
    }

    public static void Release(SonnetMqStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        lock (_sync)
        {
            var key = FindKey(store);
            if (key is null)
                return;

            if (!_entries.TryGetValue(key, out var entry))
                return;

            entry.RefCount--;
            if (entry.RefCount > 0)
                return;

            _entries.TryRemove(key, out _);
            entry.Store.Dispose();
        }
    }

    private static string NormalizeKey(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? FindKey(SonnetMqStore store)
    {
        foreach (var pair in _entries)
        {
            if (ReferenceEquals(pair.Value.Store, store))
                return pair.Key;
        }

        return null;
    }

    private sealed class Entry
    {
        public Entry(SonnetMqStore store)
        {
            Store = store;
            RefCount = 1;
        }

        public SonnetMqStore Store { get; }

        public int RefCount { get; set; }
    }
}
