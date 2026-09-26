[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'IoTSharp/SonnetDB',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CommitSha,
    [Parameter(Mandatory)]
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?\z')]
    [string]$Version,
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$OutputPath = 'artifacts/release-readiness/report.json'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-readiness-policy.ps1')
$headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'SonnetDB-release-readiness' }
if ($Token) { $headers.Authorization = "Bearer $Token" }
$apiRoot = "https://api.github.com/repos/$Repository"

function Get-ReleaseApi {
    param([string]$Path)
    Invoke-RestMethod -Uri "$apiRoot/$Path" -Headers $headers -TimeoutSec 45
}

function Get-ReleaseApiPages {
    param([string]$Path, [string]$Collection)
    $separator = if ($Path.Contains('?')) { '&' } else { '?' }
    for ($page = 1; $page -le 100; $page++) {
        $response = Get-ReleaseApi "$Path${separator}per_page=100&page=$page"
        $items = @($response.$Collection)
        $items
        if ($items.Count -lt 100) { return }
    }
    throw "Pagination limit reached for $Path; refusing incomplete evidence."
}

$checks = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
foreach ($policy in Get-ReleaseWorkflowPolicy -Version $Version) {
    try {
        # Deliberately include queued and failed attempts: an older green run
        # must never hide a newer failed rerun of the release commit.
        $runs = @(Get-ReleaseApiPages "actions/workflows/$($policy.File)/runs?head_sha=$CommitSha" 'workflow_runs')
        $eligible = @($runs | Where-Object {
            $_.head_sha -eq $CommitSha -and $_.event -in @('push', 'workflow_dispatch', 'schedule') `
                -and (-not $policy.DispatchOnly -or $_.event -eq 'workflow_dispatch')
        })
        $run = Select-LatestReleaseRun $eligible
        $jobs = @(); $artifacts = @()
        if ($run) {
            $jobs = @(Get-ReleaseApiPages "actions/runs/$($run.id)/jobs?filter=latest" 'jobs')
            $artifacts = @(Get-ReleaseApiPages "actions/runs/$($run.id)/artifacts" 'artifacts')
        }
        $checks.Add((Test-ReleaseWorkflowEvidence $policy ([pscustomobject]@{ run = $run; jobs = $jobs; artifacts = $artifacts }) $CommitSha $Repository))
    }
    catch {
        $checks.Add([pscustomobject]@{ workflow = $policy.File; ready = $false; runId = $null; url = $null; issues = @("GitHub evidence unavailable: $($_.Exception.Message)") })
    }
}

$oldGhToken = $env:GH_TOKEN
try {
    if ($Token) { $env:GH_TOKEN = $Token }
    $parityVerifier = Join-Path $PSScriptRoot '../tests/SonnetDB.Parity/scripts/verify-parity-nightly-evidence.ps1'
    $nightlyPath = Join-Path $outputDirectory 'parity-nightly.json'
    try {
        & $parityVerifier -Repository $Repository -OutputPath $nightlyPath
        $nightly = Get-Content -Raw -LiteralPath $nightlyPath | ConvertFrom-Json
        if ($nightly.source -ne 'github' -or $nightly.status -ne 'READY') { throw 'Nightly evidence did not pass online verification.' }
    }
    catch { $errors.Add("Seven consecutive scheduled Parity runs: $($_.Exception.Message)") }
    $parity = $checks | Where-Object workflow -EQ 'parity.yml'
    if ($parity.ready) {
        try {
            & $parityVerifier -Repository $Repository -CandidateRunId ([string]$parity.runId) -ExpectedCommitSha $CommitSha -OutputPath (Join-Path $outputDirectory 'parity-candidate.json')
        }
        catch { $errors.Add("Candidate Parity artifacts: $($_.Exception.Message)") }
    }
}
finally { $env:GH_TOKEN = $oldGhToken }

$ready = @($checks | Where-Object { -not $_.ready }).Count -eq 0 -and $errors.Count -eq 0
$report = [ordered]@{
    schemaVersion = 1; status = if ($ready) { 'READY' } else { 'NOT_READY' }
    repository = $Repository; commitSha = $CommitSha; version = $Version; source = 'github'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    workflows = $checks.ToArray(); issues = $errors.ToArray()
    boundary = 'Workflow success verifies the executed profiles. Quick profiles do not establish fixed-hardware capacity, clean offline installation, model quality or production acceptance.'
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $outputFullPath -Encoding utf8
Write-Host "Release readiness: $($report.status). Report: $outputFullPath"
if (-not $ready) { throw 'Release is NOT_READY. Resolve every failed or missing workflow and evidence gate before creating the release tag.' }
