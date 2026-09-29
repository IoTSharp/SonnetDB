[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ManifestPath,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $CommitSha,
    [Parameter(Mandatory)]
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?\z')]
    [string] $Version,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')] [string] $Repository = 'IoTSharp/SonnetDB',
    [string] $OutputPath = 'artifacts/m43-release-evidence/report.json',
    [switch] $AllowNotReady
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
. (Join-Path $PSScriptRoot 'release-readiness-policy.ps1')

$inventory = @(
    @{ id = 'candidate-workflows'; scope = 'Candidate CI, cross-architecture AOT and release artifacts' }
    @{ id = 'm19-capacity'; scope = 'M19 #125 four fixed-target capacity and recovery profiles' }
    @{ id = 'm20-nightly'; scope = 'M20 #136 seven consecutive scheduled light/full runs' }
    @{ id = 'm25-capacity'; scope = 'M25 #174 million and ten-million document profiles' }
    @{ id = 'm27-quality-cost'; scope = 'M27/M35 real-model quality, cost, rollback and dual-network acceptance' }
    @{ id = 'm29-installation'; scope = 'M29 #258 clean Windows installation, upgrade and uninstall' }
    @{ id = 'm40-graph-production'; scope = 'M40 #367 external comparison, recovery and 168-hour production gate' }
    @{ id = 'm41-m42-performance'; scope = 'M41/M42 fixed x64/ARM64 corpus, cold start, recovery and 168-hour workload' }
)
$manifestFullPath = [IO.Path]::GetFullPath($ManifestPath)
$manifestRoot = [IO.Path]::GetDirectoryName($manifestFullPath)
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputRoot = [IO.Path]::GetDirectoryName($outputFullPath)
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$globalIssues = [Collections.Generic.List[string]]::new()
$entries = @{}
$manifestHash = $null
$originalNativeExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue

function Assert-UnlinkedPath {
    param([string] $Path)
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.LinkType)) {
            throw 'Evidence and output paths must not use symbolic links, junctions or hard links.'
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-UnlinkedFullPath {
    param([string] $Path)
    Assert-UnlinkedPath $Path
    $current = [IO.Path]::GetFullPath($Path)
    $missing = [Collections.Generic.List[string]]::new()
    for (;;) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item) {
            # Get-Item expands existing Windows short-name aliases to their actual names.
            $resolved = $item.FullName
            foreach ($part in $missing) { $resolved = [IO.Path]::Combine($resolved, $part) }
            return $resolved
        }
        $missing.Insert(0, [IO.Path]::GetFileName($current))
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { throw 'The path has no accessible filesystem root.' }
        $current = $parent
    }
}

function Test-WithinPath {
    param([string] $Path, [string] $Root)
    $prefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return [string]::Equals($Path, $Root, $pathComparison) -or $Path.StartsWith($prefix, $pathComparison)
}

function Assert-OutputIsolation {
    param([string] $InputPath, [switch] $Directory)
    Assert-UnlinkedPath $outputFullPath
    Assert-UnlinkedPath $InputPath
    if ([string]::Equals($outputFullPath, $InputPath, $pathComparison) -or (Test-WithinPath $InputPath $outputRoot)) {
        throw 'Write the aggregate report in a separate directory outside the evidence inputs.'
    }
    if ($Directory -and (Test-WithinPath $outputFullPath $InputPath)) {
        throw 'Write the aggregate report outside the raw evidence bundle.'
    }
}

function Assert-CandidateCheckout {
    $checkoutRoot = Join-Path $PSScriptRoot '..'
    $head = (& git -C $checkoutRoot rev-parse HEAD) -join ''
    if ($LASTEXITCODE -ne 0 -or $head.Trim() -ine $CommitSha) { throw 'Graph evaluation HEAD differs from the candidate commit.' }
    $changes = @(& git -C $checkoutRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Graph evaluation requires a clean candidate checkout.' }
}

$manifestFullPath = Get-UnlinkedFullPath $manifestFullPath
$manifestRoot = [IO.Path]::GetDirectoryName($manifestFullPath)
$outputFullPath = Get-UnlinkedFullPath $outputFullPath
$outputRoot = [IO.Path]::GetDirectoryName($outputFullPath)
Assert-OutputIsolation $manifestFullPath

function Get-InputPath {
    param([object] $Entry, [string] $Property, [switch] $Directory)
    $value = $Entry.$Property
    if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) { throw "Missing path: $Property" }
    $path = Get-UnlinkedFullPath ([IO.Path]::Combine($manifestRoot, $value))
    Assert-OutputIsolation $path -Directory:$Directory
    $kind = if ($Directory) { 'Container' } else { 'Leaf' }
    if (-not (Test-Path -LiteralPath $path -PathType $kind)) { throw "Evidence input does not exist: $Property" }
    return $path
}

function Read-Verification {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'The verifier did not produce a report.' }
    if ((Get-Item -LiteralPath $Path).Length -gt 16MB) { throw 'A report exceeds the 16 MiB aggregation limit.' }
    return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -Depth 64
}

function Add-FileReference {
    param([Collections.Generic.List[object]] $Files, [string] $Path, [string] $Role, [string] $ExpectedSha256)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Evidence file disappeared: $Path" }
    Assert-UnlinkedPath $Path
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $Files.Add([ordered]@{
        role = $Role; path = $Path; sha256 = $hash
    })
    if ($ExpectedSha256 -and $hash -cne $ExpectedSha256) { throw "Evidence changed after verification: $Path" }
}

try {
    if ((Get-Item -LiteralPath $manifestFullPath).Length -gt 1MB) { throw 'Manifest exceeds 1 MiB.' }
    $manifest = Get-Content -LiteralPath $manifestFullPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 32
    $manifestHash = (Get-FileHash -LiteralPath $manifestFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest.schemaVersion -isnot [long] -or $manifest.schemaVersion -ne 1) { throw 'Unsupported manifest schemaVersion.' }
    if ($manifest.gates -isnot [array] -or $manifest.gates.Count -gt $inventory.Count) { throw 'gates must be an array of at most eight entries.' }
    foreach ($entry in $manifest.gates) {
        if ($entry.id -isnot [string] -or $entry.id -cnotin $inventory.id) { throw 'Unknown gate id.' }
        if ($entries.ContainsKey($entry.id)) { throw "Duplicate gate id: $($entry.id)" }
        if ($entry.mode -isnot [string] -or $entry.mode -cnotin @('verify', 'deferred')) { throw "Invalid gate mode: $($entry.id)" }
        $entries[$entry.id] = $entry
    }
}
catch { $globalIssues.Add("manifest_invalid: $($_.Exception.Message)") }

# Validate output aliases before writing even a failure report, preserving raw inputs.
foreach ($entry in $entries.Values) {
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($property in @('manifestPath', 'bundleRoot')) {
        $field = $entry.PSObject.Properties[$property]
        if ($null -ne $field -and $field.Value -is [string]) { $paths.Add($field.Value) }
    }
    $reportsField = $entry.PSObject.Properties['reports']
    if ($null -ne $reportsField -and $reportsField.Value -is [array]) {
        foreach ($path in $reportsField.Value) { if ($path -is [string]) { $paths.Add($path) } }
    }
    foreach ($path in $paths) {
        $inputFullPath = Get-UnlinkedFullPath ([IO.Path]::Combine($manifestRoot, $path))
        Assert-OutputIsolation $inputFullPath -Directory:(Test-Path -LiteralPath $inputFullPath -PathType Container)
    }
}

[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$results = [Collections.Generic.List[object]]::new()
foreach ($gate in $inventory) {
    $entry = $entries[$gate.id]
    $issues = [Collections.Generic.List[string]]::new()
    $files = [Collections.Generic.List[object]]::new()
    $status = 'NOT_READY'
    $authority = 'no-verified-evidence'
    $verification = $null
    $verificationPath = $null
    try {
        if ($globalIssues.Count -gt 0) { throw 'The manifest is invalid; no gate can pass.' }
        if ($null -eq $entry) { throw 'No evidence or explicit deferral was supplied.' }
        if ($entry.mode -ceq 'deferred') {
            if ($entry.reason -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.reason)) { throw 'A deferral requires a reason.' }
            $status = 'DEFERRED'
            $issues.Add($entry.reason)
        }
        else {
            $gateRoot = Join-Path $outputRoot ('verifiers/' + $gate.id)
            # Each invocation has a fresh directory; a failed verifier cannot reuse an old PASS.
            $invocationRoot = Join-Path $gateRoot ([Guid]::NewGuid().ToString('N'))
            Assert-UnlinkedPath $invocationRoot
            [IO.Directory]::CreateDirectory($invocationRoot) | Out-Null
            $verificationPath = Join-Path $invocationRoot 'verification.json'
            switch -CaseSensitive ($gate.id) {
                'candidate-workflows' {
                    & (Join-Path $PSScriptRoot 'verify-release-readiness.ps1') -Repository $Repository -CommitSha $CommitSha -Version $Version -OutputPath $verificationPath | Out-Null
                    $verification = Read-Verification $verificationPath
                    if ($verification.source -cne 'github' -or $verification.commitSha -ine $CommitSha -or $verification.version -cne $Version -or $verification.repository -cne $Repository) { throw 'Candidate identity mismatch.' }
                    $expectedWorkflows = @(Get-ReleaseWorkflowPolicy -Version $Version).File
                    if ($verification.workflows -isnot [array] -or $verification.workflows.Count -ne $expectedWorkflows.Count) { throw 'Candidate workflow inventory is incomplete.' }
                    foreach ($workflow in $expectedWorkflows) {
                        $matches = @($verification.workflows | Where-Object workflow -CEQ $workflow)
                        if ($matches.Count -ne 1 -or $matches[0].ready -isnot [bool] -or -not $matches[0].ready) { throw 'Candidate workflow evidence is incomplete.' }
                    }
                    if ($verification.status -cne 'READY') { throw 'Candidate workflows are not ready.' }
                    $status = 'PASS'; $authority = 'github-candidate-workflows-and-raw-parity'
                }
                'm19-capacity' {
                    $bundle = Get-InputPath $entry 'bundleRoot' -Directory
                    foreach ($property in @('artifactUrl', 'targetHardwareId', 'targetHardwareContract')) {
                        if ($entry.$property -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.$property)) { throw "Missing M19 identity: $property" }
                    }
                    Add-FileReference $files (Join-Path $bundle 'raw-artifact-manifest.json') 'raw-bundle-manifest'
                    & (Join-Path $PSScriptRoot '../tests/SonnetDB.EcosystemSoak/scripts/verify-m19-capacity-bundle.ps1') -BundleRoot $bundle -ArtifactUrl $entry.artifactUrl -ExpectedCommitSha $CommitSha -ExpectedTargetHardwareId $entry.targetHardwareId -ExpectedTargetHardwareContract $entry.targetHardwareContract -OutputPath $verificationPath -AllowNotReady | Out-Null
                    $verification = Read-Verification $verificationPath
                    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    foreach ($file in @($verification.files)) {
                        if ($file.path -isnot [string] -or [IO.Path]::IsPathRooted($file.path) -or -not $seen.Add($file.path) -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid M19 verifier file inventory.' }
                        $rawPath = [IO.Path]::GetFullPath([IO.Path]::Combine($bundle, $file.path))
                        if (-not (Test-WithinPath $rawPath $bundle)) { throw 'M19 verifier file escapes its bundle.' }
                        Add-FileReference $files $rawPath 'raw-bundle' $file.sha256
                    }
                    $checkout = Read-Verification (Join-Path $bundle 'checkout-attestation.json')
                    $runUrl = "https://github.com/$Repository/actions/runs/$($checkout.ci.runId)"
                    if ($checkout.ci.repository -cne $Repository -or $checkout.ci.runId -notmatch '^[1-9][0-9]*$' -or $checkout.ci.sha -ine $CommitSha -or $checkout.ci.runUrl -cne $runUrl -or $entry.artifactUrl -cne $runUrl -or $verification.artifactUrl -cne $runUrl) { throw 'M19 repository or GitHub run identity mismatch.' }
                    if ($verification.status -ceq 'PASS' -and $verification.releaseEvidence -is [bool] -and $verification.releaseEvidence -and $verification.identity.commitSha -ieq $CommitSha) {
                        if ($seen.Count -ne 10 -or -not $seen.Contains('checkout-attestation.json') -or -not $seen.Contains('target-hardware.json')) { throw 'M19 verifier file inventory is incomplete.' }
                        $status = 'PASS'; $authority = [string]$verification.authority
                    }
                }
                'm20-nightly' {
                    & (Join-Path $PSScriptRoot '../tests/SonnetDB.Parity/scripts/verify-parity-nightly-evidence.ps1') -Repository $Repository -RequiredRunCount 7 -OutputPath $verificationPath -AllowNotReady | Out-Null
                    $verification = Read-Verification $verificationPath
                    if ($verification.source -cne 'github' -or $verification.repository -cne $Repository) { throw 'Nightly evidence must come from the configured GitHub repository.' }
                    if ($verification.status -ceq 'READY') { $status = 'PASS'; $authority = 'github-seven-day-scheduled-observation' }
                }
                'm25-capacity' {
                    if ($entry.reports -isnot [array] -or $entry.reports.Count -ne 2) { throw 'M25 requires both million and ten-million reports.' }
                    if ($entry.targetHardwareId -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.targetHardwareId)) { throw 'M25 requires targetHardwareId.' }
                    $profiles = @{}
                    $decisions = [Collections.Generic.List[string]]::new()
                    foreach ($relativeReport in $entry.reports) {
                        $reportPath = Get-InputPath ([pscustomobject]@{ report = $relativeReport }) 'report'
                        Add-FileReference $files $reportPath 'raw-document-report'
                        $raw = Read-Verification $reportPath
                        if ($raw.profile -cnotin @('million', 'ten-million') -or $profiles.ContainsKey([string]$raw.profile)) { throw 'M25 profile is invalid or duplicated.' }
                        $profiles[$raw.profile] = $true
                        $profileOutput = Join-Path $invocationRoot ($raw.profile + '.json')
                        & (Join-Path $PSScriptRoot '../tests/SonnetDB.DocumentSoak/scripts/verify-document-soak-evidence.ps1') -Report $reportPath -ExpectedCommitSha $CommitSha -ExpectedTargetHardwareId $entry.targetHardwareId -Output $profileOutput -AllowNotReady | Out-Null
                        $profileVerification = Read-Verification $profileOutput
                        Add-FileReference $files $profileOutput 'verification'
                        $decisions.Add([string]$profileVerification.releaseDecision)
                        foreach ($issue in @($profileVerification.issues)) { $issues.Add("$($raw.profile): $issue") }
                    }
                    # DocumentSoak explicitly lacks external attestation; preserve its decision.
                    $status = if ($decisions.Contains('NOT_READY')) { 'NOT_READY' } elseif ($decisions.Contains('DEFERRED')) { 'DEFERRED' } else { 'NOT_READY' }
                    $authority = 'self-declared-report-only'
                }
                'm40-graph-production' {
                    $graphManifest = Get-InputPath $entry 'manifestPath'
                    Add-FileReference $files $graphManifest 'raw-graph-manifest'
                    $graphProject = Join-Path $PSScriptRoot '../tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj'
                    Assert-CandidateCheckout
                    & dotnet build $graphProject -c Release --no-incremental /warnaserror | Out-Host
                    if ($LASTEXITCODE -ne 0) { throw 'Graph production evaluator rebuild failed.' }
                    Assert-CandidateCheckout
                    & dotnet run --project $graphProject -c Release --no-build -- --m40-production-gate --manifest $graphManifest --output $invocationRoot | Out-Host
                    $graphExitCode = $LASTEXITCODE
                    $verificationPath = Join-Path $invocationRoot 'm40-graph-production-gate.json'
                    $verification = Read-Verification $verificationPath
                    if ($graphExitCode -ne 0) { throw 'Graph production evaluator did not pass.' }
                    Assert-CandidateCheckout
                    if ($verification.release_decision -cne 'PASS') { throw 'Graph production decision is not PASS.' }
                    $status = 'PASS'; $authority = 'm40-strict-raw-artifact-replay'
                }
                default { throw 'This scope has no combined release verifier yet; supply an explicit deferral and retain its raw reports.' }
            }
            if (Test-Path -LiteralPath $verificationPath -PathType Leaf) { Add-FileReference $files $verificationPath 'verification' }
            if ($null -ne $verification -and $null -ne $verification.PSObject.Properties['issues']) {
                foreach ($issue in @($verification.issues)) { if ($null -ne $issue) { $issues.Add([string]($issue | ConvertTo-Json -Compress -Depth 8)) } }
            }
            if ($status -eq 'NOT_READY' -and $issues.Count -eq 0) { $issues.Add('The original verifier did not establish release evidence.') }
        }
    }
    catch {
        $status = 'NOT_READY'
        $authority = 'no-verified-evidence'
        $issues.Add($_.Exception.Message)
        if ($verificationPath -and (Test-Path -LiteralPath $verificationPath -PathType Leaf) -and @($files | Where-Object path -EQ $verificationPath).Count -eq 0) {
            Add-FileReference $files $verificationPath 'failed-verification'
        }
    }
    $results.Add([ordered]@{ id = $gate.id; scope = $gate.scope; status = $status; authority = $authority; issues = $issues.ToArray(); files = $files.ToArray() })
}

# Recheck retained digests before publishing the summary of multiple verifiers.
foreach ($result in $results) {
    foreach ($file in $result.files) {
        if (-not (Test-Path -LiteralPath $file.path -PathType Leaf) -or (Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            $result.status = 'NOT_READY'
            $result.authority = 'no-verified-evidence'
            $result.issues += "Evidence changed during aggregation: $($file.path)"
        }
    }
}
if ($manifestHash -and (Get-FileHash -LiteralPath $manifestFullPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifestHash) { $globalIssues.Add('The input manifest changed during aggregation.') }
$statuses = @($results | ForEach-Object { $_.status })
$overall = if ($globalIssues.Count -gt 0 -or $statuses -contains 'NOT_READY') { 'NOT_READY' } elseif ($statuses -contains 'DEFERRED') { 'DEFERRED' } else { 'PASS' }
$report = [ordered]@{
    schemaVersion = 1; generatedFor = 'M43 #397'; status = $overall
    commitSha = $CommitSha.ToLowerInvariant(); version = $Version; repository = $Repository
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    manifest = @{ path = $manifestFullPath; sha256 = $manifestHash }
    counts = @{ pass = @($statuses | Where-Object { $_ -eq 'PASS' }).Count; notReady = @($statuses | Where-Object { $_ -eq 'NOT_READY' }).Count; deferred = @($statuses | Where-Object { $_ -eq 'DEFERRED' }).Count }
    gates = $results.ToArray(); issues = $globalIssues.ToArray()
    boundary = 'M43 evidence aggregation does not change release workflow policy. Seven-day scheduled Parity remains a milestone observation. A verifier PASS retains its original authority and attestation limitations; quick, fixture, missing model-cost or unexecuted installation/long-run evidence cannot become production PASS.'
}
Assert-OutputIsolation $manifestFullPath
$temporaryOutput = Join-Path $outputRoot ([Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $stream = [IO.FileStream]::new($temporaryOutput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($report | ConvertTo-Json -Depth 64))
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
    [IO.File]::Move($temporaryOutput, $outputFullPath, $true)
}
finally { if (Test-Path -LiteralPath $temporaryOutput -PathType Leaf) { Remove-Item -LiteralPath $temporaryOutput -Force } }
if ($null -eq $originalNativeExitCode) { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
else { $global:LASTEXITCODE = $originalNativeExitCode }
Write-Host "M43 evidence: $overall. Report: $outputFullPath"
if ($overall -ne 'PASS' -and -not $AllowNotReady) { throw "M43 evidence is $overall. The report lists every unresolved scope." }
