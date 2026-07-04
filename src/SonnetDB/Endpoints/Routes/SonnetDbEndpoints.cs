using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.AspNetCore;
using SonnetDB.Configuration;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    public static void MapSonnetDbEndpoints(this WebApplication app, ServerOptions serverOptions)
    {
        app.MapWebEndpoints(serverOptions);
        app.MapHealthEndpoints(serverOptions);
        app.MapSetupEndpoints();
        app.MapDatabaseEndpoints();
        app.MapSqlEndpoints();
        app.MapDocumentEndpoints();
        app.MapKvEndpoints();
        app.MapMqEndpoints();
        app.MapFrameEndpoints();
        app.MapObjectStorageEndpoints();
        app.MapManagementContractEndpoints();
        app.MapIngestionEndpoints();
        app.MapControlPlaneEndpoints();
        app.MapCopilotEndpoints(serverOptions);
        app.MapMcp("/mcp/{db}");
    }
}
