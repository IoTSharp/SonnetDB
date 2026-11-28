#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')][string]$Version,
    [Parameter(Mandatory)][string]$PackageSource,
    [Parameter(Mandatory)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Consumer output must be a fresh directory.' }
$null = New-Item -ItemType Directory -Path $OutputRoot
# 仓库外构建，独立缓存且固定内部包来源，避免 ProjectReference、中央版本或同版本缓存掩盖缺包。
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonnetdb-package-consumers-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixtureRoot
$checks = [Collections.Generic.List[object]]::new()
$report = [ordered]@{ version = $Version; status = 'RUNNING'; fixtureRoot = $fixtureRoot; checks = $checks }
$utf8 = [Text.UTF8Encoding]::new($false)
try {
    $consumerNames = @('core', 'client', 'testcontainers')
    if ([version]($Version.Split('-')[0]) -ge [version]'5.2.0') { $consumerNames += 'quartz' }
    foreach ($name in $consumerNames) {
        $directory = Join-Path $fixtureRoot $name
        $null = New-Item -ItemType Directory -Path $directory
        foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
            [IO.File]::WriteAllText((Join-Path $directory $file), '<Project />', $utf8)
        }
        $id = switch ($name) {
            'core' { 'SonnetDB.Core' }
            'client' { 'SonnetDB.Client' }
            'testcontainers' { 'SonnetDB.Testcontainers' }
            'quartz' { 'SonnetDB.Quartz' }
        }
        $aotCompatible = if ($name -in @('testcontainers', 'quartz')) { 'false' } else { 'true' }
        [IO.File]::WriteAllText((Join-Path $directory 'consumer.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors><IsAotCompatible>$aotCompatible</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="$id" Version="[$Version]" /></ItemGroup>
</Project>
"@, $utf8)
        $escapedSource = [Security.SecurityElement]::Escape($PackageSource)
        [IO.File]::WriteAllText((Join-Path $directory 'NuGet.config'), @"
<configuration>
  <packageSources><clear /><add key="candidate" value="$escapedSource" /><add key="dependencies" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="SonnetDB" /><package pattern="SonnetDB.*" /></packageSource>
    <packageSource key="dependencies"><package pattern="*" /></packageSource>
  </packageSourceMapping>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
</configuration>
"@, $utf8)
        $source = if ($name -eq 'testcontainers') { @'
using Testcontainers.SonnetDB;

var builder = new SonnetDbBuilder("iotsharp/sonnetdb:5.0.0").WithDatabase("package_consumer");
if (builder.GetType().Assembly.GetName().Name != "Testcontainers.SonnetDB")
    throw new InvalidOperationException("The renamed package changed its assembly identity.");
Console.WriteLine("PASS SonnetDB.Testcontainers package with the existing Testcontainers.SonnetDB namespace and builder.");
'@ } elseif ($name -eq 'quartz') { @'
using Quartz;
using SonnetDB.Quartz;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var connectionString = $"Data Source={args[1]};Timeout=5";
var jobKey = new JobKey("package-job");
var triggerKey = new TriggerKey("package-trigger");
await SonnetDbQuartzSchema.EnsureCreatedAsync(connectionString, timeout.Token);
var scheduler = await BuildScheduler();
try
{
    await scheduler.ScheduleJob(JobBuilder.Create<ProbeJob>().WithIdentity(jobKey).UsingJobData("marker", "package").Build(),
        TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(jobKey).StartAt(DateTimeOffset.UtcNow.AddHours(1)).Build(),
        cancellationToken: timeout.Token);
    await scheduler.PauseTrigger(triggerKey, timeout.Token);
}
finally
{
    await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken: CancellationToken.None);
}

scheduler = await BuildScheduler();
try
{
    var job = await scheduler.GetJobDetail(jobKey, timeout.Token);
    if (job?.JobDataMap.GetString("marker") != "package"
        || await scheduler.GetTriggerState(triggerKey, timeout.Token) != TriggerState.Paused
        || (await scheduler.GetTriggersOfJob(jobKey, timeout.Token)).Count != 1)
        throw new InvalidOperationException("Quartz persisted job or trigger state did not survive reopening.");
}
finally
{
    await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken: CancellationToken.None);
}
Console.WriteLine("PASS packaged Quartz JobStoreTX persisted and reopened a paused trigger and job data.");

async Task<IScheduler> BuildScheduler()
{
    var builder = QuartzSchedulerBuilder.Create(quartz =>
    {
        quartz.ConfigureScheduler(options => options.InstanceName = "package-consumer");
        quartz.UsePersistentStore(store => store.UseSonnetDB(connectionString));
    });
    return await builder.BuildScheduler(timeout.Token);
}

public sealed class ProbeJob : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
'@ } elseif ($name -eq 'client') { @'
using System.Buffers;
using SonnetDB.Client;
using SonnetDB.Protocol;

using var client = new SndbFrameClient(new Uri("http://127.0.0.1:1/"));
var buffer = new ArrayBufferWriter<byte>();
FrameCodec.WriteErrorFrame(buffer, (byte)FrameService.Sql, (byte)SqlFrameOp.Execute, 42, "probe", "package");
if (!FrameHeader.TryRead(buffer.WrittenSpan, out var header) || header.StreamId != 42 || !header.IsError)
    throw new InvalidOperationException("Protocol header round-trip failed.");
if (FrameCodec.ReadErrorPayload(buffer.WrittenSpan[FrameHeader.Size..]) != ("probe", "package"))
    throw new InvalidOperationException("Protocol payload round-trip failed.");
if (typeof(FrameCodec).Assembly.GetName().Name != "SonnetDB.Protocol")
    throw new InvalidOperationException("Protocol assembly identity differs.");
Console.WriteLine("PASS packaged Client and Protocol round-trip; remote network operations are checked separately.");
'@ } else { @'
using SonnetDB.Engine;
using SonnetDB.Protocol;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

var options = new TsdbOptions { RootDirectory = args[1] };
if (args[0] == "create")
{
    using var db = Tsdb.Open(options);
    SqlExecutor.Execute(db, "CREATE TABLE package_audits (id INT, request_key STRING NULL, status STRING, PRIMARY KEY (id))");
    SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_package_key ON package_audits (request_key)");
    SqlExecutor.Execute(db, "INSERT INTO package_audits (id, request_key, status) VALUES (1, 'key-1', 'ready'), (2, NULL, 'ready')");
}
using (var db = Tsdb.Open(options))
{
    const string sql = "SELECT EXISTS (SELECT 1 FROM package_audits WHERE request_key = @key AND status = 'ready')";
    foreach (var (key, expected) in new (string?, bool)[] { ("key-1", true), ("missing", false), (null, false) })
    {
        var result = SqlExecutor.Execute(db, databaseName: null, sql, new SqlParameters().AddNamed("key", key))
            as SelectExecutionResult ?? throw new InvalidOperationException("Expected SELECT result.");
        if (!Equals(result.Rows[0][0], expected)) throw new InvalidOperationException("EXISTS result differs.");
    }
    var nullResult = SqlExecutor.Execute(db, "SELECT EXISTS (SELECT 1 FROM package_audits WHERE request_key IS NULL)")
        as SelectExecutionResult ?? throw new InvalidOperationException("Expected SELECT result.");
    if (!Equals(nullResult.Rows[0][0], true)) throw new InvalidOperationException("NULL row was lost.");
}
if (typeof(FrameCodec).Assembly.GetName().Name != "SonnetDB.Protocol")
    throw new InvalidOperationException("Core did not use packaged Protocol.");
Console.WriteLine("PASS packaged Core create/reopen and nullable unique EXISTS.");
'@ }
        [IO.File]::WriteAllText((Join-Path $directory 'Program.cs'), $source, $utf8)
        $project = Join-Path $directory 'consumer.csproj'
        # 独占缓存跟随较短的仓库外夹具，避免 Windows 深层输出目录导致依赖 DLL 路径超长。
        $cache = Join-Path $directory 'packages'
        & dotnet restore $project --configfile (Join-Path $directory 'NuGet.config') --packages $cache *> (Join-Path $OutputRoot "$name-restore.log")
        if ($LASTEXITCODE -ne 0) { throw "$name consumer restore failed; see $OutputRoot." }
        $assets = Get-Content -LiteralPath (Join-Path $directory 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        $requiredPackages = switch ($name) {
            'testcontainers' { @($id) }
            'quartz' { @($id, 'SonnetDB', 'SonnetDB.Core', 'SonnetDB.Protocol') }
            default { @($id, 'SonnetDB.Protocol') }
        }
        foreach ($packageId in $requiredPackages) {
            if (!$assets.libraries.ContainsKey("$packageId/$Version") -or $assets.libraries["$packageId/$Version"].type -cne 'package') {
                throw "Missing installed candidate $packageId/$Version."
            }
            $package = Join-Path $PackageSource "$packageId.$Version.nupkg"
            $installed = Join-Path $cache "$($packageId.ToLowerInvariant())/$Version/$($packageId.ToLowerInvariant()).$Version.nupkg"
            $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
            if ($hash -cne (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash) { throw 'Installed candidate bytes differ.' }
            $checks.Add([ordered]@{ consumer = $name; package = $packageId; sha256 = $hash; cache = $cache })
        }
        if ($name -eq 'client' -and @($assets.libraries.Keys | Where-Object { $_ -match '^SonnetDB(\.Core|\.Data)?/' }).Count -ne 0) {
            throw 'Lightweight Client unexpectedly depends on the engine or ADO.NET.'
        }
        & dotnet build $project -c Release --no-restore -o (Join-Path $directory 'bin') *> (Join-Path $OutputRoot "$name-build.log")
        if ($LASTEXITCODE -ne 0) { throw "$name consumer build failed; see $OutputRoot." }
        foreach ($mode in $(if ($name -eq 'core') { @('create', 'reopen') } elseif ($name -eq 'testcontainers') { @('builder') } else { @('roundtrip') })) {
            $databaseDir = if ($name -eq 'quartz') { 'quartz-database' } else { 'database' }
            & dotnet (Join-Path $directory 'bin/consumer.dll') $mode (Join-Path $OutputRoot $databaseDir) *> (Join-Path $OutputRoot "$name-$mode.log")
            if ($LASTEXITCODE -ne 0) { throw "$name consumer $mode failed; see $OutputRoot." }
            $checks.Add([ordered]@{ consumer = $name; mode = $mode; status = 'PASS' })
        }
    }
    $report.status = 'PASS'
    Write-Host "Exact package consumer verification passed: $($consumerNames -join ', ')."
}
catch {
    $report.status = 'FAIL'
    throw
}
finally {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'result.json') -Encoding utf8
    # 保留仓库外源码、缓存和日志供复核，不删除消费者或数据库证据。
}
