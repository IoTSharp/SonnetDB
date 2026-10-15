param([string]$OutputDirectory, [switch]$UseLocalProxy)
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/issue-272-public-20261016-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$runRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $runRoot) { throw 'Evidence directory must be new; previous evidence is never replaced.' }
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
[IO.File]::WriteAllText((Join-Path $runRoot '.owner'), [Guid]::NewGuid().ToString('N'), $utf8)
$commands = [Collections.Generic.List[object]]::new()
$packageEvidence = @()
$receiptPath = Join-Path $runRoot 'receipt.json'
$failure = $null

function Invoke-Captured([string]$Label, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 180) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.Environment['NUGET_PACKAGES'] = Join-Path $runRoot 'packages'
    $info.Environment['NUGET_HTTP_CACHE_PATH'] = Join-Path $runRoot 'http-cache'
    $info.Environment['NUGET_PLUGINS_CACHE_PATH'] = Join-Path $runRoot 'plugins-cache'
    $info.Environment['NUGET_FALLBACK_PACKAGES'] = ''
    $info.Environment['DOTNET_CLI_HOME'] = Join-Path $runRoot 'dotnet-home'
    $info.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $info.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    $info.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    if ($UseLocalProxy) {
        $info.Environment['HTTP_PROXY'] = 'http://127.0.0.1:7890'
        $info.Environment['HTTPS_PROXY'] = 'http://127.0.0.1:7890'
        $info.Environment['NO_PROXY'] = 'localhost,127.0.0.1,::1'
    }
    $started = [DateTime]::UtcNow
    $process = [Diagnostics.Process]::Start($info)
    $pidValue = $process.Id
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
    $exitCode = $process.ExitCode
    [IO.File]::WriteAllText((Join-Path $runRoot ($Label + '.stdout.log')), $stdout.GetAwaiter().GetResult(), $utf8)
    [IO.File]::WriteAllText((Join-Path $runRoot ($Label + '.stderr.log')), $stderr.GetAwaiter().GetResult(), $utf8)
    $commands.Add([ordered]@{ label = $Label; executable = $Executable; arguments = $Arguments; workingDirectory = $WorkingDirectory; pid = $pidValue; startedUtc = $started.ToString('o'); endedUtc = [DateTime]::UtcNow.ToString('o'); exitCode = $exitCode; timedOut = $timedOut })
    $process.Dispose()
    Write-Host ($Label + ': exit=' + $exitCode)
    if ($exitCode -ne 0 -or $timedOut) { throw "Command failed: $Label; evidence retained in $runRoot" }
}

try {
    Invoke-Captured 'issue-current' 'gh' @('issue', 'view', '272', '--repo', 'IoTSharp/SonnetDB', '--json', 'number,title,state,body,comments,updatedAt,url') $repository 45
    Invoke-Captured 'sdk' 'dotnet' @('--info') $repository 45
    $issue = Get-Content -LiteralPath (Join-Path $runRoot 'issue-current.stdout.log') -Raw -Encoding utf8 | ConvertFrom-Json
    $originalSource = [regex]::Match($issue.body, '(?s)```csharp\r?\n(.*?)\r?\n```').Groups[1].Value
    if (!$originalSource.Contains('FK_A06_Book_Author')) { throw 'Original issue source was not found.' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'original-issue-Program.cs'), $originalSource + "`n", $utf8)
    foreach ($consumer in @('original', 'lifecycle')) {
        $consumerRoot = Join-Path $runRoot $consumer
        [IO.Directory]::CreateDirectory($consumerRoot) | Out-Null
        foreach ($name in @('Consumer.csproj', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $consumerRoot $name)
        }
        if ($consumer -eq 'original') {
            Copy-Item -LiteralPath (Join-Path $runRoot 'original-issue-Program.cs') -Destination (Join-Path $consumerRoot 'Program.cs')
        } else {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs') -Destination (Join-Path $consumerRoot 'Program.cs')
        }
        Invoke-Captured ($consumer + '-restore') 'dotnet' @('restore', 'Consumer.csproj', '--configfile', (Join-Path $consumerRoot 'NuGet.Config'), '--packages', (Join-Path $runRoot 'packages'), '--no-http-cache', '-p:RestoreFallbackFolders=', '-p:RestoreAdditionalProjectFallbackFolders=') $consumerRoot
        $assets = Get-Content -LiteralPath (Join-Path $consumerRoot 'obj/project.assets.json') -Raw -Encoding utf8 | ConvertFrom-Json
        if (@($assets.libraries.PSObject.Properties.Value | Where-Object type -eq 'project').Count -ne 0) { throw 'ProjectReference found in independent consumer.' }
        $folders = @($assets.packageFolders.PSObject.Properties.Name)
        if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne (Join-Path $runRoot 'packages')) { throw 'Unexpected package/fallback folder.' }
        $sources = @($assets.project.restore.sources.PSObject.Properties.Name)
        if ($sources.Count -ne 1 -or $sources[0] -ne 'https://api.nuget.org/v3/index.json') { throw 'Unexpected package source.' }
        Invoke-Captured ($consumer + '-build') 'dotnet' @('build', 'Consumer.csproj', '-c', 'Release', '--no-restore', '-nr:false') $consumerRoot
    }
    $expected = @{
        'sonnetdb.core' = '10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268'
        'sonnetdb' = '0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4'
        'sonnetdb.entityframeworkcore' = '9688BA682E160639681AF3E4882DE6A58F70CF289B42552987FA7CD85D87E125'
    }
    foreach ($id in $expected.Keys | Sort-Object) {
        $packageRoot = Join-Path $runRoot ('packages/' + $id + '/4.5.0-preview.ef266.5')
        $nupkg = Join-Path $packageRoot ($id + '.4.5.0-preview.ef266.5.nupkg')
        $hash = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash
        if ($hash -ne $expected[$id]) { throw "Unexpected public package hash: $id" }
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $packageRoot ($id + '.nuspec')) -Raw -Encoding utf8
        $sourceCommit = $nuspec.package.metadata.repository.commit
        if ($sourceCommit -ne '1af23d8f299febfdf66b040ab3419980396acf46') { throw "Unexpected package source commit: $id" }
        $metadata = Get-Content -LiteralPath (Join-Path $packageRoot '.nupkg.metadata') -Raw -Encoding utf8 | ConvertFrom-Json
        if ($metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw 'Package did not come from public NuGet source.' }
        $packageEvidence += [ordered]@{ id = $id; version = '4.5.0-preview.ef266.5'; source = $metadata.source; repositoryCommit = $sourceCommit; sha256 = $hash }
        foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $packageRoot 'lib/net10.0') -Filter '*.dll') {
            foreach ($consumer in @('original', 'lifecycle')) {
                $deployed = Join-Path $runRoot ($consumer + '/bin/Release/net10.0/' + $dll.Name)
                if ((Get-FileHash -LiteralPath $deployed -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash) { throw 'Runtime DLL differs from public package DLL.' }
            }
        }
    }
    foreach ($provider in @('Sqlite', 'SonnetDB')) {
        Invoke-Captured ('original-' + $provider) 'dotnet' @((Join-Path $runRoot 'original/bin/Release/net10.0/Consumer.dll'), $provider) (Join-Path $runRoot 'original') 60
        $databaseRoot = Join-Path $runRoot ('databases/' + $provider)
        foreach ($phase in @('initial', 'removed', 'replacement', 'rollback', 'reopen')) {
            Invoke-Captured ('lifecycle-' + $provider + '-' + $phase) 'dotnet' @((Join-Path $runRoot 'lifecycle/bin/Release/net10.0/Consumer.dll'), $provider, $phase, $databaseRoot) (Join-Path $runRoot 'lifecycle') 90
        }
    }
} catch {
    $failure = $_.ToString()
    Write-Host $failure
} finally {
    $files = @(Get-ChildItem -LiteralPath $runRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](packages|http-cache|plugins-cache|dotnet-home|databases)[\\/]' } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($runRoot, $_.FullName); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $receipt = [ordered]@{ issue = 272; utc = [DateTime]::UtcNow.ToString('o'); runRoot = $runRoot; fixedVersion = '4.5.0-preview.ef266.5'; sourceCommit = '1af23d8f299febfdf66b040ab3419980396acf46'; scope = 'public NuGet embedded consumer and SQLite reference; independent process for each lifecycle phase'; packageCache = (Join-Path $runRoot 'packages'); httpCache = (Join-Path $runRoot 'http-cache'); projectReferences = 0; globalCacheChanged = $false; localProxy = [bool]$UseLocalProxy; failure = $failure; commands = $commands.ToArray(); packages = $packageEvidence; files = $files }
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 10), $utf8)
    Write-Host ('Evidence: ' + $receiptPath)
}
if ($failure) { exit 1 }
