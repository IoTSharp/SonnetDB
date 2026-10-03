param([switch]$Trial)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$workspace = 'D:\source\SonnetDB'
$evidence = $PSScriptRoot
$timer = [Diagnostics.Stopwatch]::StartNew()
$identities = @{}

function CheckDeadline {
    if ($timer.Elapsed.TotalSeconds -ge 60 -or (Test-Path -LiteralPath (Join-Path $evidence 'cancel.flag'))) {
        throw 'Summary deadline or cancellation reached.'
    }
}
function AddIdentity($identity) {
    if ($null -eq $identity) { return }
    $key = '{0}|{1}' -f $identity.pid, $identity.created
    $identities[$key] = $identity
    if ($identities.Count -gt 4096) { throw 'Recorded identity count exceeds 4096.' }
}

$processFiles = @(Get-ChildItem -LiteralPath $evidence -Filter '*.process.json' -File)
if ($processFiles.Count -gt 32) { throw 'Run count exceeds 32.' }
if ($Trial) { $processFiles = @($processFiles | Select-Object -First 1) }
$runs = foreach ($file in $processFiles) {
    CheckDeadline
    $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    AddIdentity $record.root
    AddIdentity $record.owner
    if (@($record.samples).Count -gt 1000) { throw 'Sample count exceeds 1000.' }
    foreach ($sample in $record.samples) {
        CheckDeadline
        if (@($sample.chains).Count -gt 128) { throw 'Chain count exceeds 128.' }
        foreach ($chain in $sample.chains) {
            CheckDeadline
            if (@($chain).Count -gt 64) { throw 'Chain depth exceeds 64.' }
            AddIdentity $chain[0]
        }
    }
    if (@($record.cleanup).Count -gt 256) { throw 'Cleanup action count exceeds 256.' }
    foreach ($action in $record.cleanup) {
        CheckDeadline
        AddIdentity $action.identity
        if ($action.chain) { AddIdentity $action.chain[0] }
    }
    [ordered]@{
        name = $record.name
        runnerSha256 = $record.runnerSha256
        exitCode = $record.exitCode
        reason = $record.reason
        elapsedSeconds = $record.elapsedSeconds
        timeoutSeconds = $record.timeoutSeconds
        finalSnapshotAvailable = $record.finalSnapshotAvailable
        remainingCount = @($record.remaining).Count
        rootCimIdentityCaptured = $null -ne $record.root
        recordSha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
}

$testNames = if ($Trial) { @('targeted-final') } else { @('targeted','targeted-corrected','targeted-final','core-full') }
$tests = foreach ($name in $testNames) {
    CheckDeadline
    $path = Join-Path $evidence "$name.trx"
    $trx = [xml](Get-Content -LiteralPath $path -Raw)
    $counter = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    [ordered]@{
        name = $name
        total = [int]$counter.GetAttribute('total')
        passed = [int]$counter.GetAttribute('passed')
        failed = [int]$counter.GetAttribute('failed')
        notExecuted = [int]$counter.GetAttribute('notExecuted')
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

$current = @{}
$snapshot = if ($Trial) { @(Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -OperationTimeoutSec 5) } else { @(Get-CimInstance Win32_Process -OperationTimeoutSec 5) }
if ($snapshot.Count -gt 4096) { throw 'Current process count exceeds 4096.' }
foreach ($item in $snapshot) { CheckDeadline; $current[[int]$item.ProcessId] = $item }
$live = foreach ($identity in $identities.Values) {
    CheckDeadline
    $item = $current[[int]$identity.pid]
    if ($null -ne $item -and $item.CreationDate.ToUniversalTime().ToString('O') -eq $identity.created -and
        [string]$item.CommandLine -eq $identity.command -and [int]$item.ParentProcessId -eq $identity.parent) {
        $identity
    }
}

if ($Trial) { $paths = @('samples/SonnetDB.CdcStreamingJourney/SqlBudgetJourney.cs') }
else {
    $paths = @(& git -C $workspace diff HEAD --name-only)
    if ($LASTEXITCODE -ne 0) { throw 'Git diff inventory failed.' }
    $paths += @(& git -C $workspace ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -gt 200) { throw 'Git source inventory failed or exceeds 200.' }
}
$manifest = foreach ($path in @($paths | Sort-Object -Unique)) {
    CheckDeadline
    if ($path.StartsWith('docs/audits/roadmap-parallel-next-evidence-20261003/', [StringComparison]::Ordinal)) { continue }
    $absolute = [IO.Path]::GetFullPath((Join-Path $workspace $path))
    if (-not $absolute.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escapes workspace.' }
    [ordered]@{ path = $path; sha256 = (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash }
}
$aot = Get-Content -LiteralPath (Join-Path $evidence 'aot-binary.json') -Raw | ConvertFrom-Json
$temporary = Get-Content -LiteralPath (Join-Path $evidence 'temporary-cleanup.json') -Raw | ConvertFrom-Json
$summary = [ordered]@{
    baseline = '4ad6c47ddd327d6a66f7ecdb34b0f20ec59e41da'
    branch = 'codex/roadmap-parallel-next-20261003'
    checkedAt = [DateTime]::UtcNow.ToString('O')
    trial = [bool]$Trial
    scope = 'M42 direct measurement KNN, document vector_search and JSON-file TVF cumulative materialization admission'
    newCases = [ordered]@{ knn = 44; documentVector = 43; jsonFile = 46; sharedPreflight = 11; journey = 2; total = 146 }
    tests = @($tests)
    runs = @($runs)
    aotBinary = $aot
    resourceAudit = [ordered]@{
        recordedIdentities = $identities.Count
        currentSnapshotAvailable = $true
        snapshotScope = if ($Trial) { 'trial_current_launcher_only' } else { 'all_current_processes_max_4096' }
        matchingLiveRecordedIdentities = @($live)
        remainingCount = @($live).Count
        shortCommands = 'Commands which exited before CIM captured the root have launch/exit records, not full CIM ownership proof.'
        exclusiveMcpStarted = $false
        temporary = $temporary
        nativeTemporaryDirectoryExists = Test-Path -LiteralPath $temporary.path
        oldTemporaryDirectoryExists = Test-Path -LiteralPath $temporary.oldTemporaryPath
    }
    sourceManifest = @($manifest)
    boundary = 'Local contracts and orderly reopen only. No remote parity, fixed hardware, whole-heap, real model quality, long-run or production evidence.'
}
CheckDeadline
$outputName = if ($Trial) { 'summary-trial.json' } else { 'validation-summary.json' }
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence $outputName) -Encoding utf8
Write-Host "Summary complete: tests=$(@($tests)[-1].passed), sourceFiles=$(@($manifest).Count), remaining=$(@($live).Count)."
