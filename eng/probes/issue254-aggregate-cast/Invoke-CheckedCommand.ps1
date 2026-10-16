[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('dotnet', 'gh', 'git', 'pwsh', 'node', 'docker')][string]$Executable,
    [Parameter(Mandatory)][ValidateCount(1, 32)][string[]]$Arguments,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Label,
    [ValidateRange(5, 300)][int]$TimeoutSeconds = 120,
    [string]$WorkingDirectory = (Split-Path -Parent $PSScriptRoot),
    [switch]$Proxy
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required.' }
$taskDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
$taskLogDirectory = Join-Path $taskDirectory 'artifacts/checks'
New-Item -ItemType Directory -Path $taskLogDirectory -Force | Out-Null
$taskPrefix = Join-Path $taskLogDirectory $Label
if (Test-Path -LiteralPath ($taskPrefix + '.json')) { throw 'Use a new label for each attempt.' }
$taskStartInfo = [Diagnostics.ProcessStartInfo]::new()
$taskStartInfo.FileName = (Get-Command $Executable -CommandType Application | Select-Object -First 1).Source
$taskStartInfo.WorkingDirectory = $taskDirectory
$taskStartInfo.UseShellExecute = $false
$taskStartInfo.RedirectStandardOutput = $true
$taskStartInfo.RedirectStandardError = $true
foreach ($taskArgument in $Arguments) { $taskStartInfo.ArgumentList.Add($taskArgument) }
$taskStartInfo.Environment['MSBUILDDISABLENODEREUSE'] = '1'
$taskStartInfo.Environment['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'
$taskStartInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
$taskStartInfo.Environment['DOTNET_PROCESSOR_COUNT'] = '2'
$taskStartInfo.Environment['TEMP'] = 'D:/sndb-t/i254-build'
$taskStartInfo.Environment['TMP'] = 'D:/sndb-t/i254-build'
$taskStartInfo.Environment['NUGET_PACKAGES'] = 'D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue254-work/p'
$taskStartInfo.Environment['NUGET_HTTP_CACHE_PATH'] = 'D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue254-work/http-cache'
$taskStartInfo.Environment['NUGET_SCRATCH'] = 'D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue254-work/scratch'
$taskStartInfo.Environment['DOTNET_CLI_HOME'] = 'D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue254-work/dotnet-home'
if ($Proxy) {
    $taskStartInfo.Environment['HTTP_PROXY'] = 'http://127.0.0.1:7890'
    $taskStartInfo.Environment['HTTPS_PROXY'] = 'http://127.0.0.1:7890'
}
$taskProcess = [Diagnostics.Process]::new()
$taskProcess.StartInfo = $taskStartInfo
$taskClock = [Diagnostics.Stopwatch]::StartNew()
$taskIdentities = [Collections.Generic.Dictionary[int, object]]::new()
$taskTimedOut = $false
$taskFailure = $null
$taskExitCode = $null
$taskRootIdentity = $null
$taskRemaining = @()
$taskLaunchedPid = $null
$taskLaunchedAt = $null

function Save-TaskDescendants {
    param([int[]]$ParentIds)
    $taskParents = $ParentIds
    for ($taskDepth = 0; $taskDepth -lt 8 -and $taskParents.Count -gt 0; $taskDepth++) {
        $taskNext = [Collections.Generic.List[int]]::new()
        foreach ($taskParentId in $taskParents) {
            if ($taskClock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Process discovery deadline exceeded.' }
            if ($taskIdentities.Count -ge 128) { throw 'Process ledger limit exceeded.' }
            $taskChildren = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $taskParentId" -OperationTimeoutSec 3)
            foreach ($taskChild in $taskChildren) {
                if ($taskIdentities.Count -ge 128) { throw 'Process ledger limit exceeded.' }
                if (-not $taskChild.CreationDate) { throw 'Incomplete owned child creation identity.' }
                if (-not $taskChild.CommandLine) {
                    # 已退出的短命子进程可能仍出现在父链快照中；只重采一次，绝不终止身份不完整的进程。
                    $taskChildProbe = Get-CimInstance Win32_Process -Filter "ProcessId = $($taskChild.ProcessId)" -OperationTimeoutSec 3
                    if (-not $taskChildProbe -or $taskChildProbe.CreationDate -ne $taskChild.CreationDate) { continue }
                    if (-not $taskChildProbe.CommandLine -or $taskChildProbe.ParentProcessId -ne $taskChild.ParentProcessId) { throw 'Incomplete live owned child identity.' }
                    $taskChild = $taskChildProbe
                }
                $taskIdentities[[int]$taskChild.ProcessId] = $taskChild
                $taskNext.Add([int]$taskChild.ProcessId)
            }
        }
        $taskParents = $taskNext.ToArray()
    }
    if ($taskParents.Count -gt 0) { throw 'Process tree depth limit exceeded.' }
}

try {
    if (-not $taskProcess.Start()) { throw 'Process failed to start.' }
    $taskLaunchedPid = $taskProcess.Id
    $taskLaunchedAt = $taskProcess.StartTime.ToUniversalTime()
    $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
    $taskRootIdentity = Get-CimInstance Win32_Process -Filter "ProcessId = $($taskProcess.Id)" -OperationTimeoutSec 3
    if ($taskRootIdentity -and -not $taskRootIdentity.CommandLine -and $taskProcess.HasExited) {
        # CIM 可能在短命进程退出时仍返回身份但丢失命令行；记录启动参数与真实退出码，不尝试终止。
        $taskRootIdentity = $null
    }
    if ($taskRootIdentity) {
        if (-not $taskRootIdentity.CommandLine -or $taskRootIdentity.ParentProcessId -ne $PID) { throw 'Root identity could not be verified.' }
        $taskIdentities[[int]$taskProcess.Id] = $taskRootIdentity
    }
    elseif (-not $taskProcess.HasExited) { throw 'Live root identity could not be verified.' }
    $taskMaximumPolls = $TimeoutSeconds * 4
    for ($taskPoll = 0; $taskPoll -lt $taskMaximumPolls -and $taskClock.Elapsed.TotalSeconds -lt $TimeoutSeconds; $taskPoll++) {
        if ($taskProcess.WaitForExit(250)) { break }
        if ($taskPoll % 40 -eq 0) {
            Save-TaskDescendants -ParentIds @($taskProcess.Id)
            Write-Host "$Label running: $([int]$taskClock.Elapsed.TotalSeconds)s"
        }
    }
    if (-not $taskProcess.HasExited) { $taskTimedOut = $true; throw 'Command deadline exceeded.' }
    $taskExitCode = $taskProcess.ExitCode
}
catch { $taskFailure = $_.Exception.Message }
finally {
    $taskCleanupClock = [Diagnostics.Stopwatch]::StartNew()
    if ($taskRootIdentity -and -not $taskProcess.HasExited) {
        $taskLiveRoot = Get-CimInstance Win32_Process -Filter "ProcessId = $($taskRootIdentity.ProcessId)" -OperationTimeoutSec 3
        if ($taskLiveRoot -and $taskLiveRoot.CreationDate -eq $taskRootIdentity.CreationDate -and $taskLiveRoot.CommandLine -ceq $taskRootIdentity.CommandLine -and $taskLiveRoot.ParentProcessId -eq $taskRootIdentity.ParentProcessId) {
            $taskProcess.Kill($true)
            if (-not $taskProcess.WaitForExit(5000)) { $taskFailure = 'Owned root did not exit after cancellation.' }
        }
    }
    $taskKnownFilter = (@($taskIdentities.Keys) | ForEach-Object { "ProcessId = $_" }) -join ' OR '
    $taskLiveById = @{}
    if ($taskKnownFilter) {
        foreach ($taskKnownLive in @(Get-CimInstance Win32_Process -Filter $taskKnownFilter -OperationTimeoutSec 8)) {
            $taskLiveById[[int]$taskKnownLive.ProcessId] = $taskKnownLive
        }
    }
    foreach ($taskIdentity in @($taskIdentities.Values)) {
        if ($taskCleanupClock.Elapsed.TotalSeconds -ge 30) { $taskFailure = 'Recorded process cleanup deadline exceeded.'; break }
        if ($taskIdentity.ProcessId -eq $taskRootIdentity.ProcessId) { continue }
        $taskLive = $taskLiveById[[int]$taskIdentity.ProcessId]
        if ($taskLive -and $taskLive.CreationDate -eq $taskIdentity.CreationDate -and $taskLive.CommandLine -ceq $taskIdentity.CommandLine -and $taskLive.ParentProcessId -eq $taskIdentity.ParentProcessId) {
            try { $taskOwnedProcess = [Diagnostics.Process]::GetProcessById([int]$taskIdentity.ProcessId) } catch [ArgumentException] { continue }
            try { $taskOwnedProcess.Kill($true); $null = $taskOwnedProcess.WaitForExit(5000) }
            finally { $taskOwnedProcess.Dispose() }
        }
    }
    if ($taskStdout -and $taskStdout.IsCompleted) { [IO.File]::WriteAllText($taskPrefix + '.stdout.log', $taskStdout.GetAwaiter().GetResult()) }
    if ($taskStderr -and $taskStderr.IsCompleted) { [IO.File]::WriteAllText($taskPrefix + '.stderr.log', $taskStderr.GetAwaiter().GetResult()) }
    $taskLiveById = @{}
    if ($taskKnownFilter) {
        foreach ($taskKnownLive in @(Get-CimInstance Win32_Process -Filter $taskKnownFilter -OperationTimeoutSec 8)) {
            $taskLiveById[[int]$taskKnownLive.ProcessId] = $taskKnownLive
        }
    }
    foreach ($taskIdentity in @($taskIdentities.Values)) {
        if ($taskCleanupClock.Elapsed.TotalSeconds -ge 45) { $taskFailure = 'Recorded process audit deadline exceeded.'; break }
        $taskLive = $taskLiveById[[int]$taskIdentity.ProcessId]
        if ($taskLive -and $taskLive.CreationDate -eq $taskIdentity.CreationDate) { $taskRemaining += [int]$taskIdentity.ProcessId }
    }
    $taskReceipt = [ordered]@{
        label = $Label; executable = $taskStartInfo.FileName; arguments = $Arguments
        launchedPid = $taskLaunchedPid; launchedAt = $taskLaunchedAt; launchingParentPid = $PID
        started = $taskRootIdentity.CreationDate; pid = $taskRootIdentity.ProcessId
        parentPid = $taskRootIdentity.ParentProcessId; commandLine = $taskRootIdentity.CommandLine
        elapsedSeconds = $taskClock.Elapsed.TotalSeconds; exitCode = $taskExitCode
        timedOut = $taskTimedOut; failure = $taskFailure; remainingRecordedProcesses = $taskRemaining
        recordedIdentities = @($taskIdentities.Values | Select-Object ProcessId, ParentProcessId, CreationDate, CommandLine)
        unobservedDescendantsProvenAbsent = $false
    }
    [IO.File]::WriteAllText($taskPrefix + '.json', ($taskReceipt | ConvertTo-Json -Depth 6))
    $taskProcess.Dispose()
}
Get-Content -LiteralPath ($taskPrefix + '.stdout.log') -ErrorAction SilentlyContinue | Select-Object -Last 25
Get-Content -LiteralPath ($taskPrefix + '.stderr.log') -ErrorAction SilentlyContinue | Select-Object -Last 15
if ($taskFailure -or $taskRemaining.Count -gt 0) { throw "$Label failed: $taskFailure; remaining=$($taskRemaining.Count)" }
if ($taskExitCode -ne 0) { throw "$Label exited with $taskExitCode" }
