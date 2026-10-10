param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$PackageRoot, [int]$DeadlineSeconds = 120)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$watch = [Diagnostics.Stopwatch]::StartNew()
$dependencies = [Collections.Generic.List[object]]::new()
$count = 0
function Check-Budget {
    $script:count++
    if ($script:count -gt 20000 -or $watch.Elapsed.TotalSeconds -gt $DeadlineSeconds -or (Test-Path (Join-Path $PackageRoot 'cancel'))) { throw 'License item/time budget exceeded.' }
}
$assets = Get-Content (Join-Path $RepoRoot 'src/SonnetDB/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$folder = @($assets.packageFolders.Keys)[0]
foreach ($key in @($assets.libraries.Keys | Sort-Object)) {
    Check-Budget
    $library = $assets.libraries[$key]
    if ($library.type -ne 'package') { continue }
    $parts = $key.Split('/')
    $nupkg = Join-Path $folder "$($library.path)/$($parts[0].ToLowerInvariant()).$($parts[1]).nupkg"
    if (-not (Test-Path -LiteralPath $nupkg)) { throw "Resolved NuGet archive unavailable: $key" }
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
    $licenseFiles = [Collections.Generic.List[string]]::new()
    $declaration = $null
    try {
        if ($zip.Entries.Count -gt 5000) { throw 'NuGet entry budget exceeded.' }
        foreach ($entry in $zip.Entries) {
            Check-Budget
            if ($entry.FullName -match '^[^/]+\.nuspec$') {
                $reader = [IO.StreamReader]::new($entry.Open())
                try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $declaration = $nuspec.SelectSingleNode('//*[local-name()="license"]')
                if ($null -ne $declaration) { $declaration = @{ type=$declaration.GetAttribute('type'); value=$declaration.InnerText } }
            }
            if ($entry.Length -gt 0 -and [IO.Path]::GetFileName($entry.FullName) -match '^(?:LICENSE|LICENCE|NOTICE|THIRD[-_. ]?PARTY|COPYING)' ) {
                if ($entry.Length -gt 4MB) { throw 'License text budget exceeded.' }
                $name = $entry.FullName.Replace('/', '_').Replace('\', '_')
                $relative = "licenses/nuget/$($key.Replace('/','-'))/$name"
                $target = Join-Path $PackageRoot $relative
                $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
                $licenseFiles.Add($relative)
            }
        }
    } finally { $zip.Dispose() }
    $dependencies.Add(@{ ecosystem='nuget'; id=$parts[0]; version=$parts[1]; resolvedSha512=$library.sha512; archiveSha256=(Get-FileHash $nupkg).Hash.ToLowerInvariant(); license=$declaration; includedLicenseFiles=$licenseFiles.ToArray(); licenseEvidence=if($null -eq $declaration -and $licenseFiles.Count -eq 0){'MISSING_REQUIRES_P07_REVIEW'}else{'PACKAGE_METADATA_AND_AVAILABLE_ORIGINAL_TEXT'} })
}
$lock = Get-Content (Join-Path $RepoRoot 'web/package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($key in @($lock.packages.Keys | Sort-Object)) {
    Check-Budget
    if ($key -eq '') { continue }
    if (-not $key.StartsWith('node_modules/') -or $key.Contains('..')) { throw 'Invalid npm lock path.' }
    $item = $lock.packages[$key]
    $directory = Join-Path $RepoRoot "web/$key"
    if (-not (Test-Path -LiteralPath $directory)) {
        if ($item['optional'] -ne $true) { throw "Required locked npm dependency unavailable: $key" }
        $dependencies.Add(@{ecosystem='npm';id=$key;version=$item['version'];integrity=$item['integrity'];redistributed=$false;licenseEvidence='OPTIONAL_OTHER_PLATFORM_NOT_INSTALLED'})
        continue
    }
    $files = @(Get-ChildItem -LiteralPath $directory -File | Where-Object Name -Match '^(LICENSE|LICENCE|NOTICE|COPYING)')
    $licenseFiles = [Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        Check-Budget
        if ($file.Length -gt 4MB) { throw 'License text budget exceeded.' }
        $relative = "licenses/npm/$($key.Replace('/','_'))/$($file.Name)"
        $target = Join-Path $PackageRoot $relative
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $licenseFiles.Add($relative)
    }
    $metadata = Get-Content (Join-Path $directory 'package.json') -Raw | ConvertFrom-Json -AsHashtable
    $dependencies.Add(@{ecosystem='npm';id=$metadata['name'];version=$metadata['version'];integrity=$item['integrity'];resolved=$item['resolved'];buildOnly=($item['dev'] -eq $true);license=$metadata['license'];includedLicenseFiles=$licenseFiles.ToArray();licenseEvidence=if($licenseFiles.Count -eq 0){'METADATA_ONLY_REQUIRES_P07_REVIEW'}else{'ORIGINAL_TEXT'}})
}
$runtimeReport = Get-Content (Join-Path $PackageRoot 'native-dependencies.json') -Raw | ConvertFrom-Json
foreach ($runtime in $runtimeReport.redistributed) {
    Check-Budget
    $dependencies.Add(@{ecosystem='microsoft-vc-runtime';id=$runtime.path;version=$runtime.productVersion;fileVersion=$runtime.fileVersion;
        source=$runtime.source;registeredRedistVersion=$runtimeReport.registration.registeredRedistVersion;sha256=$runtime.sha256;
        license='Microsoft Visual Studio redistribution terms';includedLicenseFiles=@('VC-RUNTIME-NOTICE.md');
        licenseEvidence='PROVENANCE_AND_NOTICE_ORIGINAL_TERMS_REQUIRE_P07_REVIEW'})
}
$dependencies.ToArray() | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $PackageRoot 'dependency-licenses.json') -Encoding utf8
