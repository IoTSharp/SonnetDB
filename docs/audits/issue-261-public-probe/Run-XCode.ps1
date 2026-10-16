param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$XCodeSource,
    [switch]$UseLocalProxy,
    [string]$ServerSnapshot,
    [string]$ServerManifest,
    [switch]$BaselineOnly,
    [int]$RestPort = 43876,
    [int]$FramePort = 43877
)
. (Join-Path $PSScriptRoot 'Run.ps1') -OutputDirectory $OutputDirectory -RuntimeDirectory $RuntimeDirectory -UseLocalProxy:$UseLocalProxy -ServerSnapshot $ServerSnapshot -ServerManifest $ServerManifest -RestPort $RestPort -FramePort $FramePort -FunctionsOnly
$sourceRoot = Join-Path $runRoot 'xcode-source'
$sourceCommit = '757fa2a078906f87783e55fe0e9d5f54a9e916b9'
$sourceInputs = @()
$bins = [Collections.Generic.List[object]]::new()
$patchPath = Join-Path $runRoot 'XCodeMetadata.patch'
if (!$BaselineOnly) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'XCodeMetadata.patch') -Destination $patchPath }

function Assert-Package([string]$Version, [string]$Bin) {
    foreach ($id in @('sonnetdb','sonnetdb.core')) {
        $packageRoot = Join-Path $runRoot ('packages/' + $id + '/' + $Version)
        $hash = (Get-FileHash -LiteralPath (Join-Path $packageRoot ($id + '.' + $Version + '.nupkg')) -Algorithm SHA256).Hash
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $packageRoot ($id + '.nuspec')) -Encoding utf8 -Raw
        $metadata = Get-Content -LiteralPath (Join-Path $packageRoot '.nupkg.metadata') -Encoding utf8 -Raw | ConvertFrom-Json
        if ($metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw 'Non-public package source.' }
        if ($Version -eq '4.5.0-preview.ef266.5') {
            $expected = @{ sonnetdb = '0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4'; 'sonnetdb.core' = '10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268' }
            if ($hash -ne $expected[$id] -or $nuspec.package.metadata.repository.commit -ne '1af23d8f299febfdf66b040ab3419980396acf46') { throw 'Unexpected preview package.' }
        }
        $dlls = foreach ($file in Get-ChildItem -LiteralPath (Join-Path $packageRoot 'lib/net10.0') -Filter '*.dll') {
            $packageHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $runtimeHash = (Get-FileHash -LiteralPath (Join-Path $Bin $file.Name) -Algorithm SHA256).Hash
            if ($packageHash -ne $runtimeHash) { throw 'Public/runtime DLL mismatch.' }
            [ordered]@{ name = $file.Name; packageSha256 = $packageHash; runtimeSha256 = $runtimeHash }
        }
        $packages.Add([ordered]@{ id = $id; version = $Version; sha256 = $hash; source = $metadata.source; repositoryCommit = $nuspec.package.metadata.repository.commit; bin = $Bin; dlls = @($dlls) })
    }
}

try {
    Invoke-Captured 'clone' 'git' @('clone','--no-hardlinks',$XCodeSource,$sourceRoot) $runRoot 90
    Invoke-Captured 'xcode-head' 'git' @('rev-parse','HEAD') $sourceRoot 30
    if ((Get-Content -LiteralPath (Join-Path $runRoot 'xcode-head.stdout.log') -Raw -Encoding utf8).Trim() -ne $sourceCommit) { throw 'XCode source changed.' }
    $sourceInputs = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'XCode') -File -Recurse | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($sourceRoot,$_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $consumer = Join-Path $runRoot 'consumer'
    foreach ($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $consumer $name)
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $sourceRoot $name)
    }
    foreach ($name in @('XCodeConsumer.csproj','XCodeProgram.cs.txt','MetadataCases.cs')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $consumer $name) }
    $properties = @(('-p:XCodeSourceRoot=' + $sourceRoot),'-p:DisableImplicitLibraryPacksFolder=true','-p:NuGetAudit=false','-p:RestoreFallbackFolders=','-p:RestoreAdditionalProjectFallbackFolders=')
    Invoke-Captured 'sdk' 'dotnet' @('--info') $consumer 45
    foreach ($version in @('4.0.0','4.5.0-preview.ef266.5')) {
        $label = if ($version -eq '4.0.0') { 'stable' } else { 'preview' }
        $versionProperties = $properties + @('-p:PublicVersion=' + $version)
        Invoke-Captured ($label + '-restore') 'dotnet' (@('restore','XCodeConsumer.csproj','--configfile',(Join-Path $consumer 'NuGet.Config'),'--packages',(Join-Path $runRoot 'packages'),'--no-http-cache') + $versionProperties) $consumer 180
        $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Encoding utf8 -Raw | ConvertFrom-Json
        $projects = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' })
        $sources = @($assets.project.restore.sources.PSObject.Properties.Name)
        $folders = @($assets.packageFolders.PSObject.Properties.Name)
        if ($projects.Count -ne 1 -or $projects[0].Name -notlike 'NewLife.XCode/*') { throw 'Only fixed XCode source reference is allowed.' }
        if ($sources.Count -ne 1 -or $sources[0] -ne 'https://api.nuget.org/v3/index.json' -or $folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\','/') -ne (Join-Path $runRoot 'packages')) { throw 'Unexpected restore source/cache.' }
        Copy-Item -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Destination (Join-Path $runRoot ($label + '-assets.json'))
        Invoke-Captured ($label + '-build') 'dotnet' (@('build','XCodeConsumer.csproj','-c','Release','--no-restore','-nr:false') + $versionProperties) $consumer 240
        $bin = Join-Path $runRoot ($label + '-bin')
        Copy-Item -LiteralPath (Join-Path $consumer 'bin/Release/net10.0') -Destination $bin -Recurse
        Assert-Package $version $bin
        $bins.Add([ordered]@{ label=$label; xcodeSha256=(Get-FileHash -LiteralPath (Join-Path $bin 'XCode.dll') -Algorithm SHA256).Hash })
        Invoke-Captured ($label + '-guard') 'dotnet' @((Join-Path $bin 'XCodeConsumer.dll'),($label + '-guard'),('Data Source=' + (Join-Path $runtimeRoot ($label + '-db'))),'embedded') $consumer 90
    }
    if (!$BaselineOnly) {
        Invoke-Captured 'patch-check' 'git' @('apply','--ignore-space-change','--check',$patchPath) $sourceRoot 30
        Invoke-Captured 'patch-apply' 'git' @('apply','--ignore-space-change',$patchPath) $sourceRoot 30
        # 此上游文件的 Git blob 本身使用 CRLF；候选保留原换行，补丁交付统一 LF。
        $modelHelper = Join-Path $sourceRoot 'XCode/DataAccessLayer/Model/ModelHelper.cs'
        [IO.File]::WriteAllText($modelHelper,[IO.File]::ReadAllText($modelHelper).Replace("`r`n","`n").Replace("`n","`r`n"),$utf8)
        Invoke-Captured 'candidate-diff' 'git' @('diff','--','XCode/DataAccessLayer/Database/SonnetDB.cs','XCode/DataAccessLayer/Model/ModelHelper.cs') $sourceRoot 30
        Invoke-Captured 'candidate-build' 'dotnet' (@('build','XCodeConsumer.csproj','-c','Release','--no-restore','-nr:false') + $properties) $consumer 240
        $bin = Join-Path $consumer 'bin/Release/net10.0'
        Assert-Package '4.5.0-preview.ef266.5' $bin
        $bins.Add([ordered]@{ label='candidate'; xcodeSha256=(Get-FileHash -LiteralPath (Join-Path $bin 'XCode.dll') -Algorithm SHA256).Hash })
        $dll = Join-Path $bin 'XCodeConsumer.dll'
        foreach ($phase in @('initial','reopen')) { Invoke-Captured ('embedded-' + $phase) 'dotnet' @($dll,$phase,('Data Source=' + (Join-Path $runtimeRoot 'embedded-db')),'embedded') $consumer 100 }
        if ($ServerSnapshot) {
            if (!$ServerManifest) { throw 'ServerManifest required.' }
            $hostManifest = Get-Content -LiteralPath $ServerManifest -Encoding utf8 -Raw | ConvertFrom-Json
            if ($hostManifest.productionSource -ne '285186b868a59d4b22e47279ea51deb38a3a1f45') { throw 'Unexpected Server source.' }
            foreach ($file in $hostManifest.files) { if ((Get-FileHash -LiteralPath (Join-Path $ServerSnapshot $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Frozen host changed.' } }
            $content = Join-Path $runRoot 'host-content'
            [IO.Directory]::CreateDirectory($content) | Out-Null
            $settings = @{ SonnetDBServer = @{ DataRoot = (Join-Path $runtimeRoot 'server-data'); Tokens = @{ 'issue261-local-acceptance' = 'admin' }; AutoLoadExistingDatabases = $true; AllowAnonymousProbes = $true; Mqtt = @{ Enabled = $false } }; Kestrel = @{ Endpoints = @{ Rest = @{ Url = "http://127.0.0.1:$RestPort"; Protocols = 'Http1' }; Frame = @{ Url = "http://127.0.0.1:$FramePort"; Protocols = 'Http2' } } }; Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore.Hosting.Diagnostics' = 'Information' } } }
            [IO.File]::WriteAllText((Join-Path $content 'appsettings.json'),($settings | ConvertTo-Json -Depth 10),$utf8)
            $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false
            $http = [Net.Http.HttpClient]::new($handler); $http.Timeout = [TimeSpan]::FromSeconds(2)
            $activeHost = Start-HostSnapshot 1
            foreach ($protocol in @('rest','frame-http2')) {
                $database = if ($protocol -eq 'rest') { 'xcode_rest' } else { 'xcode_frame' }
                $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"http://127.0.0.1:$RestPort/v1/db")
                $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer','issue261-local-acceptance')
                $request.Content = [Net.Http.StringContent]::new(('{"name":"' + $database + '"}'),$utf8,'application/json')
                $response = $http.Send($request); $response.EnsureSuccessStatusCode() | Out-Null
                $response.Dispose(); $request.Dispose()
                $port = if ($protocol -eq 'rest') { $RestPort } else { $FramePort }
                $connection = "Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=issue261-local-acceptance;Protocol=$protocol;Timeout=15"
                Invoke-Captured ($protocol + '-initial') 'dotnet' @($dll,'initial',$connection,$protocol) $consumer 100
            }
            Stop-HostSnapshot $activeHost; $activeHost=$null
            $activeHost = Start-HostSnapshot 2
            foreach ($protocol in @('rest','frame-http2')) {
                $database = if ($protocol -eq 'rest') { 'xcode_rest' } else { 'xcode_frame' }
                $port = if ($protocol -eq 'rest') { $RestPort } else { $FramePort }
                $connection = "Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=issue261-local-acceptance;Protocol=$protocol;Timeout=15"
                Invoke-Captured ($protocol + '-reopen') 'dotnet' @($dll,'reopen',$connection,$protocol) $consumer 100
            }
            Stop-HostSnapshot $activeHost; $activeHost=$null
            $http.Dispose(); $handler.Dispose()
        }
    }
} catch {
    $failure=$_.ToString(); Write-Host $failure
} finally {
    if ($activeHost) { Stop-HostSnapshot $activeHost }
    $files = @(Get-ChildItem -LiteralPath $runRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](packages|http-cache|plugins-cache|dotnet-home|temp|xcode-source)[\\/]' } | ForEach-Object { [ordered]@{ path=[IO.Path]::GetRelativePath($runRoot,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $receipt = [ordered]@{ issue=261; utc=[DateTime]::UtcNow.ToString('o'); runRoot=$runRoot; runtimeRoot=$runtimeRoot; failure=$failure; sourceCommit=$sourceCommit; sourceInputs=$sourceInputs; projectReferences=1; sonnetProjectReferences=0; baselineOnly=[bool]$BaselineOnly; globalCacheChanged=$false; patchSha256=if (!$BaselineOnly -and (Test-Path -LiteralPath $patchPath)) { (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash } else {$null}; packages=$packages.ToArray(); bins=$bins.ToArray(); commands=$commands.ToArray(); hosts=$hosts.ToArray(); hostSource=$hostManifest.productionSource; hostManifestSha256=if ($ServerManifest) {(Get-FileHash -LiteralPath $ServerManifest -Algorithm SHA256).Hash} else {$null}; files=$files }
    [IO.File]::WriteAllText((Join-Path $runRoot 'receipt.json'),($receipt | ConvertTo-Json -Depth 12),$utf8)
    Write-Host ('Evidence: ' + (Join-Path $runRoot 'receipt.json'))
}
if ($failure) { exit 1 }
