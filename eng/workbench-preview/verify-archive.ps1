param([Parameter(Mandatory)][string]$Archive, [Parameter(Mandatory)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$watch = [Diagnostics.Stopwatch]::StartNew()
$inventory = Get-Content (Join-Path $PackageRoot 'package-inventory.json') -Raw | ConvertFrom-Json
$expected = @{}
foreach ($file in $inventory.files) { $expected.Add($file.path, $file) }
$inventoryPath = Join-Path $PackageRoot 'package-inventory.json'
$expected.Add('package-inventory.json', @{size=(Get-Item $inventoryPath).Length;sha256=(Get-FileHash $inventoryPath).Hash.ToLowerInvariant()})
$zip = [IO.Compression.ZipFile]::OpenRead($Archive)
try {
    if ($zip.Entries.Count -ne $expected.Count -or $zip.Entries.Count -gt 5000) { throw 'ZIP inventory count mismatch.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if ($watch.Elapsed.TotalSeconds -gt 120 -or (Test-Path (Join-Path $PackageRoot 'cancel'))) { throw 'ZIP validation budget/cancel.' }
        if (-not $seen.Add($entry.FullName) -or -not $expected.ContainsKey($entry.FullName)) { throw 'ZIP unlisted/duplicate entry.' }
        $file = $expected[$entry.FullName]
        if ($entry.Length -ne $file.size) { throw 'ZIP file size mismatch.' }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($hash -cne $file.sha256) { throw "ZIP hash mismatch: $($entry.FullName)" }
    }
} finally { $zip.Dispose() }
Write-Output "PASS exact ZIP inventory: $($seen.Count) entries"
