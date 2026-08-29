$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('m19-report-test-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $root 'report'
New-Item -ItemType Directory -Path $root | Out-Null
try {
    dotnet run -c Release --project (Join-Path $PSScriptRoot '..') -- --profile high-cardinality --series 2 --recovery-samples 1 --query-samples 1 --output $output
    $reportPath = Join-Path $output 'report.json'

    $result = & $PSHOME\pwsh.exe -NoLogo -NoProfile -File (Join-Path $PSScriptRoot 'verify-m19-capacity-report.ps1') -ReportPath $reportPath 2>&1
    if ($LASTEXITCODE -eq 0 -or (($result -join "`n") -notmatch 'NOT_READY')) {
        throw 'Scaled profile without target hardware declaration must remain NOT_READY.'
    }

    $missingTargetPath = Join-Path $root 'missing-target.json'
    $json = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $json.PSObject.Properties.Remove('TargetHardware')
    $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $missingTargetPath
    $missingResult = & $PSHOME\pwsh.exe -NoLogo -NoProfile -File (Join-Path $PSScriptRoot 'verify-m19-capacity-report.ps1') -ReportPath $missingTargetPath 2>&1
    if ($LASTEXITCODE -eq 0 -or (($missingResult -join "`n") -notmatch 'missing targetHardware')) {
        throw 'Missing target hardware declaration must fail verification.'
    }

    Write-Output 'M19 report verifier tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
