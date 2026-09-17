[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ReportPath,
    [switch] $AllowUnavailableEnvironment,
    [string] $ExpectedCommitSha,
    [string] $ExpectedTargetHardwareId,
    [string] $ExpectedTargetHardwareContract
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required.'
}

$script:Report = [pscustomobject]@{ profile = 'UNKNOWN' }
$script:EnvironmentUnavailable = $false
$script:RequireAuthoritativeIo = $true
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$forbiddenFileSystems = @(
    'overlay', 'aufs', 'tmpfs', 'ramfs', 'nfs', 'nfs4', 'cifs', 'smb',
    'sshfs', 'fuse.sshfs', 'ceph', 'cephfs', 'glusterfs', 'fuse.glusterfs')

function Fail-NotReady([string] $Reason) {
    [pscustomobject]@{
        status = 'NOT_READY'
        reason = $Reason
        profile = [string]$script:Report.profile
    } | ConvertTo-Json -Compress
    throw $Reason
}

function Require-Property([object] $Object, [string] $Name, [string] $Context) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        Fail-NotReady "$Context is missing '$Name'."
    }

    return $Object.$Name
}

function Require-String([object] $Value, [string] $Context) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        Fail-NotReady "$Context must be a non-empty string."
    }

    return ([string]$Value).Trim()
}

function Require-Boolean([object] $Value, [string] $Context) {
    if ($Value -isnot [bool]) {
        Fail-NotReady "$Context must be Boolean."
    }

    return [bool]$Value
}

function Convert-Integer([object] $Value, [string] $Context) {
    if ($null -eq $Value) {
        Fail-NotReady "$Context is missing."
    }

    [long] $result = 0
    $text = [System.Convert]::ToString($Value, $invariant)
    if ([string]::IsNullOrWhiteSpace($text) -or -not [long]::TryParse(
        $text,
        [System.Globalization.NumberStyles]::AllowLeadingSign,
        $invariant,
        [ref]$result)) {
        Fail-NotReady "$Context must be an integer."
    }

    return $result
}

function Require-Integer([object] $Value, [string] $Context, [long] $Minimum) {
    $result = Convert-Integer $Value $Context
    if ($result -lt $Minimum) {
        Fail-NotReady "$Context must be at least $Minimum."
    }

    return $result
}

function Require-NullableInteger([object] $Value, [string] $Context, [switch] $NonNegative) {
    if ($null -eq $Value) {
        return $null
    }

    $result = Convert-Integer $Value $Context
    if ($NonNegative -and $result -lt 0) {
        Fail-NotReady "$Context must be non-negative when present."
    }

    return $result
}

function Convert-Number([object] $Value, [string] $Context) {
    if ($null -eq $Value) {
        Fail-NotReady "$Context is missing."
    }

    [double] $result = 0
    $text = [System.Convert]::ToString($Value, $invariant)
    $parsed = [double]::TryParse(
        $text,
        [System.Globalization.NumberStyles]::Float,
        $invariant,
        [ref]$result)
    if ([string]::IsNullOrWhiteSpace($text) -or -not $parsed -or [double]::IsNaN($result) -or [double]::IsInfinity($result)) {
        Fail-NotReady "$Context must be finite numeric data."
    }

    return $result
}

function Require-Number([object] $Value, [string] $Context, [double] $Minimum) {
    $result = Convert-Number $Value $Context
    if ($result -lt $Minimum) {
        Fail-NotReady "$Context must be at least $Minimum."
    }

    return $result
}

function Require-NullableNumber([object] $Value, [string] $Context, [switch] $NonNegative) {
    if ($null -eq $Value) {
        return $null
    }

    $result = Convert-Number $Value $Context
    if ($NonNegative -and $result -lt 0) {
        Fail-NotReady "$Context must be non-negative when present."
    }

    return $result
}

function Assert-UsableMountField([object] $Value, [string] $Context) {
    $text = Require-String $Value $Context
    if ($text -in @('UNAVAILABLE', 'UNDECLARED', 'NOT_APPLICABLE', 'unavailable', 'unconfigured')) {
        Fail-NotReady "$Context is unavailable."
    }
    return $text
}

function Assert-MountFileSystem([string] $FileSystem, [string] $Context) {
    if ($forbiddenFileSystems -contains $FileSystem.ToLowerInvariant()) {
        Fail-NotReady "$Context uses a forbidden overlay, temporary, or network filesystem '$FileSystem'."
    }
}

function Require-Array([object] $Object, [string] $Name, [string] $Context) {
    $value = Require-Property $Object $Name $Context
    if ($null -eq $value) {
        # System.Text.Json writes empty IReadOnlyList<T> values as [], which PowerShell
        # materializes as $null after ConvertFrom-Json. Presence was checked above.
        return @()
    }

    return @($value)
}

function Require-DateTimeOffset([object] $Value, [string] $Context) {
    [DateTimeOffset] $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        (Require-String $Value $Context),
        $invariant,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsed)) {
        Fail-NotReady "$Context must be an ISO-8601 timestamp."
    }

    return $parsed
}

function Assert-ApproximatelyEqual([double] $Expected, [double] $Actual, [string] $Context) {
    $tolerance = [Math]::Max(0.000001, [Math]::Abs($Expected) * 0.000001)
    if ([Math]::Abs($Expected - $Actual) -gt $tolerance) {
        Fail-NotReady "$Context does not match its source evidence. expected=$Expected actual=$Actual."
    }
}

function Get-NearestRank([double[]] $Samples, [double] $Percentile) {
    [double[]] $ordered = $Samples.Clone()
    [array]::Sort($ordered)
    return $ordered[[Math]::Max(0, [Math]::Min(
        $ordered.Length - 1,
        [int][Math]::Ceiling($Percentile * $ordered.Length) - 1))]
}

function Assert-LatencySummary([object] $Summary, [double[]] $Samples, [int] $ExpectedCount, [string] $Name) {
    if ($Samples.Length -ne $ExpectedCount) {
        Fail-NotReady "$Name raw sample count must be $ExpectedCount, actual $($Samples.Length)."
    }
    $sampleCount = Require-Integer (Require-Property $Summary 'samples' "$Name latency summary") "$Name latency summary.samples" 1
    if ($sampleCount -ne $Samples.Length) {
        Fail-NotReady "$Name latency summary.samples does not match raw samples. expected=$($Samples.Length) actual=$sampleCount."
    }
    $minimum = Require-Number (Require-Property $Summary 'minimumMilliseconds' "$Name latency summary") "$Name latency minimum" 0
    $p50 = Require-Number (Require-Property $Summary 'p50Milliseconds' "$Name latency summary") "$Name latency P50" 0
    $p95 = Require-Number (Require-Property $Summary 'p95Milliseconds' "$Name latency summary") "$Name latency P95" 0
    $p99 = Require-Number (Require-Property $Summary 'p99Milliseconds' "$Name latency summary") "$Name latency P99" 0
    $maximum = Require-Number (Require-Property $Summary 'maximumMilliseconds' "$Name latency summary") "$Name latency maximum" 0
    if ($minimum -gt $p50 -or $p50 -gt $p95 -or $p95 -gt $p99 -or $p99 -gt $maximum) {
        Fail-NotReady "$Name latency summary is not ordered min <= P50 <= P95 <= P99 <= max."
    }

    [double[]] $ordered = $Samples.Clone()
    [array]::Sort($ordered)
    Assert-ApproximatelyEqual $ordered[0] $minimum "$Name latency minimum"
    Assert-ApproximatelyEqual (Get-NearestRank $ordered 0.50) $p50 "$Name latency P50"
    Assert-ApproximatelyEqual (Get-NearestRank $ordered 0.95) $p95 "$Name latency P95"
    Assert-ApproximatelyEqual (Get-NearestRank $ordered 0.99) $p99 "$Name latency P99"
    Assert-ApproximatelyEqual $ordered[$ordered.Length - 1] $maximum "$Name latency maximum"
}

function Get-WorkloadFingerprint([string] $Profile, [object] $Options) {
    $names = @(
        'profile', 'cycles', 'relationalRows', 'measurements', 'pointsPerMeasurement',
        'cacheEntries', 'cacheTtlMilliseconds', 'multipartParts', 'multipartPartBytes',
        'series', 'targetSegments', 'pointsPerSegment', 'restartCount', 'recoverySamples',
        'querySamples', 'maintenanceBatches', 'pointsPerBatch', 'dropMeasurements', 'randomSeed')
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $names) {
        $canonical = if ($name -eq 'profile') {
            Require-String $Profile 'Report.profile'
        }
        else {
            $value = Require-Property $Options $name 'Report options'
            [string](Convert-Integer $value "Report options.$name")
        }
        $lines.Add("$name=$canonical")
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes([string]::Join("`n", $lines))
    return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-Configuration([object] $Configuration, [string] $Context) {
    foreach ($name in @('scope', 'source', 'durabilityMode')) {
        [void](Require-String (Require-Property $Configuration $name $Context) "$Context.$name")
    }
    foreach ($name in @(
        'syncWalOnEveryWrite', 'flushWalToOsOnWrite', 'segmentFsyncOnCommit',
        'backgroundFlushEnabled', 'compactionEnabled', 'retentionEnabled')) {
        $value = Require-Property $Configuration $name $Context
        if ($null -ne $value) { [void](Require-Boolean $value "$Context.$name") }
    }
    $settings = Require-Property $Configuration 'settings' $Context
    if ($null -eq $settings -or @($settings.PSObject.Properties).Count -eq 0) {
        Fail-NotReady "$Context.settings must contain captured settings."
    }
    foreach ($property in $settings.PSObject.Properties) {
        [void](Require-String $property.Value "$Context.settings.$($property.Name)")
    }
}

function Assert-ConfigurationSet(
    [object[]] $Configurations,
    [string[]] $ExpectedScopes,
    [hashtable] $Contracts,
    [string] $Context) {
    if ($Configurations.Count -ne $ExpectedScopes.Count) {
        Fail-NotReady "$Context has an unexpected configuration count."
    }

    $seen = @{}
    foreach ($configuration in $Configurations) {
        Assert-Configuration $configuration $Context
        $scope = Require-String (Require-Property $configuration 'scope' $Context) "$Context.scope"
        if ($seen.ContainsKey($scope) -or $scope -notin $ExpectedScopes) {
            Fail-NotReady "$Context contains duplicate or unexpected configuration scope '$scope'."
        }
        $seen[$scope] = $configuration
    }
    foreach ($scope in $ExpectedScopes) {
        if (-not $seen.ContainsKey($scope)) {
            Fail-NotReady "$Context is missing required configuration scope '$scope'."
        }
    }

    foreach ($scope in $Contracts.Keys) {
        if (-not $seen.ContainsKey($scope)) {
            continue
        }
        $configuration = $seen[$scope]
        $contract = $Contracts[$scope]
        if ([string]$configuration.source -ne [string]$contract.source -or
            [string]$configuration.durabilityMode -ne [string]$contract.durabilityMode) {
            Fail-NotReady "$Context configuration '$scope' does not match its fixed source or durability mode."
        }
        foreach ($flag in @(
            'syncWalOnEveryWrite', 'flushWalToOsOnWrite', 'segmentFsyncOnCommit',
            'backgroundFlushEnabled', 'compactionEnabled', 'retentionEnabled')) {
            $actual = Require-Property $configuration $flag "$Context configuration '$scope'"
            if ($actual -isnot [bool] -or [bool]$actual -ne [bool]$contract[$flag]) {
                Fail-NotReady "$Context configuration '$scope'.$flag does not match its fixed capacity contract."
            }
        }
    }
}

function Assert-ProcessContribution([object] $Contribution, [string] $Context) {
    [void](Require-String (Require-Property $Contribution 'scope' $Context) "$Context.scope")
    foreach ($name in @(
        'initialWorkingSetBytes', 'finalObservedWorkingSetBytes', 'peakWorkingSetBytes',
        'initialPrivateMemoryBytes', 'finalObservedPrivateMemoryBytes', 'peakPrivateMemoryBytes')) {
        [void](Require-NullableInteger (Require-Property $Contribution $name $Context) "$Context.$name" -NonNegative)
    }
    foreach ($name in @('workingSetDeltaBytes', 'privateMemoryDeltaBytes')) {
        [void](Require-NullableInteger (Require-Property $Contribution $name $Context) "$Context.$name")
    }
    foreach ($name in @('cpuTimeMilliseconds', 'cpuUtilizationPercent')) {
        [void](Require-NullableNumber (Require-Property $Contribution $name $Context) "$Context.$name" -NonNegative)
    }
    foreach ($name in @('readOperationDelta', 'writeOperationDelta', 'readTransferBytesDelta', 'writeTransferBytesDelta')) {
        $value = Require-NullableInteger (Require-Property $Contribution $name $Context) "$Context.$name" -NonNegative
        if ($null -eq $value) {
            if ($script:RequireAuthoritativeIo) {
                Fail-NotReady "$Context.$name must be available for authoritative capacity evidence."
            }
            $script:EnvironmentUnavailable = $true
        }
    }
    [void](Require-Number (Require-Property $Contribution 'elapsedMilliseconds' $Context) "$Context.elapsedMilliseconds" 0)
    [void](Require-Integer (Require-Property $Contribution 'sampleCount' $Context) "$Context.sampleCount" 1)
    [void](Require-Boolean (Require-Property $Contribution 'processExited' $Context) "$Context.processExited")
}

function Assert-Integrity([object] $Integrity, [string] $Context, [switch] $AllowRecoveredTail) {
    [void](Require-String (Require-Property $Integrity 'scope' $Context) "$Context.scope")
    $expected = Require-Integer (Require-Property $Integrity 'expectedPoints' $Context) "$Context.expectedPoints" 1
    $observed = Require-Integer (Require-Property $Integrity 'observedPoints' $Context) "$Context.observedPoints" 0
    foreach ($name in @('missingPoints', 'duplicatePoints', 'valueMismatches')) {
        if ((Require-Integer (Require-Property $Integrity $name $Context) "$Context.$name" 0) -ne 0) {
            Fail-NotReady "$Context.$name must be zero."
        }
    }
    $unexpected = Require-Integer (Require-Property $Integrity 'unexpectedPoints' $Context) "$Context.unexpectedPoints" 0
    if (-not $AllowRecoveredTail -and $unexpected -ne 0) {
        Fail-NotReady "$Context.unexpectedPoints must be zero."
    }
    if (-not (Require-Boolean (Require-Property $Integrity 'digestMatches' $Context) "$Context.digestMatches")) {
        Fail-NotReady "$Context.digestMatches must be true."
    }
    if (-not $AllowRecoveredTail -and $observed -ne $expected) {
        Fail-NotReady "$Context.observedPoints must equal expectedPoints."
    }
    if ($AllowRecoveredTail -and $observed -ne $expected + $unexpected) {
        Fail-NotReady "$Context.observedPoints must equal expectedPoints plus recovered tail points."
    }
    return [pscustomobject]@{ expected = $expected; observed = $observed; unexpected = $unexpected }
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
        Fail-NotReady "$Context must contain exactly $ExpectedRestartCount reservations."
    }
    if ($ExpectedSeries -le 0 -or $ExpectedPointsPerBatch -le 0 -or $ExpectedMinimumBatches -le 0) {
        Fail-NotReady "$Context has invalid batch contract inputs."
    }

    [long] $previousEnd = -1
    [decimal] $reservedCurrentPoints = 0
    [decimal] $acknowledgedCurrentPoints = 0
    [decimal] $reservedButUnacknowledged = 0
    for ($index = 0; $index -lt $Reservations.Count; $index++) {
        $reservation = $Reservations[$index]
        $restart = Require-Integer (Require-Property $reservation 'restart' $Context) "$Context.restart" 1
        if ($restart -ne ($index + 1)) {
            Fail-NotReady "$Context.restart must be sequential from 1 through $ExpectedRestartCount."
        }

        $start = Require-Integer (Require-Property $reservation 'startInclusive' $Context) "$Context.startInclusive" 0
        $end = Require-Integer (Require-Property $reservation 'endInclusive' $Context) "$Context.endInclusive" 0
        $acknowledged = Require-Integer (Require-Property $reservation 'acknowledgedThroughInclusive' $Context) "$Context.acknowledgedThroughInclusive" ([long]::MinValue)
        if ($end -lt $start) {
            Fail-NotReady "$Context must use non-empty inclusive reservation ranges."
        }
        if ($index -eq 0) {
            if ($start -ne 0) {
                Fail-NotReady "$Context must reserve from sequence zero."
            }
        }
        else {
            if ($previousEnd -eq [long]::MaxValue -or $start -ne $previousEnd + 1) {
                Fail-NotReady "$Context reservation ranges must be contiguous and non-overlapping."
            }
        }

        $minimumAcknowledged = $start - 1L
        if ($acknowledged -lt $minimumAcknowledged -or $acknowledged -gt $end) {
            Fail-NotReady "$Context.acknowledgedThroughInclusive must be within startInclusive - 1 through endInclusive."
        }

        $reservedSpan = [decimal]$end - [decimal]$start + 1
        $acknowledgedSpan = [decimal]$acknowledged - [decimal]$start + 1
        if ($reservedSpan % $ExpectedPointsPerBatch -ne 0) {
            Fail-NotReady "$Context reservation span must contain complete pointsPerBatch batches."
        }
        if ($acknowledged -lt $start -or $acknowledgedSpan % $ExpectedPointsPerBatch -ne 0) {
            Fail-NotReady "$Context acknowledged span must contain complete pointsPerBatch batches."
        }
        $minimumAcknowledgedPoints = [decimal][Math]::Max(
            [long]$ExpectedSeries,
            [long]$ExpectedPointsPerBatch * [long]$ExpectedMinimumBatches)
        if ($acknowledgedSpan -lt $minimumAcknowledgedPoints) {
            Fail-NotReady "$Context acknowledged span is shorter than the minimum maintenance batch workload."
        }

        # A valid reservation can span Int64.MaxValue through an unacknowledged
        # synthetic lower bound of -1. Decimal keeps the evidence-side sums exact.
        $reservedCurrentPoints += $reservedSpan
        $acknowledgedCurrentPoints += $acknowledgedSpan
        $reservedButUnacknowledged += [decimal]$end - [decimal]$acknowledged
        $previousEnd = $end
    }

    if ([decimal]$RecoveredTail -gt $reservedButUnacknowledged) {
        Fail-NotReady "$Context recovered tail exceeds the reserved-but-unacknowledged span."
    }

    return [pscustomobject]@{
        ReservedCurrentPoints = $reservedCurrentPoints
        AcknowledgedCurrentPoints = $acknowledgedCurrentPoints
        ReservedButUnacknowledgedPoints = $reservedButUnacknowledged
        AcknowledgedBatches = $acknowledgedCurrentPoints / $ExpectedPointsPerBatch
    }
}

function Assert-MaintenanceChaosWorkerReservationConfiguration(
    [object[]] $Configurations,
    [int] $ExpectedPointsPerBatch,
    [string] $Context) {
    $workers = @($Configurations | Where-Object { [string]$_.scope -eq 'maintenance-chaos-worker' })
    if ($workers.Count -ne 1) {
        Fail-NotReady "$Context must contain exactly one maintenance-chaos-worker configuration."
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
            Fail-NotReady "$Context maintenance-chaos-worker setting '$($pair.Key)' does not match its reservation evidence contract."
        }
    }
}

function Assert-MaintenanceChaosReservationDetails(
    [object] $Details,
    [object] $Totals,
    [int] $ExpectedRestartCount,
    [int] $ExpectedPointsPerBatch,
    [string] $Context) {
    if ($ExpectedPointsPerBatch -le 0 -or [decimal]$Totals.AcknowledgedCurrentPoints % $ExpectedPointsPerBatch -ne 0) {
        Fail-NotReady "$Context acknowledged points are not aligned to pointsPerBatch."
    }
    $expectedValues = [ordered]@{
        restarts = [decimal]$ExpectedRestartCount
        restartReservations = [decimal]$ExpectedRestartCount
        acknowledgedPoints = [decimal]$Totals.AcknowledgedCurrentPoints
        reservedCurrentPoints = [decimal]$Totals.ReservedCurrentPoints
        reservedButUnacknowledgedPoints = [decimal]$Totals.ReservedButUnacknowledgedPoints
        acknowledgedWorkerBatches = [decimal]$Totals.AcknowledgedBatches
        acknowledgedWorkerWrites = [decimal]$Totals.AcknowledgedCurrentPoints + [decimal]$Totals.AcknowledgedBatches
        acknowledgedExpiredPoints = [decimal]$Totals.AcknowledgedBatches
    }
    foreach ($pair in $expectedValues.GetEnumerator()) {
        $actual = Require-Integer (Require-Property $Details $pair.Key $Context) "$Context.$($pair.Key)" 0
        if ([decimal]$actual -ne $pair.Value) {
            Fail-NotReady "$Context.$($pair.Key) does not match maintenanceChaosReservations."
        }
    }

    $writePoint = Require-String (Require-Property $Details 'reservationWritePoint' $Context) "$Context.reservationWritePoint"
    if ($writePoint -cne 'before-WriteMany') {
        Fail-NotReady "$Context.reservationWritePoint must be before-WriteMany."
    }
    $publication = Require-String (Require-Property $Details 'reservationPublication' $Context) "$Context.reservationPublication"
    if ($publication -cne 'content-flush-then-atomic-file-replace') {
        Fail-NotReady "$Context.reservationPublication does not match the fixed reservation publication contract."
    }

    [void](Assert-DetailString $Details 'retentionRunOnce' 'true' $Context)
    [void](Assert-DetailInteger $Details 'expiredPointsVisibleAfterRetention' 0 $Context 0)
    $minimumSegments = Require-Integer (Require-Property $Details 'minimumSegmentsAfterRecovery' $Context) "$Context.minimumSegmentsAfterRecovery" 1
    $maximumSegments = Require-Integer (Require-Property $Details 'maximumSegmentsAfterRecovery' $Context) "$Context.maximumSegmentsAfterRecovery" 1
    if ($minimumSegments -gt $maximumSegments) {
        Fail-NotReady "$Context segment range is inverted."
    }
    [void](Require-Integer (Require-Property $Details 'retentionDroppedSegments' $Context) "$Context.retentionDroppedSegments" 0)
    [void](Require-Integer (Require-Property $Details 'retentionInjectedTombstones' $Context) "$Context.retentionInjectedTombstones" 0)
    [void](Require-Integer (Require-Property $Details 'retentionElapsedMicros' $Context) "$Context.retentionElapsedMicros" 0)
}

function Assert-DetailInteger(
    [object] $Details,
    [string] $Name,
    [long] $Expected,
    [string] $Context,
    [long] $Minimum = 0) {
    $actual = Require-Integer (Require-Property $Details $Name $Context) "$Context.$Name" $Minimum
    if ($actual -ne $Expected) {
        Fail-NotReady "$Context.$Name does not match the fixed phase contract. expected=$Expected actual=$actual."
    }

    return $actual
}

function Assert-DetailString(
    [object] $Details,
    [string] $Name,
    [string] $Expected,
    [string] $Context) {
    $actual = Require-String (Require-Property $Details $Name $Context) "$Context.$Name"
    if ($actual -cne $Expected) {
        Fail-NotReady "$Context.$Name does not match the fixed phase contract. expected=$Expected actual=$actual."
    }

    return $actual
}

function Assert-PhaseOperations([object] $Phase, [long] $Expected, [string] $Context) {
    $actual = Require-Integer (Require-Property $Phase 'operations' $Context) "$Context.operations" 1
    if ($actual -ne $Expected) {
        Fail-NotReady "$Context.operations does not match the fixed phase contract. expected=$Expected actual=$actual."
    }
}

function Assert-IntegrityContract(
    [object] $Integrity,
    [string] $ExpectedScope,
    [long] $ExpectedPoints,
    [string] $Context) {
    if ($null -eq $Integrity) {
        Fail-NotReady "$Context must contain an integrity summary."
    }

    $scope = Require-String (Require-Property $Integrity 'scope' $Context) "$Context.scope"
    if ($scope -cne $ExpectedScope) {
        Fail-NotReady "$Context.scope does not match the fixed phase contract. expected=$ExpectedScope actual=$scope."
    }

    $actualExpected = Require-Integer (Require-Property $Integrity 'expectedPoints' $Context) "$Context.expectedPoints" 1
    if ($actualExpected -ne $ExpectedPoints) {
        Fail-NotReady "$Context.expectedPoints does not match the fixed phase contract. expected=$ExpectedPoints actual=$actualExpected."
    }
}

function Assert-IntegrityScope(
    [object] $Integrity,
    [string] $ExpectedScope,
    [string] $Context) {
    if ($null -eq $Integrity) {
        Fail-NotReady "$Context must contain an integrity summary."
    }

    $scope = Require-String (Require-Property $Integrity 'scope' $Context) "$Context.scope"
    if ($scope -cne $ExpectedScope) {
        Fail-NotReady "$Context.scope does not match the fixed phase contract. expected=$ExpectedScope actual=$scope."
    }
}

function Assert-NoPhaseIntegrity([object] $Integrity, [string] $Context) {
    if ($null -ne $Integrity) {
        Fail-NotReady "$Context must not contain an integrity summary."
    }
}

function Get-CeilingDivide([long] $Dividend, [long] $Divisor, [string] $Context) {
    if ($Dividend -le 0 -or $Divisor -le 0) {
        Fail-NotReady "$Context requires positive division operands."
    }

    return [long][Math]::Ceiling(([decimal]$Dividend) / ([decimal]$Divisor))
}

function Get-ManyMeasurementSampleShape(
    [long] $Measurements,
    [long] $DropMeasurements,
    [long] $QuerySamples) {
    if ($Measurements -lt 2 -or $DropMeasurements -lt 0 -or $QuerySamples -lt 1) {
        Fail-NotReady 'many-measurements sample shape inputs are invalid.'
    }

    $evenCount = Get-CeilingDivide $Measurements 2 'many-measurements even count'
    $oddCount = [long]([Math]::Floor(([decimal]$Measurements) / 2))
    $retentionSampleCount = [long][Math]::Min($Measurements, [Math]::Max(2, $QuerySamples))
    $retentionEvenSamples = [long][Math]::Min($evenCount, (Get-CeilingDivide $retentionSampleCount 2 'retention sample split'))
    $retentionOddSamples = [long][Math]::Min($oddCount, $retentionSampleCount - $retentionEvenSamples)
    $remaining = $retentionSampleCount - $retentionEvenSamples - $retentionOddSamples
    if ($remaining -gt 0) {
        $additionalEven = [long][Math]::Min($remaining, $evenCount - $retentionEvenSamples)
        $retentionEvenSamples += $additionalEven
        $remaining -= $additionalEven
        $retentionOddSamples += $remaining
    }

    $dropCount = [long][Math]::Min($DropMeasurements, [Math]::Floor(([decimal]$Measurements) / 2))
    $oddDropLimit = [long][Math]::Max(0, $oddCount - 1)
    $oddDropCount = [long][Math]::Min($dropCount, $oddDropLimit)
    $evenDropCount = $dropCount - $oddDropCount
    $retainedEvenCount = $evenCount - $evenDropCount
    $retainedOddCount = $oddCount - $oddDropCount
    if ($retainedOddCount -le 0) {
        Fail-NotReady 'many-measurements drop shape must retain an odd measurement.'
    }

    $retainedCount = $retainedEvenCount + $retainedOddCount
    $minimumRetainedSamples = if ($retainedEvenCount -eq 0) { 1L } else { 2L }
    $postDropSampleCount = [long][Math]::Min($retainedCount, [Math]::Max($minimumRetainedSamples, $QuerySamples))
    $postDropOddSamples = [long][Math]::Min($retainedOddCount, [Math]::Max(1, (Get-CeilingDivide $postDropSampleCount 2 'post-drop sample split')))
    $postDropEvenSamples = [long][Math]::Min($retainedEvenCount, $postDropSampleCount - $postDropOddSamples)
    $remaining = $postDropSampleCount - $postDropOddSamples - $postDropEvenSamples
    if ($remaining -gt 0) {
        $additionalOdd = [long][Math]::Min($remaining, $retainedOddCount - $postDropOddSamples)
        $postDropOddSamples += $additionalOdd
        $remaining -= $additionalOdd
        $postDropEvenSamples += $remaining
    }

    return [pscustomobject]@{
        RetentionValidatedMeasurements = $retentionSampleCount
        RetentionExpiredMeasurementSamples = $retentionEvenSamples
        RetentionSurvivingOddMeasurementSamples = $retentionOddSamples
        PostDropValidatedMeasurements = $postDropSampleCount
        PostDropSurvivingOddMeasurementSamples = $postDropOddSamples
        PostDropSurvivingEvenMeasurementSamples = $postDropEvenSamples
        DropCount = $dropCount
        RemainingMeasurements = $Measurements - $dropCount
    }
}

function Assert-ProfilePhaseContract(
    [string] $Profile,
    [string] $Name,
    [object] $Phase,
    [object] $Details,
    [object] $Integrity,
    [object[]] $Reservations,
    [object] $Expected) {
    $values = $Expected.values
    $context = "Report phase '$Name'"

    switch ($Profile) {
        'high-cardinality' {
            $sampleCount = [long][Math]::Min(
                [long]$values.series,
                [long][Math]::Max(1, [Math]::Min([long]$values.querySamples, 1024)))
            if ($Name -eq 'high_cardinality_write') {
                Assert-PhaseOperations $Phase ([long]$values.series) $context
                [void](Assert-DetailInteger $Details 'series' ([long]$values.series) $context 1)
                [void](Assert-DetailInteger $Details 'measurements' 1 $context 1)
                [void](Assert-DetailInteger $Details 'segments' 1 $context 1)
                [void](Assert-DetailInteger $Details 'expectedSegments' 1 $context 1)
                Assert-NoPhaseIntegrity $Integrity $context
            }
            else {
                Assert-PhaseOperations $Phase ([long]$values.series) $context
                [void](Assert-DetailInteger $Details 'catalogSeries' ([long]$values.series) $context 1)
                [void](Assert-DetailInteger $Details 'segments' 1 $context 1)
                [void](Assert-DetailInteger $Details 'validatedSeries' $sampleCount $context 1)
                [void](Assert-DetailInteger $Details 'recoverySamples' ([long]$values.recoverySamples) $context 1)
                [void](Assert-DetailInteger $Details 'querySamples' ([long]$values.querySamples) $context 1)
                [void](Assert-DetailString $Details 'validation' 'deterministic sampled series/time/value' $context)
                Assert-IntegrityContract $Integrity 'deterministic high-cardinality sample' $sampleCount $context
            }
        }
        'small-segments' {
            $totalPoints = [long]$values.targetSegments * [long]$values.pointsPerSegment
            if ($Name -eq 'small_segments_write') {
                Assert-PhaseOperations $Phase $totalPoints $context
                [void](Assert-DetailInteger $Details 'segments' ([long]$values.targetSegments) $context 1)
                [void](Assert-DetailInteger $Details 'pointsPerSegment' ([long]$values.pointsPerSegment) $context 1)
                [void](Require-Integer (Require-Property $Details 'segmentBytes' $context) "$context.segmentBytes" 1)
                Assert-NoPhaseIntegrity $Integrity $context
            }
            else {
                Assert-PhaseOperations $Phase $totalPoints $context
                [void](Assert-DetailInteger $Details 'segments' ([long]$values.targetSegments) $context 1)
                [void](Assert-DetailInteger $Details 'recoverySamples' ([long]$values.recoverySamples) $context 1)
                [void](Assert-DetailInteger $Details 'querySamples' ([long]$values.querySamples) $context 1)
                Assert-IntegrityContract $Integrity 'all persisted points' $totalPoints $context
            }
        }
        'maintenance-chaos' {
            if ($Name -ne 'maintenance_chaos_kill_reopen') {
                Fail-NotReady "Profile '$Profile' has an unexpected phase '$Name'."
            }

            if ($Reservations.Count -ne [int]$values.restartCount) {
                Fail-NotReady "$context must contain exactly $($values.restartCount) reservation records."
            }
            Assert-PhaseOperations $Phase ([long](Require-Integer (Require-Property $Integrity 'expectedPoints' $context) "$context.integrity.expectedPoints" 1)) $context
            [void](Assert-DetailInteger $Details 'randomSeed' ([long]$values.randomSeed) $context 0)
            [void](Assert-DetailInteger $Details 'restarts' ([long]$values.restartCount) $context 1)
            [void](Assert-DetailInteger $Details 'restartReservations' ([long]$values.restartCount) $context 1)
            [void](Assert-DetailInteger $Details 'recoverySamples' ([long]$values.recoverySamples) $context 1)
            [void](Assert-DetailInteger $Details 'querySamples' ([long]$values.querySamples) $context 1)
            [void](Assert-DetailString $Details 'retentionRunOnce' 'true' $context)
            [void](Assert-DetailInteger $Details 'expiredPointsVisibleAfterRetention' 0 $context 0)
            [void](Assert-DetailInteger $Details 'minimumSegmentsAfterRecovery' 1 $context 1)
            $maximumSegments = Require-Integer (Require-Property $Details 'maximumSegmentsAfterRecovery' $context) "$context.maximumSegmentsAfterRecovery" 1
            $minimumSegments = Require-Integer (Require-Property $Details 'minimumSegmentsAfterRecovery' $context) "$context.minimumSegmentsAfterRecovery" 1
            if ($minimumSegments -gt $maximumSegments) { Fail-NotReady "$context segment range is inverted." }
            [void](Require-Integer (Require-Property $Details 'retentionDroppedSegments' $context) "$context.retentionDroppedSegments" 0)
            [void](Require-Integer (Require-Property $Details 'retentionInjectedTombstones' $context) "$context.retentionInjectedTombstones" 0)
            [void](Require-Integer (Require-Property $Details 'retentionElapsedMicros' $context) "$context.retentionElapsedMicros" 0)
            Assert-IntegrityScope $Integrity 'acknowledged maintenance-chaos points' $context
        }
        'many-measurements' {
            $measurementsPerSegment = Get-CeilingDivide ([long]$values.measurements) ([long]$values.targetSegments) 'many-measurements measurementsPerSegment'
            $expectedSegments = Get-CeilingDivide ([long]$values.measurements) $measurementsPerSegment 'many-measurements expectedSegments'
            $totalPoints = [long]$values.measurements * [long]$values.pointsPerMeasurement
            if ($Name -eq 'many_measurements_write') {
                Assert-PhaseOperations $Phase $totalPoints $context
                [void](Assert-DetailInteger $Details 'measurements' ([long]$values.measurements) $context 1)
                [void](Assert-DetailInteger $Details 'segments' $expectedSegments $context 1)
                [void](Assert-DetailInteger $Details 'expectedSegments' $expectedSegments $context 1)
                [void](Assert-DetailInteger $Details 'series' ([long]$values.measurements) $context 1)
                Assert-NoPhaseIntegrity $Integrity $context
            }
            elseif ($Name -eq 'many_measurements_reopen') {
                Assert-PhaseOperations $Phase ([long]$values.measurements) $context
                [void](Assert-DetailInteger $Details 'measurements' ([long]$values.measurements) $context 1)
                [void](Assert-DetailInteger $Details 'segments' $expectedSegments $context 1)
                [void](Assert-DetailInteger $Details 'recoverySamples' ([long]$values.recoverySamples) $context 1)
                [void](Assert-DetailInteger $Details 'querySamples' ([long]$values.querySamples) $context 1)
                Assert-NoPhaseIntegrity $Integrity $context
            }
            elseif ($Name -eq 'many_measurements_backup_scan') {
                $bytes = Require-Integer (Require-Property $Details 'bytes' $context) "$context.bytes" 1
                Assert-PhaseOperations $Phase $bytes $context
                [void](Assert-DetailInteger $Details 'segments' $expectedSegments $context 1)
                [void](Assert-DetailInteger $Details 'expectedSegments' $expectedSegments $context 1)
                $files = Require-Integer (Require-Property $Details 'files' $context) "$context.files" 1
                $checkedFiles = Require-Integer (Require-Property $Details 'backupCheckedFiles' $context) "$context.backupCheckedFiles" 1
                if ($checkedFiles -gt $files) {
                    Fail-NotReady "$context.backupCheckedFiles cannot exceed files."
                }
                [void](Assert-DetailString $Details 'backupVerified' 'true' $context)
                Assert-NoPhaseIntegrity $Integrity $context
            }
            else {
                $shape = Get-ManyMeasurementSampleShape ([long]$values.measurements) ([long]$values.dropMeasurements) ([long]$values.querySamples)
                $expectedIntegrity = [long]$values.pointsPerMeasurement * ($shape.RetentionSurvivingOddMeasurementSamples + ($shape.PostDropValidatedMeasurements * 2))
                $retentionDropped = Require-Integer (Require-Property $Details 'retentionDroppedSegments' $context) "$context.retentionDroppedSegments" 0
                $retentionTombstones = Require-Integer (Require-Property $Details 'retentionInjectedTombstones' $context) "$context.retentionInjectedTombstones" 0
                $dropCount = Assert-DetailInteger $Details 'droppedMeasurements' $shape.DropCount $context 0
                [void](Assert-DetailInteger $Details 'retentionValidatedMeasurements' $shape.RetentionValidatedMeasurements $context 1)
                [void](Assert-DetailInteger $Details 'retentionExpiredMeasurementSamples' $shape.RetentionExpiredMeasurementSamples $context 0)
                [void](Assert-DetailInteger $Details 'retentionSurvivingOddMeasurementSamples' $shape.RetentionSurvivingOddMeasurementSamples $context 1)
                [void](Assert-DetailInteger $Details 'remainingMeasurements' $shape.RemainingMeasurements $context 1)
                [void](Assert-DetailInteger $Details 'postDropValidatedMeasurements' $shape.PostDropValidatedMeasurements $context 1)
                [void](Assert-DetailInteger $Details 'postDropSurvivingOddMeasurementSamples' $shape.PostDropSurvivingOddMeasurementSamples $context 1)
                [void](Require-Integer (Require-Property $Details 'postDropSegments' $context) "$context.postDropSegments" 1)
                [void](Require-Integer (Require-Property $Details 'retentionElapsedMicros' $context) "$context.retentionElapsedMicros" 0)
                [void](Assert-DetailString $Details 'postDropReopen' 'true' $context)
                [void](Assert-UsableMountField (Require-Property $Details 'backupDirectory' $context) "$context.backupDirectory")
                Assert-PhaseOperations $Phase ($retentionDropped + $retentionTombstones + $dropCount) $context
                Assert-IntegrityContract $Integrity 'many-measurements retention, drop, and reopen samples' $expectedIntegrity $context
            }
        }
        default { Fail-NotReady "Profile '$Profile' has no phase contract." }
    }
}

function Get-MaximumOrZero([object[]] $Values) {
    if ($Values.Count -eq 0) { return [long]0 }
    return ($Values | ForEach-Object { if ($null -eq $_) { [long]0 } else { [long]$_ } } | Measure-Object -Maximum).Maximum
}

function Get-SumAllOrNull([object[]] $Items, [string] $PropertyName, [string] $Context) {
    if ($Items.Count -eq 0) {
        return $null
    }

    [long] $total = 0
    foreach ($item in $Items) {
        $value = $item.($PropertyName)
        if ($null -eq $value) {
            return $null
        }

        try {
            $total = [long]($total + [long]$value)
        }
        catch {
            Fail-NotReady "$Context overflowed while aggregating '$PropertyName'."
        }
    }

    return $total
}

function Assert-NullableIntegerMatches([object] $ActualValue, [object] $ExpectedValue, [string] $Context) {
    if (($null -eq $ActualValue) -ne ($null -eq $ExpectedValue) -or
        ($null -ne $ActualValue -and [long]$ActualValue -ne [long]$ExpectedValue)) {
        Fail-NotReady "$Context does not match its source evidence."
    }
}

function Get-DoubleSum([object[]] $Values) {
    [double] $total = 0d
    foreach ($value in $Values) {
        $total += [double]$value
    }

    return $total
}

function Get-LongSum([object[]] $Values) {
    [long] $total = 0
    foreach ($value in $Values) {
        $total = [long]($total + [long]$value)
    }

    return $total
}

function Assert-SummaryResources([object] $Summary, [object[]] $PhaseResources, [int] $ProcessorCount) {
    $resources = Require-Property $Summary 'resources' 'Report summary'
    foreach ($name in @('totalAllocatedBytes', 'totalGen0Collections', 'totalGen1Collections', 'totalGen2Collections', 'peakPrivateMemoryBytes')) {
        [void](Require-Integer (Require-Property $resources $name 'Report summary.resources') "Report summary.resources.$name" 0)
    }
    foreach ($name in @('maximumWorkingSetDeltaBytes', 'maximumPrivateMemoryDeltaBytes')) {
        [void](Require-NullableInteger (Require-Property $resources $name 'Report summary.resources') "Report summary.resources.$name")
    }
    foreach ($name in @('totalReadOperationDelta', 'totalWriteOperationDelta', 'totalReadTransferBytesDelta', 'totalWriteTransferBytesDelta')) {
        [void](Require-NullableInteger (Require-Property $resources $name 'Report summary.resources') "Report summary.resources.$name" -NonNegative)
    }
    $totalCpu = Require-Number (Require-Property $resources 'totalCpuTimeMilliseconds' 'Report summary.resources') 'Report summary.resources.totalCpuTimeMilliseconds' 0
    $totalDuration = Require-Number (Require-Property $resources 'totalMeasuredDurationMilliseconds' 'Report summary.resources') 'Report summary.resources.totalMeasuredDurationMilliseconds' 0
    if ($totalDuration -le 0) { Fail-NotReady 'Report summary.resources.totalMeasuredDurationMilliseconds must be positive.' }
    $averageCpu = Require-NullableNumber (Require-Property $resources 'averageCpuUtilizationPercent' 'Report summary.resources') 'Report summary.resources.averageCpuUtilizationPercent' -NonNegative
    if ($null -eq $averageCpu) { Fail-NotReady 'Report summary.resources.averageCpuUtilizationPercent is required.' }

    foreach ($pair in @{
        totalAllocatedBytes = 'allocatedBytes'; totalGen0Collections = 'gen0Collections';
        totalGen1Collections = 'gen1Collections'; totalGen2Collections = 'gen2Collections'
    }.GetEnumerator()) {
        $sum = Get-LongSum @($PhaseResources | ForEach-Object { [long]$_.($pair.Value) })
        if ([long]$resources.($pair.Key) -ne [long]$sum) {
            Fail-NotReady "Report summary.resources.$($pair.Key) does not match phase data."
        }
    }
    if ([long]$resources.peakPrivateMemoryBytes -ne [long](Get-MaximumOrZero @($PhaseResources | ForEach-Object { $_.peakPrivateMemoryBytes }))) {
        Fail-NotReady 'Report summary.resources.peakPrivateMemoryBytes does not match phase data.'
    }
    foreach ($pair in @{
        maximumWorkingSetDeltaBytes = 'workingSetDeltaBytes'; maximumPrivateMemoryDeltaBytes = 'privateMemoryDeltaBytes'
    }.GetEnumerator()) {
        $values = @($PhaseResources | ForEach-Object { $_.($pair.Value) } | Where-Object { $null -ne $_ })
        $expected = if ($values.Count -eq 0) { $null } else { ($values | Measure-Object -Maximum).Maximum }
        $actual = Require-NullableInteger $resources.($pair.Key) "Report summary.resources.$($pair.Key)"
        if (($null -eq $expected) -ne ($null -eq $actual) -or ($null -ne $expected -and [long]$expected -ne [long]$actual)) {
            Fail-NotReady "Report summary.resources.$($pair.Key) does not match phase data."
        }
    }
    foreach ($pair in @{
        totalReadOperationDelta = 'readOperationDelta'; totalWriteOperationDelta = 'writeOperationDelta';
        totalReadTransferBytesDelta = 'readTransferBytesDelta'; totalWriteTransferBytesDelta = 'writeTransferBytesDelta'
    }.GetEnumerator()) {
        $expected = Get-SumAllOrNull $PhaseResources $pair.Value 'Report summary.resources'
        $actual = Require-NullableInteger (Require-Property $resources $pair.Key 'Report summary.resources') "Report summary.resources.$($pair.Key)" -NonNegative
        Assert-NullableIntegerMatches $actual $expected "Report summary.resources.$($pair.Key)"
    }
    $expectedCpu = Get-DoubleSum @($PhaseResources | ForEach-Object { if ($null -eq $_.cpuTimeMilliseconds) { 0d } else { [double]$_.cpuTimeMilliseconds } })
    $expectedDuration = Get-DoubleSum @($PhaseResources | ForEach-Object { [double]$_.durationMilliseconds })
    Assert-ApproximatelyEqual $expectedCpu $totalCpu 'Report summary.resources.totalCpuTimeMilliseconds'
    Assert-ApproximatelyEqual $expectedDuration $totalDuration 'Report summary.resources.totalMeasuredDurationMilliseconds'
    Assert-ApproximatelyEqual ($totalCpu / ($totalDuration * $ProcessorCount) * 100d) $averageCpu 'Report summary.resources.averageCpuUtilizationPercent'

    $allContributions = @($PhaseResources | ForEach-Object { $_.contributions } | ForEach-Object { $_ })
    $external = Require-Property $Summary 'externalProcessResources' 'Report summary'
    $processCount = Require-Integer (Require-Property $external 'processCount' 'Report summary.externalProcessResources') 'Report summary.externalProcessResources.processCount' 0
    if ($processCount -ne $allContributions.Count) { Fail-NotReady 'Report summary.externalProcessResources.processCount does not match phase contributions.' }
    foreach ($name in @('peakWorkingSetBytes', 'peakPrivateMemoryBytes')) { [void](Require-Integer (Require-Property $external $name 'Report summary.externalProcessResources') "Report summary.externalProcessResources.$name" 0) }
    $externalCpu = Require-Number (Require-Property $external 'totalCpuTimeMilliseconds' 'Report summary.externalProcessResources') 'Report summary.externalProcessResources.totalCpuTimeMilliseconds' 0
    $externalElapsed = Require-Number (Require-Property $external 'totalObservedElapsedMilliseconds' 'Report summary.externalProcessResources') 'Report summary.externalProcessResources.totalObservedElapsedMilliseconds' 0
    $totalSamples = Require-Integer (Require-Property $external 'totalSamples' 'Report summary.externalProcessResources') 'Report summary.externalProcessResources.totalSamples' 0
    $exitedCount = Require-Integer (Require-Property $external 'exitedProcessCount' 'Report summary.externalProcessResources') 'Report summary.externalProcessResources.exitedProcessCount' 0
    foreach ($name in @('totalReadOperationDelta', 'totalWriteOperationDelta', 'totalReadTransferBytesDelta', 'totalWriteTransferBytesDelta')) {
        [void](Require-NullableInteger (Require-Property $external $name 'Report summary.externalProcessResources') "Report summary.externalProcessResources.$name" -NonNegative)
    }
    if ([long]$external.peakWorkingSetBytes -ne [long](Get-MaximumOrZero @($allContributions | ForEach-Object { $_.peakWorkingSetBytes })) -or [long]$external.peakPrivateMemoryBytes -ne [long](Get-MaximumOrZero @($allContributions | ForEach-Object { $_.peakPrivateMemoryBytes }))) {
        Fail-NotReady 'Report summary.externalProcessResources peak values do not match phase contributions.'
    }
    Assert-ApproximatelyEqual (Get-DoubleSum @($allContributions | ForEach-Object { if ($null -eq $_.cpuTimeMilliseconds) { 0d } else { [double]$_.cpuTimeMilliseconds } })) $externalCpu 'Report summary.externalProcessResources.totalCpuTimeMilliseconds'
    Assert-ApproximatelyEqual (Get-DoubleSum @($allContributions | ForEach-Object { [double]$_.elapsedMilliseconds })) $externalElapsed 'Report summary.externalProcessResources.totalObservedElapsedMilliseconds'
    foreach ($pair in @{
        totalReadOperationDelta = 'readOperationDelta'; totalWriteOperationDelta = 'writeOperationDelta';
        totalReadTransferBytesDelta = 'readTransferBytesDelta'; totalWriteTransferBytesDelta = 'writeTransferBytesDelta'
    }.GetEnumerator()) {
        $expected = Get-SumAllOrNull $allContributions $pair.Value 'Report summary.externalProcessResources'
        $actual = Require-NullableInteger (Require-Property $external $pair.Key 'Report summary.externalProcessResources') "Report summary.externalProcessResources.$($pair.Key)" -NonNegative
        Assert-NullableIntegerMatches $actual $expected "Report summary.externalProcessResources.$($pair.Key)"
    }
    if ($totalSamples -ne (Get-LongSum @($allContributions | ForEach-Object { [long]$_.sampleCount })) -or $exitedCount -ne @($allContributions | Where-Object { $_.processExited }).Count) {
        Fail-NotReady 'Report summary.externalProcessResources count fields do not match phase contributions.'
    }
}

try {
    $resolved = (Resolve-Path -LiteralPath $ReportPath -ErrorAction Stop).Path
    $script:Report = Get-Content -LiteralPath $resolved -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
}
catch {
    Fail-NotReady "Report cannot be read as JSON: $($_.Exception.Message)"
}

$expectedByProfile = @{
    'high-cardinality' = @{ values = @{ cycles = 1; relationalRows = 1; measurements = 1; pointsPerMeasurement = 1; cacheEntries = 1; cacheTtlMilliseconds = 1; multipartParts = 1; multipartPartBytes = 1; series = 1000000; targetSegments = 1; pointsPerSegment = 1; restartCount = 1; recoverySamples = 5; querySamples = 100; maintenanceBatches = 1; pointsPerBatch = 4096; dropMeasurements = 1; randomSeed = 125 }; phases = @('high_cardinality_write', 'high_cardinality_recovery'); latency = 'high_cardinality_recovery'; integrity = 'high_cardinality_recovery' }
    'small-segments' = @{ values = @{ cycles = 1; relationalRows = 1; measurements = 1; pointsPerMeasurement = 1; cacheEntries = 1; cacheTtlMilliseconds = 1; multipartParts = 1; multipartPartBytes = 1; series = 32; targetSegments = 10000; pointsPerSegment = 1; restartCount = 1; recoverySamples = 5; querySamples = 100; maintenanceBatches = 1; pointsPerBatch = 1; dropMeasurements = 1; randomSeed = 125 }; phases = @('small_segments_write', 'small_segments_recovery_and_integrity'); latency = 'small_segments_recovery_and_integrity'; integrity = 'small_segments_recovery_and_integrity' }
    'maintenance-chaos' = @{ values = @{ cycles = 1; relationalRows = 1; measurements = 1; pointsPerMeasurement = 1; cacheEntries = 1; cacheTtlMilliseconds = 1; multipartParts = 1; multipartPartBytes = 1; series = 64; targetSegments = 1; pointsPerSegment = 1; restartCount = 20; recoverySamples = 20; querySamples = 100; maintenanceBatches = 4; pointsPerBatch = 64; dropMeasurements = 1; randomSeed = 125 }; phases = @('maintenance_chaos_kill_reopen'); latency = 'maintenance_chaos_kill_reopen'; integrity = 'maintenance_chaos_kill_reopen' }
    'many-measurements' = @{ values = @{ cycles = 1; relationalRows = 1; measurements = 10000; pointsPerMeasurement = 1; cacheEntries = 1; cacheTtlMilliseconds = 1; multipartParts = 1; multipartPartBytes = 1; series = 1; targetSegments = 100; pointsPerSegment = 1; restartCount = 1; recoverySamples = 5; querySamples = 100; maintenanceBatches = 1; pointsPerBatch = 100; dropMeasurements = 100; randomSeed = 125 }; phases = @('many_measurements_write', 'many_measurements_reopen', 'many_measurements_backup_scan', 'many_measurements_retention_and_drop'); latency = 'many_measurements_reopen'; integrity = 'many_measurements_retention_and_drop' }
}

$profile = Require-String (Require-Property $script:Report 'profile' 'Report') 'Report.profile'
if (-not $expectedByProfile.ContainsKey($profile)) { Fail-NotReady "Report profile '$profile' is not an M19 #125 capacity profile." }
$expected = $expectedByProfile[$profile]
if ((Require-Integer (Require-Property $script:Report 'schemaVersion' 'Report') 'Report.schemaVersion' 1) -ne 2) { Fail-NotReady 'Report.schemaVersion must be 2.' }

$options = Require-Property $script:Report 'options' 'Report'
foreach ($name in $expected.values.Keys) {
    $actual = Require-Integer (Require-Property $options $name 'Report options') "Report options.$name" 1
    if ($actual -ne [long]$expected.values[$name]) { Fail-NotReady "Profile '$profile' must use default $name=$($expected.values[$name]), actual $actual." }
}

$evidence = Require-Property $script:Report 'evidence' 'Report'
if ((Require-String (Require-Property $evidence 'schema' 'Report evidence') 'Report evidence.schema') -ne 'sonnetdb.ecosystem-soak.report' -or (Require-String (Require-Property $evidence 'contract' 'Report evidence') 'Report evidence.contract') -ne 'M19-#125-capacity-evidence-v2') {
    Fail-NotReady 'Report evidence schema or contract is invalid.'
}
foreach ($name in @('runId', 'generator', 'generatorVersion', 'sourceRevisionSource', 'workingTreeState', 'executionEnvironment')) { [void](Require-String (Require-Property $evidence $name 'Report evidence') "Report evidence.$name") }
[void](Require-DateTimeOffset (Require-Property $evidence 'generatedUtc' 'Report evidence') 'Report evidence.generatedUtc')
$fingerprint = Require-String (Require-Property $evidence 'workloadFingerprint' 'Report evidence') 'Report evidence.workloadFingerprint'
if ($fingerprint -notmatch '^[0-9a-f]{64}$' -or $fingerprint -ne (Get-WorkloadFingerprint $profile $options)) { Fail-NotReady 'Report evidence.workloadFingerprint does not match report options.' }
if ([string]$evidence.executionEnvironment -ne 'github-actions') {
    Fail-NotReady 'Authoritative M19 capacity evidence must be produced by GitHub Actions.'
}
if ([string]$evidence.sourceRevisionSource -ne 'environment:GITHUB_SHA') {
    Fail-NotReady 'Authoritative M19 capacity evidence must use GITHUB_SHA source revision provenance.'
}
[void](Require-Integer (Require-Property $evidence 'ciRunId' 'Report evidence') 'Report evidence.ciRunId' 1)
[void](Require-Integer (Require-Property $evidence 'ciAttempt' 'Report evidence') 'Report evidence.ciAttempt' 1)
if ([string]$evidence.workingTreeState -ne 'CLEAN') { Fail-NotReady 'Report evidence.workingTreeState must be CLEAN.' }

if (-not (Require-Boolean (Require-Property $script:Report 'succeeded' 'Report') 'Report.succeeded')) { Fail-NotReady 'Runner result is not PASS.' }
$failure = Require-Property $script:Report 'failure' 'Report'
if ($null -ne $failure -and -not [string]::IsNullOrWhiteSpace([string]$failure)) { Fail-NotReady 'Succeeded report must not include failure text.' }
$started = Require-DateTimeOffset (Require-Property $script:Report 'startedUtc' 'Report') 'Report.startedUtc'
$finished = Require-DateTimeOffset (Require-Property $script:Report 'finishedUtc' 'Report') 'Report.finishedUtc'
if ($finished -lt $started) { Fail-NotReady 'Report.finishedUtc precedes Report.startedUtc.' }

$environment = Require-Property $script:Report 'environment' 'Report'
foreach ($name in @('os', 'framework', 'architecture', 'machineName')) { [void](Require-String (Require-Property $environment $name 'Report environment') "Report environment.$name") }
$script:RequireAuthoritativeIo = -not $AllowUnavailableEnvironment
if ([string]$environment.os -notmatch '(?i)linux') {
    if ($script:RequireAuthoritativeIo) {
        Fail-NotReady 'Authoritative M19 capacity evidence requires Linux process I/O accounting.'
    }
    $script:EnvironmentUnavailable = $true
}
if ((Require-String $environment.architecture 'Report environment.architecture') -cne 'X64') {
    Fail-NotReady 'Authoritative M19 capacity evidence requires X64 process architecture.'
}
$processorCount = Require-Integer (Require-Property $environment 'processorCount' 'Report environment') 'Report environment.processorCount' 1
[void](Require-Integer (Require-Property $environment 'availableMemoryBytes' 'Report environment') 'Report environment.availableMemoryBytes' 1)
$commit = Require-String (Require-Property $environment 'commitSha' 'Report environment') 'Report environment.commitSha'
$sourceRevision = Require-String (Require-Property $evidence 'sourceRevision' 'Report evidence') 'Report evidence.sourceRevision'
if ($commit -notmatch '^[0-9a-fA-F]{40}$' -or $sourceRevision -notmatch '^[0-9a-fA-F]{40}$' -or -not [string]::Equals($commit, $sourceRevision, [StringComparison]::OrdinalIgnoreCase)) { Fail-NotReady 'Report source revision must be a matching 40-character commit SHA.' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha)) {
    if ($ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') { Fail-NotReady 'ExpectedCommitSha must be a 40-character SHA-1.' }
    if (-not [string]::Equals($commit, $ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase)) { Fail-NotReady "Report commit '$commit' does not match expected '$ExpectedCommitSha'." }
}

$hardware = Require-Property $environment 'hardware' 'Report environment'
foreach ($name in @('processorModel', 'processorModelSource', 'operatingSystemArchitecture', 'gcLatencyMode')) {
    $value = Require-String (Require-Property $hardware $name 'Report environment.hardware') "Report environment.hardware.$name"
    if ($value -in @('UNAVAILABLE', 'UNDECLARED', 'unavailable', 'unconfigured')) { Fail-NotReady "Report environment.hardware.$name is unavailable." }
}
if ((Require-String $hardware.operatingSystemArchitecture 'Report environment.hardware.operatingSystemArchitecture') -cne 'X64') {
    Fail-NotReady 'Report environment.hardware.operatingSystemArchitecture must be X64.'
}
if (-not (Require-Boolean (Require-Property $hardware 'is64BitOperatingSystem' 'Report environment.hardware') 'Report environment.hardware.is64BitOperatingSystem')) {
    Fail-NotReady 'Report environment.hardware.is64BitOperatingSystem must be true.'
}
[void](Require-Integer (Require-Property $hardware 'logicalProcessorCount' 'Report environment.hardware') 'Report environment.hardware.logicalProcessorCount' 1)
[void](Require-Integer (Require-Property $hardware 'systemPageSizeBytes' 'Report environment.hardware') 'Report environment.hardware.systemPageSizeBytes' 1)
[void](Require-Integer (Require-Property $hardware 'gcTotalAvailableMemoryBytes' 'Report environment.hardware') 'Report environment.hardware.gcTotalAvailableMemoryBytes' 1)
[void](Require-Boolean (Require-Property $hardware 'isServerGarbageCollector' 'Report environment.hardware') 'Report environment.hardware.isServerGarbageCollector')
$provenance = Require-Property $environment 'provenance' 'Report environment'
foreach ($name in @('runtimeIdentifier', 'processExecutable', 'containerState')) { [void](Require-String (Require-Property $provenance $name 'Report environment.provenance') "Report environment.provenance.$name") }
[void](Require-Integer (Require-Property $provenance 'processId' 'Report environment.provenance') 'Report environment.provenance.processId' 1)
$processStarted = Require-Property $provenance 'processStartedUtc' 'Report environment.provenance'
if ($null -ne $processStarted) { [void](Require-DateTimeOffset $processStarted 'Report environment.provenance.processStartedUtc') }
$is64BitProcess = Require-Boolean (Require-Property $provenance 'is64BitProcess' 'Report environment.provenance') 'Report environment.provenance.is64BitProcess'
if (-not $is64BitProcess) { Fail-NotReady 'Report environment.provenance.is64BitProcess must be true.' }
$containerState = Require-String $provenance.containerState 'Report environment.provenance.containerState'
if ($containerState -cne 'none') { Fail-NotReady 'Authoritative M19 capacity evidence must run outside a container.' }

$disk = Require-Property $environment 'disk' 'Report environment'
foreach ($name in @('root', 'fileSystem', 'driveType', 'volumeLabel', 'deviceModel', 'deviceModelSource')) {
    $value = Require-String (Require-Property $disk $name 'Report environment.disk') "Report environment.disk.$name"
    if ($value -in @('UNAVAILABLE', 'UNDECLARED', 'unavailable', 'unconfigured')) {
        if (-not $AllowUnavailableEnvironment) { Fail-NotReady "Report environment.disk.$name is unavailable." }
        $script:EnvironmentUnavailable = $true
    }
}
$totalBytes = Convert-Integer (Require-Property $disk 'totalBytes' 'Report environment.disk') 'Report environment.disk.totalBytes'
$availableBytes = Convert-Integer (Require-Property $disk 'availableBytes' 'Report environment.disk') 'Report environment.disk.availableBytes'
if ($totalBytes -le 0 -or $availableBytes -lt 0) {
    if (-not $AllowUnavailableEnvironment) { Fail-NotReady 'Report disk capacity snapshot is unavailable.' }
    $script:EnvironmentUnavailable = $true
}
$mountPoint = Assert-UsableMountField (Require-Property $disk 'mountPoint' 'Report environment.disk') 'Report environment.disk.mountPoint'
$mountSource = Assert-UsableMountField (Require-Property $disk 'mountSource' 'Report environment.disk') 'Report environment.disk.mountSource'
$mountDeviceId = Assert-UsableMountField (Require-Property $disk 'mountDeviceId' 'Report environment.disk') 'Report environment.disk.mountDeviceId'
$mountResolution = Require-String (Require-Property $disk 'resolutionSource' 'Report environment.disk') 'Report environment.disk.resolutionSource'
if ($mountResolution -cne 'findmnt') { Fail-NotReady 'Report environment.disk.resolutionSource must be findmnt.' }
if ((Require-String $disk.root 'Report environment.disk.root') -cne $mountPoint) { Fail-NotReady 'Report environment.disk.root must equal mountPoint.' }
Assert-MountFileSystem (Require-String $disk.fileSystem 'Report environment.disk.fileSystem') 'Report environment.disk.fileSystem'

$target = Require-Property $script:Report 'targetHardware' 'Report'
if ((Require-String (Require-Property $target 'status' 'Report targetHardware') 'Report targetHardware.status') -ne 'PASS') { Fail-NotReady 'targetHardware.status must be PASS.' }
$targetId = Require-String (Require-Property $target 'id' 'Report targetHardware') 'Report targetHardware.id'
$targetContract = Require-String (Require-Property $target 'contract' 'Report targetHardware') 'Report targetHardware.contract'
if ($targetId -eq 'UNDECLARED' -or (Require-String (Require-Property $target 'declarationSource' 'Report targetHardware') 'Report targetHardware.declarationSource') -ne 'environment') { Fail-NotReady 'targetHardware must contain an explicit environment attestation.' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetHardwareId) -and $targetId -ne $ExpectedTargetHardwareId) { Fail-NotReady "Report target hardware '$targetId' does not match expected '$ExpectedTargetHardwareId'." }
if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetHardwareContract) -and $targetContract -ne $ExpectedTargetHardwareContract) { Fail-NotReady "Report target contract '$targetContract' does not match expected '$ExpectedTargetHardwareContract'." }
if ([string]$disk.deviceModelSource -ne 'environment:SONNETDB_M19_STORAGE_MODEL') {
    Fail-NotReady 'Report environment.disk.deviceModel must originate from SONNETDB_M19_STORAGE_MODEL.'
}

$configurations = @(Require-Array $script:Report 'effectiveConfiguration' 'Report')
$engineConfigurationContracts = @{
    'manual-embedded' = @{
        source = 'SpecializedSoakRunner.OpenManual'; durabilityMode = 'buffered-until-flush-or-dispose'
        syncWalOnEveryWrite = $false; flushWalToOsOnWrite = $false; segmentFsyncOnCommit = $false
        backgroundFlushEnabled = $false; compactionEnabled = $false; retentionEnabled = $false
    }
    'maintenance-chaos-worker' = @{
        source = 'SpecializedSoakRunner.OpenMaintenanceChaosWorker'; durabilityMode = 'fsync-on-every-write'
        syncWalOnEveryWrite = $true; flushWalToOsOnWrite = $true; segmentFsyncOnCommit = $false
        backgroundFlushEnabled = $true; compactionEnabled = $true; retentionEnabled = $true
    }
    'maintenance-validation' = @{
        source = 'SpecializedSoakRunner.OpenMaintenanceValidation'; durabilityMode = 'flush-to-os-on-every-write'
        syncWalOnEveryWrite = $false; flushWalToOsOnWrite = $true; segmentFsyncOnCommit = $false
        backgroundFlushEnabled = $false; compactionEnabled = $false; retentionEnabled = $true
    }
    'many-measurements-maintenance' = @{
        source = 'SpecializedSoakRunner.OpenManyMeasurementsMaintenance'; durabilityMode = 'flush-to-os-on-every-write'
        syncWalOnEveryWrite = $false; flushWalToOsOnWrite = $true; segmentFsyncOnCommit = $false
        backgroundFlushEnabled = $false; compactionEnabled = $false; retentionEnabled = $true
    }
}
$rootConfigurationScopes = switch ($profile) {
    'high-cardinality' { @('runner-invocation', 'manual-embedded') }
    'small-segments' { @('runner-invocation', 'manual-embedded') }
    'maintenance-chaos' { @('runner-invocation', 'maintenance-chaos-worker', 'manual-embedded', 'maintenance-validation') }
    'many-measurements' { @('runner-invocation', 'manual-embedded', 'many-measurements-maintenance') }
    default { Fail-NotReady "Profile '$profile' has no fixed configuration contract." }
}
Assert-ConfigurationSet $configurations $rootConfigurationScopes $engineConfigurationContracts 'Report effectiveConfiguration'
if ($profile -eq 'maintenance-chaos') {
    Assert-MaintenanceChaosWorkerReservationConfiguration $configurations ([int]$expected.values.pointsPerBatch) 'Report effectiveConfiguration'
}
$invocation = @($configurations | Where-Object { [string]$_.scope -eq 'runner-invocation' })
if ($invocation.Count -ne 1 -or [string]$invocation[0].source -ne 'SoakOptions.Parse') { Fail-NotReady 'Report must contain exactly one runner-invocation configuration.' }
foreach ($name in @('profile', 'cycles', 'series', 'measurements', 'pointsPerMeasurement', 'targetSegments', 'pointsPerSegment', 'restartCount', 'recoverySamples', 'querySamples', 'maintenanceBatches', 'pointsPerBatch', 'dropMeasurements', 'randomSeed')) {
    $actual = Require-String (Require-Property $invocation[0].settings $name 'runner-invocation settings') "runner-invocation settings.$name"
    $wanted = if ($name -eq 'profile') { $profile } else { [string]$expected.values[$name] }
    if ($actual -ne $wanted) { Fail-NotReady "runner-invocation setting '$name' does not match the fixed capacity shape." }
}

$cycles = @(Require-Array $script:Report 'cycles' 'Report')
if ($cycles.Count -ne 1) { Fail-NotReady "Profile '$profile' must contain exactly one capacity cycle." }
$cycle = $cycles[0]
if ((Require-Integer (Require-Property $cycle 'cycle' 'Report cycle') 'Report cycle.cycle' 1) -ne 1) { Fail-NotReady 'Capacity cycle must be numbered 1.' }
$cycleStarted = Require-DateTimeOffset (Require-Property $cycle 'startedUtc' 'Report cycle') 'Report cycle.startedUtc'
$cycleFinished = Require-DateTimeOffset (Require-Property $cycle 'finishedUtc' 'Report cycle') 'Report cycle.finishedUtc'
if ($cycleFinished -lt $cycleStarted -or $cycleStarted -lt $started -or $cycleFinished -gt $finished) { Fail-NotReady 'Report cycle timestamps are inconsistent with report timestamps.' }
$phases = @(Require-Array $cycle 'phases' 'Report cycle')
if ($phases.Count -ne $expected.phases.Count) { Fail-NotReady "Profile '$profile' has an unexpected phase count." }
for ($index = 0; $index -lt $expected.phases.Count; $index++) {
    $actualName = Require-String (Require-Property $phases[$index] 'name' "Report phase[$index]") "Report phase[$index].name"
    if ($actualName -cne [string]$expected.phases[$index]) {
        Fail-NotReady "Profile '$profile' phase order does not match the fixed contract. expected=$($expected.phases[$index]) actual=$actualName."
    }
}

$phaseByName = @{}
$phaseResources = [System.Collections.Generic.List[object]]::new()
$recoverySamples = [System.Collections.Generic.List[double]]::new()
$querySamples = [System.Collections.Generic.List[double]]::new()
$phaseIntegrity = $null
$maintenanceChaosReservations = @()
foreach ($phase in $phases) {
    $name = Require-String (Require-Property $phase 'name' 'Report phase') 'Report phase.name'
    if ($phaseByName.ContainsKey($name) -or $expected.phases -notcontains $name) { Fail-NotReady "Report contains duplicate or unexpected phase '$name'." }
    $phaseByName[$name] = $phase
    $duration = Require-Number (Require-Property $phase 'durationMilliseconds' "Report phase '$name'") "Report phase '$name' durationMilliseconds" 0
    if ($duration -le 0) { Fail-NotReady "Report phase '$name' durationMilliseconds must be positive." }
    $operations = Require-Integer (Require-Property $phase 'operations' "Report phase '$name'") "Report phase '$name' operations" 1
    $operationsPerSecond = Require-Number (Require-Property $phase 'operationsPerSecond' "Report phase '$name'") "Report phase '$name' operationsPerSecond" 0
    if ($operationsPerSecond -le 0) { Fail-NotReady "Report phase '$name' operationsPerSecond must be positive." }
    Assert-ApproximatelyEqual ($operations / [Math]::Max($duration / 1000d, 0.000001d)) $operationsPerSecond "Report phase '$name' operationsPerSecond"
    $resources = [pscustomobject]@{
        durationMilliseconds = $duration
        peakManagedMemoryBytes = Require-Integer (Require-Property $phase 'peakManagedMemoryBytes' "Report phase '$name'") "Report phase '$name' peakManagedMemoryBytes" 1
        peakWorkingSetBytes = Require-Integer (Require-Property $phase 'peakWorkingSetBytes' "Report phase '$name'") "Report phase '$name' peakWorkingSetBytes" 1
        allocatedBytes = Require-Integer (Require-Property $phase 'allocatedBytes' "Report phase '$name'") "Report phase '$name' allocatedBytes" 0
        gen0Collections = Require-Integer (Require-Property $phase 'gen0Collections' "Report phase '$name'") "Report phase '$name' gen0Collections" 0
        gen1Collections = Require-Integer (Require-Property $phase 'gen1Collections' "Report phase '$name'") "Report phase '$name' gen1Collections" 0
        gen2Collections = Require-Integer (Require-Property $phase 'gen2Collections' "Report phase '$name'") "Report phase '$name' gen2Collections" 0
        peakPrivateMemoryBytes = Require-NullableInteger (Require-Property $phase 'peakPrivateMemoryBytes' "Report phase '$name'") "Report phase '$name' peakPrivateMemoryBytes" -NonNegative
        workingSetDeltaBytes = Require-NullableInteger (Require-Property $phase 'workingSetDeltaBytes' "Report phase '$name'") "Report phase '$name' workingSetDeltaBytes"
        privateMemoryDeltaBytes = Require-NullableInteger (Require-Property $phase 'privateMemoryDeltaBytes' "Report phase '$name'") "Report phase '$name' privateMemoryDeltaBytes"
        cpuTimeMilliseconds = Require-NullableNumber (Require-Property $phase 'cpuTimeMilliseconds' "Report phase '$name'") "Report phase '$name' cpuTimeMilliseconds" -NonNegative
        readOperationDelta = Require-NullableInteger (Require-Property $phase 'readOperationDelta' "Report phase '$name'") "Report phase '$name' readOperationDelta" -NonNegative
        writeOperationDelta = Require-NullableInteger (Require-Property $phase 'writeOperationDelta' "Report phase '$name'") "Report phase '$name' writeOperationDelta" -NonNegative
        readTransferBytesDelta = Require-NullableInteger (Require-Property $phase 'readTransferBytesDelta' "Report phase '$name'") "Report phase '$name' readTransferBytesDelta" -NonNegative
        writeTransferBytesDelta = Require-NullableInteger (Require-Property $phase 'writeTransferBytesDelta' "Report phase '$name'") "Report phase '$name' writeTransferBytesDelta" -NonNegative
        contributions = @(Require-Array $phase 'processResourceContributions' "Report phase '$name'")
    }
    foreach ($ioName in @('readOperationDelta', 'writeOperationDelta', 'readTransferBytesDelta', 'writeTransferBytesDelta')) {
        if ($null -eq $resources.$ioName) {
            if ($script:RequireAuthoritativeIo) {
                Fail-NotReady "Report phase '$name' $ioName must be available for authoritative capacity evidence."
            }
            $script:EnvironmentUnavailable = $true
        }
    }
    [void](Require-Integer (Require-Property $phase 'managedMemoryBytes' "Report phase '$name'") "Report phase '$name' managedMemoryBytes" 0)
    [void](Require-Integer (Require-Property $phase 'managedMemoryDeltaBytes' "Report phase '$name'") "Report phase '$name' managedMemoryDeltaBytes" ([long]::MinValue))
    [void](Require-NullableNumber (Require-Property $phase 'cpuUtilizationPercent' "Report phase '$name'") "Report phase '$name' cpuUtilizationPercent" -NonNegative)
    $details = Require-Property $phase 'details' "Report phase '$name'"
    if ($null -eq $details -or @($details.PSObject.Properties).Count -eq 0) { Fail-NotReady "Report phase '$name' must contain phase details." }
    $phaseConfigurations = @(Require-Array $phase 'effectiveConfiguration' "Report phase '$name'")
    $phaseConfigurationScopes = switch ($profile) {
        'high-cardinality' { @('manual-embedded') }
        'small-segments' { @('manual-embedded') }
        'maintenance-chaos' { @('maintenance-chaos-worker', 'manual-embedded', 'maintenance-validation') }
        'many-measurements' {
            if ($name -eq 'many_measurements_retention_and_drop') {
                @('many-measurements-maintenance', 'manual-embedded')
            }
            else {
                @('manual-embedded')
            }
        }
        default { Fail-NotReady "Profile '$profile' has no fixed phase configuration contract." }
    }
    Assert-ConfigurationSet $phaseConfigurations $phaseConfigurationScopes $engineConfigurationContracts "Report phase '$name' effectiveConfiguration"
    if ($profile -eq 'maintenance-chaos' -and $name -eq $expected.integrity) {
        Assert-MaintenanceChaosWorkerReservationConfiguration $phaseConfigurations ([int]$expected.values.pointsPerBatch) "Report phase '$name' effectiveConfiguration"
    }
    foreach ($contribution in $resources.contributions) { Assert-ProcessContribution $contribution "Report phase '$name' processResourceContributions" }
    $phaseResources.Add($resources)

    $rawRecovery = @(Require-Array $phase 'recoveryLatencySamplesMilliseconds' "Report phase '$name'")
    $rawQuery = @(Require-Array $phase 'queryLatencySamplesMilliseconds' "Report phase '$name'")
    if ($name -eq $expected.latency) {
        foreach ($sample in $rawRecovery) { $recoverySamples.Add((Require-Number $sample "Report phase '$name' recovery sample" 0)) }
        foreach ($sample in $rawQuery) { $querySamples.Add((Require-Number $sample "Report phase '$name' query sample" 0)) }
    }
    elseif ($rawRecovery.Count -ne 0 -or $rawQuery.Count -ne 0) { Fail-NotReady "Only '$($expected.latency)' may contain raw latency samples." }
    $integrity = Require-Property $phase 'integrity' "Report phase '$name'"
    if ($name -eq $expected.integrity) {
        $phaseIntegrity = Assert-Integrity $integrity "Report phase '$name' integrity" -AllowRecoveredTail:($profile -eq 'maintenance-chaos')
    }
    elseif ($null -ne $integrity) { Fail-NotReady "Only '$($expected.integrity)' may contain an integrity summary." }

    $reservations = @(Require-Array $phase 'maintenanceChaosReservations' "Report phase '$name'")
    if ($profile -eq 'maintenance-chaos' -and $name -eq $expected.integrity) {
        $maintenanceChaosReservations = $reservations
    }
    elseif ($reservations.Count -ne 0) {
        Fail-NotReady "maintenanceChaosReservations must be empty outside the maintenance-chaos integrity phase."
    }
    Assert-ProfilePhaseContract $profile $name $phase $details $integrity @($reservations) $expected
}
foreach ($name in $expected.phases) { if (-not $phaseByName.ContainsKey($name)) { Fail-NotReady "Report is missing required phase '$name'." } }
if ($null -eq $phaseIntegrity) { Fail-NotReady 'Report does not include the required profile integrity summary.' }
if ($profile -eq 'maintenance-chaos') {
    $chaos = $phaseByName[$expected.integrity]
    if (@($chaos.processResourceContributions).Count -ne [int]$expected.values.restartCount -or @($chaos.processResourceContributions | Where-Object { -not $_.processExited }).Count -ne 0) { Fail-NotReady 'maintenance-chaos must record one exited worker resource contribution for every restart.' }
    $chaosDetails = Require-Property $chaos 'details' 'maintenance-chaos phase'
    $unacknowledged = Require-Integer (Require-Property $chaosDetails 'unacknowledgedButRecovered' 'maintenance-chaos details') 'maintenance-chaos details.unacknowledgedButRecovered' 0
    if ($unacknowledged -ne $phaseIntegrity.unexpected) { Fail-NotReady 'maintenance-chaos recovered tail does not match unacknowledgedButRecovered.' }
    $reservationTotals = Assert-MaintenanceChaosReservations $maintenanceChaosReservations ([int]$expected.values.restartCount) ([int]$expected.values.series) ([int]$expected.values.pointsPerBatch) ([int]$expected.values.maintenanceBatches) $unacknowledged 'maintenance-chaos reservations'
    Assert-MaintenanceChaosReservationDetails $chaosDetails $reservationTotals ([int]$expected.values.restartCount) ([int]$expected.values.pointsPerBatch) 'maintenance-chaos details'
    if ([decimal]$phaseIntegrity.expected -ne [decimal]$reservationTotals.AcknowledgedCurrentPoints) {
        Fail-NotReady 'maintenance-chaos integrity expectedPoints does not match maintenanceChaosReservations.'
    }
}

$summary = Require-Property $script:Report 'summary' 'Report'
$peakWorkingSet = Require-Integer (Require-Property $summary 'peakWorkingSetBytes' 'Report summary') 'Report summary.peakWorkingSetBytes' 1
$peakManaged = Require-Integer (Require-Property $summary 'peakManagedMemoryBytes' 'Report summary') 'Report summary.peakManagedMemoryBytes' 1
if ($peakWorkingSet -ne ($phaseResources | Measure-Object -Property peakWorkingSetBytes -Maximum).Maximum -or $peakManaged -ne ($phaseResources | Measure-Object -Property peakManagedMemoryBytes -Maximum).Maximum) { Fail-NotReady 'Report summary peak memory does not match phase data.' }
$summaryIntegrity = Assert-Integrity (Require-Property $summary 'integrity' 'Report summary') 'Report summary integrity' -AllowRecoveredTail:($profile -eq 'maintenance-chaos')
if ($summaryIntegrity.expected -ne $phaseIntegrity.expected -or $summaryIntegrity.observed -ne $phaseIntegrity.observed -or $summaryIntegrity.unexpected -ne $phaseIntegrity.unexpected) { Fail-NotReady 'Report summary integrity does not match the integrity phase.' }
Assert-LatencySummary (Require-Property $summary 'recoveryLatency' 'Report summary') $recoverySamples.ToArray() ([int]$expected.values.recoverySamples) 'recovery'
Assert-LatencySummary (Require-Property $summary 'queryLatency' 'Report summary') $querySamples.ToArray() ([int]$expected.values.querySamples) 'query'
$boundary = Require-Property $summary 'capacityBoundary' 'Report summary'
if (@(Require-Array $boundary 'validates' 'Report capacityBoundary').Count -eq 0 -or @(Require-Array $boundary 'doesNotProve' 'Report capacityBoundary').Count -eq 0) { Fail-NotReady 'Report capacity boundary is incomplete.' }
Assert-SummaryResources $summary $phaseResources.ToArray() ([int]$processorCount)

[pscustomobject]@{
    status = if ($script:EnvironmentUnavailable) { 'VALID_SCHEMA_NOT_FIXED_HARDWARE' } else { 'PASS' }
    profile = $profile
    commitSha = $commit
    machine = $environment.machineName
    architecture = $environment.architecture
    diskRoot = $disk.root
    targetHardwareId = $targetId
    targetHardwareContract = $targetContract
    peakWorkingSetBytes = $peakWorkingSet
    peakManagedMemoryBytes = $peakManaged
    recoveryP95Milliseconds = $summary.recoveryLatency.p95Milliseconds
    queryP95Milliseconds = $summary.queryLatency.p95Milliseconds
} | ConvertTo-Json -Compress
