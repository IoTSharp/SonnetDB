using System.Text.Json;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Diagnostics;
using SonnetDB.Hosting;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    /// <summary>读取实际生效的参数；实例配置限管理员，数据库参数受原有读权限约束。</summary>
    private static void MapConfigurationEndpoints(this WebApplication app, ServerOptions options)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        app.MapGet("/v1/system/configuration", (HttpContext context) =>
        {
            if (!DatabaseAccessEvaluator.IsServerAdmin(context))
                return ForbiddenResult("仅实例管理员可读取整个实例的配置。");

            var json = JsonSerializer.SerializeToElement(options, ConfigurationJsonContext.Default.ServerOptions);
            var entries = ConfigurationSnapshotBuilder.Flatten(json, "SonnetDBServer", true, "部署配置修改后重启").ToList();
            var ai = app.Services.GetRequiredService<AiConfigStore>().Get();
            entries.AddRange(ConfigurationSnapshotBuilder.Flatten(
                JsonSerializer.SerializeToElement(ai, ConfigurationJsonContext.Default.AiOptions),
                "AI", true, "通过 AI 设置管理"));
            var response = new ConfigurationSnapshotResponse("instance",
                "本进程经绑定与默认值校验后的 ServerOptions，以及持久化 AI 配置；不包含未生效的文件改动。",
                DateTimeOffset.UtcNow, entries);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(response, ServerJsonContext.Default.ConfigurationSnapshotResponse);
        });

        app.MapGet("/v1/db/{db}/configuration", (HttpContext context, string db) =>
        {
            if (!TsdbRegistry.IsValidName(db)) return BadRequestResult("非法数据库名。");
            var permission = DatabaseAccessEvaluator.GetEffectivePermission(context, grants, db);
            if (!DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Read))
                return ForbiddenResult("当前凭据无权读取该数据库参数。");
            if (!registry.TryGet(db, out var database))
                return Results.NotFound();

            var response = new ConfigurationSnapshotResponse(db,
                "当前数据库打开时采用的引擎参数；含 WAL、刷盘、压缩、保留策略、KV、SQL 内存和 schema 策略。",
                DateTimeOffset.UtcNow,
                ConfigurationSnapshotBuilder.FlattenEngine(database.RuntimeOptions, DatabaseAccessEvaluator.IsServerAdmin(context), "需重新打开数据库或重启实例"));
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(response, ServerJsonContext.Default.ConfigurationSnapshotResponse);
        });
    }
}
