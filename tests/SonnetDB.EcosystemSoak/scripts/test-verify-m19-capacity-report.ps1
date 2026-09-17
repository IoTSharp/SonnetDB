$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }

. (Join-Path $PSScriptRoot 'm19-capacity-fixtures.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('m19-report-verifier-test-' + [guid]::NewGuid().ToString('N'))
$verifier = Join-Path $PSScriptRoot 'verify-m19-capacity-report.ps1'
$pwsh = (Get-Process -Id $PID).Path
if (-not (Test-Path -LiteralPath $pwsh -PathType Leaf)) { throw "Current PowerShell executable is unavailable: $pwsh" }

function Assert-ReportPass([string] $ReportPath) {
    $output = @(& $pwsh -NoLogo -NoProfile -File $verifier -ReportPath $ReportPath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Expected report verifier PASS, got: $($output -join "`n")" }
    $result = ($output -join "`n") | ConvertFrom-Json -Depth 32
    if ([string]$result.status -ne 'PASS') { throw "Expected PASS result, got '$($result.status)'." }
}

function Assert-ReportNotReady([string] $ReportPath, [string] $Label) {
    $output = @(& $pwsh -NoLogo -NoProfile -File $verifier -ReportPath $ReportPath 2>&1)
    if ($LASTEXITCODE -eq 0) {
        throw "$Label must return a nonzero NOT_READY outcome. Output: $($output -join "`n")"
    }
}

function Write-Variant([string] $Name, [scriptblock] $Mutation, [string] $Profile = 'high-cardinality') {
    $report = New-M19FixtureReport -Profile $Profile
    & $Mutation $report
    $path = Join-Path $root "$Name.json"
    Write-M19FixtureJson $path $report
    return $path
}

New-Item -ItemType Directory -Path $root | Out-Null
try {
    $validPath = Write-Variant 'valid' { param($report) }
    Assert-ReportPass $validPath
    foreach ($profile in @('small-segments', 'maintenance-chaos', 'many-measurements')) {
        Assert-ReportPass (Write-Variant ("valid-" + $profile) { param($report) } $profile)
    }

    Assert-ReportNotReady (Write-Variant 'default-shape' {
        param($report)
        $report.options.targetSegments = 2
    }) 'Non-default capacity shape'

    Assert-ReportNotReady (Write-Variant 'fingerprint' {
        param($report)
        $report.evidence.workloadFingerprint = ('0' * 64)
    }) 'Mismatched workload fingerprint'

    Assert-ReportNotReady (Write-Variant 'latency-summary' {
        param($report)
        $report.summary.queryLatency.p95Milliseconds = 1.0
    }) 'Tampered latency summary'

    Assert-ReportNotReady (Write-Variant 'missing-phase' {
        param($report)
        $report.cycles[0].phases = @($report.cycles[0].phases | Select-Object -Skip 1)
    }) 'Missing required phase'

    Assert-ReportNotReady (Write-Variant 'missing-integrity-counter' {
        param($report)
        $integrity = $report.cycles[0].phases[1].integrity
        $integrity.PSObject.Properties.Remove('valueMismatches')
    }) 'Missing integrity counter'

    Assert-ReportNotReady (Write-Variant 'invalid-resource' {
        param($report)
        $report.cycles[0].phases[0].readTransferBytesDelta = -1
    }) 'Negative I/O resource delta'

    Assert-ReportNotReady (Write-Variant 'dirty-worktree' {
        param($report)
        $report.evidence.workingTreeState = 'DIRTY'
    }) 'Dirty source evidence'

    Assert-ReportNotReady (Write-Variant 'local-execution-environment' {
        param($report)
        $report.evidence.executionEnvironment = 'local-or-unknown'
    }) 'Local execution environment'

    Assert-ReportNotReady (Write-Variant 'git-source-revision' {
        param($report)
        $report.evidence.sourceRevisionSource = 'git:rev-parse'
    }) 'Non-GitHub source revision provenance'

    Assert-ReportNotReady (Write-Variant 'missing-ci-run' {
        param($report)
        $report.evidence.ciRunId = $null
    }) 'Missing CI run identifier'

    Assert-ReportNotReady (Write-Variant 'invalid-ci-attempt' {
        param($report)
        $report.evidence.ciAttempt = '0'
    }) 'Invalid CI attempt'

    Assert-ReportNotReady (Write-Variant 'configuration-contract' {
        param($report)
        $report.effectiveConfiguration[1].flushWalToOsOnWrite = $true
    }) 'Configuration durability mutation'

    Assert-ReportNotReady (Write-Variant 'chaos-reservation-gap' {
        param($report)
        $report.cycles[0].phases[0].maintenanceChaosReservations[1].startInclusive = 3
        $report.cycles[0].phases[0].maintenanceChaosReservations[1].endInclusive = 3
        $report.cycles[0].phases[0].maintenanceChaosReservations[1].acknowledgedThroughInclusive = 3
    } 'maintenance-chaos') 'Non-contiguous maintenance-chaos reservation'

    Assert-ReportNotReady (Write-Variant 'chaos-recovered-tail-exceeds-reservation' {
        param($report)
        $phase = $report.cycles[0].phases[0]
        $phase.details.unacknowledgedButRecovered = '1'
        $phase.integrity.unexpectedPoints = 1
        $phase.integrity.observedPoints = $phase.integrity.expectedPoints + 1
        $report.summary.integrity.unexpectedPoints = 1
        $report.summary.integrity.observedPoints = $report.summary.integrity.expectedPoints + 1
    } 'maintenance-chaos') 'Recovered tail exceeds reservation accounting'

    Assert-ReportNotReady (Write-Variant 'chaos-reservation-publication-contract' {
        param($report)
        $worker = @($report.effectiveConfiguration | Where-Object { $_.scope -eq 'maintenance-chaos-worker' })[0]
        $worker.settings.reservationReplace = 'plain-file-write'
    } 'maintenance-chaos') 'Reservation worker publication metadata'

    Assert-ReportNotReady (Write-Variant 'non-chaos-reservation' {
        param($report)
        $report.cycles[0].phases[0].maintenanceChaosReservations = @([pscustomobject]@{
            restart = 1; startInclusive = 0; endInclusive = 0; acknowledgedThroughInclusive = 0
        })
    }) 'Non-chaos maintenance reservation'

    Assert-ReportNotReady (Write-Variant 'phase-order' {
        param($report)
        $phases = $report.cycles[0].phases
        $report.cycles[0].phases = @($phases[1], $phases[0], $phases[2], $phases[3])
    } 'many-measurements') 'Out-of-order phase sequence'

    Assert-ReportNotReady (Write-Variant 'high-write-operations' {
        param($report)
        $phase = $report.cycles[0].phases[0]
        $phase.operations = 999999
        $phase.operationsPerSecond = [double]$phase.operations / ([double]$phase.durationMilliseconds / 1000)
    } 'high-cardinality') 'High-cardinality write operations contract'

    Assert-ReportNotReady (Write-Variant 'high-recovery-sample-count' {
        param($report)
        $report.cycles[0].phases[1].details.validatedSeries = '99'
    } 'high-cardinality') 'High-cardinality recovery sample contract'

    Assert-ReportNotReady (Write-Variant 'small-segment-bytes' {
        param($report)
        $report.cycles[0].phases[0].details.segmentBytes = '0'
    } 'small-segments') 'Small-segments byte evidence contract'

    Assert-ReportNotReady (Write-Variant 'chaos-worker-batch-count' {
        param($report)
        $report.cycles[0].phases[0].details.acknowledgedWorkerBatches = '1'
    } 'maintenance-chaos') 'Maintenance-chaos worker batch total'

    Assert-ReportNotReady (Write-Variant 'chaos-retention-run-once' {
        param($report)
        $report.cycles[0].phases[0].details.retentionRunOnce = 'false'
    } 'maintenance-chaos') 'Maintenance-chaos retention completion'

    Assert-ReportNotReady (Write-Variant 'chaos-reservation-batch-alignment' {
        param($report)
        $reservation = $report.cycles[0].phases[0].maintenanceChaosReservations[0]
        $reservation.acknowledgedThroughInclusive = $reservation.acknowledgedThroughInclusive - 1
    } 'maintenance-chaos') 'Maintenance-chaos reservation batch alignment'

    Assert-ReportNotReady (Write-Variant 'many-write-expected-segments' {
        param($report)
        $report.cycles[0].phases[0].details.expectedSegments = '99'
    } 'many-measurements') 'Many-measurements initial segment contract'

    Assert-ReportNotReady (Write-Variant 'many-backup-checked-files' {
        param($report)
        $report.cycles[0].phases[2].details.backupCheckedFiles = '3'
    } 'many-measurements') 'Many-measurements backup file accounting'

    Assert-ReportNotReady (Write-Variant 'many-backup-not-verified' {
        param($report)
        $report.cycles[0].phases[2].details.backupVerified = 'false'
    } 'many-measurements') 'Many-measurements backup verification flag'

    Assert-ReportNotReady (Write-Variant 'many-retention-samples' {
        param($report)
        $report.cycles[0].phases[3].details.retentionSurvivingOddMeasurementSamples = '49'
    } 'many-measurements') 'Many-measurements retention sample contract'

    Assert-ReportNotReady (Write-Variant 'many-retention-integrity' {
        param($report)
        $phase = $report.cycles[0].phases[3]
        $phase.integrity.expectedPoints = 249
        $phase.integrity.observedPoints = 249
        $report.summary.integrity.expectedPoints = 249
        $report.summary.integrity.observedPoints = 249
    } 'many-measurements') 'Many-measurements integrity contract'

    Assert-ReportNotReady (Write-Variant 'architecture' {
        param($report)
        $report.environment.architecture = 'Arm64'
    }) 'Process architecture contract'

    Assert-ReportNotReady (Write-Variant 'os-not-64-bit' {
        param($report)
        $report.environment.hardware.is64BitOperatingSystem = $false
    }) 'Operating-system bitness contract'

    Assert-ReportNotReady (Write-Variant 'process-bitness-string' {
        param($report)
        $report.environment.provenance.is64BitProcess = 'true'
    }) 'Boolean process bitness contract'

    Assert-ReportNotReady (Write-Variant 'container' {
        param($report)
        $report.environment.provenance.containerState = 'docker'
    }) 'Container execution contract'

    Assert-ReportNotReady (Write-Variant 'mount-resolution' {
        param($report)
        $report.environment.disk.resolutionSource = 'driveinfo-path-root'
    }) 'Linux mount resolution contract'

    Assert-ReportNotReady (Write-Variant 'mount-identity' {
        param($report)
        $report.environment.disk.mountPoint = '/other'
    }) 'Mount root identity contract'

    Assert-ReportNotReady (Write-Variant 'forbidden-filesystem' {
        param($report)
        $report.environment.disk.fileSystem = 'overlay'
    }) 'Forbidden filesystem contract'

    Write-Output 'M19 report verifier contract tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
