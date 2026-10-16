param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$XCodeSource,
    [switch]$UseLocalProxy
)
$env:DOTNET_PROCESSOR_COUNT = '2'
. (Join-Path $PSScriptRoot 'Run.ps1') -OutputDirectory $OutputDirectory -RuntimeDirectory $RuntimeDirectory -UseLocalProxy:$UseLocalProxy -FunctionsOnly
$sourceRoot = Join-Path $runRoot 'xcode-source'
$sourceCommit = '757fa2a078906f87783e55fe0e9d5f54a9e916b9'
$patchPath = Join-Path $runRoot 'XCodeMetadata.patch'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'XCodeMetadata.patch') -Destination $patchPath
try {
    Invoke-Captured 'clone' 'git' @('clone', '--no-hardlinks', $XCodeSource, $sourceRoot) $runRoot 90
    Invoke-Captured 'xcode-head' 'git' @('rev-parse', 'HEAD') $sourceRoot 30
    if ((Get-Content -LiteralPath (Join-Path $runRoot 'xcode-head.stdout.log') -Raw -Encoding utf8).Trim() -ne $sourceCommit) { throw 'XCode source changed.' }
    $consumer = Join-Path $runRoot 'consumer'
    $generated = Join-Path $runRoot 'generated'
    [IO.Directory]::CreateDirectory($generated) | Out-Null
    foreach ($directory in @($sourceRoot, $consumer, $generated)) {
        foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $directory $name)
        }
    }
    foreach ($name in @('GenerateConsumer.csproj', 'GenerateProgram.cs.txt')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $consumer $name) }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'GeneratedEntities.csproj') -Destination (Join-Path $generated 'GeneratedEntities.csproj')
    Invoke-Captured 'patch-check' 'git' @('apply', '--ignore-space-change', '--check', $patchPath) $sourceRoot 30
    Invoke-Captured 'patch-apply' 'git' @('apply', '--ignore-space-change', $patchPath) $sourceRoot 30
    $modelHelper = Join-Path $sourceRoot 'XCode/DataAccessLayer/Model/ModelHelper.cs'
    [IO.File]::WriteAllText($modelHelper, [IO.File]::ReadAllText($modelHelper).Replace("`r`n", "`n").Replace("`n", "`r`n"), $utf8)
    Invoke-Captured 'candidate-diff' 'git' @('diff', '--', 'XCode/DataAccessLayer/Database/SonnetDB.cs', 'XCode/DataAccessLayer/Model/ModelHelper.cs') $sourceRoot 30
    $properties = @(('-p:XCodeSourceRoot=' + $sourceRoot), '-p:DisableImplicitLibraryPacksFolder=true', '-p:NuGetAudit=false', '-p:RestoreFallbackFolders=', '-p:RestoreAdditionalProjectFallbackFolders=', '-p:UseSharedCompilation=false', '-m:1', '-nr:false')
    Invoke-Captured 'sdk' 'dotnet' @('--info') $consumer 45
    Invoke-Captured 'consumer-restore' 'dotnet' (@('restore', 'GenerateConsumer.csproj', '--configfile', (Join-Path $consumer 'NuGet.Config'), '--packages', (Join-Path $runRoot 'packages'), '--no-http-cache') + $properties) $consumer 240
    $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Encoding utf8 -Raw | ConvertFrom-Json
    $projects = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' })
    $sources = @($assets.project.restore.sources.PSObject.Properties.Name)
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    if ($projects.Count -ne 1 -or $projects[0].Name -notlike 'NewLife.XCode/*') { throw 'Only fixed XCode source reference is allowed.' }
    if ($sources.Count -ne 1 -or $sources[0] -ne 'https://api.nuget.org/v3/index.json' -or $folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne (Join-Path $runRoot 'packages')) { throw 'Unexpected restore source/cache.' }
    Invoke-Captured 'consumer-build' 'dotnet' (@('build', 'GenerateConsumer.csproj', '-c', 'Release', '--no-restore') + $properties) $consumer 300
    $bin = Join-Path $consumer 'bin/Release/net10.0'
    foreach ($id in @('sonnetdb', 'sonnetdb.core')) {
        $packageRoot = Join-Path $runRoot ('packages/' + $id + '/4.5.0-preview.ef266.5')
        $hash = (Get-FileHash -LiteralPath (Join-Path $packageRoot ($id + '.4.5.0-preview.ef266.5.nupkg')) -Algorithm SHA256).Hash
        $expected = @{ sonnetdb = '0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4'; 'sonnetdb.core' = '10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268' }
        $metadata = Get-Content -LiteralPath (Join-Path $packageRoot '.nupkg.metadata') -Raw -Encoding utf8 | ConvertFrom-Json
        if ($hash -ne $expected[$id] -or $metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw 'Unexpected public package.' }
        $dlls = foreach ($file in Get-ChildItem -LiteralPath (Join-Path $packageRoot 'lib/net10.0') -Filter '*.dll') {
            $packageHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $runtimeHash = (Get-FileHash -LiteralPath (Join-Path $bin $file.Name) -Algorithm SHA256).Hash
            if ($packageHash -ne $runtimeHash) { throw 'Public/runtime DLL mismatch.' }
            [ordered]@{ name = $file.Name; sha256 = $runtimeHash }
        }
        $packages.Add([ordered]@{ id = $id; sha256 = $hash; source = $metadata.source; dlls = @($dlls) })
    }
    $dll = Join-Path $bin 'GenerateConsumer.dll'
    Invoke-Captured 'generate' 'dotnet' @($dll, 'generate', (Join-Path $runtimeRoot 'database'), $generated) $consumer 120
    Invoke-Captured 'entities-restore' 'dotnet' (@('restore', 'GeneratedEntities.csproj', '--configfile', (Join-Path $generated 'NuGet.Config'), '--packages', (Join-Path $runRoot 'packages')) + $properties) $generated 180
    Invoke-Captured 'entities-build' 'dotnet' (@('build', 'GeneratedEntities.csproj', '-c', 'Release', '--no-restore') + $properties) $generated 240
    Invoke-Captured 'verify' 'dotnet' @($dll, 'verify', (Join-Path $generated 'models.xml'), (Join-Path $generated 'bin/Release/net10.0/GeneratedEntities.dll')) $consumer 90
} catch { $failure = $_.ToString(); Write-Host $failure }
finally {
    $files = @(Get-ChildItem -LiteralPath $runRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](packages|http-cache|plugins-cache|dotnet-home|temp|xcode-source)[\\/]' } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($runRoot, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $receipt = [ordered]@{ issue = 261; utc = [DateTime]::UtcNow.ToString('o'); runRoot = $runRoot; runtimeRoot = $runtimeRoot; sourceCommit = $sourceCommit; failure = $failure; projectReferences = 1; sonnetProjectReferences = 0; patchSha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash; packages = $packages.ToArray(); commands = $commands.ToArray(); files = $files }
    [IO.File]::WriteAllText((Join-Path $runRoot 'receipt.json'), ($receipt | ConvertTo-Json -Depth 10), $utf8)
}
if ($failure) { exit 1 }
