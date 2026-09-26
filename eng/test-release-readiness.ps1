$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-readiness-policy.ps1')
$sha = 'a' * 40
$repository = 'IoTSharp/SonnetDB'
$policy = @(Get-ReleaseWorkflowPolicy | Where-Object File -EQ 'publish.yml')[0]
$baseline = [pscustomobject]@{
    run = [pscustomobject]@{ id = 1; head_sha = $sha; repository = @{ full_name = $repository }; path = '.github/workflows/publish.yml'; status = 'completed'; conclusion = 'success'; event = 'workflow_dispatch'; html_url = 'https://github.com/IoTSharp/SonnetDB/actions/runs/1'; run_started_at = '2026-09-01T00:00:00Z' }
    jobs = @($policy.Jobs | ForEach-Object { @{ name = $_; status = 'completed'; conclusion = 'success'; steps = @(Get-RequiredReleaseSteps $_ | ForEach-Object { @{ name = $_; conclusion = 'success' } }) } })
    artifacts = @($policy.Artifacts | ForEach-Object { @{ name = $_; expired = $false; size_in_bytes = 42; created_at = '2026-09-01T00:10:00Z' } })
}
function Assert-Readiness {
    param([object]$Evidence, [bool]$Expected, [string]$Name)
    $result = Test-ReleaseWorkflowEvidence $policy $Evidence $sha $repository
    if ($result.ready -ne $Expected) { throw "$Name readiness mismatch: $($result.issues -join '; ')" }
}
function Copy-Evidence { $baseline | ConvertTo-Json -Depth 12 | ConvertFrom-Json }
Assert-Readiness $baseline $true 'Complete candidate'
$case = Copy-Evidence; $case.run = $null; Assert-Readiness $case $false 'Missing run'
$case = Copy-Evidence; $case.run.head_sha = 'b' * 40; Assert-Readiness $case $false 'Stale commit'
$case = Copy-Evidence; $case.run.repository.full_name = 'other/repo'; Assert-Readiness $case $false 'Wrong repository'
$case = Copy-Evidence; $case.run.path = '.github/workflows/other.yml'; Assert-Readiness $case $false 'Wrong workflow'
$case = Copy-Evidence; $case.run.event = 'push'; Assert-Readiness $case $false 'Tag cannot validate itself'
$case = Copy-Evidence; $case.run.event = 'pull_request'; Assert-Readiness $case $false 'Pull request cannot be release evidence'
$case = Copy-Evidence; $case.run.status = 'in_progress'; Assert-Readiness $case $false 'Pending run'
$case = Copy-Evidence; $case.run.conclusion = 'failure'; Assert-Readiness $case $false 'Failed run'
$case = Copy-Evidence; $case.jobs[0].conclusion = 'skipped'; Assert-Readiness $case $false 'Skipped required job'
$case = Copy-Evidence; $case.jobs = @($case.jobs[1]); Assert-Readiness $case $false 'Missing matrix jobs'
$case = Copy-Evidence; $case.jobs += $case.jobs[0]; Assert-Readiness $case $false 'Duplicate job'
$case = Copy-Evidence; $case.jobs[0].steps[0].conclusion = 'failure'; Assert-Readiness $case $false 'Failed nested step'
$case = Copy-Evidence; $case.jobs[0].steps[0].conclusion = 'skipped'; Assert-Readiness $case $false 'Skipped validation step'
$case = Copy-Evidence; $case.artifacts = @(); Assert-Readiness $case $false 'Missing artifacts'
$case = Copy-Evidence; $case.artifacts[0].expired = $true; Assert-Readiness $case $false 'Expired artifact'
$case = Copy-Evidence; $case.artifacts[0].size_in_bytes = 0; Assert-Readiness $case $false 'Empty artifact'
$case = Copy-Evidence; $case.artifacts[0].created_at = '2026-08-31T00:00:00Z'; Assert-Readiness $case $false 'Earlier attempt artifact'
$case = Copy-Evidence; $case.jobs += @{ name = 'Publish verified NuGet packages'; status = 'completed'; conclusion = 'skipped'; steps = @() }; Assert-Readiness $case $true 'Expected nonpublishing dispatch'
if (@(Get-ReleaseWorkflowPolicy).Count -ne 12) { throw 'All 12 repository workflows must remain in the release policy.' }
$runs = @(
    @{ id = 1; created_at = '2026-09-01T00:00:00Z'; run_started_at = '2026-09-03T00:00:00Z'; updated_at = '2026-09-03T01:00:00Z'; run_attempt = 2; conclusion = 'failure' }
    @{ id = 2; created_at = '2026-09-02T00:00:00Z'; run_started_at = '2026-09-02T00:00:00Z'; updated_at = '2026-09-02T01:00:00Z'; run_attempt = 1; conclusion = 'success' }
)
if ((Select-LatestReleaseRun $runs).id -ne 1) { throw 'A newly failed rerun of an older run must supersede an earlier success.' }
$runs[0].conclusion = $null; $runs[0].updated_at = '2026-09-03T00:00:00Z'
if ((Select-LatestReleaseRun $runs).id -ne 1) { throw 'A pending rerun must not be hidden by an earlier success.' }
Write-Host 'Release readiness contract tests passed (21 cases and complete workflow inventory).'
