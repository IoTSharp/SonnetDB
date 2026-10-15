[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$ServerSnapshot,
    [Parameter(Mandatory)][string]$ServerManifest,
    [string]$TemporaryRoot = 'D:/sndb-t',
    [int]$RestPort = 43870,
    [int]$FramePort = 43871
)

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$id = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$run = Join-Path ([IO.Path]::GetFullPath($EvidenceRoot)) ('r-' + $id)
$scratch = Join-Path ([IO.Path]::GetFullPath($TemporaryRoot)) ('i270-' + $id)
foreach ($path in @($run, $scratch)) {
    if (Test-Path -LiteralPath $path) { throw 'Each invocation requires fresh owned directories.' }
    New-Item -ItemType Directory -Path $path | Out-Null
}
[IO.File]::WriteAllText((Join-Path $scratch '.owner'), $run, $utf8)
$sourceNames = @('Program.cs', 'ContainsRepro.csproj', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config')
foreach ($name in $sourceNames) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $run $name) }
$program = Join-Path $run 'Program.cs'
$before = [IO.File]::ReadAllText($program)
$old = 'options.UseSonnetDB("Data Source=" + Path.Combine(directory, "sonnetdb"));'
$new = 'options.UseSonnetDB(Environment.GetEnvironmentVariable("ISSUE270_CONNECTION") ?? throw new InvalidOperationException("Remote connection required."));'
if ($before.Split($old).Count -ne 2) { throw 'Expected exactly one embedded connection expression.' }
[IO.File]::WriteAllText($program, $before.Replace($old, $new), $utf8)
$derivation = [ordered]@{
    sourceSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Program.cs') -Algorithm SHA256).Hash
    derivedSha256 = (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash
    original = $old; replacement = $new
}
$environment = @{
    NUGET_PACKAGES = (Join-Path $run 'p'); NUGET_HTTP_CACHE_PATH = (Join-Path $run 'h')
    DOTNET_CLI_HOME = (Join-Path $run 'c'); TEMP = $scratch; TMP = $scratch; NUGET_SCRATCH = (Join-Path $scratch 'nuget')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    MSBUILDDISABLENODEREUSE = '1'; DOTNET_CLI_USE_MSBUILD_SERVER = '0'; DOTNET_PROCESSOR_COUNT = '2'
    HTTP_PROXY = 'http://127.0.0.1:7890'; HTTPS_PROXY = 'http://127.0.0.1:7890'
    NO_PROXY = 'localhost,127.0.0.1'; ASPNETCORE_ENVIRONMENT = 'Production'; DOTNET_ENVIRONMENT = 'Production'
}
foreach ($path in @($environment.NUGET_PACKAGES, $environment.NUGET_HTTP_CACHE_PATH, $environment.DOTNET_CLI_HOME, $environment.NUGET_SCRATCH)) {
    New-Item -ItemType Directory -Path $path | Out-Null
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$steps = [Collections.Generic.List[object]]::new()
$packages = [Collections.Generic.List[object]]::new()
$databaseResponses = [Collections.Generic.List[object]]::new()
$server = $null
$serverIdentity = $null
$serverStoppedByOwnedKill = $false
$failure = $null
$snapshot = Get-Content -LiteralPath $ServerManifest -Raw -Encoding UTF8 | ConvertFrom-Json
$serverPath = [IO.Path]::GetFullPath($ServerSnapshot)
$serverManifestHash = (Get-FileHash -LiteralPath $ServerManifest -Algorithm SHA256).Hash

function New-DotnetStartInfo([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $info.WorkingDirectory = $run
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    foreach ($key in @($info.Environment.Keys | Where-Object { $_.StartsWith('SONNETDB_', [StringComparison]::OrdinalIgnoreCase) })) { $null = $info.Environment.Remove($key) }
    foreach ($key in $environment.Keys) { $info.Environment[$key] = $environment[$key] }
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    return $info
}

function Stop-OwnedProcess([Diagnostics.Process]$Process, [object]$Identity) {
    if ($Process.HasExited) { return }
    $current = Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.Id)" -OperationTimeoutSec 3
    if (-not $Identity -or -not $current -or $current.CreationDate -ne $Identity.CreationDate -or
        $current.ParentProcessId -ne $Identity.ParentProcessId -or $current.CommandLine -cne $Identity.CommandLine) {
        throw 'Refusing to terminate a process without matching owned identity.'
    }
    $Process.Kill($true)
    if (-not $Process.WaitForExit(5000)) { throw 'Owned process did not exit.' }
}

function Invoke-BoundedDotnet([string]$Name, [string[]]$Arguments, [int]$TimeoutSeconds, [string]$Connection = '') {
    $info = New-DotnetStartInfo $Arguments
    if ($Connection) { $info.Environment['ISSUE270_CONNECTION'] = $Connection }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = [DateTimeOffset]::UtcNow
    $identity = $null
    try {
        if (-not $process.Start()) { throw 'Process failed to start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $identity = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -OperationTimeoutSec 3
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { Stop-OwnedProcess $process $identity }
        [IO.File]::WriteAllText((Join-Path $run ($Name + '.stdout.log')), $stdout.GetAwaiter().GetResult(), $utf8)
        [IO.File]::WriteAllText((Join-Path $run ($Name + '.stderr.log')), $stderr.GetAwaiter().GetResult(), $utf8)
        $result = [pscustomobject]@{
            name = $Name; arguments = $Arguments; pid = $process.Id; startedUtc = $started
            finishedUtc = [DateTimeOffset]::UtcNow; timedOut = $timedOut; exitCode = $process.ExitCode
            identity = ($identity | Select-Object ProcessId, ParentProcessId, CreationDate, CommandLine)
        }
        $steps.Add($result)
        Write-Host ($result | ConvertTo-Json -Depth 4 -Compress)
        return $result
    } finally {
        if (-not $process.HasExited) { Stop-OwnedProcess $process $identity }
        $process.Dispose()
    }
}

try {
    foreach ($entry in $snapshot.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $serverPath $entry.path))
        if (-not $path.StartsWith($serverPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Frozen Server snapshot mismatch: $($entry.path)" }
    }
    $restore = Invoke-BoundedDotnet 'restore' @('restore', 'ContainsRepro.csproj', '--configfile', 'NuGet.Config', '--packages', $environment.NUGET_PACKAGES, '--no-cache', '-p:RestoreFallbackFolders=', '-p:NuGetAudit=false', '-nr:false') 300
    if ($restore.exitCode -ne 0 -or $restore.timedOut) { throw 'Isolated public client restore failed.' }
    $assets = Get-Content -LiteralPath (Join-Path $run 'obj/project.assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne $environment.NUGET_PACKAGES.TrimEnd('\', '/')) { throw 'Unexpected package folder or fallback.' }
    if (@($assets.libraries.PSObject.Properties.Value | Where-Object { $_.type -ne 'package' }).Count -ne 0) { throw 'Unexpected project or source dependency.' }
    $expectedHashes = @{
        'sonnetdb.core' = '10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268'
        'sonnetdb' = '0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4'
        'sonnetdb.entityframeworkcore' = '9688BA682E160639681AF3E4882DE6A58F70CF289B42552987FA7CD85D87E125'
    }
    foreach ($packageId in $expectedHashes.Keys) {
        $path = Join-Path $environment.NUGET_PACKAGES "$packageId/4.5.0-preview.ef266.5"
        $metadata = Get-Content -LiteralPath (Join-Path $path '.nupkg.metadata') -Raw -Encoding UTF8 | ConvertFrom-Json
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $path "$packageId.nuspec") -Raw -Encoding UTF8
        $hash = (Get-FileHash -LiteralPath (Join-Path $path "$packageId.4.5.0-preview.ef266.5.nupkg") -Algorithm SHA256).Hash
        if ($metadata.source -ne 'https://api.nuget.org/v3/index.json' -or $hash -ne $expectedHashes[$packageId] -or
            $nuspec.package.metadata.repository.commit -ne '1af23d8f299febfdf66b040ab3419980396acf46') { throw "Unexpected public package identity: $packageId" }
        $packages.Add([pscustomobject]@{id = $packageId; version = $nuspec.package.metadata.version; source = $metadata.source; nupkgSha256 = $hash; repositoryCommit = $nuspec.package.metadata.repository.commit})
    }
    $build = Invoke-BoundedDotnet 'build' @('build', 'ContainsRepro.csproj', '-c', 'Release', '--no-restore', '-nr:false') 180
    if ($build.exitCode -ne 0 -or $build.timedOut) { throw 'Public client build failed.' }
    $token = 'issue270-disposable-local-test-token'
    $contentRoot = Join-Path $run 'host'
    New-Item -ItemType Directory -Path $contentRoot | Out-Null
    $config = @{
        Logging = @{LogLevel = @{Default = 'Warning'; 'Microsoft.Hosting.Lifetime' = 'Information'; 'Microsoft.AspNetCore.Hosting.Diagnostics' = 'Information'}}
        Kestrel = @{Endpoints = @{Http = @{Url = "http://127.0.0.1:$RestPort"; Protocols = 'Http1'}; FrameH2 = @{Url = "http://127.0.0.1:$FramePort"; Protocols = 'Http2'}}}
        SonnetDBServer = @{DataRoot = (Join-Path $scratch 'db'); Tokens = @{$token = 'admin'}; AutoLoadExistingDatabases = $true; AllowAnonymousProbes = $true; Mqtt = @{Enabled = $false}}
    }
    [IO.File]::WriteAllText((Join-Path $contentRoot 'appsettings.json'), ($config | ConvertTo-Json -Depth 10), $utf8)
    $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    if (@($listeners | Where-Object { $_.Port -in @($RestPort, $FramePort) }).Count -gt 0) { throw 'Requested owned Server port is already in use.' }
    $server = [Diagnostics.Process]::new()
    $server.StartInfo = New-DotnetStartInfo @((Join-Path $serverPath 'SonnetDB.dll'), '--contentRoot', $contentRoot)
    if (-not $server.Start()) { throw 'Frozen Server failed to start.' }
    $serverOut = $server.StandardOutput.ReadToEndAsync()
    $serverErr = $server.StandardError.ReadToEndAsync()
    $serverIdentity = Get-CimInstance Win32_Process -Filter "ProcessId = $($server.Id)" -OperationTimeoutSec 3
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($server.HasExited) { throw 'Frozen Server exited during startup.' }
        try {
            $health = Invoke-WebRequest -Uri "http://127.0.0.1:$RestPort/healthz/ready" -TimeoutSec 2 -NoProxy
            if ($health.StatusCode -eq 200) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'Frozen Server did not become ready before deadline.' }
    foreach ($transport in @('rest', 'frame-http2')) {
        foreach ($mode in @('original-percent', 'original-underscore', 'original-backslash', 'matrix-all')) {
            $database = ($transport.Replace('-', '') + '-' + $mode)
            $created = Invoke-WebRequest -Uri "http://127.0.0.1:$RestPort/v1/db" -Method Post -Headers @{Authorization = "Bearer $token"} -ContentType 'application/json' -Body (@{name = $database} | ConvertTo-Json -Compress) -TimeoutSec 10 -NoProxy
            if ($created.StatusCode -ne 201) { throw 'Expected a fresh remote database.' }
            $databaseResponses.Add([pscustomobject]@{database = $database; statusCode = $created.StatusCode; body = $created.Content})
            $port = if ($transport -eq 'rest') { $RestPort } else { $FramePort }
            $connection = "Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=$token;Protocol=$transport;Timeout=10"
            $parts = $mode.Split('-')
            $name = "$transport-$mode"
            $null = Invoke-BoundedDotnet $name @('bin/Release/net10.0/ContainsRepro.dll', 'SonnetDB', $parts[0], $parts[1], (Join-Path $run $name)) 110 $connection
        }
    }
} catch { $failure = $_.Exception.ToString(); Write-Host $failure }
finally {
    if ($server) {
        if (-not $server.HasExited) { Stop-OwnedProcess $server $serverIdentity; $serverStoppedByOwnedKill = $true }
        [IO.File]::WriteAllText((Join-Path $run 'server.stdout.log'), $serverOut.GetAwaiter().GetResult(), $utf8)
        [IO.File]::WriteAllText((Join-Path $run 'server.stderr.log'), $serverErr.GetAwaiter().GetResult(), $utf8)
        $serverExitCode = $server.ExitCode
        $server.Dispose()
    }
}

$receipts = @(Get-ChildItem -LiteralPath $run -Directory | Where-Object { $_.Name -match '^(rest|frame-http2)-' } | ForEach-Object { Get-Item -LiteralPath (Join-Path $_.FullName 'receipt.json') -ErrorAction SilentlyContinue })
$cases = @($receipts | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).Cases })
$serverLog = if (Test-Path -LiteralPath (Join-Path $run 'server.stdout.log')) { [IO.File]::ReadAllText((Join-Path $run 'server.stdout.log')) } else { '' }
$restObserved = [regex]::Matches($serverLog, 'Request starting HTTP/1\.1 POST http://127\.0\.0\.1:' + $RestPort + '/v1/db/[^ ]+/sql').Count
$frameObserved = [regex]::Matches($serverLog, 'Request starting HTTP/2 POST http://127\.0\.0\.1:' + $FramePort + '/v1/frame').Count
$files = foreach ($file in @(Get-ChildItem -LiteralPath $run -File) + $receipts + @(Get-Item -LiteralPath (Join-Path $run 'obj/project.assets.json') -ErrorAction SilentlyContinue) + @(Get-ChildItem -LiteralPath (Join-Path $run 'bin/Release/net10.0') -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.dll', '.pdb', '.json') })) {
    [pscustomobject]@{path = [IO.Path]::GetRelativePath($run, $file.FullName); sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
$manifest = [ordered]@{
    issue = 270; runDirectory = $run; shortTemporaryRoot = $scratch; environment = $environment
    connectionSourceDerivation = $derivation; projectReferences = 0; sourceDllSubstitutions = 0; packages = $packages.ToArray()
    sourceServer = @{path = $serverPath; manifestSha256 = $serverManifestHash; productionCommit = $snapshot.productionSource; filesVerified = $snapshot.files.Count; identity = ($serverIdentity | Select-Object ProcessId, ParentProcessId, CreationDate, CommandLine); stoppedByOwnedKill = $serverStoppedByOwnedKill; exitCode = $serverExitCode}
    databases = $databaseResponses.ToArray(); observedRestHttp11SqlRequests = $restObserved; observedFrameHttp2Requests = $frameObserved
    steps = $steps.ToArray(); failure = $failure; cases = $cases.Count; passedCases = @($cases | Where-Object Passed).Count; failedCases = @($cases | Where-Object { -not $_.Passed }).Count
    files = @($files)
}
[IO.File]::WriteAllText((Join-Path $run 'validation-manifest.json'), ($manifest | ConvertTo-Json -Depth 20), $utf8)
Write-Host "MANIFEST $(Join-Path $run 'validation-manifest.json')"
if ($failure -or @($steps | Where-Object { $_.exitCode -ne 0 -or $_.timedOut }).Count -gt 0 -or $cases.Count -ne 102 -or $manifest.failedCases -ne 0 -or $restObserved -eq 0 -or $frameObserved -eq 0) { exit 1 }
exit 0
