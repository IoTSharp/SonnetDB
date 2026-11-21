using Microsoft.AspNetCore.Http.Features;
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
                BackupTaskDefinition saved = store.Upsert(task);
                return Results.Json(
                    new BackupTaskResponse(saved, store.GetState(saved.Id)),
                    ServerJsonContext.Default.BackupTaskResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
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
            task.DestinationRoot = NormalizeTaskRoot(task.DestinationRoot, options.Backup.DestinationRoot!);
            BackupTaskDefinition saved = store.Upsert(task);
            return Results.Json(
                new BackupTaskResponse(saved, store.GetState(saved.Id)),
                ServerJsonContext.Default.BackupTaskResponse);
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
            try
            {
                BackupTaskState state = await scheduler.RunNowAsync(id, ctx.RequestAborted).ConfigureAwait(false);
                BackupTaskDefinition task = store.TryGetTask(id)!;
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
            BackupRestoreRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.BackupRestoreRequest).ConfigureAwait(false);
            if (request is null)
                return ErrorResult("bad_request", "恢复请求不能为空。", StatusCodes.Status400BadRequest);
            try
            {
                BackupRestoreResponse response = await restore.RestoreAsync(request.Path, request.Url, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Json(response, ServerJsonContext.Default.BackupRestoreResponse, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
            {
                return ErrorResult("restore_failed", exception.Message, StatusCodes.Status422UnprocessableEntity);
            }
        });

        app.MapPost("/v1/system/backup-restore/upload", async (HttpContext ctx) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可上传恢复数据库。", StatusCodes.Status403Forbidden);
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = options.Backup.MaxTransferBytes;
            if (ctx.Request.ContentLength is > 0 && ctx.Request.ContentLength > options.Backup.MaxTransferBytes + 1024 * 1024)
                return ErrorResult("payload_too_large", "上传备份包超过配置的最大传输大小。", StatusCodes.Status413PayloadTooLarge);

            string uploadPath = Path.Combine(Path.GetTempPath(), "sonnetdb-upload-" + Guid.NewGuid().ToString("N") + ".package");
            using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            uploadTimeout.CancelAfter(TimeSpan.FromSeconds(options.Backup.TransferTimeoutSeconds));
            CancellationToken uploadToken = uploadTimeout.Token;
            try
            {
                IFormCollection form = await ctx.Request.ReadFormAsync(uploadToken).ConfigureAwait(false);
                IFormFile? file = form.Files.GetFile("file");
                if (file is null)
                    return ErrorResult("bad_request", "multipart 请求缺少 file。", StatusCodes.Status400BadRequest);
                await using (Stream source = file.OpenReadStream())
                await using (var target = new FileStream(uploadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    await CopyUploadAsync(source, target, options.Backup.MaxTransferBytes, uploadToken).ConfigureAwait(false);
                }

                BackupRestoreResponse response = await restore.RestoreAsync(uploadPath, null, uploadToken).ConfigureAwait(false);
                return Results.Json(response, ServerJsonContext.Default.BackupRestoreResponse, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
            {
                return ErrorResult("restore_failed", exception.Message, StatusCodes.Status422UnprocessableEntity);
            }
            finally
            {
                try
                {
                    if (File.Exists(uploadPath))
                        File.Delete(uploadPath);
                }
                catch (IOException)
                {
                }
            }
        });

        app.MapPut("/v1/system/databases/{db}/name", async (HttpContext ctx, string db) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                return ErrorResult("forbidden", "仅 server admin 可重命名数据库。", StatusCodes.Status403Forbidden);
            RenameDatabaseRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.RenameDatabaseRequest).ConfigureAwait(false);
            if (request is null || !TsdbRegistry.IsValidName(request.Name))
                return ErrorResult("bad_request", "新数据库名称不符合 [a-zA-Z0-9_-] 规则。", StatusCodes.Status400BadRequest);
            if (!registry.TryRename(db, request.Name))
                return ErrorResult("conflict", "数据库不存在、已挂载、名称冲突或名称未发生变化。", StatusCodes.Status409Conflict);
            grants.RenameDatabaseGrants(db, request.Name);
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

    /// <summary>有界复制 multipart 上传内容。</summary>
    private static async Task CopyUploadAsync(Stream source, Stream target, long maxBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > maxBytes)
                throw new InvalidDataException("上传备份包超过配置的最大传输大小。");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>使用源生成 JSON 上下文返回统一错误。</summary>
    private static IResult ErrorResult(string code, string message, int statusCode)
        => Results.Json(
            new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse,
            statusCode: statusCode);
}
