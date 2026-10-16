param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$ChloeSource
)
$env:DOTNET_PROCESSOR_COUNT='2'
. (Join-Path $PSScriptRoot 'Common.ps1') -OutputDirectory $OutputDirectory -RuntimeDirectory $RuntimeDirectory -UseLocalProxy
$sourceRoot=Join-Path $runRoot 'chloe-source'
$consumer=Join-Path $runRoot 'consumer'
$sourceInputs=@()
try {
    $executed=Join-Path $runRoot 'executed-probe'
    [IO.Directory]::CreateDirectory($executed)|Out-Null
    Get-ChildItem -LiteralPath $PSScriptRoot -File|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $executed}
    Invoke-Captured 'clone' 'git' @('clone','--no-hardlinks',$ChloeSource,$sourceRoot) $runRoot 90
    Invoke-Captured 'chloe-head' 'git' @('rev-parse','HEAD') $sourceRoot 30
    if((Get-Content -LiteralPath (Join-Path $runRoot 'chloe-head.stdout.log') -Raw -Encoding utf8).Trim() -ne 'bb7ec8735bd4511c35453eb009f70cc606795703') {throw 'Unexpected original Chloe head.'}
    $sourceInputs=@(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src/Chloe'),(Join-Path $sourceRoot 'src/Chloe.SonnetDB') -File -Recurse|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($sourceRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $sourceRoot
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $consumer
    }
    foreach($name in @('Consumer.csproj','Program.cs.txt')) {Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $consumer}
    $properties=@(('-p:ChloeSourceRoot='+$sourceRoot),'-p:TargetFrameworks=net10.0','-p:DisableImplicitLibraryPacksFolder=true','-p:NuGetAudit=false','-p:RestoreFallbackFolders=','-p:RestoreAdditionalProjectFallbackFolders=','-p:UseSharedCompilation=false','-nr:false','-m:1')
    foreach($version in @('4.0.0','4.5.0-preview.ef266.5')) {
        $label=if($version -eq '4.0.0') {'stable'} else {'preview'}
        $versionProperties=$properties+@('-p:PublicVersion='+$version)
        Invoke-Captured ($label+'-restore') 'dotnet' (@('restore','Consumer.csproj','--configfile',(Join-Path $consumer 'NuGet.Config'))+$versionProperties) $consumer 180
        $assetsPath=Join-Path $consumer 'obj/project.assets.json'
        Copy-Item -LiteralPath $assetsPath -Destination (Join-Path $runRoot ($label+'-assets.json'))
        $assets=Get-Content -LiteralPath $assetsPath -Raw -Encoding utf8|ConvertFrom-Json
        $projects=@($assets.libraries.PSObject.Properties|Where-Object {$_.Value.type -eq 'project'}|ForEach-Object Name)
        if($projects.Count -ne 2 -or $projects -notcontains 'Chloe.SonnetDB/5.56.0' -or $projects -notcontains 'Chloe/5.56.0') {throw 'Unexpected project dependency.'}
        Invoke-Captured ($label+'-build') 'dotnet' (@('build','Consumer.csproj','-c','Release','--no-restore')+$versionProperties) $consumer 240
        $bin=Join-Path $runRoot ($label+'-bin')
        Copy-Item -LiteralPath (Join-Path $consumer 'bin/Release/net10.0') -Destination $bin -Recurse
        foreach($id in @('sonnetdb','sonnetdb.core')) {
            $packageRoot=Join-Path $runRoot ('packages/'+$id+'/'+$version)
            $metadata=Get-Content -LiteralPath (Join-Path $packageRoot '.nupkg.metadata') -Raw -Encoding utf8|ConvertFrom-Json
            [xml]$nuspec=Get-Content -LiteralPath (Join-Path $packageRoot ($id+'.nuspec')) -Raw -Encoding utf8
            if($metadata.source -ne 'https://api.nuget.org/v3/index.json') {throw 'Unexpected package source.'}
            $dlls=@(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'lib/net10.0') -Filter '*.dll'|ForEach-Object {
                $hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                if((Get-FileHash -LiteralPath (Join-Path $bin $_.Name) -Algorithm SHA256).Hash -ne $hash) {throw 'Loaded DLL differs from public package.'}
                [ordered]@{name=$_.Name;sha256=$hash}
            })
            $packages.Add([ordered]@{id=$id;version=$version;source=$metadata.source;sourceCommit=$nuspec.package.metadata.repository.commit;sha256=(Get-FileHash -LiteralPath (Join-Path $packageRoot ($id+'.'+$version+'.nupkg')) -Algorithm SHA256).Hash;dlls=$dlls})
        }
        foreach($phase in @('initial','reopen')) {
            Invoke-Captured ($label+'-'+$phase) 'dotnet' @((Join-Path $bin 'Consumer.dll'),$phase,('Data Source='+(Join-Path $runtimeRoot ($label+'-db'))),'embedded') $consumer 90
        }
    }
    foreach($file in $sourceInputs) {if((Get-FileHash -LiteralPath (Join-Path $sourceRoot $file.path) -Algorithm SHA256).Hash -ne $file.sha256) {throw 'Original Chloe production source changed.'}}
} catch {$failure=$_.ToString();Write-Host $failure}
finally {
    $files=@(Get-ChildItem -LiteralPath $runRoot -File -Recurse|Where-Object {$_.FullName -notmatch '[\\/](packages|http-cache|plugins-cache|dotnet-home|temp|chloe-source)[\\/]'}|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($runRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    $receipt=[ordered]@{issue=251;utc=[DateTime]::UtcNow.ToString('o');failure=$failure;kind='original-Chloe/public-SonnetDB negative baseline';chloeSource='bb7ec8735bd4511c35453eb009f70cc606795703';chloeProductionChanges=0;sonnetDbProjectReferences=0;sourceInputs=$sourceInputs;packages=$packages.ToArray();commands=$commands.ToArray();files=$files}
    [IO.File]::WriteAllText((Join-Path $runRoot 'receipt.json'),($receipt|ConvertTo-Json -Depth 10),$utf8)
}
if($failure) {exit 1}
