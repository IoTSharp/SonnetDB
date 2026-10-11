param([Parameter(Mandatory)][string]$Seal,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 required'}
$sealPath=[IO.Path]::GetFullPath($Seal)
$record=Get-Content -LiteralPath $sealPath -Raw|ConvertFrom-Json -DateKind String
if($record.repository -cne 'IoTSharp/SonnetDB' -or $record.tag -cnotmatch '^workbench-preview-1\.[1-9][0-9]{0,5}$'){throw 'Frozen repository/tag required'}
$node=(Get-Command node -CommandType Application|Select-Object -First 1).Source
& $node (Join-Path $PSScriptRoot 'public-readback.mjs') check-seal $sealPath
if($LASTEXITCODE -ne 0){throw 'Anonymous download seal preflight failed'}
$destination=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $destination){throw 'Fresh immutable public-download directory required'}
$null=New-Item -ItemType Directory -Path $destination
$delivery=Join-Path $destination 'delivery';$null=New-Item -ItemType Directory -Path $delivery
$watch=[Diagnostics.Stopwatch]::StartNew();$requests=0;$observations=[Collections.Generic.List[object]]::new()
$result=[ordered]@{status='RUNNING';source='anonymous-public-http';candidate=$record.candidate;version=$record.version;sourceCommit=$record.sourceCommit;mutationStarted=$false;firstRun='NOT_RUN';startUtc=[DateTimeOffset]::UtcNow.ToString('o')}
function Budget {
 if($script:requests -ge 48 -or $watch.Elapsed.TotalSeconds -ge 480 -or (Test-Path -LiteralPath (Join-Path $destination 'cancel'))){throw 'Anonymous request/count/wall/cancel budget'}
}
function TrustedUri([uri]$Uri){
 if($Uri.Scheme -cne 'https' -or $Uri.Port -ne 443 -or $Uri.UserInfo -or $Uri.Host -cnotin @('api.github.com','github.com','release-assets.githubusercontent.com','objects.githubusercontent.com')){throw 'Untrusted anonymous redirect/URL'}
}
function PublicError([Exception]$Exception){
 # Transport exceptions can include the full signed redirect URL. Persist only our safe reasons.
 $message=$Exception.Message
 if($message -cmatch '^Anonymous HTTP [0-9]{3}$' -or $message -cin @('Anonymous request/count/wall/cancel budget','Untrusted anonymous redirect/URL','Redirect lacks location','Anonymous content size budget','Anonymous download cancelled','Anonymous body size budget','Anonymous stream count/wall deadline','Anonymous redirect/wall budget','Anonymous download incomplete','Release ID invalid','Exactly four public attachments required','Public attachment mismatch','Public attachment URL mismatch','Anonymous byte/identity verification failed')){return $message}
 return 'Anonymous transport or verification failure; signed URL details omitted'
}
function Download([string]$Name,[string]$Url,[string]$Target,[long]$MaximumBytes,[switch]$Allow404){
 TrustedUri ([uri]$Url)
 for($attempt=0;$attempt -lt 2 -and $watch.Elapsed.TotalSeconds -lt 480;$attempt++){
  $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseCookies=$false
  $handler.UseProxy=$attempt -eq 1
  if($attempt -eq 1){$handler.Proxy=[Net.WebProxy]::new('http://127.0.0.1:7890')}
  $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(45)
  $client.DefaultRequestHeaders.UserAgent.ParseAdd('SonnetDB-M47-P08-anonymous-readback')
  $partial=Join-Path $destination ("$Name-attempt-$attempt.partial")
  $current=[uri]$Url;$completed=$false
  try{
   for($redirect=0;$redirect -lt 6 -and $watch.Elapsed.TotalSeconds -lt 480;$redirect++){
    Budget;TrustedUri $current;$script:requests++
    $token=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds([Math]::Min(45,480-$watch.Elapsed.TotalSeconds)))
    $response=$null
    try{
     $response=$client.GetAsync($current,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$token.Token).GetAwaiter().GetResult()
     $status=[int]$response.StatusCode
     # Signed redirect query strings are temporary credentials; retain host/path only.
     $observations.Add(@{name=$Name;attempt=$attempt;redirect=$redirect;uri=$current.GetLeftPart([UriPartial]::Path);status=$status;anonymous=$true})
     if($status -eq 404 -and $Allow404){[IO.File]::WriteAllText($Target,'null');$completed=$true;return}
     if($status -in @(301,302,303,307,308)){
      if($null -eq $response.Headers.Location){throw 'Redirect lacks location'}
      $current=[uri]::new($current,$response.Headers.Location);TrustedUri $current;continue
     }
     if($status -ne 200){throw "Anonymous HTTP $status"}
     if($response.Content.Headers.ContentLength -gt $MaximumBytes){throw 'Anonymous content size budget'}
     $inputStream=$response.Content.ReadAsStreamAsync($token.Token).GetAwaiter().GetResult()
     $outputStream=[IO.File]::Open($partial,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
     try{
      $buffer=[byte[]]::new(65536);$total=0L
      # ReadAsync may return a partial buffer. Bound successful reads separately from the byte limit.
      $maxReads=[int][Math]::Min(131072,$MaximumBytes+1)
      for($read=0;$read -lt $maxReads -and $watch.Elapsed.TotalSeconds -lt 480;$read++){
       if(Test-Path -LiteralPath (Join-Path $destination 'cancel')){throw 'Anonymous download cancelled'}
       $size=$inputStream.ReadAsync($buffer,0,$buffer.Length,$token.Token).GetAwaiter().GetResult()
       if($size -eq 0){$completed=$true;break}
       $total+=$size;if($total -gt $MaximumBytes){throw 'Anonymous body size budget'}
       $outputStream.Write($buffer,0,$size)
      }
      if(!$completed){throw 'Anonymous stream count/wall deadline'}
     }finally{$outputStream.Dispose();$inputStream.Dispose()}
     $observations.Add(@{name=$Name;size=(Get-Item -LiteralPath $partial).Length;sha256=(Get-FileHash -LiteralPath $partial).Hash.ToLowerInvariant();anonymous=$true})
     Move-Item -LiteralPath $partial -Destination $Target -ErrorAction Stop
     return
    }finally{if($null -ne $response){$response.Dispose()};$token.Dispose()}
   }
   throw 'Anonymous redirect/wall budget'
  }catch{
   $observations.Add(@{name=$Name;attempt=$attempt;error=(PublicError $_.Exception);anonymous=$true})
   if($attempt -eq 1){throw}
   Start-Sleep -Milliseconds 500
  }finally{$client.Dispose();$handler.Dispose()}
 }
 throw 'Anonymous download incomplete'
}
try{
 $api='https://api.github.com/repos/'+$record.repository
 Download 'release' "$api/releases/tags/$($record.tag)" (Join-Path $destination 'release.json') 4MB
 $release=Get-Content -LiteralPath (Join-Path $destination 'release.json') -Raw|ConvertFrom-Json -DateKind String
 if($release.id -isnot [long] -and $release.id -isnot [int]){throw 'Release ID invalid'}
 Download 'assets' "$api/releases/$($release.id)/assets?per_page=100" (Join-Path $destination 'assets.json') 4MB
 Download 'tag' "$api/git/ref/tags/$($record.tag)" (Join-Path $destination 'tag.json') 1MB
 Download 'latest' "$api/releases/latest" (Join-Path $destination 'latest.json') 1MB -Allow404
 Download 'page' "https://github.com/$($record.repository)/releases/tag/$($record.tag)" (Join-Path $destination 'public-page.html') 4MB
 $assets=@(Get-Content -LiteralPath (Join-Path $destination 'assets.json') -Raw|ConvertFrom-Json -DateKind String)
 if($assets.Count -ne 4 -or $record.attachments.Count -ne 4){throw 'Exactly four public attachments required'}
 foreach($asset in $assets){
  $expected=@($record.attachments|Where-Object path -CEQ $asset.name)
  if($expected.Count -ne 1 -or $asset.size -ne $expected[0].size -or $asset.state -cne 'uploaded' -or $asset.id -isnot [ValueType] -or $asset.id -le 0 -or $asset.id -ne [long]$asset.id){throw 'Public attachment mismatch'}
  $url="https://github.com/$($record.repository)/releases/download/$($record.tag)/$($expected[0].path)"
  if($asset.browser_download_url -cne $url){throw 'Public attachment URL mismatch'}
  Download ('asset-'+$asset.id) $url (Join-Path $delivery $expected[0].path) $expected[0].size
 }
 & $node (Join-Path $PSScriptRoot 'public-readback.mjs') $sealPath $destination $delivery (Join-Path $destination 'verified-public.json')
 if($LASTEXITCODE -ne 0){throw 'Anonymous byte/identity verification failed'}
 & (Join-Path $PSScriptRoot 'Verify-Distribution.ps1') -Seal $sealPath -Delivery $delivery
 $result.status='PUBLIC_DOWNLOAD_AND_ARCHIVE_VERIFIED';$result.releaseId=$release.id
}catch{$result.status='REFUSED';$result.error=PublicError $_.Exception}
finally{
 $result.endUtc=[DateTimeOffset]::UtcNow.ToString('o');$result.elapsedSeconds=$watch.Elapsed.TotalSeconds;$result.requests=$requests
 $result.observations=$observations.ToArray();$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $destination 'public-download.json') -Encoding utf8
}
$result|Select-Object status,candidate,version,sourceCommit,firstRun,requests,elapsedSeconds|ConvertTo-Json
if($result.status -cne 'PUBLIC_DOWNLOAD_AND_ARCHIVE_VERIFIED'){exit 1}
