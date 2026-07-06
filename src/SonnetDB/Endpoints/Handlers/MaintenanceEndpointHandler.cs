using SonnetDB.Backup;
using SonnetDB.Contracts;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.Storage.Format;

namespace SonnetDB.Endpoints;

/// <summary>
/// 提供数据库维护操作的响应构造逻辑。
/// </summary>
internal static class MaintenanceEndpointHandler
{
    /// <summary>
    /// 执行数据库维护操作。
    /// </summary>
    public static IResult Handle(Tsdb tsdb, MaintenanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(request);

        var operation = NormalizeOperation(request.Operation);
        return operation switch
        {
            "health_check" => Json(HealthCheck(tsdb)),
            "backup_verify" => Json(BackupVerify(request)),
            "restore_dry_run" => Json(RestoreDryRun(request)),
            "rebuild_index" => Json(RebuildIndex(tsdb, request)),
            "quality_analysis" or "quality" => Json(QualityAnalysis(tsdb)),
            _ => Results.Json(
                Failed(operation, $"未知维护操作 '{request.Operation}'。"),
                ServerJsonContext.Default.MaintenanceResponse,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }

    /// <summary>
    /// 规范化维护操作名称。
    /// </summary>
    public static string NormalizeOperation(string? operation)
        => string.IsNullOrWhiteSpace(operation)
            ? string.Empty
            : operation.Trim().Replace('-', '_').ToLowerInvariant();

    private static IResult Json(MaintenanceResponse response)
        => Results.Json(
            response,
            ServerJsonContext.Default.MaintenanceResponse,
            statusCode: response.Success ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);

    private static MaintenanceResponse HealthCheck(Tsdb tsdb)
    {
        var root = tsdb.RootDirectory;
        var checks = new List<MaintenanceCheckInfo>
        {
            new("root_directory", Directory.Exists(root) ? "ok" : "error", root),
            new("measurements", "ok", "measurement schema catalog", tsdb.Measurements.Snapshot().Count),
            new("tables", "ok", "relation table catalog", tsdb.Tables.Catalog.Snapshot().Count),
            new("document_collections", "ok", "document collection catalog", tsdb.Documents.Catalog.Snapshot().Count),
            new("memtable_points", "ok", "unflushed in-memory points", tsdb.MemTable.PointCount),
            new("wal_files", "ok", "WAL files on disk", CountFiles(TsdbPaths.WalDir(root), "*.SDBWAL")),
        };

        var segmentFiles = tsdb.ListSegments();
        var loadedSegments = tsdb.Segments.SegmentCount;
        checks.Add(new MaintenanceCheckInfo(
            "segments",
            loadedSegments == segmentFiles.Count ? "ok" : "warning",
            loadedSegments == segmentFiles.Count
                ? "all segment files are loaded"
                : "some segment files were skipped during open",
            loadedSegments));

        var indexes = CountIndexes(tsdb);
        checks.Add(new MaintenanceCheckInfo("indexes", "ok", "declared lifecycle indexes", indexes));

        if (tsdb.LastError is { } lastError)
        {
            checks.Add(new MaintenanceCheckInfo(
                "last_error",
                "warning",
                lastError.Message));
        }

        var success = checks.All(static c => c.Status is "ok");
        return new MaintenanceResponse(
            "health_check",
            success ? "ok" : "warning",
            success,
            success ? "数据库健康检查通过。" : "数据库健康检查存在警告。",
            DateTimeOffset.UtcNow,
            checks);
    }

    private static MaintenanceResponse BackupVerify(MaintenanceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BackupDirectory))
            return Failed("backup_verify", "backupDirectory 不能为空。");

        var service = new BackupService();
        var result = service.Verify(request.BackupDirectory);
        var verification = new BackupVerificationInfo(
            result.IsValid,
            result.CheckedFiles,
            result.Errors.ToList());
        return new MaintenanceResponse(
            "backup_verify",
            result.IsValid ? "ok" : "failed",
            result.IsValid,
            result.IsValid ? "备份校验通过。" : "备份校验失败。",
            DateTimeOffset.UtcNow,
            [new("backup_files", result.IsValid ? "ok" : "error", "checked files", result.CheckedFiles)],
            BackupVerification: verification);
    }

    private static MaintenanceResponse RestoreDryRun(MaintenanceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BackupDirectory))
            return Failed("restore_dry_run", "backupDirectory 不能为空。");
        if (string.IsNullOrWhiteSpace(request.RestoreTargetDirectory))
            return Failed("restore_dry_run", "restoreTargetDirectory 不能为空。");

        var service = new BackupService();
        var dryRun = service.RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = request.BackupDirectory,
            TargetDirectory = request.RestoreTargetDirectory,
            Overwrite = request.Overwrite,
        });

        if (!dryRun.Verification.IsValid)
        {
            return new MaintenanceResponse(
                "restore_dry_run",
                "failed",
                Success: false,
                "备份校验失败，不能执行恢复 dry-run。",
                DateTimeOffset.UtcNow,
                [new("backup_verify", "error", "backup verification failed", dryRun.Verification.CheckedFiles)],
                BackupVerification: new BackupVerificationInfo(false, dryRun.Verification.CheckedFiles, dryRun.Verification.Errors.ToList()));
        }

        var restoreDryRun = new RestoreDryRunInfo(
            dryRun.IsValid,
            dryRun.FileCount,
            dryRun.TotalBytes,
            dryRun.IndexCount,
            dryRun.TargetDirectoryExists,
            dryRun.TargetDirectoryEmpty);

        return new MaintenanceResponse(
            "restore_dry_run",
            dryRun.IsValid ? "ok" : "failed",
            dryRun.IsValid,
            dryRun.IsValid
                ? "恢复 dry-run 通过，未复制任何文件。"
                : "恢复目标目录不满足离线恢复策略。",
            DateTimeOffset.UtcNow,
            [
                new("backup_verify", "ok", "backup verification passed", dryRun.Verification.CheckedFiles),
                new("target_directory", dryRun.IsValid ? "ok" : "error", Path.GetFullPath(request.RestoreTargetDirectory))
            ],
            BackupVerification: new BackupVerificationInfo(true, dryRun.Verification.CheckedFiles, []),
            RestoreDryRun: restoreDryRun);
    }

    private static MaintenanceResponse RebuildIndex(Tsdb tsdb, MaintenanceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TargetModel)
            || string.IsNullOrWhiteSpace(request.TargetOwner)
            || string.IsNullOrWhiteSpace(request.TargetName))
        {
            return Failed("rebuild_index", "targetModel、targetOwner 与 targetName 不能为空。");
        }

        var model = request.TargetModel.Trim().Replace('-', '_').ToLowerInvariant();
        var owner = request.TargetOwner.Trim();
        var name = request.TargetName.Trim();

        try
        {
            if (model == "table")
            {
                var index = tsdb.Tables.RebuildIndex(owner, name);
                return RebuildIndexResponse(
                    "table",
                    owner,
                    name,
                    IndexKind(index),
                    mode: "sync",
                    planned: false,
                    rebuildable: true);
            }

            if (model is "document_fulltext" or "fulltext")
            {
                int documentCount = tsdb.Documents.RebuildFullTextIndex(owner, name);
                return RebuildIndexResponse(
                    "document",
                    owner,
                    name,
                    "fulltext",
                    "sync_touch",
                    planned: false,
                    rebuildable: true,
                    documentCount);
            }

            if (model is "document" or "document_json" or "json_path")
            {
                var schema = tsdb.Documents.Catalog.TryGet(owner)
                    ?? throw new InvalidOperationException($"document collection '{owner}' 不存在。");
                var jsonIndex = schema.TryGetIndex(name);
                var fullTextIndex = schema.TryGetFullTextIndex(name);
                if (model is "document" && jsonIndex is not null && fullTextIndex is not null)
                    return Failed("rebuild_index", $"document collection '{owner}' 中索引 '{name}' 同时命中 JSON path 与全文索引，请指定 targetModel。");

                if (model is not "document_fulltext" && jsonIndex is not null)
                {
                    _ = tsdb.Documents.RebuildIndex(owner, name);
                    return RebuildIndexResponse("document", owner, name, DocumentIndexKind(jsonIndex), "sync", planned: false, rebuildable: true);
                }

                if (fullTextIndex is not null)
                {
                    int documentCount = tsdb.Documents.RebuildFullTextIndex(owner, name);
                    return RebuildIndexResponse(
                        "document",
                        owner,
                        name,
                        "fulltext",
                        "sync_touch",
                        planned: false,
                        rebuildable: true,
                        documentCount);
                }

                throw new InvalidOperationException($"document collection '{owner}' 中索引 '{name}' 不存在。");
            }

            if (model is "measurement" or "vector")
            {
                var schema = tsdb.Measurements.TryGet(owner)
                    ?? throw new InvalidOperationException($"measurement '{owner}' 不存在。");
                var column = schema.TryGetColumn(name)
                    ?? throw new InvalidOperationException($"measurement '{owner}' 中列 '{name}' 不存在。");
                if (column.DataType != FieldType.Vector || column.VectorIndex is null)
                    throw new InvalidOperationException($"measurement '{owner}' 的列 '{name}' 不是已声明索引的 VECTOR 列。");

                return RebuildIndexResponse(
                    "measurement",
                    owner,
                    name,
                    "vector:" + column.VectorIndex.Kind,
                    mode: "planned",
                    planned: true,
                    rebuildable: true,
                    message: "向量索引按 Segment 生命周期在 flush / compaction / restore 后重建；当前服务端不提供 durable rebuild job。");
            }

            return Failed("rebuild_index", $"不支持 targetModel '{request.TargetModel}'。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Failed("rebuild_index", ex.Message);
        }
    }

    private static MaintenanceResponse QualityAnalysis(Tsdb tsdb)
    {
        var indexes = new List<QualityIndexInfo>();
        var issues = new List<MaintenanceCheckInfo>();

        foreach (var table in tsdb.Tables.Catalog.Snapshot())
        {
            foreach (var index in table.Indexes)
            {
                var kind = string.IsNullOrWhiteSpace(index.JsonPath)
                    ? (index.IsUnique ? "unique_secondary" : "secondary")
                    : "json_path";
                indexes.Add(new QualityIndexInfo(
                    $"table:{table.Name}:{index.Name}",
                    "table",
                    table.Name,
                    index.Name,
                    kind,
                    "ok",
                    IncludedInBackup: false,
                    Rebuildable: true,
                    Detail: string.IsNullOrWhiteSpace(index.JsonPath)
                        ? string.Join(",", index.Columns)
                        : $"{index.Columns[0]}:{index.JsonPath}"));
            }
        }

        foreach (var collection in tsdb.Documents.Catalog.Snapshot())
        {
            var store = tsdb.Documents.Open(collection.Name);
            long documentCount = store.Count();
            foreach (var index in collection.Indexes)
            {
                indexes.Add(new QualityIndexInfo(
                    $"document:{collection.Name}:{index.Name}",
                    "document",
                    collection.Name,
                    index.Name,
                    DocumentIndexKind(index),
                    "ok",
                    IncludedInBackup: false,
                    Rebuildable: true,
                    DocumentCount: documentCount,
                    Detail: FormatDocumentIndexDetail(index)));
            }

            foreach (var index in collection.FullTextIndexes)
            {
                var state = documentCount == 0 ? "warning" : "ok";
                indexes.Add(new QualityIndexInfo(
                    $"document:{collection.Name}:{index.Name}",
                    "document",
                    collection.Name,
                    index.Name,
                    "fulltext",
                    state,
                    IncludedInBackup: false,
                    Rebuildable: true,
                    DocumentCount: documentCount,
                    Detail: $"{index.Tokenizer}:{string.Join(",", index.Fields)}"));
                if (documentCount == 0)
                {
                    issues.Add(new MaintenanceCheckInfo(
                        $"document:{collection.Name}:{index.Name}",
                        "warning",
                        "全文索引所在 document collection 为空，检索质量无法评估。",
                        0));
                }
            }
        }

        foreach (var measurement in tsdb.Measurements.Snapshot())
        {
            foreach (var column in measurement.Columns)
            {
                if (column.DataType != FieldType.Vector || column.VectorIndex is null)
                    continue;

                indexes.Add(new QualityIndexInfo(
                    $"measurement:{measurement.Name}:{column.Name}",
                    "measurement",
                    measurement.Name,
                    column.Name,
                    "vector:" + column.VectorIndex.Kind,
                    "planned",
                    IncludedInBackup: false,
                    Rebuildable: true,
                    Detail: "向量索引随 Segment flush / compaction / restore 生命周期重建。"));
                issues.Add(new MaintenanceCheckInfo(
                    $"measurement:{measurement.Name}:{column.Name}",
                    "info",
                    "measurement vector index 当前通过 Segment 生命周期维护，管理端仅展示 planned rebuild 状态。"));
            }
        }

        if (indexes.Count == 0)
        {
            issues.Add(new MaintenanceCheckInfo(
                "indexes",
                "warning",
                "当前数据库没有声明任何二级、全文、JSON path 或向量索引。",
                0));
        }

        var analysis = new QualityAnalysisInfo(
            indexes.Count,
            indexes.Count(static index => index.Rebuildable),
            indexes.Count(static index => string.Equals(index.State, "planned", StringComparison.OrdinalIgnoreCase)),
            indexes.Count(static index => index.IncludedInBackup),
            issues.Count(static issue => issue.Status is "warning" or "error"),
            indexes,
            issues);

        return new MaintenanceResponse(
            "quality_analysis",
            issues.Any(static issue => issue.Status == "error") ? "failed" : issues.Count == 0 ? "ok" : "warning",
            Success: !issues.Any(static issue => issue.Status == "error"),
            issues.Count == 0 ? "索引质量分析通过。" : "索引质量分析完成，存在提示或警告。",
            DateTimeOffset.UtcNow,
            issues.Count == 0
                ? [new("indexes", "ok", "declared lifecycle indexes", indexes.Count)]
                : issues,
            QualityAnalysis: analysis);
    }

    private static MaintenanceResponse RebuildIndexResponse(
        string model,
        string owner,
        string name,
        string kind,
        string mode,
        bool planned,
        bool rebuildable,
        long? documentCount = null,
        string? message = null)
        => new(
            "rebuild_index",
            planned ? "planned" : "ok",
            Success: true,
            message ?? "索引维护操作完成。",
            DateTimeOffset.UtcNow,
            [new("index", planned ? "planned" : "ok", $"{model}/{owner}/{name}")],
            Index: new IndexMaintenanceInfo(model, owner, name, kind, mode, planned, rebuildable, documentCount));

    private static string IndexKind(SonnetDB.Tables.TableIndex index)
        => string.IsNullOrWhiteSpace(index.JsonPath)
            ? index.IsUnique ? "unique_secondary" : "secondary"
            : "json_path";

    private static string DocumentIndexKind(DocumentPathIndex index)
    {
        if (index.IsTtl)
            return "ttl";
        if (index.IsUnique)
            return "unique_document";
        if (index.PartialFilter is not null)
            return "partial_document";
        if (index.IsSparse)
            return "sparse_document";
        return index.Paths.Count > 1 ? "compound_document" : "document";
    }

    private static string FormatDocumentIndexDetail(DocumentPathIndex index)
    {
        var parts = new List<string>
        {
            "paths=" + string.Join(",", index.Paths),
        };
        if (index.IsUnique)
            parts.Add("unique=true");
        if (index.IsSparse)
            parts.Add("sparse=true");
        if (index.PartialFilter is not null)
            parts.Add($"partial={index.PartialFilter.Path}:{index.PartialFilter.Operator}:{index.PartialFilter.ValueScalar}");
        if (index.IsTtl)
            parts.Add("ttl_seconds=" + index.TtlSeconds);
        return string.Join(";", parts);
    }

    private static MaintenanceResponse Failed(string operation, string message)
        => new(
            string.IsNullOrWhiteSpace(operation) ? "unknown" : operation,
            "failed",
            Success: false,
            message,
            DateTimeOffset.UtcNow,
            [new("request", "error", message)]);

    private static int CountIndexes(Tsdb tsdb)
    {
        int count = 0;
        foreach (var table in tsdb.Tables.Catalog.Snapshot())
            count += table.Indexes.Count;
        foreach (var collection in tsdb.Documents.Catalog.Snapshot())
            count += collection.Indexes.Count + collection.FullTextIndexes.Count;
        foreach (var measurement in tsdb.Measurements.Snapshot())
            count += measurement.Columns.Count(static c => c.DataType == FieldType.Vector && c.VectorIndex is not null);
        return count;
    }

    private static int CountFiles(string directory, string searchPattern)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly).Count()
            : 0;
}
