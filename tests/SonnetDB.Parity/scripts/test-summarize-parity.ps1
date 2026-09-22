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

    Write-Host "Parity summary contract tests passed."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
