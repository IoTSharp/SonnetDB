#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [string]$Proxy,
    [string]$ServerDirectory,
    [string]$ServerSourceCommit
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$script:Evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
if ((Test-Path -LiteralPath $script:Evidence) -or (Test-Path -LiteralPath $runtime)) { throw 'Use new evidence and runtime directories.' }
foreach ($path in @($script:Evidence, $runtime)) {
    [IO.Directory]::CreateDirectory($path) | Out-Null
    [IO.File]::WriteAllText((Join-Path $path '.owner'), [Guid]::NewGuid().ToString('N'))
}
$script:SourceRoot = Join-Path $script:Evidence 'consumer'
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName)
    if ($relative -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
    $destination = Join-Path $script:SourceRoot $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$packages = Join-Path $script:Evidence 'packages'
$checkout = Join-Path $script:Evidence 'chloe'
$commit = 'bb7ec8735bd4511c35453eb009f70cc606795703'
$environment = @{
    NUGET_PACKAGES = $packages; NUGET_HTTP_CACHE_PATH = (Join-Path $script:Evidence 'http-cache'); NUGET_FALLBACK_PACKAGES = '';
    DOTNET_CLI_HOME = (Join-Path $script:Evidence 'cli-home'); TEMP = $runtime; TMP = $runtime; NUGET_SCRATCH = (Join-Path $runtime 'nuget-scratch');
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; NO_PROXY = 'localhost,127.0.0.1,::1';
}
if ($Proxy) { $environment.HTTP_PROXY = $Proxy; $environment.HTTPS_PROXY = $Proxy }
Write-Receipt (Join-Path $script:Evidence 'runtime.json') @{ path = $runtime }
try {
    Invoke-Recorded 'sdk' @('--info') -Environment $environment
    Invoke-Recorded 'chloe-clone' @('-c', 'core.longpaths=true', 'clone', '--no-checkout', '--filter=blob:none', 'https://github.com/maikebing/Chloe.git', $checkout) -Executable 'git' -Environment $environment
    Invoke-Recorded 'chloe-checkout' @('-c', 'core.longpaths=true', 'checkout', '--detach', $commit) -Executable 'git' -WorkingDirectory $checkout -Environment $environment
    foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        if (Test-Path -LiteralPath (Join-Path $checkout $name)) { throw "Review new upstream configuration: $name" }
        [IO.File]::WriteAllText((Join-Path $checkout $name), '<Project />')
    }
    $project = Join-Path $script:SourceRoot 'Probe/Probe.csproj'
    $properties = @("-p:ChloeRoot=$checkout", '-p:TargetFrameworks=net10.0', '-p:DisableImplicitLibraryPacksFolder=true', '-p:NuGetAudit=false', "-p:SourceRevisionId=$commit", "-p:RepositoryCommit=$commit")
    Invoke-Recorded 'restore' (@('restore', $project, '--configfile', (Join-Path $script:SourceRoot 'NuGet.Config'), '--packages', $packages, '--force', '--no-cache') + $properties) -TimeoutSeconds 300 -Environment $environment
    Save-PackageEvidence $packages @((Join-Path $script:SourceRoot 'Probe/obj/project.assets.json'))
    Invoke-Recorded 'build' (@('build', $project, '-c', 'Release', '--no-restore', '-p:UseSharedCompilation=false') + $properties) -TimeoutSeconds 300 -Environment $environment
    $assembly = Join-Path $script:SourceRoot 'Probe/bin/Release/net10.0/Probe.dll'
    foreach ($mode in @('ado', 'chloe')) {
        foreach ($phase in @('exercise', 'reopen')) {
            Invoke-Recorded "embedded-$mode-$phase" @($assembly, $mode, $phase, $runtime) -TimeoutSeconds 170 -Environment $environment
        }
    }
    if ($ServerDirectory) {
        if (!$ServerSourceCommit) { throw 'The source Server commit is required.' }
        & (Join-Path $PSScriptRoot 'Remote.ps1') -EvidenceDirectory (Join-Path $script:Evidence 'remote') -ServerDirectory $ServerDirectory -ServerSourceCommit $ServerSourceCommit -ProbeAssembly $assembly -RuntimeDirectory (Join-Path $runtime 'remote')
    }
}
finally {
    if (Test-Path -LiteralPath (Join-Path $checkout '.git')) {
        Invoke-Recorded 'chloe-final-diff' @('-c', 'core.longpaths=true', 'diff', '--exit-code', $commit, '--') -Executable 'git' -WorkingDirectory $checkout
        Invoke-Recorded 'chloe-final-status' @('-c', 'core.longpaths=true', 'status', '--short', '--untracked-files=normal') -Executable 'git' -WorkingDirectory $checkout
    }
    Save-Manifest $PSScriptRoot
}
