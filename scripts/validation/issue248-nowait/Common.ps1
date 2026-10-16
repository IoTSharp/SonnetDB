#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Receipt([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Invoke-Recorded([string]$Label, [string[]]$Arguments, [int]$TimeoutSeconds = 180,
    [string]$WorkingDirectory = $script:SourceRoot, [hashtable]$Environment = @{}, [string]$Executable = 'dotnet') {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = [string]$Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = [DateTimeOffset]::UtcNow
    if (!$process.Start()) { throw "Cannot start $Label" }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $identity = @{ pid = $process.Id; startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O'); executable = $Executable; arguments = $Arguments; workingDirectory = $WorkingDirectory }
    $timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
    $out = $stdout.GetAwaiter().GetResult()
    $err = $stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $script:Evidence "$Label.stdout.log"), $out)
    [IO.File]::WriteAllText((Join-Path $script:Evidence "$Label.stderr.log"), $err)
    $result = @{ label = $Label; identity = $identity; startedUtc = $started.ToString('O'); endedUtc = [DateTimeOffset]::UtcNow.ToString('O'); exitCode = $process.ExitCode; timedOut = $timedOut }
    Write-Receipt (Join-Path $script:Evidence "$Label.json") $result
    $process.Dispose()
    Write-Host "$Label exit=$($result.exitCode) timeout=$timedOut"
    if ($timedOut -or $result.exitCode -ne 0) { throw "$Label failed; inspect the recorded stdout/stderr." }
}

function Save-Manifest([string]$SourceDirectory) {
    $files = @()
    foreach ($root in @($SourceDirectory, $script:Evidence)) {
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
            if ($relative -match '(^|[\\/])(bin|obj|packages|http-cache|server|xcode|data|temp|cli-home)([\\/]|$)' -or $file.Name -eq 'manifest.json') { continue }
            $files += @{ root = $root; path = $relative; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
        }
    }
    Write-Receipt (Join-Path $script:Evidence 'manifest.json') @{ generatedUtc = [DateTimeOffset]::UtcNow.ToString('O'); files = $files }
}
