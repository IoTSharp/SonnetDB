using System.Globalization;
using System.Reflection;
using SonnetDB.Backup;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;

namespace SonnetDB.Cli;

internal sealed class CliApplication
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly CliProfileStore _profileStore;

    public CliApplication(TextReader input, TextWriter output, TextWriter error)
        : this(input, output, error, new CliProfileStore())
    {
    }

    internal CliApplication(TextReader input, TextWriter output, TextWriter error, CliProfileStore profileStore)
    {
        _input = input;
        _output = output;
        _error = error;
        _profileStore = profileStore;
    }

    public int Run(IReadOnlyList<string> args)
    {
        try
        {
            if (args.Count == 0)
            {
                WriteHelp();
                return ExitCodes.Success;
            }

            var command = args[0];
            if (IsHelp(command))
            {
                WriteHelp();
                return ExitCodes.Success;
            }

            if (IsVersion(command))
            {
                WriteVersion();
                return ExitCodes.Success;
            }

            return command.ToLowerInvariant() switch
            {
                "sql" => RunSql(args),
                "repl" => RunRepl(args),
                "local" => RunLocal(args),
                "remote" => RunRemote(args),
                "connect" => RunConnect(args),
                "backup" => RunBackup(args),
                "copilot" => new CopilotCommandRunner(_output, _error).Run(args),
                _ => FailParse($"未知命令 '{command}'。"),
            };
        }
        catch (CliUsageException ex)
        {
            _error.WriteLine(ex.Message);
            _error.WriteLine("使用 `sndb help` 查看帮助。");
            return ExitCodes.InvalidArguments;
        }
        catch (Exception ex)
        {
            _error.WriteLine(ex.Message);
            return ExitCodes.ExecutionFailed;
        }
    }

    // ── sql / repl (legacy connection-string commands) ────────────────────────

    private int RunSql(IReadOnlyList<string> args)
    {
        var options = ParseSqlOptions(args);
        ExecuteAndRender(options.ConnectionString, options.SqlText);
        return ExitCodes.Success;
    }

    private int RunRepl(IReadOnlyList<string> args)
    {
        var options = ParseReplOptions(args);
        return RunRepl(options.ConnectionString);
    }

    // ── local ─────────────────────────────────────────────────────────────────

    private int RunLocal(IReadOnlyList<string> args)
    {
        var options = ParseLocalOptions(args);
        return options.Action switch
        {
            LocalAction.Use => RunLocalUse(options),
            LocalAction.List => RunLocalList(),
            LocalAction.Remove => RunLocalRemove(options),
            _ => throw new InvalidOperationException("未知的本地命令动作。"),
        };
    }

    private int RunLocalUse(LocalCommandOptions options)
    {
        var rootPath = ResolveLocalPath(options);
        var connectionString = CreateLocalConnectionString(rootPath);

        if (!string.IsNullOrWhiteSpace(options.SaveProfileName))
            _profileStore.UpsertLocal(new CliLocalProfile(options.SaveProfileName, rootPath));

        if (options.SetDefault)
        {
            var name = options.SaveProfileName ?? options.ProfileName;
            if (string.IsNullOrWhiteSpace(name))
                throw new CliUsageException("--default 只能与 --save-profile 或 --profile 一起使用。");
            _profileStore.SetDefault(name);
        }

        return options.Mode switch
        {
            ExecMode.Info => WriteConnectionInfo(connectionString),
            ExecMode.Sql => ExecuteSqlCommand(connectionString, options.SqlText!),
            ExecMode.Repl => RunRepl(connectionString),
            _ => throw new InvalidOperationException("未知的执行模式。"),
        };
    }

    private int RunLocalList()
    {
        var doc = _profileStore.Load();
        if (doc.LocalProfiles.Count == 0)
        {
            _output.WriteLine("未配置任何 local profile。");
            return ExitCodes.Success;
        }

        foreach (var p in doc.LocalProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var marker = string.Equals(p.Name, doc.DefaultProfile, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            _output.WriteLine($"{marker} {p.Name} => {p.Path}");
        }

        return ExitCodes.Success;
    }

    private int RunLocalRemove(LocalCommandOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProfileName))
            throw new CliUsageException("local remove 必须通过 --profile 指定名称。");

        if (!_profileStore.RemoveLocal(options.ProfileName))
            throw new CliUsageException($"未找到 local profile '{options.ProfileName}'。");

        _output.WriteLine($"已删除 local profile '{options.ProfileName}'。");
        return ExitCodes.Success;
    }

    private string ResolveLocalPath(LocalCommandOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RootPath))
            return options.RootPath;

        if (!string.IsNullOrWhiteSpace(options.ProfileName))
        {
            var profile = _profileStore.GetLocal(options.ProfileName)
                ?? throw new CliUsageException($"未找到 local profile '{options.ProfileName}'。");
            return profile.Path;
        }

        if (options.UseDefaultProfile)
        {
            var (local, _) = _profileStore.GetDefault();
            if (local is null)
                throw new CliUsageException("未设置默认 local profile，请先通过 --save-profile ... --default 设置。");
            return local.Path;
        }

        // implicit default: try global default
        var (implicitLocal, _) = _profileStore.GetDefault();
        if (implicitLocal is not null)
            return implicitLocal.Path;

        throw new CliUsageException("必须通过 --path 指定本地数据目录，或通过 --profile / 默认 profile 提供。");
    }

    // ── remote ────────────────────────────────────────────────────────────────

    private int RunRemote(IReadOnlyList<string> args)
    {
        var options = ParseRemoteOptions(args);
        return options.Action switch
        {
            RemoteAction.Use => RunRemoteUse(options),
            RemoteAction.List => RunRemoteList(),
            RemoteAction.Remove => RunRemoteRemove(options),
            _ => throw new InvalidOperationException("未知的远程命令动作。"),
        };
    }

    private int RunRemoteUse(RemoteCommandOptions options)
    {
        var resolved = ResolveRemoteOptions(options);
        var connectionString = CreateRemoteConnectionString(resolved);

        if (!string.IsNullOrWhiteSpace(options.SaveProfileName))
        {
            _profileStore.Upsert(new CliRemoteProfile(
                options.SaveProfileName,
                resolved.BaseUrl!,
                resolved.Database!,
                resolved.Token,
                resolved.Timeout ?? 100));
        }

        if (options.SetDefault)
        {
            var name = options.SaveProfileName ?? options.ProfileName;
            if (string.IsNullOrWhiteSpace(name))
                throw new CliUsageException("--default 只能与 --save-profile 或 --profile 一起使用。");
            _profileStore.SetDefault(name);
        }

        return resolved.Mode switch
        {
            ExecMode.Info => WriteConnectionInfo(connectionString),
            ExecMode.Sql => ExecuteSqlCommand(connectionString, resolved.SqlText!),
            ExecMode.Repl => RunRepl(connectionString),
            _ => throw new InvalidOperationException("未知的执行模式。"),
        };
    }

    private int RunRemoteList()
    {
        var doc = _profileStore.Load();
        if (doc.Profiles.Count == 0)
        {
            _output.WriteLine("未配置任何 remote profile。");
            return ExitCodes.Success;
        }

        foreach (var p in doc.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var marker = string.Equals(p.Name, doc.DefaultProfile, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            _output.WriteLine($"{marker} {p.Name} => {p.BaseUrl}/{p.Database} (timeout={p.Timeout})");
        }

        return ExitCodes.Success;
    }

    private int RunRemoteRemove(RemoteCommandOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProfileName))
            throw new CliUsageException("remote remove 必须通过 --profile 指定名称。");

        if (!_profileStore.Remove(options.ProfileName))
            throw new CliUsageException($"未找到 remote profile '{options.ProfileName}'。");

        _output.WriteLine($"已删除 remote profile '{options.ProfileName}'。");
        return ExitCodes.Success;
    }

    private RemoteCommandOptions ResolveRemoteOptions(RemoteCommandOptions options)
    {
        CliRemoteProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(options.ProfileName))
            profile = _profileStore.Get(options.ProfileName);
        else if (options.UseDefaultProfile)
        {
            var (_, remote) = _profileStore.GetDefault();
            profile = remote;
        }

        if (profile is null
            && string.IsNullOrWhiteSpace(options.BaseUrl)
            && string.IsNullOrWhiteSpace(options.Database)
            && string.IsNullOrWhiteSpace(options.Token))
        {
            var (_, remote) = _profileStore.GetDefault();
            profile = remote;
        }

        var baseUrl = options.BaseUrl ?? profile?.BaseUrl;
        var database = options.Database ?? profile?.Database;
        var token = options.Token ?? profile?.Token;
        var timeout = options.Timeout ?? profile?.Timeout ?? 100;

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new CliUsageException("必须通过 --url 指定 SonnetDB 地址，或通过 --profile / 默认 profile 提供。");

        if (string.IsNullOrWhiteSpace(database))
            throw new CliUsageException("必须通过 --database 指定数据库名，或通过 --profile / 默认 profile 提供。");

        return options with { BaseUrl = baseUrl, Database = database, Token = token, Timeout = timeout };
    }

    // ── connect (unified profile shortcut) ───────────────────────────────────

    private int RunConnect(IReadOnlyList<string> args)
    {
        var options = ParseConnectOptions(args);

        string connectionString;
        if (options.UseDefault)
        {
            var (local, remote) = _profileStore.GetDefault();
            if (local is not null)
                connectionString = CreateLocalConnectionString(local.Path);
            else if (remote is not null)
                connectionString = CreateRemoteConnectionStringFromProfile(remote);
            else
                throw new CliUsageException("未设置任何默认 profile，请先通过 local/remote --save-profile ... --default 设置。");
        }
        else
        {
            var name = options.ProfileName!;
            var (local, remote) = _profileStore.GetByName(name);
            if (local is not null)
                connectionString = CreateLocalConnectionString(local.Path);
            else if (remote is not null)
                connectionString = CreateRemoteConnectionStringFromProfile(remote);
            else
                throw new CliUsageException($"未找到 profile '{name}'。");
        }

        return options.Mode switch
        {
            ExecMode.Info => WriteConnectionInfo(connectionString),
            ExecMode.Sql => ExecuteSqlCommand(connectionString, options.SqlText!),
            ExecMode.Repl => RunRepl(connectionString),
            _ => throw new InvalidOperationException("未知的执行模式。"),
        };
    }

    // ── backup ───────────────────────────────────────────────────────────────

    private int RunBackup(IReadOnlyList<string> args)
    {
        var options = ParseBackupOptions(args);
        var service = new BackupService();
        return options.Action switch
        {
            BackupAction.Create => RunBackupCreate(service, options),
            BackupAction.Inspect => RunBackupInspect(service, options),
            BackupAction.Verify => RunBackupVerify(service, options),
            BackupAction.DryRun => RunBackupRestoreDryRun(service, options),
            BackupAction.Restore => RunBackupRestore(service, options),
            BackupAction.RebuildIndexes => RunBackupRebuildIndexes(service, options),
            _ => throw new InvalidOperationException("未知的备份命令动作。"),
        };
    }

    private int RunBackupCreate(BackupService service, BackupCommandOptions options)
    {
        using var db = Tsdb.Open(CreateMaintenanceOptions(options.SourcePath!));
        var manifest = service.Create(db, new BackupCreateOptions
        {
            DestinationDirectory = options.BackupPath!,
            Overwrite = options.Overwrite,
            IncludeFullTextIndexes = options.IncludeFullTextIndexes,
        });

        _output.WriteLine("备份已创建。");
        WriteBackupSummary(manifest);
        return ExitCodes.Success;
    }

    private int RunBackupInspect(BackupService service, BackupCommandOptions options)
    {
        var manifest = service.ReadManifest(options.BackupPath!);
        WriteBackupSummary(manifest);
        WriteIndexSummary(manifest);
        return ExitCodes.Success;
    }

    private int RunBackupVerify(BackupService service, BackupCommandOptions options)
    {
        var result = service.Verify(options.BackupPath!);
        if (result.IsValid)
        {
            _output.WriteLine(FormattableString.Invariant($"备份校验通过：{result.CheckedFiles} 个文件。"));
            return ExitCodes.Success;
        }

        _error.WriteLine(FormattableString.Invariant($"备份校验失败：{result.CheckedFiles} 个文件已检查。"));
        foreach (string error in result.Errors)
            _error.WriteLine(" - " + error);
        return ExitCodes.ExecutionFailed;
    }

    private int RunBackupRestore(BackupService service, BackupCommandOptions options)
    {
        var manifest = service.Restore(new BackupRestoreOptions
        {
            BackupDirectory = options.BackupPath!,
            TargetDirectory = options.TargetPath!,
            Overwrite = options.Overwrite,
            VerifyBeforeRestore = options.VerifyBeforeRestore,
        });

        _output.WriteLine("备份已恢复。");
        WriteBackupSummary(manifest);
        _output.WriteLine("恢复目标: " + options.TargetPath);
        if (!options.RebuildIndexes)
            return ExitCodes.Success;

        using var db = Tsdb.Open(CreateMaintenanceOptions(options.TargetPath!));
        var rebuild = service.RebuildIndexes(db);
        WriteIndexRebuildSummary(rebuild);
        return rebuild.FailedIndexes == 0 ? ExitCodes.Success : ExitCodes.ExecutionFailed;
    }

    private int RunBackupRestoreDryRun(BackupService service, BackupCommandOptions options)
    {
        var dryRun = service.RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = options.BackupPath!,
            TargetDirectory = options.TargetPath!,
            Overwrite = options.Overwrite,
            VerifyBeforeRestore = options.VerifyBeforeRestore,
        });

        var writer = dryRun.IsValid ? _output : _error;
        writer.WriteLine(dryRun.IsValid ? "恢复 dry-run 通过，未复制任何文件。" : "恢复 dry-run 失败。");
        writer.WriteLine(FormattableString.Invariant(
            $"文件: {dryRun.FileCount} 个, {dryRun.TotalBytes} bytes, indexes={dryRun.IndexCount}"));
        writer.WriteLine(FormattableString.Invariant(
            $"目标目录: exists={dryRun.TargetDirectoryExists}, empty={dryRun.TargetDirectoryEmpty}, overwrite={options.Overwrite}"));
        writer.WriteLine(FormattableString.Invariant(
            $"校验: valid={dryRun.Verification.IsValid}, checked_files={dryRun.Verification.CheckedFiles}"));
        foreach (string error in dryRun.Errors)
            writer.WriteLine(" - " + error);
        return dryRun.IsValid ? ExitCodes.Success : ExitCodes.ExecutionFailed;
    }

    private int RunBackupRebuildIndexes(BackupService service, BackupCommandOptions options)
    {
        using var db = Tsdb.Open(CreateMaintenanceOptions(options.SourcePath!));
        var result = service.RebuildIndexes(db);
        WriteIndexRebuildSummary(result);
        return result.FailedIndexes == 0 ? ExitCodes.Success : ExitCodes.ExecutionFailed;
    }

    private void WriteIndexRebuildSummary(BackupIndexRebuildResult result)
    {
        _output.WriteLine(FormattableString.Invariant(
            $"索引补建: total={result.TotalIndexes}, rebuilt={result.RebuiltIndexes}, planned={result.PlannedIndexes}, failed={result.FailedIndexes}"));
        foreach (var entry in result.Entries.OrderBy(static x => x.Model, StringComparer.Ordinal)
                     .ThenBy(static x => x.Owner, StringComparer.Ordinal)
                     .ThenBy(static x => x.Name, StringComparer.Ordinal))
        {
            string count = entry.DocumentCount is null
                ? string.Empty
                : FormattableString.Invariant($", documents={entry.DocumentCount.Value}");
            _output.WriteLine($"  {entry.Model}/{entry.Owner}/{entry.Name}: {entry.Kind}, {entry.Status}{count}");
        }
    }

    private void WriteBackupSummary(BackupManifest manifest)
    {
        _output.WriteLine(FormattableString.Invariant($"格式: {manifest.DatabaseFormat} / manifest v{manifest.FormatVersion}"));
        _output.WriteLine("创建时间: " + manifest.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        _output.WriteLine(FormattableString.Invariant(
            $"一致性: checkpoint_lsn={manifest.Consistency.CheckpointLsn}, next_segment_id={manifest.Consistency.NextSegmentId}, segments={manifest.Consistency.SegmentCount}"));
        _output.WriteLine(FormattableString.Invariant(
            $"模型: measurements={manifest.Models.Measurements.Count}, tables={manifest.Models.Tables.Count}, keyspaces={manifest.Models.Keyspaces.Count}, documents={manifest.Models.DocumentCollections.Count}"));
        _output.WriteLine(FormattableString.Invariant(
            $"文件: {manifest.Files.Count} 个, {manifest.Consistency.TotalBytes} bytes"));
    }

    private void WriteIndexSummary(BackupManifest manifest)
    {
        if (manifest.Indexes.Count == 0)
            return;

        _output.WriteLine("索引:");
        foreach (var index in manifest.Indexes.OrderBy(static x => x.Model, StringComparer.Ordinal)
                     .ThenBy(static x => x.Owner, StringComparer.Ordinal)
                     .ThenBy(static x => x.Name, StringComparer.Ordinal))
        {
            string included = index.Included ? "included" : "excluded";
            string rebuildable = index.Rebuildable ? "rebuildable" : "required";
            _output.WriteLine($"  {index.Model}/{index.Owner}/{index.Name}: {index.Kind}, {included}, {rebuildable}");
        }
    }

    private static TsdbOptions CreateMaintenanceOptions(string rootDirectory)
        => new()
        {
            RootDirectory = rootDirectory,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Retention = new RetentionPolicy { Enabled = false },
        };

    // ── shared execution ──────────────────────────────────────────────────────

    private int ExecuteSqlCommand(string connectionString, string sqlText)
    {
        ExecuteAndRender(connectionString, sqlText);
        return ExitCodes.Success;
    }

    private int RunRepl(string connectionString)
    {
        _output.WriteLine("SonnetDB SQL REPL");
        _output.WriteLine($"连接: {connectionString}");
        _output.WriteLine("输入单行 SQL 后回车执行；输入 exit 或 quit 退出。");

        while (true)
        {
            _output.Write("sndb> ");
            _output.Flush();

            var line = _input.ReadLine();
            if (line is null) break;

            line = line.Trim();
            if (line.Length == 0) continue;

            if (string.Equals(line, "exit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, "quit", StringComparison.OrdinalIgnoreCase))
                break;

            try
            {
                ExecuteAndRender(connectionString, line);
            }
            catch (Exception ex)
            {
                _error.WriteLine(ex.Message);
            }
        }

        return ExitCodes.Success;
    }

    private int WriteConnectionInfo(string connectionString)
    {
        _output.WriteLine(connectionString);
        return ExitCodes.Success;
    }

    // ── connection string builders ────────────────────────────────────────────

    private static string CreateLocalConnectionString(string rootPath)
        => new SndbConnectionStringBuilder
        {
            Mode = SndbProviderMode.Embedded,
            DataSource = rootPath,
        }.ConnectionString;

    private static string CreateRemoteConnectionString(RemoteCommandOptions options)
    {
        var builder = new SndbConnectionStringBuilder
        {
            Mode = SndbProviderMode.Remote,
            DataSource = $"{options.BaseUrl!.TrimEnd('/')}/{options.Database}",
            Timeout = options.Timeout ?? 100,
        };
        if (!string.IsNullOrWhiteSpace(options.Token))
            builder.Token = options.Token;
        return builder.ConnectionString;
    }

    private static string CreateRemoteConnectionStringFromProfile(CliRemoteProfile profile)
    {
        var builder = new SndbConnectionStringBuilder
        {
            Mode = SndbProviderMode.Remote,
            DataSource = $"{profile.BaseUrl.TrimEnd('/')}/{profile.Database}",
            Timeout = profile.Timeout,
        };
        if (!string.IsNullOrWhiteSpace(profile.Token))
            builder.Token = profile.Token;
        return builder.ConnectionString;
    }

    // ── query execution ───────────────────────────────────────────────────────

    private void ExecuteAndRender(string connectionString, string sqlText)
    {
        using var connection = new SndbConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sqlText;

        using var reader = command.ExecuteReader();
        if (reader.FieldCount == 0)
        {
            _output.WriteLine(FormattableString.Invariant($"OK ({reader.RecordsAffected} rows affected)"));
            return;
        }

        var headers = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            headers[i] = reader.GetName(i);

        var rows = new List<string[]>(capacity: 32);
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = FormatValue(reader.GetValue(i));
            rows.Add(row);
        }

        ConsoleTableFormatter.Write(_output, headers, rows);
        _output.WriteLine(FormattableString.Invariant($"({rows.Count} row(s))"));
    }

    private static string FormatValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    // ── argument parsers ──────────────────────────────────────────────────────

    private static SqlCommandOptions ParseSqlOptions(IReadOnlyList<string> args)
    {
        string? connectionString = null;
        string? sqlText = null;
        string? sqlFile = null;

        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--connection" or "-c":
                    connectionString = ReadRequiredValue(args, ref i, "连接字符串"); break;
                case "--command" or "-q":
                    sqlText = ReadRequiredValue(args, ref i, "SQL 文本"); break;
                case "--file" or "-f":
                    sqlFile = ReadRequiredValue(args, ref i, "SQL 文件路径"); break;
                case "--help" or "-h":
                    throw new CliUsageException(BuildSqlHelp());
                default:
                    throw new CliUsageException($"未知参数 '{args[i]}'。");
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CliUsageException("必须通过 --connection 指定连接字符串。");
        if (string.IsNullOrWhiteSpace(sqlText) == string.IsNullOrWhiteSpace(sqlFile))
            throw new CliUsageException("必须且只能通过 --command 或 --file 提供一条 SQL。");

        return new SqlCommandOptions(connectionString, sqlText ?? File.ReadAllText(sqlFile!));
    }

    private static ReplCommandOptions ParseReplOptions(IReadOnlyList<string> args)
    {
        string? connectionString = null;

        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--connection" or "-c":
                    connectionString = ReadRequiredValue(args, ref i, "连接字符串"); break;
                case "--help" or "-h":
                    throw new CliUsageException(BuildReplHelp());
                default:
                    throw new CliUsageException($"未知参数 '{args[i]}'。");
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CliUsageException("必须通过 --connection 指定连接字符串。");

        return new ReplCommandOptions(connectionString);
    }

    private static LocalCommandOptions ParseLocalOptions(IReadOnlyList<string> args)
    {
        string? rootPath = null;
        string? sqlText = null;
        string? sqlFile = null;
        string? profileName = null;
        string? saveProfileName = null;
        var mode = ExecMode.Info;
        var action = LocalAction.Use;
        var setDefault = false;
        var useDefaultProfile = false;

        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "list":
                    EnsureActionNotSpecified(action != LocalAction.Use, args[i]);
                    action = LocalAction.List; break;
                case "remove":
                    EnsureActionNotSpecified(action != LocalAction.Use, args[i]);
                    action = LocalAction.Remove; break;
                case "--path" or "-p":
                    rootPath = ReadRequiredValue(args, ref i, "本地数据目录"); break;
                case "--profile":
                    profileName = ReadRequiredValue(args, ref i, "profile 名称"); break;
                case "--save-profile":
                    saveProfileName = ReadRequiredValue(args, ref i, "profile 名称"); break;
                case "--default":
                    setDefault = true; break;
                case "--use-default":
                    useDefaultProfile = true; break;
                case "--command" or "-q":
                    sqlText = ReadRequiredValue(args, ref i, "SQL 文本");
                    mode = ExecMode.Sql; break;
                case "--file" or "-f":
                    sqlFile = ReadRequiredValue(args, ref i, "SQL 文件路径");
                    mode = ExecMode.Sql; break;
                case "--repl":
                    mode = ExecMode.Repl; break;
                case "--help" or "-h":
                    throw new CliUsageException(BuildLocalHelp());
                default:
                    throw new CliUsageException($"未知参数 '{args[i]}'。");
            }
        }

        if (action is LocalAction.List or LocalAction.Remove)
        {
            if (mode != ExecMode.Info)
                throw new CliUsageException("local list/remove 不支持 --command、--file 或 --repl。");
            if (action == LocalAction.Remove && !string.IsNullOrWhiteSpace(saveProfileName))
                throw new CliUsageException("local remove 不支持 --save-profile。");
            return new LocalCommandOptions(rootPath, profileName, saveProfileName, mode, sqlText, action, setDefault, useDefaultProfile);
        }

        if (mode == ExecMode.Sql)
        {
            if (string.IsNullOrWhiteSpace(sqlText) == string.IsNullOrWhiteSpace(sqlFile))
                throw new CliUsageException("必须且只能通过 --command 或 --file 提供一条 SQL。");
            sqlText ??= File.ReadAllText(sqlFile!);
        }

        return new LocalCommandOptions(rootPath, profileName, saveProfileName, mode, sqlText, action, setDefault, useDefaultProfile);
    }

    private static RemoteCommandOptions ParseRemoteOptions(IReadOnlyList<string> args)
    {
        string? baseUrl = null;
        string? database = null;
        string? token = null;
        string? sqlText = null;
        string? sqlFile = null;
        int? timeout = null;
        string? profileName = null;
        string? saveProfileName = null;
        var mode = ExecMode.Info;
        var action = RemoteAction.Use;
        var setDefault = false;
        var useDefaultProfile = false;

        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "list":
                    EnsureActionNotSpecified(action != RemoteAction.Use, args[i]);
                    action = RemoteAction.List; break;
                case "remove":
                    EnsureActionNotSpecified(action != RemoteAction.Use, args[i]);
                    action = RemoteAction.Remove; break;
                case "--url" or "-u":
                    baseUrl = ReadRequiredValue(args, ref i, "服务端地址"); break;
                case "--database" or "-d":
                    database = ReadRequiredValue(args, ref i, "数据库名"); break;
                case "--token" or "-t":
                    token = ReadRequiredValue(args, ref i, "访问令牌"); break;
                case "--timeout":
                    timeout = ParsePositiveInt(ReadRequiredValue(args, ref i, "超时时间（秒）"), "--timeout"); break;
                case "--profile":
                    profileName = ReadRequiredValue(args, ref i, "profile 名称"); break;
                case "--save-profile":
                    saveProfileName = ReadRequiredValue(args, ref i, "profile 名称"); break;
                case "--default":
                    setDefault = true; break;
                case "--use-default":
                    useDefaultProfile = true; break;
                case "--command" or "-q":
                    sqlText = ReadRequiredValue(args, ref i, "SQL 文本");
                    mode = ExecMode.Sql; break;
                case "--file" or "-f":
                    sqlFile = ReadRequiredValue(args, ref i, "SQL 文件路径");
                    mode = ExecMode.Sql; break;
                case "--repl":
                    mode = ExecMode.Repl; break;
                case "--help" or "-h":
                    throw new CliUsageException(BuildRemoteHelp());
                default:
                    throw new CliUsageException($"未知参数 '{args[i]}'。");
            }
        }

        if (action is RemoteAction.List or RemoteAction.Remove)
        {
            if (mode != ExecMode.Info)
                throw new CliUsageException("remote list/remove 不支持 --command、--file 或 --repl。");
            if (action == RemoteAction.Remove && (!string.IsNullOrWhiteSpace(saveProfileName) || setDefault))
                throw new CliUsageException("remote remove 不支持 --save-profile 或 --default。");
            return new RemoteCommandOptions(baseUrl, database, token, timeout, mode, sqlText, action, profileName, saveProfileName, setDefault, useDefaultProfile);
        }

        if (mode == ExecMode.Sql)
        {
            if (string.IsNullOrWhiteSpace(sqlText) == string.IsNullOrWhiteSpace(sqlFile))
                throw new CliUsageException("必须且只能通过 --command 或 --file 提供一条 SQL。");
            sqlText ??= File.ReadAllText(sqlFile!);
        }

        return new RemoteCommandOptions(baseUrl, database, token, timeout, mode, sqlText, action, profileName, saveProfileName, setDefault, useDefaultProfile);
    }

    private static ConnectOptions ParseConnectOptions(IReadOnlyList<string> args)
    {
        string? profileName = null;
        string? sqlText = null;
        string? sqlFile = null;
        var mode = ExecMode.Info;
        var useDefault = false;

        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--default":
                    useDefault = true; break;
                case "--command" or "-q":
                    sqlText = ReadRequiredValue(args, ref i, "SQL 文本");
                    mode = ExecMode.Sql; break;
                case "--file" or "-f":
                    sqlFile = ReadRequiredValue(args, ref i, "SQL 文件路径");
                    mode = ExecMode.Sql; break;
                case "--repl":
                    mode = ExecMode.Repl; break;
                case "--help" or "-h":
                    throw new CliUsageException(BuildConnectHelp());
                default:
                    // positional: profile name
                    if (!args[i].StartsWith('-') && profileName is null)
                        profileName = args[i];
                    else
                        throw new CliUsageException($"未知参数 '{args[i]}'。");
                    break;
            }
        }

        if (!useDefault && string.IsNullOrWhiteSpace(profileName))
            throw new CliUsageException("必须提供 profile 名称或 --default 参数。");

        if (useDefault && !string.IsNullOrWhiteSpace(profileName))
            throw new CliUsageException("不能同时指定 profile 名称和 --default。");

        if (mode == ExecMode.Sql)
        {
            if (string.IsNullOrWhiteSpace(sqlText) == string.IsNullOrWhiteSpace(sqlFile))
                throw new CliUsageException("必须且只能通过 --command 或 --file 提供一条 SQL。");
            sqlText ??= File.ReadAllText(sqlFile!);
        }

        return new ConnectOptions(profileName, useDefault, mode, sqlText);
    }

    private static BackupCommandOptions ParseBackupOptions(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            throw new CliUsageException(BuildBackupHelp());

        var action = args[1].ToLowerInvariant() switch
        {
            "create" => BackupAction.Create,
            "inspect" => BackupAction.Inspect,
            "verify" => BackupAction.Verify,
            "dry-run" or "restore-dry-run" => BackupAction.DryRun,
            "restore" => BackupAction.Restore,
            "rebuild-indexes" => BackupAction.RebuildIndexes,
            "--help" or "-h" => throw new CliUsageException(BuildBackupHelp()),
            _ => throw new CliUsageException($"未知 backup 动作 '{args[1]}'。"),
        };

        string? sourcePath = null;
        string? backupPath = null;
        string? targetPath = null;
        var overwrite = false;
        var includeFullTextIndexes = true;
        var verifyBeforeRestore = true;
        var dryRun = false;
        var rebuildIndexes = false;

        for (var i = 2; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--path" or "-p":
                    if (action is BackupAction.Create or BackupAction.RebuildIndexes)
                        sourcePath = ReadRequiredValue(args, ref i, "数据库目录");
                    else
                        backupPath = ReadRequiredValue(args, ref i, "备份目录");
                    break;
                case "--source":
                    sourcePath = ReadRequiredValue(args, ref i, "数据库目录"); break;
                case "--output" or "-o":
                    backupPath = ReadRequiredValue(args, ref i, "备份目录"); break;
                case "--backup":
                    backupPath = ReadRequiredValue(args, ref i, "备份目录"); break;
                case "--target" or "-t":
                    targetPath = ReadRequiredValue(args, ref i, "恢复目标目录"); break;
                case "--overwrite":
                    overwrite = true; break;
                case "--no-fulltext-indexes":
                    includeFullTextIndexes = false; break;
                case "--no-verify":
                    verifyBeforeRestore = false; break;
                case "--dry-run":
                    dryRun = true; break;
                case "--rebuild-indexes":
                    rebuildIndexes = true; break;
                case "--help" or "-h":
                    throw new CliUsageException(BuildBackupHelp());
                default:
                    throw new CliUsageException($"未知参数 '{args[i]}'。");
            }
        }

        if (dryRun)
        {
            if (action != BackupAction.Restore)
                throw new CliUsageException("--dry-run 只能用于 backup restore。");
            action = BackupAction.DryRun;
        }

        return ValidateBackupOptions(new BackupCommandOptions(
            action,
            sourcePath,
            backupPath,
            targetPath,
            overwrite,
            includeFullTextIndexes,
            verifyBeforeRestore,
            rebuildIndexes));
    }

    private static BackupCommandOptions ValidateBackupOptions(BackupCommandOptions options)
    {
        return options.Action switch
        {
            BackupAction.Create => string.IsNullOrWhiteSpace(options.SourcePath)
                ? throw new CliUsageException("backup create 必须通过 --path 指定数据库目录。")
                : string.IsNullOrWhiteSpace(options.BackupPath)
                    ? throw new CliUsageException("backup create 必须通过 --output 指定备份目录。")
                    : options,
            BackupAction.Inspect or BackupAction.Verify => string.IsNullOrWhiteSpace(options.BackupPath)
                ? throw new CliUsageException("backup inspect/verify 必须通过 --path 指定备份目录。")
                : options,
            BackupAction.DryRun or BackupAction.Restore => string.IsNullOrWhiteSpace(options.BackupPath)
                ? throw new CliUsageException("backup restore/dry-run 必须通过 --path 指定备份目录。")
                : string.IsNullOrWhiteSpace(options.TargetPath)
                    ? throw new CliUsageException("backup restore/dry-run 必须通过 --target 指定恢复目标目录。")
                    : options,
            BackupAction.RebuildIndexes => string.IsNullOrWhiteSpace(options.SourcePath)
                ? throw new CliUsageException("backup rebuild-indexes 必须通过 --path 指定数据库目录。")
                    : options,
            _ => throw new InvalidOperationException("未知的备份命令动作。"),
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void EnsureActionNotSpecified(bool alreadySet, string actionName)
    {
        if (alreadySet)
            throw new CliUsageException($"只能指定一个动作，不能重复使用 '{actionName}'。");
    }

    private static int ParsePositiveInt(string value, string optionName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new CliUsageException($"参数 {optionName} 必须是正整数。");
        return parsed;
    }

    private static string ReadRequiredValue(IReadOnlyList<string> args, ref int index, string description)
    {
        if (index + 1 >= args.Count)
            throw new CliUsageException($"参数 {args[index]} 缺少{description}。");
        index++;
        return args[index];
    }

    private int FailParse(string message)
    {
        _error.WriteLine(message);
        _error.WriteLine("使用 `sndb help` 查看帮助。");
        return ExitCodes.InvalidArguments;
    }

    // ── help / version ────────────────────────────────────────────────────────

    private void WriteHelp()
    {
        _output.WriteLine(
            """
SonnetDB CLI __VERSION__

用法:
  sndb version
  sndb sql     --connection "<conn>" (--command "<sql>" | --file ./q.sql)
  sndb repl    --connection "<conn>"
  sndb local   --path ./data [--save-profile home] [--default] [--command "<sql>" | --file ./q.sql | --repl]
  sndb local   --profile home [--command "<sql>" | --file ./q.sql | --repl]
  sndb local   --use-default [--command "<sql>" | --file ./q.sql | --repl]
  sndb local   list
  sndb local   remove --profile home
  sndb remote  --url http://127.0.0.1:5080 --database db [--token t] [--timeout 30] [--save-profile dev] [--default] [--command "<sql>" | --file ./q.sql | --repl]
  sndb remote  --profile dev [--command "<sql>" | --file ./q.sql | --repl]
  sndb remote  --use-default [--command "<sql>" | --file ./q.sql | --repl]
  sndb remote  list
  sndb remote  remove --profile dev
  sndb connect <profile-name> [--command "<sql>" | --file ./q.sql | --repl]
  sndb connect --default [--command "<sql>" | --file ./q.sql | --repl]
  sndb backup  create --path ./data --output ./backup [--overwrite] [--no-fulltext-indexes]
  sndb backup  inspect --path ./backup
  sndb backup  verify --path ./backup
  sndb backup  dry-run --path ./backup --target ./restored [--overwrite] [--no-verify]
  sndb backup  restore --path ./backup --target ./restored [--overwrite] [--no-verify] [--rebuild-indexes]
  sndb backup  rebuild-indexes --path ./restored
  sndb copilot ingest [--root ./docs]... [--endpoint http://host] [--token t] [--force] [--dry-run]

示例:
  sndb local  --path ./demo-data --save-profile home --default
  sndb local  --profile home --command "SELECT count(*) FROM cpu"
  sndb local  list
  sndb remote --url http://127.0.0.1:5080 --database metrics --token t --save-profile dev --default
  sndb remote list
  sndb connect home
  sndb connect dev --repl
  sndb connect --default --command "SELECT count(*) FROM cpu"
  sndb backup create --path ./demo-data --output ./demo-backup
  sndb backup verify --path ./demo-backup
  sndb backup dry-run --path ./demo-backup --target ./demo-restored
  sndb backup restore --path ./demo-backup --target ./demo-restored --rebuild-indexes
""".Replace("__VERSION__", GetVersion(), StringComparison.Ordinal));
    }

    private void WriteVersion()
        => _output.WriteLine($"SonnetDB CLI {GetVersion()}");

    private static string GetVersion()
    {
        return typeof(CliApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CliApplication).Assembly.GetName().Version?.ToString()
            ?? "0.0.0-dev";
    }

    private static bool IsHelp(string arg)
        => arg is "help" or "--help" or "-h";

    private static bool IsVersion(string arg)
        => arg is "version" or "--version" or "-v";

    private static string BuildSqlHelp()
        => "用法: sndb sql --connection \"<conn>\" (--command \"<sql>\" | --file ./q.sql)";

    private static string BuildReplHelp()
        => "用法: sndb repl --connection \"<conn>\"";

    private static string BuildLocalHelp()
        => "用法: sndb local (--path ./data | --profile name | --use-default) [--save-profile name] [--default] [--command \"<sql>\" | --file ./q.sql | --repl] | list | remove --profile name";

    private static string BuildRemoteHelp()
        => "用法: sndb remote (--url http://host --database db | --profile name | --use-default) [...] [--command \"<sql>\" | --file ./q.sql | --repl] | list | remove --profile name";

    private static string BuildConnectHelp()
        => "用法: sndb connect <profile-name> [--command \"<sql>\" | --file ./q.sql | --repl]  或  sndb connect --default [...]";

    private static string BuildBackupHelp()
        => "用法: sndb backup create --path ./data --output ./backup [--overwrite] [--no-fulltext-indexes] | inspect --path ./backup | verify --path ./backup | dry-run --path ./backup --target ./restored [--overwrite] [--no-verify] | restore --path ./backup --target ./restored [--overwrite] [--no-verify] [--rebuild-indexes] | rebuild-indexes --path ./restored";
}

// ── Option records ────────────────────────────────────────────────────────────

internal readonly record struct SqlCommandOptions(string ConnectionString, string SqlText);

internal readonly record struct ReplCommandOptions(string ConnectionString);

internal readonly record struct LocalCommandOptions(
    string? RootPath,
    string? ProfileName,
    string? SaveProfileName,
    ExecMode Mode,
    string? SqlText,
    LocalAction Action,
    bool SetDefault,
    bool UseDefaultProfile);

internal readonly record struct RemoteCommandOptions(
    string? BaseUrl,
    string? Database,
    string? Token,
    int? Timeout,
    ExecMode Mode,
    string? SqlText,
    RemoteAction Action,
    string? ProfileName,
    string? SaveProfileName,
    bool SetDefault,
    bool UseDefaultProfile);

internal readonly record struct ConnectOptions(
    string? ProfileName,
    bool UseDefault,
    ExecMode Mode,
    string? SqlText);

internal readonly record struct BackupCommandOptions(
    BackupAction Action,
    string? SourcePath,
    string? BackupPath,
    string? TargetPath,
    bool Overwrite,
    bool IncludeFullTextIndexes,
    bool VerifyBeforeRestore,
    bool RebuildIndexes);

// ── Enums ─────────────────────────────────────────────────────────────────────

internal enum ExecMode { Info, Sql, Repl }
internal enum LocalAction { Use, List, Remove }
internal enum RemoteAction { Use, List, Remove }
internal enum BackupAction { Create, Inspect, Verify, DryRun, Restore, RebuildIndexes }

// ── Exceptions / exit codes ───────────────────────────────────────────────────

internal sealed class CliUsageException(string message) : Exception(message);

internal static class ExitCodes
{
    public const int Success = 0;
    public const int ExecutionFailed = 1;
    public const int InvalidArguments = 2;
}
