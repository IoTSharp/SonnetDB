param(
    [Parameter(Mandatory = $true)]
    [string] $Report,
    [string] $Output,
    [string] $ExpectedCommitSha,
    [string] $ExpectedTargetHardwareId,
    [switch] $AllowNotReady
)

$ErrorActionPreference = 'Stop'
$expectedTargetHardwareContract = 'M25-#174-fixed-target-v1'
$requiredPhases = @('write', 'index_create', 'indexed_query', 'index_rebuild', 'ttl_index_create', 'ttl_cleanup', 'backup', 'hot_reopen', 'cold_process_start', 'crash_recovery', 'backup_restore')
$requiredMemoryPhases = @('write', 'hot_end', 'completed')
$issues = [System.Collections.Generic.List[string]]::new()

function Test-FiniteNonNegativeNumber([object] $Value) {
    if ($null -eq $Value -or $Value -is [bool]) { return $false }

    [double] $number = 0
    if (-not [double]::TryParse(
        [string]$Value,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$number)) {
        return $false
    }

    return -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and $number -ge 0
}

function Test-RequiredProperty([object] $Object, [string] $Name) {
    return $null -ne $Object -and
        $null -ne $Object.PSObject.Properties[$Name] -and
        $null -ne $Object.$Name
}

function Test-IsoTimestamp([object] $Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $false }

    [DateTimeOffset] $timestamp = [DateTimeOffset]::MinValue
    return [DateTimeOffset]::TryParse(
        [string]$Value,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$timestamp)
}

try {
    if (-not (Test-Path -LiteralPath $Report -PathType Leaf)) {
        throw 'report_missing'
    }
    $document = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
} catch {
    $issue = if ($_.Exception.Message -eq 'report_missing') { 'report_missing' } else { 'report_invalid_json' }
    $issues.Add($issue)
    $document = $null
}

if ($null -ne $document) {
    if ($document.schemaVersion -ne 2) { $issues.Add('schema_version_invalid') }
    if ($document.profile -notin @('million', 'ten-million')) { $issues.Add('profile_not_release_scale') }
    $expected = if ($document.profile -eq 'million') { 1000000 } elseif ($document.profile -eq 'ten-million') { 10000000 } else { 0 }
    if ($document.documentCount -ne $expected) { $issues.Add('document_count_mismatch') }
    if ($document.succeeded -isnot [bool] -or $document.succeeded -ne $true) { $issues.Add('soak_failed') }
    if (-not (Test-RequiredProperty $document 'batchSize') -or -not (Test-FiniteNonNegativeNumber $document.batchSize) -or [double]$document.batchSize -le 0) { $issues.Add('batch_size_invalid') }
    if (-not (Test-IsoTimestamp $document.startedAtUtc) -or -not (Test-IsoTimestamp $document.completedAtUtc)) { $issues.Add('timestamps_invalid') }
    elseif ([DateTimeOffset]$document.completedAtUtc -lt [DateTimeOffset]$document.startedAtUtc) { $issues.Add('timestamps_out_of_order') }

    foreach ($name in @('environment', 'targetHardware', 'phases', 'memorySamples')) {
        if (-not (Test-RequiredProperty $document $name)) { $issues.Add("$name`_missing") }
    }

    $environment = if (Test-RequiredProperty $document 'environment') { $document.environment } else { $null }
    $targetHardware = if (Test-RequiredProperty $document 'targetHardware') { $document.targetHardware } else { $null }
    $dataVolume = if (Test-RequiredProperty $environment 'dataVolume') { $environment.dataVolume } else { $null }
    if ($null -eq $dataVolume) { $issues.Add('data_volume_missing') }

    $reportCommitSha = [string]$environment.commitSha
    if ($reportCommitSha -notmatch '^[0-9a-fA-F]{40}$') { $issues.Add('commit_sha_missing_or_invalid') }
    if ([string]::IsNullOrWhiteSpace($ExpectedCommitSha)) {
        $issues.Add('expected_commit_sha_missing')
    }
    elseif ($ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
        $issues.Add('expected_commit_sha_invalid')
    }
    elseif ($reportCommitSha -notmatch '^[0-9a-fA-F]{40}$' -or
        -not [string]::Equals($reportCommitSha, $ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase)) {
        $issues.Add('expected_commit_sha_mismatch')
    }
    if ([string]$targetHardware.status -ne 'PASS') { $issues.Add('fixed_target_hardware_not_attested') }
    $reportTargetHardwareId = [string]$targetHardware.targetId
    if ([string]::IsNullOrWhiteSpace($reportTargetHardwareId)) { $issues.Add('target_hardware_id_missing') }
    if ([string]::IsNullOrWhiteSpace($ExpectedTargetHardwareId)) {
        $issues.Add('expected_target_hardware_id_missing')
    }
    elseif (-not [string]::Equals($reportTargetHardwareId, $ExpectedTargetHardwareId, [StringComparison]::Ordinal)) {
        $issues.Add('expected_target_hardware_id_mismatch')
    }
    if ([string]::IsNullOrWhiteSpace([string]$targetHardware.contract)) { $issues.Add('target_hardware_contract_missing') }
    elseif ([string]$targetHardware.contract -ne $expectedTargetHardwareContract) { $issues.Add('target_hardware_contract_invalid') }
    if ([string]::IsNullOrWhiteSpace([string]$dataVolume.deviceModel) -or [string]$dataVolume.deviceModel -eq 'unknown') { $issues.Add('disk_model_missing') }
    if (-not (Test-FiniteNonNegativeNumber $dataVolume.totalBytes) -or -not (Test-FiniteNonNegativeNumber $dataVolume.availableBytes) -or [double]$dataVolume.totalBytes -le 0 -or [double]$dataVolume.availableBytes -le 0) { $issues.Add('volume_capacity_missing') }
    if (-not (Test-FiniteNonNegativeNumber $environment.processorCount) -or -not (Test-FiniteNonNegativeNumber $environment.totalAvailableMemoryBytes) -or [double]$environment.processorCount -le 0 -or [double]$environment.totalAvailableMemoryBytes -le 0) { $issues.Add('machine_specs_missing') }

    $phaseNames = @($document.phases | ForEach-Object { [string]$_.name })
    foreach ($phase in $requiredPhases) {
        if ($phaseNames -notcontains $phase) { $issues.Add("phase_missing:$phase") }
    }
    $uniquePhaseCount = @($phaseNames | Sort-Object -Unique).Count
    if ($phaseNames.Count -ne $uniquePhaseCount) {
        $issues.Add('phase_names_duplicate')
    }
    foreach ($phaseName in $phaseNames) {
        if ($phaseName -notin $requiredPhases) { $issues.Add("phase_unexpected:$phaseName") }
    }
    foreach ($phase in @($document.phases)) {
        $phaseName = [string]$phase.name
        if (-not (Test-RequiredProperty $phase 'name') -or [string]::IsNullOrWhiteSpace($phaseName) -or
            -not (Test-FiniteNonNegativeNumber $phase.durationMilliseconds) -or
            -not (Test-FiniteNonNegativeNumber $phase.operations) -or
            -not (Test-FiniteNonNegativeNumber $phase.operationsPerSecond) -or
            -not (Test-RequiredProperty $phase 'details')) {
            $issues.Add("phase_values_invalid:$phaseName")
        }
    }
    $memorySamples = @($document.memorySamples)
    if ($memorySamples.Count -lt 2) { $issues.Add('memory_curve_insufficient') }
    $memoryPhaseNames = @($memorySamples | ForEach-Object { [string]$_.phase })
    foreach ($phase in $requiredMemoryPhases) {
        if ($memoryPhaseNames -notcontains $phase) { $issues.Add("memory_phase_missing:$phase") }
    }
    foreach ($sample in $memorySamples) {
        $samplePhase = [string]$sample.phase
        if (-not (Test-IsoTimestamp $sample.timestampUtc) -or
            [string]::IsNullOrWhiteSpace($samplePhase) -or
            -not (Test-FiniteNonNegativeNumber $sample.documents) -or
            -not (Test-FiniteNonNegativeNumber $sample.workingSetBytes) -or
            -not (Test-FiniteNonNegativeNumber $sample.privateBytes) -or
            -not (Test-FiniteNonNegativeNumber $sample.managedBytes)) {
            $issues.Add("memory_sample_invalid:$samplePhase")
        }
    }
}

$reportStatus = if ($issues.Count -eq 0) { 'PASS' } else { 'NOT_READY' }
if ($reportStatus -eq 'PASS') {
    # The current workflow has no protected CI bundle or independently verifiable
    # attestation. A report's own PASS fields are therefore never release evidence.
    $issues.Add('external_attestation_missing')
}
$status = if ($issues.Count -eq 0) { 'PASS' } else { 'NOT_READY' }
$verification = [ordered]@{
    schemaVersion = 1
    status = $status
    reportStatus = $reportStatus
    releaseDecision = if ($reportStatus -eq 'PASS') { 'DEFERRED' } else { 'NOT_READY' }
    report = if (Test-Path -LiteralPath $Report) { (Resolve-Path -LiteralPath $Report).Path } else { $Report }
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    issues = @($issues)
    releaseEvidence = $false
    authority = 'self-declared-report-only'
    attestationLimitations = @(
        'The report records a commit and target hardware declaration, but those fields are self-declared and are not a trust root.',
        'DocumentSoak has no protected CI artifact bundle or independently verifiable attestation contract yet; release evidence remains DEFERRED.'
    )
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path (Split-Path -Parent $Report) 'verification.json'
}
$parent = Split-Path -Parent $Output
if (-not [string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$verification | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding utf8NoBOM
$verification | ConvertTo-Json -Depth 8

if ($status -ne 'PASS' -and -not $AllowNotReady) {
    throw "Document soak evidence is NOT_READY: $($issues -join ', ')"
}
