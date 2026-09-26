#requires -Version 7.0
# OutputRoot 同时承载临时数据库，必须支持跨进程文件锁；Windows Docker 挂载目录请改用 Linux volume 或容器内目录，再导出证据。
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PackageSource,
    [Parameter(Mandatory)][string]$ServerPath,
    [string]$OutputRoot,
    [switch]$VerifyPublishedBaseline,
    [string]$BaselineVersion = '3.1.0',
    [string]$BaselineServerPath,
    [uri]$DownloadProxy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
$ServerPath = (Resolve-Path -LiteralPath $ServerPath).Path
if (!$OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts/release-contract' }
$runRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$evidence = [ordered]@{
    version = $Version
    sourceCommit = (& git -C $repoRoot rev-parse HEAD)
    serverSha256 = (Get-FileHash -LiteralPath $ServerPath -Algorithm SHA256).Hash
    baselineVersion = $BaselineVersion
    baselineRequested = [bool]$VerifyPublishedBaseline
    checks = $checks
    status = 'RUNNING'
}

function Invoke-CheckedDotNet([string[]]$Arguments, [string]$LogName)
{
    & dotnet @Arguments *> (Join-Path $runRoot "$LogName.log")
    if ($LASTEXITCODE -ne 0)
    {
        Get-Content -LiteralPath (Join-Path $runRoot "$LogName.log") -Tail 60 | Write-Host
        throw "$LogName failed with exit code $LASTEXITCODE."
    }
}

function Build-Consumer([string]$PackageVersion, [string]$Name, [switch]$Published)
{
    $directory = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    Copy-Item -Path (Join-Path $repoRoot 'tests/SonnetDB.ReleaseContractSmoke/*.cs') -Destination $directory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tests/SonnetDB.ReleaseContractSmoke/SonnetDB.ReleaseContractSmoke.csproj') -Destination $directory
    # 固定 SonnetDB 包来源且使用独立缓存，不能让同版本全局缓存或项目引用代替待验收包。
    $source = if ($Published) { 'https://api.nuget.org/v3/index.json' } else { $PackageSource }
    $escapedSource = [Security.SecurityElement]::Escape($source)
    $dependencySource = if ($Published) { '' } else { '<add key="dependencies" value="https://api.nuget.org/v3/index.json" />' }
    $contractPatterns = if ($Published) { '<package pattern="*" />' } else { '<package pattern="SonnetDB" /><package pattern="SonnetDB.Core" />' }
    $dependencyMapping = if ($Published) { '' } else { '<packageSource key="dependencies"><package pattern="*" /></packageSource>' }
    $config = @"
<configuration>
  <packageSources>
    <clear />
    <add key="contract" value="$escapedSource" />
    $dependencySource
  </packageSources>
  <packageSourceMapping>
    <packageSource key="contract">$contractPatterns</packageSource>
    $dependencyMapping
  </packageSourceMapping>
</configuration>
"@
    $configPath = Join-Path $directory 'NuGet.config'
    [IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))
    $project = Join-Path $directory 'SonnetDB.ReleaseContractSmoke.csproj'
    $packageCache = Join-Path $runRoot "$Name-packages"
    Invoke-CheckedDotNet @('restore', $project, "-p:SmokePackageVersion=$PackageVersion", '--configfile', $configPath,
        '--packages', $packageCache) "$Name-restore"
    $assets = Get-Content -LiteralPath (Join-Path $directory 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($id in @('SonnetDB', 'SonnetDB.Core'))
    {
        if (!$assets.libraries.ContainsKey("$id/$PackageVersion")) { throw "Consumer did not restore $id/$PackageVersion." }
        if ($assets.libraries["$id/$PackageVersion"].type -ne 'package') { throw "$id is not a package reference." }
        if (!$Published)
        {
            $package = Join-Path $PackageSource "$id.$PackageVersion.nupkg"
            $installed = Join-Path $packageCache "$($id.ToLowerInvariant())/$PackageVersion/$($id.ToLowerInvariant()).$PackageVersion.nupkg"
            if ((Get-FileHash -LiteralPath $package).Hash -ne (Get-FileHash -LiteralPath $installed).Hash)
            { throw "Installed $id does not match the candidate package." }
            $evidence["$id-packageSha256"] = (Get-FileHash -LiteralPath $package).Hash
        }
    }
    Invoke-CheckedDotNet @('build', $project, '-c', 'Release', '--no-restore', "-p:SmokePackageVersion=$PackageVersion",
        '-o', (Join-Path $directory 'bin')) "$Name-build"
    return Join-Path $directory 'bin/SonnetDB.ReleaseContractSmoke.dll'
}

function Start-ContractServer([string]$Executable, [string]$Name)
{
    $directory = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $token = 'contract-' + [Guid]::NewGuid().ToString('N')
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # 显式配置两条 loopback 监听，隔离数据和凭据，不读取 bundle 中的安装默认配置。
    foreach ($argument in @(
        '--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0', '--Kestrel:Endpoints:Http:Protocols=Http1',
        '--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0', '--Kestrel:Endpoints:FrameH2:Protocols=Http2',
        "--SonnetDBServer:DataRoot=$(Join-Path $directory 'data')", '--SonnetDBServer:AllowAnonymousProbes=true',
        "--SonnetDBServer:Tokens:${token}=admin", '--SonnetDBServer:Mqtt:Enabled=false',
        '--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information'))
    { $info.ArgumentList.Add($argument) }
    # 防止运行环境中的 SONNETDB_ 设置覆盖测试隔离参数。
    foreach ($key in @($info.Environment.Keys))
    { if ($key.StartsWith('SONNETDB_', [StringComparison]::OrdinalIgnoreCase)) { $info.Environment.Remove($key) | Out-Null } }
    $info.Environment['NO_PROXY'] = 'localhost,127.0.0.1,::1'
    $process = [Diagnostics.Process]::Start($info)
    $processes.Add($process)
    $stderr = $process.StandardError.ReadToEndAsync()
    $addresses = [Collections.Generic.List[string]]::new()
    $startupLog = [Collections.Generic.List[string]]::new()
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $lineTask = $process.StandardOutput.ReadLineAsync()
    while ($addresses.Count -lt 2)
    {
        if ($process.HasExited) { throw "$Name exited during startup: $($stderr.GetAwaiter().GetResult())" }
        if ([DateTime]::UtcNow -gt $deadline) { throw "$Name did not expose both listeners within 60 seconds." }
        if (!$lineTask.Wait(200)) { continue }
        $line = $lineTask.GetAwaiter().GetResult()
        if ($null -eq $line) { throw "$Name closed stdout during startup." }
        $startupLog.Add($line)
        if ($line -match 'Now listening on: (http://127\.0\.0\.1:\d+)') { $addresses.Add($Matches[1]) }
        if ($addresses.Count -lt 2) { $lineTask = $process.StandardOutput.ReadLineAsync() }
    }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $http = $null
    $http2 = $null
    foreach ($address in $addresses)
    {
        try
        {
            $probe = Invoke-WebRequest -Uri "$address/healthz" -NoProxy -TimeoutSec 5
            if ($probe.StatusCode -eq 200) { $http = $address }
        }
        catch { $http2 = $address }
    }
    if (!$http -or !$http2) { throw "$Name listeners could not be identified." }
    Invoke-RestMethod -Method Post -Uri "$http/v1/db" -NoProxy -TimeoutSec 30 `
        -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body '{"name":"returning_contract"}' | Out-Null
    return @{ Process = $process; Http = $http; Http2 = $http2; Token = $token; Stderr = $stderr;
        Stdout = $stdout; StartupLog = $startupLog; Directory = $directory }
}

function Invoke-Contract([string]$Consumer, [string]$Scenario, [string]$Connection, [string]$Name, [string]$Scope)
{
    Invoke-CheckedDotNet @($Consumer, $Scenario, $Connection) $Name
    $checks.Add([ordered]@{ name = $Name; scope = $Scope; status = 'PASS' })
    Write-Host "PASS $Name ($Scope)"
}

function Get-PublishedServer
{
    if ($BaselineServerPath)
    {
        $evidence['baselineOrigin'] = 'supplied executable; caller must retain published archive and checksum evidence'
        return (Resolve-Path -LiteralPath $BaselineServerPath).Path
    }
    $rid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } else { throw 'Published baseline requires Windows or Linux x64.' }
    $extension = if ($IsWindows) { 'zip' } else { 'tar.gz' }
    $asset = "sonnetdb-full-$BaselineVersion-$rid.$extension"
    $url = "https://github.com/IoTSharp/SonnetDB/releases/download/v$BaselineVersion/$asset"
    $archive = Join-Path $runRoot $asset
    $downloadOptions = @{}
    if ($DownloadProxy) { $downloadOptions.Proxy = $DownloadProxy }
    Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 180 @downloadOptions
    $checksum = (Invoke-WebRequest -Uri "$url.sha256" -TimeoutSec 60 @downloadOptions).Content
    if ($checksum -is [byte[]]) { $checksum = [Text.Encoding]::UTF8.GetString($checksum) }
    $expected = [regex]::Match($checksum, '(?i)\b[a-f0-9]{64}\b').Value
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if (!$expected -or $expected -ne $actual) { throw 'Published baseline SHA256 mismatch.' }
    $evidence['baselineOrigin'] = $url
    $evidence['baselineArchiveSha256'] = $actual
    $destination = Join-Path $runRoot 'baseline-bundle'
    New-Item -ItemType Directory -Path $destination | Out-Null
    if ($IsWindows) { Expand-Archive -LiteralPath $archive -DestinationPath $destination }
    else
    {
        & tar -xzf $archive -C $destination
        if ($LASTEXITCODE -ne 0) { throw 'Published baseline extraction failed.' }
    }
    $name = if ($IsWindows) { 'SonnetDB.exe' } else { 'SonnetDB' }
    $executables = @(Get-ChildItem -LiteralPath $destination -Recurse -File -Filter $name)
    if ($executables.Count -ne 1) { throw 'Expected exactly one published Server executable.' }
    return $executables[0].FullName
}

$servers = [Collections.Generic.List[hashtable]]::new()
try
{
    $consumer = Build-Consumer $Version 'candidate-consumer'
    Invoke-Contract $consumer 'insert-returning' "Data Source=$(Join-Path $runRoot 'embedded')" 'candidate-embedded' 'full'
    $server = Start-ContractServer $ServerPath 'candidate-server'
    $servers.Add($server)
    foreach ($protocol in @('rest', 'auto', 'frame-http2'))
    {
        $address = if ($protocol -eq 'frame-http2') { $server.Http2 } else { $server.Http }
        Invoke-Contract $consumer 'insert-returning' "Data Source=sonnetdb+$address/returning_contract;Token=$($server.Token);Protocol=$protocol" "candidate-$protocol" 'full'
    }
    if ($VerifyPublishedBaseline)
    {
        $baselinePath = Get-PublishedServer
        $evidence['baselineServerSha256'] = (Get-FileHash -LiteralPath $baselinePath).Hash
        $baseline = Start-ContractServer $baselinePath 'baseline-server'
        $servers.Add($baseline)
        $legacyConsumer = Build-Consumer $BaselineVersion 'baseline-consumer' -Published
        foreach ($protocol in @('rest', 'auto', 'frame-http2'))
        {
            $oldAddress = if ($protocol -eq 'frame-http2') { $baseline.Http2 } else { $baseline.Http }
            $newAddress = if ($protocol -eq 'frame-http2') { $server.Http2 } else { $server.Http }
            Invoke-Contract $consumer 'insert-legacy' "Data Source=sonnetdb+$oldAddress/returning_contract;Token=$($baseline.Token);Protocol=$protocol" "new-client-old-server-$protocol" 'legacy-values-only'
            Invoke-Contract $legacyConsumer 'insert-legacy' "Data Source=sonnetdb+$newAddress/returning_contract;Token=$($server.Token);Protocol=$protocol" "old-client-new-server-$protocol" 'legacy-values-only'
        }
    }
    $evidence.status = 'PASS'
}
catch
{
    $evidence.status = 'FAIL'
    $evidence['failure'] = $_.Exception.Message
    throw
}
finally
{
    foreach ($process in $processes)
    {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    }
    foreach ($server in $servers)
    {
        $remaining = $server.Stdout.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $server.Directory 'stdout.log'), ($server.StartupLog -join "`n") + "`n$remaining")
        [IO.File]::WriteAllText((Join-Path $server.Directory 'stderr.log'), $server.Stderr.GetAwaiter().GetResult())
    }
    foreach ($process in $processes) { $process.Dispose() }
    $evidence['finishedUtc'] = [DateTime]::UtcNow.ToString('O')
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Host "Evidence: $runRoot"
}
