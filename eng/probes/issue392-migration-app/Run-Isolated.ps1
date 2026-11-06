param(
    [Parameter(Mandatory)][string]$ProviderVersion,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$CandidateFeed,
    [string]$ConnectionString
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output must be new: $output" }
$null = New-Item -ItemType Directory -Path $output
[IO.File]::WriteAllText((Join-Path $output 'Directory.Build.props'), '<Project />', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output 'Directory.Build.targets'), '<Project />', [Text.UTF8Encoding]::new($false))
$project = Join-Path $output 'app'
$null = New-Item -ItemType Directory -Path $project
# Only five known app inputs, copied out of the repository to avoid inherited props or source references.
$inputs = @('InventoryMigrationApp.csproj', 'Program.cs', 'InventoryModel.cs', 'InventoryMigrations.cs', 'InventoryWorkflow.cs')
foreach ($inputFile in $inputs) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $inputFile) -Destination $project }
$config = Join-Path $output 'NuGet.Config'
$candidateSource = ''
if ($CandidateFeed) {
    $feed = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($CandidateFeed))
    $candidateSource = '<add key="candidate" value="' + $feed + '" />'
}
$configuration = '<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' + $candidateSource + '</packageSources></configuration>'
[IO.File]::WriteAllText($config, $configuration, [Text.UTF8Encoding]::new($false))
$previousPackages = $env:NUGET_PACKAGES
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
$previousLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$env:NUGET_PACKAGES = Join-Path $output 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $output 'http-cache'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$dotnet = 'C:/Program Files/dotnet/dotnet.exe'
$manifest = [ordered]@{
    providerVersion = $ProviderVersion
    sourceBoundary = $(if ($CandidateFeed) { 'LOCAL_CANDIDATE_NOT_PUBLIC' } else { 'NUGET_ORG_ONLY' })
    output = $output
    executionDirectory = $project
    packageCache = $env:NUGET_PACKAGES
    startedUtc = [DateTime]::UtcNow.ToString('O')
    status = 'RUNNING'
    actions = @()
    inputs = @($inputs | ForEach-Object { @{file = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $project $_)).Hash} })
}
$locationPushed = $false
try {
    Push-Location -LiteralPath $project
    $locationPushed = $true
    $csproj = Join-Path $project 'InventoryMigrationApp.csproj'
    & $dotnet restore $csproj --configfile $config --packages $env:NUGET_PACKAGES --no-http-cache --force "-p:ProviderVersion=$ProviderVersion" *> (Join-Path $output 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $LASTEXITCODE" }
    & $dotnet build $csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false "-p:ProviderVersion=$ProviderVersion" *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
    $restoredAssets = Get-Content -LiteralPath (Join-Path $project 'obj/project.assets.json') -Raw | ConvertFrom-Json
    $packageBindings = @(
        @{id = 'SonnetDB.Protocol'; assembly = 'SonnetDB.Protocol'},
        @{id = 'SonnetDB.Core'; assembly = 'SonnetDB.Core'},
        @{id = 'SonnetDB'; assembly = 'SonnetDB.Data'},
        @{id = 'SonnetDB.EntityFrameworkCore'; assembly = 'SonnetDB.EntityFrameworkCore'}
    )
    foreach ($binding in $packageBindings) {
        $packageName = $binding.id
        $binaryName = $binding.assembly
        $identity = "$packageName/$ProviderVersion"
        $library = $restoredAssets.libraries.PSObject.Properties[$identity]
        if ($null -eq $library -or $library.Value.type -ne 'package') { throw "Expected exact package dependency: $identity" }
        $cachedBinary = Join-Path $env:NUGET_PACKAGES ($packageName.ToLowerInvariant() + '/' + $ProviderVersion.ToLowerInvariant() + '/lib/net10.0/' + $binaryName + '.dll')
        $loadedBinary = Join-Path $project ("bin/Release/net10.0/$binaryName.dll")
        if ((Get-FileHash -LiteralPath $cachedBinary).Hash -cne (Get-FileHash -LiteralPath $loadedBinary).Hash) { throw "Package/output DLL mismatch: $identity" }
    }
    $manifest.packageIdentityVerified = $true
    if (-not $ConnectionString) { $ConnectionString = 'Data Source=' + (Join-Path $output 'database') }
    $dll = Join-Path $project 'bin/Release/net10.0/InventoryMigrationApp.dll'
    # Three separate, finite app processes; Cordonix owns the outer deadline and process tree.
    $actions = @('roundtrip', 'verify-new', 'verify-new')
    for ($index = 0; $index -lt $actions.Count; $index++) {
        $action = $actions[$index]
        $log = Join-Path $output ("app-$index-$action.log")
        & $dotnet $dll $action $ConnectionString *> $log
        $manifest.actions += @{action = $action; exitCode = $LASTEXITCODE; log = $log}
        if ($LASTEXITCODE -ne 0) { throw "Application failed: $action, exit=$LASTEXITCODE" }
    }
    $manifest.status = 'PASS'
} catch {
    $manifest.status = 'FAIL'
    $manifest.failure = $_.Exception.Message
    throw
} finally {
    $assets = Join-Path $project 'obj/project.assets.json'
    if (Test-Path -LiteralPath $assets) {
        $resolved = Get-Content -LiteralPath $assets -Raw | ConvertFrom-Json
        $manifest.sonnetDependencies = @($resolved.libraries.PSObject.Properties | Where-Object Name -Like 'SonnetDB*' | Select-Object -First 16 | ForEach-Object {
            $identity = $_.Name
            $packageDirectory = Join-Path (Join-Path $output 'packages') $_.Value.path
            $metadataPath = Join-Path $packageDirectory '.nupkg.metadata'
            $metadata = $(if (Test-Path -LiteralPath $metadataPath) { Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json } else { $null })
            $nuspecPath = Join-Path $packageDirectory ($identity.Split('/')[0].ToLowerInvariant() + '.nuspec')
            $repositoryCommit = $(if (Test-Path -LiteralPath $nuspecPath) { ([xml](Get-Content -LiteralPath $nuspecPath -Raw)).package.metadata.repository.commit } else { $null })
            @{identity = $identity; sha512 = $_.Value.sha512; type = $_.Value.type; path = $_.Value.path; source = $metadata.source; repositoryCommit = $repositoryCommit}
        })
        $binaryDirectory = Join-Path $project 'bin/Release/net10.0'
        $manifest.sonnetBinaries = @('SonnetDB.Protocol.dll', 'SonnetDB.Core.dll', 'SonnetDB.Data.dll', 'SonnetDB.EntityFrameworkCore.dll') | ForEach-Object {
            $binary = Join-Path $binaryDirectory $_
            if (Test-Path -LiteralPath $binary) { @{file = $_; sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash} }
        }
    }
    $manifest.finishedUtc = [DateTime]::UtcNow.ToString('O')
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8NoBOM
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
    $env:DOTNET_CLI_UI_LANGUAGE = $previousLanguage
    if ($locationPushed) { Pop-Location }
}
Write-Output ("PASS PACKAGE_CONSUMER " + $ProviderVersion + ' | ' + $manifest.sourceBoundary + ' | ' + $output)
