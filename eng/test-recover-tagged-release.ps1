$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path $PSScriptRoot 'recover-tagged-release.ps1'
$scriptSource = Get-Content -LiteralPath $scriptPath -Raw
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -ne 0) {
    $errorMessages = @($errors | ForEach-Object { $_.Message }) -join '; '
    throw "Release recovery script has parser errors: $errorMessages"
}
if ($scriptSource -notmatch 'verify-release-readiness\.ps1') {
    throw 'Recovery must verify all release evidence before rerunning the tagged workflows.'
}
if ($scriptSource -notmatch 'Microsoft\\WinGet\\Links\\gh\.exe') {
    throw 'Recovery must resolve a current-user WinGet gh installation when PATH is stale.'
}
if ($scriptSource -notmatch '\$script:GitHubCliPath') {
    throw 'Recovery must use a resolved gh executable path after preflight validation.'
}
if ($scriptSource -notmatch '\$ReuseSuccessfulPreflight' -or $scriptSource -notmatch 'function Get-SuccessfulPreflight') {
    throw 'Recovery must support reusing successful preflights without dispatching duplicates.'
}
if ($scriptSource -notmatch '\[string\]::Join\("`n", \[string\[\]\]\$jsonLines\)') {
    throw 'Recovery must parse GitHub CLI output as one JSON document.'
}
if ($scriptSource -notmatch 'function Get-WorkflowRunCreatedAt') {
    throw 'Recovery must validate each workflow run timestamp before ordering.'
}
if ($scriptSource -match '\brun watch\b' -or $scriptSource -notmatch 'function Wait-ForSuccessfulRun' -or
    $scriptSource -notmatch '\$maxPolls' -or $scriptSource -notmatch '\$DiscoveryTimeoutMinutes') {
    throw 'Recovery workflow waits must use bounded status polling instead of an unbounded gh run watch.'
}
if ($scriptSource -notmatch 'Assert-ReleaseReadiness\s*\r?\n\s*# Publish owns GitHub Release creation') {
    throw 'Release readiness must run before the tagged workflow recovery sequence.'
}
if ($scriptSource -match '(?im)^\s*&?\s*git\s+(?:push|tag|update-ref)\b') {
    throw 'Recovery must not mutate the release tag.'
}
if ($scriptSource -notmatch 'tag-publication-reconciliation\.ps1' -or
    $scriptSource -notmatch 'function Assert-RecoveryOfflineEvidence') {
    throw 'Recovery must gate -Resume through the offline policy/reconciliation library.'
}
if ($scriptSource -notmatch '\$RecoveryEvidencePath' -or $scriptSource -notmatch '\$Offline') {
    throw 'Recovery must expose an explicit evidence path and no-external-write mode.'
}
$parityVerifierPath = Join-Path $PSScriptRoot '../tests/SonnetDB.Parity/scripts/verify-parity-nightly-evidence.ps1'
$parityVerifierSource = Get-Content -LiteralPath $parityVerifierPath -Raw
if ($parityVerifierSource -notmatch 'Microsoft\\WinGet\\Links\\gh\.exe') {
    throw 'Parity evidence verification must resolve a current-user WinGet gh installation when PATH is stale.'
}
if ($parityVerifierSource -notmatch '\$script:GitHubCliPath') {
    throw 'Parity evidence verification must use the resolved gh executable path for API and artifact calls.'
}

. (Join-Path $PSScriptRoot 'tag-publication-reconciliation.ps1')

function global:git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    if ($Arguments -join ' ' -notmatch 'ls-remote') { throw 'Unexpected git command.' }
    $global:LASTEXITCODE = 0
    $joined = $Arguments -join ' '
    if ($joined -match [regex]::Escape($global:MockTag)) {
        "$global:MockCommit`trefs/tags/$global:MockTag"
    }
    else {
        "d49babae0e049ca4554d26772b92493220d42822`trefs/tags/v4.0.0"
    }
}

function Copy-Fixture([object]$Value) {
    return ($Value | ConvertTo-Json -Depth 32 | ConvertFrom-Json -AsHashtable -Depth 32)
}

function New-IntegrationZip([Collections.IDictionary]$Entries, [string]$Extension) {
    $script:FixtureId++
    $path = Join-Path $script:FixtureRoot ("fixture-$script:FixtureId$Extension")
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $entry = $zip.CreateEntry([string]$name)
            $stream = $entry.Open()
            try {
                [byte[]]$bytes = if ($Entries[$name] -is [byte[]]) { $Entries[$name] } else { [Text.Encoding]::UTF8.GetBytes([string]$Entries[$name]) }
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    return $path
}

function Assert-Integration([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}

try {
    $script:MockCommit = 'a' * 40
    $script:MockVersion = '5.1.0-preview.recovery'
    $script:MockTag = "v$script:MockVersion"
    $global:MockCommit = $script:MockCommit
    $global:MockTag = $script:MockTag
    $script:CurrentMainCommit = 'b' * 40
    $script:FixtureId = 0
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $script:FixtureRoot = Join-Path $tempParent ('sonnetdb-issue402-resume-' + [Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($script:FixtureRoot)
    $script:ExternalWrites = 0

    $plan = @(& $scriptPath -Repository 'IoTSharp/SonnetDB' -Tag 'v4.0.0') | Where-Object { $_ -is [pscustomobject] } | Select-Object -Last 1
    if ($null -eq $plan -or $plan.CommitSha -ne 'd49babae0e049ca4554d26772b92493220d42822') {
        throw 'Plan mode did not return the resolved tag commit.'
    }
    if ($plan.Version -ne '4.0.0' -or $plan.MutatesTag -ne $false) {
        throw 'Plan mode did not preserve tag/version identity or tag immutability.'
    }
    if (($plan.PreflightWorkflows -join ',') -cne 'publish.yml,connectors-release.yml,docker-publish.yml') {
        throw 'Preflight workflow inventory changed unexpectedly.'
    }
    if (($plan.TaggedWorkflowOrder -join ',') -cne 'publish.yml,docker-publish.yml,connectors-release.yml') {
        throw 'Tagged recovery order must publish primary assets before connectors.'
    }

    . $scriptPath -Repository 'IoTSharp/SonnetDB' -Tag 'v4.0.0' | Out-Null
    $runs = @(
        [pscustomobject]@{ databaseId = 1; createdAt = '2026-09-28T00:01:00Z' },
        [pscustomobject]@{ databaseId = 2; createdAt = '2026-09-28T00:02:00Z' }
    )
    $ordered = @($runs | Sort-Object @{ Expression = { Get-WorkflowRunCreatedAt -Run $_ }; Descending = $true })
    if (($ordered.databaseId -join ',') -cne '2,1') {
        throw 'Workflow run timestamp ordering did not select the newest run.'
    }
    $rejectedTimestamp = $false
    try {
        Get-WorkflowRunCreatedAt -Run ([pscustomobject]@{ createdAt = @('2026-09-28T00:00:00Z', '2026-09-28T00:01:00Z') }) | Out-Null
    }
    catch {
        $rejectedTimestamp = $_.Exception.Message -like '*exactly one createdAt timestamp*'
    }
    if (-not $rejectedTimestamp) {
        throw 'Multiple createdAt values were accepted as one workflow run timestamp.'
    }

    $rejected = $false
    try { & $scriptPath -Repository 'IoTSharp/SonnetDB' -Tag 'v4.0.0' -Version '4.0.1' | Out-Null }
    catch { $rejected = $_.Exception.Message -like '*do not identify the same release*' }
    if (-not $rejected) { throw 'Mismatched tag/version was accepted.' }

    $packagePath = New-IntegrationZip ([ordered]@{
        'SonnetDB.Core.nuspec' = "<package><metadata><id>SonnetDB.Core</id><version>$script:MockVersion</version><repository type='git' commit='$script:MockCommit'/></metadata></package>"
        'lib/net10.0/SonnetDB.Core.dll' = 'fixture-dll'
        '[Content_Types].xml' = '<Types/>'
    }) '.nupkg'
    $packageIdentity = Get-TaggedNuGetPayloadIdentity -Path $packagePath -PackageId 'SonnetDB.Core' `
        -Version $script:MockVersion -CommitSha $script:MockCommit
    $archivePath = New-IntegrationZip ([ordered]@{
        "packages/SonnetDB.Core.$script:MockVersion.nupkg" = [IO.File]::ReadAllBytes($packagePath)
    }) '.zip'
    $archiveInventory = Get-PublicationArchiveInventory -Path $archivePath
    $archiveEntry = $archiveInventory.entries[0]
    $assetName = "SonnetDB.Core.$script:MockVersion.nupkg"
    $approvedFile = @{ path = $archiveEntry.path; sha256 = $archiveEntry.sha256; bytes = $archiveEntry.bytes; assetName = $assetName }
    $artifactName = "nuget-packages-$script:MockVersion"
    $jobs = @('NuGet Packages', 'Release Bundles (linux-x64)', 'Release Bundles (win-x64)') | ForEach-Object {
        @{ name = $_; status = 'completed'; conclusion = 'success'; run_id = 101; run_attempt = 2 }
    }
    $sourceRun = @{ id = 101; run_attempt = 2; repository = @{ full_name = 'IoTSharp/SonnetDB' }; event = 'push';
        path = '.github/workflows/publish.yml'; head_branch = $script:MockTag; head_sha = $script:MockCommit; status = 'completed'; conclusion = 'success' }
    $manifest = @{ schemaVersion = 1; repository = 'IoTSharp/SonnetDB'; tag = $script:MockTag; commitSha = $script:MockCommit;
        sources = @(@{ runId = 101; runAttempt = 2; workflow = 'publish.yml'; artifacts = @(@{ id = 201; name = $artifactName;
                    sha256 = $archiveInventory.fileSha256; files = @($approvedFile) }) }) }
    $manifestPath = Join-Path $script:FixtureRoot 'approved-manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $packageBytes = (Get-Item -LiteralPath $packagePath).Length
    $packageSha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $packageAsset = @{ name = $assetName; sha256 = $packageSha; bytes = $packageBytes; assetId = 77 }
    $readinessPackages = @('SonnetDB', 'SonnetDB.Caching.Distributed', 'SonnetDB.Caching.EasyCaching', 'SonnetDB.CAP',
        'SonnetDB.Cli', 'SonnetDB.Client', 'SonnetDB.Core', 'SonnetDB.EntityFrameworkCore', 'SonnetDB.Protocol', 'SonnetDB.Testcontainers')
    $currentReadiness = @{ status = 'READY'; repository = 'IoTSharp/SonnetDB'; commitSha = $script:MockCommit; version = $script:MockVersion;
        source = 'github'; policyRevision = 'tag-publication-v1'; policySource = 'current-main'; policyCommit = $script:CurrentMainCommit; packageIds = $readinessPackages }
    $frozenReadiness = Copy-Fixture $currentReadiness
    $frozenReadiness.policySource = 'frozen-tag'
    $frozenReadiness.policyCommit = $script:MockCommit
    $evidence = @{ schemaVersion = 1; repository = 'IoTSharp/SonnetDB'; tag = $script:MockTag; version = $script:MockVersion;
        commitSha = $script:MockCommit; currentMainCommit = $script:CurrentMainCommit;
        manifest = @{ path = [IO.Path]::GetFileName($manifestPath); sha256 = $manifestHash }; artifactRoot = '.';
        sources = @(@{ run = $sourceRun; jobs = @($jobs) });
        artifacts = @(@{ id = 201; name = $artifactName; expired = $false; digest = "sha256:$($archiveInventory.fileSha256)";
            workflow_run = @{ id = 101; head_sha = $script:MockCommit } });
        downloads = @(@{ artifactId = 201; archivePath = [IO.Path]::GetFileName($archivePath) });
        currentReadiness = $currentReadiness; frozenReadiness = $frozenReadiness;
        nuget = @(@{ candidate = $packageIdentity; readState = 'FOUND'; published = $packageIdentity });
        release = @{ readState = 'FOUND'; releaseId = 42; expectedAssets = @($packageAsset);
            release = @{ id = 42; tag_name = $script:MockTag };
            existingAssets = @(@{ id = 77; name = $assetName; digest = "sha256:$packageSha"; state = 'uploaded'; size = $packageBytes }) } }
    $evidencePath = Join-Path $script:FixtureRoot 'recovery-evidence.json'
    [IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))

    $direct = Assert-RecoveryOfflineEvidence -Path $evidencePath -ExpectedRepository 'IoTSharp/SonnetDB' `
        -ExpectedTag $script:MockTag -ExpectedVersion $script:MockVersion -ExpectedCommitSha $script:MockCommit
    Assert-Integration ($direct.state -ceq 'OFFLINE_ADMISSION_VERIFIED' -and -not $direct.externalWriteAuthorized) `
        'Offline admission did not return a non-authorizing verified plan.'
    $offlineOutput = @(& $scriptPath -Repository 'IoTSharp/SonnetDB' -Tag $script:MockTag -Version $script:MockVersion `
        -Resume -Offline -RecoveryEvidencePath $evidencePath)
    $offlineResult = $offlineOutput | Where-Object { $_ -is [pscustomobject] -and $_.state -ceq 'OFFLINE_ADMISSION_VERIFIED' } | Select-Object -Last 1
    Assert-Integration ($null -ne $offlineResult -and -not $offlineResult.externalWriteAuthorized) `
        'Offline -Resume did not return the verified admission plan.'
    Assert-Integration ($script:ExternalWrites -eq 0) 'Offline -Resume attempted an external write.'

    $unknownEvidence = Copy-Fixture $evidence
    $unknownEvidence.nuget[0].readState = 'UNKNOWN_NOT_FOUND'
    $unknownPath = Join-Path $script:FixtureRoot 'unknown-evidence.json'
    [IO.File]::WriteAllText($unknownPath, ($unknownEvidence | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
    $unknownRejected = $false
    try { & $scriptPath -Repository 'IoTSharp/SonnetDB' -Tag $script:MockTag -Version $script:MockVersion `
        -Resume -Offline -RecoveryEvidencePath $unknownPath | Out-Null }
    catch { $unknownRejected = $_.Exception.Message -like '*NuGet reconciliation is not safe to resume*' }
    Assert-Integration $unknownRejected 'Unknown write state was accepted by offline -Resume.'
    Assert-Integration ($script:ExternalWrites -eq 0) 'Unknown offline evidence attempted an external write.'

    $missingEvidenceRejected = $false
    try { & $scriptPath -Repository 'IoTSharp/SonnetDB' -Tag $script:MockTag -Version $script:MockVersion -Resume | Out-Null }
    catch { $missingEvidenceRejected = $_.Exception.Message -like '*RecoveryEvidencePath*' }
    Assert-Integration $missingEvidenceRejected 'Resume without approved offline evidence was accepted.'
    Assert-Integration ($script:ExternalWrites -eq 0) 'Resume without evidence attempted an external write.'

    Write-Host 'Tagged release recovery plan contracts passed.'
}
finally {
    if ($null -ne $script:FixtureRoot) {
        $resolved = [IO.Path]::GetFullPath($script:FixtureRoot)
        if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notmatch '\Asonnetdb-issue402-resume-[0-9a-f]{32}\z') { throw 'Refusing unowned integration fixture cleanup.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    Remove-Item -LiteralPath Function:\global:git -ErrorAction SilentlyContinue
}
