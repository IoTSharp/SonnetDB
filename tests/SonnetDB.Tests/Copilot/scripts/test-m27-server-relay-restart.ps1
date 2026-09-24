<#!
.SYNOPSIS
  Runs a loopback-only M27 #340 ServerRelay process-switch smoke test.

.DESCRIPTION
  Starts a local OpenAI-compatible HttpListener and a real SonnetDB server
  process, completes one deterministic Copilot run, terminates that process,
  starts a second process against the same DataRoot, and replays the run by
  runId. The provider call count must remain unchanged during replay.

  This is local evidence only. It does not exercise OAuth, public networking,
  a deployed browser, or a real model. Browser refresh is reported as NOT_RUN.
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Join-Path $PSScriptRoot '..\..\..\..'),
    [string] $ServerDll,
    [ValidateRange(30, 120)]
    [int] $TimeoutSeconds = 90,
    [switch] $KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required.'
}

$script:smokeDeadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

function Assert-SmokeDeadline {
    param([Parameter(Mandatory = $true)][string] $Operation)
    if ([DateTimeOffset]::UtcNow -ge $script:smokeDeadline) {
        throw "Smoke wall-clock deadline exceeded during $Operation."
    }
}

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if ([string]::IsNullOrWhiteSpace($ServerDll)) {
    $ServerDll = Join-Path $RepoRoot 'src\SonnetDB\bin\Debug\net10.0\SonnetDB.dll'
}
$ServerDll = (Resolve-Path -LiteralPath $ServerDll).Path
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source

function Get-FreeTcpPort {
    param([int[]] $ExcludePorts = @())
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw 'Loopback TCP port allocation exceeded its 5-second deadline.'
        }
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        try {
            $listener.Start()
            $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
            if ($ExcludePorts -notcontains $port) { return $port }
        }
        finally {
            $listener.Stop()
        }
    }
    throw "Unable to allocate a loopback TCP port outside the excluded set after 8 attempts."
}

function Write-AtomicText {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][string] $Text)
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [System.IO.File]::WriteAllText($temporary, $Text, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Get-ProcessEvidence {
    param([Parameter(Mandatory = $true)][int] $ProcessId)
    $items = [System.Collections.Generic.List[object]]::new()
    $current = $ProcessId
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    for ($level = 0; $level -lt 4 -and $current -gt 0; $level++) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Process evidence lookup for PID $ProcessId exceeded its 10-second deadline."
        }
        $cim = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $current" -OperationTimeoutSec 2 -ErrorAction SilentlyContinue
        if ($null -eq $cim) { break }
        $items.Add([pscustomobject]@{
            level = $level
            pid = [int]$cim.ProcessId
            parentPid = [int]$cim.ParentProcessId
            commandLine = [string]$cim.CommandLine
            creationDate = [string]$cim.CreationDate
        })
        if ([int]$cim.ParentProcessId -eq $current) { break }
        $current = [int]$cim.ParentProcessId
    }
    return @($items)
}

function Get-DescendantProcessEvidence {
    param(
        [Parameter(Mandatory = $true)][int] $RootProcessId,
        [Parameter(Mandatory = $true)][string] $OwnerToken
    )
    $items = [System.Collections.Generic.List[object]]::new()
    $frontier = [System.Collections.Generic.List[int]]::new()
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    $frontier.Add($RootProcessId)
    [void]$seen.Add($RootProcessId)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    for ($level = 1; $level -le 4 -and $frontier.Count -gt 0; $level++) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Descendant process lookup for PID $RootProcessId exceeded its 10-second deadline."
        }
        $next = [System.Collections.Generic.List[int]]::new()
        foreach ($parentId in @($frontier.ToArray())) {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw "Descendant process lookup for PID $RootProcessId exceeded its 10-second deadline."
            }
            $children = @(Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId = $parentId" -OperationTimeoutSec 2 -ErrorAction Stop)
            foreach ($child in $children) {
                if ([DateTimeOffset]::UtcNow -ge $deadline) {
                    throw "Descendant process lookup for PID $RootProcessId exceeded its 10-second deadline."
                }
                if ($items.Count -ge 32) {
                    throw "Refusing to manage more than 32 descendant processes for PID $RootProcessId."
                }
                $childId = [int]$child.ProcessId
                if (-not $seen.Add($childId)) { continue }
                $commandLine = [string]$child.CommandLine
                $hasOwnerToken = -not [string]::IsNullOrWhiteSpace($commandLine) -and
                    $commandLine.IndexOf($OwnerToken, [System.StringComparison]::Ordinal) -ge 0
                $isKnownConsoleHost = -not [string]::IsNullOrWhiteSpace($commandLine) -and
                    $commandLine -match '(?i)(^|\\)conhost\.exe(\s|$)'
                if (-not $hasOwnerToken -and -not $isKnownConsoleHost) {
                    throw "Refusing to manage descendant PID ${childId}: ownership evidence is missing."
                }
                $items.Add([pscustomobject]@{
                        level = $level
                        pid = $childId
                        parentPid = [int]$child.ParentProcessId
                        commandLine = $commandLine
                        creationDate = [string]$child.CreationDate
                    })
                $next.Add($childId)
            }
        }
        $frontier = $next
    }
    if ($frontier.Count -gt 0) {
        throw "Descendant process tree for PID $RootProcessId exceeded its depth bound of 4."
    }
    return @($items)
}

function Get-ProcessEvidenceFingerprint {
    param([Parameter(Mandatory = $true)][object[]] $Evidence)
    $normalized = [System.Collections.Generic.List[object]]::new()
    foreach ($item in @($Evidence)) {
        $normalized.Add([pscustomobject]@{
                level = [int]$item.level
                pid = [int]$item.pid
                parentPid = [int]$item.parentPid
                commandLine = [string]$item.commandLine
                creationDate = [string]$item.creationDate
            })
    }
    return ($normalized | ConvertTo-Json -Compress -Depth 4)
}

function Get-EventSemanticFingerprint {
    param([Parameter(Mandatory = $true)][object[]] $Events)
    $normalized = [System.Collections.Generic.List[object]]::new()
    foreach ($event in @($Events)) {
        $ordered = [ordered]@{}
        foreach ($property in @($event.PSObject.Properties | Sort-Object Name)) {
            if ($property.Name -in @('runId', 'sequence', 'cursor')) { continue }
            $ordered[$property.Name] = $property.Value
        }
        $normalized.Add([pscustomobject]$ordered)
    }
    return ($normalized | ConvertTo-Json -Compress -Depth 20)
}

function Wait-File {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][scriptblock] $Ready,
        [Parameter(Mandatory = $true)][int] $MaxAttempts
    )
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Assert-SmokeDeadline -Operation "waiting for $Path"
        if (Test-Path -LiteralPath $Path) {
            try {
                $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
                if (& $Ready $value) { return $value }
            }
            catch {
                # The writer may still be replacing the file; retry within the bound.
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for '$Path' after $MaxAttempts attempts."
}

function Wait-HttpReady {
    param(
        [Parameter(Mandatory = $true)][string] $Uri,
        [Parameter(Mandatory = $true)][ValidateRange(1, 90)][int] $MaxAttempts,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Process
    )
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Assert-SmokeDeadline -Operation "waiting for $Uri"
        if ($Process.HasExited) {
            throw "Server process $($Process.Id) exited with code $($Process.ExitCode) before '$Uri' became ready."
        }
        try {
            # 90 attempts × (1s request timeout + 250ms backoff) stays below 120s.
            $response = Invoke-WebRequest -Uri $Uri -Method Get -TimeoutSec 1 -SkipHttpErrorCheck
            if ([int]$response.StatusCode -in @(200, 204, 401, 403)) { return }
        }
        catch {
            # Startup is expected to take a few bounded polling intervals.
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for '$Uri' after $MaxAttempts attempts."
}

function Stop-OwnedServer {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Process,
        [System.Nullable[datetime]] $StartTime,
        [string] $ExpectedCommandLine = '',
        [object[]] $ExpectedParentChain = @(),
        [Parameter(Mandatory = $true)][string] $OwnerToken
    )
    if ($Process.HasExited) {
        $remainingDescendants = @(Get-DescendantProcessEvidence -RootProcessId $Process.Id -OwnerToken $OwnerToken)
        if ($remainingDescendants.Count -gt 0) {
            throw "Owned Server PID $($Process.Id) exited but left descendant processes running."
        }
        return
    }
    $current = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    if ($null -eq $current) { return }
    if ($null -ne $StartTime -and $current.StartTime -ne [datetime]$StartTime) {
        throw "Refusing to stop PID $($Process.Id): process start time changed."
    }

    $currentEvidence = @(Get-ProcessEvidence -ProcessId $Process.Id)
    if ($currentEvidence.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$currentEvidence[0].commandLine)) {
        throw "Refusing to stop PID $($Process.Id): current command line evidence is unavailable."
    }
    $currentCommandLine = [string]$currentEvidence[0].commandLine
    if ([string]::IsNullOrWhiteSpace($ExpectedCommandLine)) {
        if ($currentCommandLine.IndexOf($ServerDll, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Refusing to stop PID $($Process.Id): command line does not identify the requested Server DLL."
        }
        $ExpectedCommandLine = $currentCommandLine
    }
    elseif (-not [string]::Equals($currentCommandLine, $ExpectedCommandLine, [System.StringComparison]::Ordinal)) {
        throw "Refusing to stop PID $($Process.Id): command line changed."
    }
    if ($currentCommandLine.IndexOf($OwnerToken, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Refusing to stop PID $($Process.Id): ownership token is missing."
    }
    if (@($ExpectedParentChain).Count -eq 0) {
        $ExpectedParentChain = $currentEvidence
    }
    $expectedFingerprint = Get-ProcessEvidenceFingerprint -Evidence @($ExpectedParentChain)
    $currentFingerprint = Get-ProcessEvidenceFingerprint -Evidence $currentEvidence
    if (-not [string]::Equals($currentFingerprint, $expectedFingerprint, [System.StringComparison]::Ordinal)) {
        throw "Refusing to stop PID $($Process.Id): parent process chain changed."
    }
    $descendants = @(Get-DescendantProcessEvidence -RootProcessId $Process.Id -OwnerToken $OwnerToken)
    foreach ($descendant in @($descendants | Sort-Object level -Descending)) {
        $child = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($descendant.pid)" -OperationTimeoutSec 2 -ErrorAction SilentlyContinue
        if ($null -eq $child) { continue }
        if (
            [string]$child.CreationDate -ne [string]$descendant.creationDate -or
            [int]$child.ParentProcessId -ne [int]$descendant.parentPid -or
            [string]$child.CommandLine -ne [string]$descendant.commandLine) {
            throw "Refusing to stop descendant PID $($descendant.pid): identity evidence changed."
        }
        $childProcess = Get-Process -Id $descendant.pid -ErrorAction SilentlyContinue
        if ($null -eq $childProcess) { continue }
        Stop-Process -Id $descendant.pid -Force
        $childDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        for ($attempt = 1; $attempt -le 40; $attempt++) {
            if ($childProcess.HasExited) { break }
            if ([DateTimeOffset]::UtcNow -ge $childDeadline) {
                throw "Owned descendant process $($descendant.pid) did not exit within 10 seconds."
            }
            Start-Sleep -Milliseconds 250
        }
        if (-not $childProcess.HasExited) {
            throw "Owned descendant process $($descendant.pid) did not exit within 10 seconds."
        }
    }
    Stop-Process -Id $Process.Id -Force
    $stopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        if ($Process.HasExited) {
            $remainingDescendants = @(Get-DescendantProcessEvidence -RootProcessId $Process.Id -OwnerToken $OwnerToken)
            if ($remainingDescendants.Count -gt 0) {
                throw "Owned Server PID $($Process.Id) exited but left descendant processes running."
            }
            return
        }
        if ([DateTimeOffset]::UtcNow -ge $stopDeadline) { break }
        Start-Sleep -Milliseconds 250
    }
    throw "Owned Server process $($Process.Id) did not exit within 10 seconds."
}

function Start-SonnetDbServer {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][int] $Port,
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [Parameter(Mandatory = $true)][string] $LogRoot,
        [Parameter(Mandatory = $true)][string] $OwnerToken
    )
    $stdout = Join-Path $LogRoot "$Name.stdout.log"
    $stderr = Join-Path $LogRoot "$Name.stderr.log"
    $arguments = @(
        $ServerDll,
        '--contentRoot', $RepoRoot,
        '--M27:SmokeOwner', $OwnerToken,
        '--Kestrel:Endpoints:Http:Url', "http://127.0.0.1:$Port",
        '--Kestrel:Endpoints:FrameH2:Url', 'http://127.0.0.1:0'
    )
    $environment = @{
        SONNETDB_SONNETDBSERVER__DATAROOT = $DataRoot
        SONNETDB_SONNETDBSERVER__AUTOLOADEXISTINGDATABASES = 'true'
        SONNETDB_SONNETDBSERVER__ALLOWANONYMOUSPROBES = 'true'
        'SONNETDB_SONNETDBSERVER__TOKENS__m27_restart_token' = 'admin'
        SONNETDB_SONNETDBSERVER__MQTT__ENABLED = 'false'
        SONNETDB_SONNETDBSERVER__COAP__ENABLED = 'false'
        SONNETDB_SONNETDBSERVER__LINEPROTOCOLUDP__ENABLED = 'false'
        SONNETDB_SONNETDBSERVER__MODBUS__ENABLED = 'false'
        SONNETDB_SONNETDBSERVER__COPILOT__ENABLED = 'true'
        SONNETDB_SONNETDBSERVER__COPILOT__INTERNALONLY = 'true'
        SONNETDB_SONNETDBSERVER__COPILOT__CHAT__PROVIDER = 'openai'
        SONNETDB_SONNETDBSERVER__COPILOT__CHAT__ENDPOINT = "http://127.0.0.1:$providerPort/v1/"
        SONNETDB_SONNETDBSERVER__COPILOT__CHAT__APIKEY = 'm27-local-provider-key'
        SONNETDB_SONNETDBSERVER__COPILOT__CHAT__MODEL = 'm27-restart-model'
        SONNETDB_SONNETDBSERVER__COPILOT__CHAT__TIMEOUTSECONDS = '10'
        SONNETDB_SONNETDBSERVER__COPILOT__DOCS__AUTOINGESTONSTARTUP = 'false'
        SONNETDB_SONNETDBSERVER__COPILOT__SKILLS__AUTOINGESTONSTARTUP = 'false'
    }
    $process = Start-Process -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $RepoRoot `
        -Environment $environment -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
        -WindowStyle Hidden -PassThru
    $evidence = @()
    $evidenceError = $null
    $startTime = $null
    $evidenceDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            $evidence = @(Get-ProcessEvidence -ProcessId $process.Id)
        }
        catch {
            $evidenceError = "Unable to capture Server PID $($process.Id) process evidence: $($_.Exception.Message)"
            break
        }
        if ($evidence.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$evidence[0].commandLine)) {
            break
        }
        if ([DateTimeOffset]::UtcNow -ge $evidenceDeadline) { break }
        Start-Sleep -Milliseconds 100
    }
    try {
        $startTime = $process.StartTime
    }
    catch {
        $evidenceError = "Unable to capture Server PID $($process.Id) start time: $($_.Exception.Message)"
    }
    if ($evidence.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$evidence[0].commandLine)) {
        if ($null -eq $evidenceError) {
            $evidenceError = "Unable to capture bounded ownership evidence for Server PID $($process.Id)."
        }
    }
    elseif ($null -eq $evidenceError) {
        $capturedCommandLine = [string]$evidence[0].commandLine
        if ($capturedCommandLine.IndexOf($ServerDll, [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $capturedCommandLine.IndexOf($OwnerToken, [System.StringComparison]::Ordinal) -lt 0) {
            $evidenceError = "Server PID $($process.Id) command line did not match the requested DLL and ownership token."
        }
    }
    [pscustomobject]@{
        process = $process
        name = $Name
        startTime = $startTime
        stdout = $stdout
        stderr = $stderr
        commandLine = if ($evidence.Count -gt 0) { [string]$evidence[0].commandLine } else { '' }
        ownerToken = $OwnerToken
        parentChain = $evidence
        evidence = $evidence
        evidenceReady = $null -eq $evidenceError
        evidenceError = $evidenceError
    }
}

function Invoke-RelayRequest {
    param(
        [Parameter(Mandatory = $true)][string] $BaseUri,
        [Parameter(Mandatory = $true)][hashtable] $Payload
    )
    Assert-SmokeDeadline -Operation 'ServerRelay request'
    $json = $Payload | ConvertTo-Json -Depth 12 -Compress
    $response = Invoke-WebRequest -Uri "$BaseUri/v1/copilot/chat" -Method Post `
        -Headers @{ Authorization = 'Bearer m27_restart_token' } `
        -ContentType 'application/json' -Body $json -TimeoutSec 20 -SkipHttpErrorCheck
    $responseText = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString([byte[]]$response.Content)
    }
    else {
        [string]$response.Content
    }
    if ($script:temporaryRoot) {
        [System.IO.File]::WriteAllText(
            (Join-Path $script:temporaryRoot 'last-relay-response.ndjson'),
            $responseText,
            [System.Text.UTF8Encoding]::new($false))
    }
    $events = [System.Collections.Generic.List[object]]::new()
    foreach ($line in ($responseText -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $parsed = $line | ConvertFrom-Json
            if ($parsed -is [System.Array]) {
                foreach ($item in $parsed) { [void]$events.Add($item) }
            }
            else {
                [void]$events.Add($parsed)
            }
        }
        catch { throw "Server returned a non-JSON NDJSON line: $line" }
    }
    if ([int]$response.StatusCode -ne 200) {
        throw "Copilot request returned HTTP $($response.StatusCode): $responseText"
    }
    return ,([object[]]$events.ToArray())
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('m27-server-relay-restart-' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $temporaryRoot 'data'
$logRoot = Join-Path $temporaryRoot 'logs'
$providerStatePath = Join-Path $temporaryRoot 'provider-state.json'
$ownerToken = 'm27-restart-' + [guid]::NewGuid().ToString('N')
$serverPort = Get-FreeTcpPort
$providerPort = Get-FreeTcpPort -ExcludePorts @($serverPort)
$databaseName = 'm27_restart_db'
$serverBaseUri = "http://127.0.0.1:$serverPort"
$providerBaseUri = "http://127.0.0.1:$providerPort/v1"
$providerJob = $null
$servers = [System.Collections.Generic.List[object]]::new()
$report = $null

try {
    New-Item -ItemType Directory -Path $dataRoot, $logRoot -Force | Out-Null

    $providerJob = Start-ThreadJob -Name 'm27-server-relay-provider' -ArgumentList @(
        "$providerBaseUri/",
        $providerStatePath,
        $TimeoutSeconds
    ) -ScriptBlock {
        param([string] $Prefix, [string] $StatePath, [int] $LifetimeSeconds)
        $ErrorActionPreference = 'Stop'
        function Write-AtomicText {
            param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][string] $Text)
            $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
            [System.IO.File]::WriteAllText($temporary, $Text, [System.Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temporary -Destination $Path -Force
        }
        $listener = [System.Net.HttpListener]::new()
        $listener.Prefixes.Add($Prefix)
        $calls = 0
        $plannerCalls = 0
        $answerCalls = 0
        $handled = 0
        $stopRequested = $false
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Min($LifetimeSeconds, 110))
        try {
            $listener.Start()
            Write-AtomicText -Path $StatePath -Text (@{
                    status = 'ready'
                    prefix = $Prefix
                    calls = 0
                    plannerCalls = 0
                    answerCalls = 0
                    handled = 0
                } | ConvertTo-Json -Compress)
            for ($poll = 1; $listener.IsListening -and $handled -lt 16 -and [DateTimeOffset]::UtcNow -lt $deadline -and -not $stopRequested -and $poll -le 120; $poll++) {
                $remainingMilliseconds = [int][Math]::Max(1, [Math]::Min(110000, ($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds))
                $contextTask = $listener.GetContextAsync()
                if (-not $contextTask.Wait($remainingMilliseconds)) { break }
                $context = $contextTask.GetAwaiter().GetResult()
                $handled++
                try {
                    $request = $context.Request
                    $response = $context.Response
                    if ($request.Url.AbsolutePath -eq '/v1/__stop') {
                        $stopRequested = $true
                        $payload = '{"status":"stopping"}'
                    }
                    elseif ($request.HttpMethod -eq 'GET' -and $request.Url.AbsolutePath -eq '/v1/models') {
                        $payload = '{"data":[{"id":"m27-restart-model"}]}'
                    }
                    elseif ($request.HttpMethod -eq 'POST' -and $request.Url.AbsolutePath -eq '/v1/chat/completions') {
                        if ($request.Headers['Authorization'] -ne 'Bearer m27-local-provider-key') {
                            $response.StatusCode = 401
                            $payload = '{"error":"unexpected provider credential"}'
                        }
                        else {
                            $reader = [System.IO.StreamReader]::new($request.InputStream, $request.ContentEncoding)
                            try {
                                $requestBody = $reader.ReadToEnd()
                            }
                            finally {
                                $reader.Dispose()
                            }
                            $requestJson = $requestBody | ConvertFrom-Json
                            $systemContent = [string]$requestJson.messages[0].content
                            $calls++
                            if ($systemContent -like '*工具规划器*') {
                                $plannerCalls++
                                if ($plannerCalls -gt 1) {
                                    throw "planner called unexpectedly on request $plannerCalls"
                                }
                                $content = '{"tools":[]}'
                            }
                            else {
                                $answerCalls++
                                if ($answerCalls -gt 1) {
                                    throw "answer called unexpectedly on request $answerCalls"
                                }
                                $content = 'M27 local restart replay answer'
                            }
                            $payload = @{ choices = @(@{ message = @{ content = $content } }) } | ConvertTo-Json -Compress -Depth 8
                        }
                    }
                    else {
                        $response.StatusCode = 404
                        $payload = '{"error":"not found"}'
                    }
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
                    $response.ContentType = 'application/json; charset=utf-8'
                    $response.Close($bytes, $false)
                }
                catch {
                    try {
                        $context.Response.StatusCode = 500
                        $context.Response.Close()
                    }
                    catch { }
                }
                Write-AtomicText -Path $StatePath -Text (@{
                        status = 'ready'
                        prefix = $Prefix
                        calls = $calls
                        plannerCalls = $plannerCalls
                        answerCalls = $answerCalls
                        handled = $handled
                    } | ConvertTo-Json -Compress)
            }
        }
        finally {
            if ($listener.IsListening) { $listener.Stop() }
            $listener.Close()
            Write-AtomicText -Path $StatePath -Text (@{
                    status = 'stopped'
                    prefix = $Prefix
                    calls = $calls
                    plannerCalls = $plannerCalls
                    answerCalls = $answerCalls
                    handled = $handled
                } | ConvertTo-Json -Compress)
        }
    }

    $providerState = Wait-File -Path $providerStatePath -MaxAttempts 80 -Ready { param($value) $value.status -eq 'ready' }
    Assert-SmokeDeadline -Operation 'provider probe'
    $providerProbe = Invoke-WebRequest -Uri "$providerBaseUri/models" -Method Get -TimeoutSec 2 -SkipHttpErrorCheck
    if ([int]$providerProbe.StatusCode -ne 200) {
        throw "Loopback provider probe returned HTTP $($providerProbe.StatusCode)."
    }
    $first = Start-SonnetDbServer -Name 'server-1' -Port $serverPort -DataRoot $dataRoot -LogRoot $logRoot -OwnerToken $ownerToken
    $servers.Add($first)
    if (-not $first.evidenceReady) { throw $first.evidenceError }
    Wait-HttpReady -Uri "$serverBaseUri/healthz/ready" -MaxAttempts ([Math]::Min($TimeoutSeconds * 2, 90)) -Process $first.process

    Assert-SmokeDeadline -Operation 'database creation'
    $databaseBody = @{ name = $databaseName } | ConvertTo-Json -Compress
    $databaseResponse = Invoke-WebRequest -Uri "$serverBaseUri/v1/db" -Method Post `
        -Headers @{ Authorization = 'Bearer m27_restart_token' } `
        -ContentType 'application/json' -Body $databaseBody -TimeoutSec 10 -SkipHttpErrorCheck
    if ([int]$databaseResponse.StatusCode -notin @(200, 201)) {
        throw "Database create returned HTTP $($databaseResponse.StatusCode): $($databaseResponse.Content)"
    }
    try {
        $databaseOperation = $databaseResponse.Content | ConvertFrom-Json
    }
    catch {
        throw "Database create returned invalid JSON: $($databaseResponse.Content)"
    }
    if ([string]$databaseOperation.database -ne $databaseName -or
        [string]$databaseOperation.status -notin @('created', 'exists')) {
        throw "Database create returned an unexpected operation: $($databaseResponse.Content)"
    }

    $payload = @{
        db = $databaseName
        message = 'reply with a deterministic local restart smoke answer'
        messages = @(@{ role = 'user'; content = 'reply with a deterministic local restart smoke answer' })
        conversationId = 'm27-restart-session'
        mode = 'read-only'
        docsK = 0
        skillsK = 0
        model = 'm27-restart-model'
        runId = 'm27-restart-run'
    }
    $firstEvents = Invoke-RelayRequest -BaseUri $serverBaseUri -Payload $payload
    $firstTypes = @($firstEvents | ForEach-Object {
        $typeProperty = $_.PSObject.Properties['type']
        if ($null -eq $typeProperty) { throw "Initial ServerRelay event had no type: $($_ | ConvertTo-Json -Compress -Depth 8)" }
        [string]$typeProperty.Value
    })
    $firstSequences = @($firstEvents | ForEach-Object {
        $sequenceProperty = $_.PSObject.Properties['sequence']
        if ($null -eq $sequenceProperty) { throw "Initial ServerRelay event had no sequence: $($_ | ConvertTo-Json -Compress -Depth 8)" }
        [long]$sequenceProperty.Value
    })
    $expectedEventTypes = @('start', 'retrieval', 'final', 'done')
    if ($firstTypes.Count -ne $expectedEventTypes.Count) {
        throw "Initial ServerRelay event types were not exactly start,retrieval,final,done: $($firstTypes -join ', ')"
    }
    for ($index = 0; $index -lt $expectedEventTypes.Count; $index++) {
        if ($firstTypes[$index] -ne $expectedEventTypes[$index]) {
            throw "Initial ServerRelay event types were not exactly start,retrieval,final,done: $($firstTypes -join ', ')"
        }
        if ($firstSequences[$index] -ne ($index + 1)) {
            throw "Initial ServerRelay sequence was not contiguous 1..4: $($firstSequences -join ', ')"
        }
        if ([string]$firstEvents[$index].runId -ne [string]$payload.runId -or
            [string]$firstEvents[$index].cursor -ne "$($payload.runId):$($index + 1)") {
            throw "Initial ServerRelay event identity was not bound to runId $($payload.runId)."
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$firstEvents[2].answer)) {
        throw 'Initial ServerRelay final event did not contain an answer.'
    }
    $journalPath = Join-Path $dataRoot '.system\copilot-relay-journal.json'
    $journal = Wait-File -Path $journalPath -MaxAttempts 40 -Ready { param($value) @($value.runs).Count -ge 1 -and [bool]$value.runs[0].completed }
    $initialProviderState = Wait-File -Path $providerStatePath -MaxAttempts 40 -Ready {
        param($value)
        [int]$value.calls -eq 2 -and [int]$value.plannerCalls -eq 1 -and [int]$value.answerCalls -eq 1
    }

    Stop-OwnedServer -Process $first.process -StartTime $first.startTime -ExpectedCommandLine $first.commandLine -ExpectedParentChain @($first.parentChain) -OwnerToken $first.ownerToken
    $second = Start-SonnetDbServer -Name 'server-2' -Port $serverPort -DataRoot $dataRoot -LogRoot $logRoot -OwnerToken $ownerToken
    $servers.Add($second)
    if (-not $second.evidenceReady) { throw $second.evidenceError }
    Wait-HttpReady -Uri "$serverBaseUri/healthz/ready" -MaxAttempts ([Math]::Min($TimeoutSeconds * 2, 90)) -Process $second.process

    $replayEvents = Invoke-RelayRequest -BaseUri $serverBaseUri -Payload $payload
    $replayTypes = @($replayEvents | ForEach-Object {
        $typeProperty = $_.PSObject.Properties['type']
        if ($null -eq $typeProperty) { throw "Replay ServerRelay event had no type: $($_ | ConvertTo-Json -Compress -Depth 8)" }
        [string]$typeProperty.Value
    })
    $replaySequences = @($replayEvents | ForEach-Object {
        $sequenceProperty = $_.PSObject.Properties['sequence']
        if ($null -eq $sequenceProperty) { throw "Replay ServerRelay event had no sequence: $($_ | ConvertTo-Json -Compress -Depth 8)" }
        [long]$sequenceProperty.Value
    })
    $providerState = Wait-File -Path $providerStatePath -MaxAttempts 20 -Ready { param($value) $value.status -in @('ready', 'stopped') }
    if ([int]$initialProviderState.calls -ne 2 -or
        [int]$providerState.calls -ne 2 -or
        [int]$providerState.plannerCalls -ne 1 -or
        [int]$providerState.answerCalls -ne 1) {
        throw "Provider call count changed during restart replay: expected calls=2 plannerCalls=1 answerCalls=1, got calls=$($providerState.calls) plannerCalls=$($providerState.plannerCalls) answerCalls=$($providerState.answerCalls)."
    }
    if ($first.process.Id -eq $second.process.Id) {
        throw "Server process was not replaced: both runs used PID $($second.process.Id)."
    }
    if ($replayTypes.Count -ne $expectedEventTypes.Count) {
        throw "Restart replay event types were not exactly start,retrieval,final,done: $($replayTypes -join ', ')"
    }
    for ($index = 0; $index -lt $expectedEventTypes.Count; $index++) {
        if ($replayTypes[$index] -ne $expectedEventTypes[$index]) {
            throw "Restart replay event types were not exactly start,retrieval,final,done: $($replayTypes -join ', ')"
        }
        if ($replaySequences[$index] -ne ($index + 1)) {
            throw "Restart replay sequence was not exactly 1..4: $($replaySequences -join ', ')"
        }
        if ([string]$replayEvents[$index].runId -ne [string]$payload.runId -or
            [string]$replayEvents[$index].cursor -ne "$($payload.runId):$($index + 1)") {
            throw "Restart replay event identity was not bound to runId $($payload.runId)."
        }
    }
    if ([string]$replayEvents[2].answer -ne [string]$firstEvents[2].answer) {
        throw 'Restart replay final answer differed from the original journal payload.'
    }
    if ((Get-EventSemanticFingerprint -Events $replayEvents) -ne (Get-EventSemanticFingerprint -Events $firstEvents)) {
        throw 'Restart replay event payload differed from the original journal payload.'
    }

    $report = [pscustomobject]@{
        schema = 'm27-server-relay-restart-smoke-v1'
        status = 'LOCAL_ONLY'
        serverRestart = 'PASS_LOCAL_ONLY'
        browserRefresh = 'NOT_RUN'
        reason = '真实本机 Server 进程切换和 HTTP replay 已验证；浏览器刷新需要 Web host/auth 联调，本脚本不模拟。'
        provider = [pscustomobject]@{ endpoint = "http://127.0.0.1:$providerPort/v1/"; calls = [int]$providerState.calls; plannerCalls = [int]$providerState.plannerCalls; answerCalls = [int]$providerState.answerCalls }
        initial = [pscustomobject]@{ pid = $first.process.Id; database = $databaseName; events = $firstTypes; sequences = $firstSequences; journalCompleted = [bool]$journal.runs[0].completed }
        replay = [pscustomobject]@{ pid = $second.process.Id; events = $replayTypes; sequences = $replaySequences }
        processEvidence = @($servers | ForEach-Object { [pscustomobject]@{ name = $_.name; pid = $_.process.Id; startTime = $_.startTime; parentChain = $_.evidence } })
        artifacts = if ($KeepArtifacts) { $temporaryRoot } else { $null }
    }
    $report | ConvertTo-Json -Depth 12
}
catch {
    $failure = [pscustomobject]@{
        schema = 'm27-server-relay-restart-smoke-v1'
        status = 'FAIL_LOCAL'
        browserRefresh = 'NOT_RUN'
        reason = $_.Exception.Message
        artifacts = if ($KeepArtifacts) { $temporaryRoot } else { $null }
    }
    $failure | ConvertTo-Json -Depth 8
    throw
}
finally {
    foreach ($entry in @($servers)) {
        try {
            Stop-OwnedServer -Process $entry.process -StartTime $entry.startTime `
                -ExpectedCommandLine $entry.commandLine -ExpectedParentChain @($entry.parentChain) -OwnerToken $entry.ownerToken
        }
        catch { Write-Warning "Server cleanup failed for PID $($entry.process.Id): $($_.Exception.Message)" }
    }
    if ($null -ne $providerJob) {
        try { Invoke-WebRequest -Uri "$providerBaseUri/__stop" -Method Get -TimeoutSec 2 -SkipHttpErrorCheck | Out-Null } catch { }
        try { Wait-Job -Job $providerJob -Timeout 5 -ErrorAction SilentlyContinue | Out-Null } catch { }
        if ($providerJob.State -notin @('Completed', 'Failed', 'Stopped')) {
            try { Stop-Job -Job $providerJob -ErrorAction SilentlyContinue } catch { }
            try { Wait-Job -Job $providerJob -Timeout 2 -ErrorAction SilentlyContinue | Out-Null } catch { }
        }
        if ($providerJob.State -notin @('Completed', 'Failed', 'Stopped')) {
            Write-Warning "Provider thread job $($providerJob.Id) did not stop within the bounded cleanup window."
        }
        try { Remove-Job -Job $providerJob -Force -ErrorAction SilentlyContinue } catch { }
    }
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $temporaryRoot)) {
        $resolvedTemp = $null
        try { $resolvedTemp = (Resolve-Path -LiteralPath $temporaryRoot).Path } catch { }
        $tempPrefix = ([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $pathIsOwned = $null -ne $resolvedTemp -and
            $resolvedTemp.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
            [System.IO.Path]::GetFileName($resolvedTemp).StartsWith('m27-server-relay-restart-', [System.StringComparison]::Ordinal)
        if ($pathIsOwned) {
            $removed = $false
            $lastCleanupError = $null
            $cleanupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
            for ($attempt = 1; $attempt -le 8; $attempt++) {
                if ([DateTimeOffset]::UtcNow -ge $cleanupDeadline) { break }
                try {
                    Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction Stop
                    if (-not (Test-Path -LiteralPath $resolvedTemp)) {
                        $removed = $true
                        break
                    }
                }
                catch {
                    $lastCleanupError = $_.Exception.Message
                }
                if ($attempt -lt 8) { Start-Sleep -Milliseconds 250 }
            }
            if (-not $removed -and (Test-Path -LiteralPath $resolvedTemp)) {
                $suffix = if ($null -eq $lastCleanupError) { '' } else { " Last error: $lastCleanupError" }
                Write-Warning "Temporary M27 artifact directory was not removed after 8 attempts: $resolvedTemp.$suffix"
            }
        }
    }
}
