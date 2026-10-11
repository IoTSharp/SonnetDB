param([switch]$Trial)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$cancelPath = Join-Path $PSScriptRoot 'cancel.flag'
$records = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.process.json' -File)
if ($records.Count -gt 32) { throw 'Evidence record limit exceeded.' }
if ($Trial) { $records = @($records | Select-Object -First 1) }
$results = [System.Collections.Generic.List[object]]::new()
foreach ($file in $records) {
    if ($timer.Elapsed.TotalSeconds -ge 60 -or (Test-Path -LiteralPath $cancelPath)) { throw 'Summary cancelled or timed out.' }
    if ($file.Length -gt 20MB) { throw 'Evidence file exceeds 20 MiB.' }
    $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    $results.Add([ordered]@{
        name = $record.name; exitCode = $record.exitCode; reason = $record.reason
        seconds = $record.elapsedSeconds; timeoutSeconds = $record.timeoutSeconds
        executable = $record.executable; arguments = $record.arguments
        finalSnapshotAvailable = $record.finalSnapshotAvailable; remaining = @($record.remaining).Count
        recordSha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    })
}
$tests = [System.Collections.Generic.List[object]]::new()
$traces = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.trx' -File)
if ($traces.Count -gt 8) { throw 'TRX file limit exceeded.' }
if ($Trial) { $traces = @($traces | Select-Object -First 1) }
foreach ($file in $traces) {
    if ($timer.Elapsed.TotalSeconds -ge 60 -or (Test-Path -LiteralPath $cancelPath)) { throw 'Summary cancelled or timed out.' }
    if ($file.Length -gt 32MB) { throw 'TRX exceeds 32 MiB.' }
    [xml]$document = Get-Content -LiteralPath $file.FullName -Raw
    $counters = $document.TestRun.ResultSummary.Counters
    $tests.Add([ordered]@{
        file = $file.Name; total = [int]$counters.total; passed = [int]$counters.passed
        failed = [int]$counters.failed; notExecuted = [int]$counters.notExecuted
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    })
}
$sourcePaths = @(
    'src/SonnetDB.Core/Cdc/CdcEventSpool.cs', 'src/SonnetDB.Core/Cdc/CdcStreamingBridge.cs',
    'src/SonnetDB.Core/Cdc/CdcStreamingBridgeTopology.cs', 'src/SonnetDB.Core/Streaming/FileStreamingTaskRunner.cs',
    'src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs', 'src/SonnetDB.Core/Sql/Execution/SqlExecutionOptions.cs',
    'samples/SonnetDB.CdcStreamingJourney/CdcStreamingTaskJourney.cs', 'samples/SonnetDB.CdcStreamingJourney/Program.cs',
    'tests/SonnetDB.Core.Tests/Sql/MeasurementOrderMaterializationBudgetTests.cs',
    'tests/SonnetDB.Core.Tests/Sql/SqlMeasurementMaterializationBudgetTests.cs',
    'tests/SonnetDB.Core.Tests/Cdc/CdcStreamingBridgeTopologyTests.cs',
    'tests/SonnetDB.Core.Tests/Streaming/FileStreamingTaskRunnerTests.cs',
    'tests/SonnetDB.Core.Tests/Audits/CdcStreamingTaskJourneyTests.cs'
)
$sources = [System.Collections.Generic.List[object]]::new()
if ($Trial) { $sourcePaths = @($sourcePaths | Select-Object -First 1) }
foreach ($relativePath in $sourcePaths) {
    if ($timer.Elapsed.TotalSeconds -ge 60 -or (Test-Path -LiteralPath $cancelPath)) { throw 'Summary cancelled or timed out.' }
    $path = Join-Path 'D:\source\SonnetDB' $relativePath
    $sources.Add([ordered]@{ file = $relativePath; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash })
}
$summary = [ordered]@{
    baseline = 'a0e1dff4679f4450fc10fec87cd527515777c700'
    evidenceLevel = 'LOCAL_ONLY'; powershell = $PSVersionTable.PSVersion.ToString()
    maxRecords = 32; maxTraces = 8; timeoutSeconds = 60; runs = $results.ToArray(); tests = $tests.ToArray(); sources = $sources.ToArray()
}
if ($Trial) { $summary | ConvertTo-Json -Depth 8; exit 0 }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'validation-summary.json') -Encoding utf8
Write-Host "Summarized $($results.Count) runs and $($tests.Count) TRX files."
