[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BundleRoot,
    [Parameter(Mandatory = $true)]
    [string] $ArtifactUrl,
    [Parameter(Mandatory = $true)]
    [string] $TargetHardwareId,
    [Parameter(Mandatory = $true)]
    [string] $TargetHardwareContract,
    [Parameter(Mandatory = $true)]
    [string] $ExpectedCommitSha
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required.'
}

$profiles = @('high-cardinality', 'small-segments', 'maintenance-chaos', 'many-measurements')
$rawFilePaths = [System.Collections.Generic.List[string]]::new()
foreach ($profile in $profiles) {
    $rawFilePaths.Add("$profile/report.json")
    $rawFilePaths.Add("$profile/report.md")
}
$rawFilePaths.Add('target-hardware.json')
$rawFilePaths.Add('checkout-attestation.json')

function Normalize-Declaration([string] $Value, [string] $Name) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must be a non-empty declaration."
    }

    return ($Value.Trim() -replace '\s+', ' ')
}

function Invoke-GitText([string[]] $Arguments) {
    $result = @(& git @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($result -join [Environment]::NewLine)"
    }

    return ($result -join "`n").Trim()
}

function Assert-ExpectedHead([string] $Expected, [string] $Context) {
    $head = (Invoke-GitText @('rev-parse', '--verify', 'HEAD')).ToLowerInvariant()
    if (-not [string]::Equals($head, $Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Context changed HEAD from '$Expected' to '$head'."
    }

    return $head
}

function Get-GitStatusLines {
    $status = @(& git status --porcelain=v1 --untracked-files=all 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed: $($status -join [Environment]::NewLine)"
    }

    return @($status | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Assert-CleanWorktree([string] $Context) {
    $status = Get-GitStatusLines
    if ($status.Count -ne 0) {
        throw "$Context requires a clean checkout: $($status -join '; ')"
    }
}

function Get-EnvironmentDeclaration([string] $Name) {
    return Normalize-Declaration ([Environment]::GetEnvironmentVariable($Name)) $Name
}

function ConvertTo-RelativeBundlePath([string] $Path) {
    return [System.IO.Path]::GetRelativePath($bundleRootFull, $Path).Replace('\', '/')
}

function Write-JsonFile([string] $Path, [object] $Value) {
    $parent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Path))
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $Value | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function New-RawManifest(
    [string] $CommitSha,
    [string] $HardwareId,
    [string] $HardwareContract,
    [bool] $WorktreeClean,
    [string[]] $WorktreeStatus) {
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($relativePath in $rawFilePaths) {
        $candidate = Join-Path $bundleRootFull ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $entries.Add([ordered]@{
                path = $relativePath
                sha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
                bytes = (Get-Item -LiteralPath $candidate).Length
            })
        }
        else {
            # Preserve the expected raw surface so the verifier can diagnose an incomplete run.
            $entries.Add([ordered]@{
                path = $relativePath
                sha256 = $null
                bytes = $null
            })
        }
    }

    return [ordered]@{
        schemaVersion = 1
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        source = [ordered]@{
            commitSha = $CommitSha
            worktreeClean = $WorktreeClean
            worktreeStatus = @($WorktreeStatus)
        }
        targetHardware = [ordered]@{
            id = $HardwareId
            contract = $HardwareContract
        }
        files = $entries
    }
}

$repoRoot = Invoke-GitText @('rev-parse', '--show-toplevel')
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw 'Unable to resolve the repository root.'
}

Push-Location -LiteralPath $repoRoot
try {
    if ($ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'ExpectedCommitSha must be a 40-character commit SHA.'
    }
    $expectedCommit = $ExpectedCommitSha.ToLowerInvariant()
    $actualCommit = (Invoke-GitText @('rev-parse', '--verify', 'HEAD')).ToLowerInvariant()
    if ($actualCommit -notmatch '^[0-9a-f]{40}$' -or -not [string]::Equals($actualCommit, $expectedCommit, [StringComparison]::Ordinal)) {
        throw "Current HEAD '$actualCommit' does not match expected '$expectedCommit'."
    }
    Assert-CleanWorktree 'M19 capacity bundle preflight'

    $targetStatus = [Environment]::GetEnvironmentVariable('SONNETDB_M19_TARGET_HARDWARE_STATUS')
    if ($targetStatus -cne 'PASS') {
        throw 'SONNETDB_M19_TARGET_HARDWARE_STATUS must be exactly PASS.'
    }
    $declaredTargetId = Get-EnvironmentDeclaration 'SONNETDB_M19_TARGET_HARDWARE_ID'
    $declaredTargetContract = Get-EnvironmentDeclaration 'SONNETDB_M19_TARGET_HARDWARE_CONTRACT'
    $storageModel = Get-EnvironmentDeclaration 'SONNETDB_M19_STORAGE_MODEL'
    if ($declaredTargetId -eq 'UNDECLARED' -or $declaredTargetId -cne (Normalize-Declaration $TargetHardwareId 'TargetHardwareId')) {
        throw 'SONNETDB_M19_TARGET_HARDWARE_ID does not match TargetHardwareId.'
    }
    if ($declaredTargetContract -cne (Normalize-Declaration $TargetHardwareContract 'TargetHardwareContract')) {
        throw 'SONNETDB_M19_TARGET_HARDWARE_CONTRACT does not match TargetHardwareContract.'
    }

    if ([Environment]::GetEnvironmentVariable('GITHUB_ACTIONS') -cne 'true') {
        throw 'Authoritative M19 evidence must run in GitHub Actions.'
    }
    $githubSha = Get-EnvironmentDeclaration 'GITHUB_SHA'
    $githubRepository = Get-EnvironmentDeclaration 'GITHUB_REPOSITORY'
    $githubRef = Get-EnvironmentDeclaration 'GITHUB_REF'
    $githubRefProtected = Get-EnvironmentDeclaration 'GITHUB_REF_PROTECTED'
    $githubEventName = Get-EnvironmentDeclaration 'GITHUB_EVENT_NAME'
    $githubRunId = Get-EnvironmentDeclaration 'GITHUB_RUN_ID'
    $githubRunAttempt = Get-EnvironmentDeclaration 'GITHUB_RUN_ATTEMPT'
    $githubWorkflow = Get-EnvironmentDeclaration 'GITHUB_WORKFLOW'
    $githubWorkflowRef = Get-EnvironmentDeclaration 'GITHUB_WORKFLOW_REF'
    $githubServerUrl = Get-EnvironmentDeclaration 'GITHUB_SERVER_URL'
    $runnerName = Get-EnvironmentDeclaration 'RUNNER_NAME'
    $runnerOs = Get-EnvironmentDeclaration 'RUNNER_OS'
    $runnerArchitecture = Get-EnvironmentDeclaration 'RUNNER_ARCH'
    if ($githubSha -notmatch '^[0-9a-fA-F]{40}$' -or -not [string]::Equals($githubSha, $actualCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GITHUB_SHA does not match the checked out commit.'
    }
    if ($githubRepository -notmatch '^[^/\s]+/[^/\s]+$' -or
        $githubRef -cne 'refs/heads/main' -or
        $githubRefProtected -cne 'true' -or
        $githubEventName -cne 'workflow_dispatch' -or
        $githubServerUrl -cne 'https://github.com') {
        throw 'GitHub Actions context is not the protected main-branch workflow_dispatch GitHub.com workflow context.'
    }
    if ($githubWorkflow -cne 'M19 Capacity Evidence' -or
        $githubWorkflowRef -cne "$githubRepository/.github/workflows/m19-capacity-evidence.yml@refs/heads/main" -or
        $runnerOs -cne 'Linux' -or $runnerArchitecture -cne 'X64') {
        throw 'GitHub Actions context does not identify the protected Linux x64 M19 Capacity Evidence workflow.'
    }
    [long] $parsedRunId = 0
    [long] $parsedRunAttempt = 0
    if (-not [long]::TryParse($githubRunId, [ref]$parsedRunId) -or $parsedRunId -le 0 -or
        -not [long]::TryParse($githubRunAttempt, [ref]$parsedRunAttempt) -or $parsedRunAttempt -le 0) {
        throw 'GITHUB_RUN_ID and GITHUB_RUN_ATTEMPT must be positive integers.'
    }
    $expectedArtifactUrl = "https://github.com/$githubRepository/actions/runs/$githubRunId"
    if ($ArtifactUrl -cne $expectedArtifactUrl) {
        throw "ArtifactUrl must equal '$expectedArtifactUrl'."
    }

    $bundleRootFull = [System.IO.Path]::GetFullPath($BundleRoot)
    $repoRootFull = [System.IO.Path]::GetFullPath($repoRoot)
    if ([string]::Equals($bundleRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar), $repoRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'BundleRoot must not be the repository root.'
    }
    $relativeBundleRoot = [System.IO.Path]::GetRelativePath($repoRootFull, $bundleRootFull)
    $bundleInsideRepository = -not ($relativeBundleRoot -eq '..' -or $relativeBundleRoot.StartsWith('..' + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal))
    if ($bundleInsideRepository) {
        $ignoreProbe = [System.IO.Path]::GetRelativePath($repoRootFull, (Join-Path $bundleRootFull '.m19-capacity-ignore-probe'))
        & git check-ignore -q -- $ignoreProbe
        if ($LASTEXITCODE -ne 0) {
            throw 'BundleRoot inside the checkout must be ignored, otherwise later reports would attest a dirty worktree.'
        }
    }
    if (Test-Path -LiteralPath $bundleRootFull) {
        $existing = @(Get-ChildItem -LiteralPath $bundleRootFull -Force -ErrorAction Stop)
        if ($existing.Count -ne 0) {
            throw "BundleRoot '$bundleRootFull' already contains evidence and will not be overwritten."
        }
    }

    # All assertions above happen before any evidence directory is created.
    New-Item -ItemType Directory -Force -Path $bundleRootFull | Out-Null
    $checkoutPath = Join-Path $bundleRootFull 'checkout-attestation.json'
    $checkoutAttestation = [ordered]@{
        schemaVersion = 3
        attestedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        source = [ordered]@{
            commitSha = $actualCommit
            worktreeClean = $true
            worktreeStatus = @()
        }
        targetHardware = [ordered]@{
            status = 'PASS'
            id = $declaredTargetId
            contract = $declaredTargetContract
            declarationSource = 'environment'
            storageModel = $storageModel
        }
        ci = [ordered]@{
            provider = 'github-actions'
            repository = $githubRepository
            ref = $githubRef
            refProtected = $true
            eventName = $githubEventName
            sha = $githubSha.ToLowerInvariant()
            runId = $parsedRunId
            runAttempt = $parsedRunAttempt
            workflow = $githubWorkflow
            workflowRef = $githubWorkflowRef
            serverUrl = $githubServerUrl
            runUrl = $expectedArtifactUrl
            runnerName = $runnerName
            runnerOs = $runnerOs
            runnerArchitecture = $runnerArchitecture
        }
    }
    Write-JsonFile $checkoutPath $checkoutAttestation

    $captureScript = Join-Path $PSScriptRoot 'capture-m19-target-hardware.ps1'
    if (-not (Test-Path -LiteralPath $captureScript -PathType Leaf)) {
        throw "Target hardware capture script is missing: $captureScript"
    }
    $captureWorkPath = Join-Path $bundleRootFull 'work'
    New-Item -ItemType Directory -Force -Path $captureWorkPath | Out-Null
    & $captureScript -OutputPath (Join-Path $bundleRootFull 'target-hardware.json') -WorkPath $captureWorkPath -ExpectedCommitSha $actualCommit -ExpectedTargetHardwareId $declaredTargetId -ExpectedTargetHardwareContract $declaredTargetContract
    if ($LASTEXITCODE -ne 0) {
        throw "Target hardware capture failed with exit code $LASTEXITCODE."
    }

    $projectPath = Join-Path $repoRootFull 'tests/SonnetDB.EcosystemSoak/SonnetDB.EcosystemSoak.csproj'
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Capacity runner project is missing: $projectPath"
    }
    $profileExitCodes = [ordered]@{}
    foreach ($profile in $profiles) {
        [void](Assert-ExpectedHead $actualCommit "M19 capacity bundle before profile '$profile'")
        Assert-CleanWorktree "M19 capacity bundle before profile '$profile'"
        $profileOutput = Join-Path $bundleRootFull $profile
        # Keep the database workload on the same declared volume as its evidence bundle.
        $profileWork = Join-Path (Join-Path $bundleRootFull 'work') $profile
        Write-Host "Running fixed M19 capacity profile '$profile' serially."
        & dotnet run -c Release --no-build --no-restore --project $projectPath -- --profile $profile --work $profileWork --output $profileOutput
        $profileExitCodes[$profile] = $LASTEXITCODE
        [void](Assert-ExpectedHead $actualCommit "M19 capacity bundle after profile '$profile'")
        Assert-CleanWorktree "M19 capacity bundle after profile '$profile'"
    }

    # Reports attest a clean checkout at process startup. Record the terminal
    # checkout state too, so a mutation during the serial bundle cannot be
    # promoted to release evidence by a later hard-coded manifest flag.
    [void](Assert-ExpectedHead $actualCommit 'M19 capacity bundle manifest capture')
    $completedWorktreeStatus = @()
    try {
        $completedWorktreeStatus = @(Get-GitStatusLines)
    }
    catch {
        $completedWorktreeStatus = @("git status unavailable: $($_.Exception.Message)")
    }
    $manifestPath = Join-Path $bundleRootFull 'raw-artifact-manifest.json'
    Write-JsonFile $manifestPath (New-RawManifest `
        $actualCommit `
        $declaredTargetId `
        $declaredTargetContract `
        ($completedWorktreeStatus.Count -eq 0) `
        $completedWorktreeStatus)

    $bundleVerifier = Join-Path $PSScriptRoot 'verify-m19-capacity-bundle.ps1'
    if (-not (Test-Path -LiteralPath $bundleVerifier -PathType Leaf)) {
        throw "Bundle verifier is missing: $bundleVerifier"
    }
    $verificationPath = Join-Path $bundleRootFull 'm19-capacity-bundle-verification.json'
    $verificationInvocationFailed = $false
    try {
        & $bundleVerifier -BundleRoot $bundleRootFull -ArtifactUrl $ArtifactUrl -OutputPath $verificationPath -ExpectedCommitSha $actualCommit -ExpectedTargetHardwareId $declaredTargetId -ExpectedTargetHardwareContract $declaredTargetContract -AllowNotReady
        if ($LASTEXITCODE -ne 0) {
            $verificationInvocationFailed = $true
        }
    }
    catch {
        $verificationInvocationFailed = $true
        Write-Error "Bundle verifier invocation failed: $($_.Exception.Message)"
    }

    $verification = $null
    if (Test-Path -LiteralPath $verificationPath -PathType Leaf) {
        try {
            $verification = Get-Content -LiteralPath $verificationPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
        }
        catch {
            $verificationInvocationFailed = $true
            Write-Error "Bundle verification output is unreadable: $($_.Exception.Message)"
        }
    }
    else {
        $verificationInvocationFailed = $true
        Write-Error 'Bundle verifier did not produce m19-capacity-bundle-verification.json.'
    }

    $failedProfiles = @($profileExitCodes.GetEnumerator() | Where-Object { $_.Value -ne 0 } | ForEach-Object { $_.Key })
    $releaseEvidenceIsBooleanTrue = $null -ne $verification
        -and $null -ne $verification.PSObject.Properties['releaseEvidence']
        -and $verification.releaseEvidence -is [bool]
        -and $verification.releaseEvidence -eq $true
    if ($failedProfiles.Count -ne 0 -or $verificationInvocationFailed -or $null -eq $verification -or [string]$verification.status -ne 'PASS' -or -not $releaseEvidenceIsBooleanTrue) {
        $reason = @()
        if ($failedProfiles.Count -ne 0) { $reason += "failed profiles: $($failedProfiles -join ', ')" }
        if ($verificationInvocationFailed) { $reason += 'bundle verifier invocation/output failed' }
        if ($null -ne $verification -and [string]$verification.status -ne 'PASS') { $reason += "bundle status: $($verification.status)" }
        if (-not $releaseEvidenceIsBooleanTrue) { $reason += 'bundle releaseEvidence is not Boolean true' }
        throw "M19 capacity bundle is NOT_READY: $($reason -join '; ')."
    }

    [pscustomobject]@{
        status = 'PASS'
        bundleRoot = $bundleRootFull
        commitSha = $actualCommit
        profiles = $profileExitCodes
        verificationPath = ConvertTo-RelativeBundlePath $verificationPath
    } | ConvertTo-Json -Depth 16
}
finally {
    Pop-Location
}
