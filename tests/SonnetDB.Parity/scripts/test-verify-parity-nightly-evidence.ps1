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
    foreach ($run in $readyFixture.runs) {
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

    Write-Host "Parity nightly evidence contract tests passed."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
