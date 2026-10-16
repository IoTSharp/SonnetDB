#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$ServerDirectory,
    [Parameter(Mandatory)][string]$ServerSourceLabel,
    [Parameter(Mandatory)][string]$ConsumerDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Receipt([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 30) -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

function Copy-Snapshot([string]$Source, [string]$Destination, [string]$Label) {
    $sourcePath = [IO.Path]::GetFullPath($Source)
    $files = foreach ($file in Get-ChildItem -LiteralPath $sourcePath -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($sourcePath, $file.FullName)
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'Runtime copy hash differs.' }
        @{ path = $relative.Replace('\', '/'); length = $file.Length; sha256 = $hash }
    }
    Write-Receipt (Join-Path $evidence "$Label-snapshot.json") @{ source = $sourcePath; label = $Label; files = @($files) }
}

function Get-AvailablePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

function Invoke-Consumer([string]$Label, [string]$Connection, [string]$Mode) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @((Join-Path $consumer 'StoredProcedureProbe.dll'), '--connection-env', $Label, $Mode)) { $start.ArgumentList.Add($argument) }
    foreach ($key in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY')) { $start.Environment[$key] = '' }
    $start.Environment['NO_PROXY'] = 'localhost,127.0.0.1,::1'
    $start.Environment['TEMP'] = $runtime
    $start.Environment['TMP'] = $runtime
    $start.Environment['ISSUE247_CONNECTION'] = $Connection
    $start.Environment['ISSUE247_EXTERNAL_FIXTURE'] = '1'
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (!$process.Start()) { throw 'Consumer did not start.' }
    $identity = @{ pid = $process.Id; startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O'); assembly = $start.ArgumentList[0] }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $timedOut = !$process.WaitForExit(60000)
        if ($timedOut) { $process.Kill($true); if (!$process.WaitForExit(5000)) { throw 'Owned consumer did not terminate.' } }
        if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) { throw 'Consumer output drain timed out.' }
        $output = $stdout.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $evidence "$Label.stdout.log"), $output)
        [IO.File]::WriteAllText((Join-Path $evidence "$Label.stderr.log"), $stderr.GetAwaiter().GetResult())
        $passes = @($output -split '\r?\n' | Where-Object { $_.StartsWith('PASS|') })
        $failures = @($output -split '\r?\n' | Where-Object { $_.StartsWith('FAIL|') })
        $complete = @($output -split '\r?\n' | Where-Object { $_.StartsWith('COMPLETE|') })
        $result = @{ label = $Label; identity = $identity; exitCode = $process.ExitCode; timedOut = $timedOut; passes = $passes.Count; failureCount = $failures.Count; failures = $failures; completion = $complete }
        Write-Receipt (Join-Path $evidence "$Label.json") $result
        Write-Host "$Label exit=$($process.ExitCode) pass=$($passes.Count) fail=$($failures.Count)"
        if ($timedOut -or $complete.Count -ne 1) { throw 'Consumer baseline did not complete.' }
        $script:results.Add($result)
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}

$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
foreach ($path in @($evidence, $runtime)) {
    if (Test-Path -LiteralPath $path) { throw 'Use a fresh evidence and runtime directory.' }
    [IO.Directory]::CreateDirectory($path) | Out-Null
    [IO.File]::WriteAllText((Join-Path $path '.owner'), [Guid]::NewGuid().ToString('N'))
}
$server = Join-Path $evidence 'server'
$consumer = Join-Path $evidence 'consumer'
Copy-Snapshot $ServerDirectory $server 'server'
Copy-Snapshot $ConsumerDirectory $consumer 'consumer'
Write-Receipt (Join-Path $evidence 'baseline-input.json') @{ serverSourceLabel = $ServerSourceLabel; runtime = $runtime; purpose = 'Preparation baseline; failures remain failures.' }
$script:results = [Collections.Generic.List[object]]::new()

foreach ($protocol in @('rest', 'frame-http2')) {
    $httpPort = Get-AvailablePort
    do { $h2Port = Get-AvailablePort } while ($h2Port -eq $httpPort)
    $port = if ($protocol -eq 'rest') { $httpPort } else { $h2Port }
    $bearer = [Guid]::NewGuid().ToString('N')
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add((Join-Path $server 'SonnetDB.dll'))
    $start.WorkingDirectory = $server
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $environment = @{
        ASPNETCORE_ENVIRONMENT = 'Production';
        Kestrel__Endpoints__Http__Url = "http://127.0.0.1:$httpPort";
        Kestrel__Endpoints__Http__Protocols = 'Http1';
        Kestrel__Endpoints__FrameH2__Url = "http://127.0.0.1:$h2Port";
        Kestrel__Endpoints__FrameH2__Protocols = 'Http2';
        SonnetDBServer__DataRoot = (Join-Path $runtime "$protocol/databases");
        SonnetDBServer__Mqtt__Enabled = 'false';
        SonnetDBServer__AutoLoadExistingDatabases = 'true';
        SonnetDBServer__AllowAnonymousProbes = 'true';
        'Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics' = 'Information';
        TEMP = $runtime; TMP = $runtime;
    }
    $environment["SonnetDBServer__Tokens__$bearer"] = 'admin'
    foreach ($key in $environment.Keys) { $start.Environment[$key] = [string]$environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (!$process.Start()) { throw 'Server did not start.' }
    $identity = @{ pid = $process.Id; startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O'); assembly = $start.ArgumentList[0]; workingDirectory = $server }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
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
        foreach ($mode in @('native', 'sqlsugar')) {
            $database = "procedure_contract_$mode"
            $managementHandler = [Net.Http.HttpClientHandler]::new()
            $managementHandler.UseProxy = $false
            $management = [Net.Http.HttpClient]::new($managementHandler)
            $management.Timeout = [TimeSpan]::FromSeconds(15)
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "http://127.0.0.1:$port/v1/db")
            $request.Version = if ($protocol -eq 'rest') { [Version]::new(1, 1) } else { [Version]::new(2, 0) }
            $request.VersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $bearer)
            $request.Content = [Net.Http.StringContent]::new((@{ name = $database } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
            try {
                $created = $management.SendAsync($request).GetAwaiter().GetResult()
                $created.EnsureSuccessStatusCode() | Out-Null
                Write-Receipt (Join-Path $evidence "create-$protocol-$mode.json") @{ status = [int]$created.StatusCode; version = $created.Version.ToString(); database = $database }
                $created.Dispose()
                $prefix = if ($mode -eq 'native') { 'n' } else { 's' }
                $fixture = @(
                    "CREATE TABLE ${prefix}_items(id INT,value INT,PRIMARY KEY(id))",
                    "CREATE PROCEDURE ${prefix}_echo(IN p_left INT,IN p_right INT) LANGUAGE SQL AS BEGIN SELECT @p_left*10+@p_right AS result; END",
                    "CREATE PROCEDURE ${prefix}_write(IN p_left INT,IN p_right INT) LANGUAGE SQL AS BEGIN INSERT INTO ${prefix}_items(id,value) VALUES(@p_left,@p_left*10+@p_right); END"
                )
                if ($mode -eq 'native') { $fixture += "CREATE PROCEDURE n_two() LANGUAGE SQL AS BEGIN SELECT 11 AS first; SELECT 'second' AS second; END" }
                $fixtureIndex = 0
                foreach ($sql in $fixture) {
                    $fixtureRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "http://127.0.0.1:$port/v1/db/$database/sql")
                    $fixtureRequest.Version = $request.Version
                    $fixtureRequest.VersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
                    $fixtureRequest.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $bearer)
                    $fixtureRequest.Content = [Net.Http.StringContent]::new((@{ sql = $sql } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
                    try {
                        $fixtureResponse = $management.SendAsync($fixtureRequest).GetAwaiter().GetResult()
                        $body = $fixtureResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        Write-Receipt (Join-Path $evidence "fixture-$protocol-$mode-$fixtureIndex.json") @{ sql = $sql; status = [int]$fixtureResponse.StatusCode; version = $fixtureResponse.Version.ToString(); body = $body; scope = 'Public HTTP fixture only; remote ADO CREATE PROCEDURE is a separate failing case.' }
                        $fixtureResponse.EnsureSuccessStatusCode() | Out-Null
                        if ($body -match '"(?:type|kind)"\s*:\s*"error"') { throw 'Fixture SQL returned an error record.' }
                        $fixtureResponse.Dispose()
                    }
                    finally { $fixtureRequest.Dispose() }
                    $fixtureIndex++
                }
            }
            finally { $request.Dispose(); $management.Dispose() }
            Invoke-Consumer "$protocol-$mode" "Data Source=sonnetdb+http://127.0.0.1:$port/$database;Token=$bearer;Timeout=30;Protocol=$protocol" $mode
        }
    }
    finally {
        $client.Dispose()
        $terminated = !$process.HasExited
        if ($terminated) { $process.Kill($true); if (!$process.WaitForExit(5000)) { throw 'Owned server did not terminate.' } }
        if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) { throw 'Server output drain timed out.' }
        $output = $stdout.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $evidence "server-$protocol.stdout.log"), $output)
        [IO.File]::WriteAllText((Join-Path $evidence "server-$protocol.stderr.log"), $stderr.GetAwaiter().GetResult())
        Write-Receipt (Join-Path $evidence "server-$protocol.json") @{ identity = $identity; terminated = $terminated; exitCode = $process.ExitCode; exited = $process.HasExited; httpPort = $httpPort; h2Port = $h2Port }
        $process.Dispose()
    }
    $expected = if ($protocol -eq 'rest') { 'HTTP/1.1' } else { 'HTTP/2' }
    $requests = @($output -split '\r?\n' | Where-Object { $_ -match 'Request starting ' -and ($_ -match '/v1/db/procedure_contract_' -or $_ -match '/v1/frame') })
    if ($requests.Count -eq 0 -or @($requests | Where-Object { $_ -notmatch [regex]::Escape($expected) }).Count -ne 0) { throw 'Observed data protocol differs.' }
    if ($protocol -eq 'frame-http2' -and @($requests | Where-Object { $_ -match '/v1/frame' }).Count -eq 0) { throw 'No real Frame request was observed.' }
    Write-Receipt (Join-Path $evidence "protocol-$protocol.json") @{ expected = $expected; requestCount = $requests.Count; requests = $requests; note = 'DDL and CALL controls reach Server. StoredProcedure setter rejections are client side.' }
}
Write-Receipt (Join-Path $evidence 'summary.json') @{ harnessCompleted = $true; results = $script:results.ToArray(); functionalAcceptance = @($script:results | Where-Object { $_.exitCode -ne 0 }).Count -eq 0 }
if (@($script:results | Where-Object { $_.exitCode -ne 0 }).Count -gt 0) { exit 1 }
