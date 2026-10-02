$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$taskRoot = [IO.Path]::GetFullPath('C:\Temp\sdbp-81bfff26')
$delivery = 'D:\source\SonnetDB\docs\audits\roadmap-parallel-evidence-20261002'
if ($taskRoot -ne 'C:\Temp\sdbp-81bfff26' -or (Get-Item -LiteralPath $taskRoot).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected temporary root.' }
$timer = [Diagnostics.Stopwatch]::StartNew()
$records = @([IO.Directory]::GetFiles($delivery, '*.processes.json'))
if ($records.Count -gt 32) { throw 'Too many process ledgers.' }
$identities = @{}
foreach ($record in $records) {
    if ($timer.Elapsed.TotalSeconds -ge 15) { throw 'Process audit timed out.' }
    $data = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    if ($data.Processes.Count -gt 256) { throw 'Too many processes in a ledger.' }
    foreach ($entry in $data.Processes) { $identities[[string]$entry.ProcessId + '|' + $entry.CreationDate] = $entry }
}
if ($identities.Count -gt 512) { throw 'Too many task process identities.' }
$live = @(Get-CimInstance Win32_Process -OperationTimeoutSec 3)
if ($live.Count -gt 4096) { throw 'Too many system processes for bounded audit.' }
$retained = @()
foreach ($entry in $identities.Values) {
    if ($timer.Elapsed.TotalSeconds -ge 20) { throw 'Process audit timed out.' }
    $match = @($live | Where-Object { $_.ProcessId -eq $entry.ProcessId -and $_.CreationDate -eq [datetime]$entry.CreationDate -and $_.CommandLine -ceq $entry.CommandLine -and $_.ParentProcessId -eq $entry.ParentProcessId })
    if ($match.Count -ne 0) { $retained += $entry }
}
if ($retained.Count -ne 0) {
    $retained | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $delivery 'retained-processes.json')
    throw 'Owned process still live; preserve build directory and inspect before cleanup.'
}
$stack = [Collections.Generic.Stack[string]]::new()
$stack.Push($taskRoot)
$entries = 0
for ($directoryCount = 0; $directoryCount -lt 20000 -and $stack.Count -gt 0; $directoryCount++) {
    if ($timer.Elapsed.TotalSeconds -ge 45 -or (Test-Path -LiteralPath (Join-Path $taskRoot 'cancel'))) { throw 'Cleanup audit cancelled or timed out.' }
    $directory = $stack.Pop()
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
        $entries++
        if ($entries -gt 20000 -or $timer.Elapsed.TotalSeconds -ge 45) { throw 'Cleanup tree exceeds bounds.' }
        $fullPath = [IO.Path]::GetFullPath($path)
        if (-not $fullPath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Entry outside temporary root.' }
        $attributes = [IO.File]::GetAttributes($fullPath)
        if ($attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected link in temporary tree; preserve and inspect.' }
        if ($attributes.HasFlag([IO.FileAttributes]::Directory)) { $stack.Push($fullPath) }
    }
}
if ($stack.Count -ne 0) { throw 'Directory iteration limit exceeded.' }
Write-Output "AUDIT processIdentities=$($identities.Count) ownedLive=0 entries=$entries target=$taskRoot"
Remove-Item -LiteralPath $taskRoot -Recurse -Force
if (Test-Path -LiteralPath $taskRoot) { throw 'Temporary root was not removed.' }
[ordered]@{
    TaskRoot=$taskRoot; ProcessIdentityCount=$identities.Count; OwnedLive=0; RemovedEntries=$entries
    TempRemoved=$true; SharedCachesRemoved=$false; UserFilesRemoved=$false; ElapsedSeconds=$timer.Elapsed.TotalSeconds
    FirstBuildRootPid=91004; FirstBuildRootLive=[bool]($live | Where-Object { $_.ProcessId -eq 91004 -and $_.CommandLine -like '*sdbp-81bfff26*' })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $delivery 'cleanup.json')
Write-Output 'Task temporary build, logs and test data removed; delivery evidence preserved.'
