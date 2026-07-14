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

    Write-Host "Parity summary contract tests passed."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
