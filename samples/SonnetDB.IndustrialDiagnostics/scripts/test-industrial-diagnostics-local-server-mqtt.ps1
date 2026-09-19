$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-ForReadyServer([string]$url, [System.Diagnostics.Process]$process, [string]$standardErrorPath) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            $standardError = if (Test-Path -LiteralPath $standardErrorPath) { Get-Content -LiteralPath $standardErrorPath -Raw } else { '' }
            throw "Local SonnetDB Server exited before readiness. $standardError"
        }

        try {
            $response = Invoke-WebRequest -Uri "$url/healthz/ready" -SkipHttpErrorCheck -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch {
            # Server is still binding its HTTP and MQTT listeners.
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Local SonnetDB Server did not become ready at $url within 45 seconds."
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('m27-industrial-local-mqtt-' + [guid]::NewGuid().ToString('N'))
$server = $null
$environmentKeys = @(
    'SONNETDB_Kestrel__Endpoints__Http__Url',
    'SONNETDB_Kestrel__Endpoints__FrameH2__Url',
    'SONNETDB_SonnetDBServer__DataRoot',
    'SONNETDB_SonnetDBServer__Mqtt__Enabled',
    'SONNETDB_SonnetDBServer__Mqtt__Port',
    'SONNETDB_SonnetDBServer__Mqtt__WebSocketPath',
    'SONNETDB_SonnetDBServer__Tokens__m27-industrial-local-token')
$previousEnvironment = @{}

try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $httpPort = Get-FreeTcpPort
    $framePort = Get-FreeTcpPort
    $mqttPort = Get-FreeTcpPort
    $serverUrl = "http://127.0.0.1:$httpPort"
    $token = 'm27-industrial-local-token'

    foreach ($key in $environmentKeys) { $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
    $env:SONNETDB_Kestrel__Endpoints__Http__Url = $serverUrl
    $env:SONNETDB_Kestrel__Endpoints__FrameH2__Url = "http://127.0.0.1:$framePort"
    $env:SONNETDB_SonnetDBServer__DataRoot = (Join-Path $root 'data')
    $env:SONNETDB_SonnetDBServer__Mqtt__Enabled = 'true'
    $env:SONNETDB_SonnetDBServer__Mqtt__Port = [string]$mqttPort
    $env:SONNETDB_SonnetDBServer__Mqtt__WebSocketPath = ''
    [Environment]::SetEnvironmentVariable('SONNETDB_SonnetDBServer__Tokens__m27-industrial-local-token', 'admin', 'Process')

    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    $serverProject = Join-Path $repositoryRoot 'src\SonnetDB\SonnetDB.csproj'
    $sampleProject = Join-Path $PSScriptRoot '..\SonnetDB.IndustrialDiagnostics.csproj'
    $serverOutput = Join-Path $root 'server.stdout.log'
    $serverError = Join-Path $root 'server.stderr.log'
    $server = Start-Process -FilePath dotnet -ArgumentList @('run', '-c', 'Release', '--no-restore', '--project', $serverProject) `
        -WorkingDirectory $repositoryRoot -RedirectStandardOutput $serverOutput -RedirectStandardError $serverError -PassThru -WindowStyle Hidden

    Wait-ForReadyServer $serverUrl $server $serverError

    & dotnet run -c Release --no-restore --project $sampleProject -- `
        --token $token --server $serverUrl --database industrialmqtt --transport mqtt --mqtt-host 127.0.0.1 --mqtt-port $mqttPort --output (Join-Path $root 'report')
    if ($LASTEXITCODE -ne 0) { throw "MQTT-only industrial journey failed with exit code $LASTEXITCODE." }

    $report = Get-Content -LiteralPath (Join-Path $root 'report\industrial-diagnostics-report.json') -Raw | ConvertFrom-Json
    if ([string]$report.status -ne 'PASS' -or [string]$report.dataStatus -ne 'PASS' -or [string]$report.transportStatus -ne 'PASS') {
        throw 'MQTT-only journey must report PASS for data and transport.'
    }
    if ([int64]$report.rowsWritten -ne 9) { throw 'MQTT-only journey must acknowledge all nine telemetry rows.' }
    if (@($report.queryMatchedDevices) -notcontains 'pump-03') { throw 'MQTT-only SQL query did not observe pump-03.' }
    if ([string]$report.copilot.status -ne 'NOT_READY' -or [bool]$report.copilot.completionReceived) {
        throw 'The local MQTT journey must not be represented as Copilot provider evidence.'
    }

    Write-Output 'Local Server + authenticated MQTT broker industrial journey passed.'
}
finally {
    if ($null -ne $server) {
        # `dotnet run` owns a child SonnetDB process; terminate the exact process tree
        # so the WAL handle is released before the temporary fixture is removed.
        if (-not $server.HasExited) {
            & taskkill.exe /PID $server.Id /T /F *> $null
        }
        try { $server.WaitForExit(10000) | Out-Null } catch { }
    }
    foreach ($key in $environmentKeys) { [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process') }
    if (Test-Path -LiteralPath $root) {
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 19) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
    }
}
