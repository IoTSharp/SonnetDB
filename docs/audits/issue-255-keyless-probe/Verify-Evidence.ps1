param(
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputFile) {throw 'OutputFile must be new.'}
$verified=[Collections.Generic.List[object]]::new()
function Check([string]$Base,$Files,[string]$Category) {
    foreach($file in $Files) {
        $path=Join-Path $Base $file.path
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {throw ('SHA mismatch: '+$path)}
        $verified.Add([ordered]@{category=$Category;path=$path;sha256=$file.sha256})
    }
}
$source=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'source-01/source-manifest.json') -Raw -Encoding utf8|ConvertFrom-Json
if($source.failure -or $source.sourceCommit -ne 'b157dddc2886127e2deddb22a75555a3883e687d') {throw 'Unexpected source build.'}
Check $SourceRoot $source.sources 'source'
Check (Join-Path $EvidenceRoot 'source-01') $source.files 'source-snapshot'
foreach($run in @('matrix-05','matrix-07-strong-baseline','matrix-09-final','matrix-10-callback-baseline','matrix-11-callback-final')) {
    $root=Join-Path $EvidenceRoot $run
    $receipt=Get-Content -LiteralPath (Join-Path $root 'receipt.json') -Raw -Encoding utf8|ConvertFrom-Json
    Check $root $receipt.files $run
}
$baseline=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'matrix-05/receipt.json') -Raw -Encoding utf8|ConvertFrom-Json
$public=@($baseline.commands|Where-Object label -match '^(stable|preview)-(Native|XCode)-negative$')
if($public.Count -ne 4 -or @($public|Where-Object {$_.exitCode -ne 0 -or $_.timedOut}).Count) {throw 'Public negative matrix is incomplete.'}
$strong=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'matrix-07-strong-baseline/receipt.json') -Raw -Encoding utf8|ConvertFrom-Json
if(-not $strong.failure -or @($strong.commands|Where-Object {$_.label -eq 'XCode-embedded-initial' -and $_.exitCode -eq 1}).Count -ne 1) {throw 'Strong pre-fix failure is missing.'}
$callback=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'matrix-10-callback-baseline/receipt.json') -Raw -Encoding utf8|ConvertFrom-Json
if(-not $callback.failure -or @($callback.commands|Where-Object {$_.label -eq 'XCode-embedded-initial' -and $_.exitCode -eq 1}).Count -ne 1) {throw 'Callback source regression baseline is missing.'}
if(-not (Select-String -LiteralPath (Join-Path $EvidenceRoot 'matrix-10-callback-baseline/XCode-embedded-initial.stderr.log') -SimpleMatch 'custom-paging-uses-alternate-dal')) {throw 'Expected callback source regression was not observed.'}
$final=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'matrix-11-callback-final/receipt.json') -Raw -Encoding utf8|ConvertFrom-Json
if($final.failure -or @($final.commands|Where-Object {$_.exitCode -ne 0 -or $_.timedOut}).Count) {throw 'Final matrix failed.'}
$positive=@($final.commands|Where-Object label -match '^(core-(initial|reopen)|(Native|XCode)-(embedded|rest|frame-http2)-(initial|reopen))$')
if($positive.Count -ne 14) {throw 'Final process count differs.'}
foreach($protocol in @('embedded','rest','frame-http2')) {
    $log=Join-Path $EvidenceRoot ('matrix-11-callback-final/XCode-'+$protocol+'-initial.stdout.log')
    foreach($assertion in @('custom-paging-uses-alternate-dal','custom-paging-original-row-total-count-contract','null-callback-result-default-multipage','constructor-default-multipage','forward-preserves-typed-null','backward-preserves-typed-null')) {
        if(-not (Select-String -LiteralPath $log -SimpleMatch ('PASS|'+$assertion))) {throw ('Missing final callback/default assertion: '+$assertion)}
    }
}
$models=@(Get-ChildItem -LiteralPath $RuntimeDirectory -Filter '*.xml' -Recurse|ForEach-Object {[ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
$xcodeRoot=Join-Path $EvidenceRoot 'matrix-11-callback-final/xcode-source'
$candidate=@(foreach($path in @('XCode/DataAccessLayer/Common/DbSession.cs','XCode/DataAccessLayer/DAL_DbOperate.cs','XCode/DataAccessLayer/DbPackage.cs','XCode/DataAccessLayer/Database/SonnetDB.cs','XCode/DataAccessLayer/Model/ModelHelper.cs')) {[ordered]@{path=(Join-Path $xcodeRoot $path);sha256=(Get-FileHash -LiteralPath (Join-Path $xcodeRoot $path) -Algorithm SHA256).Hash}})
$result=[ordered]@{issue=255;utc=[DateTime]::UtcNow.ToString('o');verifiedFiles=$verified.Count;publicNegativeProcesses=4;sourcePositiveProcesses=14;originalXmlBaselineProcesses=1;modelFiles=$models;candidateSources=$candidate;verified=$verified.ToArray()}
[IO.File]::WriteAllText($OutputFile,($result|ConvertTo-Json -Depth 8).Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
Write-Host ('Verified '+$verified.Count+' files; public negative=4; source positive=14; original XML baseline=1.')
