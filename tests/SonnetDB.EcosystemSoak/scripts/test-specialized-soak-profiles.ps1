[CmdletBinding()]
param(
    # Explicit output roots retain raw reports and workload files, including failed runs.
    [string] $OutputRoot,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required.'
}

$projectPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$retainOutput = -not [string]::IsNullOrWhiteSpace($OutputRoot)
$root = if ($retainOutput) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) (
        'm19-specialized-profile-smoke-' + [guid]::NewGuid().ToString('N'))))
}
if (Test-Path -LiteralPath $root) {
    throw "Specialized profile output root must be new: $root"
}
$targetEnvironmentNames = @(
    'SONNETDB_M19_TARGET_HARDWARE_STATUS',
    'SONNETDB_M19_TARGET_HARDWARE_ID',
    'SONNETDB_M19_TARGET_HARDWARE_CONTRACT',
    'SONNETDB_M19_STORAGE_MODEL',
    'SONNETDB_M19_DISK_MODEL'
)
$originalTargetEnvironment = @{}

function Require-Property([object] $Object, [string] $Name, [string] $Context) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        throw "$Context is missing '$Name'."
    }

    return $Object.$Name
}

function Require-String([object] $Value, [string] $Context) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        throw "$Context must be a non-empty string."
    }

    return [string]$Value
}

function Require-Integer([object] $Value, [string] $Context, [long] $Minimum) {
    try {
        [long] $number = [Convert]::ToInt64($Value, [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "$Context must be an integer."
    }

    if ($number -lt $Minimum) {
        throw "$Context must be at least $Minimum."
    }

    return $number
}

function Require-Boolean([object] $Value, [string] $Context) {
    if ($Value -isnot [bool]) {
        throw "$Context must be Boolean."
    }

    return [bool]$Value
}

function Require-Array([object] $Object, [string] $Name, [string] $Context) {
    $value = Require-Property $Object $Name $Context
    if ($null -eq $value) {
        return @()
    }

    return @($value)
}

function Assert-MaintenanceChaosReservations(
    [object[]] $Reservations,
    [int] $ExpectedRestartCount,
    [int] $ExpectedSeries,
    [int] $ExpectedPointsPerBatch,
    [int] $ExpectedMinimumBatches,
    [long] $RecoveredTail,
    [string] $Context) {
    if ($Reservations.Count -ne $ExpectedRestartCount) {
        throw "$Context must contain exactly $ExpectedRestartCount reservations."
    }

    [long] $previousEnd = -1
    [decimal] $reservedCurrentPoints = 0
    [decimal] $acknowledgedCurrentPoints = 0
    [decimal] $reservedButUnacknowledged = 0
    for ($index = 0; $index -lt $Reservations.Count; $index++) {
        $reservation = $Reservations[$index]
        $restart = Require-Integer (Require-Property $reservation 'restart' $Context) "$Context.restart" 1
        if ($restart -ne ($index + 1)) {
            throw "$Context.restart must be sequential from 1 through $ExpectedRestartCount."
        }

        $start = Require-Integer (Require-Property $reservation 'startInclusive' $Context) "$Context.startInclusive" 0
        $end = Require-Integer (Require-Property $reservation 'endInclusive' $Context) "$Context.endInclusive" 0
        $acknowledged = Require-Integer (Require-Property $reservation 'acknowledgedThroughInclusive' $Context) "$Context.acknowledgedThroughInclusive" ([long]::MinValue)
        if ($end -lt $start) {
            throw "$Context must use non-empty inclusive reservation ranges."
        }
        if ($index -eq 0) {
            if ($start -ne 0) {
                throw "$Context must reserve from sequence zero."
            }
        }
        elseif ($previousEnd -eq [long]::MaxValue -or $start -ne ($previousEnd + 1)) {
            throw "$Context reservation ranges must be contiguous and non-overlapping."
        }

        if ($acknowledged -lt $start -or $acknowledged -gt $end) {
            throw "$Context.acknowledgedThroughInclusive must be within startInclusive through endInclusive."
        }
        $acknowledgedSpan = [decimal]$acknowledged - [decimal]$start + 1
        if ($acknowledgedSpan % $ExpectedPointsPerBatch -ne 0) {
            throw "$Context acknowledged span must contain complete pointsPerBatch batches."
        }
        $minimumAcknowledgedPoints = [decimal][Math]::Max(
            [long]$ExpectedSeries,
            [long]$ExpectedPointsPerBatch * [long]$ExpectedMinimumBatches)
        if ($acknowledgedSpan -lt $minimumAcknowledgedPoints) {
            throw "$Context acknowledged span is shorter than the pre-kill progress target."
        }
        $reservedCurrentPoints += [decimal]$end - [decimal]$start + 1
        $acknowledgedCurrentPoints += $acknowledgedSpan
        $reservedButUnacknowledged += [decimal]$end - [decimal]$acknowledged
        $previousEnd = $end
    }

    if ([decimal]$RecoveredTail -gt $reservedButUnacknowledged) {
        throw "$Context recovered tail exceeds the reserved-but-unacknowledged span."
    }

    return [pscustomobject]@{
        ReservedCurrentPoints = $reservedCurrentPoints
        AcknowledgedCurrentPoints = $acknowledgedCurrentPoints
        ReservedButUnacknowledgedPoints = $reservedButUnacknowledged
    }
}

function Assert-MaintenanceChaosWorkerReservationConfiguration(
    [object[]] $Configurations,
    [int] $ExpectedPointsPerBatch,
    [string] $Context) {
    $workers = @($Configurations | Where-Object { [string]$_.scope -eq 'maintenance-chaos-worker' })
    if ($workers.Count -ne 1) {
        throw "$Context must contain exactly one maintenance-chaos-worker configuration."
    }

    $settings = Require-Property $workers[0] 'settings' "$Context maintenance-chaos-worker"
    $expectedSettings = [ordered]@{
        progressWritePoint = 'after-WriteMany-returns'
        reservationWritePoint = 'before-WriteMany'
        reservationRecord = 'batch-start-and-end-inclusive'
        reservationContentFlush = 'FileStream.Flush(true)'
        reservationReplace = 'File.Move-overwrite-same-directory'
        restartReservationEvidence = 'worker-start-through-last-reserved-end'
        expiredPointsPerBatch = '1'
        currentPointsPerBatch = [string]$ExpectedPointsPerBatch
    }
    foreach ($pair in $expectedSettings.GetEnumerator()) {
        $actual = Require-String (Require-Property $settings $pair.Key "$Context maintenance-chaos-worker settings") "$Context maintenance-chaos-worker settings.$($pair.Key)"
        if ($actual -cne $pair.Value) {
            throw "$Context maintenance-chaos-worker setting '$($pair.Key)' does not match its reservation evidence contract."
        }
    }
}

function Assert-NoMaintenanceChaosProgressTemporaryFiles([string] $WorkRoot, [string] $Context) {
    if (-not (Test-Path -LiteralPath $WorkRoot -PathType Container)) {
        throw "$Context retained work root is missing."
    }

    $temporaryFiles = @(
        Get-ChildItem -LiteralPath $WorkRoot -Recurse -File |
            Where-Object {
                $_.Name -match '^progress-\d{4}\.txt(?:\.reservation)?\.tmp$'
            })
    if ($temporaryFiles.Count -ne 0) {
        $paths = $temporaryFiles | ForEach-Object { $_.FullName }
        throw "$Context left progress/reservation temporary files: $($paths -join '; ')"
    }
}

function Assert-MaintenanceChaosReservationDetails(
    [object] $Details,
    [object] $Totals,
    [int] $ExpectedRestartCount,
    [string] $Context) {
    $expectedValues = [ordered]@{
        restarts = [decimal]$ExpectedRestartCount
        restartReservations = [decimal]$ExpectedRestartCount
        acknowledgedPoints = [decimal]$Totals.AcknowledgedCurrentPoints
        reservedCurrentPoints = [decimal]$Totals.ReservedCurrentPoints
        reservedButUnacknowledgedPoints = [decimal]$Totals.ReservedButUnacknowledgedPoints
    }
    foreach ($pair in $expectedValues.GetEnumerator()) {
        $actual = Require-Integer (Require-Property $Details $pair.Key $Context) "$Context.$($pair.Key)" 0
        if ([decimal]$actual -ne $pair.Value) {
            throw "$Context.$($pair.Key) does not match maintenanceChaosReservations."
        }
    }

    if ((Require-String (Require-Property $Details 'reservationWritePoint' $Context) "$Context.reservationWritePoint") -cne 'before-WriteMany') {
        throw "$Context.reservationWritePoint must be before-WriteMany."
    }
    if ((Require-String (Require-Property $Details 'reservationPublication' $Context) "$Context.reservationPublication") -cne 'content-flush-then-atomic-file-replace') {
        throw "$Context.reservationPublication does not match the fixed reservation publication contract."
    }
}

function Assert-Configuration([object] $Configuration, [string] $Context) {
    foreach ($name in @('scope', 'source', 'durabilityMode')) {
        [void](Require-String (Require-Property $Configuration $name $Context) "$Context.$name")
    }

    foreach ($name in @(
        'syncWalOnEveryWrite',
        'flushWalToOsOnWrite',
        'segmentFsyncOnCommit',
        'backgroundFlushEnabled',
        'compactionEnabled',
        'retentionEnabled')) {
        $value = Require-Property $Configuration $name $Context
        if ($null -ne $value) {
            [void](Require-Boolean $value "$Context.$name")
        }
    }

    $settings = Require-Property $Configuration 'settings' $Context
    if ($null -eq $settings -or @($settings.PSObject.Properties).Count -eq 0) {
        throw "$Context.settings must contain captured settings."
    }
}

function Assert-StrictIntegrity([object] $Integrity, [switch] $AllowRecoveredTail) {
    [void](Require-String (Require-Property $Integrity 'scope' 'Report integrity') 'Report integrity.scope')
    $expected = Require-Integer (Require-Property $Integrity 'expectedPoints' 'Report integrity') 'Report integrity.expectedPoints' 1
    $observed = Require-Integer (Require-Property $Integrity 'observedPoints' 'Report integrity') 'Report integrity.observedPoints' 0
    foreach ($name in @('missingPoints', 'duplicatePoints', 'valueMismatches')) {
        if ((Require-Integer (Require-Property $Integrity $name 'Report integrity') "Report integrity.$name" 0) -ne 0) {
            throw "Report integrity.$name must be zero."
        }
    }

    $unexpected = Require-Integer (Require-Property $Integrity 'unexpectedPoints' 'Report integrity') 'Report integrity.unexpectedPoints' 0
    if (-not (Require-Boolean (Require-Property $Integrity 'digestMatches' 'Report integrity') 'Report integrity.digestMatches')) {
        throw 'Report integrity.digestMatches must be true.'
    }
    if ($AllowRecoveredTail) {
        if ($observed -ne $expected + $unexpected) {
            throw 'maintenance-chaos observedPoints must equal expectedPoints plus recovered tail points.'
        }
    }
    else {
        if ($unexpected -ne 0 -or $observed -ne $expected) {
            throw 'Strict integrity requires observedPoints=expectedPoints and unexpectedPoints=0.'
        }
    }

    return [pscustomobject]@{
        Expected = $expected
        Unexpected = $unexpected
    }
}

function Assert-Report([object] $Report, [object] $Profile) {
    if ((Require-Integer (Require-Property $Report 'schemaVersion' 'Report') 'Report.schemaVersion' 1) -ne 2) {
        throw "[$($Profile.Name)] report schemaVersion must be 2."
    }
    if (-not (Require-Boolean (Require-Property $Report 'succeeded' 'Report') 'Report.succeeded')) {
        throw "[$($Profile.Name)] runner did not succeed."
    }
    if ((Require-String (Require-Property $Report 'profile' 'Report') 'Report.profile') -ne $Profile.Name) {
        throw "[$($Profile.Name)] report profile does not match the requested profile."
    }

    $target = Require-Property $Report 'targetHardware' 'Report'
    $targetStatus = Require-String (Require-Property $target 'status' 'Report targetHardware') 'Report targetHardware.status'
    $targetId = Require-String (Require-Property $target 'id' 'Report targetHardware') 'Report targetHardware.id'
    $targetDeclarationSource = Require-String (Require-Property $target 'declarationSource' 'Report targetHardware') 'Report targetHardware.declarationSource'
    if ($targetStatus -ne 'NOT_READY' -or $targetId -ne 'UNDECLARED' -or $targetDeclarationSource -ne 'unconfigured') {
        throw "[$($Profile.Name)] scaled smoke must remain an unconfigured NOT_READY target-hardware report."
    }

    $environment = Require-Property $Report 'environment' 'Report'
    $provenance = Require-Property $environment 'provenance' 'Report environment'
    $containerState = Require-String (Require-Property $provenance 'containerState' 'Report environment.provenance') 'Report environment.provenance.containerState'
    if ($containerState -ne 'none') {
        throw "[$($Profile.Name)] non-container smoke must emit the authoritative containerState value 'none', actual '$containerState'."
    }

    $options = Require-Property $Report 'options' 'Report'
    foreach ($pair in $Profile.ExpectedOptions.GetEnumerator()) {
        $actual = Require-Integer (Require-Property $options $pair.Key 'Report options') "Report options.$($pair.Key)" 1
        if ($actual -ne [long]$pair.Value) {
            throw "[$($Profile.Name)] option '$($pair.Key)' expected $($pair.Value), actual $actual."
        }
    }

    $evidence = Require-Property $Report 'evidence' 'Report'
    if ((Require-String (Require-Property $evidence 'contract' 'Report evidence') 'Report evidence.contract') -ne 'M19-#125-capacity-evidence-v2') {
        throw "[$($Profile.Name)] report evidence contract is incorrect."
    }

    $configurations = @(Require-Property $Report 'effectiveConfiguration' 'Report')
    if ($configurations.Count -lt $Profile.ExpectedScopes.Count) {
        throw "[$($Profile.Name)] report has too few effective configuration records."
    }
    foreach ($configuration in $configurations) {
        Assert-Configuration $configuration "[$($Profile.Name)] report effectiveConfiguration"
    }
    foreach ($scope in $Profile.ExpectedScopes) {
        $matches = @($configurations | Where-Object { [string]$_.scope -eq $scope })
        if ($matches.Count -ne 1) {
            throw "[$($Profile.Name)] effectiveConfiguration must contain exactly one '$scope' record."
        }
    }
    if ($Profile.IsMaintenanceChaos) {
        Assert-MaintenanceChaosWorkerReservationConfiguration $configurations ([int]$Profile.ExpectedOptions.pointsPerBatch) "[$($Profile.Name)] report effectiveConfiguration"
    }

    $cycles = @(Require-Property $Report 'cycles' 'Report')
    if ($cycles.Count -ne 1) {
        throw "[$($Profile.Name)] smoke must contain exactly one cycle."
    }
    $phases = @(Require-Property $cycles[0] 'phases' 'Report cycle')
    $actualPhaseNames = @($phases | ForEach-Object { Require-String (Require-Property $_ 'name' 'Report phase') 'Report phase.name' })
    if ($actualPhaseNames.Count -ne $Profile.ExpectedPhases.Count) {
        throw "[$($Profile.Name)] phase count differs from the expected profile journey."
    }
    for ($index = 0; $index -lt $Profile.ExpectedPhases.Count; $index++) {
        if ($actualPhaseNames[$index] -ne $Profile.ExpectedPhases[$index]) {
            throw "[$($Profile.Name)] expected phase '$($Profile.ExpectedPhases[$index])' at index $index, actual '$($actualPhaseNames[$index])'."
        }
    }

    $chaosReservations = @()
    foreach ($phase in $phases) {
        [void](Require-Integer (Require-Property $phase 'operations' 'Report phase') 'Report phase.operations' 1)
        [void](Require-Property $phase 'readOperationDelta' 'Report phase')
        [void](Require-Property $phase 'writeOperationDelta' 'Report phase')
        [void](Require-Property $phase 'readTransferBytesDelta' 'Report phase')
        [void](Require-Property $phase 'writeTransferBytesDelta' 'Report phase')
        $phaseConfigurations = @(Require-Property $phase 'effectiveConfiguration' 'Report phase')
        if ($phaseConfigurations.Count -eq 0) {
            throw "[$($Profile.Name)] phase '$($phase.name)' is missing effective configuration evidence."
        }
        foreach ($configuration in $phaseConfigurations) {
            Assert-Configuration $configuration "[$($Profile.Name)] phase '$($phase.name)' effectiveConfiguration"
        }
        if ($Profile.IsMaintenanceChaos -and [string]$phase.name -eq 'maintenance_chaos_kill_reopen') {
            Assert-MaintenanceChaosWorkerReservationConfiguration $phaseConfigurations ([int]$Profile.ExpectedOptions.pointsPerBatch) "[$($Profile.Name)] phase '$($phase.name)' effectiveConfiguration"
        }
        $reservations = @(Require-Array $phase 'maintenanceChaosReservations' "[$($Profile.Name)] phase '$($phase.name)'")
        if ($Profile.IsMaintenanceChaos -and [string]$phase.name -eq 'maintenance_chaos_kill_reopen') {
            $chaosReservations = $reservations
        }
        elseif ($reservations.Count -ne 0) {
            throw "[$($Profile.Name)] maintenanceChaosReservations must be empty outside maintenance-chaos."
        }
    }

    $summary = Require-Property $Report 'summary' 'Report'
    $resources = Require-Property $summary 'resources' 'Report summary'
    foreach ($name in @(
        'totalReadOperationDelta',
        'totalWriteOperationDelta',
        'totalReadTransferBytesDelta',
        'totalWriteTransferBytesDelta')) {
        [void](Require-Property $resources $name 'Report summary.resources')
    }
    $integrity = Assert-StrictIntegrity (Require-Property $summary 'integrity' 'Report summary') -AllowRecoveredTail:$Profile.IsMaintenanceChaos
    if ($Profile.IsMaintenanceChaos) {
        $chaosPhase = $phases[0]
        $details = Require-Property $chaosPhase 'details' 'maintenance-chaos phase'
        $accountedTail = Require-Integer (Require-Property $details 'unacknowledgedButRecovered' 'maintenance-chaos phase details') 'maintenance-chaos unacknowledgedButRecovered' 0
        if ($accountedTail -ne $integrity.Unexpected) {
            throw 'maintenance-chaos recovered tail is not accounted by unacknowledgedButRecovered.'
        }
        $reservationTotals = Assert-MaintenanceChaosReservations `
            $chaosReservations `
            ([int]$Profile.ExpectedOptions.restartCount) `
            ([int]$Profile.ExpectedOptions.series) `
            ([int]$Profile.ExpectedOptions.pointsPerBatch) `
            ([int]$Profile.ExpectedOptions.maintenanceBatches) `
            $accountedTail `
            'maintenance-chaos reservations'
        Assert-MaintenanceChaosReservationDetails $details $reservationTotals ([int]$Profile.ExpectedOptions.restartCount) 'maintenance-chaos phase details'
        if ([decimal]$integrity.Expected -ne [decimal]$reservationTotals.AcknowledgedCurrentPoints) {
            throw 'maintenance-chaos integrity expectedPoints does not match maintenanceChaosReservations.'
        }
    }
}

$profiles = @(
    [pscustomobject]@{
        Name = 'high-cardinality'
        ExpectedPhases = @('high_cardinality_write', 'high_cardinality_recovery')
        ExpectedScopes = @('runner-invocation', 'manual-embedded')
        IsMaintenanceChaos = $false
        Arguments = @('--measurements', '1', '--points-per-measurement', '1', '--series', '8', '--target-segments', '1', '--points-per-segment', '1', '--restart-count', '1', '--recovery-samples', '2', '--query-samples', '4', '--maintenance-batches', '1', '--points-per-batch', '8', '--drop-measurements', '1', '--random-seed', '125')
        ExpectedOptions = @{ cycles = 1; measurements = 1; pointsPerMeasurement = 1; series = 8; targetSegments = 1; pointsPerSegment = 1; restartCount = 1; recoverySamples = 2; querySamples = 4; maintenanceBatches = 1; pointsPerBatch = 8; dropMeasurements = 1; randomSeed = 125 }
    },
    [pscustomobject]@{
        Name = 'small-segments'
        ExpectedPhases = @('small_segments_write', 'small_segments_recovery_and_integrity')
        ExpectedScopes = @('runner-invocation', 'manual-embedded')
        IsMaintenanceChaos = $false
        Arguments = @('--measurements', '1', '--points-per-measurement', '1', '--series', '4', '--target-segments', '4', '--points-per-segment', '1', '--restart-count', '1', '--recovery-samples', '2', '--query-samples', '4', '--maintenance-batches', '1', '--points-per-batch', '1', '--drop-measurements', '1', '--random-seed', '125')
        ExpectedOptions = @{ cycles = 1; measurements = 1; pointsPerMeasurement = 1; series = 4; targetSegments = 4; pointsPerSegment = 1; restartCount = 1; recoverySamples = 2; querySamples = 4; maintenanceBatches = 1; pointsPerBatch = 1; dropMeasurements = 1; randomSeed = 125 }
    },
    [pscustomobject]@{
        Name = 'maintenance-chaos'
        ExpectedPhases = @('maintenance_chaos_kill_reopen')
        ExpectedScopes = @('runner-invocation', 'maintenance-chaos-worker', 'manual-embedded', 'maintenance-validation')
        IsMaintenanceChaos = $true
        Arguments = @('--measurements', '1', '--points-per-measurement', '1', '--series', '4', '--target-segments', '1', '--points-per-segment', '1', '--restart-count', '3', '--recovery-samples', '3', '--query-samples', '4', '--maintenance-batches', '2', '--points-per-batch', '8', '--drop-measurements', '1', '--random-seed', '125')
        ExpectedOptions = @{ cycles = 1; measurements = 1; pointsPerMeasurement = 1; series = 4; targetSegments = 1; pointsPerSegment = 1; restartCount = 3; recoverySamples = 3; querySamples = 4; maintenanceBatches = 2; pointsPerBatch = 8; dropMeasurements = 1; randomSeed = 125 }
    },
    [pscustomobject]@{
        Name = 'many-measurements'
        ExpectedPhases = @('many_measurements_write', 'many_measurements_reopen', 'many_measurements_backup_scan', 'many_measurements_retention_and_drop')
        ExpectedScopes = @('runner-invocation', 'manual-embedded', 'many-measurements-maintenance')
        IsMaintenanceChaos = $false
        Arguments = @('--measurements', '8', '--points-per-measurement', '2', '--series', '1', '--target-segments', '4', '--points-per-segment', '1', '--restart-count', '1', '--recovery-samples', '2', '--query-samples', '4', '--maintenance-batches', '1', '--points-per-batch', '2', '--drop-measurements', '2', '--random-seed', '125')
        ExpectedOptions = @{ cycles = 1; measurements = 8; pointsPerMeasurement = 2; series = 1; targetSegments = 4; pointsPerSegment = 1; restartCount = 1; recoverySamples = 2; querySamples = 4; maintenanceBatches = 1; pointsPerBatch = 2; dropMeasurements = 2; randomSeed = 125 }
    }
)

try {
    foreach ($name in $targetEnvironmentNames) {
        $originalTargetEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable($name, $null, [EnvironmentVariableTarget]::Process)
    }

    New-Item -ItemType Directory -Path $root | Out-Null
    foreach ($profile in $profiles) {
        $output = Join-Path $root (Join-Path 'output' $profile.Name)
        $work = Join-Path $root (Join-Path 'work' $profile.Name)
        $arguments = @(
            'run', '-c', 'Release', '--project', $projectPath
        )
        if ($NoBuild) { $arguments += '--no-build' }
        $arguments += @(
            '--',
            '--profile', $profile.Name,
            '--cycles', '1',
            '--relational-rows', '1',
            '--cache-entries', '1',
            '--cache-ttl-ms', '1',
            '--multipart-parts', '1',
            '--multipart-part-bytes', '1',
            '--work', $work,
            '--output', $output,
            '--keep-data'
        ) + $profile.Arguments
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "[$($profile.Name)] scaled specialized profile failed with exit code $LASTEXITCODE."
        }

        $reportPath = Join-Path $output 'report.json'
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "[$($profile.Name)] report.json was not written."
        }
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
        Assert-Report $report $profile
        if ($profile.IsMaintenanceChaos) {
            Assert-NoMaintenanceChaosProgressTemporaryFiles $work "[$($profile.Name)]"
        }
    }

    Write-Output 'M19 specialized profile smoke tests passed.'
}
finally {
    foreach ($name in $targetEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalTargetEnvironment[$name], [EnvironmentVariableTarget]::Process)
    }
    if (-not $retainOutput -and (Test-Path -LiteralPath $root)) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
