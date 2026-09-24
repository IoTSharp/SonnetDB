<#
.SYNOPSIS
  Verifies ServerRelay across two real loopback Server processes.
.DESCRIPTION
  Each Server owns a separate DataRoot. Only the explicitly configured relay
  journal is shared. A deterministic loopback provider pauses before answering
  so the second Server must follow a live owner, then a second run verifies
  hard-kill failure sealing without another provider or tool execution.
  This is LOCAL_ONLY evidence; it is not a real-model or deployed HA gate.
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Join-Path $PSScriptRoot '..\..\..\..'),
    [string] $ServerDll,
    [ValidateRange(60, 180)][int] $TimeoutSeconds = 120,
    [switch] $KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
if (-not $IsWindows) { throw 'This smoke uses Windows process identity evidence.' }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if ([string]::IsNullOrWhiteSpace($ServerDll)) {
    $ServerDll = Join-Path $RepoRoot 'src\SonnetDB\bin\Release\net10.0\SonnetDB.dll'
}
$ServerDll = (Resolve-Path -LiteralPath $ServerDll).Path
$dotnetPath = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$script:deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$taskToken = 'm27-multi-' + [guid]::NewGuid().ToString('N')
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) $taskToken
$journalPath = Join-Path $temporaryRoot 'shared\relay.json'
$providerStatePath = Join-Path $temporaryRoot 'provider.json'
$releasePath = Join-Path $temporaryRoot 'release-live'
$stopPath = Join-Path $temporaryRoot 'stop-provider'
$servers = [Collections.Generic.List[object]]::new()
$jobs = [Collections.Generic.List[object]]::new()
$cleanupErrors = [Collections.Generic.List[string]]::new()
$report = $null
$databaseName = 'm27_multi_db'
$headers = @{ Authorization = 'Bearer m27_multi_token' }

function Assert-Deadline([string] $Operation) {
    if ([DateTimeOffset]::UtcNow -ge $script:deadline) { throw "Deadline exceeded: $Operation" }
}

function Wait-Condition([scriptblock] $Condition, [string] $Operation, [int] $Attempts = 160) {
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        Assert-Deadline $Operation
        if (& $Condition) { return }
        if (($attempt % 40) -eq 39) { Write-Host "Waiting: $Operation ($($attempt + 1)/$Attempts)" }
        Start-Sleep -Milliseconds 100
    }
    throw "Attempt limit exceeded: $Operation ($Attempts attempts)"
}

function Read-Json([string] $Path) {
    if (Test-Path -LiteralPath $Path) {
        try {
            $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
            if ($state.status -eq 'failed') { throw "Reader failed: $($state.failure)" }
            return $state
        }
        catch {
            if ($_.Exception.Message -like 'Reader failed:*') { throw }
            return $null
        }
    }
    return $null
}

function Get-Port([int[]] $Excluded) {
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        Assert-Deadline 'allocate loopback port'
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        try {
            $listener.Start()
            $candidate = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
            if ($Excluded -notcontains $candidate) { return $candidate }
        }
        finally { $listener.Stop() }
    }
    throw 'Loopback port allocation exhausted 8 attempts.'
}

function Get-Identity([int] $ProcessId) {
    $entry = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -OperationTimeoutSec 2
    if ($null -eq $entry) { return $null }
    return [pscustomobject]@{
        pid = [int]$entry.ProcessId
        parentPid = [int]$entry.ParentProcessId
        created = [string]$entry.CreationDate
        commandLine = [string]$entry.CommandLine
    }
}

function Get-OwnedTree([int] $RootId) {
    $items = [Collections.Generic.List[object]]::new()
    $parents = @($RootId)
    $treeDeadline = [DateTimeOffset]::UtcNow.AddSeconds(8)
    for ($depth = 0; $depth -lt 4 -and $parents.Count -gt 0; $depth++) {
        $next = [Collections.Generic.List[int]]::new()
        foreach ($parentId in $parents) {
            if ([DateTimeOffset]::UtcNow -ge $treeDeadline) { throw 'Process tree evidence timed out.' }
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $parentId" -OperationTimeoutSec 2)
            if ($children.Count + $items.Count -gt 32) { throw 'Process tree exceeds 32 children.' }
            foreach ($child in $children) {
                $identity = Get-Identity ([int]$child.ProcessId)
                if ($null -eq $identity) { continue }
                if ($identity.commandLine -notlike "*$taskToken*" -and $identity.commandLine -notmatch '(?i)conhost\.exe') {
                    throw "Unrecognized child process: $($identity.pid)"
                }
                $items.Add($identity)
                $next.Add($identity.pid)
            }
        }
        $parents = $next.ToArray()
    }
    if ($parents.Count -gt 0) { throw 'Process tree exceeds 4 levels.' }
    return $items.ToArray()
}

function Stop-OwnedServer([object] $Entry) {
    $current = Get-Identity $Entry.process.Id
    if ($null -eq $current) {
        if (@(Get-OwnedTree $Entry.process.Id).Count -ne 0) { throw 'Exited Server left descendants.' }
        return
    }
    if (($current | ConvertTo-Json -Compress) -cne ($Entry.identity | ConvertTo-Json -Compress) -or
        $current.commandLine -notlike "*$taskToken*" -or $current.parentPid -ne $PID) {
        throw "Refusing to terminate changed or unowned PID $($Entry.process.Id)."
    }
    $children = @(Get-OwnedTree $Entry.process.Id)
    foreach ($child in $children) {
        $observed = Get-Identity $child.pid
        if ($null -ne $observed -and ($observed | ConvertTo-Json -Compress) -cne ($child | ConvertTo-Json -Compress)) {
            throw "Child PID $($child.pid) changed identity."
        }
    }
    $Entry.process.Kill($true)
    if (-not $Entry.process.WaitForExit(5000)) { throw 'Owned Server did not exit within 5 seconds.' }
    foreach ($child in $children) {
        $remaining = Get-Identity $child.pid
        if ($null -ne $remaining -and $remaining.created -eq $child.created) { throw "Owned child $($child.pid) remained alive." }
    }
    if (@(Get-OwnedTree $Entry.process.Id).Count -ne 0) { throw 'Owned Server left descendants.' }
}

function Start-Server([string] $Name, [int] $Port, [int] $ProviderPort) {
    Assert-Deadline "start $Name"
    $dataRoot = Join-Path $temporaryRoot $Name
    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    $info = [Diagnostics.ProcessStartInfo]::new($dotnetPath)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $RepoRoot
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in @($ServerDll, '--contentRoot', $RepoRoot, '--M27:SmokeOwner', $taskToken,
        '--Kestrel:Endpoints:Http:Url', "http://127.0.0.1:$Port", '--Kestrel:Endpoints:FrameH2:Url', 'http://127.0.0.1:0')) {
        $info.ArgumentList.Add($argument)
    }
    $environment = @{
        'SONNETDB_SONNETDBSERVER__DATAROOT' = $dataRoot
        'SONNETDB_SONNETDBSERVER__ALLOWANONYMOUSPROBES' = 'true'
        'SONNETDB_SONNETDBSERVER__TOKENS__m27_multi_token' = 'admin'
        'SONNETDB_SONNETDBSERVER__MQTT__ENABLED' = 'false'
        'SONNETDB_SONNETDBSERVER__COAP__ENABLED' = 'false'
        'SONNETDB_SONNETDBSERVER__LINEPROTOCOLUDP__ENABLED' = 'false'
        'SONNETDB_SONNETDBSERVER__MODBUS__ENABLED' = 'false'
        'SONNETDB_SONNETDBSERVER__COPILOT__ENABLED' = 'true'
        'SONNETDB_SONNETDBSERVER__COPILOT__INTERNALONLY' = 'true'
        'SONNETDB_SONNETDBSERVER__COPILOT__SERVERRELAYJOURNALPATH' = $journalPath
        'SONNETDB_SONNETDBSERVER__COPILOT__CHAT__PROVIDER' = 'openai'
        'SONNETDB_SONNETDBSERVER__COPILOT__CHAT__ENDPOINT' = "http://127.0.0.1:$ProviderPort/v1/"
        'SONNETDB_SONNETDBSERVER__COPILOT__CHAT__APIKEY' = 'm27-local-provider-key'
        'SONNETDB_SONNETDBSERVER__COPILOT__CHAT__MODEL' = 'm27-multi-model'
        'SONNETDB_SONNETDBSERVER__COPILOT__CHAT__TIMEOUTSECONDS' = '50'
        'SONNETDB_SONNETDBSERVER__COPILOT__DOCS__AUTOINGESTONSTARTUP' = 'false'
        'SONNETDB_SONNETDBSERVER__COPILOT__SKILLS__AUTOINGESTONSTARTUP' = 'false'
    }
    foreach ($key in $environment.Keys) { $info.Environment[$key] = $environment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    $entry = [pscustomobject]@{
        name = $Name; process = $process; identity = $null; dataRoot = $dataRoot
        stdout = $process.StandardOutput.ReadToEndAsync(); stderr = $process.StandardError.ReadToEndAsync()
    }
    $servers.Add($entry)
    $entry.identity = Get-Identity $process.Id
    if ($null -eq $entry.identity -or $entry.identity.parentPid -ne $PID -or $entry.identity.commandLine -notlike "*$taskToken*") {
        throw "Missing process ownership evidence for $Name."
    }
    Write-Host "Started $Name PID=$($process.Id) at $($entry.identity.created)"
    return $entry
}

function Start-Reader([string] $Name, [int] $Port, [string] $RunId) {
    $path = Join-Path $temporaryRoot "$Name.json"
    $job = Start-ThreadJob -Name "$taskToken-$Name" -ArgumentList $Port,$RunId,$databaseName,$path -ScriptBlock {
        param($Port, $RunId, $Database, $Path)
        $ErrorActionPreference = 'Stop'
        $events = [Collections.Generic.List[object]]::new()
        $client = [Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(50)
        $cancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(50))
        $response = $null
        $reader = $null
        function Save-State([string] $Status, [string] $Failure = '') {
            $state = @{ status = $Status; events = $events.ToArray(); failure = $Failure } | ConvertTo-Json -Depth 24 -Compress
            $temporary = $Path + '.tmp'
            [IO.File]::WriteAllText($temporary, $state)
            [IO.File]::Move($temporary, $Path, $true)
        }
        try {
            $message = 'Inspect available measurements and answer the local multi-instance smoke.'
            $body = @{ db=$Database; messages=@(@{role='user'; content=$message}); message=$message
                conversationId=$RunId; runId=$RunId; mode='read-only'; docsK=0; skillsK=0; model='m27-multi-model' } | ConvertTo-Json -Depth 8
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "http://127.0.0.1:$Port/v1/copilot/chat")
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer','m27_multi_token')
            $request.Content = [Net.Http.StringContent]::new($body,[Text.Encoding]::UTF8,'application/json')
            try {
                $response = $client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
                $response.EnsureSuccessStatusCode() | Out-Null
                $reader = [IO.StreamReader]::new($response.Content.ReadAsStream())
                for ($lineNumber=0; $lineNumber -lt 64; $lineNumber++) {
                    $cancel.Token.ThrowIfCancellationRequested()
                    $line = $reader.ReadLineAsync($cancel.Token).AsTask().GetAwaiter().GetResult()
                    if ($null -eq $line) { Save-State 'completed'; return }
                    if (-not [string]::IsNullOrWhiteSpace($line)) { $events.Add(($line | ConvertFrom-Json)); Save-State 'reading' }
                }
                throw 'NDJSON response exceeded 64 lines.'
            }
            finally { $request.Dispose() }
        }
        catch { Save-State 'failed' $_.Exception.Message; throw }
        finally {
            if ($null -ne $reader) { $reader.Dispose() }
            if ($null -ne $response) { $response.Dispose() }
            $cancel.Dispose(); $client.Dispose()
        }
    }
    $jobs.Add($job)
    return [pscustomobject]@{ job=$job; path=$path }
}

function Assert-Events([object[]] $Events, [string] $RunId, [string] $Outcome) {
    $expected = @('start','retrieval','tool_call','tool_result',$Outcome,'done')
    if ($Events.Count -ne $expected.Count) { throw "Unexpected event count: $($Events.Count)" }
    for ($index=0; $index -lt $expected.Count; $index++) {
        Assert-Deadline 'check bounded event sequence'
        if ($Events[$index].type -cne $expected[$index] -or $Events[$index].sequence -ne ($index+1) -or
            $Events[$index].runId -cne $RunId -or $Events[$index].cursor -cne "${RunId}:$($index+1)") {
            throw "Unexpected event at sequence $($index+1)."
        }
    }
    if ($Events[2].toolCallId -cne $Events[3].toolCallId -or [string]::IsNullOrWhiteSpace($Events[2].toolCallId)) {
        throw 'Tool call/result identity mismatch.'
    }
}

try {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $journalPath)) | Out-Null
    $portA = Get-Port @()
    $portB = Get-Port @($portA)
    $providerPort = Get-Port @($portA,$portB)
    $provider = Start-ThreadJob -Name "$taskToken-provider" -ArgumentList $providerPort,$providerStatePath,$releasePath,$stopPath,$TimeoutSeconds -ScriptBlock {
        param($Port,$StatePath,$ReleasePath,$StopPath,$Lifetime)
        $ErrorActionPreference = 'Stop'
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/v1/")
        $providerDeadline = [DateTimeOffset]::UtcNow.AddSeconds($Lifetime)
        $calls=0; $plans=0; $answers=0
        function Save-Provider([string] $Status) {
            $json=@{status=$Status; calls=$calls; plannerCalls=$plans; answerCalls=$answers} | ConvertTo-Json -Compress
            [IO.File]::WriteAllText($StatePath+'.tmp',$json)
            [IO.File]::Move($StatePath+'.tmp',$StatePath,$true)
        }
        try {
            $listener.Start(); Save-Provider 'ready'
            for ($handled=0; $handled -lt 12 -and [DateTimeOffset]::UtcNow -lt $providerDeadline; $handled++) {
                $pending=$listener.GetContextAsync()
                for ($poll=0; $poll -lt 1800 -and -not $pending.IsCompleted; $poll++) {
                    if ((Test-Path -LiteralPath $StopPath) -or [DateTimeOffset]::UtcNow -ge $providerDeadline) { return }
                    Start-Sleep -Milliseconds 100
                }
                if (-not $pending.IsCompleted) { throw 'Provider accept attempt limit reached.' }
                $context=$pending.GetAwaiter().GetResult()
                try {
                    if ($context.Request.HttpMethod -eq 'GET' -and $context.Request.Url.AbsolutePath -eq '/v1/models') {
                        $content='{"data":[{"id":"m27-multi-model"}]}'
                    }
                    elseif ($context.Request.HttpMethod -ne 'POST' -or $context.Request.Url.AbsolutePath -ne '/v1/chat/completions' -or
                        $context.Request.Headers['Authorization'] -cne 'Bearer m27-local-provider-key') {
                        throw 'Unexpected provider request.'
                    }
                    else {
                        $input=[IO.StreamReader]::new($context.Request.InputStream)
                        try { $payload=$input.ReadToEnd() | ConvertFrom-Json } finally { $input.Dispose() }
                        $calls++
                        if ([string]$payload.messages[0].content -like '*工具规划器*') {
                            $plans++
                            $content=if (($plans % 2) -eq 1) { '{"tools":[{"name":"list_measurements"}]}' } else { '{"tools":[]}' }
                        }
                        else {
                            $answers++; Save-Provider 'answer-blocked'
                            for ($pause=0; $pause -lt 500; $pause++) {
                                if ((Test-Path -LiteralPath $StopPath) -or [DateTimeOffset]::UtcNow -ge $providerDeadline) { return }
                                if ($answers -eq 1 -and (Test-Path -LiteralPath $ReleasePath)) { break }
                                Start-Sleep -Milliseconds 100
                            }
                            if ($answers -gt 1 -or -not (Test-Path -LiteralPath $ReleasePath)) { throw 'Provider hold expired.' }
                            $content='M27 live multi-instance answer'
                        }
                    }
                    Save-Provider 'ready'
                    $response=if ($context.Request.HttpMethod -eq 'GET') { $content } else {
                        @{ choices=@(@{message=@{content=$content}}) } | ConvertTo-Json -Compress -Depth 8
                    }
                    $bytes=[Text.Encoding]::UTF8.GetBytes($response)
                    $context.Response.ContentType='application/json'
                    $context.Response.Close($bytes,$false)
                }
                catch {
                    try { $context.Response.StatusCode=500; $context.Response.Close() } catch { }
                    throw
                }
            }
        }
        finally { $listener.Close(); Save-Provider 'stopped' }
    }
    $jobs.Add($provider)
    Wait-Condition { $null -ne (Read-Json $providerStatePath) } 'provider readiness'
    $serverA=Start-Server 'server-a' $portA $providerPort
    $serverB=Start-Server 'server-b' $portB $providerPort
    foreach ($serverPort in @($portA,$portB)) {
        Wait-Condition {
            try { (Invoke-WebRequest "http://127.0.0.1:$serverPort/healthz/ready" -TimeoutSec 1 -SkipHttpErrorCheck).StatusCode -in @(200,204,401,403) }
            catch { $false }
        } "Server readiness on $serverPort" 60
        $creation=Invoke-WebRequest "http://127.0.0.1:$serverPort/v1/db" -Method Post -Headers $headers -ContentType 'application/json' `
            -Body (@{name=$databaseName} | ConvertTo-Json -Compress) -TimeoutSec 5 -SkipHttpErrorCheck
        if ($creation.StatusCode -notin @(200,201)) { throw "Database create returned $($creation.StatusCode)." }
    }
    Write-Host 'Checking live-owner cross-process following.'
    $liveOwner=Start-Reader 'live-owner' $portA 'm27-multi-live'
    Wait-Condition { $state=Read-Json $providerStatePath; $null -ne $state -and $state.answerCalls -eq 1 } 'live owner reaches held answer'
    $liveFollower=Start-Reader 'live-follower' $portB 'm27-multi-live'
    Wait-Condition {
        $state=Read-Json $liveFollower.path
        $null -ne $state -and @($state.events).Count -eq 4 -and $state.events[3].type -eq 'tool_result'
    } 'follower receives live owner tool result before final'
    if ($serverA.process.HasExited -or (Read-Json $providerStatePath).calls -ne 3) { throw 'Owner exited or duplicate provider call occurred.' }
    [IO.File]::WriteAllText($releasePath,'release')
    Wait-Condition { (Read-Json $liveOwner.path).status -eq 'completed' -and (Read-Json $liveFollower.path).status -eq 'completed' } 'live requests finish'
    $liveEvents=@((Read-Json $liveOwner.path).events)
    $followEvents=@((Read-Json $liveFollower.path).events)
    Assert-Events $liveEvents 'm27-multi-live' 'final'
    Assert-Events $followEvents 'm27-multi-live' 'final'
    if (($liveEvents | ConvertTo-Json -Depth 24 -Compress) -cne ($followEvents | ConvertTo-Json -Depth 24 -Compress)) { throw 'Live owner/follower payload mismatch.' }

    Write-Host 'Checking hard-kill owner loss without repeating provider or tool.'
    $crashOwner=Start-Reader 'crash-owner' $portA 'm27-multi-crash'
    Wait-Condition { (Read-Json $providerStatePath).answerCalls -eq 2 } 'crash owner reaches held answer'
    $crashFollower=Start-Reader 'crash-follower' $portB 'm27-multi-crash'
    Wait-Condition {
        $state=Read-Json $crashFollower.path
        $null -ne $state -and @($state.events).Count -eq 4 -and $state.events[3].type -eq 'tool_result'
    } 'crash follower receives existing tool result'
    Stop-OwnedServer $serverA
    Wait-Condition { (Read-Json $crashFollower.path).status -eq 'completed' } 'follower seals lost owner' 250
    $crashEvents=@((Read-Json $crashFollower.path).events)
    Assert-Events $crashEvents 'm27-multi-crash' 'error'
    $replay=Start-Reader 'crash-replay' $portB 'm27-multi-crash'
    Wait-Condition { $state=Read-Json $replay.path; $null -ne $state -and $state.status -eq 'completed' } 'replay sealed failure'
    $replayEvents=@((Read-Json $replay.path).events)
    Assert-Events $replayEvents 'm27-multi-crash' 'error'
    if (($crashEvents | ConvertTo-Json -Depth 24 -Compress) -cne ($replayEvents | ConvertTo-Json -Depth 24 -Compress)) { throw 'Failure replay changed terminal events.' }
    $providerState=Read-Json $providerStatePath
    if ($providerState.calls -ne 6 -or $providerState.plannerCalls -ne 4 -or $providerState.answerCalls -ne 2) { throw 'Provider was repeated during follow or failure recovery.' }
    $report=[ordered]@{
        status='PASS_LOCAL_ONLY'; liveOwnerFollowing='PASS'; hardKillFailureSeal='PASS'; stableFailureReplay='PASS'
        independentDataRoots=$true; sharedJournal=$journalPath; serverDll=$ServerDll
        serverSha256=(Get-FileHash -LiteralPath $ServerDll -Algorithm SHA256).Hash
        scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        providerCalls=$providerState.calls; plannerCalls=$providerState.plannerCalls; answerCalls=$providerState.answerCalls
        toolPairsPerRun=1; realModel='NOT_RUN'; deployedHa='NOT_RUN'; providerFailureResume='UNSUPPORTED_FAIL_CLOSED'
        processes=@($servers | ForEach-Object { $_.identity }); recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { [IO.File]::WriteAllText($stopPath,'stop') }
    foreach ($entry in $servers) {
        try {
            Stop-OwnedServer $entry
            if ($entry.stdout.Wait(2000)) { [IO.File]::WriteAllText((Join-Path $temporaryRoot ($entry.name+'.stdout.log')),$entry.stdout.Result) }
            if ($entry.stderr.Wait(2000)) { [IO.File]::WriteAllText((Join-Path $temporaryRoot ($entry.name+'.stderr.log')),$entry.stderr.Result) }
        }
        catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    foreach ($job in $jobs) {
        try {
            Wait-Job $job -Timeout 2 | Out-Null
            if ($job.State -notin @('Completed','Failed','Stopped')) { Stop-Job $job }
            Remove-Job $job -Force
        }
        catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    if ($null -ne $report) {
        $report.cleanup=if ($cleanupErrors.Count -eq 0) { 'PASS' } else { 'FAILED' }
        $report.cleanupErrors=$cleanupErrors.ToArray()
        $json=$report | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText((Join-Path $temporaryRoot 'report.json'),$json)
        $json
    }
    if ($cleanupErrors.Count -gt 0) { Write-Warning ($cleanupErrors -join '; ') }
    if (-not $KeepArtifacts -and $cleanupErrors.Count -eq 0 -and (Test-Path -LiteralPath $temporaryRoot)) {
        $resolved=(Resolve-Path -LiteralPath $temporaryRoot).Path
        $expected=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $taskToken))
        if ($resolved -cne $expected -or [IO.Path]::GetFileName($resolved) -cne $taskToken) { throw 'Refusing unowned artifact deletion.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $temporaryRoot) { Write-Host "Artifacts: $temporaryRoot" }
}
if ($cleanupErrors.Count -gt 0) { throw 'Task process or temporary-resource cleanup failed.' }
