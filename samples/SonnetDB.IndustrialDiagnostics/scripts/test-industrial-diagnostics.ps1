$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('m27-industrial-sample-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    & dotnet run -c Release --no-restore --project (Join-Path $PSScriptRoot '..\SonnetDB.IndustrialDiagnostics.csproj') -- `
        --token smoke --server http://127.0.0.1:1 --output $root
    if ($LASTEXITCODE -ne 3) { throw "unreachable server should return exit code 3, got $LASTEXITCODE" }

    $report = Get-Content -LiteralPath (Join-Path $root 'industrial-diagnostics-report.json') -Raw | ConvertFrom-Json
    if ([string]$report.status -ne 'NOT_READY' -or [string]$report.dataStatus -ne 'NOT_READY') {
        throw 'unreachable server must produce NOT_READY data evidence.'
    }
    if ([string]$report.copilot.status -ne 'NOT_READY' -or [bool]$report.copilot.usage.reported) {
        throw 'unavailable provider must remain NOT_READY with unreported usage.'
    }
    if ($null -ne $report.copilot.usage.totalTokens -or $null -ne $report.copilot.usage.costUsd) {
        throw 'unreported model usage must remain null.'
    }
    if (@($report.anomalies).Count -ne 1 -or [string]$report.anomalies[0].device -ne 'pump-03') {
        throw 'deterministic anomaly journey is missing pump-03.'
    }
    if (@($report.queryMatchedDevices).Count -ne 0) {
        throw 'an unreachable server must not claim query-matched devices.'
    }
    if (@($report.citations).Count -lt 3) { throw 'diagnostic report must include at least three citations.' }
    Write-Output 'Industrial diagnostics sample smoke passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
