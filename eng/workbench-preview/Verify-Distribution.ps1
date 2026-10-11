param([Parameter(Mandatory)][string]$Seal, [Parameter(Mandatory)][string]$Delivery)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$clock = [Diagnostics.Stopwatch]::StartNew()
$sealRecord = Get-Content -LiteralPath $Seal -Raw | ConvertFrom-Json -DateKind String
$base = "sonnetdb-workbench-web-$($sealRecord.version)"
$manifest = Get-Content -LiteralPath (Join-Path $Delivery "$base-manifest.json") -Raw | ConvertFrom-Json -DateKind String
$expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach ($file in $manifest.files) { $expected.Add($file.path, $file) }
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Delivery "$base-win-x64.zip"))
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
try {
    if ($zip.Entries.Count -ne $sealRecord.archiveFiles -or $zip.Entries.Count -ne $expected.Count -or $zip.Entries.Count -gt 5000) { throw 'ZIP inventory count mismatch.' }
    $total = 0L
    foreach ($entry in $zip.Entries) {
        if ($clock.Elapsed.TotalSeconds -ge 120 -or (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $Delivery) 'cancel'))) { throw 'ZIP wall-clock/cancel budget.' }
        if (-not $seen.Add($entry.FullName) -or -not $expected.ContainsKey($entry.FullName)) { throw 'ZIP duplicate/unlisted entry.' }
        $file = $expected[$entry.FullName]
        $total += $entry.Length
        if ($total -gt 512MB -or $entry.Length -gt 128MB -or $entry.Length -ne $file.size) { throw 'ZIP size budget/mismatch.' }
        $stream = $entry.Open()
        try { $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($sha -cne $file.sha256) { throw "ZIP hash mismatch: $($entry.FullName)" }
        if ($entry.FullName -in @('wwwroot/workbench-preview.json', 'preview-1.json')) {
            $stream = $entry.Open()
            try {
                if ($entry.Length -gt 1MB) { throw 'Embedded identity size budget.' }
                $reader = [IO.StreamReader]::new($stream)
                try { $profile = $reader.ReadToEnd() | ConvertFrom-Json -DateKind String } finally { $reader.Dispose() }
                foreach ($field in @('candidate', 'version', 'sourceCommit', 'profile', 'scopeRevision', 'rid')) {
                    if ($profile.$field -cne $sealRecord.$field) { throw 'Embedded identity mismatch.' }
                }
            } finally { $stream.Dispose() }
        }
    }
    if (-not $seen.Contains('wwwroot/workbench-preview.json') -or -not $seen.Contains('preview-1.json')) { throw 'Embedded identities absent.' }
} finally { $zip.Dispose() }
Write-Output "PASS exact distribution ZIP: $($seen.Count) entries; no extraction/start/publication"
