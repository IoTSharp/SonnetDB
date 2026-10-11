using System.Reflection;
using System.Text.Json;
using SonnetDB.Contracts;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

internal static class WorkbenchPreviewPackage
{
    internal static WorkbenchPreviewIdentity? Load(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "preview-1.json");
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length > 65536)
            throw new InvalidDataException("Preview identity exceeds its input budget.");
        using var stream = File.OpenRead(path);
        var identity = JsonSerializer.Deserialize(stream, ServerJsonContext.Default.WorkbenchPreviewIdentity)
            ?? throw new InvalidDataException("Missing Preview identity.");
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var sequence = identity.Version.Split('.').Last();
        if (identity.Profile != "preview-1" || identity.ScopeRevision != "R1" || identity.Rid != "win-x64"
            || identity.Candidate != "candidate-" + sequence.PadLeft(2, '0')
            || version != identity.Version + "+" + identity.SourceCommit)
            throw new InvalidDataException("Preview Server/package identity mismatch.");
        return identity;
    }

    internal static bool Matches(HttpRequest request, WorkbenchPreviewIdentity identity) =>
        request.Headers["X-SonnetDB-Workbench-Profile"] == identity.Profile
        && request.Headers["X-SonnetDB-Workbench-Version"] == identity.Version
        && request.Headers["X-SonnetDB-Workbench-Source"] == identity.SourceCommit;
}
