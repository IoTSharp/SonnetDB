param(
    [Parameter(Mandatory = $true)]
    [string] $ReportPath,
    [switch] $AllowUnavailableEnvironment
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required.'
}

$resolved = (Resolve-Path -LiteralPath $ReportPath).Path
$report = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json

function Fail-NotReady([string] $Reason) {
    [pscustomobject]@{
        status = 'NOT_READY'
        reason = $Reason
        profile = [string]$report.profile
    } | ConvertTo-Json -Compress
    throw $Reason
}

$requiredProfiles = @('high-cardinality', 'small-segments', 'maintenance-chaos', 'many-measurements')
if ($requiredProfiles -notcontains [string]$report.profile) {
    Fail-NotReady "Report profile '$($report.profile)' is not an M19 #125 capacity profile."
}

$target = $report.targetHardware
if ($null -eq $target) {
    Fail-NotReady 'Report is missing targetHardware status/id/contract.'
}

$expectedByProfile = @{
    'high-cardinality' = @{ series = 1000000; targetSegments = 1; pointsPerSegment = 1; restartCount = 1; recoverySamples = 5; querySamples = 100; maintenanceBatches = 1; pointsPerBatch = 4096; dropMeasurements = 1; measurements = 1 }
    'small-segments' = @{ series = 32; targetSegments = 10000; pointsPerSegment = 1; restartCount = 1; recoverySamples = 5; querySamples = 100; maintenanceBatches = 1; pointsPerBatch = 1; dropMeasurements = 1; measurements = 1 }
    'maintenance-chaos' = @{ series = 64; targetSegments = 1; pointsPerSegment = 1; restartCount = 20; recoverySamples = 20; querySamples = 100; maintenanceBatches = 4; pointsPerBatch = 64; dropMeasurements = 1; measurements = 1 }
    'many-measurements' = @{ series = 1; targetSegments = 1; pointsPerSegment = 1; restartCount = 1; recoverySamples = 5; querySamples = 100; maintenanceBatches = 1; pointsPerBatch = 100; dropMeasurements = 100; measurements = 10000 }
}
$expected = $expectedByProfile[[string]$report.profile]
if ($null -eq $expected) {
    Fail-NotReady "Profile '$($report.profile)' does not have a fixed target contract."
}
if ([int]$report.options.cycles -ne 1) {
    Fail-NotReady "Profile '$($report.profile)' must use one capacity cycle."
}
if ($null -ne $expected.series -and [int]$report.options.series -ne $expected.series) {
        Fail-NotReady "Profile '$($report.profile)' must use default series=$($expected.series)."
    }
if ($null -ne $expected.targetSegments -and [int]$report.options.targetSegments -ne $expected.targetSegments) {
        Fail-NotReady "Profile '$($report.profile)' must use default targetSegments=$($expected.targetSegments)."
    }
if ($null -ne $expected.measurements -and [int]$report.options.measurements -ne $expected.measurements) {
        Fail-NotReady "Profile '$($report.profile)' must use default measurements=$($expected.measurements)."
    }
foreach ($name in @('pointsPerSegment', 'restartCount', 'recoverySamples', 'querySamples', 'maintenanceBatches', 'pointsPerBatch', 'dropMeasurements')) {
    if ($null -ne $expected.$name -and [int]$report.options.$name -ne $expected.$name) {
        Fail-NotReady "Profile '$($report.profile)' must use default $name=$($expected.$name)."
    }
}
if ([string]$report.environment.commitSha -notmatch '^[0-9a-fA-F]{40}$') {
    Fail-NotReady 'Report environment.commitSha must be a 40-character SHA-1.'
}

$disk = $report.environment.disk
if ($null -eq $disk) {
    Fail-NotReady 'Report is missing environment.disk.'
}
if ($disk.totalBytes -le 0 -or $disk.availableBytes -lt 0) {
    if (-not $AllowUnavailableEnvironment) {
        Fail-NotReady 'Disk capacity snapshot is unavailable; this cannot be used as fixed-hardware evidence.'
    }
}

if ($null -eq $report.summary -or $null -eq $report.summary.recoveryLatency -or $null -eq $report.summary.queryLatency) {
    Fail-NotReady 'Report must include recovery and query latency summaries.'
}

$integrity = $report.summary.integrity
if ($null -eq $integrity) {
    Fail-NotReady 'Report must include an integrity summary.'
}
foreach ($name in @('missingPoints', 'duplicatePoints', 'unexpectedPoints', 'valueMismatches')) {
    if ($null -eq $integrity.$name) {
        Fail-NotReady "Integrity summary is missing '$name'."
    }
}

if (-not $report.succeeded) {
    Fail-NotReady 'Runner result is not PASS; failed runs cannot form capacity evidence.'
}

if ([string]$target.status -ne 'PASS') {
    Fail-NotReady "targetHardware.status is '$($target.status)', expected PASS for fixed-hardware evidence."
}
if ([string]::IsNullOrWhiteSpace([string]$target.id) -or [string]$target.id -eq 'UNDECLARED') {
    Fail-NotReady 'targetHardware.id must identify the frozen target machine.'
}
if ([string]::IsNullOrWhiteSpace([string]$target.contract)) {
    Fail-NotReady 'targetHardware.contract is required.'
}

[pscustomobject]@{
    status = if ($AllowUnavailableEnvironment) { 'VALID_SCHEMA_NOT_FIXED_HARDWARE' } else { 'PASS' }
    profile = $report.profile
    commitSha = $report.environment.commitSha
    machine = $report.environment.machineName
    diskRoot = $disk.root
    peakWorkingSetBytes = $report.summary.peakWorkingSetBytes
    peakManagedMemoryBytes = $report.summary.peakManagedMemoryBytes
    recoveryP95Milliseconds = $report.summary.recoveryLatency.p95Milliseconds
    queryP95Milliseconds = $report.summary.queryLatency.p95Milliseconds
} | ConvertTo-Json -Compress
