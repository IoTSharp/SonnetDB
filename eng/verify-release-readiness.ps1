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
$observations = [Collections.Generic.List[object]]::new()
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
    $nightlyObservation = [ordered]@{
        name = 'scheduled-parity-seven-days'; blocking = $false; status = 'UNAVAILABLE'
        validRunCount = $null; requiredRunCount = 7; reportPath = $nightlyPath; issues = @()
    }
    # 七天 scheduled 只提供长期观察；不足、失败或读取不到均不得阻断当前候选发布。
    # 恢复原生退出码，避免可选观察中的 gh 失败被 Actions 的 pwsh 包装器再次作为失败退出。
    $nightlyLastExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    try {
        if (Test-Path -LiteralPath $nightlyPath) { Remove-Item -LiteralPath $nightlyPath }
        & $parityVerifier -Repository $Repository -OutputPath $nightlyPath -AllowNotReady
        $nightly = Get-Content -Raw -LiteralPath $nightlyPath | ConvertFrom-Json
        if ($nightly.source -ne 'github' -or $nightly.repository -ne $Repository -or $nightly.status -notin @('READY', 'NOT_READY')) {
            throw 'Nightly observation has an unexpected source, repository or status.'
        }
        $nightlyObservation.status = $nightly.status
        $nightlyObservation.validRunCount = $nightly.validRunCount
        $nightlyObservation.requiredRunCount = $nightly.requiredRunCount
        $nightlyObservation.issues = @($nightly.issues)
        if ($nightly.status -ne 'READY') {
            Write-Warning "Non-blocking seven-day Parity observation: $($nightly.validRunCount)/$($nightly.requiredRunCount) scheduled runs validated."
        }
    }
    catch {
        $nightlyObservation.issues = @(@{ code = 'observation_unavailable'; message = $_.Exception.Message })
        Write-Warning "Non-blocking seven-day Parity observation unavailable: $($_.Exception.Message)"
    }
    finally {
        if ($null -eq $nightlyLastExitCode) { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
        else { $global:LASTEXITCODE = $nightlyLastExitCode }
    }
    $observations.Add([pscustomobject]$nightlyObservation)
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
    workflows = $checks.ToArray(); issues = $errors.ToArray(); observations = $observations.ToArray()
    boundary = 'Release readiness requires the candidate workflows and raw Parity evidence. Seven-day scheduled Parity is a non-blocking observation. Quick profiles do not establish fixed-hardware capacity, clean offline installation, model quality or production acceptance.'
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $outputFullPath -Encoding utf8
Write-Host "Release readiness: $($report.status). Report: $outputFullPath"
if (-not $ready) { throw 'Release is NOT_READY. Resolve every failed or missing workflow and evidence gate before creating the release tag.' }
