$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-readiness-policy.ps1')
$sha = 'a' * 40
$repository = 'IoTSharp/SonnetDB'
$version = '4.0.0'
$policy = @(Get-ReleaseWorkflowPolicy -Version $version | Where-Object File -EQ 'publish.yml')[0]
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
if (@(Get-ReleaseWorkflowPolicy -Version $version).Count -ne 12) { throw 'All 12 repository workflows must remain in the release policy.' }

# Exercise actual artifact identities from both publishing workflows. A successful
# dispatch of another package version at the same SHA must never validate this release.
foreach ($workflow in @('publish.yml', 'connectors-release.yml')) {
    $policy = @(Get-ReleaseWorkflowPolicy -Version $version | Where-Object File -EQ $workflow)[0]
    $case = Copy-Evidence
    $case.run.path = ".github/workflows/$workflow"
    $case.jobs = @($policy.Jobs | ForEach-Object {
        @{ name = $_; status = 'completed'; conclusion = 'success'; steps = @(Get-RequiredReleaseSteps $_ | ForEach-Object { @{ name = $_; conclusion = 'success' } }) }
    })
    $case.artifacts = @($policy.Artifacts | ForEach-Object {
        @{ name = $_; expired = $false; size_in_bytes = 42; created_at = '2026-09-01T00:10:00Z' }
    })
    Assert-Readiness $case $true "$workflow exact candidate version"
    foreach ($otherVersion in @('3.9.9', '4.0.0-rc.1', '4.0.1')) {
        $different = $case | ConvertTo-Json -Depth 12 | ConvertFrom-Json
        foreach ($artifact in $different.artifacts) { $artifact.name = $artifact.name.Replace($version, $otherVersion) }
        Assert-Readiness $different $false "$workflow rejects $otherVersion preflight"
    }
    $unversioned = $case | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    foreach ($artifact in $unversioned.artifacts) { $artifact.name = $artifact.name.Replace("-$version", '') }
    Assert-Readiness $unversioned $false "$workflow rejects legacy unversioned artifacts"
    $partial = $case | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $partial.artifacts[-1].name = $partial.artifacts[-1].name.Replace($version, '4.0.0-rc.1')
    Assert-Readiness $partial $false "$workflow rejects mixed matrix versions"
    $policy = @(Get-ReleaseWorkflowPolicy -Version '4.0.0-rc.1' | Where-Object File -EQ $workflow)[0]
    foreach ($artifact in $case.artifacts) { $artifact.name = $artifact.name.Replace($version, '4.0.0-rc.1') }
    Assert-Readiness $case $true "$workflow exact prerelease version"
}
foreach ($invalidVersion in @('4.0.0*', "4.0.0`n", 'v4.0.0', '')) {
    $rejected = $false
    try { Get-ReleaseWorkflowPolicy -Version $invalidVersion | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid candidate version must not become an artifact wildcard.' }
}
$runs = @(
    @{ id = 1; created_at = '2026-09-01T00:00:00Z'; run_started_at = '2026-09-03T00:00:00Z'; updated_at = '2026-09-03T01:00:00Z'; run_attempt = 2; conclusion = 'failure' }
    @{ id = 2; created_at = '2026-09-02T00:00:00Z'; run_started_at = '2026-09-02T00:00:00Z'; updated_at = '2026-09-02T01:00:00Z'; run_attempt = 1; conclusion = 'success' }
)
if ((Select-LatestReleaseRun $runs).id -ne 1) { throw 'A newly failed rerun of an older run must supersede an earlier success.' }
$runs[0].conclusion = $null; $runs[0].updated_at = '2026-09-03T00:00:00Z'
if ((Select-LatestReleaseRun $runs).id -ne 1) { throw 'A pending rerun must not be hidden by an earlier success.' }
Write-Host 'Release readiness contract tests passed (21 cases and complete workflow inventory).'
