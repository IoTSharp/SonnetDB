param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [switch]$UseLocalProxy,
    [string]$ServerSnapshot,
    [string]$ServerManifest,
    [int]$RestPort = 43866,
    [int]$FramePort = 43867
)
. (Join-Path $PSScriptRoot 'Run.ps1') -OutputDirectory $OutputDirectory -RuntimeDirectory $RuntimeDirectory -UseLocalProxy:$UseLocalProxy -ServerSnapshot $ServerSnapshot -ServerManifest $ServerManifest -RestPort $RestPort -FramePort $FramePort -FunctionsOnly
$sourceRoot = Join-Path $runRoot 'chloe-source'
$sourceCommit = 'bb7ec8735bd4511c35453eb009f70cc606795703'
$patchPath = Join-Path $PSScriptRoot 'ChloeDecimal.patch'
$sourceInputs = @()
try {
    Invoke-Captured 'clone' 'git' @('clone', '--depth', '1', '--single-branch', '--branch', 'feat/sonnetdb-provider', 'https://github.com/maikebing/Chloe.git', $sourceRoot) $runRoot 90
    Invoke-Captured 'chloe-head' 'git' @('rev-parse', 'HEAD') $sourceRoot 30
    if ((Get-Content -LiteralPath (Join-Path $runRoot 'chloe-head.stdout.log') -Raw -Encoding utf8).Trim() -ne $sourceCommit) { throw 'Chloe PR head changed; review before adapting.' }
    $sourceInputs = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src/Chloe'), (Join-Path $sourceRoot 'src/Chloe.SonnetDB') -File -Recurse | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($sourceRoot, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $consumer = Join-Path $runRoot 'consumer'
    foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $consumer $name)
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $sourceRoot $name)
    }
    foreach ($name in @('ChloeConsumer.csproj', 'ChloeProgram.cs.txt')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $consumer $name)
    }
    $properties = @(('-p:ChloeSourceRoot=' + $sourceRoot), '-p:TargetFrameworks=net10.0', '-p:DisableImplicitLibraryPacksFolder=true', '-p:NuGetAudit=false', '-p:RestoreFallbackFolders=', '-p:RestoreAdditionalProjectFallbackFolders=')
    Invoke-Captured 'sdk' 'dotnet' @('--info') $consumer 45
    Invoke-Captured 'restore' 'dotnet' (@('restore', 'ChloeConsumer.csproj', '--configfile', (Join-Path $consumer 'NuGet.Config'), '--packages', (Join-Path $runRoot 'packages'), '--no-http-cache') + $properties) $consumer
    $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Encoding utf8 -Raw | ConvertFrom-Json
    $projectNames = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' } | ForEach-Object Name | Sort-Object)
    if ($projectNames.Count -ne 2 -or $projectNames -notcontains 'Chloe.SonnetDB/5.56.0' -or $projectNames -notcontains 'Chloe/5.56.0') { throw 'Unexpected project dependency; only fixed Chloe source is permitted.' }
    $sources = @($assets.project.restore.sources.PSObject.Properties.Name)
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    if ($sources.Count -ne 1 -or $sources[0] -ne 'https://api.nuget.org/v3/index.json' -or $folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne (Join-Path $runRoot 'packages')) { throw 'Unexpected restore source or fallback folder.' }
    Invoke-Captured 'baseline-build' 'dotnet' (@('build', 'ChloeConsumer.csproj', '-c', 'Release', '--no-restore', '-nr:false') + $properties) $consumer 240
    $baselineBin = Join-Path $runRoot 'baseline-bin'
    Copy-Item -LiteralPath (Join-Path $consumer 'bin/Release/net10.0') -Destination $baselineBin -Recurse
    Invoke-Captured 'baseline-guard' 'dotnet' @((Join-Path $baselineBin 'ChloeConsumer.dll'), 'guard', ('Data Source=' + (Join-Path $runtimeRoot 'baseline-db')), 'embedded') $consumer 90
    Invoke-Captured 'patch-check' 'git' @('apply', '--check', $patchPath) $sourceRoot 30
    Invoke-Captured 'patch-apply' 'git' @('apply', $patchPath) $sourceRoot 30
    Invoke-Captured 'candidate-diff' 'git' @('diff', '--', 'src/Chloe.SonnetDB/DDL/SonnetDBTableGenerator.cs') $sourceRoot 30
    Invoke-Captured 'candidate-build' 'dotnet' (@('build', 'ChloeConsumer.csproj', '-c', 'Release', '--no-restore', '-nr:false') + $properties) $consumer 240
    $expected = @{ sonnetdb = '0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4'; 'sonnetdb.core' = '10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268' }
    foreach ($id in $expected.Keys | Sort-Object) {
        $packageRoot = Join-Path $runRoot ('packages/' + $id + '/4.5.0-preview.ef266.5')
        $hash = (Get-FileHash -LiteralPath (Join-Path $packageRoot ($id + '.4.5.0-preview.ef266.5.nupkg')) -Algorithm SHA256).Hash
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $packageRoot ($id + '.nuspec')) -Encoding utf8 -Raw
        $metadata = Get-Content -LiteralPath (Join-Path $packageRoot '.nupkg.metadata') -Encoding utf8 -Raw | ConvertFrom-Json
        if ($hash -ne $expected[$id] -or $nuspec.package.metadata.repository.commit -ne '1af23d8f299febfdf66b040ab3419980396acf46' -or $metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw 'Unexpected public package provenance.' }
        $dlls = foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $packageRoot 'lib/net10.0') -Filter '*.dll') {
            foreach ($bin in @($baselineBin, (Join-Path $consumer 'bin/Release/net10.0'))) {
                $packageHash = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash
                $runtimeHash = (Get-FileHash -LiteralPath (Join-Path $bin $dll.Name) -Algorithm SHA256).Hash
                if ($packageHash -ne $runtimeHash) { throw 'Runtime DLL differs from public package.' }
                [ordered]@{ name = $dll.Name; bin = $bin; packageSha256 = $packageHash; runtimeSha256 = $runtimeHash }
            }
        }
        $packages.Add([ordered]@{ id = $id; version = '4.5.0-preview.ef266.5'; sha256 = $hash; repositoryCommit = $nuspec.package.metadata.repository.commit; source = $metadata.source; dlls = @($dlls) })
    }
    $dll = Join-Path $consumer 'bin/Release/net10.0/ChloeConsumer.dll'
    foreach ($phase in @('initial', 'reopen')) {
        Invoke-Captured ('embedded-' + $phase) 'dotnet' @($dll, $phase, ('Data Source=' + (Join-Path $runtimeRoot 'embedded-db')), 'embedded') $consumer 100
    }
    if ($ServerSnapshot) {
        if (!$ServerManifest) { throw 'ServerManifest is required.' }
        $hostManifest = Get-Content -LiteralPath $ServerManifest -Encoding utf8 -Raw | ConvertFrom-Json
        if ($hostManifest.productionSource -ne '285186b868a59d4b22e47279ea51deb38a3a1f45') { throw 'Unexpected Server source.' }
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
            $database = if ($protocol -eq 'rest') { 'chloe_rest' } else { 'chloe_frame' }
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "http://127.0.0.1:$RestPort/v1/db")
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'issue264-local-acceptance')
            $request.Content = [Net.Http.StringContent]::new(('{"name":"' + $database + '"}'), $utf8, 'application/json')
            $response = $http.Send($request); $response.EnsureSuccessStatusCode() | Out-Null
            $response.Dispose(); $request.Dispose()
            $port = if ($protocol -eq 'rest') { $RestPort } else { $FramePort }
            $connection = "Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=issue264-local-acceptance;Protocol=$protocol;Timeout=15"
            Invoke-Captured ($protocol + '-initial') 'dotnet' @($dll, 'initial', $connection, $protocol) $consumer 100
        }
        Stop-HostSnapshot $activeHost; $activeHost = $null
        $activeHost = Start-HostSnapshot 2
        foreach ($protocol in @('rest', 'frame-http2')) {
            $database = if ($protocol -eq 'rest') { 'chloe_rest' } else { 'chloe_frame' }
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
    $files = @(Get-ChildItem -LiteralPath $runRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](packages|http-cache|plugins-cache|dotnet-home|temp|\.git)[\\/]' } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($runRoot, $_.FullName); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $receipt = [ordered]@{ issue = 264; utc = [DateTime]::UtcNow.ToString('o'); runRoot = $runRoot; runtimeRoot = $runtimeRoot; failure = $failure; chloeSourceCommit = $sourceCommit; localCandidate = $true; patchSha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash; sonnetDbProjectReferences = 0; chloeProjectReferences = 2; globalCacheChanged = $false; originalSourceFiles = $sourceInputs; packages = $packages.ToArray(); commands = $commands.ToArray(); hosts = $hosts.ToArray(); hostSource = $hostManifest.productionSource; hostManifestSha256 = if ($ServerManifest) { (Get-FileHash -LiteralPath $ServerManifest -Algorithm SHA256).Hash } else { $null }; files = $files }
    [IO.File]::WriteAllText((Join-Path $runRoot 'receipt.json'), ($receipt | ConvertTo-Json -Depth 12), $utf8)
    Write-Host ('Evidence: ' + (Join-Path $runRoot 'receipt.json'))
}
if ($failure) { exit 1 }
