using System.Diagnostics;
using SonnetDB.Engine;

namespace SonnetDB.Cli;

internal sealed partial class CliApplication
{
    private const string KvMigrationHelp = "sndb kv rollback-migration --database <数据库根目录> --path <KV或表目录> --offline";

    private int RunKvMigration(IReadOnlyList<string> args)
    {
        if (args.Count == 2 && IsHelp(args[1]))
        {
            _output.WriteLine(KvMigrationHelp);
            return ExitCodes.Success;
        }
        if (args.Count is < 2 or > 9 || args[1] != "rollback-migration")
            throw new CliUsageException(KvMigrationHelp);
        string? database = null;
        string? path = null;
        bool offline = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        for (int index = 2; index < args.Count; index++)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5) || !seen.Add(args[index]))
                throw new CliUsageException("KV 参数重复或解析预算超限。");
            switch (args[index])
            {
                case "--database": database = ReadRequiredValue(args, ref index, "数据库根目录"); break;
                case "--path": path = ReadRequiredValue(args, ref index, "KV 或表目录"); break;
                case "--offline": offline = true; break;
                default: throw new CliUsageException($"未知 KV 参数 '{args[index]}'。");
            }
        }
        if (!offline || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(path))
            throw new CliUsageException(KvMigrationHelp);
        Tsdb.RollbackKvMigration(database, path);
        _output.WriteLine($"迁移已回滚：{Path.GetFullPath(path)}。再次打开时设置 LegacyMigration=Disabled 以保留 legacy 布局。");
        return ExitCodes.Success;
    }
}
