param(
    [Parameter(Mandatory = $true)]
    [string] $ReportRoot,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Profile,

    [string] $Repository = "",

    [string] $RunId = "",

    [string] $RunNumber = "",

    [string] $CommitSha = "",

    [int] $ParityTestExitCode = 0,

    [int] $ReliabilityTestExitCode = 0
)

$ErrorActionPreference = "Stop"

function Test-PerformanceOnlyScenario {
    param([object] $Scenario)

    foreach ($backend in $Scenario.backends) {
        if ($null -eq $backend.metrics) {
            continue
        }

        $metric = $backend.metrics.performance_gating
        if ($metric -eq "warning_only") {
            return $true
        }
    }

    return $false
}

function Get-GateClass {
    param([string] $ScenarioName, [object] $Backend)

    $required = ""
    if ($null -ne $script:RequiredByScenario -and $script:RequiredByScenario.ContainsKey($ScenarioName)) {
        $required = [string]$script:RequiredByScenario[$ScenarioName]
    }

    if ($required -match "Accuracy|Quantile|Percentile|Derivative|RateIrate|DistinctCount|HoltWinters|Hnsw|Fulltext|Analytics") {
        return "accuracy"
    }

    return "capability"
}

function Add-GateFailure {
    param(
        [System.Collections.Generic.List[object]] $Failures,
        [string] $Gate,
        [string] $Suite,
        [string] $Scenario,
        [string] $Reason
    )

    $Failures.Add([ordered]@{
        gate = $Gate
        suite = $Suite
        scenario = $Scenario
        reason = $Reason
    })
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$reportFiles = @()
if (Test-Path $ReportRoot) {
    $reportFiles = Get-ChildItem -Path $ReportRoot -Filter report.json -Recurse | Sort-Object FullName
}

$totalScenarios = 0
$passedScenarios = 0
$skippedScenarios = 0
$failedScenarios = 0
$warningOnlyScenarios = 0
$suiteRows = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[object]
$performanceWarnings = New-Object System.Collections.Generic.List[object]

foreach ($file in $reportFiles) {
    $report = Get-Content -Raw -Path $file.FullName | ConvertFrom-Json
    $suiteName = [string]$report.runId
    $script:RequiredByScenario = @{}
    foreach ($gap in $report.capabilityGaps) {
        $script:RequiredByScenario[[string]$gap.scenario] = [string]$gap.required
    }

    $suiteTotal = 0
    $suitePass = 0
    $suiteSkip = 0
    $suiteFail = 0

    foreach ($scenario in $report.scenarios) {
        $suiteTotal++
        $totalScenarios++

        $backendStatuses = @($scenario.backends | ForEach-Object { [string]$_.status })
        $hasFail = $backendStatuses -contains "fail"
        $hasSkip = $backendStatuses -contains "skipped"
        $allSkipped = $backendStatuses.Count -gt 0 -and (@($backendStatuses | Where-Object { $_ -eq "skipped" }).Count -eq $backendStatuses.Count)
        $isWarningOnly = Test-PerformanceOnlyScenario $scenario

        if ($isWarningOnly) {
            $warningOnlyScenarios++
            $performanceWarnings.Add([ordered]@{
                suite = $suiteName
                scenario = [string]$scenario.name
                reason = "performance metrics are warning only"
            })
        }

        if ($hasFail -or ($scenario.withinTolerance -eq $false -and -not $isWarningOnly)) {
            $failedScenarios++
            $suiteFail++
            $reason = if ($scenario.differences.Count -gt 0) { ($scenario.differences -join "; ") } else { "backend reported fail" }
            $failedBackends = @($scenario.backends | Where-Object { $_.status -eq "fail" })
            if ($failedBackends.Count -gt 0) {
                $gate = Get-GateClass ([string]$scenario.name) $failedBackends[0]
            } else {
                $gate = "accuracy"
            }
            Add-GateFailure $failures $gate $suiteName ([string]$scenario.name) $reason
            continue
        }

        if ($hasSkip -or $allSkipped) {
            $skippedScenarios++
            $suiteSkip++
            continue
        }

        $passedScenarios++
        $suitePass++
    }

    $suiteRows.Add([ordered]@{
        suite = $suiteName
        total = $suiteTotal
        passed = $suitePass
        skipped = $suiteSkip
        failed = $suiteFail
        source = Resolve-Path -Relative $file.FullName
    })
}

if ($ParityTestExitCode -ne 0) {
    Add-GateFailure $failures "capability" "dotnet-test" "parity" "dotnet test exited with code $ParityTestExitCode"
}

if ($ReliabilityTestExitCode -ne 0) {
    Add-GateFailure $failures "reliability" "dotnet-test" "crash-reliability" "crash reliability test exited with code $ReliabilityTestExitCode"
}

if ($totalScenarios -eq 0 -and $ParityTestExitCode -eq 0) {
    Add-GateFailure $failures "capability" "reporting" "parity-report" "no parity report.json files were found"
}

$passRate = if ($totalScenarios -eq 0) { 0 } else { [Math]::Round(($passedScenarios + $skippedScenarios) * 100.0 / $totalScenarios, 2) }
$status = if ($failures.Count -eq 0) { "passing" } else { "failing" }
$badgeColor = if ($failures.Count -eq 0) { "brightgreen" } else { "red" }
$badgeUrl = "https://img.shields.io/badge/parity-$passRate%25-$badgeColor"

$summary = [ordered]@{
    schemaVersion = 1
    label = "parity"
    message = "$passRate%"
    color = $badgeColor
    profile = $Profile
    status = $status
    passRate = $passRate
    totalScenarios = $totalScenarios
    passedScenarios = $passedScenarios
    skippedScenarios = $skippedScenarios
    failedScenarios = $failedScenarios
    warningOnlyScenarios = $warningOnlyScenarios
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    repository = $Repository
    runId = $RunId
    runNumber = $RunNumber
    commitSha = $CommitSha
    badgeUrl = $badgeUrl
    suites = $suiteRows
    gateFailures = $failures
    performanceWarnings = $performanceWarnings
}

$summaryJsonPath = Join-Path $OutputDirectory "summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryJsonPath -Encoding utf8

$summaryMdPath = Join-Path $OutputDirectory "summary.md"
$md = New-Object System.Collections.Generic.List[string]
$md.Add("# SonnetDB Parity Summary")
$md.Add("")
$md.Add("| Field | Value |")
$md.Add("|---|---|")
$md.Add("| Profile | $Profile |")
$md.Add("| Status | $status |")
$md.Add("| Pass rate | $passRate% |")
$md.Add("| Scenarios | $passedScenarios passed / $skippedScenarios skipped / $failedScenarios failed / $totalScenarios total |")
$md.Add("| Warning-only performance scenarios | $warningOnlyScenarios |")
$md.Add("| Commit | $CommitSha |")
$md.Add("| GitHub run | $RunId |")
$md.Add("")
$md.Add("## Suites")
$md.Add("")
$md.Add("| Suite | Passed | Skipped | Failed | Total |")
$md.Add("|---|---:|---:|---:|---:|")
foreach ($suite in $suiteRows) {
    $md.Add("| $($suite.suite) | $($suite.passed) | $($suite.skipped) | $($suite.failed) | $($suite.total) |")
}
$md.Add("")
$md.Add("## Gate Failures")
$md.Add("")
if ($failures.Count -eq 0) {
    $md.Add("No capability, reliability, or accuracy gate failures.")
} else {
    $md.Add("| Gate | Suite | Scenario | Reason |")
    $md.Add("|---|---|---|---|")
    foreach ($failure in $failures) {
        $reason = ([string]$failure.reason).Replace("|", "\|")
        $md.Add("| $($failure.gate) | $($failure.suite) | $($failure.scenario) | $reason |")
    }
}
$md.Add("")
$md.Add("## Performance Warnings")
$md.Add("")
if ($performanceWarnings.Count -eq 0) {
    $md.Add("No warning-only performance scenarios were reported.")
} else {
    $md.Add("| Suite | Scenario | Note |")
    $md.Add("|---|---|---|")
    foreach ($warning in $performanceWarnings) {
        $md.Add("| $($warning.suite) | $($warning.scenario) | $($warning.reason) |")
    }
}

$md | Set-Content -Path $summaryMdPath -Encoding utf8

if ($failures.Count -ne 0) {
    Write-Error "Parity gates failed. See $summaryMdPath."
}
