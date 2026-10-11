param([Parameter(Mandatory)][string]$PackageRoot,[Parameter(Mandatory)][object[]]$Dependencies,[int]$DeadlineSeconds=30)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$watch=[Diagnostics.Stopwatch]::StartNew()
$root=Join-Path $PSScriptRoot 'licenses-microsoft'
$manifest=Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw|ConvertFrom-Json -DateKind String
if($manifest.schemaVersion -ne 1 -or $manifest.kind -cne 'pinned-microsoft-distribution-originals' -or $manifest.originals.Count -ne 3 -or $manifest.publisher.githubUser -cne 'maikebing' -or $manifest.publisher.edition -cne 'Visual Studio Community 2026' -or $manifest.publisher.qualificationKind -cne 'USER_REPORTED_VALID_LICENSE_EDITION'){throw 'Microsoft original/qualification inventory mismatch'}
$provenance=@()
foreach($original in $manifest.originals){
 if($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds -or (Test-Path -LiteralPath (Join-Path $PackageRoot 'cancel'))){throw 'Microsoft notice deadline/cancel'}
 if($original.file -cnotmatch '^[a-zA-Z0-9_.-]+\.(docx|html|txt)$' -or $original.storageFile -cne ($original.file+'.gz') -or $original.size -lt 1 -or $original.size -gt 1MB){throw 'Unsafe Microsoft original'}
 $stored=Join-Path $root $original.storageFile
 if((Get-FileHash -LiteralPath $stored).Hash.ToLowerInvariant() -cne $original.storageSha256){throw 'Microsoft compressed original drift'}
 $input=[IO.File]::OpenRead($stored);$gzip=[IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress);$output=[IO.MemoryStream]::new()
 try{
  $buffer=[byte[]]::new(65536);$ended=$false
  for($round=0;$round -lt 18 -and $watch.Elapsed.TotalSeconds -lt $DeadlineSeconds;$round++){
   $count=$gzip.Read($buffer,0,$buffer.Length);if($count -eq 0){$ended=$true;break}
   $output.Write($buffer,0,$count);if($output.Length -gt 1MB){throw 'Microsoft decompression budget'}
  }
  if(!$ended){throw 'Microsoft decompression did not terminate'}
  $bytes=$output.ToArray()
 }finally{$output.Dispose();$gzip.Dispose();$input.Dispose()}
 if($bytes.Length -ne $original.size -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $original.sha256){throw 'Microsoft original hash/size drift'}
 $relative='licenses/microsoft/'+$original.file;$target=Join-Path $PackageRoot $relative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
 if(Test-Path -LiteralPath $target){throw 'Existing Microsoft original output'}
 [IO.File]::WriteAllBytes($target,$bytes)
 $provenance+=@{file=$relative;size=$bytes.Length;sha256=$original.sha256;source=$original.source}
}
$runtimes=@($Dependencies|Where-Object ecosystem -CEQ 'microsoft-vc-runtime')
if($runtimes.Count -ne 4 -or @($runtimes.id|Sort-Object -Unique).Count -ne 4){throw 'Exact four runtime dependencies required'}
foreach($runtime in $runtimes){
 if($runtime.id -cnotin $manifest.runtimeNames -or $runtime.source.Replace('\','/') -notmatch '/VC/Redist/MSVC/[0-9.]+/x64/Microsoft\.VC[0-9]+\.CRT/' -or (Get-FileHash -LiteralPath (Join-Path $PackageRoot $runtime.id)).Hash.ToLowerInvariant() -cne $runtime.sha256){throw 'Microsoft runtime name/source/hash mismatch'}
 $runtime.includedLicenseFiles=@($runtime.includedLicenseFiles)+@($provenance.file)+@('MICROSOFT-REDISTRIBUTION-TERMS.md','microsoft-license-provenance.json')
 $runtime.licenseEvidence='ORIGINAL_TERMS_QUALIFICATION_AND_DOWNSTREAM_PROTECTION'
}
if(!(Test-Path -LiteralPath (Join-Path $PackageRoot 'MICROSOFT-REDISTRIBUTION-TERMS.md'))){throw 'Downstream protection terms absent'}
@{schemaVersion=1;publisher=$manifest.publisher;originals=$provenance;runtimeNames=$manifest.runtimeNames;downstreamTerms=@{file='MICROSOFT-REDISTRIBUTION-TERMS.md';sha256=(Get-FileHash -LiteralPath (Join-Path $PackageRoot 'MICROSOFT-REDISTRIBUTION-TERMS.md')).Hash.ToLowerInvariant()};acceptance='Start-Preview requires -AcceptThirdPartyTerms before execution; VerifyOnly does not run Server or accept';boundary=$manifest.boundary}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $PackageRoot 'microsoft-license-provenance.json') -Encoding utf8
