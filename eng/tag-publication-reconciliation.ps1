# Pure recovery admission and local byte reconciliation. No network request or publication is performed here.
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
. (Join-Path $PSScriptRoot 'tag-publication-policy.ps1')

function Assert-PublicationBudget {
    param([Diagnostics.Stopwatch]$Clock, [int]$TimeoutSeconds, [Threading.CancellationToken]$CancellationToken)
    $CancellationToken.ThrowIfCancellationRequested()
    if ($Clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Publication reconciliation deadline exceeded.' }
}

function Get-PublicationProperty {
    param([object]$Value, [string]$Name, [switch]$Optional)
    if ($Value -is [Collections.IDictionary]) {
        if ($Value.Contains($Name)) { return $Value[$Name] }
    }
    elseif ($null -ne $Value -and $null -ne $Value.PSObject.Properties[$Name]) { return $Value.$Name }
    if (-not $Optional) { throw "Missing publication property: $Name" }
    return $null
}

function Assert-PublicationRelativePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Length -gt 1024 -or $Path.StartsWith('/') -or
        $Path.Contains('\') -or $Path.Contains(':') -or $Path.Contains([char]0) -or
        @($Path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw "Invalid publication entry path: $Path"
    }
}

function Assert-PublicationAssetName {
    param([string]$Name)
    if ($Name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._+-]{0,199}\z') { throw 'Invalid release asset name.' }
}

function Get-PublicationArchiveInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$ExcludeNuGetSignature,
        [ValidateRange(1, 90)][int]$TimeoutSeconds = 30,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt 268435456) { throw 'Invalid archive file or size.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        if ($zip.Entries.Count -gt 8192) { throw 'Publication archive entry limit exceeded.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $records = [Collections.Generic.List[object]]::new()
        $buffer = [byte[]]::new(65536)
        [long]$totalBytes = 0
        foreach ($entry in $zip.Entries) {
            Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
            Assert-PublicationRelativePath ($entry.FullName.TrimEnd('/'))
            if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) { continue }
            Assert-PublicationRelativePath $entry.FullName
            if (-not $seen.Add($entry.FullName)) { throw 'Duplicate or case-conflicting archive entry.' }
            if ($entry.Length -lt 0 -or $entry.Length -gt 268435456) { throw 'Publication entry size limit exceeded.' }
            $totalBytes += $entry.Length
            if ($totalBytes -gt 536870912) { throw 'Publication expanded byte limit exceeded.' }
            if ($ExcludeNuGetSignature -and $entry.FullName -ceq '.signature.p7s') { continue }
            $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            $stream = $entry.Open()
            try {
                [long]$readBytes = 0
                # DeflateStream may return any positive short read. Each operation advances by at
                # least one byte or exits at EOF, so expanded bytes + one is the strict bound.
                $maxChunks = $entry.Length + 1
                for ([long]$chunk = 0; $chunk -lt $maxChunks; $chunk++) {
                    Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
                    $read = $stream.Read($buffer, 0, $buffer.Length)
                    if ($read -eq 0) { break }
                    $readBytes += $read
                    if ($readBytes -gt $entry.Length) { throw 'Expanded entry exceeded its declared size.' }
                    $hash.AppendData($buffer, 0, $read)
                }
                if ($readBytes -ne $entry.Length) { throw 'Truncated publication archive entry.' }
                $records.Add([pscustomobject]@{ path = $entry.FullName; bytes = $readBytes;
                    sha256 = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant() })
            }
            finally { $stream.Dispose(); $hash.Dispose() }
        }
        return [pscustomobject]@{ entries = $records.ToArray(); fileSha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    finally { $zip.Dispose() }
}

function Get-TaggedNuGetPayloadIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$CommitSha,
        [ValidateRange(1, 90)][int]$TimeoutSeconds = 30,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $inventory = Get-PublicationArchiveInventory -Path $Path -ExcludeNuGetSignature -TimeoutSeconds $TimeoutSeconds -CancellationToken $CancellationToken
    Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
    $nuspecs = @($inventory.entries | Where-Object { $_.path.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
    if ($nuspecs.Count -ne 1 -or $nuspecs[0].path.Contains('/') -or $nuspecs[0].bytes -gt 65536) { throw 'Invalid nuspec inventory.' }
    $zip = [IO.Compression.ZipFile]::OpenRead((Get-Item -LiteralPath $Path).FullName)
    try {
        $stream = $zip.GetEntry($nuspecs[0].path).Open()
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 65536
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try { $xml = [Xml.XmlDocument]::new(); $xml.Load($reader) }
        finally { $reader.Dispose(); $stream.Dispose() }
    }
    finally { $zip.Dispose() }
    $metadata = $xml.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($metadata.Count -ne 1) { throw 'Invalid nuspec metadata.' }
    $ids = $metadata[0].SelectNodes('*[local-name()="id"]')
    $versions = $metadata[0].SelectNodes('*[local-name()="version"]')
    $repositories = $metadata[0].SelectNodes('*[local-name()="repository"]')
    if ($ids.Count -ne 1 -or $versions.Count -ne 1 -or $repositories.Count -ne 1 -or
        $ids[0].InnerText -ine $PackageId -or $versions[0].InnerText -cne $Version -or
        $repositories[0].GetAttribute('commit') -cne $CommitSha) { throw 'NuGet id/version/source commit conflict.' }
    $names = [Collections.Generic.List[string]]::new()
    $byName = @{}
    foreach ($entry in $inventory.entries) {
        Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
        $names.Add($entry.path); $byName[$entry.path] = $entry
    }
    $names.Sort([StringComparer]::Ordinal)
    $canonical = [Text.StringBuilder]::new()
    foreach ($name in $names) {
        Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
        $entry = $byName[$name]
        $null = $canonical.Append($name).Append([char]0).Append($entry.bytes).Append([char]0).Append($entry.sha256).Append("`n")
    }
    return [pscustomobject]@{ id = $PackageId; version = $Version; commitSha = $CommitSha;
        payloadSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical.ToString()))).ToLowerInvariant();
        fileSha256 = $inventory.fileSha256; entryCount = $inventory.entries.Count }
}

function Get-TaggedPublicationReadState {
    param(
        [int]$StatusCode = 0,
        [switch]$TransportFailed,
        [switch]$AuthoritativeNotFound,
        [switch]$PreviousWriteUnknown
    )
    if ($TransportFailed) { return 'TRANSPORT_ERROR' }
    if ($StatusCode -in @(401, 403)) { return 'AUTHORIZATION_ERROR' }
    if ($StatusCode -eq 200) { return 'FOUND' }
    if ($StatusCode -eq 404 -and $PreviousWriteUnknown) { return 'UNKNOWN_WRITE_PENDING' }
    if ($StatusCode -eq 404 -and $AuthoritativeNotFound) { return 'NOT_FOUND' }
    if ($StatusCode -eq 404) { return 'UNKNOWN_NOT_FOUND' }
    if ($StatusCode -eq 429) { return 'RATE_LIMITED' }
    if ($StatusCode -ge 500 -and $StatusCode -le 599) { return 'REMOTE_ERROR' }
    return 'UNKNOWN_READ'
}

function Compare-TaggedNuGetPublication {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Candidate,
        [Parameter(Mandatory)][string]$ReadState,
        [object]$Published,
        [switch]$PreviousWriteUnknown
    )
    foreach ($identity in @($Candidate) + @($Published | Where-Object { $null -ne $_ })) {
        if ($identity.id -cnotmatch '\A[A-Za-z0-9_.-]{1,100}\z' -or
            $identity.commitSha -cnotmatch '\A[0-9a-f]{40}\z' -or $identity.payloadSha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Invalid NuGet reconciliation identity.' }
    }
    if ($ReadState -ceq 'FOUND') {
        if ($null -eq $Published -or $Candidate.id -ine $Published.id -or $Candidate.version -cne $Published.version -or
            $Candidate.commitSha -cne $Published.commitSha -or $Candidate.payloadSha256 -cne $Published.payloadSha256) { throw 'Published NuGet identity or effective payload conflict.' }
        return 'ALREADY_PRESENT_SAME_PAYLOAD'
    }
    if ($ReadState -ceq 'NOT_FOUND' -and -not $PreviousWriteUnknown) { return 'MISSING_CONFIRMED' }
    if ($PreviousWriteUnknown -or $ReadState -ceq 'UNKNOWN_WRITE_PENDING') { return 'UNKNOWN_WRITE_PENDING' }
    return "BLOCKED_$ReadState"
}

function Invoke-TaggedPublicationWriteFence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$CommitSha,
        [Parameter(Mandatory)][scriptblock]$VerifyContent,
        [Parameter(Mandatory)][scriptblock]$ResolveRemoteTag,
        [Parameter(Mandatory)][scriptblock]$Action,
        [ValidateRange(1, 90)][int]$TimeoutSeconds = 30,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
    $verified = @(& $VerifyContent)
    if ($verified.Count -ne 1 -or $verified[0] -isnot [bool] -or -not $verified[0]) { throw 'Content reconciliation did not verify one exact write.' }
    Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
    # Fresh resolver is invoked for every operation, after expensive inventory checks and immediately before the action.
    $observations = @(& $ResolveRemoteTag $Repository $Tag)
    if ($observations.Count -ne 1) { throw 'Remote tag read is missing or ambiguous.' }
    $observed = $observations[0]
    if ($observed.readState -cne 'FOUND' -or $observed.repository -cne $Repository -or
        $observed.tag -cne $Tag -or $observed.commitSha -cne $CommitSha) { throw 'Remote tag no longer identifies the frozen source.' }
    Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
    try {
        $result = @(& $Action)
        return [pscustomobject]@{ state = 'WRITE_RETURNED_REQUIRES_READBACK'; retryAllowed = $false; result = $result }
    }
    catch {
        # The action may have reached the remote service. Returning an unknown state never retries it.
        return [pscustomobject]@{ state = 'UNKNOWN_WRITE_PENDING'; retryAllowed = $false; errorType = $_.Exception.GetType().FullName }
    }
}

function Assert-TaggedPublicationPolicyBoundary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Current,
        [Parameter(Mandatory)][object]$Frozen,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$CommitSha,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$CurrentMainCommit
    )
    if ($Version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?\z' -or
        [version]($Version.Split('-')[0]) -lt [version]'5.0.0') { throw 'This recovery policy requires an explicit 5.x-or-later inventory contract.' }
    $required = @('SonnetDB', 'SonnetDB.Caching.Distributed', 'SonnetDB.Caching.EasyCaching', 'SonnetDB.CAP', 'SonnetDB.Cli',
        'SonnetDB.Client', 'SonnetDB.Core', 'SonnetDB.EntityFrameworkCore', 'SonnetDB.Protocol', 'SonnetDB.Testcontainers')
    foreach ($report in @($Current, $Frozen)) {
        if ($report.status -cne 'READY' -or $report.repository -cne $Repository -or $report.commitSha -cne $CommitSha -or
            $report.version -cne $Version -or $report.source -cne 'github' -or $report.policyRevision -cne 'tag-publication-v1') { throw 'Readiness identity or policy revision conflict.' }
        $ids = @($report.packageIds)
        if ($ids.Count -ne $required.Count -or @($ids | Sort-Object -Unique).Count -ne $required.Count -or
            @($required | Where-Object { $_ -cnotin $ids }).Count -ne 0) { throw 'Current/frozen package inventory does not satisfy the ten-package policy.' }
    }
    if ($Current.policySource -cne 'current-main' -or $Frozen.policySource -cne 'frozen-tag' -or
        $Current.policyCommit -cne $CurrentMainCommit -or $Frozen.policyCommit -cne $CommitSha) { throw 'Current and frozen policy provenance is not explicit.' }
    # This is a normalized recovery contract, not a claim that the existing readiness report has these extra fields.
    return 'CURRENT_AND_FROZEN_VERIFIED'
}

function Assert-PublicationContainedFile {
    param([string]$Root, [string]$Path)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $fullPath.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'Archive is outside the owned artifact root.' }
    $item = Get-Item -LiteralPath $fullPath -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Artifact file cannot be a directory or reparse point.' }
    $directory = $item.Directory
    for ($depth = 0; $depth -lt 32 -and $null -ne $directory; $depth++) {
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Artifact ancestry cannot contain reparse points.' }
        if ($directory.FullName.Equals($rootPath, $comparison)) { return $item.FullName }
        $directory = $directory.Parent
    }
    throw 'Artifact ancestry exceeded its bound.'
}

function Read-TaggedPublicationManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{64}\z')][string]$ExpectedSha256
    )
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt 1048576) { throw 'Invalid approved manifest file or size.' }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $ExpectedSha256) { throw 'Approved manifest bytes changed.' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    return ($text.TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable -Depth 16)
}

function Assert-TaggedPublicationArtifactSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][object[]]$Sources,
        [Parameter(Mandatory)][object[]]$Artifacts,
        [Parameter(Mandatory)][object[]]$Downloads,
        [Parameter(Mandatory)][string]$ArtifactRoot,
        [ValidateRange(1, 90)][int]$TimeoutSeconds = 60,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if ($Manifest.schemaVersion -ne 1 -or $Manifest.repository -cne 'IoTSharp/SonnetDB' -or
        $Manifest.commitSha -cnotmatch '\A[0-9a-f]{40}\z' -or $Manifest.tag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\z') { throw 'Invalid approved manifest identity.' }
    $expectedSources = @($Manifest.sources)
    if ($expectedSources.Count -lt 1 -or $expectedSources.Count -gt 2 -or $Sources.Count -ne $expectedSources.Count -or
        $Artifacts.Count -gt 16 -or $Downloads.Count -gt 16) { throw 'Invalid or incomplete recovery inventory bounds.' }
    $artifactIds = [Collections.Generic.HashSet[long]]::new()
    $sourceIds = [Collections.Generic.HashSet[long]]::new()
    $assetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $uploads = [Collections.Generic.List[object]]::new()
    foreach ($expectedSource in $expectedSources) {
        Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
        if ($expectedSource.runId -le 0 -or $expectedSource.runAttempt -le 0 -or -not $sourceIds.Add([long]$expectedSource.runId)) { throw 'Invalid or duplicate approved source run.' }
        $matches = @($Sources | Where-Object { $_.run.id -eq $expectedSource.runId })
        if ($matches.Count -ne 1) { throw 'Source run is missing or duplicated.' }
        $source = $matches[0]
        if ($source.run.run_attempt -ne $expectedSource.runAttempt) { throw 'Source run attempt drifted from the approved manifest.' }
        Assert-TaggedPublicationSource -Run $source.run -Jobs @($source.jobs) -Repository $Manifest.repository -Tag $Manifest.tag -CommitSha $Manifest.commitSha -Workflow $expectedSource.workflow
        foreach ($job in @($source.jobs)) {
            if ($job.run_id -ne $expectedSource.runId -or $job.run_attempt -ne $expectedSource.runAttempt) { throw 'Contract jobs are not from the exact approved run attempt.' }
        }
        $expectedArtifacts = @($expectedSource.artifacts)
        if ($expectedArtifacts.Count -lt 1 -or $expectedArtifacts.Count -gt 16) { throw 'Invalid approved artifact count.' }
        foreach ($expectedArtifact in $expectedArtifacts) {
            Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
            if ($expectedArtifact.id -le 0 -or -not $artifactIds.Add([long]$expectedArtifact.id) -or
                $artifactIds.Count -gt 16 -or $expectedArtifact.sha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Invalid or duplicate approved artifact.' }
            $found = @($Artifacts | Where-Object { $_.id -eq $expectedArtifact.id })
            $download = @($Downloads | Where-Object { $_.artifactId -eq $expectedArtifact.id })
            if ($found.Count -ne 1 -or $download.Count -ne 1) { throw 'Artifact metadata/download is missing or duplicated.' }
            $artifact = $found[0]
            if ($artifact.expired -or $artifact.name -cne $expectedArtifact.name -or
                $artifact.workflow_run.id -ne $expectedSource.runId -or $artifact.workflow_run.head_sha -cne $Manifest.commitSha -or
                $artifact.digest -cne "sha256:$($expectedArtifact.sha256)") { throw 'Artifact origin, name, digest or retention conflict.' }
            $archivePath = Assert-PublicationContainedFile -Root $ArtifactRoot -Path $download[0].archivePath
            $remainingSeconds = [Math]::Max(1, [Math]::Ceiling($TimeoutSeconds - $clock.Elapsed.TotalSeconds))
            $inventory = Get-PublicationArchiveInventory -Path $archivePath -TimeoutSeconds $remainingSeconds -CancellationToken $CancellationToken
            if ($inventory.fileSha256 -cne $expectedArtifact.sha256) { throw 'Downloaded artifact archive digest conflict.' }
            $expectedFiles = @($expectedArtifact.files)
            if ($expectedFiles.Count -lt 1 -or $expectedFiles.Count -gt 4096 -or $inventory.entries.Count -ne $expectedFiles.Count) { throw 'Unverified extra file or missing file in artifact archive.' }
            $fileNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($expectedFile in $expectedFiles) {
                Assert-PublicationBudget $clock $TimeoutSeconds $CancellationToken
                Assert-PublicationRelativePath $expectedFile.path
                if (-not $fileNames.Add([string]$expectedFile.path) -or $expectedFile.sha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Invalid or duplicate approved file.' }
                $entries = @($inventory.entries | Where-Object path -CEQ $expectedFile.path)
                if ($entries.Count -ne 1 -or $entries[0].sha256 -cne $expectedFile.sha256 -or $entries[0].bytes -ne $expectedFile.bytes) { throw 'Approved file payload/length conflict.' }
                $assetName = Get-PublicationProperty $expectedFile 'assetName' -Optional
                if (-not [string]::IsNullOrEmpty([string]$assetName)) {
                    Assert-PublicationAssetName $assetName
                    if ($expectedFile.bytes -le 0) { throw 'Release assets cannot have empty payloads.' }
                    if (-not $assetNames.Add([string]$assetName)) { throw 'Conflicting asset names across approved artifacts.' }
                    $uploads.Add([pscustomobject]@{ runId = $expectedSource.runId; runAttempt = $expectedSource.runAttempt;
                        artifactId = $expectedArtifact.id; archivePath = $archivePath; entryPath = $expectedFile.path;
                        name = $assetName; sha256 = $expectedFile.sha256; bytes = $expectedFile.bytes })
                }
            }
        }
    }
    if ($artifactIds.Count -ne $Artifacts.Count -or $artifactIds.Count -ne $Downloads.Count) { throw 'Extra unapproved artifact or download was supplied.' }
    return [pscustomobject]@{ state = 'LOCAL_ALLOWLIST_VERIFIED'; uploads = $uploads.ToArray(); externalWriteAuthorized = $false }
}

function Compare-TaggedReleaseAssets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReadState,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][long]$ExpectedReleaseId,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ExpectedAssets,
        [object]$Release,
        [AllowEmptyCollection()][object[]]$ExistingAssets = @(),
        [switch]$PreviousWriteUnknown
    )
    if ($ReadState -cne 'FOUND') {
        if ($ReadState -ceq 'NOT_FOUND' -and -not $PreviousWriteUnknown) { return [pscustomobject]@{ state = 'MISSING_CONFIRMED'; assets = @(); externalWriteAuthorized = $false } }
        $state = if ($PreviousWriteUnknown) { 'UNKNOWN_WRITE_PENDING' } else { "BLOCKED_$ReadState" }
        return [pscustomobject]@{ state = $state; assets = @(); externalWriteAuthorized = $false }
    }
    if ($ExpectedReleaseId -le 0 -or $Release.id -ne $ExpectedReleaseId -or $Release.tag_name -cne $Tag -or
        $ExpectedAssets.Count -gt 256 -or $ExistingAssets.Count -gt 256) { throw 'Release id/tag/inventory conflict.' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $ids = [Collections.Generic.HashSet[long]]::new()
    foreach ($asset in $ExistingAssets) {
        Assert-PublicationBudget $clock 10 ([Threading.CancellationToken]::None)
        Assert-PublicationAssetName $asset.name
        if ($asset.id -le 0 -or -not $ids.Add([long]$asset.id) -or -not $names.Add([string]$asset.name)) { throw 'Duplicate or invalid remote release asset.' }
        if (@($ExpectedAssets | Where-Object name -CEQ $asset.name).Count -ne 1) { throw 'Existing release contains an unapproved asset.' }
    }
    $names.Clear()
    $records = [Collections.Generic.List[object]]::new()
    foreach ($expected in $ExpectedAssets) {
        Assert-PublicationBudget $clock 10 ([Threading.CancellationToken]::None)
        Assert-PublicationAssetName $expected.name
        if (-not $names.Add([string]$expected.name) -or $expected.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or $expected.bytes -le 0) { throw 'Duplicate/invalid approved asset.' }
        $matches = @($ExistingAssets | Where-Object name -CEQ $expected.name)
        if ($matches.Count -eq 0) {
            $decision = if ($PreviousWriteUnknown) { 'UNKNOWN_WRITE_PENDING' } else { 'MISSING_CONFIRMED' }
            $records.Add([pscustomobject]@{ name = $expected.name; state = $decision; id = $null })
            continue
        }
        $asset = $matches[0]
        $expectedId = Get-PublicationProperty $expected 'assetId' -Optional
        if ($null -ne $expectedId -and $asset.id -ne $expectedId) { throw 'Existing asset id drifted.' }
        if ($asset.state -cne 'uploaded' -or $asset.digest -cnotmatch '\Asha256:[0-9a-f]{64}\z') { throw 'Asset result/digest is unknown; independent readback is required.' }
        if ($asset.digest -cne "sha256:$($expected.sha256)" -or $asset.size -ne $expected.bytes) { throw 'Existing asset payload conflict; replacement is forbidden.' }
        $records.Add([pscustomobject]@{ name = $expected.name; state = 'ALREADY_PRESENT_SAME_PAYLOAD'; id = $asset.id })
    }
    $overall = if (@($records | Where-Object state -CEQ 'UNKNOWN_WRITE_PENDING').Count -gt 0) { 'UNKNOWN_WRITE_PENDING' } else { 'RECONCILED_REQUIRES_WRITE_FENCE' }
    return [pscustomobject]@{ state = $overall; releaseId = $ExpectedReleaseId; assets = $records.ToArray(); externalWriteAuthorized = $false }
}
