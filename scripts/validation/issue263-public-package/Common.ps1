#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Receipt([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Invoke-Recorded([string]$Label, [string[]]$Arguments, [int]$TimeoutSeconds = 180,
    [string]$WorkingDirectory = $script:SourceRoot, [hashtable]$Environment = @{}, [string]$Executable = 'dotnet') {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = [string]$Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = [DateTimeOffset]::UtcNow
    if (!$process.Start()) { throw "Cannot start $Label" }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $identity = @{ pid = $process.Id; startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O'); executable = $Executable; arguments = $Arguments; workingDirectory = $WorkingDirectory }
    $timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
    $out = $stdout.GetAwaiter().GetResult()
    $err = $stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $script:Evidence "$Label.stdout.log"), $out)
    [IO.File]::WriteAllText((Join-Path $script:Evidence "$Label.stderr.log"), $err)
    $result = @{ label = $Label; identity = $identity; startedUtc = $started.ToString('O'); endedUtc = [DateTimeOffset]::UtcNow.ToString('O'); exitCode = $process.ExitCode; timedOut = $timedOut }
    Write-Receipt (Join-Path $script:Evidence "$Label.json") $result
    $process.Dispose()
    Write-Host "$Label exit=$($result.exitCode) timeout=$timedOut"
    if ($timedOut -or $result.exitCode -ne 0) { throw "$Label failed; inspect the recorded stdout/stderr." }
}

function Save-PackageEvidence([string]$Packages, [string[]]$AssetPaths) {
    $items = foreach ($id in @('sonnetdb.core', 'sonnetdb')) {
        $directory = Join-Path $Packages "$id/4.5.0-preview.ef266.5"
        $metadata = Get-Content -LiteralPath (Join-Path $directory '.nupkg.metadata') -Raw | ConvertFrom-Json
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $directory "$id.nuspec") -Raw
        if ($metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw "Unexpected package source: $id" }
        if ($nuspec.package.metadata.repository.commit -ne '1af23d8f299febfdf66b040ab3419980396acf46') { throw "Unexpected commit: $id" }
        @{ id = $id; version = '4.5.0-preview.ef266.5'; source = $metadata.source; repositoryCommit = $nuspec.package.metadata.repository.commit; nupkgSha256 = (Get-FileHash -LiteralPath (Join-Path $directory "$id.4.5.0-preview.ef266.5.nupkg") -Algorithm SHA256).Hash; nugetContentHash = $metadata.contentHash }
    }
    $assets = foreach ($path in $AssetPaths) {
        $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $sources = @($value.project.restore.sources.PSObject.Properties.Name)
        $folders = @($value.packageFolders.PSObject.Properties.Name)
        if ($sources.Count -ne 1 -or $sources[0] -ne 'https://api.nuget.org/v3/index.json') { throw "Unexpected restore source in $path" }
        if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($Packages).TrimEnd('\', '/')) { throw "Unexpected package fallback in $path" }
        @{ path = $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; sources = $sources; packageFolders = $folders; projectReferences = @($value.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' } | Select-Object -ExpandProperty Name) }
    }
    Write-Receipt (Join-Path $script:Evidence 'public-packages.json') @{ packages = @($items); assets = @($assets) }
}

function Save-Manifest([string]$SourceDirectory) {
    $files = @()
    foreach ($root in @($SourceDirectory, $script:Evidence)) {
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
            if ($relative -match '(^|[\\/])(bin|obj|packages|http-cache|server|chloe|data|temp|cli-home)([\\/]|$)' -or $file.Name -eq 'manifest.json') { continue }
            $files += @{ root = $root; path = $relative; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
        }
    }
    Write-Receipt (Join-Path $script:Evidence 'manifest.json') @{ generatedUtc = [DateTimeOffset]::UtcNow.ToString('O'); files = $files }
}
