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
$taskObservationOperations = [Collections.Generic.List[object]]::new()
$taskObservationState = 'complete'
$taskObservationBookkeepingMilliseconds = 0.0

# Snapshot diagnostics observe existing calls only. The separate bookkeeping
# cap excludes the blocking CIM wait and never changes an action/budget guard.
function Get-TaskObservationMilliseconds {
    return $taskWatch.Elapsed.TotalMilliseconds
}

function Add-TaskObservationBookkeeping([double] $Milliseconds) {
    try {
        $script:taskObservationBookkeepingMilliseconds += $Milliseconds
        if ($script:taskObservationBookkeepingMilliseconds -ge 100 -and $script:taskObservationState -eq 'complete') {
            $script:taskObservationState = 'incomplete'
        }
    }
    catch { $script:taskObservationState = 'unknown' }
}

function Start-TaskCimObservation([string] $Phase) {
    if ($Action -ne 'snapshot') { return $null }
    $taskObservationWatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($script:taskObservationState -ne 'complete') { return $null }
        if ($script:taskObservationBookkeepingMilliseconds -ge 100 -or $script:taskObservationOperations.Count -ge 160) {
            $script:taskObservationState = 'incomplete'
            return $null
        }
        if ($Phase -cnotin @('self-handshake', 'parent-chain', 'seed-lookup', 'child-enumeration')) { throw 'Unknown observation phase.' }
        $taskStarted = Get-TaskObservationMilliseconds
        if (-not [double]::IsFinite($taskStarted) -or $taskStarted -lt 0) { throw 'Invalid observation clock.' }
        $taskSlot = [ordered]@{ ordinal = $script:taskCimCount; phase = $Phase; startedMilliseconds = $taskStarted;
            elapsedMilliseconds = $null; outcome = 'unknown'; resultCount = $null }
        $script:taskObservationOperations.Add($taskSlot)
        return $taskSlot
    }
    catch { $script:taskObservationState = 'unknown'; return $null }
    finally { Add-TaskObservationBookkeeping $taskObservationWatch.Elapsed.TotalMilliseconds }
}

function Complete-TaskCimObservation($Slot, [string] $Outcome, $Results, [int] $MaxResults) {
    if ($null -eq $Slot) { return }
    $taskObservationWatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $Slot.outcome = $Outcome
        $taskEnded = Get-TaskObservationMilliseconds
        if (-not [double]::IsFinite($taskEnded) -or $taskEnded -lt $Slot.startedMilliseconds) { throw 'Invalid observation clock.' }
        $Slot.elapsedMilliseconds = $taskEnded - $Slot.startedMilliseconds
        if ($Outcome -eq 'returned') {
            $taskResultCount = if ($null -eq $Results) { 0 } else { @($Results).Count }
            if ($taskResultCount -le $MaxResults) { $Slot.resultCount = $taskResultCount }
            elseif ($script:taskObservationState -eq 'complete') { $script:taskObservationState = 'incomplete' }
        }
    }
    catch { $script:taskObservationState = 'unknown' }
    finally { Add-TaskObservationBookkeeping $taskObservationWatch.Elapsed.TotalMilliseconds }
}

function Get-TaskCimObservation {
    if ($Action -ne 'snapshot') { return $null }
    $taskObservationWatch = $null
    try {
        $taskObservationWatch = [Diagnostics.Stopwatch]::StartNew()
        $taskObserved = @($script:taskObservationOperations.ToArray())
    }
    catch { $script:taskObservationState = 'unknown'; $taskObserved = @() }
    finally {
        try {
            if ($null -ne $taskObservationWatch) { Add-TaskObservationBookkeeping $taskObservationWatch.Elapsed.TotalMilliseconds }
        }
        catch { $script:taskObservationState = 'unknown' }
    }
    return @{ schemaVersion = 1; state = $script:taskObservationState; operations = $taskObserved }
}

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

function Get-TaskCim([int] $ProcessId, [switch] $Fresh, [string] $Phase = 'seed-lookup') {
    Assert-TaskBudget
    if (-not $Fresh -and $script:taskCimCache.ContainsKey($ProcessId)) {
        return $script:taskCimCache[$ProcessId]
    }
    $script:taskCimCount++
    $taskObservationSlot = $null
    try { $taskObservationSlot = Start-TaskCimObservation $Phase }
    catch { $script:taskObservationState = 'unknown' }
    $taskObservationOutcome = 'threw'
    $taskRaw = $null
    try {
        $taskRaw = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -OperationTimeoutSec 20
        $taskObservationOutcome = 'returned'
    }
    finally {
        try { Complete-TaskCimObservation $taskObservationSlot $taskObservationOutcome $taskRaw 1 }
        catch { $script:taskObservationState = 'unknown' }
    }
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
        $taskRawParent = Get-TaskCim $taskParentId -Phase 'parent-chain'
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
    $taskSelf = Convert-TaskIdentity (Get-TaskCim $PID -Phase 'self-handshake')
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
            $taskIdentity = Convert-TaskIdentity (Get-TaskCim $taskProcessId -Phase 'seed-lookup')
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
                $taskObservationSlot = $null
                try { $taskObservationSlot = Start-TaskCimObservation 'child-enumeration' }
                catch { $script:taskObservationState = 'unknown' }
                $taskObservationOutcome = 'threw'
                $taskChildren = @()
                try {
                    $taskChildren = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $taskProcessId" -OperationTimeoutSec 20)
                    $taskObservationOutcome = 'returned'
                }
                finally {
                    try { Complete-TaskCimObservation $taskObservationSlot $taskObservationOutcome $taskChildren $taskMaxProcesses }
                    catch { $script:taskObservationState = 'unknown' }
                }
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
                    # .NET MainWindowHandle discovers an owned visible,
                    # ownerless top-level window. Refresh clears its cached
                    # window lookup; zero alone does not diagnose the product.
                    $taskWindowWatch = [Diagnostics.Stopwatch]::StartNew()
                    $taskWindowHandle = 0L
                    $taskWindowTitle = ''
                    $taskWindowAttempts = 0
                    for ($taskWindowAttempt = 0; $taskWindowAttempt -lt 10 -and $taskWindowWatch.Elapsed.TotalSeconds -lt 2; $taskWindowAttempt++) {
                        Assert-TaskBudget
                        $taskWindowAttempts++
                        $taskProcess.Refresh()
                        if ($taskProcess.HasExited) { break }
                        $taskWindowHandle = $taskProcess.MainWindowHandle.ToInt64()
                        $taskWindowTitle = $taskProcess.MainWindowTitle
                        if ($taskWindowHandle -ne 0) { break }
                        Start-Sleep -Milliseconds 100
                    }
                    $taskDiscovery = @{ strategy = 'System.Diagnostics.Process.Refresh/MainWindowHandle'; attempts = $taskWindowAttempts;
                        elapsedMilliseconds = $taskWindowWatch.ElapsedMilliseconds; mainWindowHandle = $taskWindowHandle;
                        mainWindowTitle = $taskWindowTitle; processExitedBeforeClose = $taskProcess.HasExited }
                    if ($taskWindowHandle -eq 0) {
                        $taskResult = @{ acted = $false; method = 'CloseMainWindow'; discovery = $taskDiscovery; reason = 'native-main-window-not-identified' }
                    }
                    else {
                        Assert-TaskBudget
                        $taskBeforeClose = Convert-TaskIdentity (Get-TaskCim ([int] $taskExpected.processId) -Fresh)
                        if (-not (Test-TaskIdentity $taskExpected $taskBeforeClose)) { throw 'Studio identity changed during native window discovery; close refused.' }
                        $taskAccepted = $taskProcess.CloseMainWindow()
                        $taskResult = @{ acted = $taskAccepted; method = 'CloseMainWindow'; discovery = $taskDiscovery }
                    }
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
    [Console]::Out.WriteLine((@{ kind = 'result'; result = $taskResult; cimQueries = $taskCimCount; cachedPids = $taskCimCache.Count; elapsedSeconds = $taskWatch.Elapsed.TotalSeconds; cimObservation = (Get-TaskCimObservation) } | ConvertTo-Json -Depth 20 -Compress))
}
catch {
    # Never echo the payload, native bootstrap, request headers or environment.
    $taskPrimaryError = $_
    [Console]::Out.WriteLine((@{ kind = 'error'; message = $taskPrimaryError.Exception.Message; cimQueries = $taskCimCount; cachedPids = $taskCimCache.Count; elapsedSeconds = $taskWatch.Elapsed.TotalSeconds; cimObservation = (Get-TaskCimObservation) } | ConvertTo-Json -Depth 20 -Compress))
    exit 1
}
