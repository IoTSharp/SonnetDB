[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$taskLogs = Join-Path $WorkRoot 'artifacts/checks'
$taskSpecifications = [Collections.Generic.List[object]]::new()
$taskSpecifications.Add(@{ label = 'native-public4-original'; passed = 3; failed = 4; failureText = 'TableConstraintException.*无主键空表' })
$taskSpecifications.Add(@{ label = 'native-public5-original'; passed = 3; failed = 4; failureText = 'TableConstraintException.*无主键空表' })
$taskSpecifications.Add(@{ label = 'native-source-final'; passed = 7; failed = 0 })
foreach ($taskProtocol in @('embedded', 'rest', 'frame-http2')) {
    $taskSpecifications.Add(@{ label = "chloe-original-01-$taskProtocol-exercise"; passed = 9; failed = 11; failureText = 'SonnetDB cannot write relational tables without a primary key' })
    $taskSpecifications.Add(@{ label = "chloe-candidate-01-$taskProtocol-exercise"; passed = 20; failed = 0 })
    $taskSpecifications.Add(@{ label = "chloe-candidate-01-$taskProtocol-reopen"; passed = 5; failed = 0 })
}
$taskRuns = [Collections.Generic.List[object]]::new()
$taskBinaries = [Collections.Generic.Dictionary[string, object]]::new()
$taskProcessIds = [Collections.Generic.HashSet[int]]::new()
foreach ($taskSpecification in $taskSpecifications) {
    $taskLabel = $taskSpecification.label
    $taskReceipt = Get-Content -LiteralPath (Join-Path $taskLogs "$taskLabel.json") -Raw -Encoding utf8 | ConvertFrom-Json
    $taskLines = [IO.File]::ReadAllLines((Join-Path $taskLogs "$taskLabel.stdout.log"))
    $taskPass = @($taskLines | Where-Object { $_.StartsWith('PASS|') }).Count
    $taskFailures = @($taskLines | Where-Object { $_.StartsWith('FAIL|') })
    $taskExpectedExit = if ($taskSpecification.failed -eq 0) { 0 } else { 1 }
    if ($taskPass -ne $taskSpecification.passed -or $taskFailures.Count -ne $taskSpecification.failed -or $taskReceipt.exitCode -ne $taskExpectedExit) { throw "Unexpected results: $taskLabel" }
    if ($taskReceipt.timedOut -or $taskReceipt.failure -or $taskReceipt.remainingRecordedProcesses.Count -gt 0) { throw "Process boundary failed: $taskLabel" }
    if ($taskSpecification.failed -gt 0 -and @($taskFailures | Where-Object { $_ -notmatch $taskSpecification.failureText }).Count -gt 0) { throw "Unexpected failure reason: $taskLabel" }
    if (-not $taskProcessIds.Add([int]$taskReceipt.launchedPid)) { throw 'Consumer process was reused.' }
    foreach ($taskLine in $taskLines | Where-Object { $_.StartsWith('ASSEMBLY|') }) {
        $taskParts = $taskLine.Split('|')
        $taskHash = (Get-FileHash -LiteralPath $taskParts[4] -Algorithm SHA256).Hash
        if ($taskHash -ne $taskParts[3]) { throw "Executed binary changed: $($taskParts[4])" }
        $taskBinaries[$taskParts[4]] = [ordered]@{ path = $taskParts[4]; sha256 = $taskHash; informationalVersion = $taskParts[2] }
    }
    $taskRuns.Add([ordered]@{ label = $taskLabel; processId = $taskReceipt.launchedPid; passed = $taskPass; failed = $taskFailures.Count; exitCode = $taskReceipt.exitCode; failures = $taskFailures })
}
foreach ($taskName in @('KeylessChloe.dll', 'Chloe.dll', 'SonnetDB.Core.dll', 'SonnetDB.Data.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $WorkRoot "chloe-original-source-engine/$taskName")).Hash -ne (Get-FileHash -LiteralPath (Join-Path $WorkRoot "chloe-candidate-source-engine/$taskName")).Hash) { throw "Non-provider assembly changed: $taskName" }
}
foreach ($taskName in @('SonnetDB.Core', 'SonnetDB.Data')) {
    if ((Get-FileHash -LiteralPath (Join-Path $WorkRoot "chloe-candidate-source-engine/$taskName.dll")).Hash -ne (Get-FileHash -LiteralPath (Join-Path $SourceRoot "src/$taskName/bin/Release/net10.0/$taskName.dll")).Hash) { throw 'Source binary origin differs.' }
}
$taskAssets = [Collections.Generic.List[object]]::new()
foreach ($taskVersion in @('4', '5')) {
    $taskVersionText = if ($taskVersion -eq '4') { '4.0.0' } else { '4.5.0-preview.ef266.5' }
    $taskAssetPath = Join-Path $WorkRoot "native-public$taskVersion/obj/project.assets.json"
    $taskAsset = Get-Content -LiteralPath $taskAssetPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
    if (@($taskAsset.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -gt 0) { throw 'Public consumer has ProjectReference.' }
    foreach ($taskPackage in @('SonnetDB', 'SonnetDB.Core')) {
        if (-not $taskAsset.libraries.ContainsKey("$taskPackage/$taskVersionText")) { throw 'Fixed package missing.' }
    }
    $taskAssets.Add([ordered]@{ path = $taskAssetPath; version = $taskVersionText; projectReferenceCount = 0; sha256 = (Get-FileHash -LiteralPath $taskAssetPath).Hash })
}
$taskRequests = [Collections.Generic.List[object]]::new()
$taskServers = [Collections.Generic.List[object]]::new()
foreach ($taskMatrixLabel in @('chloe-original-01', 'chloe-candidate-01')) {
    $taskMatrix = Get-Content -LiteralPath (Join-Path $taskLogs "$taskMatrixLabel-matrix.json") -Raw -Encoding utf8 | ConvertFrom-Json
    if (@($taskMatrix.serverSessions | Where-Object { -not $_.exited }).Count -ne 0) { throw 'Owned server still running.' }
    if ($taskMatrixLabel -eq 'chloe-candidate-01' -and ($taskMatrix.serverSessions.Count -ne 2 -or $taskMatrix.serverSessions[0].pid -eq $taskMatrix.serverSessions[1].pid)) { throw 'Remote server did not restart into a new process.' }
    foreach ($taskSession in $taskMatrix.serverSessions) {
        $taskPath = Join-Path $taskLogs "$taskMatrixLabel-server-$($taskSession.phase).stdout.log"
        $taskObserved = @(Get-Content -LiteralPath $taskPath -Encoding utf8 | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.Category -eq 'Microsoft.AspNetCore.Hosting.Diagnostics' -and $_.EventId -eq 1 })
        $taskRest = @($taskObserved | Where-Object { $_.State.Protocol -eq 'HTTP/1.1' -and $_.State.Path -match '/v1/db/.+/sql' -and $_.State.Host -eq "127.0.0.1:$($taskMatrix.httpPort)" })
        $taskH2 = @($taskObserved | Where-Object { $_.State.Protocol -eq 'HTTP/2' -and $_.State.Path -eq '/v1/frame' -and $_.State.Host -eq "127.0.0.1:$($taskMatrix.h2Port)" })
        if ($taskRest.Count -eq 0 -or $taskH2.Count -eq 0) { throw 'Actual REST/h2c requests missing.' }
        $taskRequests.Add([ordered]@{ matrix = $taskMatrixLabel; phase = $taskSession.phase; restSqlRequests = $taskRest.Count; h2FrameRequests = $taskH2.Count; serverPid = $taskSession.pid })
    }
    $taskServers.Add([ordered]@{ label = $taskMatrixLabel; dataRoot = $taskMatrix.dataRoot; sourceServer = $taskMatrix.sourceServer; sessions = @($taskMatrix.serverSessions) })
}
$taskTests = [Collections.Generic.List[object]]::new()
foreach ($taskName in @('keyless-core.trx', 'keyless-remote.trx')) {
    $taskPath = Join-Path $WorkRoot "test-results/$taskName"
    [xml]$taskTrx = Get-Content -LiteralPath $taskPath -Raw -Encoding utf8
    $taskCounter = $taskTrx.TestRun.ResultSummary.Counters
    if ([int]$taskCounter.failed -ne 0 -or [int]$taskCounter.notExecuted -ne 0) { throw 'Source regression failed or skipped.' }
    $taskTests.Add([ordered]@{ path = $taskPath; total = [int]$taskCounter.total; passed = [int]$taskCounter.passed; failed = [int]$taskCounter.failed; notExecuted = [int]$taskCounter.notExecuted })
}
$taskFiles = [Collections.Generic.Dictionary[string, object]]::new()
function Add-TaskFile([string]$Path) {
    $taskItem = Get-Item -LiteralPath $Path
    $taskFiles[$taskItem.FullName] = [ordered]@{ path = $taskItem.FullName; bytes = $taskItem.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
foreach ($taskDirectory in @('artifacts/checks', 'test-results', 'source-snapshots')) { Get-ChildItem -LiteralPath (Join-Path $WorkRoot $taskDirectory) -File -Recurse | ForEach-Object { Add-TaskFile $_.FullName } }
foreach ($taskPackage in @('sonnetdb', 'sonnetdb.core')) {
    foreach ($taskVersion in @('4.0.0', '4.5.0-preview.ef266.5')) { Get-ChildItem -LiteralPath (Join-Path $WorkRoot "p/$taskPackage/$taskVersion") -File | ForEach-Object { Add-TaskFile $_.FullName } }
}
foreach ($taskDirectory in @('native-public4', 'native-public5', 'chloe-consumer-source')) {
    Get-ChildItem -LiteralPath (Join-Path $WorkRoot $taskDirectory) -File | ForEach-Object { Add-TaskFile $_.FullName }
    Add-TaskFile (Join-Path $WorkRoot "$taskDirectory/obj/project.assets.json")
}
foreach ($taskBinary in $taskBinaries.Values) { Add-TaskFile $taskBinary.path }
foreach ($taskRelative in @('src/SonnetDB/bin/Release/net10.0/SonnetDB.dll', 'src/SonnetDB/bin/Release/net10.0/SonnetDB.Core.dll', 'src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs', 'tests/SonnetDB.Core.Tests/Sql/SqlKeylessDmlTests.cs', 'tests/SonnetDB.Core.Tests/Sql/SqlKeylessRecoveryTests.cs', 'tests/SonnetDB.Tests/RemoteKeylessDmlTests.cs', 'tests/SonnetDB.Core.Tests/bin/Release/net10.0/SonnetDB.Core.Tests.dll', 'tests/SonnetDB.Tests/bin/Release/net10.0/win-x64/SonnetDB.Tests.dll')) { Add-TaskFile (Join-Path $SourceRoot $taskRelative) }
foreach ($taskRelative in @('src/Chloe.SonnetDB/SonnetDBContextProvider.cs', 'src/Chloe.SonnetDB/DDL/SonnetDBTableGenerator.cs')) {
    Add-TaskFile (Join-Path $WorkRoot "chloe-source/$taskRelative")
    Add-TaskFile "D:/sndb-t/i253-patch/original/$taskRelative"
}
Add-TaskFile (Join-Path $WorkRoot 'patch-verification.json')
Add-TaskFile (Join-Path $WorkRoot 'chloe-source-audit.json')
Add-TaskFile (Join-Path $WorkRoot 'native-public4/bin/Release/net10.0/KeylessNative.dll')
Add-TaskFile (Join-Path $WorkRoot 'native-public5/bin/Release/net10.0/KeylessNative.dll')
Add-TaskFile (Join-Path $WorkRoot 'chloe-candidate-source-engine/KeylessChloe.dll')
Add-TaskFile 'D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue262-work/chloe-baseline.zip'
$taskReport = [ordered]@{
    issue = 253
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceHead = 'f8bce9839e7344bd8a8095d14979b0ee24bc574b'
    stablePackage = '4.0.0+d49babae0e049ca4554d26772b92493220d42822'
    previewPackage = '4.5.0-preview.ef266.5+1af23d8f299febfdf66b040ab3419980396acf46'
    chloeHead = 'bb7ec8735bd4511c35453eb009f70cc606795703'
    sourceChanges = 0
    closure = 'OPEN: existing #243 source works; both tested public versions still reject keyless writes; local Chloe candidate is not upstream delivery'
    sourceBoundary = 'Positive native/Chloe runs use source Core/Data. Real REST/h2c use source Server. No released Server acceptance is claimed.'
    returningBoundary = 'Raw SQL through Chloe Session verifies INSERT/UPDATE/DELETE RETURNING. Entity Insert uses existing API; keyless expression Insert returns null identity. No new ORM RETURNING API.'
    restartBoundary = 'Each reopen is a new consumer process; remote source Server is killed after completed requests and restarted with same data root/new PID. This is not journal-stage fault injection.'
    retainedFailures = @('Original public packages: 3 PASS / 4 keyless write FAIL each', 'Original Chloe provider with source engine: 9 PASS / 11 guard FAIL per protocol', 'Initial archive root assumption caused missing-project build; corrected without deleting evidence', 'Initial source Server command wrapper reported a generic error during process observation and did not record a build exit code; controlled retry passed', 'Initial consumer build omitted required table arguments on three InsertRange calls; corrected, final build zero warnings/errors')
    upstreamWarnings = 'Original and candidate Chloe builds each report 38 existing warnings; none suppressed'
    runs = @($taskRuns)
    serverSessions = @($taskServers)
    actualRequests = @($taskRequests)
    sourceTests = @($taskTests)
    publicAssets = @($taskAssets)
    executedBinaries = @($taskBinaries.Values | Sort-Object path)
    evidence = @($taskFiles.Values | Sort-Object path)
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), ($taskReport | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "Verified $($taskRuns.Count) consumer processes, $($taskBinaries.Count) executed assemblies, $($taskFiles.Count) evidence files."
