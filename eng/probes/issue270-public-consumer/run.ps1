[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceRoot)

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$evidence = [System.IO.Path]::GetFullPath($EvidenceRoot)
$run = Join-Path $evidence ('r-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
if (Test-Path -LiteralPath $run) { throw 'Refusing to reuse a consumer directory.' }
New-Item -ItemType Directory -Path $run | Out-Null
$sourceNames = @('Program.cs', 'ContainsRepro.csproj', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config')
foreach ($name in $sourceNames) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $run $name) }
$env:NUGET_PACKAGES = Join-Path $run 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $run 'http-cache'
$env:DOTNET_CLI_HOME = Join-Path $run 'cli-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$steps = [System.Collections.Generic.List[object]]::new()
$version = '4.5.0-preview.ef266.5'
$sourceCommit = '1af23d8f299febfdf66b040ab3419980396acf46'
$packageEvidence = @()
$failure = $null

function Invoke-BoundedDotnet([string]$Name, [string[]]$Arguments, [int]$TimeoutSeconds) {
    $start = [DateTimeOffset]::UtcNow
    $info = [System.Diagnostics.ProcessStartInfo]::new($dotnet)
    $info.WorkingDirectory = $run
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        [System.IO.File]::WriteAllText((Join-Path $run ($Name + '.stdout.log')), $stdout.GetAwaiter().GetResult(), $utf8)
        [System.IO.File]::WriteAllText((Join-Path $run ($Name + '.stderr.log')), $stderr.GetAwaiter().GetResult(), $utf8)
        $result = [pscustomobject]@{
            name = $Name; arguments = $Arguments; pid = $process.Id
            startedUtc = $start; finishedUtc = [DateTimeOffset]::UtcNow
            timedOut = $timedOut; exitCode = $process.ExitCode
        }
        $steps.Add($result)
        Write-Host ($result | ConvertTo-Json -Compress)
        return $result
    } finally { $process.Dispose() }
}

try {
    $restore = Invoke-BoundedDotnet 'restore' @('restore', 'ContainsRepro.csproj', '--configfile', 'NuGet.Config', '--packages', $env:NUGET_PACKAGES, '--no-cache', '-p:RestoreFallbackFolders=', '-p:NuGetAudit=false', '-nr:false') 300
    if ($restore.exitCode -ne 0 -or $restore.timedOut) { throw 'Isolated restore failed.' }
    $assets = Get-Content -LiteralPath (Join-Path $run 'obj/project.assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    if ($folders.Count -ne 1 -or [System.IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne $env:NUGET_PACKAGES.TrimEnd('\', '/')) {
        throw 'Unexpected package folder or fallback in project.assets.json.'
    }
    if (@($assets.libraries.PSObject.Properties.Value | Where-Object { $_.type -ne 'package' }).Count -ne 0) {
        throw 'Consumer contains a non-package project/library reference.'
    }
    foreach ($id in @('sonnetdb.core', 'sonnetdb', 'sonnetdb.entityframeworkcore')) {
        $packagePath = Join-Path $env:NUGET_PACKAGES "$id/$version"
        $metadata = Get-Content -LiteralPath (Join-Path $packagePath '.nupkg.metadata') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw "Unexpected source for $id" }
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $packagePath "$id.nuspec") -Raw -Encoding UTF8
        if ($nuspec.package.metadata.version -ne $version -or $nuspec.package.metadata.repository.commit -ne $sourceCommit) {
            throw "Unexpected package identity for $id"
        }
        $packageEvidence += [pscustomobject]@{
            id = $id; version = $version; source = $metadata.source
            repositoryCommit = $nuspec.package.metadata.repository.commit
            nupkgSha256 = (Get-FileHash -LiteralPath (Join-Path $packagePath "$id.$version.nupkg") -Algorithm SHA256).Hash
            nuspecSha256 = (Get-FileHash -LiteralPath (Join-Path $packagePath "$id.nuspec") -Algorithm SHA256).Hash
        }
    }
    $build = Invoke-BoundedDotnet 'build' @('build', 'ContainsRepro.csproj', '-c', 'Release', '--no-restore', '-nr:false') 180
    if ($build.exitCode -ne 0 -or $build.timedOut) { throw 'Consumer build failed.' }
    foreach ($provider in @('SonnetDB', 'Sqlite')) {
        foreach ($pattern in @('percent', 'underscore', 'backslash')) {
            $name = "$provider-original-$pattern"
            $null = Invoke-BoundedDotnet $name @('bin/Release/net10.0/ContainsRepro.dll', $provider, 'original', $pattern, (Join-Path $run $name)) 45
        }
        $name = "$provider-matrix"
        $null = Invoke-BoundedDotnet $name @('bin/Release/net10.0/ContainsRepro.dll', $provider, 'matrix', 'all', (Join-Path $run $name)) 110
    }
} catch { $failure = $_.Exception.ToString(); Write-Host $failure }

$receipts = @(Get-ChildItem -LiteralPath $run -Directory | Where-Object { $_.Name -match '^(SonnetDB|Sqlite)-' } | ForEach-Object {
    Get-Item -LiteralPath (Join-Path $_.FullName 'receipt.json') -ErrorAction SilentlyContinue
})
$cases = @($receipts | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).Cases })
$files = @()
foreach ($file in @(Get-ChildItem -LiteralPath $run -File) + $receipts + @(Get-Item -LiteralPath (Join-Path $run 'obj/project.assets.json') -ErrorAction SilentlyContinue) + @(Get-ChildItem -LiteralPath (Join-Path $run 'bin/Release/net10.0') -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.dll', '.pdb', '.json') })) {
    $files += [pscustomobject]@{ path = [System.IO.Path]::GetRelativePath($run, $file.FullName); sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$final = [pscustomobject]@{
    issue = 270; runDirectory = $run; packageVersion = $version; packageSourceCommit = $sourceCommit
    privatePackages = $env:NUGET_PACKAGES; privateHttpCache = $env:NUGET_HTTP_CACHE_PATH
    projectReferences = 0; sourceDllSubstitutions = 0; packages = $packageEvidence
    steps = $steps.ToArray(); failure = $failure; cases = $cases.Count
    passedCases = @($cases | Where-Object { $_.Passed }).Count
    failedCases = @($cases | Where-Object { -not $_.Passed }).Count
    files = $files
}
[System.IO.File]::WriteAllText((Join-Path $run 'validation-manifest.json'), ($final | ConvertTo-Json -Depth 20), $utf8)
Write-Host "MANIFEST $(Join-Path $run 'validation-manifest.json')"
if ($failure -or @($steps | Where-Object { $_.exitCode -ne 0 -or $_.timedOut }).Count -ne 0 -or $cases.Count -ne 102 -or $final.failedCases -ne 0) { exit 1 }
exit 0
