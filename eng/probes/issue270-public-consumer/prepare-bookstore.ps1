[CmdletBinding()]
param([Parameter(Mandatory)][string]$SnapshotDirectory)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($SnapshotDirectory)
$original = Join-Path $root 'tests/SonnetDB.Abp.BookStore.Tests/BookStoreSonnetHttpJourneyTests.cs'
$target = Join-Path $root 'tests/SonnetDB.Abp.BookStore.Tests/BookStoreContainsPublicPackageTests.cs'
if (Test-Path -LiteralPath $target) { throw 'Refusing to overwrite an existing application probe.' }
$source = [System.IO.File]::ReadAllText($original)
$start = $source.IndexOf('    /// <summary>SonnetDB 公开修复包')
$end = $source.IndexOf('    private static async Task LoginAsync(')
if ($start -lt 0 -or $end -le $start) { throw 'Pinned fixture structure changed.' }
$method = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'bookstore-method.cs.txt'))
$result = ($source.Substring(0, $start) + $method + $source.Substring($end)).Replace('public sealed class BookStoreSonnetHttpJourneyTests', 'public sealed class BookStoreContainsPublicPackageTests')
[System.IO.File]::WriteAllText($target, $result, [System.Text.UTF8Encoding]::new($false))
Get-FileHash -LiteralPath $target -Algorithm SHA256 | ConvertTo-Json -Compress
