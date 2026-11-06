$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
. (Join-Path $PSScriptRoot 'tag-publication-reconciliation.ps1')

$script:Clock = [Diagnostics.Stopwatch]::StartNew()
$script:Passed = 0
$script:FixtureId = 0
$script:Commit = 'a' * 40
$script:Version = '5.1.0-preview.recovery'
$script:Tag = "v$script:Version"
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$script:Root = Join-Path $tempParent ('sonnetdb-issue402-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($script:Root)

function Assert-True([bool]$Value, [string]$Message = 'Assertion failed.') { if (-not $Value) { throw $Message } }
function Copy-Fixture([object]$Value) { return ($Value | ConvertTo-Json -Depth 16 | ConvertFrom-Json -AsHashtable -Depth 16) }
function Test-Case([string]$Name, [scriptblock]$Action, [switch]$Reject) {
    Assert-PublicationBudget $script:Clock 90 ([Threading.CancellationToken]::None)
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    if ($Reject -and $null -eq $failure) { throw "Unsafe case accepted: $Name" }
    if (-not $Reject -and $null -ne $failure) { throw "Case '$Name' failed: $($failure.Exception.Message)" }
    $script:Passed++
    Write-Host "PASS $Name"
}
function New-ZipFixture([Collections.IDictionary]$Entries, [string]$Extension = '.zip') {
    Assert-PublicationBudget $script:Clock 90 ([Threading.CancellationToken]::None)
    if ($Entries.Count -gt 16 -or $script:FixtureId -ge 100) { throw 'Fixture count limit exceeded.' }
    $script:FixtureId++
    $path = Join-Path $script:Root ("fixture-$script:FixtureId$Extension")
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            Assert-PublicationBudget $script:Clock 90 ([Threading.CancellationToken]::None)
            $entry = $zip.CreateEntry([string]$name)
            $stream = $entry.Open()
            try {
                [byte[]]$bytes = if ($Entries[$name] -is [byte[]]) { $Entries[$name] } else { [Text.Encoding]::UTF8.GetBytes([string]$Entries[$name]) }
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    return $path
}
function New-NuGetFixture([string]$Dll = 'actual-dll-bytes', [string]$Commit = $script:Commit, [string]$Signature = '', [string]$Id = 'SonnetDB.Core') {
    $entries = [ordered]@{
        'SonnetDB.Core.nuspec' = "<package><metadata><id>$Id</id><version>$script:Version</version><repository type='git' commit='$Commit'/></metadata></package>"
        'lib/net10.0/SonnetDB.Core.dll' = $Dll
        '[Content_Types].xml' = '<Types/>'
    }
    if ($Signature) { $entries['.signature.p7s'] = $Signature }
    return (New-ZipFixture $entries '.nupkg')
}
function Read-Package([string]$Path) {
    Get-TaggedNuGetPayloadIdentity -Path $Path -PackageId SonnetDB.Core -Version $script:Version -CommitSha $script:Commit
}
function Test-Allowlist([object]$Fixture) {
    Assert-TaggedPublicationArtifactSet -Manifest $Fixture.manifest -Sources @($Fixture.sources) `
        -Artifacts @($Fixture.artifacts) -Downloads @($Fixture.downloads) -ArtifactRoot $script:Root
}

try {
    # Smallest input first: a one-entry archive validates bounded stream exit before larger fixtures.
    Test-Case 'one-byte archive preflight' {
        $one = New-ZipFixture @{ 'a' = 'x' }
        $inventory = Get-PublicationArchiveInventory $one
        Assert-True ($inventory.entries.Count -eq 1 -and $inventory.entries[0].bytes -eq 1)
    }
    Test-Case 'large compressed entry supports arbitrary short stream reads' {
        $payload = [byte[]]::new(200000)
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $random.GetBytes($payload) } finally { $random.Dispose() }
        $path = New-ZipFixture @{ 'payload.bin' = $payload }
        $inventory = Get-PublicationArchiveInventory $path
        $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload)).ToLowerInvariant()
        Assert-True ($inventory.entries[0].bytes -eq $payload.Length -and $inventory.entries[0].sha256 -ceq $expected)
    }
    $candidatePath = New-NuGetFixture
    $candidate = Read-Package $candidatePath
    $signedPath = New-NuGetFixture -Signature 'first simulated repository signature'
    $signed = Read-Package $signedPath
    Test-Case 'signature changes zip bytes but preserves all effective payload' {
        Assert-True ($candidate.fileSha256 -cne $signed.fileSha256)
        Assert-True ((Compare-TaggedNuGetPublication $candidate FOUND $signed) -ceq 'ALREADY_PRESENT_SAME_PAYLOAD')
    }
    Test-Case 'different DLL with same id/version/source rejected' {
        $changed = Read-Package (New-NuGetFixture -Dll 'different-dll')
        Compare-TaggedNuGetPublication $candidate FOUND $changed
    } -Reject
    Test-Case 'same id/version from a different source commit rejected' { Read-Package (New-NuGetFixture -Commit ('b' * 40)) } -Reject
    Test-Case 'different nuspec id rejected' { Read-Package (New-NuGetFixture -Id Other.Core) } -Reject
    Test-Case 'multiple nuspec entries rejected' {
        Read-Package (New-ZipFixture @{ 'a.nuspec' = '<package/>'; 'b.nuspec' = '<package/>' } '.nupkg')
    } -Reject
    Test-Case 'DTD and external entity nuspec rejected' {
        Read-Package (New-ZipFixture @{ 'a.nuspec' = '<!DOCTYPE package [<!ENTITY ext SYSTEM "file:///no-file">]><package>&ext;</package>' } '.nupkg')
    } -Reject
    Test-Case 'case-conflicting zip entries rejected' {
        $caseEntries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $caseEntries.Add('A.dll', 'x'); $caseEntries.Add('a.DLL', 'x')
        Get-PublicationArchiveInventory (New-ZipFixture $caseEntries)
    } -Reject
    Test-Case 'zip traversal rejected' { Get-PublicationArchiveInventory (New-ZipFixture @{ '../outside.dll' = 'x' }) } -Reject
    Test-Case 'only exact signature path is excluded' {
        $other = Get-PublicationArchiveInventory (New-ZipFixture @{ '.signature.p7s' = 'sig'; 'lib/.signature.p7s' = 'payload' }) -ExcludeNuGetSignature
        Assert-True ($other.entries.Count -eq 1 -and $other.entries[0].path -ceq 'lib/.signature.p7s')
    }
    Test-Case 'pre-cancelled archive read rejected' {
        $cancel = [Threading.CancellationTokenSource]::new()
        try { $cancel.Cancel(); Get-PublicationArchiveInventory -Path $candidatePath -CancellationToken $cancel.Token }
        finally { $cancel.Dispose() }
    } -Reject

    foreach ($case in @(
        @{ status = 200; expected = 'FOUND' }, @{ status = 401; expected = 'AUTHORIZATION_ERROR' },
        @{ status = 403; expected = 'AUTHORIZATION_ERROR' }, @{ status = 404; expected = 'UNKNOWN_NOT_FOUND' },
        @{ status = 429; expected = 'RATE_LIMITED' }, @{ status = 503; expected = 'REMOTE_ERROR' }, @{ status = 0; expected = 'UNKNOWN_READ' }
    )) {
        Test-Case "HTTP $($case.status) is $($case.expected)" { Assert-True ((Get-TaggedPublicationReadState -StatusCode $case.status) -ceq $case.expected) }
    }
    Test-Case 'transport failure does not become absence' { Assert-True ((Get-TaggedPublicationReadState -TransportFailed) -ceq 'TRANSPORT_ERROR') }
    Test-Case 'explicit authoritative 404 can identify absence' { Assert-True ((Get-TaggedPublicationReadState -StatusCode 404 -AuthoritativeNotFound) -ceq 'NOT_FOUND') }
    Test-Case '404 after uncertain write remains pending' { Assert-True ((Get-TaggedPublicationReadState -StatusCode 404 -AuthoritativeNotFound -PreviousWriteUnknown) -ceq 'UNKNOWN_WRITE_PENDING') }
    Test-Case 'NuGet missing after uncertain push cannot authorize retry' { Assert-True ((Compare-TaggedNuGetPublication $candidate NOT_FOUND -PreviousWriteUnknown) -ceq 'UNKNOWN_WRITE_PENDING') }
    Test-Case 'NuGet authentication failure blocks' { Assert-True ((Compare-TaggedNuGetPublication $candidate AUTHORIZATION_ERROR) -ceq 'BLOCKED_AUTHORIZATION_ERROR') }
    Test-Case 'malformed equivalent payload hashes rejected' {
        $badCandidate = @{ id = 'SonnetDB.Core'; version = $script:Version; commitSha = $script:Commit; payloadSha256 = '' }
        Compare-TaggedNuGetPublication $badCandidate FOUND $badCandidate
    } -Reject

    $script:Writes = 0
    $script:Resolves = 0
    $fence = @{ Repository = 'IoTSharp/SonnetDB'; Tag = $script:Tag; CommitSha = $script:Commit; VerifyContent = { $true }; Action = { $script:Writes++; 'mock-only' } }
    $resolver = { param($repository, $tag) $script:Resolves++; @{ repository = $repository; tag = $tag; commitSha = $script:Commit; readState = 'FOUND' } }
    Test-Case 'fresh tag is resolved immediately for each mock write' {
        $result = Invoke-TaggedPublicationWriteFence @fence -ResolveRemoteTag $resolver
        Assert-True ($script:Writes -eq 1 -and $script:Resolves -eq 1 -and $result.state -ceq 'WRITE_RETURNED_REQUIRES_READBACK')
    }
    Test-Case 'tag moved after earlier checks blocks the next write' {
        Invoke-TaggedPublicationWriteFence @fence -ResolveRemoteTag { param($repository, $tag) @{ repository = $repository; tag = $tag; commitSha = ('b' * 40); readState = 'FOUND' } }
    } -Reject
    Assert-True ($script:Writes -eq 1) 'Moved tag performed a write.'
    Test-Case 'failed remote read blocks write' { Invoke-TaggedPublicationWriteFence @fence -ResolveRemoteTag { throw 'mock network error' } } -Reject
    Test-Case 'ambiguous tag response blocks write' { Invoke-TaggedPublicationWriteFence @fence -ResolveRemoteTag { @(@{}, @{}) } } -Reject
    Test-Case 'failed content recheck blocks write' { $changed = $fence.Clone(); $changed.VerifyContent = { $false }; Invoke-TaggedPublicationWriteFence @changed -ResolveRemoteTag $resolver } -Reject
    Test-Case 'cancelled fence never invokes a write' {
        $cancel = [Threading.CancellationTokenSource]::new()
        try { $cancel.Cancel(); Invoke-TaggedPublicationWriteFence @fence -ResolveRemoteTag $resolver -CancellationToken $cancel.Token }
        finally { $cancel.Dispose() }
    } -Reject
    Test-Case 'content deadline expires before tag or write callback' {
        $changed = $fence.Clone(); $changed.VerifyContent = { Start-Sleep -Milliseconds 1100; $true }
        Invoke-TaggedPublicationWriteFence @changed -ResolveRemoteTag $resolver -TimeoutSeconds 1
    } -Reject
    Test-Case 'unknown mock write is returned once without retry' {
        $changed = $fence.Clone(); $changed.Action = { $script:Writes++; throw [TimeoutException]::new('mock uncertain upload') }
        $result = Invoke-TaggedPublicationWriteFence @changed -ResolveRemoteTag $resolver
        Assert-True ($result.state -ceq 'UNKNOWN_WRITE_PENDING' -and -not $result.retryAllowed -and $script:Writes -eq 2)
    }

    $archivePath = New-ZipFixture ([ordered]@{ 'packages/recovery.nupkg' = [IO.File]::ReadAllBytes($candidatePath); 'reports/contract.json' = '{}' })
    $archive = Get-PublicationArchiveInventory $archivePath
    $files = @($archive.entries | ForEach-Object { @{ path = $_.path; sha256 = $_.sha256; bytes = $_.bytes } })
    $files[0].assetName = 'SonnetDB.Core.5.1.0-preview.recovery.nupkg'
    $jobs = @('NuGet Packages', 'Release Bundles (linux-x64)', 'Release Bundles (win-x64)') | ForEach-Object {
        @{ name = $_; status = 'completed'; conclusion = 'success'; run_id = 101; run_attempt = 2 }
    }
    $fixture = @{
        manifest = @{ schemaVersion = 1; repository = 'IoTSharp/SonnetDB'; tag = $script:Tag; commitSha = $script:Commit;
            sources = @(@{ runId = 101; runAttempt = 2; workflow = 'publish.yml'; artifacts = @(@{ id = 201; name = 'nuget-packages-5.1.0-preview.recovery'; sha256 = $archive.fileSha256; files = $files }) }) }
        sources = @(@{ run = @{ id = 101; run_attempt = 2; repository = @{ full_name = 'IoTSharp/SonnetDB' }; event = 'push';
                    path = '.github/workflows/publish.yml'; head_branch = $script:Tag; head_sha = $script:Commit; status = 'completed'; conclusion = 'failure' }; jobs = @($jobs) })
        artifacts = @(@{ id = 201; name = 'nuget-packages-5.1.0-preview.recovery'; expired = $false; digest = "sha256:$($archive.fileSha256)";
                workflow_run = @{ id = 101; head_sha = $script:Commit } })
        downloads = @(@{ artifactId = 201; archivePath = $archivePath })
    }
    Test-Case 'exact run attempt artifact archive and file inventory yields only selected assets' {
        $plan = Test-Allowlist $fixture
        Assert-True ($plan.uploads.Count -eq 1 -and $plan.uploads[0].artifactId -eq 201 -and $plan.uploads[0].runAttempt -eq 2 -and -not $plan.externalWriteAuthorized)
    }
    $manifestPath = Join-Path $script:Root 'approved-manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($fixture.manifest | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Test-Case 'approved manifest exact bytes can be read' { $read = Read-TaggedPublicationManifest $manifestPath $manifestHash; Assert-True ($read.sources[0].runId -eq 101) }
    Test-Case 'changed manifest fingerprint rejected' { Read-TaggedPublicationManifest $manifestPath ('0' * 64) } -Reject
    foreach ($change in @(
        @{ name = 'run attempt drift'; apply = { param($x) $x.sources[0].run.run_attempt = 3 } },
        @{ name = 'old attempt contract jobs'; apply = { param($x) $x.sources[0].jobs[0].run_attempt = 1 } },
        @{ name = 'dispatch cannot replace tagged push'; apply = { param($x) $x.sources[0].run.event = 'workflow_dispatch' } },
        @{ name = 'foreign repository'; apply = { param($x) $x.sources[0].run.repository.full_name = 'foreign/SonnetDB' } },
        @{ name = 'failed platform contract'; apply = { param($x) $x.sources[0].jobs[1].conclusion = 'failure' } },
        @{ name = 'artifact expired'; apply = { param($x) $x.artifacts[0].expired = $true } },
        @{ name = 'artifact name drift'; apply = { param($x) $x.artifacts[0].name = 'another-package-set' } },
        @{ name = 'artifact source SHA drift'; apply = { param($x) $x.artifacts[0].workflow_run.head_sha = ('b' * 40) } },
        @{ name = 'artifact id drift'; apply = { param($x) $x.artifacts[0].id = 202 } },
        @{ name = 'archive API digest conflict'; apply = { param($x) $x.artifacts[0].digest = 'sha256:' + ('0' * 64) } },
        @{ name = 'file digest conflict'; apply = { param($x) $x.manifest.sources[0].artifacts[0].files[0].sha256 = '0' * 64 } },
        @{ name = 'duplicate approved file'; apply = { param($x) $x.manifest.sources[0].artifacts[0].files += $x.manifest.sources[0].artifacts[0].files[0] } },
        @{ name = 'extra unapproved artifact'; apply = { param($x) $extra = Copy-Fixture $x.artifacts[0]; $extra.id = 999; $x.artifacts += $extra } }
    )) {
        Test-Case $change.name { $changed = Copy-Fixture $fixture; & $change.apply $changed; Test-Allowlist $changed } -Reject
    }
    Test-Case 'extra ZIP in an otherwise digest-pinned artifact rejected' {
        $changed = Copy-Fixture $fixture
        $extraPath = New-ZipFixture ([ordered]@{ 'packages/recovery.nupkg' = [IO.File]::ReadAllBytes($candidatePath); 'reports/contract.json' = '{}'; 'unexpected.zip' = 'unverified' })
        $extra = Get-PublicationArchiveInventory $extraPath
        $changed.manifest.sources[0].artifacts[0].sha256 = $extra.fileSha256
        $changed.artifacts[0].digest = "sha256:$($extra.fileSha256)"
        $changed.downloads[0].archivePath = $extraPath
        Test-Allowlist $changed
    } -Reject
    Test-Case 'download outside owned artifact root rejected' { $changed = Copy-Fixture $fixture; $changed.downloads[0].archivePath = Join-Path $PSScriptRoot 'release.ps1'; Test-Allowlist $changed } -Reject

    $packageIds = @('SonnetDB', 'SonnetDB.Caching.Distributed', 'SonnetDB.Caching.EasyCaching', 'SonnetDB.CAP', 'SonnetDB.Cli', 'SonnetDB.Client', 'SonnetDB.Core', 'SonnetDB.EntityFrameworkCore', 'SonnetDB.Protocol', 'SonnetDB.Testcontainers')
    $current = @{ status = 'READY'; repository = 'IoTSharp/SonnetDB'; commitSha = $script:Commit; version = $script:Version; source = 'github';
        policyRevision = 'tag-publication-v1'; policySource = 'current-main'; policyCommit = ('b' * 40); packageIds = $packageIds }
    $frozen = Copy-Fixture $current; $frozen.policySource = 'frozen-tag'; $frozen.policyCommit = $script:Commit
    $boundary = @{ Repository = 'IoTSharp/SonnetDB'; CommitSha = $script:Commit; Version = $script:Version; CurrentMainCommit = ('b' * 40) }
    Test-Case 'current-main and frozen-tag readiness identities are distinct' { Assert-TaggedPublicationPolicyBoundary -Current $current -Frozen $frozen @boundary }
    Test-Case 'current policy cannot masquerade as frozen policy' { $changed = Copy-Fixture $current; $changed.policySource = 'frozen-tag'; Assert-TaggedPublicationPolicyBoundary -Current $changed -Frozen $frozen @boundary } -Reject
    Test-Case 'current policy SHA drift rejected' { $changed = Copy-Fixture $current; $changed.policyCommit = 'c' * 40; Assert-TaggedPublicationPolicyBoundary -Current $changed -Frozen $frozen @boundary } -Reject
    Test-Case 'policy revision drift rejected' { $changed = Copy-Fixture $frozen; $changed.policyRevision = 'future-unreviewed'; Assert-TaggedPublicationPolicyBoundary -Current $current -Frozen $changed @boundary } -Reject
    Test-Case 'legacy Testcontainers package id rejected' { $changed = Copy-Fixture $frozen; $changed.packageIds[-1] = 'Testcontainers.SonnetDB'; Assert-TaggedPublicationPolicyBoundary -Current $current -Frozen $changed @boundary } -Reject
    Test-Case 'missing Protocol or Client inventory rejected' { $changed = Copy-Fixture $frozen; $changed.packageIds = @($changed.packageIds | Where-Object { $_ -cne 'SonnetDB.Protocol' }); Assert-TaggedPublicationPolicyBoundary -Current $current -Frozen $changed @boundary } -Reject

    $expectedAsset = @{ name = 'SonnetDB.Core.5.1.0-preview.recovery.nupkg'; sha256 = $candidate.fileSha256; bytes = (Get-Item -LiteralPath $candidatePath).Length; assetId = 77 }
    $existingAsset = @{ id = 77; name = $expectedAsset.name; digest = "sha256:$($expectedAsset.sha256)"; state = 'uploaded'; size = $expectedAsset.bytes }
    $release = @{ id = 42; tag_name = $script:Tag }
    $assetArguments = @{ ReadState = 'FOUND'; Tag = $script:Tag; ExpectedReleaseId = 42; ExpectedAssets = @($expectedAsset); Release = $release; ExistingAssets = @($existingAsset) }
    Test-Case 'same release id asset id name and digest can be skipped' { $plan = Compare-TaggedReleaseAssets @assetArguments; Assert-True ($plan.assets[0].state -ceq 'ALREADY_PRESENT_SAME_PAYLOAD' -and $plan.assets[0].id -eq 77 -and -not $plan.externalWriteAuthorized) }
    foreach ($change in @(
        @{ name = 'release id conflict'; apply = { param($x) $x.Release.id = 43 } },
        @{ name = 'asset id conflict'; apply = { param($x) $x.ExistingAssets[0].id = 78 } },
        @{ name = 'asset digest conflict'; apply = { param($x) $x.ExistingAssets[0].digest = 'sha256:' + ('0' * 64) } },
        @{ name = 'asset size conflict'; apply = { param($x) $x.ExistingAssets[0].size++ } },
        @{ name = 'unknown asset digest'; apply = { param($x) $x.ExistingAssets[0].digest = $null } },
        @{ name = 'unknown upload state'; apply = { param($x) $x.ExistingAssets[0].state = 'new' } },
        @{ name = 'duplicate remote asset'; apply = { param($x) $x.ExistingAssets += $x.ExistingAssets[0] } },
        @{ name = 'unexpected existing release asset'; apply = { param($x) $extra = Copy-Fixture $x.ExistingAssets[0]; $extra.id = 78; $extra.name = 'unexpected.zip'; $x.ExistingAssets += $extra } }
    )) {
        Test-Case $change.name { $changed = Copy-Fixture $assetArguments; & $change.apply $changed; Compare-TaggedReleaseAssets @changed } -Reject
    }
    Test-Case 'missing asset after uncertain upload remains pending' { $changed = $assetArguments.Clone(); $changed.ExistingAssets = @(); $plan = Compare-TaggedReleaseAssets @changed -PreviousWriteUnknown; Assert-True ($plan.state -ceq 'UNKNOWN_WRITE_PENDING' -and $plan.assets[0].state -ceq 'UNKNOWN_WRITE_PENDING') }
    Test-Case 'release HTTP auth failure is not creation permission' { $changed = $assetArguments.Clone(); $changed.ReadState = 'AUTHORIZATION_ERROR'; $plan = Compare-TaggedReleaseAssets @changed; Assert-True ($plan.state -ceq 'BLOCKED_AUTHORIZATION_ERROR') }
    Test-Case 'release untrusted 404 is not creation permission' { $changed = $assetArguments.Clone(); $changed.ReadState = 'UNKNOWN_NOT_FOUND'; $plan = Compare-TaggedReleaseAssets @changed; Assert-True ($plan.state -ceq 'BLOCKED_UNKNOWN_NOT_FOUND') }

    Write-Host "Tagged publication reconciliation: $script:Passed checks passed; all writes were local mock callbacks."
}
finally {
    $resolved = [IO.Path]::GetFullPath($script:Root)
    if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '\Asonnetdb-issue402-[0-9a-f]{32}\z') { throw 'Refusing unowned fixture cleanup.' }
    [IO.Directory]::Delete($resolved, $true)
}
