param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [switch]$UseLocalProxy,
    [string]$ServerSnapshot,
    [string]$ServerManifest,
    [switch]$FunctionsOnly,
    [int]$RestPort = 43864,
    [int]$FramePort = 43865
)
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$runRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $runRoot) { throw 'OutputDirectory must be new; evidence is never overwritten.' }
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeDirectory)
if (Test-Path -LiteralPath $runtimeRoot) { throw 'RuntimeDirectory must be new and exclusively owned.' }
[IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $runtimeRoot 'temp')) | Out-Null
[IO.File]::WriteAllText((Join-Path $runtimeRoot '.owner'), ('issue264|' + $runRoot), $utf8)
foreach ($name in @('temp', 'consumer', 'dotnet-home')) { [IO.Directory]::CreateDirectory((Join-Path $runRoot $name)) | Out-Null }
$commands = [Collections.Generic.List[object]]::new()
$packages = [Collections.Generic.List[object]]::new()
$hosts = [Collections.Generic.List[object]]::new()
$failure = $null
$activeHost = $null
$hostManifest = $null

function Start-Owned([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($pair in @{
        NUGET_PACKAGES = 'packages'; NUGET_HTTP_CACHE_PATH = 'http-cache'; NUGET_PLUGINS_CACHE_PATH = 'plugins-cache';
        DOTNET_CLI_HOME = 'dotnet-home'
    }.GetEnumerator()) { $info.Environment[$pair.Key] = Join-Path $runRoot $pair.Value }
    $info.Environment['TEMP'] = Join-Path $runtimeRoot 'temp'
    $info.Environment['TMP'] = Join-Path $runtimeRoot 'temp'
    $info.Environment['NUGET_FALLBACK_PACKAGES'] = ''
    $info.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    $info.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $info.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $info.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    if ($UseLocalProxy) {
        $info.Environment['HTTP_PROXY'] = 'http://127.0.0.1:7890'
        $info.Environment['HTTPS_PROXY'] = 'http://127.0.0.1:7890'
        $info.Environment['NO_PROXY'] = 'localhost,127.0.0.1,::1'
    }
    $started = [DateTime]::UtcNow.ToString('o')
    $process = [Diagnostics.Process]::Start($info)
    return @{ process = $process; stdout = $process.StandardOutput.ReadToEndAsync(); stderr = $process.StandardError.ReadToEndAsync(); startedUtc = $started; executable = $Executable; arguments = $Arguments; workingDirectory = $WorkingDirectory; pid = $process.Id }
}

function Save-Owned($Owned, [string]$Label, [bool]$TimedOut = $false, [bool]$KilledForReopen = $false) {
    [IO.File]::WriteAllText((Join-Path $runRoot ($Label + '.stdout.log')), $Owned.stdout.GetAwaiter().GetResult(), $utf8)
    [IO.File]::WriteAllText((Join-Path $runRoot ($Label + '.stderr.log')), $Owned.stderr.GetAwaiter().GetResult(), $utf8)
    $record = [ordered]@{ label = $Label; executable = $Owned.executable; arguments = $Owned.arguments; workingDirectory = $Owned.workingDirectory; pid = $Owned.pid; startedUtc = $Owned.startedUtc; endedUtc = [DateTime]::UtcNow.ToString('o'); exitCode = $Owned.process.ExitCode; timedOut = $TimedOut; killedForReopen = $KilledForReopen }
    $Owned.process.Dispose()
    return $record
}

function Invoke-Captured([string]$Label, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 180) {
    $owned = Start-Owned $Executable $Arguments $WorkingDirectory
    $timedOut = !$owned.process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) { $owned.process.Kill($true); $owned.process.WaitForExit() }
    $record = Save-Owned $owned $Label $timedOut
    $commands.Add($record)
    Write-Host ($Label + ': exit=' + $record.exitCode)
    if ($record.exitCode -ne 0 -or $timedOut) { throw ('Command failed: ' + $Label) }
}

function Start-HostSnapshot([int]$Round) {
    $owned = Start-Owned 'dotnet' @((Join-Path $ServerSnapshot 'SonnetDB.dll'), '--contentRoot', (Join-Path $runRoot 'host-content')) $runRoot
    $owned.label = 'server-' + $Round
    $waitUntil = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $waitUntil) {
        if ($owned.process.HasExited) { $hosts.Add((Save-Owned $owned $owned.label)); throw 'Server exited before readiness.' }
        try {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "http://127.0.0.1:$RestPort/v1/db")
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'issue264-local-acceptance')
            $response = $http.Send($request)
            $ready = $response.IsSuccessStatusCode
            $response.Dispose(); $request.Dispose()
            if ($ready) { return $owned }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    $owned.process.Kill($true); $owned.process.WaitForExit()
    $hosts.Add((Save-Owned $owned $owned.label $true))
    throw 'Server readiness timeout.'
}

function Stop-HostSnapshot($Owned) {
    $killed = !$Owned.process.HasExited
    if ($killed) { $Owned.process.Kill($true); $Owned.process.WaitForExit() }
    $hosts.Add((Save-Owned $Owned $Owned.label $false $killed))
}

if ($FunctionsOnly) { return }

try {
    $consumer = Join-Path $runRoot 'consumer'
    foreach ($name in @('Consumer.csproj', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config', 'Program.cs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $consumer $name)
    }
    Invoke-Captured 'sdk' 'dotnet' @('--info') $consumer 45
    Invoke-Captured 'restore' 'dotnet' @('restore', 'Consumer.csproj', '--configfile', (Join-Path $consumer 'NuGet.Config'), '--packages', (Join-Path $runRoot 'packages'), '--no-http-cache', '-p:RestoreFallbackFolders=', '-p:RestoreAdditionalProjectFallbackFolders=') $consumer
    $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Encoding utf8 -Raw | ConvertFrom-Json
    if (@($assets.libraries.PSObject.Properties.Value | Where-Object type -eq 'project').Count -ne 0) { throw 'Unexpected ProjectReference.' }
    $sources = @($assets.project.restore.sources.PSObject.Properties.Name)
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    if ($sources.Count -ne 1 -or $sources[0] -ne 'https://api.nuget.org/v3/index.json' -or $folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne (Join-Path $runRoot 'packages')) { throw 'Unexpected restore source or fallback folder.' }
    Invoke-Captured 'build' 'dotnet' @('build', 'Consumer.csproj', '-c', 'Release', '--no-restore', '-nr:false') $consumer
    $expected = @{ sonnetdb = '0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4'; 'sonnetdb.core' = '10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268' }
    foreach ($id in $expected.Keys | Sort-Object) {
        $packageRoot = Join-Path $runRoot ('packages/' + $id + '/4.5.0-preview.ef266.5')
        $hash = (Get-FileHash -LiteralPath (Join-Path $packageRoot ($id + '.4.5.0-preview.ef266.5.nupkg')) -Algorithm SHA256).Hash
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $packageRoot ($id + '.nuspec')) -Encoding utf8 -Raw
        $metadata = Get-Content -LiteralPath (Join-Path $packageRoot '.nupkg.metadata') -Encoding utf8 -Raw | ConvertFrom-Json
        if ($hash -ne $expected[$id] -or $nuspec.package.metadata.repository.commit -ne '1af23d8f299febfdf66b040ab3419980396acf46' -or $metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw 'Unexpected public package provenance.' }
        $dlls = foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $packageRoot 'lib/net10.0') -Filter '*.dll') {
            $packageHash = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash
            $runtimeHash = (Get-FileHash -LiteralPath (Join-Path $consumer ('bin/Release/net10.0/' + $dll.Name)) -Algorithm SHA256).Hash
            if ($packageHash -ne $runtimeHash) { throw 'Runtime DLL differs from public package.' }
            [ordered]@{ name = $dll.Name; packageSha256 = $packageHash; runtimeSha256 = $runtimeHash }
        }
        $packages.Add([ordered]@{ id = $id; version = '4.5.0-preview.ef266.5'; sha256 = $hash; repositoryCommit = $nuspec.package.metadata.repository.commit; source = $metadata.source; dlls = @($dlls) })
    }
    $dll = Join-Path $consumer 'bin/Release/net10.0/Consumer.dll'
    foreach ($phase in @('initial', 'reopen')) {
        Invoke-Captured ('embedded-' + $phase) 'dotnet' @($dll, $phase, ('Data Source=' + (Join-Path $runtimeRoot 'embedded-db')), 'embedded') $consumer 100
    }
    Invoke-Captured 'core' 'dotnet' @($dll, 'core', (Join-Path $runtimeRoot 'core-db'), 'embedded-core') $consumer 100
    if ($ServerSnapshot) {
        if (!$ServerManifest) { throw 'ServerManifest is required for remote validation.' }
        $hostManifest = Get-Content -LiteralPath $ServerManifest -Encoding utf8 -Raw | ConvertFrom-Json
        if ($hostManifest.productionSource -ne '285186b868a59d4b22e47279ea51deb38a3a1f45') { throw 'Unexpected frozen Server source.' }
        foreach ($file in $hostManifest.files) {
            if ((Get-FileHash -LiteralPath (Join-Path $ServerSnapshot $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw ('Frozen host differs: ' + $file.path) }
        }
        $content = Join-Path $runRoot 'host-content'
        [IO.Directory]::CreateDirectory($content) | Out-Null
        $settings = @{ SonnetDBServer = @{ DataRoot = (Join-Path $runtimeRoot 'server-data'); Tokens = @{ 'issue264-local-acceptance' = 'admin' }; AutoLoadExistingDatabases = $true; AllowAnonymousProbes = $true; Mqtt = @{ Enabled = $false } }; Kestrel = @{ Endpoints = @{ Rest = @{ Url = "http://127.0.0.1:$RestPort"; Protocols = 'Http1' }; Frame = @{ Url = "http://127.0.0.1:$FramePort"; Protocols = 'Http2' } } }; Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore.Hosting.Diagnostics' = 'Information' } } }
        [IO.File]::WriteAllText((Join-Path $content 'appsettings.json'), ($settings | ConvertTo-Json -Depth 10), $utf8)
        $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false
        $http = [Net.Http.HttpClient]::new($handler); $http.Timeout = [TimeSpan]::FromSeconds(2)
        $activeHost = Start-HostSnapshot 1
        foreach ($protocol in @('rest', 'frame-http2')) {
            $database = if ($protocol -eq 'rest') { 'decimal_rest' } else { 'decimal_frame' }
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "http://127.0.0.1:$RestPort/v1/db")
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'issue264-local-acceptance')
            $request.Content = [Net.Http.StringContent]::new(('{"name":"' + $database + '"}'), $utf8, 'application/json')
            $response = $http.Send($request)
            $response.EnsureSuccessStatusCode() | Out-Null
            $response.Dispose(); $request.Dispose()
            $port = if ($protocol -eq 'rest') { $RestPort } else { $FramePort }
            $connection = "Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=issue264-local-acceptance;Protocol=$protocol;Timeout=15"
            Invoke-Captured ($protocol + '-initial') 'dotnet' @($dll, 'initial', $connection, $protocol) $consumer 100
        }
        Stop-HostSnapshot $activeHost; $activeHost = $null
        $activeHost = Start-HostSnapshot 2
        foreach ($protocol in @('rest', 'frame-http2')) {
            $database = if ($protocol -eq 'rest') { 'decimal_rest' } else { 'decimal_frame' }
            $port = if ($protocol -eq 'rest') { $RestPort } else { $FramePort }
            $connection = "Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=issue264-local-acceptance;Protocol=$protocol;Timeout=15"
            Invoke-Captured ($protocol + '-reopen') 'dotnet' @($dll, 'reopen', $connection, $protocol) $consumer 100
        }
        Stop-HostSnapshot $activeHost; $activeHost = $null
        $http.Dispose(); $handler.Dispose()
    }
} catch {
    $failure = $_.ToString()
    Write-Host $failure
} finally {
    if ($activeHost) { Stop-HostSnapshot $activeHost }
    $files = @(Get-ChildItem -LiteralPath $runRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](packages|http-cache|plugins-cache|dotnet-home|temp|embedded-db|core-db|server-data)[\\/]' } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($runRoot, $_.FullName); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $receipt = [ordered]@{ issue = 264; utc = [DateTime]::UtcNow.ToString('o'); runRoot = $runRoot; runtimeRoot = $runtimeRoot; failure = $failure; projectReferences = 0; globalCacheChanged = $false; packages = $packages.ToArray(); commands = $commands.ToArray(); hosts = $hosts.ToArray(); hostSource = $hostManifest.productionSource; hostManifestSha256 = if ($ServerManifest) { (Get-FileHash -LiteralPath $ServerManifest -Algorithm SHA256).Hash } else { $null }; files = $files }
    [IO.File]::WriteAllText((Join-Path $runRoot 'receipt.json'), ($receipt | ConvertTo-Json -Depth 12), $utf8)
    Write-Host ('Evidence: ' + (Join-Path $runRoot 'receipt.json'))
}
if ($failure) { exit 1 }
