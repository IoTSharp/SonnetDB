param([Parameter(Mandatory)][string] $ConfigPath)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$taskRoot = [IO.Path]::GetFullPath($config.TaskRoot).TrimEnd('\')
if ($taskRoot -ne 'C:\Temp\sdb-delivery-9bdceac1') { throw 'Unexpected task directory.' }
$outputRoot = if ($config.OutputDirectory) { [IO.Path]::GetFullPath($config.OutputDirectory).TrimEnd('\') } else { $taskRoot }
if ($outputRoot -notin @($taskRoot, 'D:\source\SonnetDB\docs\audits\roadmap-parallel-evidence-20261002')) { throw 'Unexpected output directory.' }
$prefix = [IO.Path]::GetFullPath((Join-Path $outputRoot $config.Name))
if (-not $prefix.StartsWith($outputRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid output prefix.' }
$timeout = [int]$config.TimeoutSeconds
if ($timeout -lt 1 -or $timeout -gt 1200) { throw 'Timeout must be 1..1200 seconds.' }
if ($config.Arguments.Count -gt 64) { throw 'Too many arguments.' }
$start = [Diagnostics.ProcessStartInfo]::new($config.Executable)
$start.WorkingDirectory = 'D:\source\SonnetDB'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in $config.Arguments) { $start.ArgumentList.Add([string]$argument) }
$start.Environment['MSBUILDDISABLENODEREUSE'] = '1'
$start.Environment['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'
$start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
$start.Environment['DOTNET_PROCESSOR_COUNT'] = '4'
$start.Environment['TEMP'] = Join-Path $taskRoot 'tmp'
$start.Environment['TMP'] = Join-Path $taskRoot 'tmp'
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$tracked = @{}
$timer = [Diagnostics.Stopwatch]::StartNew()
$stdoutTask = $null
$stderrTask = $null
$started = $false
$failure = $null
$exitCode = -1
function Register-OwnedProcess($entry) {
    if ($null -ne $entry -and -not $tracked.ContainsKey([int]$entry.ProcessId)) {
        if ($tracked.Count -ge 256) { throw 'Task process count exceeded 256.' }
        $tracked[[int]$entry.ProcessId] = [pscustomobject]@{
            ProcessId = [int]$entry.ProcessId; ParentProcessId = [int]$entry.ParentProcessId
            CreationDate = $entry.CreationDate; CommandLine = $entry.CommandLine
        }
    }
}
function Test-OwnedIdentity($current, $original) {
    return $null -ne $current -and $null -ne $original -and
        $current.CreationDate -eq $original.CreationDate -and
        $current.CommandLine -ceq $original.CommandLine -and
        $current.ParentProcessId -eq $original.ParentProcessId
}
try {
    $started = $process.Start()
    if (-not $started) { throw 'Process did not start.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    Register-OwnedProcess (Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -OperationTimeoutSec 3)
    Write-Output "START $($config.Name) PID=$($process.Id) parent=$PID UTC=$([DateTime]::UtcNow.ToString('o'))"
    $maxPolls = 2 * $timeout + 2
    for ($poll = 0; $poll -lt $maxPolls -and $timer.Elapsed.TotalSeconds -lt $timeout; $poll++) {
        if (Test-Path -LiteralPath (Join-Path $taskRoot 'cancel')) { throw 'Task cancellation requested.' }
        if ($process.WaitForExit(500)) { break }
        if ($poll % 4 -eq 0) {
            $filter = ($tracked.Keys | ForEach-Object { "ParentProcessId = $_" }) -join ' OR '
            if ($filter) {
                $children = @(Get-CimInstance Win32_Process -Filter $filter -OperationTimeoutSec 3)
                if ($children.Count -gt 256) { throw 'Too many children.' }
                foreach ($child in $children) { Register-OwnedProcess $child }
            }
        }
        if ($poll % 40 -eq 39) { Write-Output "RUNNING $($config.Name) elapsed=$([int]$timer.Elapsed.TotalSeconds)s tracked=$($tracked.Count)" }
    }
    if (-not $process.HasExited) { throw "Timed out after $timeout seconds." }
    $exitCode = $process.ExitCode
} catch { $failure = $_.Exception.Message }
finally {
    if ($null -ne $stdoutTask -and $stdoutTask.IsCompletedSuccessfully) { [IO.File]::WriteAllText($prefix + '.stdout.log', $stdoutTask.Result) }
    if ($null -ne $stderrTask -and $stderrTask.IsCompletedSuccessfully) { [IO.File]::WriteAllText($prefix + '.stderr.log', $stderrTask.Result) }
    if ($started -and -not $process.HasExited) {
        $live = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -OperationTimeoutSec 3
        if (Test-OwnedIdentity $live $tracked[$process.Id]) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
        }
    }
    $cleanup = @()
    $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
    $remaining = @{}
    $filter = ($tracked.Keys | ForEach-Object { "ProcessId = $_" }) -join ' OR '
    if ($filter) {
        foreach ($live in @(Get-CimInstance Win32_Process -Filter $filter -OperationTimeoutSec 3)) {
            $remaining[[int]$live.ProcessId] = $live
        }
    }
    foreach ($entry in @($tracked.Values | Sort-Object CreationDate -Descending)) {
        if ($cleanupWatch.Elapsed.TotalSeconds -ge 30) { Write-Warning 'Cleanup deadline reached; retained process identities require final audit.'; break }
        $live = $remaining[$entry.ProcessId]
        if (Test-OwnedIdentity $live $entry) {
            $owned = $null
            try {
                $owned = [Diagnostics.Process]::GetProcessById($entry.ProcessId)
                if (-not $owned.HasExited) { $owned.Kill($true); $null = $owned.WaitForExit(5000); $cleanup += $entry.ProcessId }
            }
            catch [ArgumentException] { Write-Output "ALREADY EXITED PID=$($entry.ProcessId)" }
            catch [InvalidOperationException] { Write-Output "EXIT RACE PID=$($entry.ProcessId)" }
            finally { if ($null -ne $owned) { $owned.Dispose() } }
        }
    }
    if ($null -ne $stdoutTask -and $stdoutTask.Wait(5000)) { [IO.File]::WriteAllText($prefix + '.stdout.log', $stdoutTask.Result) }
    if ($null -ne $stderrTask -and $stderrTask.Wait(5000)) { [IO.File]::WriteAllText($prefix + '.stderr.log', $stderrTask.Result) }
    [ordered]@{
        Name = $config.Name; Executable = $config.Executable; Arguments = $config.Arguments
        ExitCode = $exitCode; Failure = $failure; DurationSeconds = $timer.Elapsed.TotalSeconds
        Processes = @($tracked.Values); CleanedProcesses = $cleanup
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($prefix + '.processes.json')
    $process.Dispose()
}
Write-Output "EXIT $($config.Name) code=$exitCode elapsed=$([int]$timer.Elapsed.TotalSeconds)s"
if (Test-Path -LiteralPath ($prefix + '.stdout.log')) { Get-Content -LiteralPath ($prefix + '.stdout.log') -Tail 20 }
if (Test-Path -LiteralPath ($prefix + '.stderr.log')) { Get-Content -LiteralPath ($prefix + '.stderr.log') -Tail 15 }
if ($failure) { throw $failure }
if ($exitCode -ne 0) { throw "Command failed: $exitCode" }
