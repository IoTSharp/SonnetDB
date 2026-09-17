$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }

. (Join-Path $PSScriptRoot 'm19-capacity-fixtures.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('m19-bundle-verifier-test-' + [guid]::NewGuid().ToString('N'))
$verifier = Join-Path $PSScriptRoot 'verify-m19-capacity-bundle.ps1'
$pwsh = (Get-Process -Id $PID).Path
if (-not (Test-Path -LiteralPath $pwsh -PathType Leaf)) { throw "Current PowerShell executable is unavailable: $pwsh" }

function Test-ReleaseEvidenceBooleanTrue([object] $Result) {
    return ($null -ne $Result -and
        $null -ne $Result.PSObject.Properties['releaseEvidence'] -and
        $Result.releaseEvidence -is [bool] -and
        $Result.releaseEvidence -eq $true)
}

function Assert-BundleResult([string] $BundleRoot, [string] $ExpectedStatus, [string] $Label) {
    $outputPath = Join-Path $BundleRoot 'test-verification.json'
    $output = @(& $pwsh -NoLogo -NoProfile -File $verifier -BundleRoot $BundleRoot -ArtifactUrl 'https://github.com/fixture/sonnetdb/actions/runs/42' -OutputPath $outputPath -ExpectedCommitSha ('a' * 40) -ExpectedTargetHardwareId 'fixture-target' -ExpectedTargetHardwareContract 'M19-#125-frozen-target-v1' -AllowNotReady 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "$Label verifier invocation failed: $($output -join "`n")" }
    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) { throw "$Label did not write verification output." }
    $result = Get-Content -LiteralPath $outputPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    if ([string]$result.status -ne $ExpectedStatus) {
        throw "$Label expected status '$ExpectedStatus', got '$($result.status)': $($result.issues -join ', ')"
    }
    $releaseEvidenceIsBooleanTrue = Test-ReleaseEvidenceBooleanTrue $result
    if ($ExpectedStatus -eq 'PASS' -and -not $releaseEvidenceIsBooleanTrue) { throw "$Label must contain Boolean releaseEvidence=true when PASS." }
    if ($ExpectedStatus -ne 'PASS' -and $releaseEvidenceIsBooleanTrue) { throw "$Label must not claim release evidence." }
    return $result
}

function New-CaseBundle([string] $Name) {
    $bundle = Join-Path $root $Name
    return New-M19FixtureBundle $bundle
}

New-Item -ItemType Directory -Path $root | Out-Null
try {
    $valid = New-CaseBundle 'valid'
    [void](Assert-BundleResult $valid.bundleRoot 'PASS' 'Valid bundle')

    foreach ($serialized in @('{"releaseEvidence":"true"}', '{"releaseEvidence":"false"}', '{"releaseEvidence":1}', '{}')) {
        $synthetic = $serialized | ConvertFrom-Json -Depth 8
        if (Test-ReleaseEvidenceBooleanTrue $synthetic) {
            throw "Non-Boolean releaseEvidence fixture '$serialized' must not satisfy the release-evidence contract."
        }
    }

    $identityMismatch = New-CaseBundle 'identity-mismatch'
    $identityReportPath = Join-Path $identityMismatch.bundleRoot 'small-segments/report.json'
    $identityReport = Get-Content -LiteralPath $identityReportPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $identityReport.environment.machineName = 'other-host'
    Write-M19FixtureJson $identityReportPath $identityReport
    Write-M19FixtureRawManifest $identityMismatch.bundleRoot $identityMismatch.commitSha $identityMismatch.targetHardwareId $identityMismatch.targetHardwareContract
    [void](Assert-BundleResult $identityMismatch.bundleRoot 'NOT_READY' 'Cross-profile identity mismatch')

    $missingMarkdown = New-CaseBundle 'missing-markdown'
    Remove-Item -LiteralPath (Join-Path $missingMarkdown.bundleRoot 'many-measurements/report.md') -Force
    [void](Assert-BundleResult $missingMarkdown.bundleRoot 'NOT_READY' 'Missing markdown')

    $reportTamper = New-CaseBundle 'report-tamper'
    $reportPath = Join-Path $reportTamper.bundleRoot 'high-cardinality/report.json'
    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $report.evidence.workloadFingerprint = ('0' * 64)
    Write-M19FixtureJson $reportPath $report
    [void](Assert-BundleResult $reportTamper.bundleRoot 'NOT_READY' 'Report tamper')

    $markdownTamper = New-CaseBundle 'markdown-tamper'
    Add-Content -LiteralPath (Join-Path $markdownTamper.bundleRoot 'high-cardinality/report.md') -Value 'tampered' -Encoding utf8NoBOM
    [void](Assert-BundleResult $markdownTamper.bundleRoot 'NOT_READY' 'Markdown manifest tamper')

    $hardwareTamper = New-CaseBundle 'hardware-tamper'
    $hardwarePath = Join-Path $hardwareTamper.bundleRoot 'target-hardware.json'
    $hardware = Get-Content -LiteralPath $hardwarePath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $hardware.storage.declaredModel = 'other-storage'
    Write-M19FixtureJson $hardwarePath $hardware
    Write-M19FixtureRawManifest $hardwareTamper.bundleRoot $hardwareTamper.commitSha $hardwareTamper.targetHardwareId $hardwareTamper.targetHardwareContract
    [void](Assert-BundleResult $hardwareTamper.bundleRoot 'NOT_READY' 'Hardware snapshot storage mismatch')

    $hardwareDeclarationTamper = New-CaseBundle 'hardware-declaration-tamper'
    $hardwareDeclarationPath = Join-Path $hardwareDeclarationTamper.bundleRoot 'target-hardware.json'
    $hardwareDeclaration = Get-Content -LiteralPath $hardwareDeclarationPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $hardwareDeclaration.targetHardware.declarationSource = 'manual-input'
    Write-M19FixtureJson $hardwareDeclarationPath $hardwareDeclaration
    Write-M19FixtureRawManifest $hardwareDeclarationTamper.bundleRoot $hardwareDeclarationTamper.commitSha $hardwareDeclarationTamper.targetHardwareId $hardwareDeclarationTamper.targetHardwareContract
    $hardwareDeclarationResult = Assert-BundleResult $hardwareDeclarationTamper.bundleRoot 'NOT_READY' 'Hardware declaration source tamper'
    if (@($hardwareDeclarationResult.issues) -notcontains 'target_hardware_snapshot:declaration_source_invalid') {
        throw 'Hardware declaration source tamper did not produce the expected declaration-source issue.'
    }

    $hardwareStatusTamper = New-CaseBundle 'hardware-status-tamper'
    $hardwareStatusPath = Join-Path $hardwareStatusTamper.bundleRoot 'target-hardware.json'
    $hardwareStatus = Get-Content -LiteralPath $hardwareStatusPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $hardwareStatus.source.worktreeStatus = @(' M tampered-file')
    Write-M19FixtureJson $hardwareStatusPath $hardwareStatus
    Write-M19FixtureRawManifest $hardwareStatusTamper.bundleRoot $hardwareStatusTamper.commitSha $hardwareStatusTamper.targetHardwareId $hardwareStatusTamper.targetHardwareContract
    $hardwareStatusResult = Assert-BundleResult $hardwareStatusTamper.bundleRoot 'NOT_READY' 'Hardware worktree status tamper'
    if (@($hardwareStatusResult.issues) -notcontains 'target_hardware_snapshot:source:worktree_status_not_empty') {
        throw 'Hardware worktree status tamper did not produce the expected consistency issue.'
    }

    $hardwareMemoryTamper = New-CaseBundle 'hardware-memory-tamper'
    $hardwareMemoryPath = Join-Path $hardwareMemoryTamper.bundleRoot 'target-hardware.json'
    $hardwareMemory = Get-Content -LiteralPath $hardwareMemoryPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $hardwareMemory.machine.physicalMemoryBytes = 0
    Write-M19FixtureJson $hardwareMemoryPath $hardwareMemory
    Write-M19FixtureRawManifest $hardwareMemoryTamper.bundleRoot $hardwareMemoryTamper.commitSha $hardwareMemoryTamper.targetHardwareId $hardwareMemoryTamper.targetHardwareContract
    $hardwareMemoryResult = Assert-BundleResult $hardwareMemoryTamper.bundleRoot 'NOT_READY' 'Hardware physical-memory availability tamper'
    if (@($hardwareMemoryResult.issues) -notcontains 'target_hardware_snapshot:machine:physicalMemoryBytes_invalid') {
        throw 'Hardware physical-memory tamper did not produce the expected availability issue.'
    }

    $hardwareProcessorTamper = New-CaseBundle 'hardware-processor-tamper'
    $hardwareProcessorPath = Join-Path $hardwareProcessorTamper.bundleRoot 'target-hardware.json'
    $hardwareProcessor = Get-Content -LiteralPath $hardwareProcessorPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $hardwareProcessor.machine.processorCount = 2
    Write-M19FixtureJson $hardwareProcessorPath $hardwareProcessor
    Write-M19FixtureRawManifest $hardwareProcessorTamper.bundleRoot $hardwareProcessorTamper.commitSha $hardwareProcessorTamper.targetHardwareId $hardwareProcessorTamper.targetHardwareContract
    $hardwareProcessorResult = Assert-BundleResult $hardwareProcessorTamper.bundleRoot 'NOT_READY' 'Hardware logical-processor mismatch tamper'
    if (@($hardwareProcessorResult.issues) -notcontains 'profile:high-cardinality:processor_count_mismatch') {
        throw 'Hardware logical-processor tamper did not produce the expected profile identity issue.'
    }

    $checkoutTamper = New-CaseBundle 'checkout-tamper'
    $checkoutPath = Join-Path $checkoutTamper.bundleRoot 'checkout-attestation.json'
    $checkout = Get-Content -LiteralPath $checkoutPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $checkout.ci.workflow = 'Unrelated Workflow'
    Write-M19FixtureJson $checkoutPath $checkout
    Write-M19FixtureRawManifest $checkoutTamper.bundleRoot $checkoutTamper.commitSha $checkoutTamper.targetHardwareId $checkoutTamper.targetHardwareContract
    [void](Assert-BundleResult $checkoutTamper.bundleRoot 'NOT_READY' 'Checkout workflow attestation tamper')

    $checkoutProtectionTamper = New-CaseBundle 'checkout-protection-tamper'
    $checkoutProtectionPath = Join-Path $checkoutProtectionTamper.bundleRoot 'checkout-attestation.json'
    $checkoutProtection = Get-Content -LiteralPath $checkoutProtectionPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $checkoutProtection.ci.refProtected = $false
    Write-M19FixtureJson $checkoutProtectionPath $checkoutProtection
    Write-M19FixtureRawManifest $checkoutProtectionTamper.bundleRoot $checkoutProtectionTamper.commitSha $checkoutProtectionTamper.targetHardwareId $checkoutProtectionTamper.targetHardwareContract
    $checkoutProtectionResult = Assert-BundleResult $checkoutProtectionTamper.bundleRoot 'NOT_READY' 'Checkout unprotected ref attestation tamper'
    if (@($checkoutProtectionResult.issues) -notcontains 'checkout_attestation:ci_ref_not_protected') {
        throw 'Checkout unprotected-ref tamper did not produce the expected protection issue.'
    }

    $checkoutEventTamper = New-CaseBundle 'checkout-event-tamper'
    $checkoutEventPath = Join-Path $checkoutEventTamper.bundleRoot 'checkout-attestation.json'
    $checkoutEvent = Get-Content -LiteralPath $checkoutEventPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $checkoutEvent.ci.eventName = 'push'
    Write-M19FixtureJson $checkoutEventPath $checkoutEvent
    Write-M19FixtureRawManifest $checkoutEventTamper.bundleRoot $checkoutEventTamper.commitSha $checkoutEventTamper.targetHardwareId $checkoutEventTamper.targetHardwareContract
    $checkoutEventResult = Assert-BundleResult $checkoutEventTamper.bundleRoot 'NOT_READY' 'Checkout event attestation tamper'
    if (@($checkoutEventResult.issues) -notcontains 'checkout_attestation:ci_event_invalid') {
        throw 'Checkout event tamper did not produce the expected event issue.'
    }

    $checkoutStorageTamper = New-CaseBundle 'checkout-storage-tamper'
    $checkoutStoragePath = Join-Path $checkoutStorageTamper.bundleRoot 'checkout-attestation.json'
    $checkoutStorage = Get-Content -LiteralPath $checkoutStoragePath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $checkoutStorage.targetHardware.storageModel = 'other-storage'
    Write-M19FixtureJson $checkoutStoragePath $checkoutStorage
    Write-M19FixtureRawManifest $checkoutStorageTamper.bundleRoot $checkoutStorageTamper.commitSha $checkoutStorageTamper.targetHardwareId $checkoutStorageTamper.targetHardwareContract
    [void](Assert-BundleResult $checkoutStorageTamper.bundleRoot 'NOT_READY' 'Checkout storage declaration mismatch')

    $checkoutStatusTamper = New-CaseBundle 'checkout-status-tamper'
    $checkoutStatusPath = Join-Path $checkoutStatusTamper.bundleRoot 'checkout-attestation.json'
    $checkoutStatus = Get-Content -LiteralPath $checkoutStatusPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $checkoutStatus.source.worktreeStatus = @(' M tampered-file')
    Write-M19FixtureJson $checkoutStatusPath $checkoutStatus
    Write-M19FixtureRawManifest $checkoutStatusTamper.bundleRoot $checkoutStatusTamper.commitSha $checkoutStatusTamper.targetHardwareId $checkoutStatusTamper.targetHardwareContract
    $checkoutStatusResult = Assert-BundleResult $checkoutStatusTamper.bundleRoot 'NOT_READY' 'Checkout worktree status tamper'
    if (@($checkoutStatusResult.issues) -notcontains 'checkout_attestation:source:worktree_status_not_empty') {
        throw 'Checkout worktree status tamper did not produce the expected consistency issue.'
    }

    $terminalWorktreeTamper = New-CaseBundle 'terminal-worktree-tamper'
    $terminalManifestPath = Join-Path $terminalWorktreeTamper.bundleRoot 'raw-artifact-manifest.json'
    $terminalManifest = Get-Content -LiteralPath $terminalManifestPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $terminalManifest.source.worktreeStatus = @(' M src/SonnetDB.Core/Engine/Tsdb.cs')
    Write-M19FixtureJson $terminalManifestPath $terminalManifest
    [void](Assert-BundleResult $terminalWorktreeTamper.bundleRoot 'NOT_READY' 'Terminal worktree mutation')

    $manifestTamper = New-CaseBundle 'manifest-tamper'
    $manifestPath = Join-Path $manifestTamper.bundleRoot 'raw-artifact-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $manifest.files[0].sha256 = ('0' * 64)
    Write-M19FixtureJson $manifestPath $manifest
    [void](Assert-BundleResult $manifestTamper.bundleRoot 'NOT_READY' 'Manifest hash tamper')

    $mountTamper = New-CaseBundle 'mount-tamper'
    $mountPath = Join-Path $mountTamper.bundleRoot 'target-hardware.json'
    $mount = Get-Content -LiteralPath $mountPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $mount.storage.workloadMount.source = '/dev/other'
    Write-M19FixtureJson $mountPath $mount
    Write-M19FixtureRawManifest $mountTamper.bundleRoot $mountTamper.commitSha $mountTamper.targetHardwareId $mountTamper.targetHardwareContract
    [void](Assert-BundleResult $mountTamper.bundleRoot 'NOT_READY' 'Target workload mount identity mismatch')

    $reportMountTamper = New-CaseBundle 'report-mount-tamper'
    $reportMountPath = Join-Path $reportMountTamper.bundleRoot 'high-cardinality/report.json'
    $reportMount = Get-Content -LiteralPath $reportMountPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $reportMount.environment.disk.mountDeviceId = '8:2'
    Write-M19FixtureJson $reportMountPath $reportMount
    Write-M19FixtureRawManifest $reportMountTamper.bundleRoot $reportMountTamper.commitSha $reportMountTamper.targetHardwareId $reportMountTamper.targetHardwareContract
    [void](Assert-BundleResult $reportMountTamper.bundleRoot 'NOT_READY' 'Report workload mount identity mismatch')

    $forbiddenFileSystem = New-CaseBundle 'forbidden-filesystem'
    $forbiddenReportPath = Join-Path $forbiddenFileSystem.bundleRoot 'small-segments/report.json'
    $forbiddenReport = Get-Content -LiteralPath $forbiddenReportPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $forbiddenReport.environment.disk.fileSystem = 'overlay'
    Write-M19FixtureJson $forbiddenReportPath $forbiddenReport
    Write-M19FixtureRawManifest $forbiddenFileSystem.bundleRoot $forbiddenFileSystem.commitSha $forbiddenFileSystem.targetHardwareId $forbiddenFileSystem.targetHardwareContract
    [void](Assert-BundleResult $forbiddenFileSystem.bundleRoot 'NOT_READY' 'Forbidden report filesystem')

    $architectureTamper = New-CaseBundle 'architecture-tamper'
    $architecturePath = Join-Path $architectureTamper.bundleRoot 'target-hardware.json'
    $architecture = Get-Content -LiteralPath $architecturePath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
    $architecture.machine.processArchitecture = 'Arm64'
    Write-M19FixtureJson $architecturePath $architecture
    Write-M19FixtureRawManifest $architectureTamper.bundleRoot $architectureTamper.commitSha $architectureTamper.targetHardwareId $architectureTamper.targetHardwareContract
    [void](Assert-BundleResult $architectureTamper.bundleRoot 'NOT_READY' 'Target architecture contract')

    Write-Output 'M19 bundle verifier contract tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
