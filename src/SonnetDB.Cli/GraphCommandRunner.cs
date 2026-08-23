using System.Globalization;
using System.Text.Json;
using SonnetDB.Data.Graphs;
using SonnetDB.Graphs;

namespace SonnetDB.Cli;

/// <summary>处理 Graph 运维、导入导出与可视化命令。</summary>
internal sealed class GraphCommandRunner
{
    private readonly TextWriter _output;

    internal GraphCommandRunner(TextWriter output, TextWriter error)
    {
        _output = output;
        _ = error;
    }

    internal int Run(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            throw new CliUsageException(BuildHelp());
        return args[1].ToLowerInvariant() switch
        {
            "list" => RunList(ParseCommon(args, 2)),
            "overview" => RunOverview(ParseCommon(args, 2, requireGraph: true)),
            "visualize" => RunVisualize(ParseCommon(args, 2, requireGraph: true, allowLimit: true)),
            "export" => RunExport(ParseCommon(args, 2, requireGraph: true, allowOutput: true, allowMaxElements: true)),
            "import" => RunImport(ParseCommon(args, 2, requireGraph: true, allowInput: true, allowBatchSize: true, allowRequestId: true)),
            "maintenance" => RunMaintenance(args),
            "help" or "--help" or "-h" => Help(),
            _ => throw new CliUsageException($"未知 graph 子命令 '{args[1]}'。\n{BuildHelp()}"),
        };
    }

    private int RunList(GraphCommandOptions options)
    {
        using var client = new SndbGraphClient(options.ConnectionString);
        IReadOnlyList<GraphInfoDto> graphs = client.ListGraphsAsync().GetAwaiter().GetResult();
        _output.WriteLine(JsonSerializer.Serialize(graphs.ToArray(), CliJsonContext.Default.GraphInfoDtoArray));
        return ExitCodes.Success;
    }

    private int RunOverview(GraphCommandOptions options)
    {
        using var client = new SndbGraphClient(options.ConnectionString);
        GraphOperationsOverviewDto result = client
            .GetOperationsOverviewAsync(options.Graph!)
            .GetAwaiter()
            .GetResult();
        _output.WriteLine(JsonSerializer.Serialize(result, CliJsonContext.Default.GraphOperationsOverviewDto));
        return ExitCodes.Success;
    }

    private int RunVisualize(GraphCommandOptions options)
    {
        using var client = new SndbGraphClient(options.ConnectionString);
        GraphVisualizationDto result = client
            .GetVisualizationAsync(options.Graph!, options.Limit)
            .GetAwaiter()
            .GetResult();
        _output.WriteLine(JsonSerializer.Serialize(result, CliJsonContext.Default.GraphVisualizationDto));
        return ExitCodes.Success;
    }

    private int RunExport(GraphCommandOptions options)
    {
        string fullPath = Path.GetFullPath(options.OutputPath!);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using var client = new SndbGraphClient(options.ConnectionString);
        using var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        client.ExportJsonAsync(options.Graph!, output, options.MaxElements).GetAwaiter().GetResult();
        _output.WriteLine($"Graph JSON 已写入 {fullPath}");
        return ExitCodes.Success;
    }

    private int RunImport(GraphCommandOptions options)
    {
        string fullPath = Path.GetFullPath(options.InputPath!);
        using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var client = new SndbGraphClient(options.ConnectionString);
        SndbGraphImportReport report = SndbGraphImporter.ImportJsonAsync(
                client,
                options.Graph!,
                source,
                new SndbGraphImportOptions
                {
                    RequestId = options.RequestId,
                    BatchSize = options.BatchSize,
                })
            .GetAwaiter()
            .GetResult();
        _output.WriteLine(FormattableString.Invariant(
            $"Graph import: vertices={report.VertexCount}, edges={report.EdgeCount}, batches={report.BatchCount}, sequence={report.LastSequence}"));
        return ExitCodes.Success;
    }

    private int RunMaintenance(IReadOnlyList<string> args)
    {
        if (args.Count < 3)
            throw new CliUsageException(BuildHelp());
        string action = args[2].ToLowerInvariant();
        if (action is "help" or "--help" or "-h")
            return Help();
        GraphCommandOptions options = ParseCommon(
            args,
            3,
            requireGraph: true,
            allowAction: action == "stage",
            allowApproval: action is "approve" or "reject",
            allowReason: action == "reject",
            allowMaxWorkUnits: action == "stage",
            allowCompact: action == "stage",
            allowLimit: action == "audit");
        using var client = new SndbGraphClient(options.ConnectionString);
        GraphMaintenanceApprovalDto result;
        switch (action)
        {
            case "stage":
                result = client.StageMaintenanceAsync(
                        options.Graph!,
                        new GraphMaintenanceStageRequest
                        {
                            Action = ParseMaintenanceAction(options.Action),
                            MaxWorkUnits = options.MaxWorkUnits,
                            CompactOnCompletion = options.CompactOnCompletion,
                        })
                    .GetAwaiter()
                    .GetResult();
                break;
            case "approve":
                result = client.ApproveMaintenanceAsync(options.Graph!, options.ApprovalId)
                    .GetAwaiter()
                    .GetResult();
                break;
            case "reject":
                result = client.RejectMaintenanceAsync(options.Graph!, options.ApprovalId, options.Reason)
                    .GetAwaiter()
                    .GetResult();
                break;
            case "audit":
                IReadOnlyList<GraphMaintenanceApprovalDto> audit = client
                    .ListMaintenanceAuditAsync(options.Graph!, options.Limit)
                    .GetAwaiter()
                    .GetResult();
                _output.WriteLine(JsonSerializer.Serialize(
                    new GraphMaintenanceAuditListDto(audit),
                    CliJsonContext.Default.GraphMaintenanceAuditListDto));
                return ExitCodes.Success;
            default:
                throw new CliUsageException($"未知 graph maintenance 子命令 '{action}'。\n{BuildHelp()}");
        }

        _output.WriteLine(JsonSerializer.Serialize(result, CliJsonContext.Default.GraphMaintenanceApprovalDto));
        return ExitCodes.Success;
    }

    private static GraphCommandOptions ParseCommon(
        IReadOnlyList<string> args,
        int start,
        bool requireGraph = false,
        bool allowLimit = false,
        bool allowOutput = false,
        bool allowInput = false,
        bool allowBatchSize = false,
        bool allowRequestId = false,
        bool allowMaxElements = false,
        bool allowAction = false,
        bool allowApproval = false,
        bool allowReason = false,
        bool allowMaxWorkUnits = false,
        bool allowCompact = false)
    {
        string? connection = null;
        string? graph = null;
        string? output = null;
        string? input = null;
        string? maintenanceAction = null;
        string? reason = null;
        int limit = 250;
        int maxElements = 100_000;
        int batchSize = 1_000;
        int maxWorkUnits = 64;
        Guid requestId = Guid.NewGuid();
        Guid approvalId = Guid.Empty;
        bool compact = false;
        for (int index = start; index < args.Count; index++)
        {
            string flag = args[index];
            switch (flag)
            {
                case "--connection" or "-c":
                    connection = RequireValue(args, ref index, flag);
                    break;
                case "--graph" or "-g":
                    graph = RequireValue(args, ref index, flag);
                    break;
                case "--limit" when allowLimit:
                    limit = ParsePositiveInt(RequireValue(args, ref index, flag), flag);
                    break;
                case "--output" or "-o" when allowOutput:
                    output = RequireValue(args, ref index, flag);
                    break;
                case "--input" or "-i" when allowInput:
                    input = RequireValue(args, ref index, flag);
                    break;
                case "--batch-size" when allowBatchSize:
                    batchSize = ParsePositiveInt(RequireValue(args, ref index, flag), flag);
                    break;
                case "--request-id" when allowRequestId:
                    if (!Guid.TryParse(RequireValue(args, ref index, flag), out requestId) || requestId == Guid.Empty)
                        throw new CliUsageException("--request-id 必须是非空 GUID。");
                    break;
                case "--max-elements" when allowMaxElements:
                    maxElements = ParsePositiveInt(RequireValue(args, ref index, flag), flag);
                    break;
                case "--action" when allowAction:
                    maintenanceAction = RequireValue(args, ref index, flag);
                    break;
                case "--approval" when allowApproval:
                    if (!Guid.TryParse(RequireValue(args, ref index, flag), out approvalId) || approvalId == Guid.Empty)
                        throw new CliUsageException("--approval 必须是非空 GUID。");
                    break;
                case "--reason" when allowReason:
                    reason = RequireValue(args, ref index, flag);
                    break;
                case "--max-work-units" when allowMaxWorkUnits:
                    maxWorkUnits = ParsePositiveInt(RequireValue(args, ref index, flag), flag);
                    break;
                case "--compact-on-completion" when allowCompact:
                    compact = true;
                    break;
                default:
                    throw new CliUsageException($"未知 graph 参数 '{flag}'。\n{BuildHelp()}");
            }
        }

        if (string.IsNullOrWhiteSpace(connection))
            throw new CliUsageException("graph 命令必须通过 --connection 指定连接字符串。");
        if (requireGraph && string.IsNullOrWhiteSpace(graph))
            throw new CliUsageException("graph 命令必须通过 --graph 指定图名称。");
        if (allowOutput && string.IsNullOrWhiteSpace(output))
            throw new CliUsageException("graph export 必须通过 --output 指定目标文件。");
        if (allowInput && string.IsNullOrWhiteSpace(input))
            throw new CliUsageException("graph import 必须通过 --input 指定输入文件。");
        if (allowAction && string.IsNullOrWhiteSpace(maintenanceAction))
            throw new CliUsageException("graph maintenance stage 必须通过 --action 指定动作。");
        if (allowApproval && approvalId == Guid.Empty)
            throw new CliUsageException("graph maintenance 决策必须通过 --approval 指定审批 ID。");
        if (limit > 2_000 || maxElements > 1_000_000 || batchSize > 10_000 || maxWorkUnits > 4_096)
            throw new CliUsageException("graph 命令参数超过公开预算上限。");

        return new GraphCommandOptions(
            connection,
            graph,
            output,
            input,
            limit,
            maxElements,
            batchSize,
            requestId,
            maintenanceAction,
            approvalId,
            reason,
            maxWorkUnits,
            compact);
    }

    private int Help()
    {
        _output.WriteLine(BuildHelp());
        return ExitCodes.Success;
    }

    private static GraphMaintenanceAction ParseMaintenanceAction(string? value)
        => value?.ToLowerInvariant() switch
        {
            "repair" or "rebuild" or "repair-rebuild" => GraphMaintenanceAction.RepairRebuild,
            "checkpoint" => GraphMaintenanceAction.Checkpoint,
            "compact" => GraphMaintenanceAction.Compact,
            _ => throw new CliUsageException("--action 必须是 repair、checkpoint 或 compact。"),
        };

    private static int ParsePositiveInt(string value, string flag)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : throw new CliUsageException($"{flag} 必须是正整数。");

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (index + 1 >= args.Count)
            throw new CliUsageException($"{flag} 缺少参数值。");
        return args[++index];
    }

    private static string BuildHelp()
        => """
sndb graph - 原生属性图运维

用法:
  sndb graph list --connection "<conn>"
  sndb graph overview --connection "<conn>" --graph <name>
  sndb graph visualize --connection "<conn>" --graph <name> [--limit 250]
  sndb graph export --connection "<conn>" --graph <name> --output <file> [--max-elements 100000]
  sndb graph import --connection "<conn>" --graph <name> --input <file> [--batch-size 1000] [--request-id <guid>]
  sndb graph maintenance stage --connection "<conn>" --graph <name> --action repair|checkpoint|compact [--max-work-units 64] [--compact-on-completion]
  sndb graph maintenance approve --connection "<conn>" --graph <name> --approval <guid>
  sndb graph maintenance reject --connection "<conn>" --graph <name> --approval <guid> [--reason <text>]
  sndb graph maintenance audit --connection "<conn>" --graph <name> [--limit 200]
""";
}

internal readonly record struct GraphCommandOptions(
    string ConnectionString,
    string? Graph,
    string? OutputPath,
    string? InputPath,
    int Limit,
    int MaxElements,
    int BatchSize,
    Guid RequestId,
    string? Action,
    Guid ApprovalId,
    string? Reason,
    int MaxWorkUnits,
    bool CompactOnCompletion);
