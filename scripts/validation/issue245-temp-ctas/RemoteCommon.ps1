#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Receipt([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}

function Invoke-TempCtasProbe([string]$Label, [string[]]$Arguments, [hashtable]$Environment) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = [string]$Environment[$key] }
    $start.Environment['HTTP_PROXY'] = ''
    $start.Environment['HTTPS_PROXY'] = ''
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (!$process.Start()) { throw "Cannot start $Label" }
    $identity = @{ pid = $process.Id; startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O'); assembly = $Arguments[0] }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $timedOut = !$process.WaitForExit(180000)
        if ($timedOut) { $process.Kill($true); if (!$process.WaitForExit(5000)) { throw 'Owned probe failed to terminate.' } }
        $output = $stdout.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $script:Evidence "$Label.stdout.log"), $output)
        [IO.File]::WriteAllText((Join-Path $script:Evidence "$Label.stderr.log"), $stderr.GetAwaiter().GetResult())
        $failures = @($output -split '\r?\n' | Where-Object { $_.StartsWith('FAIL|') })
        $passes = @($output -split '\r?\n' | Where-Object { $_.StartsWith('PASS|') })
        Write-Receipt (Join-Path $script:Evidence "$Label.json") @{ identity = $identity; exitCode = $process.ExitCode; timedOut = $timedOut; passes = $passes.Count; failures = $failures }
        Write-Host "$Label exit=$($process.ExitCode) pass=$($passes.Count) fail=$($failures.Count)"
        if ($timedOut) { throw 'Probe timeout.' }
        if ($process.ExitCode -ne 0 -or $failures.Count -ne 0) { $script:ProbeFailures++ }
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}
