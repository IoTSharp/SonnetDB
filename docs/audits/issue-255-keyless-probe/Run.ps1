param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$XCodeSource,
    [Parameter(Mandatory)][string]$SourceBuild,
    [switch]$SkipPublic,
    [switch]$OriginalNullBehavior,
    [int]$RestPort=43890,
    [int]$FramePort=43891
)
$env:DOTNET_PROCESSOR_COUNT='2'
. (Join-Path $PSScriptRoot 'Common.ps1') -OutputDirectory $OutputDirectory -RuntimeDirectory $RuntimeDirectory -UseLocalProxy -RestPort $RestPort -FramePort $FramePort
$sourceRoot=Join-Path $runRoot 'xcode-source'
$sourceManifest=Join-Path $SourceBuild 'source-manifest.json'
$referenceRoot=Join-Path $SourceBuild 'client'
$ServerSnapshot=Join-Path $SourceBuild 'server'
$inputs=@()
function Build-Consumer([string]$Kind,[string]$Label,[string[]]$Properties) {
    $directory=Join-Path $runRoot $Kind
    Invoke-Captured ($Label+'-'+$Kind+'-restore') 'dotnet' (@('restore',($Kind+'Consumer.csproj'),'--configfile',(Join-Path $directory 'NuGet.Config'))+$Properties) $directory 180
    Copy-Item -LiteralPath (Join-Path $directory 'obj/project.assets.json') -Destination (Join-Path $runRoot ($Label+'-'+$Kind+'-assets.json'))
    Invoke-Captured ($Label+'-'+$Kind+'-build') 'dotnet' (@('build',($Kind+'Consumer.csproj'),'-c','Release','--no-restore')+$Properties) $directory 240
    $target=Join-Path $runRoot ($Label+'-'+$Kind+'-bin')
    Copy-Item -LiteralPath (Join-Path $directory 'bin/Release/net10.0') -Destination $target -Recurse
    return $target
}
function Assert-Public([string]$Version,[string]$Bin) {
    foreach($id in @('sonnetdb','sonnetdb.core')) {
        $root=Join-Path $runRoot ('packages/'+$id+'/'+$Version)
        $metadata=Get-Content -LiteralPath (Join-Path $root '.nupkg.metadata') -Raw -Encoding utf8|ConvertFrom-Json
        [xml]$nuspec=Get-Content -LiteralPath (Join-Path $root ($id+'.nuspec')) -Raw -Encoding utf8
        if($metadata.source -ne 'https://api.nuget.org/v3/index.json') {throw 'Unexpected package source.'}
        $dlls=@(Get-ChildItem -LiteralPath (Join-Path $root 'lib/net10.0') -Filter '*.dll'|ForEach-Object {
            $hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            if((Get-FileHash -LiteralPath (Join-Path $Bin $_.Name) -Algorithm SHA256).Hash -ne $hash) {throw 'Runtime public DLL mismatch.'}
            [ordered]@{name=$_.Name;sha256=$hash}
        })
        $packages.Add([ordered]@{id=$id;version=$Version;bin=$Bin;source=$metadata.source;sourceCommit=$nuspec.package.metadata.repository.commit;sha256=(Get-FileHash -LiteralPath (Join-Path $root ($id+'.'+$Version+'.nupkg')) -Algorithm SHA256).Hash;dlls=$dlls})
    }
}
function Assert-Candidate([string]$Bin) {
    foreach($file in Get-ChildItem -LiteralPath $referenceRoot -Filter '*.dll') {if((Get-FileHash -LiteralPath (Join-Path $Bin $file.Name) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {throw 'Source candidate DLL mismatch.'}}
}
function Run-Probe([string]$Kind,[string]$Bin,[string]$Label,[string]$Phase,[string]$Connection,[string]$Protocol,[string]$ModelDirectory) {
    $arguments=@((Join-Path $Bin ($Kind+'Consumer.dll')),$Phase,$Connection,$Protocol)
    if($Kind -eq 'XCode') {[IO.Directory]::CreateDirectory($ModelDirectory)|Out-Null;$arguments+=@($ModelDirectory)}
    Invoke-Captured $Label 'dotnet' $arguments (Join-Path $runRoot $Kind) 120
}
try {
    $executed=Join-Path $runRoot 'executed-probe'
    [IO.Directory]::CreateDirectory($executed)|Out-Null
    Get-ChildItem -LiteralPath $PSScriptRoot -File|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $executed}
    $hostManifest=Get-Content -LiteralPath $sourceManifest -Raw -Encoding utf8|ConvertFrom-Json
    if($hostManifest.failure -or $hostManifest.sourceCommit -ne 'b157dddc2886127e2deddb22a75555a3883e687d') {throw 'Invalid source build.'}
    foreach($file in $hostManifest.files) {if((Get-FileHash -LiteralPath (Join-Path $SourceBuild $file.path) -Algorithm SHA256).Hash -ne $file.sha256) {throw 'Source snapshot changed.'}}
    Invoke-Captured 'clone' 'git' @('clone','--no-hardlinks',$XCodeSource,$sourceRoot) $runRoot 90
    Invoke-Captured 'xcode-head' 'git' @('rev-parse','HEAD') $sourceRoot 30
    if((Get-Content -LiteralPath (Join-Path $runRoot 'xcode-head.stdout.log') -Raw).Trim() -ne '757fa2a078906f87783e55fe0e9d5f54a9e916b9') {throw 'Unexpected XCode head.'}
    $inputs=@(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'XCode') -File -Recurse|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($sourceRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    foreach($kind in @('Native','XCode')) {
        $dir=Join-Path $runRoot $kind
        [IO.Directory]::CreateDirectory($dir)|Out-Null
        foreach($file in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config',($kind+'Consumer.csproj'),($kind+'Program.cs.txt'))) {Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $dir}
    }
    foreach($file in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config')) {Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $sourceRoot}
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'Directory.Build.targets'),'<Project><PropertyGroup><TargetFrameworks>netstandard2.1</TargetFrameworks></PropertyGroup></Project>',$utf8)
    $properties=@('-m:1','-nr:false','-p:UseSharedCompilation=false','-p:DisableImplicitLibraryPacksFolder=true','-p:NuGetAudit=false','-p:RestoreFallbackFolders=','-p:RestoreAdditionalProjectFallbackFolders=',('-p:XCodeSourceRoot='+$sourceRoot))
    foreach($version in $(if($SkipPublic) {@()} else {@('4.0.0','4.5.0-preview.ef266.5')})) {
        $label=if($version -eq '4.0.0') {'stable'} else {'preview'}
        foreach($kind in @('Native','XCode')) {
            $bin=Build-Consumer $kind $label ($properties+@('-p:PublicVersion='+$version))
            Assert-Public $version $bin
            Run-Probe $kind $bin ($label+'-'+$kind+'-negative') 'negative' ('Data Source='+ (Join-Path $runtimeRoot ($label+'-'+$kind))) 'embedded' (Join-Path $runtimeRoot ($label+'-'+$kind+'-models'))
        }
    }
    $candidateProperties=$properties+@('-p:RuntimeReferenceRoot='+$referenceRoot)
    $original=Build-Consumer 'XCode' 'source-original' $candidateProperties
    Assert-Candidate $original
    Run-Probe 'XCode' $original 'source-original-metadata' 'source-original' ('Data Source='+(Join-Path $runtimeRoot 'original-source')) 'embedded' (Join-Path $runtimeRoot 'original-models')
    $patch=Join-Path $runRoot 'XCodeMetadata.patch'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'XCodeMetadata.patch') -Destination $patch
    Invoke-Captured 'patch-check' 'git' @('apply','--ignore-space-change','--check',$patch) $sourceRoot 30
    Invoke-Captured 'patch-apply' 'git' @('apply','--ignore-space-change',$patch) $sourceRoot 30
    if (!$OriginalNullBehavior) {
        $nullPatch=Join-Path $runRoot 'XCodeNulls.patch'
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'XCodeNulls.patch') -Destination $nullPatch
        Invoke-Captured 'null-patch-check' 'git' @('apply','--ignore-space-change','--check',$nullPatch) $sourceRoot 30
        Invoke-Captured 'null-patch-apply' 'git' @('apply','--ignore-space-change',$nullPatch) $sourceRoot 30
    }
    $helper=Join-Path $sourceRoot 'XCode/DataAccessLayer/Model/ModelHelper.cs'
    [IO.File]::WriteAllText($helper,[IO.File]::ReadAllText($helper).Replace("`r`n","`n").Replace("`n","`r`n"),$utf8)
    Invoke-Captured 'xcode-candidate-diff' 'git' @('diff','--','XCode') $sourceRoot 30
    $bins=@{}
    foreach($kind in @('Native','XCode')) {
        $bins[$kind]=Build-Consumer $kind 'candidate' $candidateProperties
        Assert-Candidate $bins[$kind]
        foreach($phase in @('initial','reopen')) {Run-Probe $kind $bins[$kind] ($kind+'-embedded-'+$phase) $phase ('Data Source='+(Join-Path $runtimeRoot ($kind+'-embedded'))) 'embedded' (Join-Path $runtimeRoot ($kind+'-embedded-models'))}
    }
    foreach($phase in @('core-initial','core-reopen')) {Run-Probe 'Native' $bins.Native $phase $phase (Join-Path $runtimeRoot 'core') 'core' ''}
    $content=Join-Path $runRoot 'host-content'
    [IO.Directory]::CreateDirectory($content)|Out-Null
    $settings=@{SonnetDBServer=@{DataRoot=(Join-Path $runtimeRoot 'server-data');Tokens=@{'issue255-local-acceptance'='admin'};AutoLoadExistingDatabases=$true;AllowAnonymousProbes=$true;Mqtt=@{Enabled=$false}};Kestrel=@{Endpoints=@{Rest=@{Url="http://127.0.0.1:$RestPort";Protocols='Http1'};Frame=@{Url="http://127.0.0.1:$FramePort";Protocols='Http2'}}};Logging=@{LogLevel=@{Default='Information';'Microsoft.AspNetCore.Hosting.Diagnostics'='Information'}}}
    [IO.File]::WriteAllText((Join-Path $content 'appsettings.json'),($settings|ConvertTo-Json -Depth 10),$utf8)
    $handler=[Net.Http.HttpClientHandler]::new();$handler.UseProxy=$false
    $http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromSeconds(2)
    $activeHost=Start-HostSnapshot 1
    foreach($kind in @('Native','XCode')) {foreach($protocol in @('rest','frame-http2')) {
        $database=$kind.ToLowerInvariant()+'_'+$protocol.Replace('-','_')
        $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"http://127.0.0.1:$RestPort/v1/db")
        $request.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer','issue255-local-acceptance')
        $request.Content=[Net.Http.StringContent]::new(('{"name":"'+$database+'"}'),$utf8,'application/json')
        $response=$http.Send($request);$response.EnsureSuccessStatusCode()|Out-Null;$response.Dispose();$request.Dispose()
        $port=if($protocol -eq 'rest') {$RestPort} else {$FramePort}
        $connection="Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=issue255-local-acceptance;Protocol=$protocol;Timeout=15"
        Run-Probe $kind $bins[$kind] ($kind+'-'+$protocol+'-initial') 'initial' $connection $protocol (Join-Path $runtimeRoot ($database+'-models'))
    }}
    Stop-HostSnapshot $activeHost;$activeHost=$null
    $activeHost=Start-HostSnapshot 2
    foreach($kind in @('Native','XCode')) {foreach($protocol in @('rest','frame-http2')) {
        $database=$kind.ToLowerInvariant()+'_'+$protocol.Replace('-','_');$port=if($protocol -eq 'rest') {$RestPort} else {$FramePort}
        $connection="Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=issue255-local-acceptance;Protocol=$protocol;Timeout=15"
        Run-Probe $kind $bins[$kind] ($kind+'-'+$protocol+'-reopen') 'reopen' $connection $protocol (Join-Path $runtimeRoot ($database+'-models'))
    }}
    Stop-HostSnapshot $activeHost;$activeHost=$null
    $http.Dispose();$handler.Dispose()
} catch {$failure=$_.ToString();Write-Host $failure}
finally {
    if($activeHost) {Stop-HostSnapshot $activeHost}
    $files=@(Get-ChildItem -LiteralPath $runRoot -File -Recurse|Where-Object {$_.FullName -notmatch '[\\/](packages|http-cache|plugins-cache|dotnet-home|temp|xcode-source)[\\/]'}|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($runRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    $receipt=[ordered]@{issue=255;utc=[DateTime]::UtcNow.ToString('o');failure=$failure;skipPublic=[bool]$SkipPublic;sourceCommit=$hostManifest.sourceCommit;sourceManifestSha256=(Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash;xcodeCommit='757fa2a078906f87783e55fe0e9d5f54a9e916b9';xcodeOriginalSources=$inputs;metadataDependency=261;metadataPatchSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'XCodeMetadata.patch') -Algorithm SHA256).Hash;packages=$packages.ToArray();commands=$commands.ToArray();hosts=$hosts.ToArray();files=$files}
    $receipt['originalNullBehavior']=[bool]$OriginalNullBehavior
    $receipt['nullPatchSha256']=if ($OriginalNullBehavior) {$null} else {(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'XCodeNulls.patch') -Algorithm SHA256).Hash}
    [IO.File]::WriteAllText((Join-Path $runRoot 'receipt.json'),($receipt|ConvertTo-Json -Depth 10),$utf8)
}
if($failure) {exit 1}
