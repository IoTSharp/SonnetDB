using System.Globalization;
using SonnetDB.Kv;

namespace SonnetDB.Benchmarks.Benchmarks;

internal static class KvLsmCapacityCommand
{
    internal static KvLsmCapacityOptions Parse(string[] arguments)
    {
        if (arguments.Length is < 1 or > 33 || arguments[0] != "--kv-lsm-capacity" || arguments.Length % 2 == 0)
            throw new ArgumentException("Use --kv-lsm-capacity followed by option/value pairs.");
        string[] allowed = ["--scenario", "--engine", "--entries", "--value-bytes", "--samples", "--cold-tables", "--rows-per-table",
            "--seconds", "--max-rss-mib", "--seed", "--sync-wal", "--order", "--source", "--output"];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < arguments.Length; index += 2)
        {
            if (!allowed.Contains(arguments[index], StringComparer.Ordinal) || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(arguments[index], arguments[index + 1]))
                throw new ArgumentException("Unknown, duplicate or missing option: " + arguments[index]);
        }
        string Read(string name, string fallback) => values.GetValueOrDefault(name, fallback);
        int Number(string name, int fallback) => int.Parse(Read(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        if (!Enum.TryParse(Read("--engine", "Lsm"), ignoreCase: true, out KvStorageEngine engine) || !Enum.IsDefined(engine))
            throw new ArgumentException("engine must be Legacy or Lsm.");
        string order = Read("--order", "sequential");
        if (order is not ("sequential" or "random")) throw new ArgumentException("order must be sequential or random.");
        var options = new KvLsmCapacityOptions
        {
            Scenario = Read("--scenario", "kv"),
            Engine = engine,
            Entries = Number("--entries", 100_000),
            ValueBytes = Number("--value-bytes", 100),
            Samples = Number("--samples", 200),
            ColdTables = Number("--cold-tables", 1000),
            RowsPerTable = Number("--rows-per-table", 10),
            TimeoutSeconds = Number("--seconds", 120),
            MaxRssMiB = Number("--max-rss-mib", 2048),
            Seed = Number("--seed", 42),
            SyncWal = bool.Parse(Read("--sync-wal", "false")),
            RandomOrder = order == "random",
            Source = Read("--source", ""),
            Output = Read("--output", ""),
        };
        options.Validate();
        return options;
    }
}
