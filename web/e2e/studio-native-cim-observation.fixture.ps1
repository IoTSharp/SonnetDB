param(
    [ValidateSet('Micro', 'Full')]
    [string] $Mode = 'Micro',
    [string] $CancelFile = ''
)

# Pure injection fixture: never launches a child or invokes live CIM. The root
# runs this file through its retained-handle/identity managed command wrapper.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$fixtureWatch = [Diagnostics.Stopwatch]::StartNew()
$fixtureSeconds = if ($Mode -eq 'Micro') { 10 } else { 120 }
$fixtureDeadline = [DateTime]::UtcNow.AddSeconds($fixtureSeconds)
$fixtureSourcePath = Join-Path $PSScriptRoot 'studio-native-process.ps1'
$fixtureSource = [IO.File]::ReadAllText($fixtureSourcePath)
if ($fixtureSource.Length -gt 25000) { throw 'Fixture source cap exceeded.' }
$fixtureTokens = $null
$fixtureErrors = $null
$fixtureAst = [Management.Automation.Language.Parser]::ParseInput($fixtureSource, [ref] $fixtureTokens, [ref] $fixtureErrors)
if ($fixtureErrors.Count -ne 0) { throw 'Production helper AST has errors.' }
$fixtureFunctionNames = @('Get-TaskObservationMilliseconds', 'Add-TaskObservationBookkeeping', 'Start-TaskCimObservation',
    'Complete-TaskCimObservation', 'Get-TaskCimObservation', 'Assert-TaskBudget', 'Set-TaskCachedCim', 'Get-TaskCim',
    'Convert-TaskIdentity', 'Get-TaskParentChain')
$fixtureDefinitions = @($fixtureAst.FindAll({ param($Node) $Node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false))
if ($fixtureDefinitions.Count -gt 16) { throw 'Function discovery result cap exceeded.' }
$fixtureSnapshotStart = $fixtureSource.IndexOf('    if ($Action -eq ''snapshot'') {', [StringComparison]::Ordinal)
$fixtureSnapshotEnd = $fixtureSource.IndexOf("`n    else {", $fixtureSnapshotStart, [StringComparison]::Ordinal)
$fixtureSelfStart = $fixtureSource.IndexOf('    $taskSelf = Convert-TaskIdentity', [StringComparison]::Ordinal)
$fixtureSelfEnd = $fixtureSource.IndexOf('    [Console]::Out.WriteLine', $fixtureSelfStart, [StringComparison]::Ordinal)
if ($fixtureSnapshotStart -lt 0 -or $fixtureSnapshotEnd -le $fixtureSnapshotStart -or
    $fixtureSelfStart -lt 0 -or $fixtureSelfEnd -le $fixtureSelfStart) { throw 'Production block boundaries absent.' }
$fixtureSnapshotBlock = [scriptblock]::Create($fixtureSource.Substring($fixtureSnapshotStart, $fixtureSnapshotEnd - $fixtureSnapshotStart))
$fixtureSelfBlock = [scriptblock]::Create($fixtureSource.Substring($fixtureSelfStart, $fixtureSelfEnd - $fixtureSelfStart))

function Assert-FixtureBound {
    if ([DateTime]::UtcNow -ge $fixtureDeadline -or $fixtureWatch.Elapsed.TotalSeconds -ge $fixtureSeconds) { throw 'Fixture deadline exceeded.' }
    if ($CancelFile -and [IO.File]::Exists($CancelFile)) { throw 'Fixture cancelled.' }
}

function Assert-Fixture([bool] $Condition, [string] $Message) {
    Assert-FixtureBound
    if (-not $Condition) { throw $Message }
}

function New-FixtureRow([int] $ProcessId, [int] $ParentProcessId = 0, [string] $CommandLine = 'fixture-owned') {
    return [pscustomobject]@{ ProcessId = $ProcessId; ParentProcessId = $ParentProcessId; CommandLine = $CommandLine;
        CreationDate = [DateTime]::Parse('2026-10-08T00:36:00Z').ToUniversalTime(); ExecutablePath = 'fixture-only.exe' }
}

function Get-CimInstance([string] $ClassName, [string] $Filter, [int] $OperationTimeoutSec) {
    Assert-FixtureBound
    if ($script:fixtureQueries.Count -ge 160) { throw 'Fixture query count cap exceeded.' }
    $script:fixtureQueries.Add(@{ class = $ClassName; filter = $Filter; timeout = $OperationTimeoutSec })
    $script:fixtureClock += $script:fixtureQueryMilliseconds
    if ($script:fixtureAdvanceTaskSeconds) {
        $script:taskWatch.Elapsed.TotalSeconds += $script:fixtureQueryMilliseconds / 1000
    }
    if ($null -ne $script:fixtureQueryException) { throw $script:fixtureQueryException }
    if ($script:fixtureRows.ContainsKey($Filter)) { return $script:fixtureRows[$Filter] }
    # A missing CIM row emits no pipeline objects. Explicit $null becomes one
    # array item in @(...), which would invent a null child during enumeration.
    return
}

[string[]] $fixtureCases = if ($Mode -eq 'Micro') { @('self') } else {
    @('self', 'cache-null', 'parent-boundary', 'child-cache', 'primary-error', 'start-clock', 'end-clock',
        'append-error', 'observer-error', 'bookkeeping-cap', 'returned-before-budget', 'query-count-cap', 'result-cap', 'mutations')
}
if ($fixtureCases.Count -gt 14) { throw 'Fixture case cap exceeded.' }
$fixturePassed = 0
$fixtureExecutedCases = [Collections.Generic.List[string]]::new()
for ($fixtureCaseIndex = 0; $fixtureCaseIndex -lt $fixtureCases.Count -and $fixtureCaseIndex -lt 14; $fixtureCaseIndex++) {
    Assert-FixtureBound
    # Restore exact production definitions before each injected failure case.
    for ($fixtureDefinitionIndex = 0; $fixtureDefinitionIndex -lt $fixtureFunctionNames.Count -and $fixtureDefinitionIndex -lt 10; $fixtureDefinitionIndex++) {
        Assert-FixtureBound
        $fixtureName = $fixtureFunctionNames[$fixtureDefinitionIndex]
        $fixtureMatches = @($fixtureDefinitions.Where({ $_.Name -ceq $fixtureName }))
        Assert-Fixture ($fixtureMatches.Count -eq 1) 'Production function discovery must be unique.'
        . ([scriptblock]::Create($fixtureMatches[0].Extent.Text))
    }
    $script:Action = 'snapshot'
    $script:taskWatch = [pscustomobject]@{ Elapsed = [pscustomobject]@{ TotalSeconds = 0.0; TotalMilliseconds = 0.0 } }
    $script:taskCimCount = 0
    $script:taskMaxProcesses = 64
    $script:taskCimCache = [Collections.Generic.Dictionary[int, object]]::new()
    $script:taskObservationOperations = [Collections.Generic.List[object]]::new()
    $script:taskObservationState = 'complete'
    $script:taskObservationBookkeepingMilliseconds = 0.0
    $script:fixtureQueries = [Collections.Generic.List[object]]::new()
    $script:fixtureRows = [Collections.Generic.Dictionary[string, object]]::new()
    $script:fixtureClock = 0.0
    $script:fixtureClockCalls = 0
    $script:fixtureClockFailAt = 0
    $script:fixtureQueryMilliseconds = 21537.2204
    $script:fixtureAdvanceTaskSeconds = $false
    $script:fixtureQueryException = $null
    function Get-TaskObservationMilliseconds {
        $script:fixtureClockCalls++
        if ($script:fixtureClockFailAt -eq $script:fixtureClockCalls) { throw 'Injected observation clock failure.' }
        return $script:fixtureClock
    }
    $fixtureCase = $fixtureCases[$fixtureCaseIndex]
    switch ($fixtureCase) {
        'self' {
            $script:fixtureRows["ProcessId = $PID"] = New-FixtureRow $PID
            . $fixtureSelfBlock
            $fixtureValue = Get-TaskCimObservation
            Assert-Fixture ($taskSelf.processId -eq $PID -and $taskCimCount -eq 1 -and $fixtureQueries.Count -eq 1) 'Handshake query/result changed.'
            Assert-Fixture ($fixtureQueries[0].class -ceq 'Win32_Process' -and $fixtureQueries[0].filter -ceq "ProcessId = $PID" -and $fixtureQueries[0].timeout -eq 20) 'Original CIM arguments changed.'
            Assert-Fixture ($fixtureValue.operations.Count -eq 1 -and $fixtureValue.operations[0].phase -ceq 'self-handshake' -and
                $fixtureValue.operations[0].elapsedMilliseconds -eq 21537.2204 -and $fixtureValue.operations[0].resultCount -eq 1) 'Micro timing/phase/result count missing.'
        }
        'cache-null' {
            $fixtureFirst = Get-TaskCim 101
            $fixtureSecond = Get-TaskCim 101
            Assert-Fixture ($null -eq $fixtureFirst -and $null -eq $fixtureSecond -and $fixtureQueries.Count -eq 1 -and
                $taskCimCount -eq 1 -and $taskObservationOperations.Count -eq 1 -and $taskObservationOperations[0].resultCount -eq 0) 'Null cache became an additional query/slot.'
        }
        'parent-boundary' {
            $fixtureIdentity = Convert-TaskIdentity (New-FixtureRow 201 202)
            $fixtureChain = @(Get-TaskParentChain $fixtureIdentity)
            Assert-Fixture ($fixtureChain.Count -eq 0 -and $taskParentBoundary.reason -ceq 'exited' -and
                $fixtureQueries.Count -eq 1 -and $taskObservationOperations[0].phase -ceq 'parent-chain') 'Exited parent boundary changed.'
            $script:fixtureRows['ProcessId = 203'] = New-FixtureRow 203 0 ''
            $fixtureIdentity.parentProcessId = 203
            $fixtureChain = @(Get-TaskParentChain $fixtureIdentity)
            Assert-Fixture ($fixtureChain.Count -eq 0 -and $taskParentBoundary.reason -ceq 'ancestor-not-inspectable' -and $fixtureQueries.Count -eq 2) 'Uninspectable parent boundary changed.'
        }
        'child-cache' {
            $script:fixtureRows['ProcessId = 201'] = New-FixtureRow 201
            $script:fixtureRows['ParentProcessId = 201'] = @(New-FixtureRow 202 201)
            $taskPayload = @{ processIds = @(201); descendants = $true }
            . $fixtureSnapshotBlock
            Assert-Fixture ($taskResult.inspected -eq 2 -and $taskResult.identities.Count -eq 2 -and $fixtureQueries.Count -eq 3 -and
                $taskCimCache.Count -eq 2 -and $taskObservationOperations.Count -eq 3) 'Child/BFS cache reuse changed.'
            Assert-Fixture ($taskObservationOperations[0].phase -ceq 'seed-lookup' -and $taskObservationOperations[1].phase -ceq 'child-enumeration' -and
                $taskObservationOperations[1].resultCount -eq 1 -and $taskObservationOperations[2].resultCount -eq 0) 'Child observation differs from real calls.'
        }
        'primary-error' {
            $script:fixtureQueryException = [InvalidOperationException]::new('Original query exception.')
            $fixtureCaught = $null
            try { Get-TaskCim 101 } catch { $fixtureCaught = $_.Exception }
            Assert-Fixture ([object]::ReferenceEquals($fixtureCaught, $fixtureQueryException)) 'Observation replaced the original query exception object.'
            Assert-Fixture ($taskObservationOperations.Count -eq 1 -and $taskObservationOperations[0].outcome -ceq 'threw' -and
                $null -eq $taskObservationOperations[0].resultCount -and $taskCimCache.Count -eq 0) 'Thrown query evidence changed.'
        }
        'start-clock' {
            $script:fixtureClockFailAt = 1
            $script:fixtureRows['ProcessId = 101'] = New-FixtureRow 101
            $fixtureRaw = Get-TaskCim 101
            Assert-Fixture ($fixtureRaw.ProcessId -eq 101 -and $fixtureQueries.Count -eq 1 -and $taskObservationState -ceq 'unknown' -and
                $taskObservationOperations.Count -eq 0 -and $taskCimCache.Count -eq 1) 'Start clock failure altered query/cache.'
        }
        'end-clock' {
            $script:fixtureClockFailAt = 2
            $script:fixtureQueryException = [InvalidOperationException]::new('Original end-clock primary.')
            $fixtureCaught = $null
            try { Get-TaskCim 101 } catch { $fixtureCaught = $_.Exception }
            Assert-Fixture ([object]::ReferenceEquals($fixtureCaught, $fixtureQueryException) -and $taskObservationState -ceq 'unknown' -and
                $taskObservationOperations[0].outcome -ceq 'threw' -and $null -eq $taskObservationOperations[0].elapsedMilliseconds) 'End clock failure masked the query exception or prefix.'
        }
        'append-error' {
            $script:fixtureRows['ProcessId = 101'] = New-FixtureRow 101
            $fixtureRaw = Get-TaskCim 101
            $script:fixtureObservationPrefix = $script:taskObservationOperations.ToArray()
            $script:taskObservationOperations = [pscustomobject]@{ Count = 1 }
            $script:taskObservationOperations | Add-Member ScriptMethod Add { param($Value) throw 'Injected append failure.' }
            $script:taskObservationOperations | Add-Member ScriptMethod ToArray { return $script:fixtureObservationPrefix }
            $script:fixtureRows['ProcessId = 102'] = New-FixtureRow 102
            $fixtureRaw = Get-TaskCim 102
            $fixtureValue = Get-TaskCimObservation
            Assert-Fixture ($fixtureRaw.ProcessId -eq 102 -and $fixtureQueries.Count -eq 2 -and $taskObservationState -ceq 'unknown' -and
                $fixtureValue.operations.Count -eq 1 -and [object]::ReferenceEquals($fixtureValue.operations[0], $fixtureObservationPrefix[0])) 'Append failure altered query or dropped retained prefix.'
        }
        'observer-error' {
            function Start-TaskCimObservation { param($Phase) throw 'Injected observation start failure.' }
            $script:fixtureRows['ProcessId = 101'] = New-FixtureRow 101
            $fixtureRaw = Get-TaskCim 101
            Assert-Fixture ($fixtureRaw.ProcessId -eq 101 -and $fixtureQueries.Count -eq 1 -and $taskObservationState -ceq 'unknown') 'Observer entry failure changed query.'
            # Restore the real entry function, then fail its completion boundary.
            $fixtureStart = @($fixtureDefinitions.Where({ $_.Name -ceq 'Start-TaskCimObservation' }))[0]
            . ([scriptblock]::Create($fixtureStart.Extent.Text))
            $script:taskObservationState = 'complete'
            function Complete-TaskCimObservation { param($Slot, $Outcome, $Results, $MaxResults) throw 'Injected completion failure.' }
            $script:fixtureQueryException = [InvalidOperationException]::new('Original completion primary.')
            $fixtureCaught = $null
            try { Get-TaskCim 102 } catch { $fixtureCaught = $_.Exception }
            Assert-Fixture ([object]::ReferenceEquals($fixtureCaught, $fixtureQueryException) -and $fixtureQueries.Count -eq 2 -and
                $taskObservationState -ceq 'unknown') 'Observer completion failure masked primary.'
        }
        'bookkeeping-cap' {
            $script:taskObservationBookkeepingMilliseconds = 100
            $fixtureFirst = Get-TaskCim 101
            $fixtureSecond = Get-TaskCim 102
            Assert-Fixture ($fixtureQueries.Count -eq 2 -and $taskCimCount -eq 2 -and $taskObservationOperations.Count -eq 0 -and
                $taskObservationState -ceq 'incomplete') 'Observation cap changed actual query allowance.'
        }
        'returned-before-budget' {
            $script:taskCimCount = 159
            $script:fixtureRows['ProcessId = 101'] = New-FixtureRow 101
            $fixtureCaught = $null
            try { Get-TaskCim 101 } catch { $fixtureCaught = $_.Exception }
            Assert-Fixture ($null -ne $fixtureCaught -and $fixtureCaught.Message.StartsWith('Native process helper budget exceeded') -and
                $fixtureQueries.Count -eq 1 -and $taskCimCount -eq 160 -and $taskCimCache.Count -eq 0 -and
                $taskObservationOperations[0].outcome -ceq 'returned' -and $taskObservationOperations[0].ordinal -eq 160) 'Post-query budget refusal or returned evidence changed.'
            # A different original guard: the same synchronous query returns
            # after the wall budget, then Set-TaskCachedCim refuses it.
            $script:taskCimCount = 0
            $script:taskCimCache.Clear()
            $script:taskObservationOperations = [Collections.Generic.List[object]]::new()
            $script:taskObservationState = 'complete'
            $script:taskObservationBookkeepingMilliseconds = 0
            $script:fixtureQueries.Clear()
            $script:fixtureClock = 0
            $script:fixtureAdvanceTaskSeconds = $true
            $fixtureCaught = $null
            try { Get-TaskCim 101 } catch { $fixtureCaught = $_.Exception }
            Assert-Fixture ($null -ne $fixtureCaught -and $fixtureCaught.Message.StartsWith('Native process helper budget exceeded') -and
                $taskWatch.Elapsed.TotalSeconds -ge 20 -and $fixtureQueries.Count -eq 1 -and $taskCimCount -eq 1 -and $taskCimCache.Count -eq 0 -and
                $taskObservationOperations[0].outcome -ceq 'returned' -and $taskObservationOperations[0].elapsedMilliseconds -eq 21537.2204) 'Original wall refusal or late returned elapsed changed.'
            try { Get-TaskCim 101 } catch { }
            Assert-Fixture ($fixtureQueries.Count -eq 1 -and $taskCimCount -eq 1 -and $taskCimCache.Count -eq 0) 'Query after original wall refusal was dispatched.'
        }
        'query-count-cap' {
            $script:fixtureRows['ProcessId = 101'] = New-FixtureRow 101
            for ($fixtureQueryIndex = 0; $fixtureQueryIndex -lt 159; $fixtureQueryIndex++) {
                Assert-FixtureBound
                $fixtureRaw = Get-TaskCim 101 -Fresh
            }
            $fixtureCaught = $null
            try { Get-TaskCim 101 -Fresh } catch { $fixtureCaught = $_.Exception }
            Assert-Fixture ($fixtureQueries.Count -eq 160 -and $taskCimCount -eq 160 -and $taskObservationOperations.Count -le 160 -and
                $fixtureCaught.Message.StartsWith('Native process helper budget exceeded')) 'Original 160th-query refusal changed.'
            $fixtureBefore = $fixtureQueries.Count
            try { Get-TaskCim 101 -Fresh } catch { }
            Assert-Fixture ($fixtureQueries.Count -eq $fixtureBefore) 'Query after count refusal was dispatched.'
        }
        'result-cap' {
            $script:fixtureRows['ProcessId = 101'] = @((New-FixtureRow 101), (New-FixtureRow 102))
            $fixtureRaw = @(Get-TaskCim 101)
            Assert-Fixture ($fixtureRaw.Count -eq 2 -and $null -eq $taskObservationOperations[0].resultCount -and
                $taskObservationState -ceq 'incomplete') 'Lookup diagnostic cardinality altered original return.'
            $script:taskObservationOperations = [Collections.Generic.List[object]]::new()
            $script:taskObservationState = 'complete'
            $script:taskObservationBookkeepingMilliseconds = 0
            $script:taskCimCount = 0
            $script:taskCimCache.Clear()
            $script:fixtureRows['ProcessId = 201'] = New-FixtureRow 201
            $fixtureChildren = [Collections.Generic.List[object]]::new()
            for ($fixtureChildIndex = 0; $fixtureChildIndex -lt 65; $fixtureChildIndex++) {
                Assert-FixtureBound
                $fixtureChildren.Add((New-FixtureRow (300 + $fixtureChildIndex) 201))
            }
            $script:fixtureRows['ParentProcessId = 201'] = $fixtureChildren.ToArray()
            $taskPayload = @{ processIds = @(201); descendants = $true }
            $fixtureCaught = $null
            try { . $fixtureSnapshotBlock } catch { $fixtureCaught = $_.Exception }
            Assert-Fixture ($fixtureCaught.Message -ceq 'Descendant process cap exceeded.' -and $taskObservationOperations.Count -eq 2 -and
                $null -eq $taskObservationOperations[1].resultCount -and $taskObservationState -ceq 'incomplete') 'Child cap refusal or diagnostic result cap changed.'
        }
        'mutations' {
            $script:Action = 'close'
            $script:fixtureRows['ProcessId = 101'] = New-FixtureRow 101
            $fixtureRaw = Get-TaskCim 101 -Fresh
            Assert-Fixture ($null -eq (Get-TaskCimObservation) -and $fixtureClockCalls -eq 0 -and $taskObservationOperations.Count -eq 0) 'Close gained snapshot observation.'
            $script:Action = 'kill'
            $fixtureRaw = Get-TaskCim 101 -Fresh
            Assert-Fixture ($null -eq (Get-TaskCimObservation) -and $fixtureClockCalls -eq 0 -and $fixtureQueries.Count -eq 2) 'Kill gained snapshot observation.'
        }
        default { throw 'Unknown fixture case; no pass may be recorded.' }
    }
    $fixturePassed++
    $fixtureExecutedCases.Add($fixtureCase)
    [Console]::Out.WriteLine(('PASS {0} simulatedQueries={1}' -f $fixtureCase, $fixtureQueries.Count))
}
Assert-Fixture ($fixturePassed -eq $fixtureCases.Count) 'Fixture did not finish all bounded cases.'
[Console]::Out.WriteLine((@{ mode = $Mode; passed = $fixturePassed; cases = $fixtureCases.Count;
    executedCases = @($fixtureExecutedCases.ToArray()); elapsedMilliseconds = $fixtureWatch.Elapsed.TotalMilliseconds;
    actualCimQueries = 0; childLaunches = 0 } | ConvertTo-Json -Compress))
