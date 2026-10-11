param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) { throw 'Use PowerShell 7+ on Windows x64.' }
$watch = [Diagnostics.Stopwatch]::StartNew()
$identity = Get-Content (Join-Path $PSScriptRoot 'preview-1.json') -Raw | ConvertFrom-Json
$inventory = Get-Content (Join-Path $PSScriptRoot 'package-inventory.json') -Raw | ConvertFrom-Json
$web = Get-Content (Join-Path $PSScriptRoot 'wwwroot/workbench-preview.json') -Raw | ConvertFrom-Json
if ($identity.candidate -cne ('candidate-' + $identity.version.Split('.')[-1].PadLeft(2, '0')) -or $web.candidate -cne $identity.candidate) { throw 'Preview candidate/version mismatch.' }
if ($identity.profile -cne 'preview-1' -or $identity.scopeRevision -cne 'R1' -or $identity.version -cne $inventory.version -or $identity.sourceCommit -cne $inventory.sourceCommit -or $web.version -cne $identity.version -or $web.sourceCommit -cne $identity.sourceCommit -or $web.profile -cne $identity.profile) { throw 'Preview identity mismatch.' }
if ($inventory.files.Count -gt 5000) { throw 'Inventory budget exceeded.' }
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$null = $expected.Add('package-inventory.json')
foreach ($file in $inventory.files) {
    if ($watch.Elapsed.TotalSeconds -gt 120) { throw 'Verification deadline exceeded.' }
    $absolute = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $file.path))
    if (-not $absolute.StartsWith($PSScriptRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $expected.Add($file.path)) { throw 'Unsafe/duplicate inventory path.' }
    if ((Get-Item -LiteralPath $absolute).Length -ne $file.size -or (Get-FileHash -LiteralPath $absolute).Hash.ToLowerInvariant() -cne $file.sha256) { throw "Package mismatch: $($file.path)" }
}
$actual = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse -Depth 10)
if ($actual.Count -ne $expected.Count) { throw 'Unexpected package files; keep data and logs outside the package.' }
foreach ($file in $actual) { if (-not $expected.Contains([IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName).Replace('\','/'))) { throw 'Unlisted package file.' } }
$server = Join-Path $PSScriptRoot 'SonnetDB.exe'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($server).ProductVersion -cne "$($identity.version)+$($identity.sourceCommit)") { throw 'Server version mismatch.' }
Write-Output "Verified $($identity.version) $($identity.sourceCommit); profile preview-1 R1"
if ($VerifyOnly) { return }
$env:ASPNETCORE_ENVIRONMENT = 'Production'
Push-Location $PSScriptRoot
try { & $server; if ($LASTEXITCODE -ne 0) { throw "Server exited $LASTEXITCODE" } } finally { Pop-Location }
