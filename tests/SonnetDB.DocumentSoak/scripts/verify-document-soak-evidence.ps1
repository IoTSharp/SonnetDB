param(
    [Parameter(Mandatory = $true)]
    [string] $Report,
    [string] $Output,
    [switch] $AllowNotReady
)

$ErrorActionPreference = 'Stop'
$requiredPhases = @('write', 'index_create', 'indexed_query', 'index_rebuild', 'ttl_cleanup', 'backup', 'hot_reopen', 'cold_process_start', 'crash_recovery', 'backup_restore')
$issues = [System.Collections.Generic.List[string]]::new()

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
    if (-not [bool]$document.succeeded) { $issues.Add('soak_failed') }
    if ($document.environment.commitSha -notmatch '^[0-9a-fA-F]{40}$') { $issues.Add('commit_sha_missing_or_invalid') }
    if ($document.targetHardware.status -ne 'PASS') { $issues.Add('fixed_target_hardware_not_attested') }
    if ([string]::IsNullOrWhiteSpace([string]$document.targetHardware.targetId)) { $issues.Add('target_hardware_id_missing') }
    if ([string]::IsNullOrWhiteSpace([string]$document.targetHardware.contract)) { $issues.Add('target_hardware_contract_missing') }
    if ([string]::IsNullOrWhiteSpace([string]$document.environment.dataVolume.deviceModel) -or $document.environment.dataVolume.deviceModel -eq 'unknown') { $issues.Add('disk_model_missing') }
    if ($document.environment.dataVolume.totalBytes -le 0 -or $document.environment.dataVolume.availableBytes -le 0) { $issues.Add('volume_capacity_missing') }
    if ($document.environment.processorCount -le 0 -or $document.environment.totalAvailableMemoryBytes -le 0) { $issues.Add('machine_specs_missing') }

    $phaseNames = @($document.phases | ForEach-Object { [string]$_.name })
    foreach ($phase in $requiredPhases) {
        if ($phaseNames -notcontains $phase) { $issues.Add("phase_missing:$phase") }
    }
    foreach ($phase in @($document.phases)) {
        if ($phase.durationMilliseconds -lt 0 -or $phase.operations -lt 0) { $issues.Add("phase_values_invalid:$($phase.name)") }
    }
    if (@($document.memorySamples).Count -lt 2) { $issues.Add('memory_curve_insufficient') }
}

$status = if ($issues.Count -eq 0) { 'PASS' } else { 'NOT_READY' }
$verification = [ordered]@{
    schemaVersion = 1
    status = $status
    report = if (Test-Path -LiteralPath $Report) { (Resolve-Path -LiteralPath $Report).Path } else { $Report }
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    issues = @($issues)
    releaseEvidence = ($status -eq 'PASS')
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
