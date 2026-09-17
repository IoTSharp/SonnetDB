Set-StrictMode -Version Latest

function Get-M19FixtureProfileDefaults([string] $Profile) {
    $common = [ordered]@{
        cycles = 1; relationalRows = 1; measurements = 1; pointsPerMeasurement = 1
        cacheEntries = 1; cacheTtlMilliseconds = 1; multipartParts = 1; multipartPartBytes = 1
        series = 1; targetSegments = 1; pointsPerSegment = 1; restartCount = 1
        recoverySamples = 5; querySamples = 100; maintenanceBatches = 1; pointsPerBatch = 1
        dropMeasurements = 1; randomSeed = 125
    }
    switch ($Profile) {
        'high-cardinality' {
            $common.series = 1000000; $common.pointsPerBatch = 4096
            return [pscustomobject]@{ values = [pscustomobject]$common; phases = @('high_cardinality_write', 'high_cardinality_recovery'); latency = 'high_cardinality_recovery'; integrity = 'high_cardinality_recovery' }
        }
        'small-segments' {
            $common.series = 32; $common.targetSegments = 10000
            return [pscustomobject]@{ values = [pscustomobject]$common; phases = @('small_segments_write', 'small_segments_recovery_and_integrity'); latency = 'small_segments_recovery_and_integrity'; integrity = 'small_segments_recovery_and_integrity' }
        }
        'maintenance-chaos' {
            $common.series = 64; $common.restartCount = 20; $common.recoverySamples = 20; $common.maintenanceBatches = 4; $common.pointsPerBatch = 64
            return [pscustomobject]@{ values = [pscustomobject]$common; phases = @('maintenance_chaos_kill_reopen'); latency = 'maintenance_chaos_kill_reopen'; integrity = 'maintenance_chaos_kill_reopen' }
        }
        'many-measurements' {
            $common.measurements = 10000; $common.targetSegments = 100; $common.pointsPerBatch = 100; $common.dropMeasurements = 100
            return [pscustomobject]@{ values = [pscustomobject]$common; phases = @('many_measurements_write', 'many_measurements_reopen', 'many_measurements_backup_scan', 'many_measurements_retention_and_drop'); latency = 'many_measurements_reopen'; integrity = 'many_measurements_retention_and_drop' }
        }
        default { throw "Unknown M19 fixture profile '$Profile'." }
    }
}

function Get-M19FixtureFingerprint([string] $Profile, [object] $Options) {
    $names = @(
        'profile', 'cycles', 'relationalRows', 'measurements', 'pointsPerMeasurement',
        'cacheEntries', 'cacheTtlMilliseconds', 'multipartParts', 'multipartPartBytes',
        'series', 'targetSegments', 'pointsPerSegment', 'restartCount', 'recoverySamples',
        'querySamples', 'maintenanceBatches', 'pointsPerBatch', 'dropMeasurements', 'randomSeed')
    $lines = foreach ($name in $names) {
        if ($name -eq 'profile') { "profile=$Profile" } else { "$name=$($Options.$name)" }
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes([string]::Join("`n", @($lines)))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function New-M19FixtureEngineConfiguration([string] $Scope, [int] $PointsPerBatch = 1) {
    $contracts = @{
        'manual-embedded' = @{ source = 'SpecializedSoakRunner.OpenManual'; durabilityMode = 'buffered-until-flush-or-dispose'; sync = $false; flush = $false; fsync = $false; background = $false; compaction = $false; retention = $false }
        'maintenance-chaos-worker' = @{ source = 'SpecializedSoakRunner.OpenMaintenanceChaosWorker'; durabilityMode = 'fsync-on-every-write'; sync = $true; flush = $true; fsync = $false; background = $true; compaction = $true; retention = $true }
        'maintenance-validation' = @{ source = 'SpecializedSoakRunner.OpenMaintenanceValidation'; durabilityMode = 'flush-to-os-on-every-write'; sync = $false; flush = $true; fsync = $false; background = $false; compaction = $false; retention = $true }
        'many-measurements-maintenance' = @{ source = 'SpecializedSoakRunner.OpenManyMeasurementsMaintenance'; durabilityMode = 'flush-to-os-on-every-write'; sync = $false; flush = $true; fsync = $false; background = $false; compaction = $false; retention = $true }
    }
    $contract = $contracts[$Scope]
    if ($null -eq $contract) { throw "Unknown M19 fixture configuration '$Scope'." }
    $settings = [ordered]@{ fixture = 'true' }
    if ($Scope -eq 'maintenance-chaos-worker') {
        $settings.progressWritePoint = 'after-WriteMany-returns'
        $settings.reservationWritePoint = 'before-WriteMany'
        $settings.reservationRecord = 'batch-start-and-end-inclusive'
        $settings.reservationContentFlush = 'FileStream.Flush(true)'
        $settings.reservationReplace = 'File.Move-overwrite-same-directory'
        $settings.restartReservationEvidence = 'worker-start-through-last-reserved-end'
        $settings.expiredPointsPerBatch = '1'
        $settings.currentPointsPerBatch = [string]$PointsPerBatch
    }
    return [pscustomobject][ordered]@{
        scope = $Scope; source = $contract.source; durabilityMode = $contract.durabilityMode
        syncWalOnEveryWrite = $contract.sync; flushWalToOsOnWrite = $contract.flush; segmentFsyncOnCommit = $contract.fsync
        backgroundFlushEnabled = $contract.background; compactionEnabled = $contract.compaction; retentionEnabled = $contract.retention
        settings = [pscustomobject]$settings
    }
}

function New-M19FixtureInvocation([string] $Profile, [object] $Values) {
    $settings = [ordered]@{}
    foreach ($name in @('profile', 'cycles', 'series', 'measurements', 'pointsPerMeasurement', 'targetSegments', 'pointsPerSegment', 'restartCount', 'recoverySamples', 'querySamples', 'maintenanceBatches', 'pointsPerBatch', 'dropMeasurements', 'randomSeed')) {
        $settings[$name] = if ($name -eq 'profile') { $Profile } else { [string]$Values.$name }
    }
    return [pscustomobject][ordered]@{
        scope = 'runner-invocation'; source = 'SoakOptions.Parse'; durabilityMode = 'not-an-engine-durability-assertion'
        syncWalOnEveryWrite = $null; flushWalToOsOnWrite = $null; segmentFsyncOnCommit = $null
        backgroundFlushEnabled = $null; compactionEnabled = $null; retentionEnabled = $null
        settings = [pscustomobject]$settings
    }
}

function Get-M19FixtureRootConfigurationScopes([string] $Profile) {
    switch ($Profile) {
        'high-cardinality' { return @('runner-invocation', 'manual-embedded') }
        'small-segments' { return @('runner-invocation', 'manual-embedded') }
        'maintenance-chaos' { return @('runner-invocation', 'maintenance-chaos-worker', 'manual-embedded', 'maintenance-validation') }
        'many-measurements' { return @('runner-invocation', 'manual-embedded', 'many-measurements-maintenance') }
        default { throw "Unknown M19 fixture profile '$Profile'." }
    }
}

function Get-M19FixturePhaseConfigurationScopes([string] $Profile, [string] $Phase) {
    if ($Profile -eq 'maintenance-chaos') { return @('maintenance-chaos-worker', 'manual-embedded', 'maintenance-validation') }
    if ($Profile -eq 'many-measurements' -and $Phase -eq 'many_measurements_retention_and_drop') { return @('many-measurements-maintenance', 'manual-embedded') }
    return @('manual-embedded')
}

function New-M19FixtureLatencySummary([double[]] $Samples) {
    [double[]] $ordered = $Samples.Clone()
    [array]::Sort($ordered)
    $rank = {
        param([double] $Percentile)
        return $ordered[[Math]::Ceiling($Percentile * $ordered.Length) - 1]
    }
    return [pscustomobject][ordered]@{
        samples = $ordered.Length; minimumMilliseconds = $ordered[0]
        p50Milliseconds = & $rank 0.50; p95Milliseconds = & $rank 0.95; p99Milliseconds = & $rank 0.99
        maximumMilliseconds = $ordered[$ordered.Length - 1]
    }
}

function New-M19FixtureIntegrity([string] $Scope, [long] $Unexpected = 0, [long] $Expected = 1) {
    return [pscustomobject][ordered]@{
        scope = $Scope; expectedPoints = $Expected; observedPoints = $Expected + $Unexpected
        missingPoints = 0; duplicatePoints = 0; unexpectedPoints = $Unexpected; valueMismatches = 0; digestMatches = $true
    }
}

function New-M19FixtureMaintenanceChaosReservations([int] $RestartCount, [int] $PointsPerBatch, [int] $MinimumBatches) {
    if ($RestartCount -le 0 -or $PointsPerBatch -le 0 -or $MinimumBatches -le 0) {
        throw 'Maintenance-chaos fixture reservation dimensions must be positive.'
    }
    $reservations = [System.Collections.Generic.List[object]]::new()
    [long] $sequence = 0
    for ($restart = 1; $restart -le $RestartCount; $restart++) {
        [long] $start = $sequence
        [long] $end = $start + ([long]$PointsPerBatch * $MinimumBatches) - 1
        $reservations.Add([pscustomobject][ordered]@{
            restart = $restart
            startInclusive = $start
            endInclusive = $end
            acknowledgedThroughInclusive = $end
        })
        $sequence = $end + 1
    }

    return $reservations.ToArray()
}

function New-M19FixtureContribution {
    return [pscustomobject][ordered]@{
        scope = 'maintenance-chaos-worker'; initialWorkingSetBytes = 1; finalObservedWorkingSetBytes = 1; peakWorkingSetBytes = 1; workingSetDeltaBytes = 0
        initialPrivateMemoryBytes = 1; finalObservedPrivateMemoryBytes = 1; peakPrivateMemoryBytes = 1; privateMemoryDeltaBytes = 0
        cpuTimeMilliseconds = 1.0; elapsedMilliseconds = 1.0; cpuUtilizationPercent = 100.0
        readOperationDelta = 1; writeOperationDelta = 2; readTransferBytesDelta = 3; writeTransferBytesDelta = 4
        sampleCount = 1; processExited = $true
    }
}

function New-M19FixtureReport {
    param(
        [Parameter(Mandatory = $true)][string] $Profile,
        [string] $CommitSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        [string] $TargetHardwareId = 'fixture-target',
        [string] $TargetHardwareContract = 'M19-#125-frozen-target-v1',
        [string] $StorageModel = 'fixture-storage'
    )

    $definition = Get-M19FixtureProfileDefaults $Profile
    $values = $definition.values
    $options = [pscustomobject][ordered]@{
        cycles = $values.cycles; relationalRows = $values.relationalRows; measurements = $values.measurements; pointsPerMeasurement = $values.pointsPerMeasurement
        cacheEntries = $values.cacheEntries; cacheTtlMilliseconds = $values.cacheTtlMilliseconds; multipartParts = $values.multipartParts; multipartPartBytes = $values.multipartPartBytes
        series = $values.series; targetSegments = $values.targetSegments; pointsPerSegment = $values.pointsPerSegment; restartCount = $values.restartCount
        recoverySamples = $values.recoverySamples; querySamples = $values.querySamples; maintenanceBatches = $values.maintenanceBatches; pointsPerBatch = $values.pointsPerBatch
        dropMeasurements = $values.dropMeasurements; randomSeed = $values.randomSeed
    }
    $rootConfigurations = [System.Collections.Generic.List[object]]::new()
    $rootConfigurations.Add((New-M19FixtureInvocation $Profile $values))
    foreach ($scope in (Get-M19FixtureRootConfigurationScopes $Profile | Where-Object { $_ -ne 'runner-invocation' })) {
        $rootConfigurations.Add((New-M19FixtureEngineConfiguration $scope ([int]$values.pointsPerBatch)))
    }
    [double[]] $recoverySamples = @(1..[int]$values.recoverySamples | ForEach-Object { [double]$_ })
    [double[]] $querySamples = @(1..[int]$values.querySamples | ForEach-Object { [double]$_ })
    [long] $highSampleCount = [Math]::Min(
        [long]$values.series,
        [long][Math]::Max(1, [Math]::Min([long]$values.querySamples, 1024)))
    [long] $smallTotalPoints = [long]$values.targetSegments * [long]$values.pointsPerSegment
    [long] $maintenanceAcknowledgedBatches = [long]$values.restartCount * [long]$values.maintenanceBatches
    [long] $maintenanceAcknowledgedPoints = $maintenanceAcknowledgedBatches * [long]$values.pointsPerBatch
    [long] $manyMeasurementsPerSegment = [long][Math]::Ceiling(([decimal]$values.measurements) / [decimal]$values.targetSegments)
    [long] $manyExpectedSegments = [long][Math]::Ceiling(([decimal]$values.measurements) / [decimal]$manyMeasurementsPerSegment)
    [long] $manyIntegrityExpected = 250L
    [long] $integrityExpected = switch ($Profile) {
        'high-cardinality' { $highSampleCount }
        'small-segments' { $smallTotalPoints }
        'maintenance-chaos' { $maintenanceAcknowledgedPoints }
        'many-measurements' { $manyIntegrityExpected }
    }
    $phases = [System.Collections.Generic.List[object]]::new()
    foreach ($phaseName in $definition.phases) {
        $isLatency = $phaseName -eq $definition.latency
        $isIntegrity = $phaseName -eq $definition.integrity
        $phaseConfigurations = [System.Collections.Generic.List[object]]::new()
        foreach ($scope in (Get-M19FixturePhaseConfigurationScopes $Profile $phaseName)) {
            $phaseConfigurations.Add((New-M19FixtureEngineConfiguration $scope ([int]$values.pointsPerBatch)))
        }
        $contributions = [System.Collections.Generic.List[object]]::new()
        if ($Profile -eq 'maintenance-chaos') {
            foreach ($index in 1..[int]$values.restartCount) { $contributions.Add((New-M19FixtureContribution)) }
        }
        [long] $operations = 1
        $details = [ordered]@{ phase = 'fixture' }
        $phaseIntegrity = $null
        switch ($Profile) {
            'high-cardinality' {
                $operations = [long]$values.series
                if ($phaseName -eq 'high_cardinality_write') {
                    $details.series = [string]$values.series
                    $details.measurements = '1'
                    $details.segments = '1'
                    $details.expectedSegments = '1'
                }
                else {
                    $details.catalogSeries = [string]$values.series
                    $details.segments = '1'
                    $details.validatedSeries = [string]$highSampleCount
                    $details.recoverySamples = [string]$values.recoverySamples
                    $details.querySamples = [string]$values.querySamples
                    $details.validation = 'deterministic sampled series/time/value'
                    $phaseIntegrity = New-M19FixtureIntegrity 'deterministic high-cardinality sample' 0 $highSampleCount
                }
            }
            'small-segments' {
                $operations = $smallTotalPoints
                if ($phaseName -eq 'small_segments_write') {
                    $details.segments = [string]$values.targetSegments
                    $details.pointsPerSegment = [string]$values.pointsPerSegment
                    $details.segmentBytes = '12345'
                }
                else {
                    $details.segments = [string]$values.targetSegments
                    $details.recoverySamples = [string]$values.recoverySamples
                    $details.querySamples = [string]$values.querySamples
                    $phaseIntegrity = New-M19FixtureIntegrity 'all persisted points' 0 $smallTotalPoints
                }
            }
            'maintenance-chaos' {
                $operations = $maintenanceAcknowledgedPoints
                $details.randomSeed = [string]$values.randomSeed
                $details.restarts = [string]$values.restartCount
                $details.restartReservations = [string]$values.restartCount
                $details.recoverySamples = [string]$values.recoverySamples
                $details.querySamples = [string]$values.querySamples
                $details.acknowledgedPoints = [string]$maintenanceAcknowledgedPoints
                $details.acknowledgedWorkerBatches = [string]$maintenanceAcknowledgedBatches
                $details.acknowledgedWorkerWrites = [string]($maintenanceAcknowledgedPoints + $maintenanceAcknowledgedBatches)
                $details.acknowledgedExpiredPoints = [string]$maintenanceAcknowledgedBatches
                $details.reservedCurrentPoints = [string]$maintenanceAcknowledgedPoints
                $details.reservedButUnacknowledgedPoints = '0'
                $details.unacknowledgedButRecovered = '0'
                $details.reservationWritePoint = 'before-WriteMany'
                $details.reservationPublication = 'content-flush-then-atomic-file-replace'
                $details.minimumSegmentsAfterRecovery = '1'
                $details.maximumSegmentsAfterRecovery = '1'
                $details.retentionRunOnce = 'true'
                $details.retentionDroppedSegments = '1'
                $details.retentionInjectedTombstones = '1'
                $details.retentionElapsedMicros = '1'
                $details.expiredPointsVisibleAfterRetention = '0'
                $phaseIntegrity = New-M19FixtureIntegrity 'acknowledged maintenance-chaos points' 0 $maintenanceAcknowledgedPoints
            }
            'many-measurements' {
                if ($phaseName -eq 'many_measurements_write') {
                    $operations = [long]$values.measurements * [long]$values.pointsPerMeasurement
                    $details.measurements = [string]$values.measurements
                    $details.segments = [string]$manyExpectedSegments
                    $details.expectedSegments = [string]$manyExpectedSegments
                    $details.series = [string]$values.measurements
                }
                elseif ($phaseName -eq 'many_measurements_reopen') {
                    $operations = [long]$values.measurements
                    $details.measurements = [string]$values.measurements
                    $details.segments = [string]$manyExpectedSegments
                    $details.recoverySamples = [string]$values.recoverySamples
                    $details.querySamples = [string]$values.querySamples
                }
                elseif ($phaseName -eq 'many_measurements_backup_scan') {
                    $operations = 1000L
                    $details.files = '2'
                    $details.bytes = '1000'
                    $details.segments = [string]$manyExpectedSegments
                    $details.expectedSegments = [string]$manyExpectedSegments
                    $details.backupVerified = 'true'
                    $details.backupCheckedFiles = '2'
                }
                else {
                    $operations = 102L
                    $details.retentionDroppedSegments = '1'
                    $details.retentionInjectedTombstones = '1'
                    $details.retentionElapsedMicros = '1'
                    $details.retentionValidatedMeasurements = '100'
                    $details.retentionExpiredMeasurementSamples = '50'
                    $details.retentionSurvivingOddMeasurementSamples = '50'
                    $details.droppedMeasurements = [string]$values.dropMeasurements
                    $details.remainingMeasurements = '9900'
                    $details.postDropValidatedMeasurements = '100'
                    $details.postDropSurvivingOddMeasurementSamples = '50'
                    $details.postDropSegments = [string]$manyExpectedSegments
                    $details.postDropReopen = 'true'
                    $details.backupDirectory = '/fixture/backup-0001'
                    $phaseIntegrity = New-M19FixtureIntegrity 'many-measurements retention, drop, and reopen samples' 0 $manyIntegrityExpected
                }
            }
        }
        $reservations = if ($Profile -eq 'maintenance-chaos' -and $isIntegrity) {
            New-M19FixtureMaintenanceChaosReservations ([int]$values.restartCount) ([int]$values.pointsPerBatch) ([int]$values.maintenanceBatches)
        }
        else {
            @()
        }
        $phases.Add([pscustomobject][ordered]@{
            name = $phaseName; durationMilliseconds = 10.0; operations = $operations; operationsPerSecond = ([double]$operations / 0.01)
            managedMemoryBytes = 10; peakManagedMemoryBytes = 50; peakWorkingSetBytes = 100; managedMemoryDeltaBytes = 1
            allocatedBytes = 100; gen0Collections = 1; gen1Collections = 0; gen2Collections = 0
            peakPrivateMemoryBytes = 80; workingSetDeltaBytes = 5; privateMemoryDeltaBytes = 3
            cpuTimeMilliseconds = 5.0; cpuUtilizationPercent = 50.0
            readOperationDelta = 2; writeOperationDelta = 3; readTransferBytesDelta = 4; writeTransferBytesDelta = 5
            integrity = $phaseIntegrity
            recoveryLatencySamplesMilliseconds = if ($isLatency) { $recoverySamples } else { @() }
            queryLatencySamplesMilliseconds = if ($isLatency) { $querySamples } else { @() }
            details = [pscustomobject]$details
            maintenanceChaosReservations = $reservations
            effectiveConfiguration = $phaseConfigurations.ToArray()
            processResourceContributions = $contributions.ToArray()
        })
    }
    $phaseCount = $phases.Count
    $externalCount = if ($Profile -eq 'maintenance-chaos') { [int]$values.restartCount } else { 0 }
    $externalIo = if ($externalCount -eq 0) { $null } else { [long]$externalCount }
    $summaryIntegrity = New-M19FixtureIntegrity ([string]$definition.integrity) 0 $integrityExpected
    $summary = [pscustomobject][ordered]@{
        peakWorkingSetBytes = 100; peakManagedMemoryBytes = 50; integrity = $summaryIntegrity
        recoveryLatency = New-M19FixtureLatencySummary $recoverySamples; queryLatency = New-M19FixtureLatencySummary $querySamples
        capacityBoundary = [pscustomobject][ordered]@{ validates = @('fixture capacity shape'); doesNotProve = @('fixture does not prove hardware capacity') }
        resources = [pscustomobject][ordered]@{
            totalAllocatedBytes = 100 * $phaseCount; totalGen0Collections = $phaseCount; totalGen1Collections = 0; totalGen2Collections = 0
            peakPrivateMemoryBytes = 80; maximumWorkingSetDeltaBytes = 5; maximumPrivateMemoryDeltaBytes = 3
            totalCpuTimeMilliseconds = 5.0 * $phaseCount; totalMeasuredDurationMilliseconds = 10.0 * $phaseCount; averageCpuUtilizationPercent = 50.0
            totalReadOperationDelta = 2 * $phaseCount; totalWriteOperationDelta = 3 * $phaseCount; totalReadTransferBytesDelta = 4 * $phaseCount; totalWriteTransferBytesDelta = 5 * $phaseCount
        }
        externalProcessResources = [pscustomobject][ordered]@{
            processCount = $externalCount; peakWorkingSetBytes = if ($externalCount -eq 0) { 0 } else { 1 }; peakPrivateMemoryBytes = if ($externalCount -eq 0) { 0 } else { 1 }
            totalCpuTimeMilliseconds = [double]$externalCount; totalObservedElapsedMilliseconds = [double]$externalCount; totalSamples = $externalCount; exitedProcessCount = $externalCount
            totalReadOperationDelta = if ($externalCount -eq 0) { $null } else { $externalIo }
            totalWriteOperationDelta = if ($externalCount -eq 0) { $null } else { 2 * $externalIo }
            totalReadTransferBytesDelta = if ($externalCount -eq 0) { $null } else { 3 * $externalIo }
            totalWriteTransferBytesDelta = if ($externalCount -eq 0) { $null } else { 4 * $externalIo }
        }
    }
    $timestamp = [DateTimeOffset]::Parse('2026-09-01T00:00:00.0000000+00:00')
    return [pscustomobject][ordered]@{
        schemaVersion = 2
        evidence = [pscustomobject][ordered]@{
            schema = 'sonnetdb.ecosystem-soak.report'; contract = 'M19-#125-capacity-evidence-v2'; runId = "fixture-$Profile"; generator = 'SonnetDB.EcosystemSoak'; generatorVersion = '1.0.0'
            sourceRevision = $CommitSha; sourceRevisionSource = 'environment:GITHUB_SHA'; workingTreeState = 'CLEAN'; generatedUtc = $timestamp.ToString('O')
            workloadFingerprint = Get-M19FixtureFingerprint $Profile $options; executionEnvironment = 'github-actions'; ciRunId = '42'; ciAttempt = '1'
        }
        profile = $Profile; startedUtc = $timestamp.ToString('O'); finishedUtc = $timestamp.AddMinutes(1).ToString('O'); succeeded = $true; failure = $null; options = $options
        environment = [pscustomobject][ordered]@{
            os = 'Linux fixture'; framework = '.NET 10.0'; architecture = 'X64'; machineName = 'fixture-host'; processorCount = 1; availableMemoryBytes = 10000; commitSha = $CommitSha
            disk = [pscustomobject][ordered]@{ root = '/'; fileSystem = 'ext4'; totalBytes = 100000; availableBytes = 50000; driveType = 'Fixed'; volumeLabel = 'NOT_APPLICABLE'; deviceModel = $StorageModel; deviceModelSource = 'environment:SONNETDB_M19_STORAGE_MODEL'; mountPoint = '/'; mountSource = '/dev/fixture'; mountDeviceId = '8:1'; resolutionSource = 'findmnt' }
            hardware = [pscustomobject][ordered]@{ processorModel = 'Fixture CPU'; processorModelSource = '/proc/cpuinfo'; operatingSystemArchitecture = 'X64'; is64BitOperatingSystem = $true; logicalProcessorCount = 1; systemPageSizeBytes = 4096; gcTotalAvailableMemoryBytes = 10000; isServerGarbageCollector = $false; gcLatencyMode = 'Interactive' }
            provenance = [pscustomobject][ordered]@{ runtimeIdentifier = 'linux-x64'; processExecutable = '/usr/bin/dotnet'; processId = 1; processStartedUtc = $null; is64BitProcess = $true; containerState = 'none' }
        }
        targetHardware = [pscustomobject][ordered]@{ status = 'PASS'; id = $TargetHardwareId; contract = $TargetHardwareContract; declarationSource = 'environment' }
        effectiveConfiguration = $rootConfigurations.ToArray(); cycles = @([pscustomobject][ordered]@{ cycle = 1; startedUtc = $timestamp.ToString('O'); finishedUtc = $timestamp.AddSeconds(30).ToString('O'); phases = $phases.ToArray() }); summary = $summary
    }
}

function Write-M19FixtureJson([string] $Path, [object] $Value) {
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $Value | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function New-M19FixtureHardwareSnapshot([string] $CommitSha, [string] $TargetHardwareId, [string] $TargetHardwareContract, [string] $StorageModel) {
    return [pscustomobject][ordered]@{
        schemaVersion = 1; capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        targetHardware = [pscustomobject][ordered]@{ status = 'PASS'; id = $TargetHardwareId; contract = $TargetHardwareContract; declarationSource = 'environment'; storageModel = $StorageModel }
        source = [pscustomobject][ordered]@{ commitSha = $CommitSha; worktreeClean = $true; worktreeStatus = @(); remote = 'https://github.com/fixture/sonnetdb.git' }
        machine = [pscustomobject][ordered]@{ name = 'fixture-host'; operatingSystem = 'Linux fixture'; processArchitecture = 'X64'; framework = '.NET 10.0'; processorCount = 1; physicalMemoryBytes = 10000; availableManagedMemoryBytes = 10000; processors = @([pscustomobject][ordered]@{ name = 'Fixture CPU'; manufacturer = 'Fixture'; cores = 1; logicalProcessors = 1; maxClockSpeedMHz = 1; processorId = 'fixture-cpu' }) }
        storage = [pscustomobject][ordered]@{ declaredModel = $StorageModel; declarationSource = 'environment:SONNETDB_M19_STORAGE_MODEL'; volumes = @([pscustomobject][ordered]@{ root = '/'; fileSystem = 'ext4'; totalBytes = 100000; availableBytes = 50000; volumeLabel = 'NOT_APPLICABLE' }); workloadMount = [pscustomobject][ordered]@{ path = '/fixture/work'; mountPoint = '/'; source = '/dev/fixture'; fileSystem = 'ext4'; deviceId = '8:1'; resolutionSource = 'findmnt' }; disks = @([pscustomobject][ordered]@{ name = 'fixture-disk'; model = 'Fixture Disk'; serial = 'fixture-serial'; rev = '1'; size = 100000; type = 'disk' }) }
    }
}

function New-M19FixtureCheckoutAttestation([string] $CommitSha, [string] $TargetHardwareId, [string] $TargetHardwareContract, [string] $StorageModel) {
    return [pscustomobject][ordered]@{
        schemaVersion = 3; attestedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        source = [pscustomobject][ordered]@{ commitSha = $CommitSha; worktreeClean = $true; worktreeStatus = @() }
        targetHardware = [pscustomobject][ordered]@{ status = 'PASS'; id = $TargetHardwareId; contract = $TargetHardwareContract; declarationSource = 'environment'; storageModel = $StorageModel }
        ci = [pscustomobject][ordered]@{ provider = 'github-actions'; repository = 'fixture/sonnetdb'; ref = 'refs/heads/main'; refProtected = $true; eventName = 'workflow_dispatch'; sha = $CommitSha; runId = 42; runAttempt = 1; workflow = 'M19 Capacity Evidence'; workflowRef = 'fixture/sonnetdb/.github/workflows/m19-capacity-evidence.yml@refs/heads/main'; serverUrl = 'https://github.com'; runUrl = 'https://github.com/fixture/sonnetdb/actions/runs/42'; runnerName = 'fixture-runner'; runnerOs = 'Linux'; runnerArchitecture = 'X64' }
    }
}

function Write-M19FixtureRawManifest([string] $BundleRoot, [string] $CommitSha, [string] $TargetHardwareId, [string] $TargetHardwareContract) {
    $paths = @(
        'high-cardinality/report.json', 'high-cardinality/report.md', 'small-segments/report.json', 'small-segments/report.md',
        'maintenance-chaos/report.json', 'maintenance-chaos/report.md', 'many-measurements/report.json', 'many-measurements/report.md',
        'target-hardware.json', 'checkout-attestation.json')
    $files = foreach ($relativePath in $paths) {
        $path = Join-Path $BundleRoot ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [pscustomobject][ordered]@{ path = $relativePath; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $path).Length }
    }
    Write-M19FixtureJson (Join-Path $BundleRoot 'raw-artifact-manifest.json') ([pscustomobject][ordered]@{
        schemaVersion = 1; source = [pscustomobject][ordered]@{ commitSha = $CommitSha; worktreeClean = $true; worktreeStatus = @() }
        targetHardware = [pscustomobject][ordered]@{ id = $TargetHardwareId; contract = $TargetHardwareContract }; files = @($files)
    })
}

function New-M19FixtureBundle([string] $BundleRoot) {
    $commit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $targetId = 'fixture-target'; $contract = 'M19-#125-frozen-target-v1'; $storage = 'fixture-storage'
    New-Item -ItemType Directory -Force -Path $BundleRoot | Out-Null
    foreach ($profile in @('high-cardinality', 'small-segments', 'maintenance-chaos', 'many-measurements')) {
        $profileRoot = Join-Path $BundleRoot $profile
        Write-M19FixtureJson (Join-Path $profileRoot 'report.json') (New-M19FixtureReport -Profile $profile -CommitSha $commit -TargetHardwareId $targetId -TargetHardwareContract $contract -StorageModel $storage)
        Set-Content -LiteralPath (Join-Path $profileRoot 'report.md') -Value "# $profile fixture" -Encoding utf8NoBOM
    }
    Write-M19FixtureJson (Join-Path $BundleRoot 'target-hardware.json') (New-M19FixtureHardwareSnapshot $commit $targetId $contract $storage)
    Write-M19FixtureJson (Join-Path $BundleRoot 'checkout-attestation.json') (New-M19FixtureCheckoutAttestation $commit $targetId $contract $storage)
    Write-M19FixtureRawManifest $BundleRoot $commit $targetId $contract
    return [pscustomobject]@{ bundleRoot = $BundleRoot; commitSha = $commit; targetHardwareId = $targetId; targetHardwareContract = $contract; artifactUrl = 'https://github.com/fixture/sonnetdb/actions/runs/42' }
}
