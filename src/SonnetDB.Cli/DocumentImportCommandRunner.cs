using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Data;
using SonnetDB.Data.Documents;

namespace SonnetDB.Cli;

/// <summary>
/// 执行 <c>sndb document import</c>，提供可重试的 MongoDB/JSON 文档迁移闭环。
/// </summary>
internal sealed class DocumentImportCommandRunner(
    TextWriter output,
    TextWriter error,
    CliProfileStore profileStore)
{
    private const int MaxEstimatedBatchBytes = 12 * 1024 * 1024;
    private const int MaxReportedErrors = 1000;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>解析并执行 document 子命令。</summary>
    internal int Run(IReadOnlyList<string> args)
        => RunAsync(Parse(args), CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    private async Task<int> RunAsync(DocumentImportOptions options, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var gaps = new DocumentImportGapCollector();
        var errors = new DocumentImportErrorCollector(MaxReportedErrors);
        var scalarPathCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int sampledDocuments = 0;
        var source = DocumentImportSourceReader.ResolveSource(options.InputPath, options.Collection, options.Format);
        string sourceHash = DocumentImportSourceReader.ComputeSourceSha256(source.DataPath, source.MetadataPath);
        var target = ResolveTarget(options);
        string checkpointPath = options.CheckpointPath
            ?? (options.Resume ? source.DataPath + ".sndb-import.checkpoint.json" : string.Empty);
        DocumentImportCheckpoint? checkpoint = options.Resume
            ? LoadCheckpoint(checkpointPath, sourceHash, target.Display, options)
            : null;
        if (checkpoint is not null)
            errors.Restore(checkpoint.Errors, checkpoint.ErrorCount);
        long resumeAt = checkpoint?.NextDocumentIndex ?? 0;
        int batchIndex = checkpoint?.NextBatchIndex ?? 0;
        long sourcePosition = 0;
        long documentsRead = 0;
        long documentsValidated = 0;
        long documentsWritten = 0;
        long inserted = 0;
        long matched = 0;
        long modified = 0;
        int batchesAttempted = 0;
        int batchesCommitted = 0;
        int batchesReplayed = 0;
        bool stop = false;
        using var client = options.DryRun ? null : new SndbDocumentClient(target.ConnectionString!);
        if (client is not null && options.CreateCollection)
            await client.CreateCollectionAsync(options.Collection, ifNotExists: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        var batch = new List<DocumentImportSourceItem>(options.BatchSize);
        int estimatedBatchBytes = 0;
        await foreach (var read in DocumentImportSourceReader.ReadAsync(
            source.DataPath,
            source.Format,
            options.IdPath,
            gaps,
            cancellationToken).ConfigureAwait(false))
        {
            sourcePosition++;
            if (sourcePosition <= resumeAt)
                continue;
            documentsRead++;
            if (read.Error is not null)
            {
                errors.Add(read.Error);
                if (options.Ordered)
                {
                    stop = true;
                    break;
                }
                continue;
            }

            DocumentImportSourceItem item = read.Item!;
            int estimatedBytes = Encoding.UTF8.GetByteCount(item.Id) + Encoding.UTF8.GetByteCount(item.Json) + 256;
            if (estimatedBytes > MaxEstimatedBatchBytes)
            {
                errors.Add(new DocumentImportItemError(
                    item.File,
                    item.SourceOrdinal,
                    item.Id,
                    "document_too_large_for_bulk",
                    "单文档估算大小超过 12 MiB migration batch 安全预算。"));
                if (options.Ordered)
                {
                    stop = true;
                    break;
                }
                continue;
            }

            if (batch.Count > 0
                && (batch.Count >= options.BatchSize || estimatedBatchBytes + estimatedBytes > MaxEstimatedBatchBytes))
            {
                stop = !await FlushAsync(sourcePosition - 1).ConfigureAwait(false);
                if (stop)
                    break;
            }

            batch.Add(item);
            estimatedBatchBytes += estimatedBytes;
            documentsValidated++;
            if (sampledDocuments < 1000)
            {
                sampledDocuments++;
                foreach (string path in item.ScalarPaths.Distinct(StringComparer.Ordinal))
                    scalarPathCounts[path] = scalarPathCounts.GetValueOrDefault(path) + 1;
            }
        }

        if (!stop && batch.Count > 0)
            await FlushAsync(sourcePosition).ConfigureAwait(false);

        if (options.DryRun)
            gaps.Add("dry_run_target_constraints_not_checked", "partial", "dry-run 保证零写入，但不连接目标，因此不验证目标 validator、unique index 或权限。");
        var suggestions = DocumentImportIndexAdvisor.Build(source.MetadataPath, scalarPathCounts, sampledDocuments, gaps);
        var report = new DocumentImportReport(
            SchemaVersion: 1,
            Operation: "document_import",
            Source: source.DataPath,
            SourceFormat: source.Format,
            SourceSha256: sourceHash,
            Collection: options.Collection,
            Target: target.Display,
            DryRun: options.DryRun,
            Mode: options.Mode,
            Ordered: options.Ordered,
            BatchSize: options.BatchSize,
            TransactionBoundary: "one_collection_batch",
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTimeOffset.UtcNow,
            DocumentsRead: documentsRead,
            DocumentsValidated: documentsValidated,
            DocumentsWritten: documentsWritten,
            Inserted: inserted,
            Matched: matched,
            Modified: modified,
            BatchesAttempted: batchesAttempted,
            BatchesCommitted: batchesCommitted,
            BatchesReplayed: batchesReplayed,
            Resumed: checkpoint is not null,
            Success: errors.TotalCount == 0,
            ErrorCount: errors.TotalCount,
            ErrorsTruncated: errors.IsTruncated,
            Errors: errors.Samples,
            Gaps: gaps.ToList(),
            IndexSuggestions: suggestions);
        WriteReport(report, options);
        return report.Success ? ExitCodes.Success : ExitCodes.ExecutionFailed;

        async Task<bool> FlushAsync(long nextDocumentIndex)
        {
            if (batch.Count == 0)
                return true;
            if (options.DryRun)
            {
                batch.Clear();
                estimatedBatchBytes = 0;
                return true;
            }

            SndbDocumentBulkWriteOperation[] operations = batch.Select(item => options.Mode == "replace"
                ? SndbDocumentBulkWrites.ReplaceOne(item.Id, item.Json, upsert: true)
                : SndbDocumentBulkWrites.InsertOne(item.Id, item.Json)).ToArray();
            string requestId = BuildRequestId(sourceHash, target.Display, options.Collection, options.Mode, options.Ordered, batchIndex, operations);
            batchesAttempted++;
            SndbDocumentBulkWriteResult result;
            try
            {
                result = await client!.BulkWriteAsync(
                    options.Collection,
                    operations,
                    options.Ordered,
                    requestId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(new DocumentImportItemError(
                    batch[0].File,
                    batch[0].SourceOrdinal,
                    batch[0].Id,
                    "bulk_transport_failed",
                    ex.Message));
                return false;
            }

            if (result.Committed)
                batchesCommitted++;
            if (result.Replayed)
                batchesReplayed++;
            inserted += result.Inserted;
            matched += result.Matched;
            modified += result.Modified;
            documentsWritten += result.Items.Count(static item => item.Status is "succeeded" or "no_op");
            foreach (var item in result.Items.Where(static item => item.Error is not null))
            {
                int index = Math.Clamp(item.Index, 0, batch.Count - 1);
                DocumentImportSourceItem sourceItem = batch[index];
                errors.Add(new DocumentImportItemError(
                    sourceItem.File,
                    sourceItem.SourceOrdinal,
                    sourceItem.Id,
                    item.Error!.Code,
                    item.Error.Message));
            }
            if (result.Errors is not null)
            {
                foreach (var batchError in result.Errors.Where(static item => item.Index < 0))
                {
                    errors.Add(new DocumentImportItemError(
                        batch[0].File,
                        batch[0].SourceOrdinal,
                        batch[0].Id,
                        batchError.Code,
                        batchError.Message));
                }
            }

            bool canContinue = result.Committed && (!options.Ordered || !result.HasErrors);
            if (!string.IsNullOrEmpty(checkpointPath) && result.Committed)
            {
                SaveCheckpoint(checkpointPath, new DocumentImportCheckpoint(
                    1,
                    sourceHash,
                    target.Display,
                    options.Collection,
                    options.Mode,
                    options.Ordered,
                    options.BatchSize,
                    nextDocumentIndex,
                    batchIndex + 1,
                    DateTimeOffset.UtcNow,
                    errors.Samples.ToArray(),
                    errors.TotalCount));
            }

            batchIndex++;
            batch.Clear();
            estimatedBatchBytes = 0;
            return canContinue;
        }
    }

    private (string? ConnectionString, string Display) ResolveTarget(DocumentImportOptions options)
    {
        int sources = (options.ConnectionString is null ? 0 : 1)
            + (options.LocalPath is null ? 0 : 1)
            + (options.ProfileName is null ? 0 : 1)
            + (options.UseDefaultProfile ? 1 : 0)
            + (options.BaseUrl is null ? 0 : 1);
        if (sources == 0)
        {
            if (options.DryRun)
                return (null, "dry-run:no-target");
            throw new CliUsageException("非 dry-run 必须通过 --connection、--path、--profile、--use-default 或 --url/--database 指定目标。");
        }
        if (sources != 1)
            throw new CliUsageException("目标连接参数只能选择一种来源。");

        if (options.ConnectionString is not null)
        {
            var builder = new SndbConnectionStringBuilder(options.ConnectionString);
            return (builder.ConnectionString, $"{builder.ResolveMode().ToString().ToLowerInvariant()}:{builder.DataSource}");
        }
        if (options.LocalPath is not null)
        {
            string path = Path.GetFullPath(options.LocalPath);
            return (new SndbConnectionStringBuilder
            {
                Mode = SndbProviderMode.Embedded,
                DataSource = path,
            }.ConnectionString, "embedded:" + path);
        }

        CliLocalProfile? local;
        CliRemoteProfile? remote;
        if (options.ProfileName is not null)
            (local, remote) = profileStore.GetByName(options.ProfileName);
        else if (options.UseDefaultProfile)
            (local, remote) = profileStore.GetDefault();
        else
            (local, remote) = (null, null);
        if (local is not null && remote is not null)
            throw new CliUsageException("同名 local/remote profile 产生歧义，请改用 --connection。");
        if (local is not null)
        {
            string path = Path.GetFullPath(local.Path);
            return (new SndbConnectionStringBuilder
            {
                Mode = SndbProviderMode.Embedded,
                DataSource = path,
            }.ConnectionString, "embedded:" + path);
        }

        string? baseUrl = remote?.BaseUrl ?? options.BaseUrl;
        string? database = remote?.Database ?? options.Database;
        string? token = remote?.Token ?? options.Token;
        int timeout = remote?.Timeout ?? options.TimeoutSeconds;
        if (baseUrl is null || database is null)
            throw new CliUsageException("远程目标必须提供 --url 和 --database，或有效 remote profile。");
        string dataSource = $"{baseUrl.TrimEnd('/')}/{database}";
        var remoteBuilder = new SndbConnectionStringBuilder
        {
            Mode = SndbProviderMode.Remote,
            DataSource = dataSource,
            Timeout = timeout,
        };
        if (!string.IsNullOrWhiteSpace(token))
            remoteBuilder.Token = token;
        return (remoteBuilder.ConnectionString, "remote:" + dataSource);
    }

    private void WriteReport(DocumentImportReport report, DocumentImportOptions options)
    {
        string json = JsonSerializer.Serialize(report, CliJsonContext.Default.DocumentImportReport);
        if (options.ReportPath is not null)
        {
            string path = Path.GetFullPath(options.ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json + Environment.NewLine, Utf8WithoutBom);
        }
        if (options.JsonOutput)
        {
            output.WriteLine(json);
            return;
        }

        output.WriteLine($"Document import {(report.DryRun ? "dry-run" : "completed")}: collection={report.Collection}, read={report.DocumentsRead}, validated={report.DocumentsValidated}, written={report.DocumentsWritten}");
        output.WriteLine($"batches={report.BatchesCommitted}/{report.BatchesAttempted}, replayed={report.BatchesReplayed}, errors={report.ErrorCount}, gaps={report.Gaps.Count}, index-suggestions={report.IndexSuggestions.Count}");
        if (options.ReportPath is not null)
            output.WriteLine($"report={Path.GetFullPath(options.ReportPath)}");
        foreach (var item in report.Errors.Take(20))
            error.WriteLine($"{item.File}:{item.SourceOrdinal}: {item.Code}: {item.Message}");
    }

    private static DocumentImportCheckpoint LoadCheckpoint(
        string path,
        string sourceHash,
        string target,
        DocumentImportOptions options)
    {
        if (!File.Exists(path))
            throw new CliUsageException($"resume checkpoint 不存在: {path}");
        using var stream = File.OpenRead(path);
        var checkpoint = JsonSerializer.Deserialize(stream, CliJsonContext.Default.DocumentImportCheckpoint)
            ?? throw new CliUsageException("resume checkpoint 内容为空。");
        if (checkpoint.SchemaVersion != 1
            || checkpoint.SourceSha256 != sourceHash
            || checkpoint.Target != target
            || checkpoint.Collection != options.Collection
            || checkpoint.Mode != options.Mode
            || checkpoint.Ordered != options.Ordered
            || checkpoint.BatchSize != options.BatchSize)
        {
            throw new CliUsageException("resume checkpoint 与 source、target、collection 或 batch 选项不匹配。");
        }
        return checkpoint;
    }

    private static void SaveCheckpoint(string path, DocumentImportCheckpoint checkpoint)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp";
        string json = JsonSerializer.Serialize(checkpoint, CliJsonContext.Default.DocumentImportCheckpoint);
        File.WriteAllText(temporary, json + Environment.NewLine, Utf8WithoutBom);
        File.Move(temporary, fullPath, overwrite: true);
    }

    private static string BuildRequestId(
        string sourceHash,
        string target,
        string collection,
        string mode,
        bool ordered,
        int batchIndex,
        IReadOnlyList<SndbDocumentBulkWriteOperation> operations)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(sourceHash);
        Append(target);
        Append(collection);
        Append(mode);
        Append(ordered ? "1" : "0");
        Append(batchIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var operation in operations)
        {
            Append(operation.Type.ToString());
            Append(operation.Id ?? string.Empty);
            Append(operation.Json ?? string.Empty);
        }
        return "document-import-v1-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
    }

    private static DocumentImportOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !string.Equals(args[1], "import", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException(HelpText);
        string? input = null;
        string? collection = null;
        string format = "auto";
        string mode = "insert";
        bool ordered = false;
        int batchSize = 500;
        string idPath = "_id";
        bool dryRun = false;
        bool create = true;
        bool resume = false;
        bool json = false;
        string? report = null;
        string? checkpoint = null;
        string? connection = null;
        string? localPath = null;
        string? profile = null;
        bool useDefault = false;
        string? url = null;
        string? database = null;
        string? token = null;
        int timeout = 100;

        for (int i = 2; i < args.Count; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--input" or "-i": input = ReadValue(args, ref i); break;
                case "--collection" or "-c": collection = ReadValue(args, ref i); break;
                case "--format": format = ReadValue(args, ref i); break;
                case "--mode": mode = ReadValue(args, ref i).ToLowerInvariant(); break;
                case "--ordered": ordered = true; break;
                case "--unordered": ordered = false; break;
                case "--batch-size": batchSize = ParsePositive(ReadValue(args, ref i), argument); break;
                case "--id-path": idPath = ReadValue(args, ref i); break;
                case "--dry-run": dryRun = true; break;
                case "--no-create": create = false; break;
                case "--resume": resume = true; break;
                case "--json": json = true; break;
                case "--report": report = ReadValue(args, ref i); break;
                case "--checkpoint": checkpoint = ReadValue(args, ref i); break;
                case "--connection": connection = ReadValue(args, ref i); break;
                case "--path" or "-p": localPath = ReadValue(args, ref i); break;
                case "--profile": profile = ReadValue(args, ref i); break;
                case "--use-default": useDefault = true; break;
                case "--url" or "-u": url = ReadValue(args, ref i); break;
                case "--database" or "-d": database = ReadValue(args, ref i); break;
                case "--token" or "-t": token = ReadValue(args, ref i); break;
                case "--timeout": timeout = ParsePositive(ReadValue(args, ref i), argument); break;
                case "--help" or "-h": throw new CliUsageException(HelpText);
                default: throw new CliUsageException($"未知 document import 参数 '{argument}'。");
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(collection))
            throw new CliUsageException("document import 必须提供 --input 和 --collection。\n" + HelpText);
        if (mode is not ("insert" or "replace"))
            throw new CliUsageException("--mode 只支持 insert 或 replace。");
        if (batchSize > 1000)
            throw new CliUsageException("--batch-size 不能超过 1000。");
        if (dryRun && resume)
            throw new CliUsageException("--dry-run 与 --resume 不能同时使用。");
        return new DocumentImportOptions(
            input,
            collection,
            format,
            mode,
            ordered,
            batchSize,
            idPath,
            dryRun,
            create,
            resume,
            json,
            report,
            checkpoint,
            connection,
            localPath,
            profile,
            useDefault,
            url,
            database,
            token,
            timeout);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index)
    {
        if (++index >= args.Count)
            throw new CliUsageException($"参数 {args[index - 1]} 缺少值。");
        return args[index];
    }

    private static int ParsePositive(string value, string name)
        => int.TryParse(value, out int result) && result > 0
            ? result
            : throw new CliUsageException($"{name} 必须是正整数。");

    internal const string HelpText =
        "用法: sndb document import --input <file|mongodump-dir> --collection <name> "
        + "[--format auto|ndjson|json|json-array|bson] [--mode insert|replace] "
        + "[--ordered|--unordered] [--batch-size 500] [--id-path _id] [--dry-run] "
        + "[--no-create] [--report report.json] [--json] [--checkpoint state.json [--resume]] "
        + "(--connection <conn>|--path <data>|--profile <name>|--use-default|"
        + "--url <host> --database <db> [--token <token>] [--timeout 100])";
}
