[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BundleRoot,
    [Parameter(Mandatory = $true)]
    [string] $ArtifactUrl,
    [string] $OutputPath,
    [string] $ExpectedCommitSha,
    [string] $ExpectedTargetHardwareId,
    [string] $ExpectedTargetHardwareContract,
    [switch] $AllowNotReady
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required.'
}

$profiles = @('high-cardinality', 'small-segments', 'maintenance-chaos', 'many-measurements')
$forbiddenFileSystems = @(
    'overlay', 'aufs', 'tmpfs', 'ramfs', 'nfs', 'nfs4', 'cifs', 'smb',
    'sshfs', 'fuse.sshfs', 'ceph', 'cephfs', 'glusterfs', 'fuse.glusterfs')
$issues = [System.Collections.Generic.List[string]]::new()
$files = [System.Collections.Generic.List[object]]::new()
$profileReports = @{}
$checkoutCi = $null
$hardwareSnapshot = $null

try {
    $root = (Resolve-Path -LiteralPath $BundleRoot -ErrorAction Stop).Path
}
catch {
    throw "M19 capacity bundle root cannot be resolved: $($_.Exception.Message)"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'm19-capacity-bundle-verification.json'
}

function Add-Issue([string] $Issue) {
    if (-not $issues.Contains($Issue)) {
        $issues.Add($Issue)
    }
}

function Read-Json([string] $Path, [string] $Label) {
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
    }
    catch {
        Add-Issue "$Label`:invalid_json"
        return $null
    }
}

function Get-RelativeBundlePath([string] $Path) {
    return [System.IO.Path]::GetRelativePath($root, $Path).Replace('\', '/')
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Add-File([string] $Kind, [string] $Path) {
    $files.Add([ordered]@{
        kind = $Kind
        path = Get-RelativeBundlePath $Path
        sha256 = Get-Sha256 $Path
        bytes = (Get-Item -LiteralPath $Path).Length
    })
}

function Read-RequiredString([object] $Object, [string] $Name, [string] $Prefix) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        Add-Issue "$Prefix`:$Name`_missing"
        return $null
    }
    $value = ([string]$Object.$Name).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        Add-Issue "$Prefix`:$Name`_missing"
        return $null
    }
    return $value
}

function Read-RequiredBoolean([object] $Object, [string] $Name, [string] $Prefix) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name] -or $Object.$Name -isnot [bool]) {
        Add-Issue "$Prefix`:$Name`_invalid"
        return $null
    }
    return [bool]$Object.$Name
}

function Read-RequiredInteger([object] $Object, [string] $Name, [string] $Prefix, [long] $Minimum) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        Add-Issue "$Prefix`:$Name`_missing"
        return $null
    }

    [long] $value = 0
    if (-not [long]::TryParse([string]$Object.$Name, [ref]$value) -or $value -lt $Minimum) {
        Add-Issue "$Prefix`:$Name`_invalid"
        return $null
    }

    return $value
}

function Read-RequiredArray([object] $Object, [string] $Name, [string] $Prefix) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name] -or $null -eq $Object.$Name) {
        Add-Issue "$Prefix`:$Name`_missing"
        return @()
    }

    return @($Object.$Name)
}

function Read-RequiredArrayAllowingEmpty([object] $Object, [string] $Name, [string] $Prefix) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        Add-Issue "$Prefix`:$Name`_missing"
        return @()
    }

    # ConvertFrom-Json materializes an empty JSON array as $null in some
    # PowerShell pipeline contexts. Property presence still establishes that
    # the producing script explicitly recorded an empty status list.
    if ($null -eq $Object.$Name) {
        return @()
    }

    return @($Object.$Name)
}

function Test-WorktreeAttestation([object] $Source, [string] $Prefix) {
    $clean = Read-RequiredBoolean $Source 'worktreeClean' $Prefix
    $status = @(Read-RequiredArrayAllowingEmpty $Source 'worktreeStatus' $Prefix)
    foreach ($entry in $status) {
        if ($null -eq $entry -or [string]::IsNullOrWhiteSpace([string]$entry)) {
            Add-Issue "$Prefix`:worktree_status_invalid"
            break
        }
    }
    if ($clean -eq $true -and $status.Count -ne 0) {
        Add-Issue "$Prefix`:worktree_status_not_empty"
    }
    elseif ($clean -eq $false -and $status.Count -eq 0) {
        Add-Issue "$Prefix`:worktree_status_missing"
    }
}

function Test-UsableText([string] $Value) {
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -notin @('UNAVAILABLE', 'UNDECLARED', 'NOT_APPLICABLE', 'NOT_DETECTED', 'unavailable', 'unconfigured')
}

function Test-IdentityValue([string] $Expected, [string] $Actual, [string] $Issue) {
    if ([string]::IsNullOrWhiteSpace($Expected) -or [string]::IsNullOrWhiteSpace($Actual) -or -not [string]::Equals($Expected, $Actual, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Issue $Issue
    }
}

function Test-ExactIdentityValue([string] $Expected, [string] $Actual, [string] $Issue) {
    if ([string]::IsNullOrWhiteSpace($Expected) -or [string]::IsNullOrWhiteSpace($Actual) -or -not [string]::Equals($Expected, $Actual, [StringComparison]::Ordinal)) {
        Add-Issue $Issue
    }
}

function Test-ExactValue([string] $Expected, [string] $Actual, [string] $Issue) {
    if ([string]::IsNullOrWhiteSpace($Expected) -or [string]::IsNullOrWhiteSpace($Actual) -or
        -not [string]::Equals($Expected, $Actual, [StringComparison]::Ordinal)) {
        Add-Issue $Issue
    }
}

function Read-RequiredMountField([object] $Object, [string] $Name, [string] $Prefix) {
    $value = Read-RequiredString $Object $Name $Prefix
    if ($null -eq $value -or $value -in @('UNAVAILABLE', 'UNDECLARED', 'NOT_APPLICABLE', 'unavailable', 'unconfigured')) {
        Add-Issue "$Prefix`:$Name`_unusable"
        return $null
    }
    return $value
}

function Get-OptionalProperty([object] $Object, [string] $Name) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return $null
    }
    return $Object.$Name
}

function Test-MountFileSystem([string] $FileSystem, [string] $Prefix) {
    if ($null -ne $FileSystem -and $forbiddenFileSystems -contains $FileSystem.ToLowerInvariant()) {
        Add-Issue "$Prefix`:forbidden_filesystem"
    }
}

function Test-GitHubActionsAttestation([object] $Checkout, [object] $Identity) {
    if ($null -eq $Checkout) {
        return
    }

    $ci = $Checkout.ci
    if ($null -eq $ci) {
        Add-Issue 'checkout_attestation:ci_missing'
        return
    }
    if ([string](Read-RequiredString $ci 'provider' 'checkout_attestation:ci') -ne 'github-actions') {
        Add-Issue 'checkout_attestation:ci_provider_invalid'
    }
    $repository = Read-RequiredString $ci 'repository' 'checkout_attestation:ci'
    if ($null -ne $repository -and $repository -notmatch '^[^/\s]+/[^/\s]+$') {
        Add-Issue 'checkout_attestation:ci_repository_invalid'
    }
    $ref = Read-RequiredString $ci 'ref' 'checkout_attestation:ci'
    if ($ref -ne 'refs/heads/main') { Add-Issue 'checkout_attestation:ci_ref_not_main' }
    $refProtected = Read-RequiredBoolean $ci 'refProtected' 'checkout_attestation:ci'
    if ($refProtected -ne $true) { Add-Issue 'checkout_attestation:ci_ref_not_protected' }
    $eventName = Read-RequiredString $ci 'eventName' 'checkout_attestation:ci'
    if ($eventName -ne 'workflow_dispatch') { Add-Issue 'checkout_attestation:ci_event_invalid' }
    $ciSha = Read-RequiredString $ci 'sha' 'checkout_attestation:ci'
    if ($null -ne $ciSha -and $ciSha -notmatch '^[0-9a-fA-F]{40}$') { Add-Issue 'checkout_attestation:ci_sha_invalid' }
    $runId = Read-RequiredInteger $ci 'runId' 'checkout_attestation:ci' 1
    $runAttempt = Read-RequiredInteger $ci 'runAttempt' 'checkout_attestation:ci' 1
    foreach ($name in @('workflow', 'workflowRef', 'serverUrl', 'runUrl', 'runnerName', 'runnerOs', 'runnerArchitecture')) {
        [void](Read-RequiredString $ci $name 'checkout_attestation:ci')
    }
    if ([string]$ci.serverUrl -ne 'https://github.com') { Add-Issue 'checkout_attestation:ci_server_url_invalid' }
    if ([string]$ci.workflow -ne 'M19 Capacity Evidence') { Add-Issue 'checkout_attestation:ci_workflow_invalid' }
    if ($null -ne $repository) {
        Test-ExactValue "$repository/.github/workflows/m19-capacity-evidence.yml@refs/heads/main" ([string]$ci.workflowRef) 'checkout_attestation:ci_workflow_ref_invalid'
    }
    if ([string]$ci.runnerOs -notmatch '^(?i:linux)$') { Add-Issue 'checkout_attestation:ci_runner_os_invalid' }
    if ([string]$ci.runnerArchitecture -notmatch '^(?i:x64)$') { Add-Issue 'checkout_attestation:ci_runner_architecture_invalid' }
    if ($null -ne $repository -and $null -ne $runId) {
        Test-ExactValue "https://github.com/$repository/actions/runs/$runId" ([string]$ci.runUrl) 'checkout_attestation:ci_run_url_mismatch'
        Test-ExactValue ([string]$ci.runUrl) $ArtifactUrl 'artifact_url_attestation_mismatch'
    }
    if ($null -ne $Identity) {
        Test-IdentityValue $Identity.commitSha $ciSha 'checkout_attestation:ci_commit_mismatch'
    }
}

function Test-ReportCiProvenance([object] $Report, [object] $Checkout, [string] $Profile) {
    if ($null -eq $Report -or $null -eq $Checkout -or $null -eq $Checkout.ci) {
        return
    }

    $evidence = $Report.evidence
    if ([string]$evidence.executionEnvironment -ne 'github-actions') {
        Add-Issue "profile:$Profile`:ci_execution_environment_invalid"
    }
    if ([string]$evidence.sourceRevisionSource -ne 'environment:GITHUB_SHA') {
        Add-Issue "profile:$Profile`:ci_source_revision_source_invalid"
    }
    Test-IdentityValue ([string]$Checkout.ci.sha) ([string]$evidence.sourceRevision) "profile:$Profile`:ci_source_revision_mismatch"
    Test-ExactValue ([string]$Checkout.ci.runId) ([string]$evidence.ciRunId) "profile:$Profile`:ci_run_id_mismatch"
    Test-ExactValue ([string]$Checkout.ci.runAttempt) ([string]$evidence.ciAttempt) "profile:$Profile`:ci_run_attempt_mismatch"
}

function Test-HardwareSnapshotAgainstReports([object] $Hardware, [object] $Checkout, [hashtable] $Reports) {
    if ($null -eq $Hardware -or $Reports.Count -eq 0) {
        return
    }

    $declaredStorageModel = Read-RequiredString $Hardware.storage 'declaredModel' 'target_hardware_snapshot:storage'
    $machine = $Hardware.machine
    $processArchitecture = Read-RequiredString $machine 'processArchitecture' 'target_hardware_snapshot:machine'
    if ($processArchitecture -cne 'X64') { Add-Issue 'target_hardware_snapshot:architecture_not_x64' }
    $snapshotProcessorCount = Read-RequiredInteger $machine 'processorCount' 'target_hardware_snapshot:machine' 1
    [void](Read-RequiredInteger $machine 'physicalMemoryBytes' 'target_hardware_snapshot:machine' 1)
    [void](Read-RequiredInteger $machine 'availableManagedMemoryBytes' 'target_hardware_snapshot:machine' 1)
    $workloadMount = Get-OptionalProperty $Hardware.storage 'workloadMount'
    $mountPath = Read-RequiredMountField $workloadMount 'path' 'target_hardware_snapshot:workload_mount'
    $mountPoint = Read-RequiredMountField $workloadMount 'mountPoint' 'target_hardware_snapshot:workload_mount'
    $mountSource = Read-RequiredMountField $workloadMount 'source' 'target_hardware_snapshot:workload_mount'
    $mountFileSystem = Read-RequiredMountField $workloadMount 'fileSystem' 'target_hardware_snapshot:workload_mount'
    $mountDeviceId = Read-RequiredMountField $workloadMount 'deviceId' 'target_hardware_snapshot:workload_mount'
    $mountResolutionSource = Read-RequiredMountField $workloadMount 'resolutionSource' 'target_hardware_snapshot:workload_mount'
    if ($mountResolutionSource -cne 'findmnt') { Add-Issue 'target_hardware_snapshot:workload_mount_resolution_invalid' }
    Test-MountFileSystem $mountFileSystem 'target_hardware_snapshot:workload_mount'
    if ($null -ne $Checkout) {
        $checkoutStorageModel = Read-RequiredString $Checkout.targetHardware 'storageModel' 'checkout_attestation:target_hardware'
        Test-ExactIdentityValue $declaredStorageModel $checkoutStorageModel 'checkout_attestation:storage_model_mismatch'
    }
    $volumes = Read-RequiredArray $Hardware.storage 'volumes' 'target_hardware_snapshot:storage'
    foreach ($profile in $profiles) {
        if (-not $Reports.ContainsKey($profile)) {
            continue
        }
        $disk = $Reports[$profile].environment.disk
        if ($null -eq $disk) {
            Add-Issue "profile:$profile`:disk_snapshot_missing"
            continue
        }
        $reportEnvironment = Get-OptionalProperty $Reports[$profile] 'environment'
        $reportHardware = Get-OptionalProperty $reportEnvironment 'hardware'
        $reportProvenance = Get-OptionalProperty $reportEnvironment 'provenance'
        if ([string](Get-OptionalProperty $reportEnvironment 'architecture') -cne 'X64') {
            Add-Issue "profile:$profile`:architecture_not_x64"
        }
        if ([string](Get-OptionalProperty $reportHardware 'operatingSystemArchitecture') -cne 'X64') {
            Add-Issue "profile:$profile`:os_architecture_not_x64"
        }
        $is64BitOperatingSystem = Get-OptionalProperty $reportHardware 'is64BitOperatingSystem'
        if ($is64BitOperatingSystem -isnot [bool] -or $is64BitOperatingSystem -ne $true) {
            Add-Issue "profile:$profile`:os_not_64_bit"
        }
        $is64BitProcess = Get-OptionalProperty $reportProvenance 'is64BitProcess'
        if ($is64BitProcess -isnot [bool] -or $is64BitProcess -ne $true) {
            Add-Issue "profile:$profile`:process_not_64_bit"
        }
        if ([string](Get-OptionalProperty $reportProvenance 'containerState') -cne 'none') {
            Add-Issue "profile:$profile`:container_state_invalid"
        }
        $reportProcessorCount = Read-RequiredInteger $reportEnvironment 'processorCount' "profile:$profile`:environment" 1
        $reportLogicalProcessorCount = Read-RequiredInteger $reportHardware 'logicalProcessorCount' "profile:$profile`:hardware" 1
        Test-ExactValue ([string]$snapshotProcessorCount) ([string]$reportProcessorCount) "profile:$profile`:processor_count_mismatch"
        Test-ExactValue ([string]$snapshotProcessorCount) ([string]$reportLogicalProcessorCount) "profile:$profile`:logical_processor_count_mismatch"
        Test-ExactIdentityValue $declaredStorageModel (Read-RequiredString $disk 'deviceModel' "profile:$profile`:disk") "profile:$profile`:storage_model_mismatch"
        if ([string]$disk.deviceModelSource -ne 'environment:SONNETDB_M19_STORAGE_MODEL') {
            Add-Issue "profile:$profile`:storage_model_source_invalid"
        }
        $rootPath = Read-RequiredString $disk 'root' "profile:$profile`:disk"
        $reportMountPoint = Read-RequiredMountField $disk 'mountPoint' "profile:$profile`:disk"
        $reportMountSource = Read-RequiredMountField $disk 'mountSource' "profile:$profile`:disk"
        $reportMountDeviceId = Read-RequiredMountField $disk 'mountDeviceId' "profile:$profile`:disk"
        $reportMountResolutionSource = Read-RequiredMountField $disk 'resolutionSource' "profile:$profile`:disk"
        $reportFileSystem = Read-RequiredMountField $disk 'fileSystem' "profile:$profile`:disk"
        if ($reportMountResolutionSource -cne 'findmnt') { Add-Issue "profile:$profile`:mount_resolution_invalid" }
        if ($null -ne $rootPath -and $null -ne $reportMountPoint -and $rootPath -cne $reportMountPoint) { Add-Issue "profile:$profile`:mount_point_root_mismatch" }
        Test-MountFileSystem $reportFileSystem "profile:$profile`:disk"
        if ($null -ne $mountPoint) { Test-ExactIdentityValue $mountPoint $reportMountPoint "profile:$profile`:mount_point_mismatch" }
        if ($null -ne $mountSource) { Test-ExactIdentityValue $mountSource $reportMountSource "profile:$profile`:mount_source_mismatch" }
        if ($null -ne $mountDeviceId) { Test-ExactIdentityValue $mountDeviceId $reportMountDeviceId "profile:$profile`:mount_device_mismatch" }
        if ($null -ne $mountFileSystem) { Test-ExactIdentityValue $mountFileSystem $reportFileSystem "profile:$profile`:mount_filesystem_mismatch" }
        if ($null -ne $mountResolutionSource) { Test-ExactIdentityValue $mountResolutionSource $reportMountResolutionSource "profile:$profile`:mount_resolution_mismatch" }
        $matchingVolume = @($volumes | Where-Object { [string]$_.root -eq $rootPath })
        if ($matchingVolume.Count -ne 1) {
            Add-Issue "profile:$profile`:snapshot_volume_missing"
            continue
        }
        $volume = $matchingVolume[0]
        Test-ExactIdentityValue (Read-RequiredString $volume 'fileSystem' 'target_hardware_snapshot:volume') (Read-RequiredString $disk 'fileSystem' "profile:$profile`:disk") "profile:$profile`:snapshot_filesystem_mismatch"
        Test-MountFileSystem (Read-RequiredString $volume 'fileSystem' 'target_hardware_snapshot:volume') 'target_hardware_snapshot:volume'
        $volumeTotal = Read-RequiredInteger $volume 'totalBytes' 'target_hardware_snapshot:volume' 1
        $reportTotal = Read-RequiredInteger $disk 'totalBytes' "profile:$profile`:disk" 1
        if ($null -ne $volumeTotal -and $null -ne $reportTotal -and $volumeTotal -ne $reportTotal) {
            Add-Issue "profile:$profile`:snapshot_total_bytes_mismatch"
        }
        [void](Read-RequiredInteger $volume 'availableBytes' 'target_hardware_snapshot:volume' 0)
        [void](Read-RequiredInteger $disk 'availableBytes' "profile:$profile`:disk" 0)
    }
}

function Get-ExpectedRawPaths {
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($profile in $profiles) {
        $paths.Add("$profile/report.json")
        $paths.Add("$profile/report.md")
    }
    $paths.Add('target-hardware.json')
    $paths.Add('checkout-attestation.json')
    return $paths.ToArray()
}

function Test-RawManifest([object] $Manifest, [object] $Identity) {
    if ($null -eq $Manifest) {
        return
    }
    if ($Manifest.schemaVersion -ne 1) { Add-Issue 'raw_manifest:schema_version_invalid' }
    $source = $Manifest.source
    $manifestCommit = Read-RequiredString $source 'commitSha' 'raw_manifest:source'
    $manifestClean = Read-RequiredBoolean $source 'worktreeClean' 'raw_manifest:source'
    $manifestStatus = @(Read-RequiredArrayAllowingEmpty $source 'worktreeStatus' 'raw_manifest:source')
    foreach ($entry in $manifestStatus) {
        if ($null -eq $entry -or [string]::IsNullOrWhiteSpace([string]$entry)) {
            Add-Issue 'raw_manifest:source_worktree_status_invalid'
            break
        }
    }
    if ($manifestClean -ne $true) { Add-Issue 'raw_manifest:source_worktree_not_clean' }
    if ($manifestClean -eq $true -and $manifestStatus.Count -ne 0) { Add-Issue 'raw_manifest:source_worktree_status_not_empty' }
    if ($manifestClean -eq $false -and $manifestStatus.Count -eq 0) { Add-Issue 'raw_manifest:source_worktree_status_missing' }
    $manifestTargetId = Read-RequiredString $Manifest.targetHardware 'id' 'raw_manifest:target_hardware'
    $manifestContract = Read-RequiredString $Manifest.targetHardware 'contract' 'raw_manifest:target_hardware'
    if ($null -ne $Identity) {
        Test-IdentityValue $Identity.commitSha $manifestCommit 'raw_manifest:commit_mismatch'
        Test-ExactIdentityValue $Identity.targetHardwareId $manifestTargetId 'raw_manifest:target_id_mismatch'
        Test-ExactIdentityValue $Identity.targetHardwareContract $manifestContract 'raw_manifest:contract_mismatch'
    }

    $entries = @($Manifest.files)
    $expectedPaths = Get-ExpectedRawPaths
    if ($entries.Count -ne $expectedPaths.Count) { Add-Issue 'raw_manifest:file_count_invalid' }
    $seen = @{}
    foreach ($entry in $entries) {
        $path = Read-RequiredString $entry 'path' 'raw_manifest:file'
        $sha = Read-RequiredString $entry 'sha256' 'raw_manifest:file'
        if ($null -eq $path -or $null -eq $sha) { continue }
        if ($path -notmatch '^[^/\\]+(?:/[^/\\]+)*$' -or $path -match '(^|/)\.\.(/|$)' -or $path -notin $expectedPaths) {
            Add-Issue "raw_manifest:unexpected_path:$path"
            continue
        }
        if ($seen.ContainsKey($path)) { Add-Issue "raw_manifest:duplicate_path:$path"; continue }
        $seen[$path] = $true
        if ($sha -notmatch '^[0-9a-f]{64}$') { Add-Issue "raw_manifest:invalid_sha256:$path"; continue }
        $candidate = Join-Path $root ($path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { Add-Issue "raw_manifest:file_missing:$path"; continue }
        if ((Get-Sha256 $candidate) -ne $sha) { Add-Issue "raw_manifest:hash_mismatch:$path" }
        [long] $manifestBytes = -1
        $validBytes = $null -ne $entry.PSObject.Properties['bytes'] -and [long]::TryParse([string]$entry.bytes, [ref]$manifestBytes)
        if (-not $validBytes -or $manifestBytes -ne (Get-Item -LiteralPath $candidate).Length) { Add-Issue "raw_manifest:byte_count_mismatch:$path" }
    }
    foreach ($expectedPath in $expectedPaths) {
        if (-not $seen.ContainsKey($expectedPath)) { Add-Issue "raw_manifest:file_missing:$expectedPath" }
    }
}

if ($ArtifactUrl -notmatch '^https://[^\s]+$') { Add-Issue 'artifact_url_invalid' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha) -and $ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') { Add-Issue 'expected_commit_invalid' }

$singleVerifier = Join-Path $PSScriptRoot 'verify-m19-capacity-report.ps1'
if (-not (Test-Path -LiteralPath $singleVerifier -PathType Leaf)) { Add-Issue 'single_report_verifier_missing' }

$identity = $null
foreach ($profile in $profiles) {
    $profileRoot = Join-Path $root $profile
    $reportPath = Join-Path $profileRoot 'report.json'
    $markdownPath = Join-Path $profileRoot 'report.md'
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { Add-Issue "profile:$profile`:report_json_missing"; continue }
    if (-not (Test-Path -LiteralPath $markdownPath -PathType Leaf)) { Add-Issue "profile:$profile`:report_markdown_missing"; continue }
    Add-File "$profile-report" $reportPath
    Add-File "$profile-markdown" $markdownPath

    $report = Read-Json $reportPath "profile:$profile`:report"
    if ($null -eq $report) { continue }
    $profileReports[$profile] = $report
    if ([string]$report.profile -ne $profile) { Add-Issue "profile:$profile`:profile_name_mismatch"; continue }
    try {
        $singleOutput = @(& $singleVerifier -ReportPath $reportPath -ExpectedCommitSha $ExpectedCommitSha -ExpectedTargetHardwareId $ExpectedTargetHardwareId -ExpectedTargetHardwareContract $ExpectedTargetHardwareContract)
        $singleResult = ($singleOutput -join "`n") | ConvertFrom-Json -Depth 16
        if ([string]$singleResult.status -ne 'PASS') {
            Add-Issue "profile:$profile`:single_report_not_authoritative"
        }
    }
    catch {
        Add-Issue "profile:$profile`:single_report_not_ready"
    }

    $current = [ordered]@{
        commitSha = Read-RequiredString $report.environment 'commitSha' "profile:$profile`:environment"
        sourceRevision = Read-RequiredString $report.evidence 'sourceRevision' "profile:$profile`:evidence"
        machineName = Read-RequiredString $report.environment 'machineName' "profile:$profile`:environment"
        architecture = Read-RequiredString $report.environment 'architecture' "profile:$profile`:environment"
        processorModel = Read-RequiredString $report.environment.hardware 'processorModel' "profile:$profile`:hardware"
        operatingSystemArchitecture = Read-RequiredString $report.environment.hardware 'operatingSystemArchitecture' "profile:$profile`:hardware"
        targetHardwareId = Read-RequiredString $report.targetHardware 'id' "profile:$profile`:target_hardware"
        targetHardwareContract = Read-RequiredString $report.targetHardware 'contract' "profile:$profile`:target_hardware"
    }
    if ($null -eq $identity) {
        $identity = $current
    }
    else {
        foreach ($key in $identity.Keys) {
            if ($key -in @('targetHardwareId', 'targetHardwareContract')) {
                Test-ExactIdentityValue $identity[$key] $current[$key] "profile:$profile`:bundle_identity_mismatch:$key"
            }
            else {
                Test-IdentityValue $identity[$key] $current[$key] "profile:$profile`:bundle_identity_mismatch:$key"
            }
        }
    }
}

$hardwarePath = Join-Path $root 'target-hardware.json'
if (-not (Test-Path -LiteralPath $hardwarePath -PathType Leaf)) {
    Add-Issue 'target_hardware_snapshot_missing'
}
else {
    Add-File 'target-hardware' $hardwarePath
    $hardware = Read-Json $hardwarePath 'target_hardware_snapshot'
    if ($null -ne $hardware) {
        $hardwareSnapshot = $hardware
        if ($hardware.schemaVersion -ne 1) { Add-Issue 'target_hardware_snapshot:schema_version_invalid' }
        $hardwareCommit = Read-RequiredString $hardware.source 'commitSha' 'target_hardware_snapshot:source'
        $hardwareClean = Read-RequiredBoolean $hardware.source 'worktreeClean' 'target_hardware_snapshot:source'
        Test-WorktreeAttestation $hardware.source 'target_hardware_snapshot:source'
        if ($hardwareClean -ne $true) { Add-Issue 'target_hardware_snapshot:worktree_not_clean' }
        $hardwareStatus = Read-RequiredString $hardware.targetHardware 'status' 'target_hardware_snapshot:target_hardware'
        if ($hardwareStatus -cne 'PASS') { Add-Issue 'target_hardware_snapshot:status_not_pass' }
        $hardwareDeclarationSource = Read-RequiredString $hardware.targetHardware 'declarationSource' 'target_hardware_snapshot:target_hardware'
        if ($hardwareDeclarationSource -cne 'environment') { Add-Issue 'target_hardware_snapshot:declaration_source_invalid' }
        $hardwareId = Read-RequiredString $hardware.targetHardware 'id' 'target_hardware_snapshot:target_hardware'
        $hardwareContract = Read-RequiredString $hardware.targetHardware 'contract' 'target_hardware_snapshot:target_hardware'
        $declaredStorageModel = Read-RequiredString $hardware.storage 'declaredModel' 'target_hardware_snapshot:storage'
        Test-ExactIdentityValue $declaredStorageModel (Read-RequiredString $hardware.targetHardware 'storageModel' 'target_hardware_snapshot:target_hardware') 'target_hardware_snapshot:storage_model_mismatch'
        if ([string]$hardware.storage.declarationSource -ne 'environment:SONNETDB_M19_STORAGE_MODEL') { Add-Issue 'target_hardware_snapshot:storage_declaration_source_invalid' }
        if (-not (Test-UsableText $declaredStorageModel)) { Add-Issue 'target_hardware_snapshot:storage_model_unusable' }
        $processors = Read-RequiredArray $hardware.machine 'processors' 'target_hardware_snapshot:machine'
        if ($processors.Count -eq 0) {
            Add-Issue 'target_hardware_snapshot:processor_inventory_missing'
        }
        else {
            $usableProcessor = @($processors | Where-Object {
                Test-UsableText (Read-RequiredString $_ 'name' 'target_hardware_snapshot:processor')
            }).Count -gt 0
            if (-not $usableProcessor) { Add-Issue 'target_hardware_snapshot:processor_inventory_unusable' }
        }
        $disks = Read-RequiredArray $hardware.storage 'disks' 'target_hardware_snapshot:storage'
        if ($disks.Count -eq 0) {
            Add-Issue 'target_hardware_snapshot:disk_inventory_missing'
        }
        else {
            $usableDisk = @($disks | Where-Object {
                Test-UsableText (Read-RequiredString $_ 'name' 'target_hardware_snapshot:disk') -or
                Test-UsableText (Read-RequiredString $_ 'model' 'target_hardware_snapshot:disk')
            }).Count -gt 0
            if (-not $usableDisk) { Add-Issue 'target_hardware_snapshot:disk_inventory_unusable' }
        }
        $volumes = Read-RequiredArray $hardware.storage 'volumes' 'target_hardware_snapshot:storage'
        if ($volumes.Count -eq 0) { Add-Issue 'target_hardware_snapshot:volume_inventory_missing' }
        if ($null -ne $identity) {
            Test-IdentityValue $identity.commitSha $hardwareCommit 'target_hardware_snapshot:commit_mismatch'
            Test-ExactIdentityValue $identity.targetHardwareId $hardwareId 'target_hardware_snapshot:target_id_mismatch'
            Test-ExactIdentityValue $identity.targetHardwareContract $hardwareContract 'target_hardware_snapshot:contract_mismatch'
            Test-IdentityValue $identity.machineName (Read-RequiredString $hardware.machine 'name' 'target_hardware_snapshot:machine') 'target_hardware_snapshot:machine_mismatch'
            Test-IdentityValue $identity.architecture (Read-RequiredString $hardware.machine 'processArchitecture' 'target_hardware_snapshot:machine') 'target_hardware_snapshot:architecture_mismatch'
        }
    }
}

$checkoutPath = Join-Path $root 'checkout-attestation.json'
if (-not (Test-Path -LiteralPath $checkoutPath -PathType Leaf)) {
    Add-Issue 'checkout_attestation_missing'
}
else {
    Add-File 'checkout-attestation' $checkoutPath
    $checkout = Read-Json $checkoutPath 'checkout_attestation'
    if ($null -ne $checkout) {
        if ($checkout.schemaVersion -ne 3) { Add-Issue 'checkout_attestation:schema_version_invalid' }
        $checkoutCommit = Read-RequiredString $checkout.source 'commitSha' 'checkout_attestation:source'
        $checkoutClean = Read-RequiredBoolean $checkout.source 'worktreeClean' 'checkout_attestation:source'
        Test-WorktreeAttestation $checkout.source 'checkout_attestation:source'
        if ($checkoutClean -ne $true) { Add-Issue 'checkout_attestation:worktree_not_clean' }
        $checkoutId = Read-RequiredString $checkout.targetHardware 'id' 'checkout_attestation:target_hardware'
        $checkoutContract = Read-RequiredString $checkout.targetHardware 'contract' 'checkout_attestation:target_hardware'
        if ([string]$checkout.targetHardware.status -ne 'PASS') { Add-Issue 'checkout_attestation:status_not_pass' }
        if ([string]$checkout.targetHardware.declarationSource -ne 'environment') { Add-Issue 'checkout_attestation:declaration_source_invalid' }
        if ($null -ne $identity) {
            Test-IdentityValue $identity.commitSha $checkoutCommit 'checkout_attestation:commit_mismatch'
            Test-ExactIdentityValue $identity.targetHardwareId $checkoutId 'checkout_attestation:target_id_mismatch'
            Test-ExactIdentityValue $identity.targetHardwareContract $checkoutContract 'checkout_attestation:contract_mismatch'
        }
        $checkoutCi = $checkout
        Test-GitHubActionsAttestation $checkout $identity
    }
}

Test-HardwareSnapshotAgainstReports $hardwareSnapshot $checkoutCi $profileReports
foreach ($profile in $profiles) {
    if ($profileReports.ContainsKey($profile)) {
        Test-ReportCiProvenance $profileReports[$profile] $checkoutCi $profile
    }
}

if ($null -eq $identity) {
    Add-Issue 'no_valid_profile_reports'
}
else {
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha)) { Test-IdentityValue $ExpectedCommitSha $identity.commitSha 'expected_commit_mismatch' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetHardwareId)) { Test-ExactIdentityValue $ExpectedTargetHardwareId $identity.targetHardwareId 'expected_target_id_mismatch' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetHardwareContract)) { Test-ExactIdentityValue $ExpectedTargetHardwareContract $identity.targetHardwareContract 'expected_target_contract_mismatch' }
}

$manifestPath = Join-Path $root 'raw-artifact-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Add-Issue 'raw_manifest_missing'
}
else {
    $manifest = Read-Json $manifestPath 'raw_manifest'
    Test-RawManifest $manifest $identity
}

$status = if ($issues.Count -eq 0) { 'PASS' } else { 'NOT_READY' }
$verification = [ordered]@{
    schemaVersion = 2
    status = $status
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    bundleRoot = $root
    artifactUrl = $ArtifactUrl
    identity = $identity
    files = $files
    issues = @($issues)
    releaseEvidence = ($status -eq 'PASS')
    authority = if ($status -eq 'PASS') { 'github-actions-attested-fixed-target-bundle' } else { 'not-ready' }
    attestationLimitations = @(
        'The artifact records GitHub Actions context and protected-environment declarations, but cannot independently prove runner-label authorization or environment protection.',
        'Review the linked GitHub Actions run and protected environment configuration before treating a PASS artifact as fixed-target capacity evidence.'
    )
}

$outputParent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))
if (-not [string]::IsNullOrWhiteSpace($outputParent)) { New-Item -ItemType Directory -Force -Path $outputParent | Out-Null }
$verification | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
$verification | ConvertTo-Json -Depth 32

if ($status -ne 'PASS' -and -not $AllowNotReady) {
    throw "M19 capacity bundle is NOT_READY: $($issues -join ', ')"
}
