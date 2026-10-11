param([Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$taskRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $taskRoot){throw 'Fresh output root required'};$null=New-Item -ItemType Directory -Path $taskRoot
$fixture=Join-Path $taskRoot 'license-fixture'
if(Test-Path -LiteralPath $fixture){throw 'Fresh fixture required'}
$null=New-Item -ItemType Directory -Path $fixture
$marker=Join-Path $fixture '.owned';[IO.File]::WriteAllText($marker,'M47 license contracts')
$watch=[Diagnostics.Stopwatch]::StartNew();$records=@()
try{
 foreach($mode in @('exact','modified-chunk','rendered-vdirs','missing-replacement','runtime-hash','runtime-source','runtime-duplicate')){
  if($watch.Elapsed.TotalSeconds -gt 100 -or (Test-Path -LiteralPath (Join-Path $taskRoot 'cancel'))){throw 'Fixture time/cancel budget'}
  $package=Join-Path $fixture $mode;$null=New-Item -ItemType Directory -Path $package
  Copy-Item -LiteralPath (Join-Path $repo 'web/dist') -Destination (Join-Path $package 'wwwroot') -Recurse
  Copy-Item -LiteralPath (Join-Path $repo 'eng/workbench-preview/MICROSOFT-REDISTRIBUTION-TERMS.md') -Destination $package
    $runtimeReport=@{registration=@{registeredRedistVersion='14.51.36231'};redistributed=@('msvcp140.dll','msvcp140_1.dll','vcruntime140.dll','vcruntime140_1.dll')|ForEach-Object{@{path=$_;source=('C:/LOCAL-CONTRACT/VC/Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT/'+$_);productVersion='0.0.0-contract';fileVersion='0.0.0-contract'}}}
  foreach($runtime in $runtimeReport.redistributed){
   [IO.File]::WriteAllText((Join-Path $package $runtime.path),'LOCAL CONTRACT BYTES '+$runtime.path)
   $runtime.sha256=(Get-FileHash -LiteralPath (Join-Path $package $runtime.path)).Hash.ToLowerInvariant()
  }
  switch($mode){
   'modified-chunk' {[IO.File]::AppendAllText((Join-Path $package ('wwwroot/'+(Get-Content (Join-Path $package 'wwwroot/workbench-module-inventory.json') -Raw|ConvertFrom-Json).chunks[0].path)),'invalid')}
   'runtime-hash' {$runtimeReport.redistributed[0].sha256='0'*64}
   'runtime-source' {$runtimeReport.redistributed[0].source='C:\unregistered\msvcp140.dll'}
   'runtime-duplicate' {$runtimeReport.redistributed[1]=$runtimeReport.redistributed[0]}
  }
  if($mode -in @('rendered-vdirs','missing-replacement')){
   $path=Join-Path $package 'wwwroot/workbench-module-inventory.json';$bundle=Get-Content $path -Raw|ConvertFrom-Json
   if($mode -eq 'rendered-vdirs'){$bundle.modules[0].module='node_modules/vdirs/es/index.js'}else{$bundle.modules=@($bundle.modules|Where-Object module -CNE $bundle.replacement)}
   $bundle|ConvertTo-Json -Depth 8|Set-Content $path -Encoding utf8
  }
  $runtimeReport|ConvertTo-Json -Depth 10|Set-Content (Join-Path $package 'native-dependencies.json') -Encoding utf8
  $refused=$false
  try{& (Join-Path $repo 'eng/workbench-preview/licenses.ps1') -RepoRoot $repo -PackageRoot $package -DeadlineSeconds 60}catch{if($mode -eq 'exact'){throw};$refused=$true}
  if($mode -ne 'exact' -and !$refused){throw 'Expected license refusal missing: '+$mode}
  if($mode -eq 'exact'){
   $inventory=@(Get-Content -LiteralPath (Join-Path $package 'dependency-licenses.json') -Raw|ConvertFrom-Json -AsHashtable)
   $mapbox=@($inventory|Where-Object id -CEQ '@mapbox/jsonlint-lines-primitives')[0]
   if($mapbox.version -cne '2.0.3' -or $mapbox.licenseEvidence -cne 'ORIGINAL_TEXT' -or $mapbox.includedLicenseFiles.Count -lt 1){throw 'Mapbox exact license missing'}
   $vdirs=@($inventory|Where-Object id -CEQ 'vdirs')[0]
   if($vdirs.buildOnly -ne $false -or $vdirs.redistributed -ne $false -or $vdirs.licenseEvidence -cne 'INSTALLED_NOT_EMITTED_WITH_HASHED_ROLLUP_PROOF'){throw 'vdirs emitted/exclusion boundary wrong'}
   $unresolved=@($inventory|Where-Object {$_.ecosystem -ceq 'npm' -and $_['buildOnly'] -eq $false -and $_['redistributed'] -ne $false -and @($_['includedLicenseFiles']).Count -eq 0})
   if($unresolved.Count -ne 0){throw 'Runtime npm license text still missing'}
   $proof=Get-Content (Join-Path $package 'microsoft-license-provenance.json') -Raw|ConvertFrom-Json
   if($proof.originals.Count -ne 3 -or $proof.publisher.edition -cne 'Visual Studio Community 2026'){throw 'Microsoft originals absent'}
   $inventory|ConvertTo-Json -Depth 12|Set-Content (Join-Path $taskRoot 'local-license-inventory.json') -Encoding utf8
  }
  $records+=@{mode=$mode;status=if($refused){'PASS_EXPECTED_REFUSAL'}else{'PASS'};kind='LOCAL_CONTRACT_NOT_CANDIDATE_ACCEPTANCE'}
 }
}finally{
 $resolved=[IO.Path]::GetFullPath($fixture)
 if($resolved -cne (Join-Path $taskRoot 'license-fixture') -or !(Test-Path -LiteralPath $marker)){throw 'Unverified fixture cleanup'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
 @{records=$records;fixtureReclaimed=!(Test-Path -LiteralPath $fixture);elapsedSeconds=$watch.Elapsed.TotalSeconds}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskRoot 'license-contracts.json') -Encoding utf8
}
