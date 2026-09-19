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

    [ordered]@{
        schemaVersion = 2; profile = 'million'; documentCount = 1000000; succeeded = $true
        environment = @{ commitSha = ('c' * 40); processorCount = 1; totalAvailableMemoryBytes = 1; dataVolume = @{ deviceModel = 'NVMe'; totalBytes = 100; availableBytes = 50 } }
        targetHardware = @{ status = 'PASS'; targetId = 'inventory-1'; contract = 'unapproved-contract' }
        phases = @(); memorySamples = @(@{ phase = 'write'; workingSetBytes = 1 }, @{ phase = 'completed'; workingSetBytes = 1 })
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/verify-document-soak-evidence.ps1') -Report $report -Output (Join-Path $fixture 'verification.json') -AllowNotReady | ConvertFrom-Json
    if ($result.status -ne 'NOT_READY' -or $result.issues -notcontains 'target_hardware_contract_invalid') { throw 'unapproved target hardware contract unexpectedly passed' }

    $phaseNames = @('write', 'index_create', 'indexed_query', 'index_rebuild', 'ttl_index_create', 'ttl_cleanup', 'backup', 'hot_reopen', 'cold_process_start', 'crash_recovery', 'backup_restore')
    $complete = [ordered]@{
        schemaVersion = 2; profile = 'million'; documentCount = 1000000; batchSize = 1000
        startedAtUtc = '2026-09-20T00:00:00.0000000+00:00'; completedAtUtc = '2026-09-20T00:01:00.0000000+00:00'; succeeded = $true
        environment = @{
            commitSha = ('d' * 40); processorCount = 1; totalAvailableMemoryBytes = 1
            dataVolume = @{ deviceModel = 'NVMe'; totalBytes = 100; availableBytes = 50 }
        }
        targetHardware = @{ status = 'PASS'; targetId = 'inventory-1'; contract = 'M25-#174-fixed-target-v1' }
        phases = @($phaseNames | ForEach-Object { @{ name = $_; durationMilliseconds = 1; operations = 1; operationsPerSecond = 1; details = @{} } })
        memorySamples = @(
            @{ timestampUtc = '2026-09-20T00:00:01.0000000+00:00'; phase = 'write'; documents = 1; workingSetBytes = 1; privateBytes = 1; managedBytes = 1 },
            @{ timestampUtc = '2026-09-20T00:00:30.0000000+00:00'; phase = 'hot_end'; documents = 1000000; workingSetBytes = 1; privateBytes = 1; managedBytes = 1 },
            @{ timestampUtc = '2026-09-20T00:01:00.0000000+00:00'; phase = 'completed'; documents = 1000000; workingSetBytes = 1; privateBytes = 1; managedBytes = 1 }
        )
    }
    $completeJson = $complete | ConvertTo-Json -Depth 12
    $completeJson | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/verify-document-soak-evidence.ps1') -Report $report -Output (Join-Path $fixture 'verification.json') -ExpectedCommitSha ('d' * 40) -ExpectedTargetHardwareId 'inventory-1' -AllowNotReady | ConvertFrom-Json
    if ($result.status -ne 'NOT_READY' -or $result.reportStatus -ne 'PASS' -or $result.releaseDecision -ne 'DEFERRED' -or [bool]$result.releaseEvidence -or $result.issues -notcontains 'external_attestation_missing') {
        throw 'complete million self-declared fixture unexpectedly became release evidence'
    }

    $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/verify-document-soak-evidence.ps1') -Report $report -Output (Join-Path $fixture 'verification.json') -ExpectedCommitSha ('e' * 40) -ExpectedTargetHardwareId 'inventory-2' -AllowNotReady | ConvertFrom-Json
    if ($result.issues -notcontains 'expected_commit_sha_mismatch' -or $result.issues -notcontains 'expected_target_hardware_id_mismatch' -or $result.releaseDecision -ne 'NOT_READY') {
        throw 'expected report identity binding did not fail closed'
    }

    $missingTtl = $completeJson | ConvertFrom-Json -Depth 12
    $missingTtl.phases = @($missingTtl.phases | Where-Object { $_.name -ne 'ttl_index_create' })
    $missingTtl | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/verify-document-soak-evidence.ps1') -Report $report -Output (Join-Path $fixture 'verification.json') -AllowNotReady | ConvertFrom-Json
    if ($result.status -ne 'NOT_READY' -or $result.issues -notcontains 'phase_missing:ttl_index_create') { throw 'missing TTL index phase unexpectedly passed' }

    $falseString = $completeJson | ConvertFrom-Json -Depth 12
    $falseString.succeeded = 'false'
    $falseString | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/verify-document-soak-evidence.ps1') -Report $report -Output (Join-Path $fixture 'verification.json') -AllowNotReady | ConvertFrom-Json
    if ($result.status -ne 'NOT_READY' -or $result.issues -notcontains 'soak_failed') { throw 'string succeeded=false unexpectedly passed' }

    $nestedMissing = [ordered]@{
        schemaVersion = 2; profile = 'million'; documentCount = 1000000; batchSize = 1000
        startedAtUtc = '2026-09-20T00:00:00.0000000+00:00'; completedAtUtc = '2026-09-20T00:01:00.0000000+00:00'; succeeded = $true
        environment = @{}; targetHardware = @{}; phases = @(); memorySamples = @()
    }
    $nestedMissing | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/verify-document-soak-evidence.ps1') -Report $report -Output (Join-Path $fixture 'verification.json') -AllowNotReady | ConvertFrom-Json
    if ($result.status -ne 'NOT_READY' -or $result.issues -notcontains 'data_volume_missing') { throw 'missing nested evidence did not remain NOT_READY' }

    Write-Host 'Document soak evidence verifier contract: PASS'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
