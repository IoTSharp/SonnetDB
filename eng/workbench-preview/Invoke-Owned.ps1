param([Parameter(Mandatory)][string]$Spec, [Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$config = Get-Content -LiteralPath $Spec -Raw | ConvertFrom-Json
if ($config.timeoutSeconds -lt 1 -or $config.timeoutSeconds -gt 1200) { throw 'Timeout outside 1..1200.' }
if ($config.name -notmatch '^[a-z0-9-]{1,60}$') { throw 'Invalid evidence name.' }
$prefix = Join-Path $evidence $config.name
if (Test-Path -LiteralPath "$prefix.result.json") { throw 'Immutable receipt already exists.' }
if (-not ('JobProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'JobProcess.cs') }
foreach ($entry in $config.environment.PSObject.Properties) { [Environment]::SetEnvironmentVariable($entry.Name, [string]$entry.Value, 'Process') }
$watch = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{ name=$config.name; startUtc=[DateTime]::UtcNow.ToString('O'); status='RUNNING'; root=$null; controller=$null; exitCode=$null; cleanupComplete=$false; cleanupIdentities=@(); identityCount=0; error=$null }
$controller = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -OperationTimeoutSec 5
$result.controller = @{ pid=$PID; parentPid=$controller.ParentProcessId; createdUtc=$controller.CreationDate.ToUniversalTime().ToString('O'); commandLine=$controller.CommandLine }
$job = $null
try {
    $job = [JobProcess]::new($config.exe, [string[]]$config.arguments, $config.cwd, "$prefix.stdout.log", "$prefix.stderr.log")
    $result.root = @($job.Refresh() | Select-Object pid,parentPid,createdUtc,commandLine,kernelJobMember)[0]
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$prefix.start.json"
    $job.Start()
    $maxPolls = [int]$config.timeoutSeconds * 2 + 2
    for ($round=0; $round -lt $maxPolls -and $watch.Elapsed.TotalSeconds -lt $config.timeoutSeconds; $round++) {
        if (Test-Path -LiteralPath (Join-Path $evidence 'cancel')) { throw 'Task cancellation file exists.' }
        if ($job.RootExited) { $result.exitCode=$job.RootExitCode; $result.status=if($result.exitCode -eq 0){'PASS'}else{'FAIL'}; break }
        if ($round % 10 -eq 0) {
            $ids=@($job.Refresh() | Select-Object pid,parentPid,createdUtc,commandLine,kernelJobMember)
            $ids | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$prefix.identities.json"
            $result.identityCount=$ids.Count
        }
        if ($round % 60 -eq 0) { Write-Output "$($config.name): $([int]$watch.Elapsed.TotalSeconds)s, $($result.identityCount) owned identities" }
        Start-Sleep -Milliseconds 500
    }
    if ($result.status -eq 'RUNNING') { throw 'Wall clock or poll budget exhausted.' }
} catch { $result.status='ERROR'; $result.error=$_.Exception.Message }
finally {
    if ($null -ne $job) {
        try {
            $ids=@($job.Refresh() | Select-Object pid,parentPid,createdUtc,commandLine,kernelJobMember)
            $ids | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$prefix.identities.json"
            $result.identityCount=$ids.Count
            $result.deferredObservations=$job.DeferredObservations
            $result.deferredObservationCount=$job.DeferredObservationCount
            $result.cleanupIdentities=@($job.TerminateOwned() | Select-Object pid,parentPid,createdUtc,commandLine,kernelJobMember)
            $cleanupClock=[Diagnostics.Stopwatch]::StartNew()
            for($round=0; $round -lt 40 -and $cleanupClock.Elapsed.TotalSeconds -lt 10; $round++) {
                if($job.Members().Count -eq 0) { $result.cleanupComplete=$true; break }
                Start-Sleep -Milliseconds 250
            }
            if(!$result.cleanupComplete) { $result.status='ERROR'; $result.error='Job membership did not empty within cleanup budget.' }
        } catch { $result.status='ERROR'; $result.error="Cleanup: $($_.Exception.Message)" }
        finally { $job.Dispose() }
    }
    $result.endUtc=[DateTime]::UtcNow.ToString('O'); $result.elapsedSeconds=$watch.Elapsed.TotalSeconds
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$prefix.result.json"
}
$result | ConvertTo-Json -Depth 6
if ($result.status -ne 'PASS') { exit 1 }
