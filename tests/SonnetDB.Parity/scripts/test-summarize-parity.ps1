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

$summarizer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../reports/summarize-parity.ps1"))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("sonnetdb-parity-summary-" + [Guid]::NewGuid().ToString("N"))
$reportRoot = Join-Path $testRoot "reports"
$successOutput = Join-Path $testRoot "success"
$failureOutput = Join-Path $testRoot "failure"
$malformedReportRoot = Join-Path $testRoot "malformed-reports"
$malformedOutput = Join-Path $testRoot "malformed-output"
$invalidShapeReportRoot = Join-Path $testRoot "invalid-shape-reports"
$invalidShapeOutput = Join-Path $testRoot "invalid-shape-output"

try {
    New-Item -ItemType Directory -Force -Path (Join-Path $reportRoot "suite") | Out-Null

    $report = [ordered]@{
        runId = "reporter-contract"
        capabilityGaps = @()
        scenarios = @(
            [ordered]@{
                name = "smoke"
                withinTolerance = $true
                differences = @()
                backends = @(
                    [ordered]@{
                        name = "sonnetdb"
                        status = "pass"
                        metrics = [ordered]@{}
                    }
                )
            }
        )
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $reportRoot "suite/report.json") -Encoding utf8

    & $summarizer `
        -ReportRoot $reportRoot `
        -OutputDirectory $successOutput `
        -Profile "test" `
        -CommitSha "success-sha"

    $success = Get-Content -Raw -Path (Join-Path $successOutput "summary.json") | ConvertFrom-Json
    Assert-Equal 2 $success.schemaVersion "汇总合同版本错误。"
    Assert-Equal "passing" $success.status "成功场景状态错误。"
    Assert-Equal 1 $success.totalScenarios "成功场景计数错误。"
    Assert-Equal 0 $success.gateFailures.Count "成功场景不应产生门禁失败。"

    $failureRaised = $false
    try {
        & $summarizer `
            -ReportRoot $reportRoot `
            -OutputDirectory $failureOutput `
            -Profile "test" `
            -CommitSha "failure-sha" `
            -StackExitCode 1
    }
    catch {
        $failureRaised = $true
    }

    Assert-Equal $true $failureRaised "基础设施失败时汇总器必须返回失败。"
    $failure = Get-Content -Raw -Path (Join-Path $failureOutput "summary.json") | ConvertFrom-Json
    Assert-Equal "failing" $failure.status "基础设施失败状态错误。"
    Assert-Equal 1 $failure.totalScenarios "基础设施失败不应丢失已有场景。"
    Assert-Equal "infrastructure" $failure.gateFailures[0].gate "基础设施门禁分类错误。"
    Assert-Equal "stack_start_failed" $failure.gateFailures[0].gap_reason "基础设施 gap_reason 错误。"

    $failureMarkdown = Get-Content -Raw -Path (Join-Path $failureOutput "summary.md")
    if (-not $failureMarkdown.Contains("stack_start_failed", [StringComparison]::Ordinal)) {
        throw "Markdown 汇总未包含结构化 gap_reason。"
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $malformedReportRoot "broken-suite") | Out-Null
    "{ not valid json" | Set-Content -Path (Join-Path $malformedReportRoot "broken-suite/report.json") -Encoding utf8
    $malformedRaised = $false
    try {
        & $summarizer `
            -ReportRoot $malformedReportRoot `
            -OutputDirectory $malformedOutput `
            -Profile "test" `
            -CommitSha "malformed-sha"
    }
    catch {
        $malformedRaised = $true
    }

    Assert-Equal $true $malformedRaised "Malformed parity reports must fail the summarizer gate."
    $malformedSummaryPath = Join-Path $malformedOutput "summary.json"
    Assert-Equal $true (Test-Path -LiteralPath $malformedSummaryPath) "Malformed parity reports must still produce summary.json."
    $malformed = Get-Content -Raw -Path $malformedSummaryPath | ConvertFrom-Json
    Assert-Equal "failing" $malformed.status "Malformed parity reports must produce a failing summary."
    Assert-Equal "parity_report_parse_failed" $malformed.gateFailures[0].gap_reason "Malformed report gap_reason 错误。"
    $malformedMarkdown = Get-Content -Raw -Path (Join-Path $malformedOutput "summary.md")
    if (-not $malformedMarkdown.Contains("parity_report_parse_failed", [StringComparison]::Ordinal)) {
        throw "Malformed report Markdown 汇总未包含结构化 gap_reason。"
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $invalidShapeReportRoot "shape-suite") | Out-Null
    '{"runId":"shape","scenarios":{},"capabilityGaps":{}}' |
        Set-Content -Path (Join-Path $invalidShapeReportRoot "shape-suite/report.json") -Encoding utf8
    $invalidShapeRaised = $false
    try {
        & $summarizer -ReportRoot $invalidShapeReportRoot -OutputDirectory $invalidShapeOutput -Profile "test" -CommitSha "invalid-shape-sha"
    }
    catch {
        $invalidShapeRaised = $true
    }

    Assert-Equal $true $invalidShapeRaised "Object-shaped parity fields must fail the summarizer gate."
    $invalidShape = Get-Content -Raw -Path (Join-Path $invalidShapeOutput "summary.json") | ConvertFrom-Json
    Assert-Equal "failing" $invalidShape.status "Object-shaped parity fields must produce a failing summary."
    Assert-Equal "parity_report_parse_failed" $invalidShape.gateFailures[0].gap_reason "Object-shaped report gap_reason 错误。"

    $referenceRoot = Join-Path $testRoot "reference-reports"
    New-Item -ItemType Directory -Force -Path (Join-Path $referenceRoot "suite") | Out-Null
    $referencePath = Join-Path $referenceRoot "suite/report.json"
    $fullReferences = @("postgres", "redis", "minio", "nats", "influxdb", "victoriametrics", "meilisearch", "qdrant", "clickhouse", "mongodb")
    foreach ($profile in @("light", "full")) {
        $required = if ($profile -eq "full") { $fullReferences } else { $fullReferences[0..3] }
        $referenceReport = [ordered]@{
            runId = "reference-contract"
            capabilityGaps = @()
            scenarios = @($required | ForEach-Object {
                [ordered]@{
                    name = "reference-$_"
                    withinTolerance = $true
                    differences = @()
                    backends = @([ordered]@{ backend = $_; status = "pass"; metrics = @{} })
                }
            })
        }
        # Optional profile services, unsupported capabilities, and external Graph
        # comparisons retain their explicit boundaries when required services run.
        $referenceReport.scenarios += [ordered]@{
            name = "capability-gap"
            differences = @()
            backends = @([ordered]@{ backend = "postgres"; status = "skipped"; gapReason = "backend lacks required capabilities: RelationalTpccLite" })
        }
        $referenceReport.scenarios += [ordered]@{
            name = "graph-deferred"
            differences = @()
            backends = @([ordered]@{ backend = "postgres"; status = "not_run"; gapReason = "external SQL/PGQ parity deferred to the target environment" })
        }
        if ($profile -eq "light") {
            $referenceReport.scenarios += [ordered]@{
                name = "optional-clickhouse"
                differences = @()
                backends = @([ordered]@{ backend = "clickhouse"; status = "skipped"; gapReason = "clickhouse unreachable" })
            }
        }
        $referenceReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $referencePath -Encoding utf8
        $referenceOutput = Join-Path $testRoot "$profile-references"
        & $summarizer -ReportRoot $referenceRoot -OutputDirectory $referenceOutput -Profile $profile
        $referenceSummary = Get-Content -LiteralPath (Join-Path $referenceOutput "summary.json") -Raw | ConvertFrom-Json
        Assert-Equal "passing" $referenceSummary.status "Available $profile references and explicit capability/profile skips must pass."

        for ($index = 0; $index -lt $required.Count; $index++) {
            $backend = $referenceReport.scenarios[$index].backends[0]
            $backend.status = "skipped"
            $backend.gapReason = "$($backend.backend) unreachable"
            # Infrastructure failure must not disappear behind warning-only metrics.
            $backend.metrics = @{ performance_gating = "warning_only" }
            $referenceReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $referencePath -Encoding utf8
            $referenceRaised = $false
            try { & $summarizer -ReportRoot $referenceRoot -OutputDirectory $referenceOutput -Profile $profile }
            catch { $referenceRaised = $true }
            Assert-Equal $true $referenceRaised "Unreachable required $profile reference $($backend.backend) must fail."
            $referenceSummary = Get-Content -LiteralPath (Join-Path $referenceOutput "summary.json") -Raw | ConvertFrom-Json
            Assert-Equal 1 $referenceSummary.failedScenarios "Unreachable reference must remain in the failed scenario denominator."
            Assert-Equal $true (@($referenceSummary.gateFailures.gap_reason) -contains "required_reference_unreachable") "Required reference infrastructure failure is missing."
            Assert-Equal $true (@($referenceSummary.gateFailures.gap_reason) -contains "required_reference_not_executed") "Missing reference execution must fail closed."
            $backend.status = "pass"
            $backend.Remove("gapReason")
            $backend.metrics = @{}
        }

        $referenceReport.scenarios = @($referenceReport.scenarios | Where-Object { $_.name -ne "reference-postgres" })
        $referenceReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $referencePath -Encoding utf8
        $missingRaised = $false
        try { & $summarizer -ReportRoot $referenceRoot -OutputDirectory $referenceOutput -Profile $profile }
        catch { $missingRaised = $true }
        Assert-Equal $true $missingRaised "An omitted required reference report must fail even when a deferred Graph outcome exists."
    }

    Write-Host "Parity summary contract tests passed."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
