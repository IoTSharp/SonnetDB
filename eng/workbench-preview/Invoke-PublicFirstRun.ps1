param([Parameter(Mandatory)][string]$Seal,[Parameter(Mandatory)][string]$DownloadRoot,[Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -lt 7 -or !$IsWindows){throw 'Windows PowerShell 7 required'}
$download=[IO.Path]::GetFullPath($DownloadRoot);$root=[IO.Path]::GetFullPath($RunRoot)
if(Test-Path -LiteralPath $root){throw 'Fresh owned first-run directory required'}
$proof=Get-Content -LiteralPath (Join-Path $download 'public-download.json') -Raw|ConvertFrom-Json -DateKind String
$identity=Get-Content -LiteralPath $Seal -Raw|ConvertFrom-Json -DateKind String
if($proof.status -cne 'PUBLIC_DOWNLOAD_AND_ARCHIVE_VERIFIED' -or $proof.source -cne 'anonymous-public-http' -or $proof.candidate -cne $identity.candidate -or $proof.version -cne $identity.version -or $proof.sourceCommit -cne $identity.sourceCommit){throw 'Anonymous public download evidence required'}
$null=New-Item -ItemType Directory -Path $root
$marker=Join-Path $root '.owned-m47-p08';[IO.File]::WriteAllText($marker,[guid]::NewGuid().ToString('N'))
$watch=[Diagnostics.Stopwatch]::StartNew();$result=[ordered]@{status='RUNNING';source='anonymous-public-download-installed-on-recorded-Windows-host';candidate=$identity.candidate;version=$identity.version;sourceCommit=$identity.sourceCommit;firstRun='NOT_RUN';startUtc=[DateTimeOffset]::UtcNow.ToString('o');temporaryFilesCleaned=$false}
$previousPassword=$env:P05_TEST_PASSWORD
try{
 $node=(Get-Command node -CommandType Application|Select-Object -First 1).Source
 & $node (Join-Path $PSScriptRoot 'public-readback.mjs') ([IO.Path]::GetFullPath($Seal)) $download (Join-Path $download 'delivery') (Join-Path $root 'reverified-public.json')
 if($LASTEXITCODE -ne 0){throw 'Fresh public byte verification failed'}
 & (Join-Path $PSScriptRoot 'Verify-Distribution.ps1') -Seal $Seal -Delivery (Join-Path $download 'delivery')
 $zip=@($identity.attachments|Where-Object path -Like '*-win-x64.zip')
 if($zip.Count -ne 1){throw 'Exactly one frozen native ZIP required'}
 Expand-Archive -LiteralPath (Join-Path $download ('delivery/'+$zip[0].path)) -DestinationPath (Join-Path $root 'package')
 $env:P05_TEST_PASSWORD='P08:'+([guid]::NewGuid().ToString('N'))+'!'
 & (Join-Path $PSScriptRoot 'Invoke-Journey.ps1') -RunRoot $root -Phase first
 $journey=Get-Content -LiteralPath (Join-Path $root 'first/result.json') -Raw|ConvertFrom-Json -DateKind String
 if($journey.status -cne 'PASS' -or $journey.normalStop -ne $true -or $journey.exitCode -ne 0 -or $journey.cleanupComplete -ne $true){throw 'Public-download actual first run failed'}
 $result.firstRun='PASS_REAL_UI_R1_ON_RECORDED_HOST';$result.journeySha256=(Get-FileHash -LiteralPath (Join-Path $root 'first/first-journey.json')).Hash.ToLowerInvariant()
 $result.normalStop=$true;$result.cleanupComplete=$true;$result.status='PUBLIC_FIRST_RUN_PASS'
}catch{$result.status='FAIL';$result.error=$_.Exception.Message}
finally{
 $env:P05_TEST_PASSWORD=$previousPassword
 try{
  $processReceipt=Join-Path $root 'first/result.json'
  $mayClean=!(Test-Path -LiteralPath $processReceipt) -and !(Test-Path -LiteralPath (Join-Path $root 'first/start.json'))
  if(Test-Path -LiteralPath $processReceipt){$ended=Get-Content -LiteralPath $processReceipt -Raw|ConvertFrom-Json -DateKind String;$mayClean=$ended.cleanupComplete -eq $true}
  if(!$mayClean){throw 'Retain runtime files until exact owned process cleanup is confirmed'}
  if(!(Test-Path -LiteralPath $marker)){throw 'Owned first-run marker missing'}
  foreach($relative in @('package','data')){
   if($watch.Elapsed.TotalSeconds -gt 950){throw 'First-run cleanup time budget'}
   $target=[IO.Path]::GetFullPath((Join-Path $root $relative))
   if(!$target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup outside owned run'}
   if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force}
  }
  $result.temporaryFilesCleaned=$true
 }catch{$result.status='FAIL';$result.cleanupError=$_.Exception.Message}
 $result.endUtc=[DateTimeOffset]::UtcNow.ToString('o');$result.elapsedSeconds=$watch.Elapsed.TotalSeconds
 $result.boundary='Actual first run from anonymously downloaded package on this recorded host; synthetic data and injected faults remain classified. No clean-machine, other host, production or crash-recovery claim.'
 $result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $root 'public-first-run.json') -Encoding utf8
}
$result|ConvertTo-Json -Depth 5
if($result.status -cne 'PUBLIC_FIRST_RUN_PASS'){exit 1}
