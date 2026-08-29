$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixture = Join-Path $env:TEMP ('sonnetdb-document-soak-fixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
try {
    $report = Join-Path $fixture 'report.json'
    [ordered]@{
        schemaVersion = 2; profile = 'quick'; documentCount = 10000; succeeded = $true
        environment = @{ commitSha = ('a' * 40); dataVolume = @{ totalBytes = 1; availableBytes = 1 } }
        targetHardware = @{ status = 'NOT_READY'; targetId = $null; contract = 'M25-#174-fixed-target-v1' }
        phases = @(); memorySamples = @()
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/verify-document-soak-evidence.ps1') -Report $report -Output (Join-Path $fixture 'verification.json') -AllowNotReady | ConvertFrom-Json
    if ($result.status -ne 'NOT_READY') { throw 'quick/unattested fixture unexpectedly passed' }
    if ($result.issues -notcontains 'profile_not_release_scale') { throw 'missing profile gap' }
    if ($result.issues -notcontains 'fixed_target_hardware_not_attested') { throw 'missing hardware gap' }

    [ordered]@{
        schemaVersion = 2; profile = 'million'; documentCount = 1000000; succeeded = $true
        environment = @{ commitSha = ('b' * 40); dataVolume = @{ deviceModel = 'NVMe'; totalBytes = 100; availableBytes = 50 } }
        targetHardware = @{ status = 'PASS'; targetId = 'inventory-1'; contract = 'M25-#174-fixed-target-v1' }
        phases = @(); memorySamples = @(@{ phase = 'write'; workingSetBytes = 1 })
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/verify-document-soak-evidence.ps1') -Report $report -Output (Join-Path $fixture 'verification.json') -AllowNotReady | ConvertFrom-Json
    if ($result.status -ne 'NOT_READY' -or $result.issues -notcontains 'phase_missing:write') { throw 'incomplete million fixture unexpectedly passed' }
    Write-Host 'Document soak evidence verifier contract: PASS'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
