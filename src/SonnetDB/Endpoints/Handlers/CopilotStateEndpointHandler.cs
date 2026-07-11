using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// 映射 Copilot 服务端会话与用量摘要端点。
/// </summary>
internal static class CopilotStateEndpointHandler
{
    /// <summary>
    /// 注册当前认证主体隔离的 Copilot 状态 API。
    /// </summary>
    public static void Map(WebApplication app, CopilotStateStore store)
    {
        app.MapMethods("/v1/copilot/conversations", ["GET"], (RequestDelegate)(async context =>
        {
            var owner = CopilotStateStore.ResolveOwner(context);
            await WriteJsonAsync(
                context,
                new CopilotConversationListResponse(store.ListConversations(owner)),
                ServerJsonContext.Default.CopilotConversationListResponse).ConfigureAwait(false);
        }));

        app.MapMethods("/v1/copilot/conversations", ["POST"], (RequestDelegate)(async context =>
        {
            var request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                ServerJsonContext.Default.CopilotConversationUpsertRequest,
                context.RequestAborted).ConfigureAwait(false);
            if (request is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request", "请求体格式无效。").ConfigureAwait(false);
                return;
            }

            try
            {
                var owner = CopilotStateStore.ResolveOwner(context);
                var response = store.UpsertConversation(owner, request.Id, request.Title, request.Database);
                await WriteJsonAsync(context, response, ServerJsonContext.Default.CopilotConversationResponse).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "forbidden", exception.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/conversations/{id}/messages", ["GET"], (RequestDelegate)(async context =>
        {
            var id = context.Request.RouteValues["id"]?.ToString() ?? string.Empty;
            try
            {
                var owner = CopilotStateStore.ResolveOwner(context);
                var response = new CopilotMessageListResponse(store.ListMessages(owner, id));
                await WriteJsonAsync(context, response, ServerJsonContext.Default.CopilotMessageListResponse).ConfigureAwait(false);
            }
            catch (KeyNotFoundException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "conversation_not_found", exception.Message).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "forbidden", exception.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/conversations/{id}", ["DELETE"], (RequestDelegate)(async context =>
        {
            var id = context.Request.RouteValues["id"]?.ToString() ?? string.Empty;
            try
            {
                var owner = CopilotStateStore.ResolveOwner(context);
                if (!store.DeleteConversation(owner, id))
                {
                    await WriteErrorAsync(context, StatusCodes.Status404NotFound, "conversation_not_found", "Copilot 会话不存在。").ConfigureAwait(false);
                    return;
                }
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
            catch (UnauthorizedAccessException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "forbidden", exception.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/conversations", ["DELETE"], (RequestDelegate)(context =>
        {
            var owner = CopilotStateStore.ResolveOwner(context);
            store.ClearConversations(owner);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }));

        app.MapMethods("/v1/copilot/metrics", ["GET"], (RequestDelegate)(async context =>
        {
            var minutes = 60;
            if (context.Request.Query.TryGetValue("windowMinutes", out var raw) &&
                (!int.TryParse(raw.ToString(), out minutes) || minutes is < 1 or > 10_080))
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request", "windowMinutes 必须在 1 到 10080 之间。").ConfigureAwait(false);
                return;
            }

            var owner = CopilotStateStore.ResolveOwner(context);
            var response = store.GetMetrics(owner, TimeSpan.FromMinutes(minutes));
            await WriteJsonAsync(context, response, ServerJsonContext.Default.CopilotMetricsResponse).ConfigureAwait(false);
        }));
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, typeInfo, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse,
            context.RequestAborted).ConfigureAwait(false);
    }
}
