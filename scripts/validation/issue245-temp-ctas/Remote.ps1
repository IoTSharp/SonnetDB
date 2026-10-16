#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$ServerDirectory,
    [Parameter(Mandatory)][string]$ServerSourceCommit,
    [Parameter(Mandatory)][string]$ConsumerAssembly,
    [string]$RuntimeDirectory,
    [ValidateSet('sugar', 'sugar-native')][string]$SugarMode = 'sugar'
)
. (Join-Path $PSScriptRoot 'RemoteCommon.ps1')
$script:SourceRoot = $PSScriptRoot
$script:Evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$script:ProbeFailures = 0
if (Test-Path -LiteralPath $script:Evidence) { throw 'Use a new remote evidence directory.' }
[IO.Directory]::CreateDirectory($script:Evidence) | Out-Null
[IO.File]::WriteAllText((Join-Path $script:Evidence '.owner'), [Guid]::NewGuid().ToString('N'))
$server = Join-Path $script:Evidence 'server'
$source = [IO.Path]::GetFullPath($ServerDirectory)
$files = foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $destination = Join-Path $server $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw "Server copy mismatch: $relative" }
    @{ path = $relative; sha256 = $hash }
}
Write-Receipt (Join-Path $script:Evidence 'server-snapshot.json') @{ source = $source; sourceCommit = $ServerSourceCommit; files = @($files) }
$temporary = if ($RuntimeDirectory) { [IO.Path]::GetFullPath($RuntimeDirectory) } else { Join-Path $script:Evidence 'temp' }
if (Test-Path -LiteralPath $temporary) { throw 'Use a new runtime directory.' }
[IO.Directory]::CreateDirectory($temporary) | Out-Null
[IO.File]::WriteAllText((Join-Path $temporary '.owner'), [Guid]::NewGuid().ToString('N'))
Write-Receipt (Join-Path $script:Evidence 'runtime.json') @{ path = $temporary }

function Get-AvailablePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

try {
    foreach ($protocol in @('rest', 'frame-http2')) {
        $root = Join-Path $temporary "data/$protocol"
        [IO.Directory]::CreateDirectory($root) | Out-Null
        [IO.File]::WriteAllText((Join-Path $root '.owner'), [Guid]::NewGuid().ToString('N'))
        $bearer = [Guid]::NewGuid().ToString('N')
        foreach ($phase in @('native', 'sugar')) {
            $httpPort = Get-AvailablePort
            do { $h2Port = Get-AvailablePort } while ($h2Port -eq $httpPort)
            $port = if ($protocol -eq 'rest') { $httpPort } else { $h2Port }
            $label = "server-$protocol-$phase"
            $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
            $start.ArgumentList.Add((Join-Path $server 'SonnetDB.dll'))
            $start.WorkingDirectory = $server
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.StandardOutputEncoding = [Text.Encoding]::UTF8
            $start.StandardErrorEncoding = [Text.Encoding]::UTF8
            $serverEnvironment = @{
                ASPNETCORE_ENVIRONMENT = 'Production';
                Kestrel__Endpoints__Http__Url = "http://127.0.0.1:$httpPort";
                Kestrel__Endpoints__Http__Protocols = 'Http1';
                Kestrel__Endpoints__FrameH2__Url = "http://127.0.0.1:$h2Port";
                Kestrel__Endpoints__FrameH2__Protocols = 'Http2';
                SonnetDBServer__DataRoot = (Join-Path $root 'databases');
                SonnetDBServer__Mqtt__Enabled = 'false';
                SonnetDBServer__AutoLoadExistingDatabases = 'true';
                SonnetDBServer__AllowAnonymousProbes = 'true';
                'Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics' = 'Information';
                TEMP = $temporary; TMP = $temporary;
            }
            $serverEnvironment["SonnetDBServer__Tokens__$bearer"] = 'admin'
            foreach ($key in $serverEnvironment.Keys) { $start.Environment[$key] = [string]$serverEnvironment[$key] }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $start
            if (!$process.Start()) { throw 'Server could not start.' }
            $identity = @{ pid = $process.Id; startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O'); executable = 'dotnet'; arguments = @($start.ArgumentList); workingDirectory = $server }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $client = [Net.Http.HttpClient]::new()
            $client.Timeout = [TimeSpan]::FromSeconds(1)
            try {
                $ready = $false
                for ($attempt = 0; $attempt -lt 40; $attempt++) {
                    if ($process.HasExited) { throw 'Server exited before readiness.' }
                    try {
                        $response = $client.GetAsync("http://127.0.0.1:$httpPort/healthz").GetAwaiter().GetResult()
                        $ready = $response.IsSuccessStatusCode
                        $response.Dispose()
                    } catch [Net.Http.HttpRequestException] { }
                    catch [Threading.Tasks.TaskCanceledException] { }
                    if ($ready) { break }
                    Start-Sleep -Milliseconds 250
                }
                if (!$ready) { throw 'Server readiness timed out.' }
                if ($phase -eq 'native') {
                    $management = [Net.Http.HttpClient]::new()
                    $management.Timeout = [TimeSpan]::FromSeconds(15)
                    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "http://127.0.0.1:$port/v1/db")
                    $request.Version = if ($protocol -eq 'rest') { [Version]::new(1, 1) } else { [Version]::new(2, 0) }
                    $request.VersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
                    $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $bearer)
                    $request.Content = [Net.Http.StringContent]::new('{"name":"temp_ctas_contract"}', [Text.Encoding]::UTF8, 'application/json')
                    try {
                        $created = $management.SendAsync($request).GetAwaiter().GetResult()
                        $created.EnsureSuccessStatusCode() | Out-Null
                        Write-Receipt (Join-Path $script:Evidence "create-database-$protocol.json") @{ status = [int]$created.StatusCode; version = $created.Version.ToString() }
                        $created.Dispose()
                    }
                    finally { $request.Dispose(); $management.Dispose() }
                }
                $environment = @{ ISSUE245_CONNECTION = "Data Source=sonnetdb+http://127.0.0.1:$port/temp_ctas_contract;Token=$bearer;Timeout=30;Protocol=$protocol"; TEMP = $temporary; TMP = $temporary; NO_PROXY = 'localhost,127.0.0.1,::1' }
                $mode = if ($phase -eq 'sugar') { $SugarMode } else { 'native' }
                Invoke-TempCtasProbe "$protocol-$phase" @([IO.Path]::GetFullPath($ConsumerAssembly), $mode, 'env:ISSUE245_CONNECTION', "$protocol-$phase") -Environment $environment
            }
            finally {
                $client.Dispose()
                # This exact Process instance is the child created above. Termination is deliberate, not a graceful-stop claim.
                $terminated = !$process.HasExited
                if ($terminated) { $process.Kill($true); $process.WaitForExit() }
                $output = $stdout.GetAwaiter().GetResult()
                [IO.File]::WriteAllText((Join-Path $script:Evidence "$label.stdout.log"), $output)
                [IO.File]::WriteAllText((Join-Path $script:Evidence "$label.stderr.log"), $stderr.GetAwaiter().GetResult())
                Write-Receipt (Join-Path $script:Evidence "$label.json") @{ identity = $identity; terminated = $terminated; exitCode = $process.ExitCode; exited = $process.HasExited; protocol = $protocol; httpPort = $httpPort; h2Port = $h2Port }
                $process.Dispose()
            }
            $expected = if ($protocol -eq 'rest') { 'HTTP/1.1' } else { 'HTTP/2' }
            $requests = @($output -split '\r?\n' | Where-Object { $_ -match 'Request starting ' -and ($_ -match '/v1/db/temp_ctas_contract/' -or $_ -match '/v1/frame') })
            if ($requests.Count -eq 0 -or @($requests | Where-Object { $_ -notmatch [regex]::Escape($expected) }).Count -ne 0) { throw 'Actual data protocol did not match the requested protocol.' }
            if ($protocol -eq 'frame-http2' -and @($requests | Where-Object { $_ -match 'frame' }).Count -eq 0) { throw 'No actual Frame request was observed.' }
            Write-Receipt (Join-Path $script:Evidence "protocol-$protocol-$phase.json") @{ expected = $expected; requestCount = $requests.Count; actualRequests = $requests }
        }
    }
}
finally { }

if ($script:ProbeFailures -ne 0) { exit 1 }
