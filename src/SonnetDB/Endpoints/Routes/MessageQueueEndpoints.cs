using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.Mcp;
using SonnetMQ;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapMqEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        app.MapPost("/v1/db/{db}/mq/{topic}/publish", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Write).ConfigureAwait(false))
                return;

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.MqPublishRequest).ConfigureAwait(false);
            if (req is null || req.Payload is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 payload。").ConfigureAwait(false);
                return;
            }

            try
            {
                var mq = app.Services.GetRequiredService<SonnetMqStore>();
                long offset = mq.Publish(QualifyMqTopic(db, topic), req.Payload, new SonnetMqPublishOptions(req.Headers));
                var response = new MqPublishResponse(topic, offset);
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, response, ServerJsonContext.Default.MqPublishResponse, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "mq_io_error", ex.Message).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "mq_error", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/mq/{topic}/publish-batch", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Write).ConfigureAwait(false))
                return;

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.MqPublishBatchRequest).ConfigureAwait(false);
            if (req is null || req.Messages is null || req.Messages.Count == 0)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含非空 messages。").ConfigureAwait(false);
                return;
            }

            var entries = new SonnetMqPublishEntry[req.Messages.Count];
            for (int i = 0; i < req.Messages.Count; i++)
            {
                var message = req.Messages[i];
                if (message?.Payload is null)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "批量消息每条都需包含 payload。").ConfigureAwait(false);
                    return;
                }

                entries[i] = new SonnetMqPublishEntry(message.Payload, message.Headers);
            }

            try
            {
                var mq = app.Services.GetRequiredService<SonnetMqStore>();
                var offsets = mq.PublishMany(QualifyMqTopic(db, topic), entries);
                var response = new MqPublishBatchResponse(topic, offsets);
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, response, ServerJsonContext.Default.MqPublishBatchResponse, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "mq_io_error", ex.Message).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "mq_error", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/mq/{topic}/pull", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Read).ConfigureAwait(false))
                return;

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.MqPullRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.ConsumerGroup))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 consumerGroup。").ConfigureAwait(false);
                return;
            }

            int maxCount = req.MaxCount is null or <= 0 ? 100 : Math.Min(req.MaxCount.Value, 1000);
            try
            {
                var mq = app.Services.GetRequiredService<SonnetMqStore>();
                var messages = mq.Pull(QualifyMqTopic(db, topic), req.ConsumerGroup, maxCount)
                    .Select(message => new MqMessageResponse(
                        topic,
                        message.Offset,
                        message.TimestampUtc,
                        message.Headers,
                        message.Payload))
                    .ToArray();
                await Results.Json(new MqPullResponse(messages), ServerJsonContext.Default.MqPullResponse).ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/mq/{topic}/ack", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Write).ConfigureAwait(false))
                return;

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.MqAckRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.ConsumerGroup))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 consumerGroup。").ConfigureAwait(false);
                return;
            }

            try
            {
                var mq = app.Services.GetRequiredService<SonnetMqStore>();
                long nextOffset = mq.Ack(QualifyMqTopic(db, topic), req.ConsumerGroup, req.Offset);
                var response = new MqAckResponse(topic, req.ConsumerGroup, nextOffset);
                await Results.Json(response, ServerJsonContext.Default.MqAckResponse).ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/mq/{topic}/stats", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Read).ConfigureAwait(false))
                return;

            var mq = app.Services.GetRequiredService<SonnetMqStore>();
            var stats = mq.GetStats(QualifyMqTopic(db, topic));
            var response = new MqStatsResponse(topic, stats.MessageCount, stats.NextOffset, stats.ConsumerOffsets);
            await Results.Json(response, ServerJsonContext.Default.MqStatsResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

    }
}
