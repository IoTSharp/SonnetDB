[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CommitSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ManifestSha256,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$clock = [Diagnostics.Stopwatch]::StartNew()
$root = [IO.Path]::GetFullPath($ArtifactRoot)
if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Artifact root cannot be a reparse point.' }
$ids = @('SonnetDB', 'SonnetDB.Caching.Distributed', 'SonnetDB.Caching.EasyCaching', 'SonnetDB.Cli', 'SonnetDB.Core', 'SonnetDB.EntityFrameworkCore', 'Testcontainers.SonnetDB')
$packages = @(Get-ChildItem -LiteralPath $root -File -Filter '*.nupkg')
if ($packages.Count -ne $ids.Count) { throw 'The candidate must contain exactly seven NuGet packages.' }
$records = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt 7 -and $clock.Elapsed.TotalSeconds -lt 90; $index++) {
    $id = $ids[$index]
    $name = "$id.$Version.nupkg"
    $file = Get-Item -LiteralPath (Join-Path $root $name)
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -gt 268435456) { throw 'Candidate file type or size is invalid.' }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $sidecarFile = Get-Item -LiteralPath ($file.FullName + '.sha256')
    if (($sidecarFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $sidecarFile.Length -gt 1024) { throw 'Invalid checksum sidecar.' }
    $sidecar = [IO.File]::ReadAllText($sidecarFile.FullName).Trim() -split '\s+'
    if ($sidecar[0] -cne $hash -and $sidecar[0].ToUpperInvariant() -cne $hash) { throw "Candidate sidecar mismatch: $name" }
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        if ($zip.Entries.Count -gt 8192) { throw 'Package entry limit exceeded.' }
        $nuspec = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($nuspec.Count -ne 1 -or $nuspec[0].Length -gt 65536) { throw 'Invalid nuspec inventory.' }
        $stream = $nuspec[0].Open()
        $xmlOptions = [Xml.XmlReaderSettings]::new()
        $xmlOptions.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $xmlOptions.XmlResolver = $null
        $xmlOptions.MaxCharactersInDocument = 65536
        $reader = [Xml.XmlReader]::Create($stream, $xmlOptions)
        try { $xml = [Xml.XmlDocument]::new(); $xml.Load($reader) }
        finally { $reader.Dispose(); $stream.Dispose() }
        $actualId = $xml.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="id"]').InnerText
        $actualVersion = $xml.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="version"]').InnerText
        $repository = $xml.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="repository"]')
        if ($actualId -cne $id -or $actualVersion -cne $Version -or $repository.commit -cne $CommitSha.ToLowerInvariant()) { throw "Candidate identity mismatch: $name" }
    }
    finally { $zip.Dispose() }
    $records.Add([pscustomobject]@{ file = $name; sha256 = $hash; bytes = $file.Length })
}
if ($records.Count -ne 7) { throw 'Candidate verification deadline exceeded.' }
# 固定 package id 顺序、UTF-8、LF 和大写十六进制，跨平台绑定同一组不可变字节。
$canonical = ($records | ForEach-Object { $_.sha256 + '  ' + $_.file + "`n" }) -join ''
$actualManifest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
if ($actualManifest -cne $ManifestSha256.ToUpperInvariant()) { throw 'Approved candidate manifest hash does not match.' }
$report = [ordered]@{ schemaVersion = 1; status = 'VERIFIED'; version = $Version; commitSha = $CommitSha.ToLowerInvariant(); manifestSha256 = $actualManifest; packages = $records.ToArray(); generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O') }
if ($OutputPath) { $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8 }
[pscustomobject]$report
