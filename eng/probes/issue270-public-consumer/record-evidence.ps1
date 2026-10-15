[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EmbeddedRun,
    [Parameter(Mandatory)][string]$RemoteRun,
    [Parameter(Mandatory)][string]$BookStoreRoot,
    [Parameter(Mandatory)][string]$BookStoreResults,
    [Parameter(Mandatory)][string]$BookStorePackages,
    [Parameter(Mandatory)][string]$ApplicationArchive,
    [Parameter(Mandatory)][string]$UpstreamProvenance,
    [Parameter(Mandatory)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$version = '4.5.0-preview.ef266.5'
$applicationCommit = '841567fd963621a8656a904121462234c6c80c0e'
$packageCommit = '1af23d8f299febfdf66b040ab3419980396acf46'
$expectedHashes = @{
    'sonnetdb.core' = '10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268'
    'sonnetdb' = '0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4'
    'sonnetdb.entityframeworkcore' = '9688BA682E160639681AF3E4882DE6A58F70CF289B42552987FA7CD85D87E125'
}
$assemblyPackages = @{'SonnetDB.Core' = 'sonnetdb.core'; 'SonnetDB.Data' = 'sonnetdb'; 'SonnetDB.EntityFrameworkCore' = 'sonnetdb.entityframeworkcore'}
$embedded = Get-Content -LiteralPath (Join-Path $EmbeddedRun 'validation-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$remote = Get-Content -LiteralPath (Join-Path $RemoteRun 'validation-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$application = Get-Content -LiteralPath (Join-Path $BookStoreResults 'bookstore-author-journey.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$provenance = Get-Content -LiteralPath $UpstreamProvenance -Raw -Encoding UTF8 | ConvertFrom-Json
$files = [Collections.Generic.List[object]]::new()
$consumerCases = [Collections.Generic.List[object]]::new()

function Record-File([string]$Path, [string]$Role) {
    $files.Add([pscustomobject]@{role = $Role; path = [IO.Path]::GetFullPath($Path); sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash})
}

foreach ($pair in @(@{root = $EmbeddedRun; manifest = $embedded}, @{root = $RemoteRun; manifest = $remote})) {
    $manifest = $pair.manifest
    if ($manifest.failure -or $manifest.cases -ne 102 -or $manifest.passedCases -ne 102 -or $manifest.failedCases -ne 0 -or
        @($manifest.steps | Where-Object { $_.exitCode -ne 0 -or $_.timedOut }).Count -gt 0) { throw 'Consumer result is incomplete or failed.' }
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $pair.root $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw "Consumer evidence changed: $($file.path)" }
    }
    foreach ($package in $manifest.packages) {
        if ($package.nupkgSha256 -ne $expectedHashes[$package.id] -or $package.repositoryCommit -ne $packageCommit -or $package.source -ne 'https://api.nuget.org/v3/index.json') { throw 'Public consumer package mismatch.' }
    }
    Record-File (Join-Path $pair.root 'validation-manifest.json') 'consumer-validation-manifest'
    foreach ($directory in Get-ChildItem -LiteralPath $pair.root -Directory | Where-Object { $_.Name -match '^(SonnetDB|Sqlite|rest|frame-http2)-' }) {
        $receiptPath = Join-Path $directory.FullName 'receipt.json'
        $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $receipt.Passed -or $receipt.FatalError) { throw 'Consumer receipt failed.' }
        foreach ($assembly in $receipt.Assemblies) {
            if ($assemblyPackages.ContainsKey($assembly.Name)) {
                $packageRoot = if ($manifest.privatePackages) { $manifest.privatePackages } else { $manifest.environment.NUGET_PACKAGES }
                $packageDll = Join-Path $packageRoot ($assemblyPackages[$assembly.Name] + '/' + $version + '/lib/net10.0/' + $assembly.Name + '.dll')
                if ((Get-FileHash -LiteralPath $packageDll -Algorithm SHA256).Hash -ne $assembly.Sha256 -or $assembly.InformationalVersion -ne "$version+$packageCommit") { throw 'Consumer runtime DLL differs from public package.' }
            }
        }
        $consumerCases.Add([pscustomobject]@{
            name = $directory.Name; passed = $receipt.Passed; cases = $receipt.Cases.Count
            originals = @($receipt.Cases | Where-Object { $receipt.Mode -eq 'original' } | Select-Object Pattern, Expected, Actual, Count, Page, Passed, Executed)
            runtimeAssemblies = $receipt.Assemblies
        })
        Record-File $receiptPath 'consumer-receipt'
    }
}
if ($remote.observedRestHttp11SqlRequests -le 0 -or $remote.observedFrameHttp2Requests -le 0) { throw 'Actual transport evidence missing.' }
if (-not $application.passed -or $application.checks.Count -ne 6 -or -not $application.standardMigrationAndRepeatedSeed -or
    -not $application.authenticatedAdminPhase0 -or -not $application.authenticatedAdminPhase1 -or -not $application.databaseDirectoryCleaned) { throw 'Application journey incomplete.' }
if (@($provenance | Where-Object { -not $_.MatchesManifest -or -not $_.EquivalentAfterKnownCompileAdaptations }).Count -gt 0) { throw 'Unexplained upstream application differences.' }
$archiveHash = (Get-FileHash -LiteralPath $ApplicationArchive -Algorithm SHA256).Hash
if ($archiveHash -ne '8BB22F2FAD35EF12807C959D0A08ECC2A51090B7345AB12C73CD03BD4523AD59') { throw 'Pinned application archive differs.' }
$bookPackages = foreach ($packageId in $expectedHashes.Keys) {
    $path = Join-Path $BookStorePackages "$packageId/$version"
    $metadata = Get-Content -LiteralPath (Join-Path $path '.nupkg.metadata') -Raw -Encoding UTF8 | ConvertFrom-Json
    [xml]$nuspec = Get-Content -LiteralPath (Join-Path $path "$packageId.nuspec") -Raw -Encoding UTF8
    $hash = (Get-FileHash -LiteralPath (Join-Path $path "$packageId.$version.nupkg") -Algorithm SHA256).Hash
    if ($hash -ne $expectedHashes[$packageId] -or $metadata.source -ne 'https://api.nuget.org/v3/index.json' -or
        $nuspec.package.metadata.repository.commit -ne $packageCommit) { throw 'Application public package mismatch.' }
    [pscustomobject]@{id = $packageId; version = $version; source = $metadata.source; nupkgSha256 = $hash; repositoryCommit = $packageCommit}
}
foreach ($assemblyName in $assemblyPackages.Keys) {
    $assembly = @($application.assemblies | Where-Object name -eq $assemblyName)
    $packageDll = Join-Path $BookStorePackages ($assemblyPackages[$assemblyName] + '/' + $version + '/lib/net10.0/' + $assemblyName + '.dll')
    if ($assembly.Count -ne 1 -or $assembly[0].informationalVersion -ne "$version+$packageCommit" -or
        (Get-FileHash -LiteralPath $packageDll -Algorithm SHA256).Hash -ne $assembly[0].sha256 -or
        (Get-FileHash -LiteralPath $assembly[0].location -Algorithm SHA256).Hash -ne $assembly[0].sha256) { throw 'Application runtime DLL differs from public package.' }
}
foreach ($assembly in $application.assemblies | Where-Object { $_.name -like 'SonnetDB.Abp.*' }) {
    if (-not $assembly.informationalVersion.EndsWith('+' + $applicationCommit, [StringComparison]::Ordinal)) { throw 'Application assembly source revision is not pinned.' }
}
$testDirectory = Join-Path $BookStoreRoot 'tests/SonnetDB.Abp.BookStore.Tests'
$assetsPath = Join-Path $testDirectory 'obj/project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$folders = @($assets.packageFolders.PSObject.Properties.Name)
if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($BookStorePackages).TrimEnd('\', '/')) { throw 'Application has an unexpected cache or fallback.' }
$projectReferences = foreach ($library in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }) {
    $project = [IO.Path]::GetFullPath((Join-Path $testDirectory $library.Value.msbuildProject))
    if (-not $project.StartsWith([IO.Path]::GetFullPath($BookStoreRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Application references a project outside its snapshot.' }
    [pscustomobject]@{name = $library.Name; path = $project}
}
$originalFixture = [IO.File]::ReadAllText((Join-Path $testDirectory 'BookStoreSonnetHttpJourneyTests.cs'))
$generatedFixture = [IO.File]::ReadAllText((Join-Path $testDirectory 'BookStoreContainsPublicPackageTests.cs'))
$anchor = '    private static async Task LoginAsync('
if ($originalFixture.Substring($originalFixture.IndexOf($anchor)) -cne $generatedFixture.Substring($generatedFixture.IndexOf($anchor))) { throw 'Application host or security fixture helpers were modified.' }
$trxPath = Join-Path $BookStoreResults 'issue270-bookstore-bound.trx'
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw -Encoding UTF8
if ($trx.TestRun.ResultSummary.Counters.total -ne '1' -or $trx.TestRun.ResultSummary.Counters.passed -ne '1' -or $trx.TestRun.ResultSummary.Counters.failed -ne '0') { throw 'Application TRX is not one passing test.' }
$applicationSteps = foreach ($label in @('issue270-bookstore-restore-disk-d', 'issue270-bookstore-assets', 'issue270-bookstore-build-bound', 'issue270-bookstore-test-bound')) {
    $path = Join-Path $BookStoreRoot "artifacts/checks/$label.json"
    $step = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($step.exitCode -ne 0 -or $step.timedOut -or $step.failure -or $step.remainingRecordedProcesses.Count -gt 0) { throw 'Application execution step did not pass.' }
    Record-File $path 'application-process-receipt'
    Record-File (Join-Path $BookStoreRoot "artifacts/checks/$label.stdout.log") 'application-process-log'
    $step
}
Record-File $ApplicationArchive 'application-source-archive'
Record-File $UpstreamProvenance 'upstream-source-provenance'
Record-File $assetsPath 'application-assets-graph'
Record-File (Join-Path $BookStoreResults 'bookstore-author-journey.json') 'application-runtime-receipt'
Record-File $trxPath 'application-test-trx'
foreach ($path in @('BookStoreSonnetHttpJourneyTests.cs', 'BookStoreContainsPublicPackageTests.cs')) { Record-File (Join-Path $testDirectory $path) 'application-probe-source' }
foreach ($path in @('src/Acme.BookStore.EntityFrameworkCore/Authors/EfCoreAuthorRepository.cs', 'src/Acme.BookStore.Application/Authors/AuthorAppService.cs')) { Record-File (Join-Path $BookStoreRoot "samples/BookStore/$path") 'official-application-source' }
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File) { Record-File $file.FullName 'reproducible-probe-asset' }
$result = [ordered]@{
    issue = 270; recordedUtc = [DateTimeOffset]::UtcNow; packageVersion = $version; packageSourceCommit = $packageCommit
    embedded = @{cases = $embedded.cases; passed = $embedded.passedCases; failed = $embedded.failedCases; runDirectory = $EmbeddedRun; packages = $embedded.packages}
    remote = @{cases = $remote.cases; passed = $remote.passedCases; failed = $remote.failedCases; runDirectory = $RemoteRun; packages = $remote.packages; sourceServer = $remote.sourceServer; observedRestHttp11SqlRequests = $remote.observedRestHttp11SqlRequests; observedFrameHttp2Requests = $remote.observedFrameHttp2Requests}
    consumerRuns = $consumerCases.ToArray()
    application = @{sourceCommit = $applicationCommit; archiveSha256 = $archiveHash; upstreamProvenance = $provenance; packages = @($bookPackages); privatePackageFolder = $BookStorePackages; snapshotInternalProjectReferences = @($projectReferences); externalEngineProjectReferences = 0; sourceDllSubstitutions = 0; fixtureHelpersUnchanged = $true; passedTests = 1; failedTests = 0; skippedTests = 0; runtime = $application; steps = @($applicationSteps)}
    boundaries = @('Public fixed NuGet clients; frozen source Server, not a Server release package', 'Official application TestServer with real authentication and embedded storage; no browser or standalone application Kestrel claim', 'Application host recreation is not process-crash recovery', 'SQLite console comparison is 10.0.12; application loads its existing SQLite.Core 10.0.11 but this journey selects SonnetDB', 'Restore disables vulnerability audit; no dependency-security conclusion', 'No production code changes, commit, push, GitHub reply or closure performed by this subagent')
    files = $files.ToArray()
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputFile), ($result | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Get-FileHash -LiteralPath $OutputFile -Algorithm SHA256 | ConvertTo-Json -Compress
