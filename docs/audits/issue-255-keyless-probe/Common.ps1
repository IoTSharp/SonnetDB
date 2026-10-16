param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [switch]$UseLocalProxy,
    [string]$ServerSnapshot,
    [string]$ServerManifest,
    [switch]$FunctionsOnly,
    [int]$RestPort = 43874,
    [int]$FramePort = 43875
)
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$runRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $runRoot) { throw 'OutputDirectory must be new; evidence is never overwritten.' }
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeDirectory)
if (Test-Path -LiteralPath $runtimeRoot) { throw 'RuntimeDirectory must be new and exclusively owned.' }
[IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $runtimeRoot 'temp')) | Out-Null
[IO.File]::WriteAllText((Join-Path $runtimeRoot '.owner'), ('issue255|' + $runRoot), $utf8)
foreach ($name in @('temp', 'consumer', 'dotnet-home')) { [IO.Directory]::CreateDirectory((Join-Path $runRoot $name)) | Out-Null }
$commands = [Collections.Generic.List[object]]::new()
$packages = [Collections.Generic.List[object]]::new()
$hosts = [Collections.Generic.List[object]]::new()
$failure = $null
$activeHost = $null
$hostManifest = $null

function Start-Owned([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($pair in @{
        NUGET_PACKAGES = 'packages'; NUGET_HTTP_CACHE_PATH = 'http-cache'; NUGET_PLUGINS_CACHE_PATH = 'plugins-cache';
        DOTNET_CLI_HOME = 'dotnet-home'
    }.GetEnumerator()) { $info.Environment[$pair.Key] = Join-Path $runRoot $pair.Value }
    $info.Environment['TEMP'] = Join-Path $runtimeRoot 'temp'
    $info.Environment['TMP'] = Join-Path $runtimeRoot 'temp'
    $info.Environment['NUGET_FALLBACK_PACKAGES'] = ''
    $info.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    $info.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $info.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $info.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    if ($UseLocalProxy) {
        $info.Environment['HTTP_PROXY'] = 'http://127.0.0.1:7890'
        $info.Environment['HTTPS_PROXY'] = 'http://127.0.0.1:7890'
        $info.Environment['NO_PROXY'] = 'localhost,127.0.0.1,::1'
    }
    $started = [DateTime]::UtcNow.ToString('o')
    $process = [Diagnostics.Process]::Start($info)
    return @{ process = $process; stdout = $process.StandardOutput.ReadToEndAsync(); stderr = $process.StandardError.ReadToEndAsync(); startedUtc = $started; executable = $Executable; arguments = $Arguments; workingDirectory = $WorkingDirectory; pid = $process.Id }
}

function Save-Owned($Owned, [string]$Label, [bool]$TimedOut = $false, [bool]$KilledForReopen = $false) {
    [IO.File]::WriteAllText((Join-Path $runRoot ($Label + '.stdout.log')), $Owned.stdout.GetAwaiter().GetResult(), $utf8)
    [IO.File]::WriteAllText((Join-Path $runRoot ($Label + '.stderr.log')), $Owned.stderr.GetAwaiter().GetResult(), $utf8)
    $record = [ordered]@{ label = $Label; executable = $Owned.executable; arguments = $Owned.arguments; workingDirectory = $Owned.workingDirectory; pid = $Owned.pid; startedUtc = $Owned.startedUtc; endedUtc = [DateTime]::UtcNow.ToString('o'); exitCode = $Owned.process.ExitCode; timedOut = $TimedOut; killedForReopen = $KilledForReopen }
    $Owned.process.Dispose()
    return $record
}

function Invoke-Captured([string]$Label, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 180) {
    $owned = Start-Owned $Executable $Arguments $WorkingDirectory
    $timedOut = !$owned.process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) { $owned.process.Kill($true); $owned.process.WaitForExit() }
    $record = Save-Owned $owned $Label $timedOut
    $commands.Add($record)
    Write-Host ($Label + ': exit=' + $record.exitCode)
    if ($record.exitCode -ne 0 -or $timedOut) { throw ('Command failed: ' + $Label) }
}

function Start-HostSnapshot([int]$Round) {
    $owned = Start-Owned 'dotnet' @((Join-Path $ServerSnapshot 'SonnetDB.dll'), '--contentRoot', (Join-Path $runRoot 'host-content')) $runRoot
    $owned.label = 'server-' + $Round
    $waitUntil = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $waitUntil) {
        if ($owned.process.HasExited) { $hosts.Add((Save-Owned $owned $owned.label)); throw 'Server exited before readiness.' }
        try {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "http://127.0.0.1:$RestPort/v1/db")
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'issue255-local-acceptance')
            $response = $http.Send($request)
            $ready = $response.IsSuccessStatusCode
            $response.Dispose(); $request.Dispose()
            if ($ready) { return $owned }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    $owned.process.Kill($true); $owned.process.WaitForExit()
    $hosts.Add((Save-Owned $owned $owned.label $true))
    throw 'Server readiness timeout.'
}

function Stop-HostSnapshot($Owned) {
    $killed = !$Owned.process.HasExited
    if ($killed) { $Owned.process.Kill($true); $Owned.process.WaitForExit() }
    $hosts.Add((Save-Owned $Owned $Owned.label $false $killed))
}
