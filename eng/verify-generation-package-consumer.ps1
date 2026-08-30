[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7)
{
    throw 'PowerShell 7 or newer is required.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sonnetdb-generation-consumer-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $temporaryRoot 'feed'
$consumer = Join-Path $temporaryRoot 'consumer'
$packages = Join-Path $temporaryRoot 'packages'
$packageVersion = '0.0.0-generation-contract'

try
{
    New-Item -ItemType Directory -Path $feed, $consumer, $packages -Force | Out-Null
    & dotnet pack (Join-Path $repoRoot 'src/SonnetDB.Core/SonnetDB.Core.csproj') `
        --configuration Release `
        --no-restore `
        --output $feed `
        -p:PackageVersion=$packageVersion `
        -p:EnablePackageValidation=false
    if ($LASTEXITCODE -ne 0)
    {
        throw 'SonnetDB.Core package creation failed.'
    }

    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SonnetDB.Core" Version="0.0.0-generation-contract" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj') -Encoding utf8NoBOM

    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="generation-contract" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'NuGet.config') -Encoding utf8NoBOM

    @'
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Generations;
using SonnetDB.Kv;

string root = Path.Combine(Path.GetTempPath(), "sonnetdb-package-generation-" + Guid.NewGuid().ToString("N"));
try
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        Kv = KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
    });
    db.Keyspaces.Open("source-a").Put("value", Encoding.UTF8.GetBytes("a"));
    DatabaseGeneration published = db.Generations.Publish(new DatabaseGenerationPublishRequest
    {
        Stream = "source",
        GenerationId = "a",
        ExpectedRevision = 0,
        Resources =
        [
            new DatabaseGenerationResource(
                "state",
                DatabaseGenerationResourceKind.KvKeyspace,
                "source-a"),
        ],
    });
    using DatabaseGenerationQueryLease lease = db.Generations.AcquireActive("source");
    string cursor = lease.CreateCursor("query-v1", [1, 2, 3]);
    if (published.Revision != 1
        || !lease.ReadCursor(cursor, "query-v1").SequenceEqual(new byte[] { 1, 2, 3 }))
        throw new InvalidOperationException("generation package contract returned an unexpected result.");
    DatabaseGenerationCleanupResult cleanup = db.Generations.CleanupRetired(
        "source",
        new DatabaseGenerationCleanupOptions(DateTimeOffset.MaxValue),
        CancellationToken.None);
    if (cleanup.RemovedRevisions.Count != 0
        || cleanup.DeferredRevisions.Count != 0
        || cleanup.RetentionDeferredRevisions.Count != 0
        || db.Generations.List("source").Single().Revision != 1)
        throw new InvalidOperationException("selective generation cleanup removed the active revision.");
    DatabaseGenerationCleanupResult legacyCleanup = db.Generations.CleanupRetired("source", default);
    if (legacyCleanup.RemovedRevisions.Count != 0
        || legacyCleanup.DeferredRevisions.Count != 0
        || legacyCleanup.RetentionDeferredRevisions.Count != 0)
        throw new InvalidOperationException("legacy generation cleanup changed behavior.");
    Console.WriteLine("generation-package-consumer: PASS");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs') -Encoding utf8NoBOM

    & dotnet restore (Join-Path $consumer 'Consumer.csproj') `
        --configfile (Join-Path $consumer 'NuGet.config') `
        --packages $packages `
        --use-lock-file
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Package consumer restore failed.'
    }
    & dotnet restore (Join-Path $consumer 'Consumer.csproj') `
        --configfile (Join-Path $consumer 'NuGet.config') `
        --packages $packages `
        --locked-mode
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Package consumer locked restore failed.'
    }
    & dotnet run --project (Join-Path $consumer 'Consumer.csproj') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Package consumer execution failed.'
    }
}
finally
{
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
