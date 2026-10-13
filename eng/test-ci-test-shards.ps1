$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$owner = [Guid]::NewGuid().ToString('N')
$corePath = Join-Path $root ('.ci-tests-' + $owner + '-core.slnx')
$restPath = Join-Path $root ('.ci-tests-' + $owner + '-rest.slnx')
$sourcePath = Join-Path $root 'SonnetDB.slnx'
$before = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$created = [Collections.Generic.List[string]]::new()
try {
    $core = & (Join-Path $PSScriptRoot 'new-ci-test-solution.ps1') -Shard core -OutputPath $corePath
    $created.Add($corePath)
    $rest = & (Join-Path $PSScriptRoot 'new-ci-test-solution.ps1') -Shard rest -OutputPath $restPath
    $created.Add($restPath)
    [xml]$source = [IO.File]::ReadAllText($sourcePath)
    $original = @($source.SelectNodes('//Project') | ForEach-Object { [string]$_.Path })
    $combined = @($core.projectPaths) + @($rest.projectPaths)
    if ($combined.Count -ne $original.Count -or @(Compare-Object ($original | Sort-Object) ($combined | Sort-Object)).Count -ne 0) { throw 'Shard union must contain every original project exactly once.' }
    if (@($core.projectPaths | Where-Object { $_ -cin $rest.projectPaths }).Count -ne 0) { throw 'Shards must not overlap.' }
    if ($core.projectPaths.Count -ne 1 -or $core.projectPaths[0] -cne 'tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj') { throw 'Core shard identity changed.' }
    [xml]$coreDocument = [IO.File]::ReadAllText($corePath)
    [xml]$restDocument = [IO.File]::ReadAllText($restPath)
    if ($coreDocument.SelectNodes('//Project').Count -ne 1 -or $restDocument.SelectNodes('//Project').Count -ne $original.Count - 1) { throw 'Persisted shard coverage mismatch.' }
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'new-ci-test-solution.ps1') -Shard rest -OutputPath $corePath | Out-Null }
    catch { $rejected = $true }
    if (!$rejected) { throw 'Existing manifest must not be overwritten.' }
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -cne $before) { throw 'Source solution was modified.' }
    "CI shard coverage passed: core=1, rest=$($rest.projectPaths.Count), total=$($original.Count), overlap=0."
}
finally {
    foreach ($path in $created) {
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -cne $root -or ![IO.Path]::GetFileName($path).StartsWith('.ci-tests-' + $owner, [StringComparison]::Ordinal)) { throw 'Cleanup ownership mismatch.' }
        Remove-Item -LiteralPath $path
    }
}
