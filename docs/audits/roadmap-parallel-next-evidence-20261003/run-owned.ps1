param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Executable,
    [string[]]$Arguments = @(),
    [ValidateRange(1, 1800)][int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$workspace = 'D:\source\SonnetDB'
$evidence = $PSScriptRoot
$cancelPath = Join-Path $evidence 'cancel.flag'
if ($Name -notmatch '^[a-z0-9-]+$') { throw 'Invalid run name.' }
$env:DOTNET_PROCESSOR_COUNT = '2'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Identity($item) {
    [pscustomobject]@{
        pid = [int]$item.ProcessId
        created = $item.CreationDate.ToUniversalTime().ToString('O')
        command = [string]$item.CommandLine
        parent = [int]$item.ParentProcessId
    }
}
function SameIdentity($left, $right) {
    return $null -ne $left -and $null -ne $right -and
        $left.pid -eq $right.pid -and $left.created -eq $right.created -and
        $left.command -eq $right.command -and $left.parent -eq $right.parent
}
function Snapshot {
    $map = @{}
    $items = @(Get-CimInstance Win32_Process -OperationTimeoutSec 5)
    if ($items.Count -gt 4096) { throw 'Process snapshot exceeds 4096 items; stop adding diagnostic load.' }
    foreach ($item in $items) {
        $map[[int]$item.ProcessId] = Identity $item
    }
    return $map
}
# Every parent is resolved from THIS snapshot; historical parent PIDs are never ancestry evidence.
function OwnedChain($map, [int]$candidate, $rootIdentity, $ownerIdentity) {
    if (-not (SameIdentity $map[$rootIdentity.pid] $rootIdentity) -or
        -not (SameIdentity $map[$ownerIdentity.pid] $ownerIdentity)) { return @() }
    $chain = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    $chainTimer = [System.Diagnostics.Stopwatch]::StartNew()
    $current = $candidate
    for ($depth = 0; $depth -lt 64 -and $chainTimer.Elapsed.TotalSeconds -lt 2; $depth++) {
        $identity = $map[$current]
        if ($null -eq $identity -or [string]::IsNullOrWhiteSpace($identity.command) -or -not $seen.Add($current)) { return @() }
        $chain.Add($identity)
        if ($current -eq $rootIdentity.pid) {
            if ($identity.parent -ne $ownerIdentity.pid -or $identity.created -lt $ownerIdentity.created) { return @() }
            $chain.Add($ownerIdentity)
            return $chain.ToArray()
        }
        $parent = $map[$identity.parent]
        if ($null -eq $parent -or $parent.created -gt $identity.created) { return @() }
        $current = $identity.parent
    }
    return @()
}

$ownerMap = Snapshot
$owner = $ownerMap[$PID]
if ($null -eq $owner -or [string]::IsNullOrWhiteSpace($owner.command)) { throw 'Cannot establish launcher identity.' }
$startInfo = [System.Diagnostics.ProcessStartInfo]::new($Executable)
$startInfo.WorkingDirectory = $workspace
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $true
foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$observed = @{}
$samples = [System.Collections.Generic.List[object]]::new()
$actions = [System.Collections.Generic.List[object]]::new()
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$rootIdentity = $null
$exitCode = 124
$outTask = $null
$errTask = $null
$reason = 'timeout'
try {
    if (-not $process.Start()) { throw 'Process did not start.' }
    $rootPid = $process.Id
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    $map = Snapshot
    $rootIdentity = $map[$rootPid]
    if ($null -eq $rootIdentity -or [string]::IsNullOrWhiteSpace($rootIdentity.command)) {
        if (-not $process.HasExited) { throw 'Cannot establish launched root identity.' }
        # A very short command can finish before CIM sees it; never infer or kill a replacement PID.
        $actions.Add([pscustomobject]@{ action = 'short-command-already-exited'; pid = $rootPid })
        $rootIdentity = $null
    }
    if ($null -ne $rootIdentity) { $observed[$rootIdentity.pid] = $rootIdentity }
    $maxPolls = [int][Math]::Ceiling($TimeoutSeconds / 2.0) + 1
    for ($poll = 0; $poll -lt $maxPolls -and $timer.Elapsed.TotalSeconds -lt $TimeoutSeconds; $poll++) {
        if (Test-Path -LiteralPath $cancelPath) { $reason = 'cancelled'; break }
        if ($null -ne $rootIdentity) {
            $map = Snapshot
            $chains = [System.Collections.Generic.List[object]]::new()
            $sampleTimer = [System.Diagnostics.Stopwatch]::StartNew()
            foreach ($candidate in @($map.Keys)) {
                if ($sampleTimer.Elapsed.TotalSeconds -ge 2 -or $timer.Elapsed.TotalSeconds -ge $TimeoutSeconds -or
                    (Test-Path -LiteralPath $cancelPath)) { break }
                $chain = @(OwnedChain $map $candidate $rootIdentity $owner)
                if ($chain.Count -eq 0) { continue }
                $observed[$candidate] = $chain[0]
                $chains.Add($chain)
            }
            $samples.Add([pscustomobject]@{ elapsedSeconds = $timer.Elapsed.TotalSeconds; chains = $chains.ToArray() })
        }
        if ($process.WaitForExit(2000)) {
            $exitCode = $process.ExitCode
            $reason = 'exited'
            break
        }
        if ($poll % 15 -eq 0) { Write-Host "$Name elapsed=$([int]$timer.Elapsed.TotalSeconds)s pid=$rootPid" }
    }
}
catch {
    $reason = 'runner-error'
    $exitCode = 125
    $actions.Add([pscustomobject]@{ action = 'runner-error-retained'; pid = $rootPid; error = $_.Exception.Message })
}
finally {
    # Stop leaves first while the recorded root AND launcher identities still match a current snapshot.
    $cleanupTimer = [System.Diagnostics.Stopwatch]::StartNew()
    try {
      for ($sweep = 0; $sweep -lt 8 -and $cleanupTimer.Elapsed.TotalSeconds -lt 30; $sweep++) {
        $map = Snapshot
        $owned = [System.Collections.Generic.List[object]]::new()
        if ($null -ne $rootIdentity) {
            foreach ($candidate in @($map.Keys)) {
                if ($cleanupTimer.Elapsed.TotalSeconds -ge 30) { break }
                $chain = @(OwnedChain $map $candidate $rootIdentity $owner)
                if ($chain.Count -gt 0) {
                    $observed[$candidate] = $chain[0]
                    $owned.Add([pscustomobject]@{ identity = $chain[0]; depth = $chain.Count })
                }
            }
        }
        if ($owned.Count -eq 0) { break }
        foreach ($item in @($owned | Sort-Object depth -Descending)) {
            if ($cleanupTimer.Elapsed.TotalSeconds -ge 30) { break }
            $fresh = Snapshot
            if ($cleanupTimer.Elapsed.TotalSeconds -ge 30) { break }
            $chain = @(OwnedChain $fresh $item.identity.pid $rootIdentity $owner)
            if ($chain.Count -eq 0 -or -not (SameIdentity $chain[0] $item.identity)) {
                $actions.Add([pscustomobject]@{ action = 'identity-changed-or-exited-retained'; identity = $item.identity })
                continue
            }
            try {
                $target = [System.Diagnostics.Process]::GetProcessById($item.identity.pid)
                try {
                    # CIM exposes microseconds; .NET may retain sub-microsecond ticks.
                    $creationDelta = ($target.StartTime.ToUniversalTime() - [DateTime]::Parse($item.identity.created).ToUniversalTime()).Duration()
                    if ($creationDelta.TotalMilliseconds -ge 0.001) { throw 'Creation time changed.' }
                    $target.Kill($false)
                    $null = $target.WaitForExit(2000)
                    $actions.Add([pscustomobject]@{ action = 'terminated-verified-owned-process'; chain = $chain })
                }
                finally { $target.Dispose() }
            }
            catch { $actions.Add([pscustomobject]@{ action = 'retained'; identity = $item.identity; error = $_.Exception.Message }) }
        }
        Start-Sleep -Milliseconds 250
      }
    }
    catch {
        $actions.Add([pscustomobject]@{ action = 'cleanup-snapshot-unavailable-retained'; error = $_.Exception.Message })
    }
    finally {
      $remaining = @()
      $finalSnapshotAvailable = $false
      try {
        $finalMap = Snapshot
        $finalSnapshotAvailable = $true
        $remaining = @($observed.Values | Where-Object { SameIdentity $finalMap[$_.pid] $_ })
      }
      catch {
        $actions.Add([pscustomobject]@{ action = 'final-snapshot-unavailable-retained'; error = $_.Exception.Message })
      }
      if ($remaining.Count -gt 0 -or -not $finalSnapshotAvailable) { Write-Warning 'Ownership/liveness could not be fully verified. Retained.' }
      try {
        $stdout = '[pipe not completed]'
        $stderr = '[pipe not completed]'
        try { if ($null -ne $outTask -and $outTask.IsCompleted) { $stdout = $outTask.GetAwaiter().GetResult() } }
        catch { $stdout = '[stdout read failed: ' + $_.Exception.Message + ']' }
        try { if ($null -ne $errTask -and $errTask.IsCompleted) { $stderr = $errTask.GetAwaiter().GetResult() } }
        catch { $stderr = '[stderr read failed: ' + $_.Exception.Message + ']' }
        [System.IO.File]::WriteAllText((Join-Path $evidence "$Name.out.log"), $stdout)
        [System.IO.File]::WriteAllText((Join-Path $evidence "$Name.err.log"), $stderr)
        $runnerSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        $record = [ordered]@{ name = $Name; runnerSha256 = $runnerSha256; executable = $Executable; arguments = $Arguments; timeoutSeconds = $TimeoutSeconds; maxPolls = $maxPolls; owner = $owner; root = $rootIdentity; reason = $reason; exitCode = $exitCode; elapsedSeconds = $timer.Elapsed.TotalSeconds; samples = $samples.ToArray(); cleanup = $actions.ToArray(); finalSnapshotAvailable = $finalSnapshotAvailable; remaining = $remaining }
        $record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence "$Name.process.json") -Encoding utf8
      }
      finally { $process.Dispose() }
    }
    Write-Host "$Name reason=$reason exit=$exitCode elapsed=$([int]$timer.Elapsed.TotalSeconds)s remaining=$($remaining.Count)"
    Write-Host ($stdout -split "`n" | Select-Object -Last 18 | Out-String)
    if ($stderr) { Write-Host ($stderr -split "`n" | Select-Object -Last 12 | Out-String) }
}
exit $exitCode
