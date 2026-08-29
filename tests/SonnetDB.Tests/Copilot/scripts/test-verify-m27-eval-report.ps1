$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$verifier = Join-Path $PSScriptRoot 'verify-m27-eval-report.ps1'
$fixture = Join-Path $PSScriptRoot '..\..\..\..\docs\benchmarks\m27-copilot-eval-report.example.json'
$valid = & $PSHOME\pwsh.exe -NoLogo -NoProfile -File $verifier -ReportPath $fixture 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $valid -notmatch 'VALID_NOT_READY') { throw "NOT_READY fixture should be schema-valid: $valid" }

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('m27-eval-report-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $invalidPath = Join-Path $root 'invalid.json'
    $json = Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json
    $json.scenarios[0].usage.reported = $true
    $json.scenarios[0].usage.inputTokens = $null
    $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $invalidPath
    $invalid = & $PSHOME\pwsh.exe -NoLogo -NoProfile -File $verifier -ReportPath $invalidPath 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $invalid -notmatch 'reported usage') { throw "invalid usage was accepted: $invalid" }

    $ready = & $PSHOME\pwsh.exe -NoLogo -NoProfile -File $verifier -ReportPath $fixture -RequireReady 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $ready -notmatch 'NOT_READY') { throw 'RequireReady must reject the fixture.' }
    Write-Output 'M27 eval report verifier tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
