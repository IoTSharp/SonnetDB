$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$taskRoot = 'C:\Temp\sdbp-81bfff26'
$repoRoot = 'D:\source\SonnetDB'
$delivery = Join-Path $repoRoot 'docs\audits\roadmap-parallel-evidence-20261002'
[IO.Directory]::CreateDirectory($delivery) | Out-Null
$timer = [Diagnostics.Stopwatch]::StartNew()
function Check-EvidenceDeadline {
    if ($timer.Elapsed.TotalSeconds -ge 45 -or (Test-Path -LiteralPath (Join-Path $taskRoot 'cancel'))) { throw 'Evidence collection cancelled or timed out.' }
}
$commands = @('runner-smoke','restore-standard','restore-isolated','build-release','tiny-boundaries','new-targeted','core-full','server-related','format-final','index-validation','restore-crash','build-crash','subscription-hard-kill-stale','subscription-hard-kill')
if ($commands.Count -gt 20) { throw 'Too many evidence commands.' }
$summaries = @()
foreach ($name in $commands) {
    Check-EvidenceDeadline
    $record = Join-Path $taskRoot ($name + '.processes.json')
    $details = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    $summaries += [ordered]@{ Name=$name; ExitCode=$details.ExitCode; Failure=$details.Failure; DurationSeconds=$details.DurationSeconds; Eligible=$name -ne 'subscription-hard-kill-stale' }
    foreach ($suffix in @('.config.json','.processes.json','.stdout.log','.stderr.log')) {
        Check-EvidenceDeadline
        $path = Join-Path $taskRoot ($name + $suffix)
        if ([IO.File]::Exists($path)) {
            if ((Get-Item -LiteralPath $path).Length -gt 8MB) { throw 'Unexpectedly large evidence log.' }
            $content = [IO.File]::ReadAllText($path).Replace([string][char]13, '')
            [IO.File]::WriteAllText((Join-Path $delivery ($name + $suffix)), $content)
        }
    }
}
$tests = [ordered]@{}
foreach ($name in @('tiny-boundaries','new-targeted','core-full','server-related','subscription-hard-kill')) {
    Check-EvidenceDeadline
    $path = Join-Path $taskRoot ('results\' + $name + '.trx')
    if ((Get-Item -LiteralPath $path).Length -gt 64MB) { throw 'Unexpectedly large test results.' }
    [xml]$run = [IO.File]::ReadAllText($path)
    $counters = $run.TestRun.ResultSummary.Counters
    $results = @($run.TestRun.Results.UnitTestResult)
    if ($results.Count -gt 10000) { throw 'Too many test outcomes.' }
    $failures = @()
    $groups = @{}
    foreach ($result in $results) {
        Check-EvidenceDeadline
        if ($result.outcome -ne 'Passed') {
            $failures += [ordered]@{ Name=$result.testName; Outcome=$result.outcome; Message=$result.Output.ErrorInfo.Message }
        }
        if ($name -eq 'new-targeted') {
            $group = if ($result.testName.Contains('.DocumentTtlSelectMaterializationBudgetTests.')) { 'TTL' }
                elseif ($result.testName.Contains('.FileStreamingSlidingWindowTests.')) { 'Sliding' }
                elseif ($result.testName.Contains('.FileStreamingDeadLetterOperationsTests.')) { 'DLQ' }
                else { 'Journey' }
            $groups[$group] = 1 + [int]$groups[$group]
        }
    }
    $tests[$name] = [ordered]@{
        Total=[int]$counters.total; Passed=[int]$counters.passed; Failed=[int]$counters.failed
        NotExecuted=[int]$counters.notExecuted; Outcome=$run.TestRun.ResultSummary.outcome
        TrxSha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; Failures=$failures; Groups=$groups
    }
}
$sourcePaths = @(git diff --name-only -- src tests)
$sourcePaths += @(git ls-files --others --exclude-standard -- src tests)
if ($sourcePaths.Count -gt 64) { throw 'Unexpected source change count.' }
$sourceHashes = @()
foreach ($relative in $sourcePaths) {
    Check-EvidenceDeadline
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
    if (-not $path.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path outside repository.' }
    $sourceHashes += [ordered]@{ Path=$relative; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
$index = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\audits\fourteen-capability-journey-index.json') -Raw | ConvertFrom-Json
$validation = [ordered]@{
    SchemaVersion='1.0'; Scope='LOCAL_WINDOWS_CONTRACT_ONLY'; BaseCommit='54e75dd6'; Branch='codex/roadmap-parallel-20261002'
    PowerShell=$PSVersionTable.PSVersion.ToString(); DotnetSdk='10.0.401'; TaskRoot=$taskRoot
    Commands=$summaries; Tests=$tests; Sources=$sourceHashes; ComposedJourneys=$index.composedJourneys.Count
    CrashArtifacts=(Get-Content -LiteralPath (Join-Path $taskRoot 'crash-artifacts.json') -Raw | ConvertFrom-Json)
    InitialBuildWrapperFailure='First full build process cleanup encountered an exited child race before logs were saved. Not counted as successful validation. Runner repaired and the final solution build returned exit 0 with 0 warnings/errors.'
    NativeAotPublish='NOT_RUN'; RemoteStreamingParity='NOT_RUN'; FixedHardware='DEFERRED'; Nightly='NOT_RUN'; LongRun='DEFERRED'
}
$validation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $delivery 'validation.json')
Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts\parallel-20261002\Run-Bounded.ps1') -Destination (Join-Path $delivery 'Run-Bounded.ps1')
Copy-Item -LiteralPath (Join-Path $taskRoot 'crash-artifacts.json') -Destination (Join-Path $delivery 'crash-artifacts.json')
Write-Output "Saved evidence: commands=$($commands.Count) sourceFiles=$($sourcePaths.Count) composedJourneys=$($index.composedJourneys.Count)"
