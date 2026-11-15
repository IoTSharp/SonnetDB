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
    [switch]$ReuseSuccessfulPreflight,
    [Alias('OfflineEvidencePath', 'PublicationEvidencePath')]
    [string]$RecoveryEvidencePath,
    [Alias('NoExternalWrites', 'DryRun')]
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:GitHubCliPath = $null
. (Join-Path $PSScriptRoot 'tag-publication-reconciliation.ps1')

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $Tag.Substring(1)
}
if ($Tag -cne "v$Version") {
    throw "Tag '$Tag' and version '$Version' do not identify the same release."
}

function Read-RecoveryEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt 2097152) {
        throw 'Recovery evidence must be a regular UTF-8 JSON file no larger than 2 MiB.'
    }

    try {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        return ($text.TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable -Depth 32)
    }
    catch {
        throw "Recovery evidence is not valid strict UTF-8 JSON: $($_.Exception.Message)"
    }
}

function Resolve-RecoveryEvidencePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$EvidenceFile,
        [Parameter(Mandatory)][string]$Path,
        [switch]$Directory
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Recovery evidence path is empty.' }
    $base = (Get-Item -LiteralPath $EvidenceFile -Force).Directory.FullName
    $candidate = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $base $Path }
    $full = [IO.Path]::GetFullPath($candidate)
    $item = Get-Item -LiteralPath $full -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($Directory -and -not $item.PSIsContainer) -or (-not $Directory -and $item.PSIsContainer)) {
        throw "Recovery evidence path has an invalid type or reparse point: $Path"
    }
    return $item.FullName
}

function Assert-RecoveryOfflineEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedRepository,
        [Parameter(Mandatory)][string]$ExpectedTag,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$ExpectedCommitSha
    )

    $evidenceFile = (Get-Item -LiteralPath $Path -Force).FullName
    $evidence = Read-RecoveryEvidence -Path $evidenceFile
    if ((Get-PublicationProperty $evidence 'schemaVersion') -ne 1) { throw 'Recovery evidence schemaVersion must be 1.' }
    if ((Get-PublicationProperty $evidence 'repository') -cne $ExpectedRepository -or
        (Get-PublicationProperty $evidence 'tag') -cne $ExpectedTag -or
        (Get-PublicationProperty $evidence 'version') -cne $ExpectedVersion -or
        (Get-PublicationProperty $evidence 'commitSha') -cne $ExpectedCommitSha) {
        throw 'Recovery evidence identity does not match the resolved repository, tag, version and commit.'
    }

    $manifestSpec = Get-PublicationProperty $evidence 'manifest'
    $manifestPath = Resolve-RecoveryEvidencePath -EvidenceFile $evidenceFile -Path ([string](Get-PublicationProperty $manifestSpec 'path'))
    $manifestSha256 = [string](Get-PublicationProperty $manifestSpec 'sha256')
    if ($manifestSha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Recovery evidence manifest SHA-256 is invalid.' }
    $manifest = Read-TaggedPublicationManifest -Path $manifestPath -ExpectedSha256 $manifestSha256
    if ($manifest.repository -cne $ExpectedRepository -or $manifest.tag -cne $ExpectedTag -or
        $manifest.commitSha -cne $ExpectedCommitSha) { throw 'Approved manifest identity does not match the recovery target.' }

    $artifactRoot = Resolve-RecoveryEvidencePath -EvidenceFile $evidenceFile -Path ([string](Get-PublicationProperty $evidence 'artifactRoot')) -Directory
    $downloads = @((Get-PublicationProperty $evidence 'downloads'))
    foreach ($download in $downloads) {
        $download.archivePath = Resolve-RecoveryEvidencePath -EvidenceFile $evidenceFile -Path ([string](Get-PublicationProperty $download 'archivePath'))
    }
    $artifactPlan = Assert-TaggedPublicationArtifactSet -Manifest $manifest `
        -Sources @((Get-PublicationProperty $evidence 'sources')) `
        -Artifacts @((Get-PublicationProperty $evidence 'artifacts')) `
        -Downloads $downloads `
        -ArtifactRoot $artifactRoot
    if ($artifactPlan.externalWriteAuthorized) { throw 'Offline artifact reconciliation unexpectedly authorized an external write.' }

    $currentMainCommit = [string](Get-PublicationProperty $evidence 'currentMainCommit')
    if ($currentMainCommit -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Current main commit identity is invalid.' }
    $policyState = Assert-TaggedPublicationPolicyBoundary `
        -Current (Get-PublicationProperty $evidence 'currentReadiness') `
        -Frozen (Get-PublicationProperty $evidence 'frozenReadiness') `
        -Repository $ExpectedRepository `
        -CommitSha $ExpectedCommitSha `
        -Version $ExpectedVersion `
        -CurrentMainCommit $currentMainCommit

    $nugetPlans = [Collections.Generic.List[object]]::new()
    $nugetEvidence = @((Get-PublicationProperty $evidence 'nuget'))
    if ($nugetEvidence.Count -lt 1 -or $nugetEvidence.Count -gt 32) { throw 'Recovery evidence must contain between one and 32 NuGet reconciliation records.' }
    foreach ($record in $nugetEvidence) {
        $state = [string](Get-PublicationProperty $record 'readState')
        $candidate = Get-PublicationProperty $record 'candidate'
        $published = Get-PublicationProperty $record 'published' -Optional
        $previousWriteUnknown = [bool](Get-PublicationProperty $record 'previousWriteUnknown' -Optional)
        $decision = Compare-TaggedNuGetPublication -Candidate $candidate -ReadState $state -Published $published `
            -PreviousWriteUnknown:$previousWriteUnknown
        if ($decision -notin @('ALREADY_PRESENT_SAME_PAYLOAD', 'MISSING_CONFIRMED')) {
            throw "NuGet reconciliation is not safe to resume: $decision"
        }
        $nugetPlans.Add([pscustomobject]@{ state = $decision; id = $candidate.id; version = $candidate.version })
    }

    $releaseEvidence = Get-PublicationProperty $evidence 'release'
    $releaseState = [string](Get-PublicationProperty $releaseEvidence 'readState')
    $releasePlan = Compare-TaggedReleaseAssets `
        -ReadState $releaseState `
        -Tag $ExpectedTag `
        -ExpectedReleaseId ([long](Get-PublicationProperty $releaseEvidence 'releaseId')) `
        -ExpectedAssets @((Get-PublicationProperty $releaseEvidence 'expectedAssets')) `
        -Release (Get-PublicationProperty $releaseEvidence 'release') `
        -ExistingAssets @((Get-PublicationProperty $releaseEvidence 'existingAssets')) `
        -PreviousWriteUnknown:([bool](Get-PublicationProperty $releaseEvidence 'previousWriteUnknown' -Optional))
    if ($releasePlan.state -notin @('MISSING_CONFIRMED', 'RECONCILED_REQUIRES_WRITE_FENCE')) {
        throw "Release reconciliation is not safe to resume: $($releasePlan.state)"
    }

    return [pscustomobject]@{
        state = 'OFFLINE_ADMISSION_VERIFIED'
        repository = $ExpectedRepository
        tag = $ExpectedTag
        version = $ExpectedVersion
        commitSha = $ExpectedCommitSha
        currentMainCommit = $currentMainCommit
        policy = $policyState
        artifact = $artifactPlan.state
        nuget = $nugetPlans.ToArray()
        release = $releasePlan
        externalWriteAuthorized = $false
        retryAllowed = $false
    }
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

function Invoke-RecoveryWriteFence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    if ([string]::IsNullOrWhiteSpace($RecoveryEvidencePath)) {
        throw "$Description requires an approved offline evidence bundle. No external action was attempted."
    }
    $result = Invoke-TaggedPublicationWriteFence `
        -Repository $Repository `
        -Tag $Tag `
        -CommitSha $commitSha `
        -VerifyContent {
            $null = Assert-RecoveryOfflineEvidence `
                -Path $RecoveryEvidencePath `
                -ExpectedRepository $Repository `
                -ExpectedTag $Tag `
                -ExpectedVersion $Version `
                -ExpectedCommitSha $commitSha
            return $true
        } `
        -ResolveRemoteTag {
            param($targetRepository, $targetTag)
            try {
                $observedCommit = Get-RemoteTagCommit -TargetRepository $targetRepository -TargetTag $targetTag
                return @{ readState = 'FOUND'; repository = $targetRepository; tag = $targetTag; commitSha = $observedCommit }
            }
            catch {
                return @{ readState = 'TRANSPORT_ERROR'; repository = $targetRepository; tag = $targetTag; commitSha = '' }
            }
        } `
        -Action $Action -TimeoutSeconds 90
    if ($result.state -cne 'WRITE_RETURNED_REQUIRES_READBACK' -or $result.retryAllowed) {
        throw "$Description did not reach a reconciliable write state: $($result.state)"
    }
    return $result
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
    $null = Invoke-RecoveryWriteFence -Description "Dispatch '$Workflow' preflight" -Action {
        & $script:GitHubCliPath @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Unable to dispatch '$Workflow' preflight." }
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

    $null = Invoke-RecoveryWriteFence -Description "Rerun '$Workflow'" -Action {
        & $script:GitHubCliPath run rerun $run.databaseId --repo $Repository
        if ($LASTEXITCODE -ne 0) { throw "Unable to rerun '$Workflow': $($run.url)" }
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
    $releasePackageIds = @('SonnetDB.Core', 'SonnetDB', 'SonnetDB.EntityFrameworkCore', 'SonnetDB.Caching.EasyCaching',
        'SonnetDB.Caching.Distributed', 'SonnetDB.Cli', 'Testcontainers.SonnetDB')
    # CAP 从 4.1.0 开始交付；旧 tag 的恢复仍使用原七包合同。
    if ([version]($Version.Split('-')[0]) -ge [version]'4.1.0') { $releasePackageIds += 'SonnetDB.CAP' }
    if ([version]($Version.Split('-')[0]) -ge [version]'5.0.0') {
        $releasePackageIds = @($releasePackageIds | Where-Object { $_ -cne 'Testcontainers.SonnetDB' })
        $releasePackageIds += @('SonnetDB.Protocol', 'SonnetDB.Client', 'SonnetDB.Testcontainers')
    }
    foreach ($package in $releasePackageIds) {
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

    foreach ($package in $releasePackageIds) {
        $nugetPackage = $package.ToLowerInvariant()
        $url = "https://api.nuget.org/v3-flatcontainer/$nugetPackage/$Version/$nugetPackage.$Version.nupkg"
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
    ExternalWriteAuthorized = $false
    OfflineOnly = [bool]$Offline
}

Write-Host "Resolved $Tag to $commitSha."
Write-Host 'Recovery always reuses this exact tag and commit; it never creates, deletes, or moves tags.'

if (($Resume -or $Offline) -and [string]::IsNullOrWhiteSpace($RecoveryEvidencePath)) {
    throw 'Resume and offline recovery require an approved -RecoveryEvidencePath. No external action was attempted.'
}

$offlineAdmission = $null
if (-not [string]::IsNullOrWhiteSpace($RecoveryEvidencePath)) {
    $offlineAdmission = Assert-RecoveryOfflineEvidence `
        -Path $RecoveryEvidencePath `
        -ExpectedRepository $Repository `
        -ExpectedTag $Tag `
        -ExpectedVersion $Version `
        -ExpectedCommitSha $commitSha
    $plan | Add-Member -NotePropertyName OfflineAdmission -NotePropertyValue $offlineAdmission
}

if (-not $Resume) {
    Write-Host 'Plan only. Re-run with -Resume, an approved offline evidence bundle and authenticated gh to dispatch preflights and recover the tagged workflows.'
    $plan
    return
}

if ($Offline) {
    Write-Host 'Offline recovery admission verified. No GitHub CLI, workflow dispatch, rerun, release or NuGet write was attempted.'
    $offlineAdmission
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
