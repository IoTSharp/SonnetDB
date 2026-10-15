#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$Proxy,
    [string]$ServerDirectory,
    [string]$ServerSourceCommit,
    [string]$RuntimeDirectory
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$script:Evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $script:Evidence) { throw 'Use a new evidence directory; previous receipts must remain unchanged.' }
[IO.Directory]::CreateDirectory($script:Evidence) | Out-Null
[IO.File]::WriteAllText((Join-Path $script:Evidence '.owner'), [Guid]::NewGuid().ToString('N'))
$script:SourceRoot = Join-Path $script:Evidence 'consumer'
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName)
    if ($relative -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
    $target = Join-Path $script:SourceRoot $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
$packages = Join-Path $script:Evidence 'packages'
$cache = Join-Path $script:Evidence 'http-cache'
$temporary = if ($RuntimeDirectory) { [IO.Path]::GetFullPath($RuntimeDirectory) } else { Join-Path $script:Evidence 'temp' }
if (Test-Path -LiteralPath $temporary) { throw 'Use a new runtime directory.' }
[IO.Directory]::CreateDirectory($temporary) | Out-Null
[IO.File]::WriteAllText((Join-Path $temporary '.owner'), [Guid]::NewGuid().ToString('N'))
Write-Receipt (Join-Path $script:Evidence 'runtime.json') @{ path = $temporary }
$environment = @{ NUGET_PACKAGES = $packages; NUGET_HTTP_CACHE_PATH = $cache; NUGET_FALLBACK_PACKAGES = ''; TEMP = $temporary; TMP = $temporary; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; NO_PROXY = 'localhost,127.0.0.1,::1' }
if ($Proxy) { $environment.HTTP_PROXY = $Proxy; $environment.HTTPS_PROXY = $Proxy }
try {
    Invoke-Recorded 'sdk' @('--info') -Environment $environment
    foreach ($project in @('Original', 'Matrix')) {
        $path = Join-Path $script:SourceRoot "$project/$project.csproj"
        Invoke-Recorded "$project-restore" @('restore', $path, '--configfile', (Join-Path $PSScriptRoot 'NuGet.Config'), '--packages', $packages, '--locked-mode', '--force', '--no-cache') -TimeoutSeconds 300 -Environment $environment
        Invoke-Recorded "$project-build" @('build', $path, '-c', 'Release', '--no-restore', '-p:UseSharedCompilation=false') -Environment $environment
    }
    Save-PackageEvidence $packages @((Join-Path $script:SourceRoot 'Original/obj/project.assets.json'), (Join-Path $script:SourceRoot 'Matrix/obj/project.assets.json'))
    $original = Join-Path $script:SourceRoot 'Original/bin/Release/net10.0/Original.dll'
    $matrix = Join-Path $script:SourceRoot 'Matrix/bin/Release/net10.0/Matrix.dll'
    foreach ($provider in @('Sqlite', 'SonnetDB')) {
        foreach ($case in @('binary-key', 'passkey-any')) {
            Invoke-Recorded "original-$provider-$case" @($original, $provider, $case) -TimeoutSeconds 40 -WorkingDirectory $script:Evidence -Environment $environment
        }
        $data = Join-Path $temporary "matrix-$provider-data"
        [IO.Directory]::CreateDirectory($data) | Out-Null
        [IO.File]::WriteAllText((Join-Path $data '.owner'), [Guid]::NewGuid().ToString('N'))
        foreach ($phase in @('exercise', 'reopen')) {
            Invoke-Recorded "matrix-$provider-$phase" @($matrix, $provider, $phase, $data) -TimeoutSeconds 140 -Environment $environment
        }
    }
    if ($ServerDirectory) {
        if (!$ServerSourceCommit) { throw 'Record the source commit of the supplied Server build.' }
        & (Join-Path $PSScriptRoot 'Remote.ps1') -EvidenceDirectory (Join-Path $script:Evidence 'remote') -ServerDirectory $ServerDirectory -ServerSourceCommit $ServerSourceCommit -MatrixAssembly $matrix -RuntimeDirectory (Join-Path $temporary 'remote')
    }
}
finally { Save-Manifest $PSScriptRoot }
