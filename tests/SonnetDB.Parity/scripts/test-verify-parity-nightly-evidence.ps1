$ErrorActionPreference = "Stop"

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Expected,

        [Parameter(Mandatory = $true)]
        [object] $Actual,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Assert-ContainsCode {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Issues,

        [Parameter(Mandatory = $true)]
        [string] $Code,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if (@($Issues | Where-Object { $_.code -eq $Code }).Count -eq 0) {
        throw "$Message Missing issue code '$Code'."
    }
}

function Copy-Fixture {
    param([object] $Fixture)

    return ($Fixture | ConvertTo-Json -Depth 32) | ConvertFrom-Json
}

function Write-Fixture {
    param(
        [object] $Fixture,
        [string] $Path
    )

    $Fixture | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Invoke-FixtureCase {
    param(
        [object] $Fixture,
        [string] $Name,
        [string] $TestRoot,
        [string] $Verifier
    )

    $fixtureCasePath = Join-Path $TestRoot "$Name-fixture.json"
    $outputPath = Join-Path $TestRoot "$Name-output.json"
    Write-Fixture $Fixture $fixtureCasePath
    & $Verifier -FixturePath $fixtureCasePath -OutputPath $outputPath -AllowNotReady
    return Get-Content -Raw -LiteralPath $outputPath | ConvertFrom-Json
}

function Get-ProfileResult {
    param(
        [object] $Report,
        [string] $RunId,
        [string] $Profile
    )

    $run = $Report.runs | Where-Object runId -eq $RunId
    return $run.profiles | Where-Object profile -eq $Profile
}

$verifier = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "verify-parity-nightly-evidence.ps1"))
$fixturePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../fixtures/nightly-evidence/three-success-four-failure.json"))
$baseFixture = Get-Content -Raw -LiteralPath $fixturePath | ConvertFrom-Json
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("sonnetdb-parity-nightly-test-" + [Guid]::NewGuid().ToString("N"))

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

    $currentOutput = Join-Path $testRoot "current.json"
    & $verifier -FixturePath $fixturePath -OutputPath $currentOutput -AllowNotReady
    $current = Get-Content -Raw -LiteralPath $currentOutput | ConvertFrom-Json
    Assert-Equal "NOT_READY" $current.status "Three successful runs followed by four failures must not pass."
    Assert-Equal 7 $current.examinedRunCount "The verifier must inspect seven scheduled runs."
    Assert-Equal 3 $current.validRunCount "Only the three passing runs should validate."
    Assert-Equal 42.86 $current.successRate "The current fixture success rate is incorrect."
    Assert-ContainsCode $current.issues "scheduled_run_failed_validation" "Mixed success and failure evidence must be rejected."
    foreach ($run in $current.runs) {
        Assert-Equal 2 $run.profiles.Count "Every run must validate light and full profiles."
    }

    $manualFixture = Copy-Fixture $baseFixture
    $manualFixture.runs[0].event = "workflow_dispatch"
    $manual = Invoke-FixtureCase $manualFixture "manual-run" $testRoot $verifier
    $manualRun = $manual.runs | Where-Object runId -eq "33071879124"
    Assert-ContainsCode $manualRun.issues "run_event_not_scheduled" "Manual runs must not count as nightly evidence."
    Assert-Equal "NOT_READY" $manual.status "A manual run in the evidence window must keep the gate NOT_READY."

    $duplicateRunFixture = Copy-Fixture $baseFixture
    $duplicateRunFixture.runs[1].runId = $duplicateRunFixture.runs[0].runId
    $duplicateRunFixture.runs[1].url = $duplicateRunFixture.runs[0].url
    foreach ($profileName in @("light", "full")) {
        $duplicateRunFixture.runs[1].profiles.PSObject.Properties[$profileName].Value.summary.runId = $duplicateRunFixture.runs[0].runId
    }
    $duplicateRun = Invoke-FixtureCase $duplicateRunFixture "duplicate-run-id" $testRoot $verifier
    Assert-Equal "NOT_READY" $duplicateRun.status "Duplicate scheduled run IDs must keep the evidence window NOT_READY."
    Assert-ContainsCode $duplicateRun.issues "duplicate_scheduled_run_id" "Duplicate scheduled run IDs must be reported at the window level."
    foreach ($duplicateReport in @($duplicateRun.runs | Where-Object runId -eq "33071879124")) {
        Assert-ContainsCode $duplicateReport.issues "run_id_duplicate" "Every duplicate scheduled run must be marked invalid."
    }

    $defaultFailureRaised = $false
    try {
        & $verifier -FixturePath $fixturePath -OutputPath (Join-Path $testRoot "must-fail.json")
    }
    catch {
        $defaultFailureRaised = $true
    }
    Assert-Equal $true $defaultFailureRaised "NOT_READY must fail by default."

    $insufficientFixture = Copy-Fixture $baseFixture
    $insufficientFixture.runs = @($insufficientFixture.runs | Select-Object -First 3)
    $insufficient = Invoke-FixtureCase $insufficientFixture "insufficient" $testRoot $verifier
    Assert-Equal "NOT_READY" $insufficient.status "Fewer than seven runs must not pass."
    Assert-ContainsCode $insufficient.issues "insufficient_scheduled_runs" "Missing scheduled days must be explicit."

    $invalidGapFixture = Copy-Fixture $baseFixture
    $invalidGapFixture.runs[3].profiles.full.summary.gateFailures[0].gap_reason = ""
    $invalidGap = Invoke-FixtureCase $invalidGapFixture "invalid-gap" $testRoot $verifier
    $invalidFull = Get-ProfileResult $invalidGap "32685178665" "full"
    Assert-ContainsCode $invalidFull.issues "gate_failure_gap_reason_missing" "Every gate failure must contain gap_reason."

    $inconsistentFixture = Copy-Fixture $baseFixture
    $inconsistentFixture.runs[0].profiles.light.summary.passedScenarios = 3
    $inconsistent = Invoke-FixtureCase $inconsistentFixture "inconsistent-counts" $testRoot $verifier
    $inconsistentLight = Get-ProfileResult $inconsistent "33071879124" "light"
    Assert-ContainsCode $inconsistentLight.issues "summary_scenario_counts_inconsistent" "Summary scenario counts must add up to totalScenarios."

    $emptySuitesFixture = Copy-Fixture $baseFixture
    $emptySuitesFixture.runs[0].profiles.light.summary.suites = @()
    $emptySuites = Invoke-FixtureCase $emptySuitesFixture "empty-suites" $testRoot $verifier
    $emptySuitesLight = Get-ProfileResult $emptySuites "33071879124" "light"
    Assert-ContainsCode $emptySuitesLight.issues "summary_suites_empty" "A summary without suites must be rejected."

    $passingFailureFixture = Copy-Fixture $baseFixture
    $passingSummary = $passingFailureFixture.runs[0].profiles.light.summary
    $passingSummary.passedScenarios = 3
    $passingSummary.failedScenarios = 1
    $passingSummary.passRate = 75
    $passingSummary.suites[0].passed = 3
    $passingSummary.suites[0].failed = 1
    $passingFailure = Invoke-FixtureCase $passingFailureFixture "passing-with-failure" $testRoot $verifier
    $passingFailureLight = Get-ProfileResult $passingFailure "33071879124" "light"
    Assert-ContainsCode $passingFailureLight.issues "passing_summary_has_failed_scenarios" "A passing summary cannot contain failed scenarios."

    $missingFieldFixture = Copy-Fixture $baseFixture
    $missingFieldFixture.runs[0].profiles.light.summary.PSObject.Properties.Remove("generatedAtUtc")
    $missingField = Invoke-FixtureCase $missingFieldFixture "missing-generated-at" $testRoot $verifier
    $missingFieldLight = Get-ProfileResult $missingField "33071879124" "light"
    Assert-ContainsCode $missingFieldLight.issues "summary_field_invalid" "Missing required schema fields must produce NOT_READY."

    $rawMismatchFixture = Copy-Fixture $baseFixture
    $rawMismatchFixture.runs[0].profiles.light.rawReports[0].scenarioCount = 3
    $rawMismatch = Invoke-FixtureCase $rawMismatchFixture "raw-count-mismatch" $testRoot $verifier
    $rawMismatchLight = Get-ProfileResult $rawMismatch "33071879124" "light"
    Assert-ContainsCode $rawMismatchLight.issues "raw_suite_count_mismatch" "Raw scenario counts must match each summary suite."
    Assert-ContainsCode $rawMismatchLight.issues "raw_summary_count_mismatch" "Raw scenario counts must match summary totalScenarios."

    $missingRawFixture = Copy-Fixture $baseFixture
    $missingRawFixture.runs[0].profiles.light.rawReports = @()
    $missingRawFixture.runs[0].profiles.light.rawReportFileCount = 0
    $missingRaw = Invoke-FixtureCase $missingRawFixture "missing-raw" $testRoot $verifier
    $missingRawLight = Get-ProfileResult $missingRaw "33071879124" "light"
    Assert-ContainsCode $missingRawLight.issues "raw_report_missing_for_suite" "Every summary suite must have exactly one raw report."

    $duplicateRawFixture = Copy-Fixture $baseFixture
    $duplicateRawProfile = $duplicateRawFixture.runs[0].profiles.light
    $duplicateRawProfile.rawReports = @(
        $duplicateRawProfile.rawReports[0]
        $duplicateRawProfile.rawReports[0]
    )
    $duplicateRawProfile.rawReportFileCount = 2
    $duplicateRaw = Invoke-FixtureCase $duplicateRawFixture "duplicate-raw" $testRoot $verifier
    $duplicateRawLight = Get-ProfileResult $duplicateRaw "33071879124" "light"
    Assert-ContainsCode $duplicateRawLight.issues "raw_report_duplicate" "Duplicate raw report runIds must be rejected."

    $invalidRawPathFixture = Copy-Fixture $baseFixture
    $invalidRawPathFixture.runs[0].profiles.light.rawReports[0].source = "raw/unexpected/report.json"
    $invalidRawPath = Invoke-FixtureCase $invalidRawPathFixture "invalid-raw-path" $testRoot $verifier
    $invalidRawPathLight = Get-ProfileResult $invalidRawPath "33071879124" "light"
    Assert-ContainsCode $invalidRawPathLight.issues "raw_report_path_invalid" "Raw reports outside raw/<runId>/report.json must be rejected."

    $invalidWindowOutput = Join-Path $testRoot "invalid-window.json"
    $invalidWindowRaised = $false
    try {
        & $verifier `
            -FixturePath $fixturePath `
            -RequiredRunCount 1 `
            -OutputPath $invalidWindowOutput `
            -AllowNotReady
    }
    catch {
        $invalidWindowRaised = $true
    }
    Assert-Equal $true $invalidWindowRaised "RequiredRunCount below seven must fail parameter binding."
    Assert-Equal $false (Test-Path -LiteralPath $invalidWindowOutput) "An invalid evidence window must not create a READY report."

    $readyFixture = Copy-Fixture $baseFixture
    $dayOffset = 0
    foreach ($run in $readyFixture.runs) {
        $run.createdAtUtc = [DateTimeOffset]::UtcNow.AddDays(-$dayOffset).AddHours(-1).ToString('o')
        $dayOffset++
        $run.conclusion = "success"
        foreach ($profileName in @("light", "full")) {
            $summary = $run.profiles.PSObject.Properties[$profileName].Value.summary
            $summary.status = "passing"
            $summary.passedScenarios += $summary.failedScenarios
            $summary.failedScenarios = 0
            $summary.passRate = 100
            $summary.message = "100%"
            $summary.color = "brightgreen"
            $summary.badgeUrl = "https://img.shields.io/badge/parity-100%25-brightgreen"
            foreach ($suite in $summary.suites) {
                $suite.passed += $suite.failed
                $suite.failed = 0
            }
            $summary.gateFailures = @()
            foreach ($raw in $run.profiles.$profileName.rawReports) {
                foreach ($scenario in $raw.report.scenarios) {
                    $scenario.withinTolerance = $true
                    foreach ($backend in $scenario.backends) {
                        if ($backend.status -eq 'fail') { $backend.status = 'pass' }
                    }
                }
            }
        }
    }
    $readyPath = Join-Path $testRoot "ready-fixture.json"
    Write-Fixture $readyFixture $readyPath
    $readyOutput = Join-Path $testRoot "ready.json"
    & $verifier -FixturePath $readyPath -OutputPath $readyOutput
    $ready = Get-Content -Raw -LiteralPath $readyOutput | ConvertFrom-Json
    Assert-Equal "READY" $ready.status "Seven consecutive passing scheduled runs must be ready."
    Assert-Equal 7 $ready.validRunCount "All seven ready runs must validate."
    Assert-Equal 100 $ready.successRate "Ready evidence must have a 100 percent success rate."

    # These changes retain the green summary and its original scenario count.
    # Only inspection of the raw outcomes can reject them.
    $rawCases = @(
        @{ Name = 'missing-content'; Code = 'raw_report_content_missing'; Change = { param($profile) $profile.rawReports[0].PSObject.Properties.Remove('report') } }
        @{ Name = 'empty-backends'; Code = 'raw_scenario_backends_invalid'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].backends = @() } }
        @{ Name = 'missing-backend'; Code = 'raw_backend_outcome_missing'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].backends = @($profile.rawReports[0].report.scenarios[0].backends | Select-Object -Skip 1) } }
        @{ Name = 'duplicate-backend'; Code = 'raw_backend_identity_invalid'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].backends += $profile.rawReports[0].report.scenarios[0].backends[0] } }
        @{ Name = 'unknown-status'; Code = 'raw_backend_status_invalid'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].backends[0].status = 'success' } }
        @{ Name = 'raw-failure'; Code = 'raw_scenario_failed'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].backends[0].status = 'fail' } }
        @{ Name = 'raw-tolerance-failure'; Code = 'raw_scenario_failed'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].withinTolerance = $false } }
        @{ Name = 'raw-tolerance-string'; Code = 'raw_tolerance_invalid'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].withinTolerance = 'true' } }
        @{ Name = 'missing-comparison'; Code = 'raw_comparison_missing'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].PSObject.Properties.Remove('withinTolerance') } }
        @{ Name = 'raw-identity'; Code = 'raw_report_content_identity_mismatch'; Change = { param($profile) $profile.rawReports[0].report.runId = 'another-suite' } }
        @{ Name = 'raw-count'; Code = 'raw_content_count_mismatch'; Change = { param($profile) $profile.rawReports[0].report.scenarios = @($profile.rawReports[0].report.scenarios | Select-Object -Skip 1) } }
        @{ Name = 'duplicate-scenario'; Code = 'raw_scenario_name_invalid'; Change = { param($profile) $profile.rawReports[0].report.scenarios[1].name = $profile.rawReports[0].report.scenarios[0].name } }
        @{ Name = 'missing-differences'; Code = 'raw_differences_invalid'; Change = { param($profile) $profile.rawReports[0].report.scenarios[0].PSObject.Properties.Remove('differences') } }
    )
    foreach ($case in $rawCases) {
        $rawFixture = Copy-Fixture $readyFixture
        & $case.Change $rawFixture.runs[0].profiles.full
        $rawResult = Invoke-FixtureCase $rawFixture $case.Name $testRoot $verifier
        Assert-Equal 'NOT_READY' $rawResult.status "Raw evidence mutation '$($case.Name)' must reject the green summary."
        $profileResult = Get-ProfileResult $rawResult '33071879124' 'full'
        Assert-ContainsCode $profileResult.issues $case.Code "Raw evidence mutation '$($case.Name)' must report its actual defect."
    }

    $unreachableFixture = Copy-Fixture $readyFixture
    $unreachableProfile = $unreachableFixture.runs[0].profiles.full
    $unreachableBackend = $unreachableProfile.rawReports[0].report.scenarios[0].backends | Where-Object backend -EQ 'clickhouse'
    $unreachableBackend.status = 'skipped'
    $unreachableBackend | Add-Member -NotePropertyName gapReason -NotePropertyValue 'clickhouse unreachable'
    $unreachableProfile.summary.passedScenarios--; $unreachableProfile.summary.skippedScenarios++
    $unreachableProfile.summary.suites[0].passed--; $unreachableProfile.summary.suites[0].skipped++
    $unreachable = Invoke-FixtureCase $unreachableFixture 'old-green-unreachable' $testRoot $verifier
    Assert-Equal 'NOT_READY' $unreachable.status 'A historical green summary with a skipped unreachable reference must fail.'
    Assert-ContainsCode (Get-ProfileResult $unreachable '33071879124' 'full').issues 'raw_required_reference_unreachable' 'Required reference reachability must come from the original outcomes.'

    $missingReferenceFixture = Copy-Fixture $readyFixture
    $missingReferenceReport = $missingReferenceFixture.runs[0].profiles.full.rawReports[0].report
    $missingReferenceReport.backends = @($missingReferenceReport.backends | Where-Object { $_ -ne 'clickhouse' })
    foreach ($scenario in $missingReferenceReport.scenarios) {
        $scenario.backends = @($scenario.backends | Where-Object backend -NE 'clickhouse')
    }
    $missingReference = Invoke-FixtureCase $missingReferenceFixture 'missing-reference' $testRoot $verifier
    Assert-Equal 'NOT_READY' $missingReference.status 'A reference omitted from all original reports cannot pass.'
    Assert-ContainsCode (Get-ProfileResult $missingReference '33071879124' 'full').issues 'raw_required_backend_not_executed' 'Profile reference requirements must not depend on the summary listing them.'

    $warningFixture = Copy-Fixture $readyFixture
    $warningProfile = $warningFixture.runs[0].profiles.full
    $warningScenario = $warningProfile.rawReports[0].report.scenarios[0]
    $warningScenario.withinTolerance = $false
    $warningScenario.backends[0].metrics | Add-Member -NotePropertyName performance_gating -NotePropertyValue 'warning_only'
    $warningProfile.summary.warningOnlyScenarios = 1
    $warningProfile.summary.performanceWarnings = @([pscustomobject]@{ suite = $warningProfile.rawReports[0].runId; scenario = $warningScenario.name; reason = 'performance metrics are warning only' })
    $warning = Invoke-FixtureCase $warningFixture 'performance-warning' $testRoot $verifier
    Assert-Equal 'READY' $warning.status 'Explicit performance warnings retain their existing nonblocking contract.'
    $warningScenario.backends[0].status = 'fail'
    $warningFailure = Invoke-FixtureCase $warningFixture 'performance-backend-failure' $testRoot $verifier
    Assert-Equal 'NOT_READY' $warningFailure.status 'Warning-only performance metrics cannot hide a failed backend.'
    Assert-ContainsCode (Get-ProfileResult $warningFailure '33071879124' 'full').issues 'raw_scenario_failed' 'Backend failures remain blocking in performance scenarios.'

    $staleFixture = Copy-Fixture $readyFixture
    foreach ($run in $staleFixture.runs) { $run.createdAtUtc = ([DateTimeOffset]$run.createdAtUtc).AddDays(-90).ToString('o') }
    $stale = Invoke-FixtureCase $staleFixture 'stale-window' $testRoot $verifier
    Assert-Equal 'NOT_READY' $stale.status 'Expired nightly windows cannot approve a release.'
    Assert-ContainsCode $stale.issues 'scheduled_window_stale' 'Stale nightly evidence must explain its age.'
    $pendingFixture = Copy-Fixture $readyFixture
    $pendingFixture.runs[0] | Add-Member -NotePropertyName status -NotePropertyValue 'in_progress'
    $pending = Invoke-FixtureCase $pendingFixture 'pending-run' $testRoot $verifier
    Assert-Equal 'NOT_READY' $pending.status 'A pending latest run cannot be hidden by completed history.'
    $skippedFixture = Copy-Fixture $readyFixture
    foreach ($profileName in @('light', 'full')) {
        $summary = $skippedFixture.runs[0].profiles.$profileName.summary
        $summary.passedScenarios = 0; $summary.skippedScenarios = $summary.totalScenarios
        foreach ($suite in $summary.suites) { $suite.passed = 0; $suite.skipped = $suite.total }
    }
    $skipped = Invoke-FixtureCase $skippedFixture 'all-skipped' $testRoot $verifier
    Assert-Equal 'NOT_READY' $skipped.status 'An all-skipped profile is not real parity evidence.'

    $candidateFixture = Copy-Fixture $readyFixture
    $candidateFixture.runs = @($candidateFixture.runs[0])
    $candidateFixture.runs[0].event = 'workflow_dispatch'
    $candidateId = [string]$candidateFixture.runs[0].runId
    $candidateSha = 'a' * 40
    $candidateFixture.runs[0].commitSha = $candidateSha
    foreach ($profileName in @('light', 'full')) {
        $candidateFixture.runs[0].profiles.$profileName.summary.commitSha = $candidateSha
    }
    $candidatePath = Join-Path $testRoot 'candidate.json'
    $candidateOutput = Join-Path $testRoot 'candidate-result.json'
    Write-Fixture $candidateFixture $candidatePath
    & $verifier -FixturePath $candidatePath -CandidateRunId $candidateId -ExpectedCommitSha $candidateSha -OutputPath $candidateOutput
    $candidateReport = Get-Content -Raw -LiteralPath $candidateOutput | ConvertFrom-Json
    Assert-Equal 'READY' $candidateReport.status 'Both profiles for the exact candidate must pass.'

    # Exercise the online download/normalization path with local artifacts.
    # This catches dropping raw outcomes before profile validation.
    $downloadSource = Join-Path $testRoot 'download-source'
    foreach ($profileName in @('light', 'full')) {
        $profileRoot = Join-Path $downloadSource $profileName
        New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null
        $profile = $candidateFixture.runs[0].profiles.$profileName
        $profile.summary | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $profileRoot 'summary.json') -Encoding utf8
        foreach ($raw in $profile.rawReports) {
            $rawPath = Join-Path $profileRoot $raw.source
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $rawPath) | Out-Null
            $raw.report | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $rawPath -Encoding utf8
        }
    }
    $fakeRun = [pscustomobject]@{
        id = $candidateId; path = '.github/workflows/parity.yml'; status = 'completed'
        conclusion = 'success'; event = 'workflow_dispatch'; created_at = [DateTimeOffset]::UtcNow.ToString('o')
        head_sha = $candidateSha; html_url = 'https://example.invalid/parity-fixture'
    }
    $originalGh = Get-Item Function:gh -ErrorAction SilentlyContinue
    try {
        function gh {
            $arguments = @($args)
            $global:LASTEXITCODE = 0
            if ($arguments[0] -eq 'api') { return $fakeRun | ConvertTo-Json }
            if ($arguments[0] -ne 'run' -or $arguments[1] -ne 'download') { throw 'Unexpected fixture gh command.' }
            $artifactName = $arguments[[Array]::IndexOf($arguments, '--name') + 1]
            $destination = $arguments[[Array]::IndexOf($arguments, '--dir') + 1]
            $profileName = $artifactName -replace '^parity-|\-reports$', ''
            Get-ChildItem -LiteralPath (Join-Path $downloadSource $profileName) | Copy-Item -Destination $destination -Recurse
        }
        & $verifier -Repository 'IoTSharp/SonnetDB' -CandidateRunId $candidateId -ExpectedCommitSha $candidateSha -OutputPath $candidateOutput
        Assert-Equal 'READY' (Get-Content -LiteralPath $candidateOutput -Raw | ConvertFrom-Json).status 'Downloaded complete raw evidence must validate.'
        $raw = $candidateFixture.runs[0].profiles.full.rawReports[0]
        $raw.report.scenarios[0].backends[0].status = 'fail'
        $raw.report | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path (Join-Path $downloadSource 'full') $raw.source) -Encoding utf8
        & $verifier -Repository 'IoTSharp/SonnetDB' -CandidateRunId $candidateId -ExpectedCommitSha $candidateSha -OutputPath $candidateOutput -AllowNotReady
        $downloadFailure = Get-Content -LiteralPath $candidateOutput -Raw | ConvertFrom-Json
        Assert-Equal 'NOT_READY' $downloadFailure.status 'A downloaded raw failure cannot disappear during normalization.'
        Assert-ContainsCode $downloadFailure.profiles[1].issues 'raw_scenario_failed' 'Downloaded failures must retain the raw failure reason.'
        $raw.report.scenarios[0].backends[0].status = 'pass'
    }
    finally {
        Remove-Item Function:gh
        if ($null -ne $originalGh) { Set-Item Function:gh $originalGh.ScriptBlock }
    }
    & $verifier -FixturePath $candidatePath -CandidateRunId $candidateId -ExpectedCommitSha ('b' * 40) -OutputPath $candidateOutput -AllowNotReady
    Assert-Equal 'NOT_READY' (Get-Content -Raw -LiteralPath $candidateOutput | ConvertFrom-Json).status 'Another commit cannot satisfy candidate evidence.'
    $candidateFixture.runs[0].profiles.full.artifactPresent = $false
    Write-Fixture $candidateFixture $candidatePath
    & $verifier -FixturePath $candidatePath -CandidateRunId $candidateId -ExpectedCommitSha $candidateSha -OutputPath $candidateOutput -AllowNotReady
    Assert-Equal 'NOT_READY' (Get-Content -Raw -LiteralPath $candidateOutput | ConvertFrom-Json).status 'A light-only dispatch cannot satisfy the full candidate gate.'

    Write-Host "Parity nightly evidence contract tests passed."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
