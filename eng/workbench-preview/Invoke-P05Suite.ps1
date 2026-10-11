param([Parameter(Mandatory)][string]$DeliveryRoot, [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$LegacyZip)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 required.'}
$root=[IO.Path]::GetFullPath($RunRoot)
$delivery=[IO.Path]::GetFullPath($DeliveryRoot)
if(Test-Path -LiteralPath $root){throw 'Fresh isolated suite root required.'}
$null=New-Item -ItemType Directory -Path $root
[IO.File]::WriteAllText((Join-Path $root '.owned-m47-p05'),'M47-P05 '+[guid]::NewGuid())
$watch=[Diagnostics.Stopwatch]::StartNew();$commands=0
$results=[Collections.Generic.List[object]]::new()
$env:P05_TEST_PASSWORD='P05:'+([guid]::NewGuid().ToString('N'))+'!'
$normal=Join-Path $root 'normal'
function Budget { if($script:commands -gt 14 -or $watch.Elapsed.TotalSeconds -gt 1140 -or (Test-Path (Join-Path $root 'cancel'))){throw 'Suite budget/cancellation.'} }
function OwnedPath([string]$Value){
    $resolved=[IO.Path]::GetFullPath($Value)
    if(!$resolved.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Outside owned suite.'}
    return $resolved
}
function Invoke-Phase([string]$Name,[string]$Executable,[string[]]$Arguments,[string]$Cwd,[int]$Seconds=120){
    Budget;$script:commands++
    $spec=Join-Path $root "$Name.spec.json"
    @{name=$Name;exe=$Executable;arguments=$Arguments;cwd=$Cwd;timeoutSeconds=$Seconds;environment=@{}}|ConvertTo-Json -Depth 6|Set-Content $spec
    & (Join-Path $PSScriptRoot 'Invoke-Owned.ps1') -Spec $spec -EvidenceRoot $root | Out-Host
    return Get-Content (Join-Path $root "$Name.result.json") -Raw|ConvertFrom-Json
}
function Inventory([string]$Directory){
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $entries=@(Get-ChildItem -LiteralPath $Directory -Recurse -Depth 10 | Select-Object -First 4001)
    if($entries.Count -gt 4000){throw 'Data copy count exceeded.'}
    $files=@()
    foreach($entry in $entries){
        if($clock.Elapsed.TotalSeconds -gt 60 -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Data copy time/link budget.'}
        $relative=[IO.Path]::GetRelativePath($Directory,$entry.FullName)
        if($relative.Split([IO.Path]::DirectorySeparatorChar).Count -gt 10){throw 'Data copy depth exceeded.'}
        if(!$entry.PSIsContainer){$files+=@{path=$relative.Replace('\','/');size=$entry.Length;sha256=(Get-FileHash -LiteralPath $entry.FullName).Hash.ToLowerInvariant()}}
    }
    return @($files|Sort-Object {$_.path})
}
function Run-Journey([string]$Directory,[string]$Phase){
    $receipt=Invoke-Phase ('suite-'+$Phase) 'C:\Program Files\PowerShell\7\pwsh.exe' @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Invoke-Journey.ps1'),'-RunRoot',$Directory,'-Phase',$Phase) (Join-Path $PSScriptRoot '../..') 900
    $phaseReceipt=Get-Content (Join-Path $Directory "$Phase/result.json") -Raw|ConvertFrom-Json
    if($receipt.status -ne 'PASS' -or !$receipt.cleanupComplete -or $phaseReceipt.status -ne 'PASS' -or !$phaseReceipt.normalStop -or !$phaseReceipt.cleanupComplete){throw 'Actual installed phase failed: '+$Phase}
    $results.Add(@{name=$Phase;status='PASS';normalStop=$true;exitCode=$phaseReceipt.exitCode;cleanupComplete=$true})
}
try {
    $manifests=@(Get-ChildItem -LiteralPath $delivery -File -Filter '*-manifest.json'|Select-Object -First 2)
    if($manifests.Count -ne 1){throw 'Unique manifest required.'}
    $manifest=Get-Content $manifests[0].FullName -Raw|ConvertFrom-Json
    $zip=Join-Path $delivery $manifest.zip.path
    if((Get-Item $zip).Length -ne $manifest.zip.size -or (Get-FileHash $zip).Hash.ToLowerInvariant() -cne $manifest.zip.sha256){throw 'Delivery ZIP mismatch.'}
    @{version=$manifest.identity.version;source=$manifest.identity.sourceCommit;zip=$manifest.zip;host='Windows 11 x64';profile='preview-1';scope='R1'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'input.json')
    $null=New-Item -ItemType Directory -Path $normal
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $normal 'package')
    Run-Journey $normal 'first'
    $data=OwnedPath (Join-Path $normal 'data');$snapshot=OwnedPath (Join-Path $root 'snapshot')
    $before=@(Inventory $data);Copy-Item -LiteralPath $data -Destination $snapshot -Recurse
    $copied=@(Inventory $snapshot)
    if(($before|ConvertTo-Json -Depth 5 -Compress) -cne ($copied|ConvertTo-Json -Depth 5 -Compress)){throw 'Independent snapshot copy mismatch.'}
    @{source=$data;snapshot=$snapshot;files=$copied;status='PASS';serverNormallyStopped=$true}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'snapshot.json')
    Run-Journey $normal 'reopen'
    $changed=@(Inventory $data)
    if(($changed|ConvertTo-Json -Depth 5 -Compress) -ceq ($before|ConvertTo-Json -Depth 5 -Compress)){throw 'Snapshot mutation was not persisted.'}
    Remove-Item -LiteralPath (OwnedPath $data) -Recurse -Force
    Copy-Item -LiteralPath $snapshot -Destination $data -Recurse
    $restored=@(Inventory $data)
    if(($restored|ConvertTo-Json -Depth 5 -Compress) -cne ($before|ConvertTo-Json -Depth 5 -Compress)){throw 'Restored snapshot differs.'}
    Run-Journey $normal 'rollback'
    $results.Add(@{name='isolated-snapshot-rollback';status='PASS';sameCandidate=$true;olderServerOpenedNewData=$false;restoredFileCount=$restored.Count})
    foreach($name in @('wrong-web','wrong-server','wrong-candidate','port-occupied','data-permission')){
        Budget
        $directory=OwnedPath (Join-Path $root $name);$null=New-Item -ItemType Directory -Path $directory
        $package=Join-Path $directory 'package';Expand-Archive -LiteralPath $zip -DestinationPath $package
        $listener=$null;$acl=$null;$dataPath=Join-Path $directory 'data'
        try {
            if($name -eq 'wrong-web'){[IO.File]::AppendAllText((Join-Path $package 'wwwroot/index.html'),'<!-- stale fixture -->')}
            if($name -eq 'wrong-candidate'){$id=Get-Content (Join-Path $package 'preview-1.json') -Raw|ConvertFrom-Json;$id.candidate='candidate-09';$id|ConvertTo-Json -Depth 30|Set-Content (Join-Path $package 'preview-1.json')}
            if($name -eq 'wrong-server'){$archive=[IO.Compression.ZipFile]::OpenRead($LegacyZip);try{[IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('SonnetDB.exe'),(Join-Path $package 'SonnetDB.exe'),$true)}finally{$archive.Dispose()}}
            if($name -eq 'port-occupied'){$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,5080);$listener.Start()}
            if($name -eq 'data-permission'){
                $null=New-Item -ItemType Directory -Path $dataPath;$acl=Get-Acl -LiteralPath $dataPath
                $denied=Get-Acl -LiteralPath $dataPath
                $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
                $rule=[Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::Write,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Deny)
                $denied.AddAccessRule($rule);Set-Acl -LiteralPath $dataPath -AclObject $denied
            }
            $arguments=@('-NoProfile','-File',(Join-Path $package 'Start-Preview.ps1'),'-AcceptThirdPartyTerms')
            if($name -in @('wrong-web','wrong-server','wrong-candidate')){$arguments+='-VerifyOnly'}
            $receipt=Invoke-Phase ('negative-'+$name) 'C:\Program Files\PowerShell\7\pwsh.exe' $arguments $package 30
            if($receipt.status -ne 'FAIL' -or $receipt.exitCode -eq 0 -or !$receipt.cleanupComplete){throw 'Negative scenario did not fail cleanly: '+$name}
            $logs=(Get-Content (Join-Path $root "negative-$name.stdout.log") -Raw)+(Get-Content (Join-Path $root "negative-$name.stderr.log") -Raw)
            $pattern=switch($name){'wrong-web'{'Package mismatch'};'wrong-server'{'Package mismatch'};'wrong-candidate'{'candidate/version mismatch'};'port-occupied'{'address.*in use|failed to bind|AddressInUseException'};'data-permission'{'UnauthorizedAccessException|access.*denied|拒绝访问'}}
            if($logs -notmatch $pattern){throw 'Negative reason not proven: '+$name}
            $results.Add(@{name=$name;status='PASS_EXPECTED_REFUSAL';actualExitCode=$receipt.exitCode;cleanupComplete=$true})
        } finally {if($listener){$listener.Stop()};if($acl){Set-Acl -LiteralPath $dataPath -AclObject $acl}}
    }
    $legacy=OwnedPath (Join-Path $root 'legacy');$null=New-Item -ItemType Directory -Path $legacy
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $legacy 'package')
    $archive=[IO.Compression.ZipFile]::OpenRead($LegacyZip)
    try{[IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('SonnetDB.exe'),(Join-Path $legacy 'package/SonnetDB.exe'),$true)}finally{$archive.Dispose()}
    Run-Journey $legacy 'legacy'
    @{status='PASS';records=$results;elapsedSeconds=$watch.Elapsed.TotalSeconds;releaseStatus='NOT_READY';scope='R1';p06p08='NOT_RUN'}|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root 'suite.json')
} catch {
    @{status='FAIL';error=$_.Exception.Message;records=$results;elapsedSeconds=$watch.Elapsed.TotalSeconds;releaseStatus='NOT_READY'}|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root 'suite.json')
    throw
} finally { $env:P05_TEST_PASSWORD=$null }
