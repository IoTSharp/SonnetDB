param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$XCodeCandidateBin,
    [Parameter(Mandatory)][string]$SonnetSource,
    [Parameter(Mandatory)][string]$SourceManifest
)
$env:DOTNET_PROCESSOR_COUNT = '2'
. (Join-Path $PSScriptRoot 'Run.ps1') -OutputDirectory $OutputDirectory -RuntimeDirectory $RuntimeDirectory -FunctionsOnly
$references = Join-Path $runRoot 'references'
[IO.Directory]::CreateDirectory($references) | Out-Null
$manifest = Get-Content -LiteralPath $SourceManifest -Encoding utf8 -Raw | ConvertFrom-Json
try {
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $SonnetSource $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Frozen source changed.' }
    }
    foreach ($file in Get-ChildItem -LiteralPath $XCodeCandidateBin -Filter '*.dll') {
        if ($file.Name -notin @('GenerateConsumer.dll', 'SonnetDB.Core.dll', 'SonnetDB.Data.dll')) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $references $file.Name) }
    }
    foreach ($name in @('SonnetDB.Core', 'SonnetDB.Data')) {
        $path = 'src/' + $name + '/bin/Release/net10.0/' + $name + '.dll'
        $expected = $manifest.bins | Where-Object path -eq $path
        if ((Get-FileHash -LiteralPath (Join-Path $SonnetSource $path) -Algorithm SHA256).Hash -ne $expected.sha256) { throw 'Frozen source DLL changed.' }
        Copy-Item -LiteralPath (Join-Path $SonnetSource $path) -Destination (Join-Path $references ($name + '.dll'))
    }
    $consumer = Join-Path $runRoot 'consumer'
    foreach ($name in @('SourceConcatConsumer.csproj', 'SourceConcatProgram.cs.txt', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $consumer $name) }
    $properties = @(('-p:RuntimeReferenceRoot=' + $references), '-p:UseSharedCompilation=false', '-m:1', '-nr:false')
    Invoke-Captured 'restore' 'dotnet' (@('restore', 'SourceConcatConsumer.csproj', '--configfile', (Join-Path $consumer 'NuGet.Config')) + $properties) $consumer 90
    Invoke-Captured 'build' 'dotnet' (@('build', 'SourceConcatConsumer.csproj', '-c', 'Release', '--no-restore') + $properties) $consumer 120
    $bin = Join-Path $consumer 'bin/Release/net10.0'
    foreach ($file in Get-ChildItem -LiteralPath $references -Filter '*.dll') {
        if ((Get-FileHash -LiteralPath (Join-Path $bin $file.Name) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw 'Runtime DLL mismatch.' }
    }
    foreach ($phase in @('initial', 'reopen')) { Invoke-Captured $phase 'dotnet' @((Join-Path $bin 'SourceConcatConsumer.dll'), $phase, (Join-Path $runtimeRoot 'database')) $consumer 90 }
} catch { $failure = $_.ToString(); Write-Host $failure }
finally {
    $files = @(Get-ChildItem -LiteralPath $runRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](packages|http-cache|plugins-cache|dotnet-home|temp)[\\/]' } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($runRoot, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $receipt = [ordered]@{ issue = 261; utc = [DateTime]::UtcNow.ToString('o'); sourceCandidateOnly = $true; publicPackageAcceptance = $false; failure = $failure; sourceManifestSha256 = (Get-FileHash -LiteralPath $SourceManifest -Algorithm SHA256).Hash; commands = $commands.ToArray(); files = $files }
    [IO.File]::WriteAllText((Join-Path $runRoot 'receipt.json'), ($receipt | ConvertTo-Json -Depth 10), $utf8)
}
if ($failure) { exit 1 }
