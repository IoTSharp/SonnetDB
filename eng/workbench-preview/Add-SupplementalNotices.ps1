param([Parameter(Mandatory)][string]$PackageRoot,[Parameter(Mandatory)][object[]]$Dependencies,[int]$DeadlineSeconds=30)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 required'}
$watch=[Diagnostics.Stopwatch]::StartNew()
$root=Join-Path $PSScriptRoot 'licenses-supplemental'
$manifest=Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw|ConvertFrom-Json -DateKind String
if($manifest.schemaVersion -ne 1 -or $manifest.kind -cne 'pinned-original-npm-notices' -or $manifest.originals.Count -gt 16 -or $manifest.dependencies.Count -gt 16){throw 'Invalid supplemental original inventory'}
$provenance=[Collections.Generic.List[object]]::new()
foreach($dependency in $manifest.dependencies){
 if($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds -or (Test-Path -LiteralPath (Join-Path $PackageRoot 'cancel'))){throw 'Supplemental notice budget/cancel'}
 $dependencyMatches=@($Dependencies|Where-Object {$_.ecosystem -ceq 'npm' -and $_.id -ceq $dependency.id -and $_.version -ceq $dependency.version})
 if($dependencyMatches.Count -ne 1 -or $dependencyMatches[0].integrity -cne $dependency.integrity -or $dependencyMatches[0].buildOnly -ne $false){throw 'Supplemental notice exact installed dependency/integrity mismatch'}
 $original=@($manifest.originals|Where-Object file -CEQ $dependency.originalFile)
 if($original.Count -ne 1){throw 'Supplemental original missing/duplicate'}
 $original=$original[0]
 if($original.file -cnotmatch '^[a-zA-Z0-9_.-]+\.(txt|md)$' -or $original.storageFile -cne ($original.file+'.gz') -or $original.encoding -cne 'gzip' -or $original.size -lt 1 -or $original.size -gt 1MB){throw 'Unsafe supplemental original path/encoding/size'}
 $stored=Join-Path $root $original.storageFile
 if((Get-FileHash -LiteralPath $stored).Hash.ToLowerInvariant() -cne $original.storageSha256){throw 'Supplemental compressed original drift'}
 $stream=[IO.File]::OpenRead($stored);$gzip=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress)
 $memory=[IO.MemoryStream]::new()
 try{
  $buffer=[byte[]]::new(65536);$ended=$false
  for($read=0;$read -lt 18 -and $watch.Elapsed.TotalSeconds -lt $DeadlineSeconds;$read++){
   $count=$gzip.Read($buffer,0,$buffer.Length);if($count -eq 0){$ended=$true;break}
   $memory.Write($buffer,0,$count);if($memory.Length -gt 1MB){throw 'Supplemental decompression budget'}
  }
  if(!$ended){throw 'Supplemental decompression did not terminate'}
  $bytes=$memory.ToArray()
 }finally{$memory.Dispose();$gzip.Dispose();$stream.Dispose()}
 if($bytes.Length -ne $original.size -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $original.sha256){throw 'Supplemental original SHA/size mismatch'}
 $relative='licenses/npm-supplemental/'+$original.file
 $output=Join-Path $PackageRoot $relative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force
 if(Test-Path -LiteralPath $output){
  if((Get-FileHash -LiteralPath $output).Hash.ToLowerInvariant() -cne $original.sha256){throw 'Existing supplemental notice mismatch'}
 }else{[IO.File]::WriteAllBytes($output,$bytes)}
 $dependencyMatches[0].includedLicenseFiles=@($dependencyMatches[0].includedLicenseFiles)+@($relative)
 $dependencyMatches[0].licenseEvidence='ORIGINAL_TEXT_WITH_PINNED_SUPPLEMENT'
 $provenance.Add(@{id=$dependency.id;version=$dependency.version;integrity=$dependency.integrity;gitHead=$dependency.gitHead;source=$dependency.source;originalPath=$relative;sha256=$original.sha256;evidence=$dependency.evidence})
}
$provenance.ToArray()|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $PackageRoot 'licenses/npm-supplemental/provenance.json') -Encoding utf8
