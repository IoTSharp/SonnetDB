[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'IoTSharp/SonnetDB',
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$Tag,
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$Version,
    [ValidateRange(1, 180)]
    [int]$DiscoveryTimeoutMinutes = 10,
    [switch]$Resume,
    [switch]$ReuseSuccessfulPreflight
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:GitHubCliPath = $null

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $Tag.Substring(1)
}
if ($Tag -cne "v$Version") {
    throw "Tag '$Tag' and version '$Version' do not identify the same release."
}

function Get-RemoteTagCommit {
    param(
        [Parameter(Mandatory)]
        [string]$TargetRepository,
        [Parameter(Mandatory)]
        [string]$TargetTag
    )

    $remote = "https://github.com/$TargetRepository.git"
    $refs = @(& git ls-remote $remote "refs/tags/$TargetTag" "refs/tags/$TargetTag^{}")
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to resolve tag '$TargetTag' from '$TargetRepository'."
    }

    $parsedRefs = @($refs | ForEach-Object {
        $parts = $_ -split "`t", 2
        if ($parts.Count -ne 2) { return }
        [pscustomobject]@{ Sha = $parts[0]; Ref = $parts[1] }
    })
    $peeled = @($parsedRefs | Where-Object Ref -ceq "refs/tags/$TargetTag^{}")
    $direct = @($parsedRefs | Where-Object Ref -ceq "refs/tags/$TargetTag")
    $target = if ($peeled.Count -eq 1) { $peeled[0] } elseif ($direct.Count -eq 1) { $direct[0] } else { $null }
    if ($null -eq $target -or $target.Sha -notmatch '^[0-9a-f]{40}$') {
        throw "Tag '$TargetTag' is missing, ambiguous, or does not resolve to a commit."
    }

    return $target.Sha
}

function Resolve-GitHubCliPath {
    $command = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }

    $candidatePaths = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidatePaths.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\gh.exe'))
        $candidatePaths.Add((Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidatePaths.Add((Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'))
    }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidatePaths.Add((Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe'))
    }

    foreach ($candidate in $candidatePaths) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Assert-GitHubCli {
    $script:GitHubCliPath = Resolve-GitHubCliPath
    if ([string]::IsNullOrWhiteSpace($script:GitHubCliPath)) {
        throw 'GitHub CLI (gh) is required for -Resume. Install it and authenticate with repository Actions write access.'
    }

    Write-Host "Using GitHub CLI: $script:GitHubCliPath"
    & $script:GitHubCliPath auth status --hostname github.com
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub CLI is not authenticated for github.com.'
    }
}

function Get-WorkflowRuns {
    param(
        [Parameter(Mandatory)]
        [string]$Workflow,
        [Parameter(Mandatory)]
        [string]$Event,
        [Parameter(Mandatory)]
        [string]$CommitSha
    )

    $jsonLines = @(& $script:GitHubCliPath run list --repo $Repository --workflow $Workflow --event $Event --commit $CommitSha --limit 100 `
        --json databaseId,headSha,status,conclusion,createdAt,url
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to list $Event runs for '$Workflow'."
    }
    $json = [string]::Join("`n", [string[]]$jsonLines)
    if ([string]::IsNullOrWhiteSpace($json)) { return }

    $runs = @($json | ConvertFrom-Json)
    foreach ($run in $runs) {
        if ($run -is [Array]) {
            throw "GitHub CLI returned a nested run collection for '$Workflow'."
        }
        if ($run.headSha -ceq $CommitSha) {
            Write-Output $run
        }
    }
}

function Get-WorkflowRunCreatedAt {
    param(
        [Parameter(Mandatory)]
        [object]$Run
    )

    if ($Run -is [Array]) {
        throw 'A nested run collection cannot be ordered.'
    }
    $values = @($Run.createdAt)
    if ($values.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$values[0])) {
        throw 'A workflow run must have exactly one createdAt timestamp.'
    }
    try {
        return [DateTimeOffset]::Parse(
            [string]$values[0],
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
    }
    catch {
        throw "Workflow run has an invalid createdAt timestamp: $($values[0])"
    }
}

function Start-Preflight {
    param(
        [Parameter(Mandatory)]
        [string]$Workflow,
        [string[]]$Fields = @()
    )

    $knownIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($run in Get-WorkflowRuns -Workflow $Workflow -Event 'workflow_dispatch' -CommitSha $commitSha) {
        $null = $knownIds.Add([string]$run.databaseId)
    }

    $arguments = @('workflow', 'run', $Workflow, '--repo', $Repository, '--ref', $Tag)
    foreach ($field in $Fields) {
        $arguments += @('--field', $field)
    }
    & $script:GitHubCliPath @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to dispatch '$Workflow' preflight."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($DiscoveryTimeoutMinutes)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $newRuns = @(Get-WorkflowRuns -Workflow $Workflow -Event 'workflow_dispatch' -CommitSha $commitSha |
            Where-Object { -not $knownIds.Contains([string]$_.databaseId) } |
            Sort-Object @{ Expression = { Get-WorkflowRunCreatedAt -Run $_ }; Descending = $true })
        if ($newRuns.Count -eq 1) {
            return $newRuns[0]
        }
        if ($newRuns.Count -gt 1) {
            throw "More than one new '$Workflow' preflight was discovered. Refusing to select an ambiguous run."
        }
        Start-Sleep -Seconds 5
    }

    throw "Timed out waiting for GitHub to create the '$Workflow' preflight run."
}

function Get-SuccessfulPreflight {
    param(
        [Parameter(Mandatory)]
        [string]$Workflow
    )

    $runs = @(Get-WorkflowRuns -Workflow $Workflow -Event 'workflow_dispatch' -CommitSha $commitSha |
        Sort-Object @{ Expression = { Get-WorkflowRunCreatedAt -Run $_ }; Descending = $true })
    if ($runs.Count -eq 0) {
        throw "No successful '$Workflow' preflight exists for '$commitSha'."
    }

    $run = $runs[0]
    if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') {
        throw "The latest '$Workflow' preflight is not successful: $($run.url)"
    }
    Write-Host "Reusing successful '$Workflow' preflight: $($run.url)"
    return $run
}

function Wait-ForSuccessfulRun {
    param(
        [Parameter(Mandatory)]
        [object]$Run,
        [Parameter(Mandatory)]
        [string]$Description
    )

    & $script:GitHubCliPath run watch $Run.databaseId --repo $Repository --exit-status
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed: $($Run.url)"
    }
}

function Get-TaggedRunToResume {
    param([Parameter(Mandatory)][string]$Workflow)

    $runs = @(Get-WorkflowRuns -Workflow $Workflow -Event 'push' -CommitSha $commitSha |
        Sort-Object @{ Expression = { Get-WorkflowRunCreatedAt -Run $_ }; Descending = $true })
    if ($runs.Count -eq 0) {
        throw "No tag-triggered '$Workflow' run exists for '$Tag' at '$commitSha'."
    }

    $run = $runs[0]
    if ($run.status -ne 'completed') {
        throw "The latest tag-triggered '$Workflow' run is still $($run.status): $($run.url)"
    }
    return $run
}

function Resume-TaggedRun {
    param([Parameter(Mandatory)][string]$Workflow)

    $run = Get-TaggedRunToResume -Workflow $Workflow
    if ($run.conclusion -eq 'success') {
        Write-Host "Tag-triggered '$Workflow' already succeeded: $($run.url)"
        return
    }

    & $script:GitHubCliPath run rerun $run.databaseId --repo $Repository
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to rerun '$Workflow': $($run.url)"
    }
    & $script:GitHubCliPath run watch $run.databaseId --repo $Repository --exit-status
    if ($LASTEXITCODE -ne 0) {
        throw "Tag-triggered '$Workflow' did not succeed after recovery: $($run.url)"
    }
}

function Assert-ReleaseReadiness {
    $reportPath = Join-Path $PSScriptRoot "../artifacts/tagged-release-recovery/$Tag-readiness.json"
    $previousToken = $env:GITHUB_TOKEN
    try {
        $token = (& $script:GitHubCliPath auth token).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
            throw 'Unable to obtain an authenticated GitHub token for release readiness verification.'
        }
        $env:GITHUB_TOKEN = $token
        try {
            & (Join-Path $PSScriptRoot 'verify-release-readiness.ps1') `
                -Repository $Repository `
                -CommitSha $commitSha `
                -Version $Version `
                -Token $token `
                -OutputPath $reportPath
        }
        catch {
            throw "Release readiness verification failed. Inspect '$reportPath'. $($_.Exception.Message)"
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Release readiness verification failed. Inspect '$reportPath'."
        }
    }
    finally {
        $env:GITHUB_TOKEN = $previousToken
    }
}

function Assert-PublishedRelease {
    $releaseJson = & $script:GitHubCliPath release view $Tag --repo $Repository --json isDraft,assets,url
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Release '$Tag' was not created."
    }
    $release = $releaseJson | ConvertFrom-Json
    if ($release.isDraft) {
        throw "GitHub Release '$Tag' is still a draft: $($release.url)"
    }

    $expectedAssets = [Collections.Generic.List[string]]::new()
    foreach ($package in @('SonnetDB.Core', 'SonnetDB', 'SonnetDB.EntityFrameworkCore', 'SonnetDB.Caching.EasyCaching',
            'SonnetDB.Caching.Distributed', 'SonnetDB.Cli', 'Testcontainers.SonnetDB')) {
        $expectedAssets.Add("$package.$Version.nupkg")
    }
    foreach ($name in @("sndb-sdk-$Version-linux-x64.tar.gz", "sonnetdb-full-$Version-linux-x64.tar.gz",
            "sonnetdb-$Version-linux-x64.deb", "sonnetdb-$Version-linux-x64.rpm",
            "sndb-sdk-$Version-win-x64.zip", "sonnetdb-full-$Version-win-x64.zip",
            "sonnetdb-studio-$Version-win-x64.zip", "sonnetdb-$Version-win-x64.msi",
            "sonnetdb-studio-$Version-win-x64.msi")) {
        $expectedAssets.Add($name)
        $expectedAssets.Add("$name.sha256")
    }
    foreach ($rid in @('linux-x64', 'win-x64')) {
        foreach ($language in @('c', 'java', 'go', 'rust', 'python', 'purebasic')) {
            $expectedAssets.Add("sonnetdb-connector-$language-$Version-$rid.zip")
        }
    }
    foreach ($language in @('c', 'vb6')) {
        $expectedAssets.Add("sonnetdb-connector-$language-$Version-win-x86.zip")
    }

    $assetNames = @($release.assets | ForEach-Object name)
    $missing = @($expectedAssets | Where-Object { $_ -cnotin $assetNames })
    if ($missing.Count -ne 0) {
        throw "GitHub Release '$Tag' is missing required assets: $($missing -join ', ')"
    }

    foreach ($package in @('sonnetdb.core', 'sonnetdb', 'sonnetdb.entityframeworkcore', 'sonnetdb.caching.easycaching',
            'sonnetdb.caching.distributed', 'sonnetdb.cli', 'testcontainers.sonnetdb')) {
        $url = "https://api.nuget.org/v3-flatcontainer/$package/$Version/$package.$Version.nupkg"
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Method Head -Uri $url -TimeoutSec 60
        }
        catch {
            throw "NuGet.org does not serve '$package' ${Version}: $($_.Exception.Message)"
        }
        if ($response.StatusCode -ne 200) {
            throw "NuGet.org returned HTTP $($response.StatusCode) for '$package' $Version."
        }
    }

    Write-Host "Release recovery completed: $($release.url)"
}

$commitSha = Get-RemoteTagCommit -TargetRepository $Repository -TargetTag $Tag
$plan = [pscustomobject]@{
    Repository = $Repository
    Tag = $Tag
    Version = $Version
    CommitSha = $commitSha
    PreflightWorkflows = @('publish.yml', 'connectors-release.yml', 'docker-publish.yml')
    TaggedWorkflowOrder = @('publish.yml', 'docker-publish.yml', 'connectors-release.yml')
    MutatesTag = $false
}

Write-Host "Resolved $Tag to $commitSha."
Write-Host 'Recovery always reuses this exact tag and commit; it never creates, deletes, or moves tags.'
if (-not $Resume) {
    Write-Host 'Plan only. Re-run with -Resume after authenticating gh to dispatch preflights and recover the tagged workflows.'
    $plan
    return
}

Assert-GitHubCli
$preflights = if ($ReuseSuccessfulPreflight) {
    @(
        Get-SuccessfulPreflight -Workflow 'publish.yml'
        Get-SuccessfulPreflight -Workflow 'connectors-release.yml'
        Get-SuccessfulPreflight -Workflow 'docker-publish.yml'
    )
}
else {
    @(
        Start-Preflight -Workflow 'publish.yml' -Fields @("version=$Version")
        Start-Preflight -Workflow 'connectors-release.yml' -Fields @("version=$Version")
        Start-Preflight -Workflow 'docker-publish.yml'
    )
}
foreach ($preflight in $preflights) {
    Wait-ForSuccessfulRun -Run $preflight -Description 'Release preflight'
}
Assert-ReleaseReadiness

# Publish owns GitHub Release creation. Connector assets wait for it, so it resumes last.
foreach ($workflow in @('publish.yml', 'docker-publish.yml', 'connectors-release.yml')) {
    Resume-TaggedRun -Workflow $workflow
}
Assert-PublishedRelease
