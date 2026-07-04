using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Hosting;
using SonnetMQ;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapFrameEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        app.MapPost("/v1/frame", (HttpContext ctx) =>
        {
            var mq = app.Services.GetRequiredService<SonnetMqStore>();
            return FrameEndpointHandler.HandleAsync(ctx, registry, grants, mq);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
    }
}
