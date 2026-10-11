param([Parameter(Mandatory)][string]$RunRoot, [Parameter(Mandatory)][ValidateSet('first','reopen','rollback','micro09','legacy')][string]$Phase)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$root = [IO.Path]::GetFullPath($RunRoot)
$package = Join-Path $root 'package'
$identity = Get-Content (Join-Path $package 'preview-1.json') -Raw | ConvertFrom-Json
$evidence = Join-Path $root $Phase
if (Test-Path -LiteralPath $evidence) { throw 'Immutable phase directory exists.' }
$null = New-Item -ItemType Directory -Path $evidence
$watch = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{phase=$Phase;startUtc=[DateTime]::UtcNow.ToString('O');package=$package;status='RUNNING';root=$null;normalStop=$false;exitCode=$null;cleanupComplete=$false;identities=@();cleanupIdentities=@();error=$null;cleanupError=$null}
$server = $null
$oldEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
    if (@(Get-NetTCPConnection -State Listen -LocalPort 5080,5081 -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Required ports already occupied; preserve the owner.' }
    if ($Phase -eq 'legacy') {
        if ([IO.Path]::GetFileName($root) -ne 'legacy' -or !(Test-Path (Join-Path (Split-Path $root) '.owned-m47-p05'))) { throw 'Explicit owned legacy mismatch fixture required.' }
        $result['legacyServerProductVersion']=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'SonnetDB.exe')).ProductVersion
        if (!$result.legacyServerProductVersion.StartsWith('4.5.0-preview.1.9+')) { throw 'Expected immutable candidate09 legacy Server.' }
    } else { & (Join-Path $package 'Start-Preview.ps1') -VerifyOnly }
    if (-not ('JobProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'JobProcess.cs') }
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $server = [JobProcess]::new((Join-Path $package 'SonnetDB.exe'), [string[]]@(), $package,
        (Join-Path $evidence 'server.stdout.log'), (Join-Path $evidence 'server.stderr.log'), $true)
    $result.root = @($server.Refresh() | Select-Object pid,parentPid,createdUtc,commandLine,kernelJobMember)[0]
    $result | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $evidence 'start.json')
    $server.Start()
    $ready = $false
    for ($round=0; $round -lt 60 -and $watch.Elapsed.TotalSeconds -lt 45; $round++) {
        if (Test-Path (Join-Path $evidence 'cancel')) { throw 'Cancelled.' }
        if ($server.RootExited) { throw "Server exited before readiness: $($server.RootExitCode)" }
        $null=$server.Refresh()
        try { $probe=Invoke-WebRequest -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 2; if($probe.StatusCode -eq 200){$ready=$true;break} } catch { Start-Sleep -Milliseconds 400 }
    }
    if (!$ready) { throw 'Server readiness budget exhausted.' }
    $listeners=@(Get-NetTCPConnection -State Listen -LocalPort 5080,5081 -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort,OwningProcess)
    if($listeners.Count -ne 2 -or @($listeners | Where-Object {$_.LocalAddress -ne '127.0.0.1' -or $_.OwningProcess -ne $server.RootPid}).Count -gt 0){throw 'Unexpected listener binding/owner.'}
    $listeners | ConvertTo-Json | Set-Content (Join-Path $evidence 'listeners.json')
    $name='browser-'+$Phase
    $spec=Join-Path $evidence "$name.spec.json"
    @{name=$name;exe='C:\Program Files\nodejs\node.exe';arguments=@((Join-Path $PSScriptRoot 'journey.mjs'),$evidence,$Phase,$identity.version,$identity.sourceCommit);cwd=(Join-Path $PSScriptRoot '../..');timeoutSeconds=780;environment=@{}} |
        ConvertTo-Json -Depth 5 | Set-Content $spec
    & (Join-Path $PSScriptRoot 'Invoke-Owned.ps1') -Spec $spec -EvidenceRoot $evidence
    $browser=Get-Content (Join-Path $evidence "$name.result.json") -Raw | ConvertFrom-Json
    if($browser.status -ne 'PASS' -or !$browser.cleanupComplete){throw 'Browser phase failed; retain original receipt.'}
    $server.InterruptRoot()
    $stop=[Diagnostics.Stopwatch]::StartNew()
    for($round=0; $round -lt 120 -and $stop.Elapsed.TotalSeconds -lt 30; $round++) {
        if($server.RootExited){break}; $null=$server.Refresh(); Start-Sleep -Milliseconds 250
    }
    if(!$server.RootExited){throw 'Normal Ctrl+C stop deadline exceeded.'}
    $result.exitCode=$server.RootExitCode
    if($result.exitCode -ne 0){throw 'Normal Server stop returned nonzero.'}
    $result.normalStop=$true
    $result.status='PASS'
} catch { $result.status='FAIL'; $result.error=$_.Exception.Message }
finally {
    if($null -ne $server){
        try {
            $drain=[Diagnostics.Stopwatch]::StartNew()
            for($round=0; $result.normalStop -and $round -lt 20 -and $drain.Elapsed.TotalSeconds -lt 5; $round++) { if($server.Members().Count -eq 0){break}; Start-Sleep -Milliseconds 250 }
            $result.identities=@($server.Refresh() | Select-Object pid,parentPid,createdUtc,commandLine,kernelJobMember)
            $result.cleanupIdentities=@($server.TerminateOwned() | Select-Object pid,parentPid,createdUtc,commandLine,kernelJobMember)
            $stop=[Diagnostics.Stopwatch]::StartNew()
            for($round=0; $round -lt 40 -and $stop.Elapsed.TotalSeconds -lt 10; $round++) { if($server.Members().Count -eq 0){$result.cleanupComplete=$true;break}; Start-Sleep -Milliseconds 250 }
            if(!$result.cleanupComplete){$result.status='FAIL';$result.error='Server Job cleanup did not empty.'}
        } catch { $result.status='FAIL';$result.cleanupError=$_.Exception.Message }
        finally { $server.Dispose() }
    }
    $env:ASPNETCORE_ENVIRONMENT=$oldEnvironment
    $result.endUtc=[DateTime]::UtcNow.ToString('O');$result.elapsedSeconds=$watch.Elapsed.TotalSeconds
    $result | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidence 'result.json')
}
$result | ConvertTo-Json -Depth 5
if($result.status -ne 'PASS'){exit 1}
