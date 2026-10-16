[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$ConsumerDirectory,
    [Parameter(Mandatory)][string]$ServerDll,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Label,
    [ValidateRange(0, 20)][int]$ExpectedFailures = 0,
    [switch]$Reopen
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$taskRoot = [IO.Path]::GetFullPath($WorkRoot)
$taskConsumer = [IO.Path]::GetFullPath((Join-Path $ConsumerDirectory 'KeylessChloe.dll'))
$taskServer = [IO.Path]::GetFullPath($ServerDll)
$taskId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$taskData = "D:/sndb-t/i253-$Label-$taskId"
$taskLogs = Join-Path $taskRoot 'artifacts/checks'
[IO.Directory]::CreateDirectory($taskLogs) | Out-Null
if (Test-Path -LiteralPath (Join-Path $taskLogs "$Label-matrix.json")) { throw 'Use a new Label.' }
$taskServerSessions = [Collections.Generic.List[object]]::new()
$taskResults = [Collections.Generic.List[object]]::new()
$taskConnections = [Collections.Generic.List[object]]::new()
$taskRunningServer = $null
function Get-TaskPort {
    $taskListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $taskListener.Start(); return $taskListener.LocalEndpoint.Port }
    finally { $taskListener.Stop() }
}
$taskHttpPort = Get-TaskPort
$taskH2Port = Get-TaskPort
$taskHttpUrl = "http://127.0.0.1:$taskHttpPort"
$taskClient = [Net.Http.HttpClient]::new([Net.Http.SocketsHttpHandler]@{ UseProxy = $false })
$taskClient.Timeout = [TimeSpan]::FromSeconds(3)
$taskClient.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'i253-local-probe')

function Start-TaskServer([string]$Phase) {
    $taskStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $taskStart.WorkingDirectory = [IO.Path]::GetDirectoryName($taskServer)
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    foreach ($taskArgument in @($taskServer,
        "--Kestrel:Endpoints:Http:Url=$taskHttpUrl",
        "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:$taskH2Port",
        '--Kestrel:Endpoints:FrameH2:Protocols=Http2',
        "--SonnetDBServer:DataRoot=$taskData/server",
        '--SonnetDBServer:AutoLoadExistingDatabases=true',
        '--SonnetDBServer:Tokens:i253-local-probe=admin',
        '--SonnetDBServer:Mqtt:Enabled=false',
        '--SonnetDBServer:Coap:Enabled=false',
        '--SonnetDBServer:Modbus:Enabled=false',
        '--Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=Information')) { $taskStart.ArgumentList.Add($taskArgument) }
    $taskStart.Environment['DOTNET_PROCESSOR_COUNT'] = '2'
    $taskStart.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $taskStart.Environment['TEMP'] = 'D:/sndb-t/i253-build'
    $taskStart.Environment['TMP'] = 'D:/sndb-t/i253-build'
    $taskProcess = [Diagnostics.Process]::new()
    $taskProcess.StartInfo = $taskStart
    if (-not $taskProcess.Start()) { throw 'Server failed to start.' }
    $script:taskRunningServer = [ordered]@{ phase = $Phase; process = $taskProcess; pid = $taskProcess.Id; startedUtc = $taskProcess.StartTime.ToUniversalTime(); stdout = $taskProcess.StandardOutput.ReadToEndAsync(); stderr = $taskProcess.StandardError.ReadToEndAsync() }
    $taskReady = $false
    $taskClock = [Diagnostics.Stopwatch]::StartNew()
    while ($taskClock.Elapsed.TotalSeconds -lt 30 -and -not $taskProcess.HasExited) {
        try {
            $taskResponse = $taskClient.GetAsync("$taskHttpUrl/healthz").GetAwaiter().GetResult()
            $taskReady = $taskResponse.IsSuccessStatusCode
            $taskResponse.Dispose()
            if ($taskReady) { break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $taskReady) { throw 'Server readiness deadline exceeded.' }
}

function Stop-TaskServer {
    if (-not $script:taskRunningServer) { return }
    $taskOwned = $script:taskRunningServer
    if (-not $taskOwned.process.HasExited) {
        if ($taskOwned.process.StartTime.ToUniversalTime() -ne $taskOwned.startedUtc) { throw 'Owned Server identity changed.' }
        $taskOwned.process.Kill($true)
        if (-not $taskOwned.process.WaitForExit(5000)) { throw 'Owned Server did not exit.' }
    }
    [IO.File]::WriteAllText((Join-Path $taskLogs "$Label-server-$($taskOwned.phase).stdout.log"), $taskOwned.stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $taskLogs "$Label-server-$($taskOwned.phase).stderr.log"), $taskOwned.stderr.GetAwaiter().GetResult())
    $taskServerSessions.Add([ordered]@{ phase = $taskOwned.phase; pid = $taskOwned.pid; startedUtc = $taskOwned.startedUtc; exited = $taskOwned.process.HasExited })
    $taskOwned.process.Dispose()
    $script:taskRunningServer = $null
}

function Invoke-TaskConsumer([object]$Connection, [string]$Phase) {
    $taskRunLabel = "$Label-$($Connection.protocol)-$Phase"
    $taskFailures = if ($Phase -eq 'exercise') { $ExpectedFailures } else { 0 }
    $taskTotal = if ($Phase -eq 'exercise') { 20 } else { 5 }
    try {
        & (Join-Path $PSScriptRoot 'Invoke-CheckedCommand.ps1') -Executable dotnet -Arguments @($taskConsumer, $Connection.connectionString, $Connection.protocol, $Phase) -Label $taskRunLabel -TimeoutSeconds 90 -WorkingDirectory $taskRoot
    } catch { if ($taskFailures -eq 0) { throw } }
    $taskReceipt = Get-Content -LiteralPath (Join-Path $taskLogs "$taskRunLabel.json") -Raw -Encoding utf8 | ConvertFrom-Json
    $taskOutput = [IO.File]::ReadAllText((Join-Path $taskLogs "$taskRunLabel.stdout.log"))
    $taskExpectedExit = if ($taskFailures -eq 0) { 0 } else { 1 }
    if ($taskReceipt.timedOut -or $taskReceipt.failure -or $taskReceipt.remainingRecordedProcesses.Count -gt 0 -or $taskReceipt.exitCode -ne $taskExpectedExit) { throw 'Consumer process boundary failed.' }
    if ($taskOutput -notmatch "COMPLETE\|$($Connection.protocol)\|$Phase\|passed=$($taskTotal-$taskFailures)\|failed=$taskFailures") { throw 'Consumer scenario counts differ.' }
    $taskResults.Add([ordered]@{ label = $taskRunLabel; protocol = $Connection.protocol; phase = $Phase; expectedFailures = $taskFailures; exitCode = $taskReceipt.exitCode })
}

try {
    Start-TaskServer 'exercise'
    foreach ($taskProtocol in @('embedded', 'rest', 'frame-http2')) {
        $taskDatabase = "i253_$($taskProtocol.Replace('-','_'))_$taskId"
        if ($taskProtocol -eq 'embedded') { $taskConnectionString = "Data Source=$taskData/embedded" }
        else {
            $taskContent = [Net.Http.StringContent]::new((@{ name = $taskDatabase } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
            try {
                $taskResponse = $taskClient.PostAsync("$taskHttpUrl/v1/db", $taskContent).GetAwaiter().GetResult()
                try { $null = $taskResponse.EnsureSuccessStatusCode() } finally { $taskResponse.Dispose() }
            } finally { $taskContent.Dispose() }
            $taskPort = if ($taskProtocol -eq 'rest') { $taskHttpPort } else { $taskH2Port }
            $taskConnectionString = "Data Source=sonnetdb+http://127.0.0.1:$taskPort/$taskDatabase;Token=i253-local-probe;Protocol=$taskProtocol;Timeout=15"
        }
        $taskConnection = [ordered]@{ protocol = $taskProtocol; connectionString = $taskConnectionString }
        $taskConnections.Add($taskConnection)
        Invoke-TaskConsumer $taskConnection 'exercise'
    }
    Stop-TaskServer
    if ($Reopen) {
        Start-TaskServer 'reopen'
        foreach ($taskConnection in $taskConnections) { Invoke-TaskConsumer $taskConnection 'reopen' }
        Stop-TaskServer
    }
} finally {
    Stop-TaskServer
    [IO.File]::WriteAllText((Join-Path $taskLogs "$Label-matrix.json"), ([ordered]@{ label = $Label; sourceServer = $taskServer; consumer = $taskConsumer; dataRoot = $taskData; httpPort = $taskHttpPort; h2Port = $taskH2Port; serverSessions = @($taskServerSessions); results = @($taskResults); reopenRequested = [bool]$Reopen } | ConvertTo-Json -Depth 6))
    $taskClient.Dispose()
}
