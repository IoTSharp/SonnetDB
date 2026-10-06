param(
    [Parameter(Mandatory)]
    [ValidateSet('snapshot', 'close', 'kill')]
    [string] $Action
)

# This helper accepts only structured stdin. It never evaluates shell commands,
# discovers tools, or kills a process by name. The Node runner owns its deadline.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
$taskCimCount = 0
$taskMaxProcesses = 64
# A cache lives only for this helper invocation. Null entries are intentional:
# absence must not silently become a different process when a PID is reused.
$taskCimCache = [Collections.Generic.Dictionary[int, object]]::new()

function Assert-TaskBudget {
    if ($taskWatch.Elapsed.TotalSeconds -ge 20 -or $script:taskCimCount -ge 160) {
        throw ('Native process helper budget exceeded (CIM queries={0}, elapsedSeconds={1:F2}, cachedPIDs={2}).' -f $script:taskCimCount, $taskWatch.Elapsed.TotalSeconds, $script:taskCimCache.Count)
    }
}

function Set-TaskCachedCim([int] $ProcessId, $Process) {
    Assert-TaskBudget
    if ($script:taskCimCache.ContainsKey($ProcessId)) {
        $taskPrevious = $script:taskCimCache[$ProcessId]
        if ($null -ne $Process -and ($null -eq $taskPrevious -or
                $taskPrevious.CreationDate.ToUniversalTime().ToString('o') -cne $Process.CreationDate.ToUniversalTime().ToString('o') -or
                [int] $taskPrevious.ParentProcessId -ne [int] $Process.ParentProcessId -or
                [string] $taskPrevious.CommandLine -cne [string] $Process.CommandLine -or
                [string] $taskPrevious.ExecutablePath -cne [string] $Process.ExecutablePath)) {
            throw 'Process identity changed inside the snapshot; PID cache update refused.'
        }
    }
    elseif ($script:taskCimCache.Count -ge 160) {
        throw 'Per-invocation process cache cap exceeded.'
    }
    $script:taskCimCache[$ProcessId] = $Process
}

function Get-TaskCim([int] $ProcessId, [switch] $Fresh) {
    Assert-TaskBudget
    if (-not $Fresh -and $script:taskCimCache.ContainsKey($ProcessId)) {
        return $script:taskCimCache[$ProcessId]
    }
    $script:taskCimCount++
    $taskRaw = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -OperationTimeoutSec 20
    Set-TaskCachedCim $ProcessId $taskRaw
    return $taskRaw
}

function Convert-TaskIdentity($Process) {
    if ($null -eq $Process) { return $null }
    if ([string]::IsNullOrWhiteSpace($Process.CommandLine) -or $null -eq $Process.CreationDate) {
        throw 'A process has no inspectable command line or creation time.'
    }
    return [ordered]@{
        processId = [int] $Process.ProcessId
        parentProcessId = [int] $Process.ParentProcessId
        creationTimeUtc = $Process.CreationDate.ToUniversalTime().ToString('o')
        commandLine = [string] $Process.CommandLine
        executablePath = [string] $Process.ExecutablePath
    }
}

function Test-TaskIdentity($Expected, $Actual) {
    return $null -ne $Actual -and
        [int] $Expected.processId -eq [int] $Actual.processId -and
        [int] $Expected.parentProcessId -eq [int] $Actual.parentProcessId -and
        [string] $Expected.creationTimeUtc -ceq [string] $Actual.creationTimeUtc -and
        [string] $Expected.commandLine -ceq [string] $Actual.commandLine -and
        [string] $Expected.executablePath -ceq [string] $Actual.executablePath
}

function Get-TaskParentChain($Identity) {
    $taskChain = [Collections.Generic.List[object]]::new()
    $taskParentId = [int] $Identity.parentProcessId
    $taskSeen = [Collections.Generic.HashSet[int]]::new()
    $script:taskParentBoundary = $null
    for ($taskDepth = 0; $taskDepth -lt 12 -and $taskParentId -gt 0; $taskDepth++) {
        Assert-TaskBudget
        if (-not $taskSeen.Add($taskParentId)) { throw 'Process ancestry contains a cycle.' }
        $taskRawParent = Get-TaskCim $taskParentId
        if ($null -eq $taskRawParent) {
            $script:taskParentBoundary = @{ processId = $taskParentId; reason = 'exited' }
            break
        }
        if ([string]::IsNullOrWhiteSpace($taskRawParent.CommandLine) -or $null -eq $taskRawParent.CreationDate) {
            $script:taskParentBoundary = @{ processId = $taskParentId; reason = 'ancestor-not-inspectable' }
            break
        }
        $taskParent = Convert-TaskIdentity $taskRawParent
        $taskChain.Add($taskParent)
        $taskParentId = [int] $taskParent.parentProcessId
    }
    if ($taskParentId -gt 0 -and $taskChain.Count -ge 12) { throw 'Process ancestry depth cap exceeded.' }
    return $taskChain.ToArray()
}

function Assert-TaskAncestry($Expected) {
    if (@($Expected.parentChain).Count -gt 12) { throw 'Parent chain cap exceeded.' }
    foreach ($taskParent in @($Expected.parentChain)) {
        Assert-TaskBudget
        # Mutations deliberately refresh ancestry even when the initial helper
        # handshake happened to inspect one of these parents.
        $taskCurrent = Convert-TaskIdentity (Get-TaskCim ([int] $taskParent.processId) -Fresh)
        # An exited parent is permitted for a previously observed orphan. A live
        # process with that PID must still have the recorded identity.
        if ($null -ne $taskCurrent -and -not (Test-TaskIdentity $taskParent $taskCurrent)) {
            throw 'Recorded process ancestry was replaced; action refused.'
        }
    }
}

try {
    $taskSelf = Convert-TaskIdentity (Get-TaskCim $PID)
    $taskSelf.parentChain = @(Get-TaskParentChain $taskSelf)
    $taskSelf.parentChainBoundary = $taskParentBoundary
    [Console]::Out.WriteLine((@{ kind = 'helper'; identity = $taskSelf; version = $PSVersionTable.PSVersion.ToString() } | ConvertTo-Json -Depth 16 -Compress))
    [Console]::Out.Flush()

    $taskInput = [Text.StringBuilder]::new()
    $taskBuffer = [char[]]::new(4096)
    for ($taskChunk = 0; $taskChunk -lt 128; $taskChunk++) {
        Assert-TaskBudget
        $taskRead = [Console]::In.Read($taskBuffer, 0, $taskBuffer.Length)
        if ($taskRead -eq 0) { break }
        [void] $taskInput.Append($taskBuffer, 0, $taskRead)
    }
    if ($taskInput.Length -ge 524288) { throw 'Structured process payload cap exceeded.' }
    $taskPayload = $taskInput.ToString() | ConvertFrom-Json -Depth 20 -DateKind String
    if ($Action -eq 'snapshot') {
        $taskSeeds = @($taskPayload.processIds)
        if ($taskSeeds.Count -lt 1 -or $taskSeeds.Count -gt 8) { throw 'Snapshot seed cap exceeded.' }
        $taskItems = [Collections.Generic.List[object]]::new()
        $taskQueue = [Collections.Generic.Queue[object]]::new()
        $taskSeen = [Collections.Generic.HashSet[int]]::new()
        foreach ($taskSeed in $taskSeeds) {
            Assert-TaskBudget
            $taskQueue.Enqueue(@{ processId = [int] $taskSeed; depth = 0 })
        }
        for ($taskIndex = 0; $taskIndex -lt $taskMaxProcesses -and $taskQueue.Count -gt 0; $taskIndex++) {
            Assert-TaskBudget
            $taskEntry = $taskQueue.Dequeue()
            $taskProcessId = [int] $taskEntry.processId
            if (-not $taskSeen.Add($taskProcessId)) { continue }
            $taskIdentity = Convert-TaskIdentity (Get-TaskCim $taskProcessId)
            if ($null -ne $taskIdentity) {
                $taskIdentity.parentChain = @(Get-TaskParentChain $taskIdentity)
                $taskIdentity.parentChainBoundary = $taskParentBoundary
                $taskIdentity.depth = [int] $taskEntry.depth
                $taskItems.Add($taskIdentity)
            }
            if ($taskPayload.descendants -eq $true -and $null -ne $taskIdentity) {
                if ([int] $taskEntry.depth -ge 12) { throw 'Descendant depth cap exceeded.' }
                Assert-TaskBudget
                $script:taskCimCount++
                $taskChildren = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $taskProcessId" -OperationTimeoutSec 20)
                if ($taskChildren.Count -gt $taskMaxProcesses -or $taskQueue.Count + $taskChildren.Count + $taskItems.Count -gt $taskMaxProcesses) {
                    throw 'Descendant process cap exceeded.'
                }
                foreach ($taskChild in $taskChildren) {
                    Assert-TaskBudget
                    # Child enumeration already supplies the full CIM row. Do
                    # not query that PID again when its BFS entry is dequeued.
                    Set-TaskCachedCim ([int] $taskChild.ProcessId) $taskChild
                    $taskQueue.Enqueue(@{ processId = [int] $taskChild.ProcessId; depth = [int] $taskEntry.depth + 1 })
                }
            }
        }
        if ($taskQueue.Count -gt 0) { throw 'Snapshot process cap exceeded.' }
        $taskResult = @{ identities = @($taskItems.ToArray()); inspected = $taskSeen.Count }
    }
    else {
        $taskExpected = $taskPayload.identity
        if ($null -eq $taskExpected -or [int] $taskExpected.processId -le 0) { throw 'A recorded identity is required.' }
        $taskCurrent = Convert-TaskIdentity (Get-TaskCim ([int] $taskExpected.processId) -Fresh)
        if ($null -eq $taskCurrent) {
            $taskResult = @{ exited = $true; acted = $false; identityReplaced = $false }
        }
        elseif (-not (Test-TaskIdentity $taskExpected $taskCurrent)) {
            throw 'Process identity changed; action refused.'
        }
        else {
            Assert-TaskAncestry $taskExpected
            $taskProcess = [Diagnostics.Process]::GetProcessById([int] $taskExpected.processId)
            try {
                if ($Action -eq 'close') {
                    $taskProcess.Refresh()
                    $taskWindowHandle = $taskProcess.MainWindowHandle.ToInt64()
                    if ($taskWindowHandle -eq 0) { throw 'The owned Studio has no native main window.' }
                    $taskAccepted = $taskProcess.CloseMainWindow()
                    $taskResult = @{ acted = $taskAccepted; method = 'CloseMainWindow'; mainWindowHandle = $taskWindowHandle }
                }
                else {
                    # Each descendant is separately recorded and revalidated by
                    # the runner. Do not implicitly kill an uninspected tree.
                    $taskProcess.Kill($false)
                    $taskExited = $taskProcess.WaitForExit(1500)
                    $taskResult = @{ acted = $true; exited = $taskExited; method = 'KillSingleVerifiedProcess' }
                }
            }
            finally { $taskProcess.Dispose() }
        }
    }
    [Console]::Out.WriteLine((@{ kind = 'result'; result = $taskResult; cimQueries = $taskCimCount; cachedPids = $taskCimCache.Count; elapsedSeconds = $taskWatch.Elapsed.TotalSeconds } | ConvertTo-Json -Depth 20 -Compress))
}
catch {
    # Never echo the payload, native bootstrap, request headers or environment.
    [Console]::Out.WriteLine((@{ kind = 'error'; message = $_.Exception.Message } | ConvertTo-Json -Compress))
    exit 1
}
