using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    /// <summary>映射备份任务、上传恢复和 HTTP 恢复管理入口。</summary>
    private static void MapBackupEndpoints(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<BackupTaskStore>();
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var scheduler = app.Services.GetRequiredService<BackupSchedulerService>();
        var restore = app.Services.GetRequiredService<BackupRestoreService>();
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value;

        app.MapGet("/v1/system/backup-tasks", (HttpContext ctx) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可管理备份任务。", StatusCodes.Status403Forbidden);
            return Results.Json(
                new BackupTaskListResponse(store.ListTasks()
                    .Select(task => new BackupTaskResponse(task, store.GetState(task.Id)))
                    .ToList()),
                ServerJsonContext.Default.BackupTaskListResponse);
        });

        app.MapPost("/v1/system/backup-tasks", async (HttpContext ctx) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可管理备份任务。", StatusCodes.Status403Forbidden);
            BackupTaskDefinition? task = await ReadJsonAsync(ctx, ServerJsonContext.Default.BackupTaskDefinition).ConfigureAwait(false);
            if (task is null)
                return ErrorResult("bad_request", "任务定义不能为空。", StatusCodes.Status400BadRequest);
            try
            {
                task.DestinationRoot = NormalizeTaskRoot(task.DestinationRoot, options.Backup.DestinationRoot!);
                _ = scheduler.ValidateTaskDestination(task);
                BackupTaskDefinition saved = store.Upsert(task);
                return Results.Json(
                    new BackupTaskResponse(saved, store.GetState(saved.Id)),
                    ServerJsonContext.Default.BackupTaskResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
            {
                return ErrorResult("bad_request", exception.Message, StatusCodes.Status400BadRequest);
            }
        });

        app.MapPut("/v1/system/backup-tasks/{id}", async (HttpContext ctx, string id) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可管理备份任务。", StatusCodes.Status403Forbidden);
            BackupTaskDefinition? task = await ReadJsonAsync(ctx, ServerJsonContext.Default.BackupTaskDefinition).ConfigureAwait(false);
            if (task is null || !string.Equals(task.Id, id, StringComparison.Ordinal))
                return ErrorResult("bad_request", "任务 Id 与路径不一致。", StatusCodes.Status400BadRequest);
            try
            {
                task.DestinationRoot = NormalizeTaskRoot(task.DestinationRoot, options.Backup.DestinationRoot!);
                _ = scheduler.ValidateTaskDestination(task);
                BackupTaskDefinition saved = store.Upsert(task);
                return Results.Json(
                    new BackupTaskResponse(saved, store.GetState(saved.Id)),
                    ServerJsonContext.Default.BackupTaskResponse);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
            {
                return ErrorResult("bad_request", exception.Message, StatusCodes.Status400BadRequest);
            }
        });

        app.MapDelete("/v1/system/backup-tasks/{id}", (HttpContext ctx, string id) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可管理备份任务。", StatusCodes.Status403Forbidden);
            return store.Remove(id)
                ? Results.NoContent()
                : ErrorResult("not_found", "备份任务不存在。", StatusCodes.Status404NotFound);
        });

        app.MapPost("/v1/system/backup-tasks/{id}/run", async (HttpContext ctx, string id) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可运行备份任务。", StatusCodes.Status403Forbidden);
            if (!options.Backup.Enabled)
                return ErrorResult("backup_disabled", "运行时备份未启用。", StatusCodes.Status403Forbidden);
            try
            {
                BackupTaskState state = await scheduler.RunNowAsync(id, ctx.RequestAborted).ConfigureAwait(false);
                BackupTaskDefinition? task = store.TryGetTask(id);
                if (task is null)
                    return ErrorResult("not_found", "备份任务已删除。", StatusCodes.Status404NotFound);
                return Results.Json(
                    new BackupTaskResponse(task, state),
                    ServerJsonContext.Default.BackupTaskResponse);
            }
            catch (KeyNotFoundException exception)
            {
                return ErrorResult("not_found", exception.Message, StatusCodes.Status404NotFound);
            }
        });

        app.MapPost("/v1/system/backup-restore", async (HttpContext ctx) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可恢复数据库。", StatusCodes.Status403Forbidden);
            if (!options.Backup.EnableRestore)
                return ErrorResult("restore_disabled", "在线恢复未启用。", StatusCodes.Status403Forbidden);
            BackupRestoreRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.BackupRestoreRequest).ConfigureAwait(false);
            if (request is null)
                return ErrorResult("bad_request", "恢复请求不能为空。", StatusCodes.Status400BadRequest);
            try
            {
                BackupRestoreResponse response = await restore.RestoreAsync(request.Path, request.Url, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Json(response, ServerJsonContext.Default.BackupRestoreResponse, statusCode: StatusCodes.Status201Created);
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return ErrorResult("restore_timeout", "在线恢复准备超过时限。", StatusCodes.Status408RequestTimeout);
            }
            catch (Exception exception) when (IsRestoreInputFailure(exception))
            {
                return ErrorResult("restore_failed", exception.Message, StatusCodes.Status422UnprocessableEntity);
            }
        });

        app.MapPost("/v1/system/backup-restore/upload", async (HttpContext ctx) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可上传恢复数据库。", StatusCodes.Status403Forbidden);
            if (!options.Backup.EnableRestore)
                return ErrorResult("restore_disabled", "在线恢复未启用。", StatusCodes.Status403Forbidden);
            const int multipartOverhead = 1024 * 1024;
            long requestLimit = options.Backup.MaxTransferBytes + multipartOverhead;
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                bodySize.MaxRequestBodySize = requestLimit;
            if (ctx.Request.ContentLength > requestLimit)
                return ErrorResult("payload_too_large", "上传备份包超过配置的最大传输大小。", StatusCodes.Status413PayloadTooLarge);

            if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out MediaTypeHeaderValue? contentType)
                || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorResult("bad_request", "上传必须使用 multipart/form-data。", StatusCodes.Status400BadRequest);
            }

            string? boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
            if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
                return ErrorResult("bad_request", "multipart boundary 无效。", StatusCodes.Status400BadRequest);

            using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            uploadTimeout.CancelAfter(TimeSpan.FromSeconds(options.Backup.TransferTimeoutSeconds));
            try
            {
                // 直接读取单个文件节，避免将整个表单预缓冲到另一份临时文件。
                var reader = new MultipartReader(boundary, ctx.Request.Body)
                {
                    BodyLengthLimit = options.Backup.MaxTransferBytes,
                };
                MultipartSection? section = await reader.ReadNextSectionAsync(uploadTimeout.Token).ConfigureAwait(false);
                if (section is null
                    || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out ContentDispositionHeaderValue? disposition)
                    || !disposition.IsFileDisposition()
                    || !HeaderUtilities.RemoveQuotes(disposition.Name).Equals("file", StringComparison.Ordinal))
                {
                    return ErrorResult("bad_request", "multipart 请求的首节必须是 file 文件。", StatusCodes.Status400BadRequest);
                }

                BackupRestoreResponse response = await restore.RestoreUploadAsync(
                    section.Body, token => RequireFinalUploadSectionAsync(reader, token), uploadTimeout.Token).ConfigureAwait(false);
                return Results.Json(response, ServerJsonContext.Default.BackupRestoreResponse, statusCode: StatusCodes.Status201Created);
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return ErrorResult("restore_timeout", "在线恢复准备超过时限。", StatusCodes.Status408RequestTimeout);
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return ErrorResult("payload_too_large", "上传备份包超过配置的最大传输大小。", StatusCodes.Status413PayloadTooLarge);
            }
            catch (Exception exception) when (IsRestoreInputFailure(exception))
            {
                return ErrorResult("restore_failed", exception.Message, StatusCodes.Status422UnprocessableEntity);
            }
        });

        app.MapPut("/v1/system/databases/{db}/name", async (HttpContext ctx, string db) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可重命名数据库。", StatusCodes.Status403Forbidden);
            if (!options.Backup.EnableRestore || !db.StartsWith("restored-", StringComparison.Ordinal))
                return ErrorResult("rename_disabled", "此入口仅处理在线恢复生成的新库。", StatusCodes.Status403Forbidden);
            RenameDatabaseRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.RenameDatabaseRequest).ConfigureAwait(false);
            if (request is null || !TsdbRegistry.IsValidName(request.Name))
                return ErrorResult("bad_request", "新数据库名称不符合 [a-zA-Z0-9_-] 规则。", StatusCodes.Status400BadRequest);
            // 防止变更名称时留下失效的任务引用或把已有授权重新绑定到不同的数据。
            if (store.ListTasks().Any(task => task.DatabaseNames.Contains(db, StringComparer.OrdinalIgnoreCase))
                || grants.ListAll().Any(grant => string.Equals(grant.Database, db, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(grant.Database, request.Name, StringComparison.OrdinalIgnoreCase)))
                return ErrorResult("conflict", "请先解除相关备份任务和数据库授权。", StatusCodes.Status409Conflict);
            if (!registry.TryRename(db, request.Name))
                return ErrorResult("conflict", "数据库不存在、已挂载、名称冲突或名称未发生变化。", StatusCodes.Status409Conflict);
            return Results.Json(new DatabaseOperationResponse(request.Name, "renamed"), ServerJsonContext.Default.DatabaseOperationResponse);
        });
    }

    /// <summary>把任务目标限制到全局备份根目录。</summary>
    private static string NormalizeTaskRoot(string? requested, string globalRoot)
    {
        string root = string.IsNullOrWhiteSpace(requested) ? globalRoot : Path.GetFullPath(requested);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(globalRoot));
        string fullPath = Path.TrimEndingDirectorySeparator(root);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(fullPath, fullRoot, comparison)
            && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("任务目标目录必须位于服务器 Backup.DestinationRoot 下。");
        return fullPath;
    }

    /// <summary>在发布恢复结果前确认上传恰好包含一个文件节。</summary>
    private static async Task RequireFinalUploadSectionAsync(MultipartReader reader, CancellationToken cancellationToken)
    {
        if (await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidDataException("multipart 请求只能包含一个 file 文件。");
    }

    /// <summary>识别可作为恢复失败返回给管理员的输入或存储错误。</summary>
    private static bool IsRestoreInputFailure(Exception exception)
        => exception is ArgumentException or InvalidDataException or IOException or InvalidOperationException
            or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException;

    /// <summary>使用源生成 JSON 上下文返回统一错误。</summary>
    private static IResult ErrorResult(string code, string message, int statusCode)
        => Results.Json(
            new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse,
            statusCode: statusCode);
}
