[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('nuget', 'bundles', 'connectors')]
    [string]$Stage,
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$Version,
    [Parameter(Mandatory)]
    [string]$ArtifactRoot,
    [ValidateSet('linux-x64', 'win-x64', 'win-x86')]
    [string]$Rid
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Artifact {
    param([string]$Path, [switch]$Checksum)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "Missing or empty release artifact: $Path"
    }
    if ($Checksum) {
        $checksumPath = "$Path.sha256"
        if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw "Missing checksum: $checksumPath" }
        $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        $expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
        if ($expected -cne "$actual  $([IO.Path]::GetFileName($Path))") { throw "Checksum mismatch: $Path" }
    }
}

function Get-ArchiveEntries {
    param([string]$Path)
    if ($Path.EndsWith('.tar.gz', [StringComparison]::Ordinal)) {
        $entries = @(& tar -tzf $Path)
        if ($LASTEXITCODE -ne 0) { throw "Unable to read release archive: $Path" }
        # Linux bundles retain their top-level directory; ZIP bundles do not.
        return @($entries | ForEach-Object { $_ -replace '^[^/]+/', '' })
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }) }
    finally { $archive.Dispose() }
}

function Assert-Archive {
    param([string]$Path, [string[]]$RequiredEntries, [switch]$Checksum)
    Assert-Artifact -Path $Path -Checksum:$Checksum
    $entries = @(Get-ArchiveEntries -Path $Path)
    foreach ($entry in $RequiredEntries) {
        if ($entries -cnotcontains $entry) { throw "Release archive '$Path' is missing '$entry'." }
    }
}

if ($Stage -eq 'nuget') {
    $packageIds = @('SonnetDB.Core', 'SonnetDB', 'SonnetDB.EntityFrameworkCore',
        'SonnetDB.Caching.EasyCaching', 'SonnetDB.Caching.Distributed', 'SonnetDB.Cli', 'Testcontainers.SonnetDB')
    $packages = @(Get-ChildItem -LiteralPath $ArtifactRoot -Filter '*.nupkg' -File)
    if ($packages.Count -ne $packageIds.Count) { throw "Expected exactly 7 NuGet packages; found $($packages.Count)." }
    foreach ($id in $packageIds) {
        $path = Join-Path $ArtifactRoot "$id.$Version.nupkg"
        Assert-Artifact -Path $path -Checksum
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $nuspecs = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
            if ($nuspecs.Count -ne 1) { throw "Expected one nuspec in $path." }
            $reader = [IO.StreamReader]::new($nuspecs[0].Open())
            try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($nuspec.package.metadata.id -cne $id -or $nuspec.package.metadata.version -cne $Version) {
                throw "NuGet identity/version mismatch: $path"
            }
        }
        finally { $archive.Dispose() }
    }
}
elseif ($Stage -eq 'bundles') {
    if ($Rid -notin @('linux-x64', 'win-x64')) { throw 'Bundles require linux-x64 or win-x64.' }
    $windows = $Rid -eq 'win-x64'
    $extension = if ($windows) { 'zip' } else { 'tar.gz' }
    $executable = if ($windows) { '.exe' } else { '' }
    $bundleRoot = Join-Path $ArtifactRoot "bundles/$Rid"
    $installerRoot = Join-Path $ArtifactRoot "installers/$Rid"
    $packageEntries = @("packages/SonnetDB.Core.$Version.nupkg", "packages/SonnetDB.$Version.nupkg")
    Assert-Archive -Path (Join-Path $bundleRoot "sndb-sdk-$Version-$Rid.$extension") `
        -RequiredEntries (@("cli/SonnetDB.Cli$executable", 'README.md', 'LICENSE') + $packageEntries) -Checksum
    Assert-Archive -Path (Join-Path $bundleRoot "sonnetdb-full-$Version-$Rid.$extension") `
        -RequiredEntries (@("SonnetDB$executable", "cli/SonnetDB.Cli$executable", 'appsettings.json', 'README.md', 'LICENSE') + $packageEntries) -Checksum
    if ($windows) {
        Assert-Archive -Path (Join-Path $bundleRoot "sonnetdb-studio-$Version-$Rid.zip") `
            -RequiredEntries @('SonnetDB.Studio.exe', 'server/SonnetDB.exe', 'README.md', 'LICENSE') -Checksum
        Assert-Artifact -Path (Join-Path $installerRoot "sonnetdb-$Version-$Rid.msi") -Checksum
        Assert-Artifact -Path (Join-Path $installerRoot "sonnetdb-studio-$Version-$Rid.msi") -Checksum
    }
    else {
        Assert-Artifact -Path (Join-Path $installerRoot "sonnetdb-$Version-$Rid.deb") -Checksum
        Assert-Artifact -Path (Join-Path $installerRoot "sonnetdb-$Version-$Rid.rpm") -Checksum
    }
}
else {
    if ([string]::IsNullOrEmpty($Rid)) { throw 'Connectors require a RID.' }
    $languages = if ($Rid -eq 'win-x86') { @('c', 'vb6') } else { @('c', 'java', 'go', 'rust', 'python', 'purebasic') }
    $native = if ($Rid.StartsWith('win-')) { 'SonnetDB.Native.dll' } else { 'SonnetDB.Native.so' }
    $packages = @(Get-ChildItem -LiteralPath $ArtifactRoot -Filter '*.zip' -File)
    if ($packages.Count -ne $languages.Count) { throw "Expected $($languages.Count) connector packages; found $($packages.Count)." }
    foreach ($language in $languages) {
        $prefix = "sonnetdb-connector-$language-$Version-$Rid"
        $path = Join-Path $ArtifactRoot "$prefix.zip"
        $required = @("$prefix/README.md", "$prefix/native/$native", "$prefix/LICENSE", "$prefix/VERSION")
        if ($language -eq 'vb6') { $required += "$prefix/native/SonnetDB.VB6.Native.dll" }
        if ($language -eq 'java') {
            $jni = if ($Rid.StartsWith('win-')) { 'SonnetDB.Java.Native.dll' } else { 'libSonnetDB.Java.Native.so' }
            $required += @("$prefix/native/$jni", "$prefix/lib/sonnetdb-java.jar")
        }
        Assert-Archive -Path $path -RequiredEntries $required
    }
}

Write-Host "Verified $Stage artifacts for $Version $Rid."
