param(
    [Parameter(Mandatory = $true)]
    [string] $ReportPath,
    [switch] $RequireReady
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$resolved = (Resolve-Path -LiteralPath $ReportPath).Path
$report = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json

function Fail-Report([string] $Reason) {
    [pscustomobject]@{ status = 'INVALID'; reason = $Reason } | ConvertTo-Json -Compress
    throw $Reason
}

if ([string]$report.schema -ne 'm27-copilot-eval-v1') { Fail-Report 'schema must be m27-copilot-eval-v1.' }
if ([string]$report.status -notin @('PASS', 'NOT_READY')) { Fail-Report 'status must be PASS or NOT_READY.' }
if ($null -eq $report.run -or $null -eq $report.readiness) { Fail-Report 'run and readiness are required.' }
if ([bool]$report.run.realProvider -and [string]$report.readiness.status -ne 'PASS') {
    Fail-Report 'realProvider=true requires readiness.status=PASS.'
}
if ([string]$report.status -eq 'NOT_READY' -and [string]::IsNullOrWhiteSpace([string]$report.readiness.reason)) {
    Fail-Report 'NOT_READY reports require readiness.reason.'
}
if ($RequireReady -and [string]$report.status -ne 'PASS') { Fail-Report 'report is NOT_READY; real provider evidence is required.' }

$requiredCategories = @('anomaly_device', 'slow_query', 'schema', 'repair_advice', 'approval')
$scenarios = @($report.scenarios)
if ($scenarios.Count -lt $requiredCategories.Count) { Fail-Report 'report must contain five required M27 scenarios.' }
foreach ($category in $requiredCategories) {
    if (@($scenarios | Where-Object { [string]$_.category -eq $category }).Count -eq 0) {
        Fail-Report "missing scenario category '$category'."
    }
}

foreach ($scenario in $scenarios) {
    foreach ($field in @('id', 'category', 'status', 'provider', 'model', 'toolNames', 'toolCalls', 'usage')) {
        if ($null -eq $scenario.$field) { Fail-Report "scenario '$($scenario.id)' is missing '$field'." }
    }
    if ([string]$scenario.status -notin @('PASS', 'FAIL', 'NOT_READY')) { Fail-Report "scenario '$($scenario.id)' has invalid status." }
    if ([int]$scenario.toolCalls -lt 0 -or @($scenario.toolNames).Count -eq 0) { Fail-Report "scenario '$($scenario.id)' must record tool calls." }
    if ([string]$scenario.status -ne 'PASS' -and [string]::IsNullOrWhiteSpace([string]$scenario.failureReason)) {
        Fail-Report "non-PASS scenario '$($scenario.id)' requires failureReason."
    }
    $usage = $scenario.usage
    foreach ($field in @('reported', 'inputTokens', 'outputTokens', 'totalTokens', 'costUsd')) {
        if ($null -eq $usage.PSObject.Properties[$field]) { Fail-Report "scenario '$($scenario.id)' usage is missing '$field'." }
    }
    if ([bool]$usage.reported) {
        foreach ($field in @('inputTokens', 'outputTokens', 'totalTokens', 'costUsd')) {
            if ($null -eq $usage.$field -or [double]$usage.$field -lt 0) { Fail-Report "reported usage for '$($scenario.id)' must be non-negative." }
        }
    } elseif ($null -ne $usage.inputTokens -or $null -ne $usage.outputTokens -or $null -ne $usage.totalTokens -or $null -ne $usage.costUsd) {
        Fail-Report "unreported usage for '$($scenario.id)' must remain null; do not estimate model cost."
    }
}

$summary = $report.summary
if ($null -eq $summary -or [int]$summary.scenarioCount -ne $scenarios.Count) { Fail-Report 'summary.scenarioCount must match scenarios.' }
if ([int]$summary.passedCount + [int]$summary.failedCount -ne $scenarios.Count) { Fail-Report 'summary pass/fail counts do not add up.' }
if ([double]$summary.totalTokens -ne [double]$summary.totalInputTokens + [double]$summary.totalOutputTokens) { Fail-Report 'summary token totals do not add up.' }

[pscustomobject]@{
    status = if ([string]$report.status -eq 'PASS') { 'PASS' } else { 'VALID_NOT_READY' }
    schema = $report.schema
    scenarioCount = $scenarios.Count
    realProvider = [bool]$report.run.realProvider
    readiness = $report.readiness.status
    totalTokens = $summary.totalTokens
    estimatedCostUsd = $summary.estimatedCostUsd
} | ConvertTo-Json -Compress
