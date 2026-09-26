[CmdletBinding()]
param([string]$ServerPath, [switch]$RequirePackagedDefaults)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonnetdb-release-network-' + [Guid]::NewGuid().ToString('N'))
$server = $null

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

try {
    $null = New-Item -ItemType Directory -Path $testRoot
    $settingsPath = Join-Path $testRoot 'appsettings.json'
    $settings = Get-Content -LiteralPath (Join-Path $repoRoot 'src/SonnetDB/appsettings.json') -Raw | ConvertFrom-Json
    # Exercise an exposed source configuration, including a future extra endpoint.
    $settings.Kestrel.Endpoints | Add-Member -NotePropertyName Extra -NotePropertyValue ([pscustomobject]@{ Url = 'https://[::]:7443' })
    foreach ($protocol in @('Mqtt', 'Coap', 'LineProtocolUdp', 'Modbus')) { $settings.SonnetDBServer.$protocol.Enabled = $true }
    $settings.SonnetDBServer.Mqtt.ExternalClient.Enabled = $true
    $settings.SonnetDBServer.Mqtt.Sparkplug.Enabled = $true
    $settings.SonnetDBServer.Coap.Dtls.Enabled = $true
    $settings | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $settingsPath -Encoding utf8

    & (Join-Path $PSScriptRoot 'set-release-network-defaults.ps1') -AppSettingsPath $settingsPath
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    foreach ($endpoint in $settings.Kestrel.Endpoints.PSObject.Properties) {
        if (-not ([Uri]$endpoint.Value.Url).IsLoopback) { throw "Non-local bundle endpoint: $($endpoint.Name)" }
    }
    if ($settings.Kestrel.Endpoints.FrameH2.Protocols -ne 'Http2' -or ([Uri]$settings.Kestrel.Endpoints.Extra.Url).Port -ne 7443) {
        throw 'Network isolation must preserve endpoint protocol and port.'
    }
    foreach ($protocol in @('Mqtt', 'Coap', 'LineProtocolUdp', 'Modbus')) {
        if ($settings.SonnetDBServer.$protocol.Enabled -ne $false) { throw "Enabled bundle protocol: $protocol" }
    }
    if ($settings.SonnetDBServer.Mqtt.ExternalClient.Enabled -or $settings.SonnetDBServer.Mqtt.Sparkplug.Enabled -or $settings.SonnetDBServer.Coap.Dtls.Enabled) {
        throw 'Nested network protocols must be disabled.'
    }
    $firstHash = (Get-FileHash -LiteralPath $settingsPath).Hash
    & (Join-Path $PSScriptRoot 'set-release-network-defaults.ps1') -AppSettingsPath $settingsPath
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $firstHash) { throw 'Applying network defaults must be idempotent.' }
    Write-Host 'PASS: exposed IPv4/IPv6 endpoints become loopback; all optional protocols disabled; applying twice is idempotent.'

    if (-not [string]::IsNullOrWhiteSpace($ServerPath)) {
        $serverExecutable = (Resolve-Path -LiteralPath $ServerPath).Path
        if ($RequirePackagedDefaults) {
            $packaged = Get-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($serverExecutable)) 'appsettings.json') -Raw | ConvertFrom-Json
            foreach ($endpoint in $packaged.Kestrel.Endpoints.PSObject.Properties) {
                if (-not ([Uri]$endpoint.Value.Url).IsLoopback) { throw "Published bundle still exposes endpoint $($endpoint.Name)." }
            }
            foreach ($protocol in @('Mqtt', 'Coap', 'LineProtocolUdp', 'Modbus')) {
                if ($packaged.SonnetDBServer.$protocol.Enabled -ne $false) { throw "Published bundle still enables $protocol." }
            }
            if ($packaged.SonnetDBServer.Mqtt.ExternalClient.Enabled -or $packaged.SonnetDBServer.Mqtt.Sparkplug.Enabled -or $packaged.SonnetDBServer.Coap.Dtls.Enabled) {
                throw 'Published bundle still enables nested network protocols.'
            }
            Write-Host 'PASS: published Server configuration has the required local network defaults.'
        }
        $httpPort = Get-FreePort
        do { $framePort = Get-FreePort } while ($framePort -eq $httpPort)
        $settings.Kestrel.Endpoints.PSObject.Properties.Remove('Extra')
        $settings.Kestrel.Endpoints.Http.Url = "http://127.0.0.1:$httpPort"
        $settings.Kestrel.Endpoints.FrameH2.Url = "http://127.0.0.1:$framePort"
        $settings.SonnetDBServer.DataRoot = Join-Path $testRoot 'data'
        $settings | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $settingsPath -Encoding utf8
        $startInfo = [Diagnostics.ProcessStartInfo]::new($serverExecutable)
        $startInfo.WorkingDirectory = $testRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($key in @($startInfo.Environment.Keys)) {
            if ($key.StartsWith('SONNETDB_', [StringComparison]::OrdinalIgnoreCase)) { $null = $startInfo.Environment.Remove($key) }
        }
        $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
        $server = [Diagnostics.Process]::Start($startInfo)
        $stdout = $server.StandardOutput.ReadToEndAsync()
        $stderr = $server.StandardError.ReadToEndAsync()
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($server.HasExited) { throw "Server exited during startup: $($stderr.GetAwaiter().GetResult())" }
            try {
                $response = Invoke-WebRequest -Uri "http://127.0.0.1:$httpPort/healthz/ready" -NoProxy -TimeoutSec 2
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            }
            catch { Start-Sleep -Milliseconds 250 }
        }
        if (-not $ready) { throw 'Server did not become ready on loopback.' }
        $externalAddress = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
            Where-Object { $_.OperationalStatus -eq 'Up' } |
            ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
            ForEach-Object { $_.Address } |
            Where-Object { $_.AddressFamily -eq 'InterNetwork' -and -not [Net.IPAddress]::IsLoopback($_) }) | Select-Object -First 1
        if ($null -eq $externalAddress) { throw 'No non-loopback IPv4 interface is available for negative network verification.' }
        foreach ($port in @($httpPort, $framePort)) {
            $localClient = [Net.Sockets.TcpClient]::new()
            try {
                $null = $localClient.ConnectAsync([Net.IPAddress]::Loopback, $port).Wait(1000)
                if (-not $localClient.Connected) { throw "Bundle Server does not accept loopback connections on port $port." }
            }
            finally { $localClient.Dispose() }
            $client = [Net.Sockets.TcpClient]::new()
            try {
                try { $null = $client.ConnectAsync($externalAddress, $port).Wait(1000) } catch [AggregateException] { }
                if ($client.Connected) { throw "Bundle Server unexpectedly accepts non-loopback connections on port $port." }
            }
            finally { $client.Dispose() }
        }
        Write-Host 'PASS: real Server is ready over loopback and rejects HTTP/Frame connections through the non-loopback interface.'
    }
    elseif ($RequirePackagedDefaults) { throw 'RequirePackagedDefaults requires ServerPath.' }
}
finally {
    if ($null -ne $server) {
        if (-not $server.HasExited) { $server.Kill($true); $server.WaitForExit() }
        $server.Dispose()
    }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to remove network test output outside the temporary directory.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
