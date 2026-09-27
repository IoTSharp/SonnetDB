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
if ($scriptSource -notmatch 'Assert-ReleaseReadiness\s*\r?\n\s*# Publish owns GitHub Release creation') {
    throw 'Release readiness must run before the tagged workflow recovery sequence.'
}
if ($scriptSource -match '(?im)^\s*&?\s*git\s+(?:push|tag|update-ref)\b') {
    throw 'Recovery must not mutate the release tag.'
}
$parityVerifierPath = Join-Path $PSScriptRoot '../tests/SonnetDB.Parity/scripts/verify-parity-nightly-evidence.ps1'
$parityVerifierSource = Get-Content -LiteralPath $parityVerifierPath -Raw
if ($parityVerifierSource -notmatch 'Microsoft\\WinGet\\Links\\gh\.exe') {
    throw 'Parity evidence verification must resolve a current-user WinGet gh installation when PATH is stale.'
}
if ($parityVerifierSource -notmatch '\$script:GitHubCliPath') {
    throw 'Parity evidence verification must use the resolved gh executable path for API and artifact calls.'
}

function global:git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    if ($Arguments -join ' ' -notmatch 'ls-remote') { throw 'Unexpected git command.' }
    $global:LASTEXITCODE = 0
    "d49babae0e049ca4554d26772b92493220d42822`trefs/tags/v4.0.0"
}

try {
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

    Write-Host 'Tagged release recovery plan contracts passed.'
}
finally {
    Remove-Item -LiteralPath Function:\global:git -ErrorAction SilentlyContinue
}
