[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$ConsumerDirectory,
    [Parameter(Mandatory)][string]$ServerDll,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Label,
    [Parameter(Mandatory)][string]$ConsumerAssembly,
    [ValidateRange(1, 100)][int]$TotalCases = 16,
    [ValidateRange(0, 100)][int]$ExpectedFailures = 0,
    [int]$ExpectedEmbeddedFailures = -1
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$taskRoot = [IO.Path]::GetFullPath($WorkRoot)
$taskConsumer = [IO.Path]::GetFullPath((Join-Path $ConsumerDirectory $ConsumerAssembly))
$taskServer = [IO.Path]::GetFullPath($ServerDll)
$taskRunId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$taskData = "D:/sndb-t/i254-$Label-$taskRunId"
$taskLogs = Join-Path $taskRoot 'artifacts/checks'
[IO.Directory]::CreateDirectory($taskLogs) | Out-Null
[IO.Directory]::CreateDirectory('D:/sndb-t/i254-build') | Out-Null
if (Test-Path -LiteralPath (Join-Path $taskLogs "$Label-matrix.json")) { throw 'Use a new label for each matrix attempt.' }

function Get-TaskPort {
    $taskListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $taskListener.Start(); return $taskListener.LocalEndpoint.Port }
    finally { $taskListener.Stop() }
}

$taskHttpPort = Get-TaskPort
$taskH2Port = Get-TaskPort
$taskHttpUrl = "http://127.0.0.1:$taskHttpPort"
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
    '--SonnetDBServer:Tokens:i254-local-probe=admin',
    '--SonnetDBServer:Mqtt:Enabled=false',
    '--Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=Information',
    '--SonnetDBServer:Coap:Enabled=false',
    '--SonnetDBServer:Modbus:Enabled=false')) { $taskStart.ArgumentList.Add($taskArgument) }
$taskStart.Environment['DOTNET_PROCESSOR_COUNT'] = '2'
$taskStart.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
$taskStart.Environment['TEMP'] = 'D:/sndb-t/i254-build'
$taskStart.Environment['TMP'] = 'D:/sndb-t/i254-build'
$taskProcess = [Diagnostics.Process]::new()
$taskProcess.StartInfo = $taskStart
$taskProcessStart = $null
$taskReceipts = [Collections.Generic.List[object]]::new()
$taskClient = [Net.Http.HttpClient]::new([Net.Http.SocketsHttpHandler]@{ UseProxy = $false })
$taskClient.Timeout = [TimeSpan]::FromSeconds(3)
$taskClient.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'i254-local-probe')
try {
    if (-not $taskProcess.Start()) { throw 'Server did not start.' }
    $taskProcessStart = $taskProcess.StartTime.ToUniversalTime()
    $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
    $taskReady = $false
    $taskDeadline = [Diagnostics.Stopwatch]::StartNew()
    while ($taskDeadline.Elapsed.TotalSeconds -lt 30 -and -not $taskProcess.HasExited) {
        try {
            $taskHealth = $taskClient.GetAsync("$taskHttpUrl/healthz").GetAwaiter().GetResult()
            $taskReady = $taskHealth.IsSuccessStatusCode
            $taskHealth.Dispose()
            if ($taskReady) { break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $taskReady) { throw 'Server readiness deadline exceeded.' }
    foreach ($taskProtocol in @('embedded', 'rest', 'frame-http2')) {
        $taskDatabase = "i254_$($taskProtocol.Replace('-','_'))_$taskRunId"
        if ($taskProtocol -eq 'embedded') { $taskConnection = "Data Source=$taskData/embedded" }
        else {
            $taskContent = [Net.Http.StringContent]::new((@{ name = $taskDatabase } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
            try {
                $taskCreated = $taskClient.PostAsync("$taskHttpUrl/v1/db", $taskContent).GetAwaiter().GetResult()
                try { $null = $taskCreated.EnsureSuccessStatusCode() } finally { $taskCreated.Dispose() }
            } finally { $taskContent.Dispose() }
            $taskPort = if ($taskProtocol -eq 'rest') { $taskHttpPort } else { $taskH2Port }
            $taskConnection = "Data Source=sonnetdb+http://127.0.0.1:$taskPort/$taskDatabase;Token=i254-local-probe;Protocol=$taskProtocol;Timeout=15"
        }
        $taskFailureCount = if ($taskProtocol -eq 'embedded' -and $ExpectedEmbeddedFailures -ge 0) { $ExpectedEmbeddedFailures } else { $ExpectedFailures }
        $taskRunLabel = "$Label-$taskProtocol"
        try {
            & (Join-Path $PSScriptRoot 'Invoke-CheckedCommand.ps1') -Executable dotnet -Arguments @($taskConsumer, $taskConnection, $taskProtocol) -Label $taskRunLabel -TimeoutSeconds 90 -WorkingDirectory $taskRoot
        } catch { if ($taskFailureCount -eq 0) { throw } }
        $taskReceipt = Get-Content -LiteralPath (Join-Path $taskLogs "$taskRunLabel.json") -Raw | ConvertFrom-Json
        $taskOutput = [IO.File]::ReadAllText((Join-Path $taskLogs "$taskRunLabel.stdout.log"))
        if ($taskReceipt.timedOut -or $taskReceipt.failure -or $taskReceipt.remainingRecordedProcesses.Count -gt 0) { throw 'Consumer process boundary failed.' }
        $taskExpectedExit = if ($taskFailureCount -eq 0) { 0 } else { 1 }
        if ($taskReceipt.exitCode -ne $taskExpectedExit -or $taskOutput -notmatch "COMPLETE\|$taskProtocol\|passed=$([int]($TotalCases-$taskFailureCount))\|failed=$taskFailureCount") { throw 'Consumer result did not match the explicit phase contract.' }
        $taskReceipts.Add([ordered]@{ protocol = $taskProtocol; label = $taskRunLabel; exitCode = $taskReceipt.exitCode; expectedFailures = $taskFailureCount })
    }
} finally {
    if ($taskProcessStart -and -not $taskProcess.HasExited) {
        if ($taskProcess.StartTime.ToUniversalTime() -ne $taskProcessStart) { throw 'Server process identity changed.' }
        $taskProcess.Kill($true)
        if (-not $taskProcess.WaitForExit(5000)) { throw 'Owned server did not exit.' }
    }
    if ($taskStdout -and $taskStdout.IsCompleted) { [IO.File]::WriteAllText((Join-Path $taskLogs "$Label-server.stdout.log"), $taskStdout.GetAwaiter().GetResult()) }
    if ($taskStderr -and $taskStderr.IsCompleted) { [IO.File]::WriteAllText((Join-Path $taskLogs "$Label-server.stderr.log"), $taskStderr.GetAwaiter().GetResult()) }
    [IO.File]::WriteAllText((Join-Path $taskLogs "$Label-matrix.json"), ([ordered]@{
        label = $Label; server = $taskServer; consumer = $taskConsumer; serverPid = $taskProcess.Id
        serverStartedUtc = $taskProcessStart; serverExited = $taskProcess.HasExited
        dataRoot = $taskData; httpPort = $taskHttpPort; h2Port = $taskH2Port; results = @($taskReceipts)
    } | ConvertTo-Json -Depth 5))
    $taskClient.Dispose()
    $taskProcess.Dispose()
}
