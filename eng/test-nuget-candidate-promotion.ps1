[CmdletBinding()]
param([switch]$Micro)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$clock = [Diagnostics.Stopwatch]::StartNew()
$owner = [Guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) ('nuget-promotion-test-' + $owner)
$version = '1.2.3-candidate.1'
$commit = 'a' * 40
$ids = @('SonnetDB', 'SonnetDB.Caching.Distributed', 'SonnetDB.Caching.EasyCaching', 'SonnetDB.Cli', 'SonnetDB.Core', 'SonnetDB.EntityFrameworkCore', 'Testcontainers.SonnetDB')
$verifier = Join-Path $PSScriptRoot 'verify-nuget-candidate-promotion.ps1'
if (Test-Path -LiteralPath $root) { throw 'Unique fixture path already exists.' }
New-Item -ItemType Directory -Path $root | Out-Null
[IO.File]::WriteAllText((Join-Path $root '.owner'), $owner)
try {
    $canonical = ''
    for ($index = 0; $index -lt 7 -and $clock.Elapsed.TotalSeconds -lt 20; $index++) {
        $id = $ids[$index]
        $name = "$id.$version.nupkg"
        $path = Join-Path $root $name
        $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $entry = $zip.CreateEntry("$id.nuspec")
            $stream = $entry.Open()
            $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
            try { $writer.Write("<package><metadata><id>$id</id><version>$version</version><repository type='git' commit='$commit'/></metadata></package>") }
            finally { $writer.Dispose(); $stream.Dispose() }
        }
        finally { $zip.Dispose() }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        [IO.File]::WriteAllText($path + '.sha256', "$hash  $name`n")
        $canonical += "$hash  $name`n"
    }
    if ($index -ne 7) { throw 'Fixture creation deadline.' }
    $manifest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    $report = & $verifier -ArtifactRoot $root -Version $version -CommitSha $commit -ManifestSha256 $manifest
    if ($report.status -ne 'VERIFIED' -or $report.packages.Count -ne 7) { throw 'Valid fixture rejected.' }
    if ($Micro) { 'PASS: one immutable candidate fixture'; return }
    $cases = @('manifest', 'commit', 'version', 'tampered-file', 'self-consistent-tamper', 'extra-package')
    $passed = 1
    for ($index = 0; $index -lt 6 -and $clock.Elapsed.TotalSeconds -lt 60; $index++) {
        $case = $cases[$index]
        $testVersion = $version; $testCommit = $commit; $testManifest = $manifest
        $first = Join-Path $root ("SonnetDB.$version.nupkg")
        switch ($case) {
            manifest { $testManifest = '0' * 64 }
            commit { $testCommit = 'b' * 40 }
            version { $testVersion = '1.2.4' }
            tampered-file { [IO.File]::AppendAllText($first, 'X') }
            self-consistent-tamper {
                $changed = (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash
                [IO.File]::WriteAllText($first + '.sha256', "$changed  $([IO.Path]::GetFileName($first))`n")
            }
            extra-package { [IO.File]::WriteAllBytes((Join-Path $root 'extra.nupkg'), [byte[]]@(1)) }
        }
        $rejected = $false
        try { & $verifier -ArtifactRoot $root -Version $testVersion -CommitSha $testCommit -ManifestSha256 $testManifest | Out-Null }
        catch { $rejected = $true }
        if (!$rejected) { throw "Invalid candidate accepted: $case" }
        $passed++
    }
    if ($passed -ne 7) { throw 'Negative fixture deadline.' }
    'PASS: 7 immutable candidate validation cases; no publication requested.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $expected = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('nuget-promotion-test-' + $owner)))
    if ($resolved -cne $expected -or [IO.File]::ReadAllText((Join-Path $resolved '.owner')) -cne $owner) { throw 'Fixture cleanup ownership mismatch.' }
    $files = @(Get-ChildItem -LiteralPath $resolved -Force)
    if ($files.Count -gt 16 -or @($files | Where-Object { $_.PSIsContainer -or (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) }).Count -ne 0) { throw 'Fixture cleanup inventory mismatch.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
