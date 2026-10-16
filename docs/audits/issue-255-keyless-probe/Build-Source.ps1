param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$RuntimeDirectory)
$env:DOTNET_PROCESSOR_COUNT = '2'
. (Join-Path $PSScriptRoot 'Common.ps1') -OutputDirectory $OutputDirectory -RuntimeDirectory $RuntimeDirectory -UseLocalProxy
try {
    Invoke-Captured 'head' 'git' @('rev-parse','HEAD') $SourceRoot 30
    $sourceCommit = (Get-Content -LiteralPath (Join-Path $runRoot 'head.stdout.log') -Raw).Trim()
    if ($sourceCommit -ne 'b157dddc2886127e2deddb22a75555a3883e687d') { throw 'Unexpected candidate source.' }
    Invoke-Captured 'submodules' 'git' @('-c','http.proxy=http://127.0.0.1:7890','submodule','update','--init') $SourceRoot 180
    Invoke-Captured 'submodule-status' 'git' @('submodule','status') $SourceRoot 30
    $properties = @('-m:1','-nr:false','-p:UseSharedCompilation=false','-p:NuGetAudit=false')
    foreach ($project in @('SonnetDB.Data','SonnetDB')) {
        $path = 'src/' + $project + '/' + $project + '.csproj'
        Invoke-Captured ($project + '-restore') 'dotnet' (@('restore',$path) + $properties) $SourceRoot 240
        Invoke-Captured ($project + '-build') 'dotnet' (@('build',$path,'-c','Release','--no-restore') + $properties) $SourceRoot 240
    }
    $snapshot = Join-Path $runRoot 'server'
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'src/SonnetDB/bin/Release/net10.0') -Destination $snapshot -Recurse
    $client = Join-Path $runRoot 'client'
    [IO.Directory]::CreateDirectory($client) | Out-Null
    foreach ($name in @('SonnetDB.Core','SonnetDB.Data')) { Copy-Item -LiteralPath (Join-Path $SourceRoot ('src/' + $name + '/bin/Release/net10.0/' + $name + '.dll')) -Destination $client }
    Invoke-Captured 'tracked' 'git' @('ls-files','src','Directory.Build.props','Directory.Packages.props') $SourceRoot 30
    $sources = @(Get-Content -LiteralPath (Join-Path $runRoot 'tracked.stdout.log') | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $SourceRoot $_) -PathType Leaf) } | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $SourceRoot $_) -Algorithm SHA256).Hash} })
    $binaries = @(foreach ($directory in @($snapshot,$client)) { Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($runRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} } })
} catch { $failure = $_.ToString(); Write-Host $failure }
finally {
    $receipt = [ordered]@{issue=255;utc=[DateTime]::UtcNow.ToString('o');sourceCommit=$sourceCommit;sourceOnly=$true;failure=$failure;commands=$commands.ToArray();sources=$sources;files=$binaries}
    [IO.File]::WriteAllText((Join-Path $runRoot 'source-manifest.json'),($receipt | ConvertTo-Json -Depth 8),$utf8)
}
if ($failure) { exit 1 }
