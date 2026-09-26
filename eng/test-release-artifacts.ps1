[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$validator = Join-Path $PSScriptRoot 'verify-release-artifacts.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonnetdb-release-artifacts-' + [Guid]::NewGuid().ToString('N'))
$version = '4.0.0-test.1'
$passed = 0

function Write-Checksum {
    param([string]$Path)
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$Path.sha256" -Value "$hash  $([IO.Path]::GetFileName($Path))" -Encoding ascii
}

function Write-Zip {
    param([string]$Path, [hashtable]$Entries)
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path }
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open())
            try { $writer.Write($Entries[$name]) } finally { $writer.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    Write-Checksum -Path $Path
}

function Assert-Rejected {
    param([string]$Name, [scriptblock]$Action, [string]$ExpectedMessage)
    $rejected = $false
    try { & $Action } catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw "Unexpected failure in ${Name}: $_" }
        $rejected = $true
    }
    if (-not $rejected) { throw "Invalid release was accepted: $Name" }
    $script:passed++
    Write-Host "PASS rejected: $Name"
}

try {
    $nugetRoot = Join-Path $testRoot 'nuget'
    $ids = @('SonnetDB.Core', 'SonnetDB', 'SonnetDB.EntityFrameworkCore', 'SonnetDB.Caching.EasyCaching',
        'SonnetDB.Caching.Distributed', 'SonnetDB.Cli', 'Testcontainers.SonnetDB')
    foreach ($id in $ids) {
        Write-Zip -Path (Join-Path $nugetRoot "$id.$version.nupkg") -Entries @{
            "$id.nuspec" = "<package><metadata><id>$id</id><version>$version</version></metadata></package>"
        }
    }
    & $validator -Stage nuget -Version $version -ArtifactRoot $nugetRoot
    $passed++

    $corePath = Join-Path $nugetRoot "SonnetDB.Core.$version.nupkg"
    Add-Content -LiteralPath $corePath -Value 'tampered'
    Assert-Rejected 'modified package bytes' { & $validator -Stage nuget -Version $version -ArtifactRoot $nugetRoot } 'Checksum mismatch'
    Write-Zip -Path $corePath -Entries @{ 'SonnetDB.Core.nuspec' = '<package><metadata><id>SonnetDB.Core</id><version>3.1.0</version></metadata></package>' }
    Assert-Rejected 'renamed old package' { & $validator -Stage nuget -Version $version -ArtifactRoot $nugetRoot } 'identity/version mismatch'
    Write-Zip -Path $corePath -Entries @{ 'SonnetDB.Core.nuspec' = "<package><metadata><id>OtherPackage</id><version>$version</version></metadata></package>" }
    Assert-Rejected 'renamed unrelated package' { & $validator -Stage nuget -Version $version -ArtifactRoot $nugetRoot } 'identity/version mismatch'
    Remove-Item -LiteralPath $corePath
    Assert-Rejected 'incomplete package inventory' { & $validator -Stage nuget -Version $version -ArtifactRoot $nugetRoot } 'Expected exactly 7'

    $bundleRoot = Join-Path $testRoot 'bundles/win-x64'
    $sdkEntries = @{ 'cli/SonnetDB.Cli.exe' = 'fixture'; 'README.md' = 'fixture'; 'LICENSE' = 'fixture' }
    $sdkEntries["packages/SonnetDB.Core.$version.nupkg"] = 'fixture'
    $sdkEntries["packages/SonnetDB.$version.nupkg"] = 'fixture'
    Write-Zip -Path (Join-Path $bundleRoot "sndb-sdk-$version-win-x64.zip") -Entries $sdkEntries
    $serverEntries = $sdkEntries.Clone()
    $serverEntries['SonnetDB.exe'] = 'fixture'
    $serverEntries['appsettings.json'] = '{}'
    $serverZip = Join-Path $bundleRoot "sonnetdb-full-$version-win-x64.zip"
    Write-Zip -Path $serverZip -Entries $serverEntries
    Write-Zip -Path (Join-Path $bundleRoot "sonnetdb-studio-$version-win-x64.zip") -Entries @{
        'SonnetDB.Studio.exe' = 'fixture'; 'server/SonnetDB.exe' = 'fixture'; 'README.md' = 'fixture'; 'LICENSE' = 'fixture'
    }
    $installerRoot = Join-Path $testRoot 'installers/win-x64'
    $null = New-Item -ItemType Directory -Path $installerRoot -Force
    foreach ($name in @('sonnetdb', 'sonnetdb-studio')) {
        $path = Join-Path $installerRoot "$name-$version-win-x64.msi"
        Set-Content -LiteralPath $path -Value 'fixture'
        Write-Checksum -Path $path
    }
    & $validator -Stage bundles -Version $version -Rid win-x64 -ArtifactRoot $testRoot
    $passed++
    $serverEntries.Remove('SonnetDB.exe')
    Write-Zip -Path $serverZip -Entries $serverEntries
    Assert-Rejected 'server missing from nonempty archive' { & $validator -Stage bundles -Version $version -Rid win-x64 -ArtifactRoot $testRoot } "missing 'SonnetDB.exe'"
    $serverEntries['SonnetDB.exe'] = 'fixture'
    Write-Zip -Path $serverZip -Entries $serverEntries
    Remove-Item -LiteralPath (Join-Path $installerRoot "sonnetdb-studio-$version-win-x64.msi.sha256")
    Assert-Rejected 'missing installer checksum' { & $validator -Stage bundles -Version $version -Rid win-x64 -ArtifactRoot $testRoot } 'Missing checksum'

    $linuxBundles = Join-Path $testRoot 'bundles/linux-x64'
    $linuxInstallers = Join-Path $testRoot 'installers/linux-x64'
    $null = New-Item -ItemType Directory -Path $linuxBundles, $linuxInstallers -Force
    foreach ($name in @('sndb-sdk', 'sonnetdb-full')) {
        $archiveName = "$name-$version-linux-x64"
        $stagingRoot = Join-Path $testRoot "staging/$archiveName"
        $entries = @('cli/SonnetDB.Cli', 'README.md', 'LICENSE', "packages/SonnetDB.Core.$version.nupkg", "packages/SonnetDB.$version.nupkg")
        if ($name -eq 'sonnetdb-full') { $entries += @('SonnetDB', 'appsettings.json') }
        foreach ($entry in $entries) {
            $path = Join-Path $stagingRoot $entry
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force
            Set-Content -LiteralPath $path -Value 'fixture'
        }
        $path = Join-Path $linuxBundles "$archiveName.tar.gz"
        & tar -czf $path -C (Join-Path $testRoot 'staging') $archiveName
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create positive Linux bundle fixture.' }
        Write-Checksum -Path $path
    }
    foreach ($extension in @('deb', 'rpm')) {
        $path = Join-Path $linuxInstallers "sonnetdb-$version-linux-x64.$extension"
        Set-Content -LiteralPath $path -Value 'fixture'
        Write-Checksum -Path $path
    }
    & $validator -Stage bundles -Version $version -Rid linux-x64 -ArtifactRoot $testRoot
    $passed++
    $rpmPath = Join-Path $linuxInstallers "sonnetdb-$version-linux-x64.rpm"
    [IO.File]::WriteAllBytes($rpmPath, [byte[]]@())
    Write-Checksum -Path $rpmPath
    Assert-Rejected 'empty Linux installer with correct checksum' { & $validator -Stage bundles -Version $version -Rid linux-x64 -ArtifactRoot $testRoot } 'Missing or empty release artifact'

    foreach ($rid in @('linux-x64', 'win-x64', 'win-x86')) {
        $connectorRoot = Join-Path $testRoot "connectors/$rid"
        $languages = if ($rid -eq 'win-x86') { @('c', 'vb6') } else { @('c', 'java', 'go', 'rust', 'python', 'purebasic') }
        $native = if ($rid.StartsWith('win-')) { 'SonnetDB.Native.dll' } else { 'SonnetDB.Native.so' }
        foreach ($language in $languages) {
            $prefix = "sonnetdb-connector-$language-$version-$rid"
            $entries = @{ "$prefix/README.md" = 'fixture'; "$prefix/LICENSE" = 'fixture'; "$prefix/VERSION" = $version; "$prefix/native/$native" = 'fixture' }
            if ($language -eq 'vb6') { $entries["$prefix/native/SonnetDB.VB6.Native.dll"] = 'fixture' }
            if ($language -eq 'java') {
                $jni = if ($rid.StartsWith('win-')) { 'SonnetDB.Java.Native.dll' } else { 'libSonnetDB.Java.Native.so' }
                $entries["$prefix/native/$jni"] = 'fixture'
                $entries["$prefix/lib/sonnetdb-java.jar"] = 'fixture'
            }
            Write-Zip -Path (Join-Path $connectorRoot "$prefix.zip") -Entries $entries
        }
        & $validator -Stage connectors -Version $version -Rid $rid -ArtifactRoot $connectorRoot
        $passed++
        $prefix = "sonnetdb-connector-c-$version-$rid"
        Write-Zip -Path (Join-Path $connectorRoot "$prefix.zip") -Entries @{ "$prefix/README.md" = 'fixture' }
        Assert-Rejected "missing $rid native runtime" { & $validator -Stage connectors -Version $version -Rid $rid -ArtifactRoot $connectorRoot } "missing '$prefix/native/$native'"
    }

    Write-Host "Release artifact contracts passed: $passed. Fixtures validate rejection behavior, not runnable release binaries."
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to remove test output outside the temporary directory.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
