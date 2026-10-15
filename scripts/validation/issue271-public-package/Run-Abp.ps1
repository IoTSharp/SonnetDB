#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceDirectory, [string]$Proxy, [string]$RuntimeDirectory)
. (Join-Path $PSScriptRoot 'Common.ps1')
$script:SourceRoot = $PSScriptRoot
$script:Evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $script:Evidence) { throw 'Use a new evidence directory.' }
[IO.Directory]::CreateDirectory($script:Evidence) | Out-Null
[IO.File]::WriteAllText((Join-Path $script:Evidence '.owner'), [Guid]::NewGuid().ToString('N'))
$checkout = Join-Path $script:Evidence 'abp-checkout'
$commit = '841567fd963621a8656a904121462234c6c80c0e'
$packages = Join-Path $script:Evidence 'packages'
$temporary = if ($RuntimeDirectory) { [IO.Path]::GetFullPath($RuntimeDirectory) } else { Join-Path $script:Evidence 'temp' }
if (Test-Path -LiteralPath $temporary) { throw 'Use a new runtime directory.' }
[IO.Directory]::CreateDirectory($temporary) | Out-Null
[IO.File]::WriteAllText((Join-Path $temporary '.owner'), [Guid]::NewGuid().ToString('N'))
Write-Receipt (Join-Path $script:Evidence 'runtime.json') @{ path = $temporary }
$environment = @{ NUGET_PACKAGES = $packages; NUGET_HTTP_CACHE_PATH = (Join-Path $script:Evidence 'http-cache'); NUGET_FALLBACK_PACKAGES = ''; TEMP = $temporary; TMP = $temporary; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; NO_PROXY = 'localhost,127.0.0.1,::1' }
if ($Proxy) { $environment.HTTP_PROXY = $Proxy; $environment.HTTPS_PROXY = $Proxy }
try {
    Invoke-Recorded 'abp-clone' @('-c', 'core.longpaths=true', 'clone', '--no-checkout', '--filter=blob:none', 'https://github.com/IoTSharp/SonnetDB.Abp.git', $checkout) -Executable 'git' -Environment $environment
    Invoke-Recorded 'abp-checkout' @('-c', 'core.longpaths=true', 'checkout', '--detach', $commit) -Executable 'git' -WorkingDirectory $checkout -Environment $environment
    Invoke-Recorded 'abp-head' @('rev-parse', 'HEAD') -Executable 'git' -WorkingDirectory $checkout
    $actual = (Get-Content -LiteralPath (Join-Path $script:Evidence 'abp-head.stdout.log') -Raw).Trim()
    if ($actual -ne $commit) { throw 'ABP checkout identity mismatch.' }
    # Only restore isolation is added. The upstream application, modules, migrations and tests stay byte-for-byte unchanged.
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.targets') -Destination (Join-Path $checkout 'Directory.Build.targets')
    $project = Join-Path $checkout 'tests/SonnetDB.Abp.BookStore.Tests/SonnetDB.Abp.BookStore.Tests.csproj'
    Invoke-Recorded 'abp-sdk' @('--info') -WorkingDirectory $checkout -Environment $environment
    Invoke-Recorded 'abp-restore' @('restore', $project, '--configfile', (Join-Path $PSScriptRoot 'NuGet.Config'), '--packages', $packages, '--force', '--no-cache', '-p:NuGetAudit=false') -TimeoutSeconds 600 -WorkingDirectory $checkout -Environment $environment
    $assets = @(Get-ChildItem -LiteralPath $checkout -Filter project.assets.json -File -Recurse | Select-Object -ExpandProperty FullName)
    Save-PackageEvidence $packages $assets
    Invoke-Recorded 'abp-build' @('build', $project, '-c', 'Release', '--no-restore', '-p:UseSharedCompilation=false') -TimeoutSeconds 300 -WorkingDirectory $checkout -Environment $environment
    $node = (Get-Command node -ErrorAction Stop).Source
    $npm = Join-Path ([IO.Path]::GetDirectoryName($node)) 'node_modules/npm/bin/npm-cli.js'
    $environment.npm_config_cache = Join-Path $script:Evidence 'npm-cache'
    Invoke-Recorded 'abp-client-libraries' @($npm, 'ci', '--prefix', 'samples/BookStore/src/Acme.BookStore.Web', '--no-audit', '--no-fund') -Executable $node -TimeoutSeconds 180 -WorkingDirectory $checkout -Environment $environment
    Invoke-Recorded 'abp-copy-assets' @('eng/Copy-BookStoreAssets.cjs') -Executable $node -WorkingDirectory $checkout -Environment $environment
    foreach ($case in @('BookStoreSonnetPasskeyTests', 'BookStoreSonnetHttpJourneyTests')) {
        Invoke-Recorded $case @('test', $project, '-c', 'Release', '--no-restore', '--no-build', '--filter', "FullyQualifiedName~$case", '--logger', "trx;LogFileName=$case.trx", '--results-directory', $script:Evidence) -TimeoutSeconds 180 -WorkingDirectory $checkout -Environment $environment
    }
}
finally {
    if (Test-Path -LiteralPath (Join-Path $checkout '.git')) {
        Invoke-Recorded 'abp-final-diff' @('-c', 'core.longpaths=true', 'diff', '--exit-code', $commit, '--') -Executable 'git' -WorkingDirectory $checkout
        Invoke-Recorded 'abp-final-status' @('-c', 'core.longpaths=true', 'status', '--short', '--untracked-files=normal') -Executable 'git' -WorkingDirectory $checkout
        $paths = @('Directory.Packages.props', 'tests/SonnetDB.Abp.BookStore.Tests/BookStoreSonnetPasskeyTests.cs', 'tests/SonnetDB.Abp.BookStore.Tests/BookStoreSonnetHttpJourneyTests.cs')
        Write-Receipt (Join-Path $script:Evidence 'abp-source.json') @{ commit = $commit; files = @($paths | ForEach-Object { @{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $checkout $_) -Algorithm SHA256).Hash } }) }
    }
    Save-Manifest $PSScriptRoot
}
