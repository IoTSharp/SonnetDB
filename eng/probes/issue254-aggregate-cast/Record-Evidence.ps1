[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$taskLogs = Join-Path $WorkRoot 'artifacts/checks'
$taskRuns = [Collections.Generic.List[object]]::new()
$taskBinaries = [Collections.Generic.Dictionary[string, object]]::new()
foreach ($taskPhase in @('native-public-final', 'chloe-original-final', 'chloe-candidate-final')) {
    foreach ($taskProtocol in @('embedded', 'rest', 'frame-http2')) {
        $taskLabel = "$taskPhase-$taskProtocol"
        $taskReceipt = Get-Content -LiteralPath (Join-Path $taskLogs "$taskLabel.json") -Raw -Encoding utf8 | ConvertFrom-Json
        $taskLines = [IO.File]::ReadAllLines((Join-Path $taskLogs "$taskLabel.stdout.log"))
        $taskPass = @($taskLines | Where-Object { $_.StartsWith('PASS|') }).Count
        $taskFail = @($taskLines | Where-Object { $_.StartsWith('FAIL|') })
        if ($taskReceipt.timedOut -or $taskReceipt.failure -or $taskReceipt.remainingRecordedProcesses.Count -gt 0) { throw "Invalid process boundary: $taskLabel" }
        $taskExpectedPass = if ($taskPhase -eq 'chloe-original-final') { 4 } elseif ($taskPhase -eq 'chloe-candidate-final') { 16 } elseif ($taskProtocol -eq 'embedded') { 13 } else { 14 }
        $taskExpectedFail = if ($taskPhase -eq 'chloe-original-final') { 12 } elseif ($taskPhase -eq 'native-public-final' -and $taskProtocol -eq 'embedded') { 1 } else { 0 }
        $taskExpectedExit = if ($taskExpectedFail -eq 0) { 0 } else { 1 }
        if ($taskPass -ne $taskExpectedPass -or $taskFail.Count -ne $taskExpectedFail -or $taskReceipt.exitCode -ne $taskExpectedExit) { throw "Unexpected case count: $taskLabel" }
        if ($taskPhase -eq 'chloe-original-final' -and @($taskFail | Where-Object { $_ -notmatch 'NotSupportedException\|SonnetDB does not support CAST around an aggregate expression' }).Count -gt 0) { throw 'Unexpected original provider failure.' }
        if ($taskPhase -eq 'native-public-final' -and $taskProtocol -eq 'embedded' -and $taskFail[0] -notmatch 'grouped-having-cast.*Declared CLR types differ') { throw 'Unexpected public native failure.' }
        foreach ($taskLine in $taskLines | Where-Object { $_.StartsWith('ASSEMBLY|') }) {
            $taskParts = $taskLine.Split('|')
            $taskHash = (Get-FileHash -LiteralPath $taskParts[4] -Algorithm SHA256).Hash
            if ($taskHash -ne $taskParts[3]) { throw "Executed assembly changed: $($taskParts[4])" }
            $taskBinaries[$taskParts[4]] = [ordered]@{ path = $taskParts[4]; sha256 = $taskHash; informationalVersion = $taskParts[2] }
        }
        $taskRuns.Add([ordered]@{ label = $taskLabel; protocol = $taskProtocol; passed = $taskPass; failed = $taskFail.Count; exitCode = $taskReceipt.exitCode; failures = $taskFail })
    }
}
$taskSourceOutput = [IO.File]::ReadAllText((Join-Path $taskLogs 'native-source-embedded-final.stdout.log'))
if ($taskSourceOutput -notmatch 'COMPLETE\|embedded\|passed=14\|failed=0') { throw 'Source embedded supplement failed.' }
foreach ($taskLine in $taskSourceOutput.Split("`n") | Where-Object { $_.StartsWith('ASSEMBLY|') }) {
    $taskParts = $taskLine.TrimEnd("`r").Split('|')
    $taskHash = (Get-FileHash -LiteralPath $taskParts[4] -Algorithm SHA256).Hash
    if ($taskHash -ne $taskParts[3]) { throw 'Source supplement assembly changed.' }
    $taskBinaries[$taskParts[4]] = [ordered]@{ path = $taskParts[4]; sha256 = $taskHash; informationalVersion = $taskParts[2] }
}
foreach ($taskName in @('AggregateCastChloe.dll', 'Chloe.dll', 'SonnetDB.Core.dll', 'SonnetDB.Data.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $WorkRoot "chloe-original-final/$taskName")).Hash -ne (Get-FileHash -LiteralPath (Join-Path $WorkRoot "chloe-candidate-final/$taskName")).Hash) { throw "Non-provider binary changed: $taskName" }
}
$taskAssets = [Collections.Generic.List[object]]::new()
foreach ($taskProject in @('native-source', 'chloe-consumer-source')) {
    $taskAssetPath = Join-Path $WorkRoot "$taskProject/obj/project.assets.json"
    $taskAsset = Get-Content -LiteralPath $taskAssetPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
    if (@($taskAsset.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -gt 0) { throw 'Public consumer contains ProjectReference.' }
    foreach ($taskPackage in @('SonnetDB', 'SonnetDB.Core')) {
        if (-not $taskAsset.libraries.ContainsKey("$taskPackage/4.5.0-preview.ef266.5")) { throw 'Fixed public package missing.' }
    }
    $taskAssets.Add([ordered]@{ path = $taskAssetPath; sha256 = (Get-FileHash -LiteralPath $taskAssetPath).Hash; projectReferenceCount = 0; version = '4.5.0-preview.ef266.5' })
}
$taskRequests = [Collections.Generic.List[object]]::new()
foreach ($taskPhase in @('native-public-final', 'chloe-original-final', 'chloe-candidate-final')) {
    $taskMatrix = Get-Content -LiteralPath (Join-Path $taskLogs "$taskPhase-matrix.json") -Raw -Encoding utf8 | ConvertFrom-Json
    if (-not $taskMatrix.serverExited) { throw 'Owned server did not exit.' }
    $taskObserved = @(Get-Content -LiteralPath (Join-Path $taskLogs "$taskPhase-server.stdout.log") -Encoding utf8 | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.Category -eq 'Microsoft.AspNetCore.Hosting.Diagnostics' -and $_.EventId -eq 1 })
    $taskRest = @($taskObserved | Where-Object { $_.State.Protocol -eq 'HTTP/1.1' -and $_.State.Path -match '/v1/db/.+/sql' -and $_.State.Host -eq "127.0.0.1:$($taskMatrix.httpPort)" })
    $taskH2 = @($taskObserved | Where-Object { $_.State.Protocol -eq 'HTTP/2' -and $_.State.Path -eq '/v1/frame' -and $_.State.Host -eq "127.0.0.1:$($taskMatrix.h2Port)" })
    if ($taskRest.Count -eq 0 -or $taskH2.Count -eq 0) { throw 'Actual REST/h2c request evidence missing.' }
    $taskRequests.Add([ordered]@{ phase = $taskPhase; restSqlRequests = $taskRest.Count; h2FrameRequests = $taskH2.Count; httpPort = $taskMatrix.httpPort; h2Port = $taskMatrix.h2Port; serverExited = $taskMatrix.serverExited })
}
$taskTests = [Collections.Generic.List[object]]::new()
foreach ($taskName in @('aggregate-cast-core.trx', 'aggregate-cast-remote.trx', 'aggregate-cast-remote-final.trx')) {
    $taskPath = Join-Path $WorkRoot "test-results/$taskName"
    [xml]$taskTrx = Get-Content -LiteralPath $taskPath -Raw -Encoding utf8
    $taskCounter = $taskTrx.TestRun.ResultSummary.Counters
    $taskTests.Add([ordered]@{ path = $taskPath; total = [int]$taskCounter.total; passed = [int]$taskCounter.passed; failed = [int]$taskCounter.failed; notExecuted = [int]$taskCounter.notExecuted })
}
$taskFiles = [Collections.Generic.Dictionary[string, object]]::new()
function Add-TaskFile([string]$Path) {
    $taskItem = Get-Item -LiteralPath $Path
    $taskFiles[$taskItem.FullName] = [ordered]@{ path = $taskItem.FullName; bytes = $taskItem.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
foreach ($taskDirectory in @('artifacts/checks', 'test-results', 'source-snapshots')) {
    Get-ChildItem -LiteralPath (Join-Path $WorkRoot $taskDirectory) -File -Recurse | ForEach-Object { Add-TaskFile $_.FullName }
}
foreach ($taskPackage in @('sonnetdb', 'sonnetdb.core')) {
    Get-ChildItem -LiteralPath (Join-Path $WorkRoot "p/$taskPackage/4.5.0-preview.ef266.5") -File | ForEach-Object { Add-TaskFile $_.FullName }
}
foreach ($taskProject in @('native-source', 'chloe-consumer-source')) {
    Get-ChildItem -LiteralPath (Join-Path $WorkRoot $taskProject) -File | ForEach-Object { Add-TaskFile $_.FullName }
    Add-TaskFile (Join-Path $WorkRoot "$taskProject/obj/project.assets.json")
}
foreach ($taskBinary in $taskBinaries.Values) { Add-TaskFile $taskBinary.path }
foreach ($taskRelative in @('src/SonnetDB/bin/Release/net10.0/SonnetDB.dll', 'src/SonnetDB/bin/Release/net10.0/SonnetDB.Core.dll', 'src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs', 'src/SonnetDB.Data/Remote/RemoteExecutionResult.cs', 'tests/SonnetDB.Core.Tests/Sql/SqlAggregateCastTests.cs', 'tests/SonnetDB.Tests/RemoteAdoHttp2TransportTests.cs', 'tests/SonnetDB.Core.Tests/bin/Release/net10.0/SonnetDB.Core.Tests.dll', 'tests/SonnetDB.Tests/bin/Release/net10.0/win-x64/SonnetDB.Tests.dll')) { Add-TaskFile (Join-Path $SourceRoot $taskRelative) }
Add-TaskFile (Join-Path $WorkRoot 'chloe-original-final/AggregateCastChloe.dll')
Add-TaskFile (Join-Path $WorkRoot 'native-source/bin/Release/net10.0/AggregateCastNative.dll')
Add-TaskFile (Join-Path $WorkRoot 'patch-verification.json')
Add-TaskFile (Join-Path $WorkRoot 'chloe-source/src/Chloe.SonnetDB/SqlGenerator.cs')
Add-TaskFile 'D:/sndb-t/i254-patch/original/src/Chloe.SonnetDB/SqlGenerator.cs'
$taskReport = [ordered]@{
    issue = 254
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceHead = '0a51102cf4e1b203565d6b02af7d5fb4c73513a8'
    publicVersion = '4.5.0-preview.ef266.5'
    publicSourceRevision = '1af23d8f299febfdf66b040ab3419980396acf46'
    chloeHead = 'bb7ec8735bd4511c35453eb009f70cc606795703'
    closure = 'OPEN: formal stable release and upstream Chloe delivery remain unverified'
    nativePublicEmbeddedBoundary = '13 PASS / 1 FAIL: concat metadata is Object in .ef266.5; the adjacent #261 declaration fix exists only in current source'
    sourceEmbeddedSupplement = 'Same native consumer with current source Core/Data: 14 PASS / 0 FAIL'
    sourceServerBoundary = 'Real REST/h2c use current source Server, not a released Server binary'
    originalChloeBuildBoundary = 'Archive build initially inherited ancestor revision; final build explicitly pins SourceRevisionId. Original Chloe rebuild has 38 upstream warnings; no warning suppression.'
    retainedFailureBoundary = 'Initial remote xUnit run with network proxy: 1 PASS / 1 h2c connection FAIL. Same DLL without proxy: 2 PASS.'
    runs = @($taskRuns)
    actualRequests = @($taskRequests)
    tests = @($taskTests)
    publicAssets = @($taskAssets)
    executedBinaries = @($taskBinaries.Values | Sort-Object path)
    evidence = @($taskFiles.Values | Sort-Object path)
}
$taskJson = ($taskReport | ConvertTo-Json -Depth 9).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $taskJson, [Text.UTF8Encoding]::new($false))
Write-Output "Verified $($taskRuns.Count) public consumer runs, $($taskBinaries.Count) executed assemblies, $($taskFiles.Count) evidence files."
