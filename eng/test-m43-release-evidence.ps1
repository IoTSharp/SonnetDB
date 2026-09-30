[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$verifier = Join-Path $PSScriptRoot 'verify-m43-release-evidence.ps1'
$templatePath = Join-Path $PSScriptRoot 'm43-release-evidence.template.json'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonnetdb-m43-evidence-' + [Guid]::NewGuid().ToString('N'))
$testSha = 'a' * 40
$testCount = 0
$fixtureLinks = [Collections.Generic.List[string]]::new()

function Write-Json {
    param([string] $Path, [object] $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 64), [Text.UTF8Encoding]::new($false))
}

function New-Manifest {
    return Get-Content -LiteralPath $templatePath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 32
}

function Assert-Evidence {
    param([object] $Manifest, [string] $Expected, [string] $Name, [string] $CandidateSha = $testSha, [string] $CandidateRepository = 'IoTSharp/SonnetDB', [switch] $Strict)
    $script:testCount++
    $manifestPath = Join-Path $tempRoot ($Name + '.json')
    $outputPath = Join-Path $tempRoot ($Name + '-output/report.json')
    Write-Json $manifestPath $Manifest
    $threw = $false
    try { & $verifier -ManifestPath $manifestPath -CommitSha $CandidateSha -Version '4.0.0' -Repository $CandidateRepository -OutputPath $outputPath -AllowNotReady:(-not $Strict) }
    catch { $threw = $true }
    if ($threw -ne [bool]$Strict) { throw "$Name unexpected error/exit behavior." }
    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) { throw "$Name did not preserve its report." }
    $report = Get-Content -LiteralPath $outputPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    if ($report.status -cne $Expected) { throw "$Name expected $Expected, got $($report.status): $($report.issues -join ', ')" }
    if ($report.gates.Count -ne 8) { throw "$Name must preserve the complete eight-gate inventory." }
    if ($report.commitSha -ine $CandidateSha -or $report.version -cne '4.0.0' -or $report.repository -cne $CandidateRepository) { throw "$Name lost its candidate identity." }
    if ($report.counts.pass + $report.counts.notReady + $report.counts.deferred -ne 8) { throw "$Name counts do not match the inventory." }
    if ($report.manifest.sha256 -cne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "$Name manifest digest mismatch." }
    foreach ($gate in $report.gates) {
        foreach ($file in $gate.files) {
            if ($file.sha256 -cne (Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "$Name lost a verifier/raw-report digest." }
        }
    }
    return $report
}

function Assert-RejectedOutput {
    param([object] $Manifest, [string] $OutputPath, [string] $ProtectedFile, [string] $Name)
    $manifestPath = Join-Path $tempRoot ($Name + '.json')
    Write-Json $manifestPath $Manifest
    $originalHash = (Get-FileHash -LiteralPath $ProtectedFile -Algorithm SHA256).Hash
    $rejected = $false
    try { & $verifier -ManifestPath $manifestPath -CommitSha $testSha -Version '4.0.0' -OutputPath $OutputPath -AllowNotReady }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-FileHash -LiteralPath $ProtectedFile -Algorithm SHA256).Hash -cne $originalHash) { throw "$Name must reject the output and preserve raw evidence." }
    $script:testCount++
}

function New-DocumentReport {
    param([string] $Profile)
    $count = if ($Profile -eq 'million') { 1000000 } else { 10000000 }
    return [ordered]@{
        schemaVersion = 2; profile = $Profile; documentCount = $count; batchSize = 1000; succeeded = $true
        startedAtUtc = '2026-09-20T00:00:00Z'; completedAtUtc = '2026-09-20T00:01:00Z'
        environment = @{ commitSha = $testSha; processorCount = 1; totalAvailableMemoryBytes = 1; dataVolume = @{ deviceModel = 'fixture-NVMe'; totalBytes = 100; availableBytes = 50 } }
        targetHardware = @{ status = 'PASS'; targetId = 'fixture-target'; contract = 'M25-#174-fixed-target-v1' }
        phases = @(@('write', 'index_create', 'indexed_query', 'index_rebuild', 'ttl_index_create', 'ttl_cleanup', 'backup', 'hot_reopen', 'cold_process_start', 'crash_recovery', 'backup_restore') | ForEach-Object { @{ name = $_; durationMilliseconds = 1; operations = 1; operationsPerSecond = 1; details = @{} } })
        memorySamples = @(@('write', 'hot_end', 'completed') | ForEach-Object { @{ timestampUtc = '2026-09-20T00:00:30Z'; phase = $_; documents = $count; workingSetBytes = 1; privateBytes = 1; managedBytes = 1 } })
    }
}

try {
    [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    [void](Assert-Evidence (New-Manifest) 'DEFERRED' 'explicit-deferrals')
    [void](Assert-Evidence (New-Manifest) 'DEFERRED' 'strict-deferrals' -Strict)
    $case = New-Manifest; $case.gates = @()
    [void](Assert-Evidence $case 'NOT_READY' 'missing-gates')
    $case = New-Manifest; $case.gates[0].reason = ' '
    [void](Assert-Evidence $case 'NOT_READY' 'blank-deferral')
    $case = New-Manifest; $case.gates[0].id = $case.gates[1].id
    $result = Assert-Evidence $case 'NOT_READY' 'duplicate-gates'
    if ($result.counts.notReady -ne 8) { throw 'An invalid manifest must prevent every gate from passing.' }
    $case = New-Manifest; $case.gates[0].id = 'invented-gate'
    [void](Assert-Evidence $case 'NOT_READY' 'unknown-gate')
    $case = New-Manifest; $case.schemaVersion = '1'
    [void](Assert-Evidence $case 'NOT_READY' 'string-schema')
    $case = New-Manifest; $case.gates = $case.gates[0]
    [void](Assert-Evidence $case 'NOT_READY' 'object-gates')
    $case = New-Manifest; $case.gates[0].mode = 'PASS'
    [void](Assert-Evidence $case 'NOT_READY' 'status-as-mode')
    $case = New-Manifest; $case.gates[4].mode = 'verify'
    $case.gates[4] | Add-Member -NotePropertyName status -NotePropertyValue 'PASS'
    [void](Assert-Evidence $case 'NOT_READY' 'unverified-model-pass')

    Write-Json (Join-Path $tempRoot 'million.json') (New-DocumentReport 'million')
    Write-Json (Join-Path $tempRoot 'ten-million.json') (New-DocumentReport 'ten-million')
    $case = New-Manifest
    $case.gates[3] = [pscustomobject]@{ id = 'm25-capacity'; mode = 'verify'; reports = @('million.json', 'ten-million.json'); targetHardwareId = 'fixture-target' }
    $result = Assert-Evidence $case 'DEFERRED' 'unattested-valid-document-reports'
    $documentGate = @($result.gates | Where-Object id -EQ 'm25-capacity')[0]
    if ($documentGate.status -ne 'DEFERRED' -or $documentGate.files.Count -ne 4 -or -not ($documentGate.issues -match 'external_attestation_missing')) { throw 'M25 must preserve raw reports and the original attestation deferral.' }
    [void](Assert-Evidence $case 'NOT_READY' 'stale-document-commit' -CandidateSha ('b' * 40))
    $case.gates[3].reports = @('million.json', 'million.json')
    [void](Assert-Evidence $case 'NOT_READY' 'duplicate-document-profile')
    $case.gates[3].reports = @('million.json', 'missing.json')
    [void](Assert-Evidence $case 'NOT_READY' 'missing-document-report')

    $rawPath = Join-Path $tempRoot 'million.json'
    $rawHash = (Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash
    $aliasManifest = Join-Path $tempRoot 'output-alias.json'
    Write-Json $aliasManifest $case
    $rejected = $false
    try { & $verifier -ManifestPath $aliasManifest -CommitSha $testSha -Version '4.0.0' -OutputPath $rawPath -AllowNotReady }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash -ne $rawHash) { throw 'An aggregate output must never overwrite its evidence input, including on failure.' }
    $testCount++

    # Reuse the established raw-bundle fixtures to test invocation, identity and tamper rejection.
    . (Join-Path $PSScriptRoot '../tests/SonnetDB.EcosystemSoak/scripts/m19-capacity-fixtures.ps1')
    $fixtureBundle = New-M19FixtureBundle (Join-Path $tempRoot 'm19-bundle')
    $case = New-Manifest
    $case.gates[1] = [pscustomobject]@{ id = 'm19-capacity'; mode = 'verify'; bundleRoot = $fixtureBundle.bundleRoot; artifactUrl = 'https://github.com/fixture/sonnetdb/actions/runs/42'; targetHardwareId = $fixtureBundle.targetHardwareId; targetHardwareContract = $fixtureBundle.targetHardwareContract }
    $result = Assert-Evidence $case 'DEFERRED' 'm19-bundle-verifier-reuse' -CandidateRepository 'fixture/sonnetdb'
    $capacityGate = @($result.gates | Where-Object id -EQ 'm19-capacity')[0]
    if ($capacityGate.status -ne 'PASS' -or $capacityGate.files.Count -lt 10) { throw 'Valid M19 fixture must exercise the existing verifier and retain its raw-file digests.' }
    $result = Assert-Evidence $case 'NOT_READY' 'cross-repository-m19-bundle'
    if (@($result.gates | Where-Object id -EQ 'm19-capacity')[0].status -cne 'NOT_READY') { throw 'A matching SHA from another repository must not pass.' }
    $case.gates[1].artifactUrl = 'https://github.com/IoTSharp/SonnetDB/actions/runs/42'
    [void](Assert-Evidence $case 'NOT_READY' 'cross-repository-m19-url' -CandidateRepository 'fixture/sonnetdb')
    $case.gates[1].artifactUrl = $fixtureBundle.artifactUrl
    $aliasOutput = Join-Path $fixtureBundle.bundleRoot 'aggregate.json'
    Write-Json $aliasManifest $case
    $rejected = $false
    try { & $verifier -ManifestPath $aliasManifest -CommitSha $testSha -Version '4.0.0' -OutputPath $aliasOutput -AllowNotReady }
    catch { $rejected = $true }
    if (-not $rejected -or (Test-Path -LiteralPath $aliasOutput)) { throw 'Aggregate output must not mutate a raw evidence bundle.' }
    $testCount++
    $protectedReport = Join-Path $fixtureBundle.bundleRoot 'high-cardinality/report.json'
    $outputAlias = Join-Path $tempRoot 'bundle-output-alias'
    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $outputAlias -Target (Split-Path -LiteralPath $protectedReport) | Out-Null
    $fixtureLinks.Add($outputAlias)
    Assert-RejectedOutput $case (Join-Path $outputAlias 'report.json') $protectedReport 'junction-output-alias'
    $sidecarRoot = Join-Path $tempRoot 'junction-verifier-output/verifiers'
    [IO.Directory]::CreateDirectory($sidecarRoot) | Out-Null
    $sidecarAlias = Join-Path $sidecarRoot 'm19-capacity'
    New-Item -ItemType $linkType -Path $sidecarAlias -Target $fixtureBundle.bundleRoot | Out-Null
    $fixtureLinks.Add($sidecarAlias)
    $originalEntries = @(Get-ChildItem -LiteralPath $fixtureBundle.bundleRoot -Force).Count
    $result = Assert-Evidence $case 'NOT_READY' 'junction-verifier' -CandidateRepository 'fixture/sonnetdb'
    if (@(Get-ChildItem -LiteralPath $fixtureBundle.bundleRoot -Force).Count -ne $originalEntries -or -not (@($result.gates | Where-Object id -EQ 'm19-capacity')[0].issues -match 'must not use symbolic links')) { throw 'Verifier sidecar directories must not write through a bundle alias.' }
    $hardLinkRoot = Join-Path $tempRoot 'hardlink-output'
    [IO.Directory]::CreateDirectory($hardLinkRoot) | Out-Null
    $hardLinkOutput = Join-Path $hardLinkRoot 'report.json'
    New-Item -ItemType HardLink -Path $hardLinkOutput -Target $protectedReport | Out-Null
    $fixtureLinks.Add($hardLinkOutput)
    Assert-RejectedOutput $case $hardLinkOutput $protectedReport 'hardlink-output-alias'
    Remove-Item -LiteralPath $hardLinkOutput -Force
    Assert-RejectedOutput $case (Join-Path $tempRoot 'report.json') $protectedReport 'output-directory-contains-bundle'
    [void](Assert-Evidence $case 'NOT_READY' 'stale-m19-commit' -CandidateSha ('b' * 40) -CandidateRepository 'fixture/sonnetdb')
    [IO.File]::AppendAllText((Join-Path $fixtureBundle.bundleRoot 'high-cardinality/report.md'), 'tampered', [Text.UTF8Encoding]::new($false))
    $result = Assert-Evidence $case 'NOT_READY' 'tampered-m19-artifact' -CandidateRepository 'fixture/sonnetdb'
    if (@($result.gates | Where-Object id -EQ 'm19-capacity')[0].status -ne 'NOT_READY') { throw 'Tampered raw artifacts must not reuse an earlier verifier PASS.' }

    # The isolated wrapper runs the real M19 verifier, then changes a raw file.
    $toolRoot = Join-Path $tempRoot 'isolated-tool'
    $toolEng = Join-Path $toolRoot 'eng'
    $toolScripts = Join-Path $toolRoot 'tests/SonnetDB.EcosystemSoak/scripts'
    [IO.Directory]::CreateDirectory($toolEng) | Out-Null
    [IO.Directory]::CreateDirectory($toolScripts) | Out-Null
    Copy-Item -LiteralPath $verifier -Destination $toolEng
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release-readiness-policy.ps1') -Destination $toolEng
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/m43-m19-post-verification.ps1') -Destination (Join-Path $toolScripts 'verify-m19-capacity-bundle.ps1')
    $originalVerifier = $verifier
    try {
        $verifier = Join-Path $toolEng 'verify-m43-release-evidence.ps1'
        foreach ($mode in @('modify', 'delete')) {
            $postBundle = New-M19FixtureBundle (Join-Path $tempRoot ('post-' + $mode))
            Write-Json (Join-Path $postBundle.bundleRoot 'aggregation-fixture.json') @{ mode = $mode; verifier = (Join-Path $PSScriptRoot '../tests/SonnetDB.EcosystemSoak/scripts/verify-m19-capacity-bundle.ps1') }
            $case = New-Manifest
            $case.gates[1] = [pscustomobject]@{ id = 'm19-capacity'; mode = 'verify'; bundleRoot = $postBundle.bundleRoot; artifactUrl = $postBundle.artifactUrl; targetHardwareId = $postBundle.targetHardwareId; targetHardwareContract = $postBundle.targetHardwareContract }
            $result = Assert-Evidence $case 'NOT_READY' ('m19-post-verification-' + $mode) -CandidateRepository 'fixture/sonnetdb'
            $capacityGate = @($result.gates | Where-Object id -EQ 'm19-capacity')[0]
            if ($capacityGate.status -cne 'NOT_READY' -or -not ($capacityGate.issues -match 'Evidence (changed after verification|file disappeared)')) { throw 'Raw evidence must be checked against the verifier digest after verification.' }
        }
    }
    finally { $verifier = $originalVerifier }

    # Mock only command dispatch to prove a stale evaluator cannot bypass rebuilding.
    $graphCommands = [Collections.Generic.List[object]]::new()
    $graphBuildFails = $false
    $graphDirty = $false
    function git {
        $global:LASTEXITCODE = 0
        if ($args -contains 'rev-parse') { return $testSha }
        if ($graphDirty) { return ' M fixture.cs' }
    }
    function dotnet {
        $graphCommands.Add(@($args))
        $global:LASTEXITCODE = 0
        if ($args[0] -ceq 'build') {
            if ($graphBuildFails) { $global:LASTEXITCODE = 1 }
            return
        }
        $outputIndex = [Array]::IndexOf([object[]]$args, '--output')
        Write-Json (Join-Path $args[$outputIndex + 1] 'm40-graph-production-gate.json') @{ release_decision = 'PASS'; fixture = $true }
    }
    try {
        Write-Json (Join-Path $tempRoot 'graph-fixture.json') @{}
        $case = New-Manifest
        $case.gates[6] = [pscustomobject]@{ id = 'm40-graph-production'; mode = 'verify'; manifestPath = 'graph-fixture.json' }
        $result = Assert-Evidence $case 'DEFERRED' 'graph-evaluator-rebuild-order'
        if (@($result.gates | Where-Object id -EQ 'm40-graph-production')[0].status -cne 'PASS' -or $graphCommands.Count -ne 2 -or $graphCommands[0][0] -cne 'build' -or $graphCommands[0] -cnotcontains '--no-incremental' -or $graphCommands[0] -cnotcontains '/warnaserror' -or $graphCommands[1][0] -cne 'run') { throw 'The evaluator must be rebuilt from the candidate before it runs.' }
        $graphCommands.Clear(); $graphBuildFails = $true
        [void](Assert-Evidence $case 'NOT_READY' 'graph-evaluator-build-failure')
        if ($graphCommands.Count -ne 1 -or $graphCommands[0][0] -cne 'build') { throw 'A failed rebuild must prevent running a stale evaluator.' }
        $graphCommands.Clear(); $graphBuildFails = $false; $graphDirty = $true
        [void](Assert-Evidence $case 'NOT_READY' 'graph-evaluator-dirty-checkout')
        if ($graphCommands.Count -ne 0) { throw 'A dirty checkout must be rejected before rebuilding the evaluator.' }
    }
    finally { Remove-Item Function:git, Function:dotnet }
}
finally {
    foreach ($link in $fixtureLinks) { if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force } }
    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolvedTemp).TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $expectedParent -and [IO.Path]::GetFileName($resolvedTemp).StartsWith('sonnetdb-m43-evidence-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $resolvedTemp) { Remove-Item -LiteralPath $resolvedTemp -Recurse -Force }
    }
}
Write-Host "M43 release evidence contracts passed ($testCount cases; all samples are synthetic and establish no production evidence)."
