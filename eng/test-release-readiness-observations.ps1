$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-readiness-policy.ps1')
$fixtureName = 'sonnetdb-release-observations-' + [Guid]::NewGuid().ToString('N')
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $fixtureName))
$fixtureSha = 'a' * 40
$fixtureRepository = 'IoTSharp/SonnetDB'
$fixtureVersion = '4.0.0'
$script:readinessApiResponses = @{}

# 用完整成功的候选证据驱动实际发布脚本；只替换网络和独立的 Parity 进程边界。
function Invoke-RestMethod {
    param([string]$Uri, [hashtable]$Headers, [int]$TimeoutSec)
    if (-not $readinessApiResponses.ContainsKey($Uri)) { throw "Unexpected fixture request: $Uri" }
    $readinessApiResponses[$Uri]
}

function Set-CandidateEvidence {
    param([string]$MissingWorkflow, [string]$FailedWorkflow)
    $script:readinessApiResponses = @{}
    $apiRoot = "https://api.github.com/repos/$fixtureRepository"
    $runId = 0
    foreach ($policy in Get-ReleaseWorkflowPolicy -Version $fixtureVersion) {
        $runId++
        $run = [pscustomobject]@{
            id = $runId; head_sha = $fixtureSha; repository = @{ full_name = $fixtureRepository }
            path = ".github/workflows/$($policy.File)"; status = 'completed'
            conclusion = if ($policy.File -eq $FailedWorkflow) { 'failure' } else { 'success' }
            event = 'workflow_dispatch'; html_url = "$apiRoot/actions/runs/$runId"
            created_at = '2026-09-01T00:00:00Z'; run_started_at = '2026-09-01T00:00:00Z'; run_attempt = 1
        }
        $runs = if ($policy.File -eq $MissingWorkflow) { @() } else { @($run) }
        $script:readinessApiResponses["$apiRoot/actions/workflows/$($policy.File)/runs?head_sha=$fixtureSha&per_page=100&page=1"] = @{ workflow_runs = $runs }
        $jobs = @($policy.Jobs | ForEach-Object {
            $jobName = $_
            $steps = if ($jobName -eq 'soak') {
                if ($policy.File -eq 'document-soak.yml') { @('Run Document Store profile', 'Audit capacity evidence contract') }
                else { @('Run ecosystem profile') }
            } else { @(Get-RequiredReleaseSteps $jobName) }
            @{ name = $jobName; status = 'completed'; conclusion = 'success'; steps = @($steps | ForEach-Object { @{ name = $_; conclusion = 'success' } }) }
        })
        $script:readinessApiResponses["$apiRoot/actions/runs/$runId/jobs?filter=latest&per_page=100&page=1"] = @{ jobs = $jobs }
        $script:readinessApiResponses["$apiRoot/actions/runs/$runId/artifacts?per_page=100&page=1"] = @{
            artifacts = @($policy.Artifacts | ForEach-Object {
                @{ name = $_.Replace('*', 'fixture'); expired = $false; size_in_bytes = 42; created_at = '2026-09-01T00:10:00Z' }
            })
        }
    }
}

try {
    $fixtureEng = Join-Path $fixtureRoot 'eng'
    $fixtureParity = Join-Path $fixtureRoot 'tests/SonnetDB.Parity/scripts'
    $null = New-Item -ItemType Directory -Path $fixtureEng, $fixtureParity -Force
    foreach ($file in @('verify-release-readiness.ps1', 'release-readiness-policy.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $fixtureEng $file)
    }
    @'
param([string]$Repository, [string]$OutputPath, [string]$CandidateRunId, [string]$ExpectedCommitSha, [switch]$AllowNotReady)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$scenario = Get-Content -LiteralPath (Join-Path $root 'scenario.json') -Raw | ConvertFrom-Json
if ($CandidateRunId) {
    @{ runId = $CandidateRunId; sha = $ExpectedCommitSha; token = $env:GH_TOKEN; allowNotReady = [bool]$AllowNotReady } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $root 'candidate-calls.jsonl')
    if ($AllowNotReady) { throw 'Candidate verification must remain blocking.' }
    if ($scenario.CandidateFailure) { throw 'Fixture candidate raw Parity evidence rejected.' }
    @{ status = 'READY'; source = 'github'; commitSha = $ExpectedCommitSha } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath
    return
}
if ($scenario.Nightly -eq 'unavailable') {
    $global:LASTEXITCODE = 23
    throw 'Fixture GitHub observation request failed.'
}
if ($scenario.Nightly -eq 'malformed') {
    '{invalid-json' | Set-Content -LiteralPath $OutputPath
    return
}
$status = if ($scenario.ValidRuns -eq 7) { 'READY' } else { 'NOT_READY' }
$issues = if ($status -eq 'READY') { @() } else { @(@{ code = 'scheduled_run_failed_validation'; message = 'Fixture observation is incomplete or contains a failed scheduled run.' }) }
@{ status = $status; source = 'github'; repository = $Repository; validRunCount = $scenario.ValidRuns; requiredRunCount = 7; issues = $issues } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath
if ($status -ne 'READY' -and -not $AllowNotReady) { throw 'Fixture seven-day window is NOT_READY.' }
'@ | Set-Content -LiteralPath (Join-Path $fixtureParity 'verify-parity-nightly-evidence.ps1') -Encoding utf8

    $scenarios = @(
        @{ Name = 'Complete candidate with 1/7 observation'; Nightly = 'available'; ValidRuns = 1; Expected = 'READY'; Observation = 'NOT_READY' }
        @{ Name = 'Complete candidate without a seven-day window'; Nightly = 'available'; ValidRuns = 0; Expected = 'READY'; Observation = 'NOT_READY' }
        @{ Name = 'Complete candidate with seven valid days'; Nightly = 'available'; ValidRuns = 7; Expected = 'READY'; Observation = 'READY' }
        @{ Name = 'Unavailable observation and native exit code do not block'; Nightly = 'unavailable'; Expected = 'READY'; Observation = 'UNAVAILABLE' }
        @{ Name = 'Malformed observation does not block'; Nightly = 'malformed'; Expected = 'READY'; Observation = 'UNAVAILABLE' }
        @{ Name = 'Candidate raw Parity failure still blocks'; Nightly = 'available'; ValidRuns = 7; CandidateFailure = $true; Expected = 'NOT_READY'; Observation = 'READY' }
        @{ Name = 'Missing candidate Parity still blocks'; Nightly = 'available'; ValidRuns = 7; MissingWorkflow = 'parity.yml'; Expected = 'NOT_READY'; Observation = 'READY' }
        @{ Name = 'Missing candidate CI still blocks'; Nightly = 'available'; ValidRuns = 1; MissingWorkflow = 'ci.yml'; Expected = 'NOT_READY'; Observation = 'NOT_READY' }
        @{ Name = 'Failed candidate CI still blocks'; Nightly = 'available'; ValidRuns = 1; FailedWorkflow = 'ci.yml'; Expected = 'NOT_READY'; Observation = 'NOT_READY' }
    )
    $reportPath = Join-Path $fixtureRoot 'output/report.json'
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force
    foreach ($scenario in $scenarios) {
        Set-CandidateEvidence -MissingWorkflow $scenario.MissingWorkflow -FailedWorkflow $scenario.FailedWorkflow
        $scenario | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot 'scenario.json') -Encoding utf8
        $callsPath = Join-Path $fixtureRoot 'candidate-calls.jsonl'
        if (Test-Path -LiteralPath $callsPath) { Remove-Item -LiteralPath $callsPath }
        # 预放旧的成功报告，确认观察请求失败后不会借用旧状态。
        '{"source":"github","status":"READY","validRunCount":7,"requiredRunCount":7}' |
            Set-Content -LiteralPath (Join-Path $fixtureRoot 'output/parity-nightly.json') -Encoding utf8
        $previousToken = $env:GH_TOKEN
        $previousExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
        $global:LASTEXITCODE = 0
        $env:GH_TOKEN = 'fixture-original-token'
        $thrown = $null
        try {
            try {
                & (Join-Path $fixtureEng 'verify-release-readiness.ps1') -Repository $fixtureRepository -CommitSha $fixtureSha -Version $fixtureVersion -Token 'fixture-release-token' -OutputPath $reportPath
            }
            catch { $thrown = $_.Exception.Message }
            if ($global:LASTEXITCODE -ne 0) { throw 'Optional observation leaked a native failure to the Actions wrapper.' }
            if ($env:GH_TOKEN -ne 'fixture-original-token') { throw 'Verifier did not restore GH_TOKEN.' }
        }
        finally {
            $env:GH_TOKEN = $previousToken
            if ($null -eq $previousExitCode) { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
            else { $global:LASTEXITCODE = $previousExitCode }
        }
        if ($scenario.Expected -eq 'READY' -and $thrown) {
            $diagnostics = Get-Content -LiteralPath $reportPath -Raw
            throw "Unexpected failure for $($scenario.Name): $thrown`n$diagnostics"
        }
        if ($scenario.Expected -eq 'NOT_READY' -and $thrown -notlike 'Release is NOT_READY.*') { throw "Expected release rejection for $($scenario.Name); got: $thrown" }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if ($report.status -ne $scenario.Expected -or @($report.workflows).Count -ne 12) { throw "Candidate status or workflow inventory mismatch: $($scenario.Name)" }
        $observation = @($report.observations)[0]
        if (@($report.observations).Count -ne 1 -or $observation.name -ne 'scheduled-parity-seven-days' -or $observation.blocking -ne $false -or $observation.status -ne $scenario.Observation) {
            throw "Observation contract mismatch: $($scenario.Name)"
        }
        if ($scenario.Nightly -eq 'available' -and ($observation.validRunCount -ne $scenario.ValidRuns -or $observation.requiredRunCount -ne 7)) {
            throw 'The original observation counts must remain visible.'
        }
        if ($scenario.Observation -ne 'READY' -and @($observation.issues).Count -eq 0) { throw 'Incomplete or unavailable observations need diagnostics.' }
        if ($scenario.Nightly -eq 'unavailable' -and (Test-Path -LiteralPath $observation.reportPath)) { throw 'Stale nightly report was retained after an observation failure.' }
        if ($scenario.CandidateFailure -and @($report.issues | Where-Object { $_ -like '*Fixture candidate raw Parity evidence rejected.*' }).Count -ne 1) {
            throw 'Candidate raw evidence failure must remain a blocking issue.'
        }
        if ($scenario.Expected -eq 'READY' -and @($report.issues).Count -ne 0) { throw 'Observation diagnostics leaked into blocking issues.' }
        if ($scenario.MissingWorkflow -eq 'parity.yml') {
            if (Test-Path -LiteralPath $callsPath) { throw 'Missing candidate run must not be replaced by nightly evidence.' }
        }
        else {
            $calls = @(Get-Content -LiteralPath $callsPath | ForEach-Object { $_ | ConvertFrom-Json })
            if ($calls.Count -ne 1 -or $calls[0].sha -ne $fixtureSha -or $calls[0].token -ne 'fixture-release-token' -or $calls[0].allowNotReady) {
                throw 'Candidate verifier did not receive the required identity and blocking mode.'
            }
        }
        Write-Host "PASS: $($scenario.Name)"
    }
    Write-Host "Release observation orchestration tests passed ($($scenarios.Count) scenarios)."
}
finally {
    $expectedRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $fixtureName))
    if ($fixtureRoot -ne $expectedRoot -or [IO.Path]::GetFileName($fixtureRoot) -ne $fixtureName) { throw 'Refusing cleanup outside the test fixture directory.' }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
