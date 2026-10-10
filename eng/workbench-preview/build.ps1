[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^4\.5\.0-preview\.1\.[1-9][0-9]*$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) { throw 'Windows PowerShell 7+ required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputRoot)
$node = (Get-Command node -CommandType Application | Select-Object -First 1).Source
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$npmCommand = (Get-Command npm -CommandType ExternalScript,Application | Select-Object -First 1).Source
$npmCandidates = @([IO.Path]::GetDirectoryName($npmCommand), [IO.Path]::GetDirectoryName($node)) |
    ForEach-Object { Join-Path $_ 'node_modules/npm/bin/npm-cli.js' } | Select-Object -Unique
$npmCli = @($npmCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
if ($npmCli.Count -ne 1) { throw "Registered npm CLI unavailable; checked: $($npmCandidates -join ', ')" }
$npmCli = $npmCli[0]
& $node (Join-Path $PSScriptRoot 'package.mjs') prepare $root $output $SourceCommit $Version
if ($LASTEXITCODE -ne 0) { throw 'Clean CI source preflight failed.' }
$evidence = Join-Path $output 'evidence'
$watch = [Diagnostics.Stopwatch]::StartNew()
$count = 0
function Invoke-OwnedCommand([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [int]$Seconds) {
    if ($script:count -ge 12 -or $watch.Elapsed.TotalSeconds -gt 2400) { throw 'Build command/wall-clock budget exhausted.' }
    $script:count++
    $spec = Join-Path $evidence "$Name.spec.json"
    $environment = @{
        MSBUILDDISABLENODEREUSE = '1'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_PROCESSOR_COUNT = '4'
        VITE_WORKBENCH_PROFILE = 'preview-1'; VITE_WORKBENCH_VERSION = $Version
        VITE_WORKBENCH_SOURCE_COMMIT = $SourceCommit; SONNETDB_WEB_BASE_PATH = '/'
    }
    @{ name=$Name; exe=$Executable; arguments=$Arguments; cwd=$WorkingDirectory; timeoutSeconds=$Seconds; environment=$environment } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $spec -Encoding utf8
    & (Join-Path $PSScriptRoot 'Invoke-Owned.ps1') -Spec $spec -EvidenceRoot $evidence
    if ($LASTEXITCODE -ne 0) { throw "Owned command failed: $Name" }
}
Invoke-OwnedCommand 'micro' $node @('-e', 'process.stdout.write("preview-job-micro\n")') $root 15
if ((Get-Content (Join-Path $evidence 'micro.stdout.log') -Raw).Trim() -ne 'preview-job-micro') { throw 'Micro output missing.' }
Invoke-OwnedCommand 'node-contracts' $node @('--test', '--test-concurrency=1', 'eng/workbench-preview/package.test.mjs') $root 120
Invoke-OwnedCommand 'dotnet-info' $dotnet @('--info') $root 30
Invoke-OwnedCommand 'node-version' $node @('--version') $root 15
Invoke-OwnedCommand 'npm-version' $node @($npmCli, '--version') $root 15
Invoke-OwnedCommand 'npm-ci' $node @($npmCli, 'ci', '--no-audit', '--no-fund') (Join-Path $root 'web') 600
Invoke-OwnedCommand 'web-build' $node @($npmCli, 'run', 'build', '--', '--mode', 'e2e') (Join-Path $root 'web') 600
$publishArguments = @('publish', 'src/SonnetDB/SonnetDB.csproj', '-c', 'Release', '-r', 'win-x64',
    '-p:SonnetDbPublishAot=true', '-p:BuildAdminUi=false', "-p:Version=$Version", "-p:InformationalVersion=$Version+$SourceCommit",
    '-p:IncludeSourceRevisionInInformationalVersion=false', "-p:SourceRevisionId=$SourceCommit",
    '-p:AssemblyVersion=4.5.0.0', ('-p:FileVersion=4.5.0.' + $Version.Split('.')[-1]), '-o', (Join-Path $output 'publish'), '/warnaserror', '-m:4')
Invoke-OwnedCommand 'native-publish' $dotnet $publishArguments $root 1200
$publish = Join-Path $output 'publish'
# Only this checkout's freshly built Web goes into this publish directory.
$webFiles = Get-ChildItem -LiteralPath (Join-Path $root 'web/dist') -File -Recurse -Depth 8
if ($webFiles.Count -gt 5000) { throw 'Web file budget exceeded.' }
foreach ($file in $webFiles) {
    if ($watch.Elapsed.TotalSeconds -gt 2400 -or (Test-Path (Join-Path $evidence 'cancel'))) { throw 'Build cancelled/time budget.' }
    $relative = [IO.Path]::GetRelativePath((Join-Path $root 'web/dist'), $file.FullName)
    $target = Join-Path $publish "wwwroot/$relative"
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
Copy-Item -LiteralPath (Join-Path $output 'identity.json') -Destination (Join-Path $publish 'wwwroot/workbench-preview.json')
# Include generated identity in the exact fresh Web comparison as well.
Copy-Item -LiteralPath (Join-Path $output 'identity.json') -Destination (Join-Path $root 'web/dist/workbench-preview.json')
& (Join-Path $root 'eng/set-release-network-defaults.ps1') -AppSettingsPath (Join-Path $publish 'appsettings.json')
$settings = Get-Content (Join-Path $publish 'appsettings.json') -Raw | ConvertFrom-Json
$settings.SonnetDBServer.DataRoot = '../data'
$settings | ConvertTo-Json -Depth 32 | Set-Content (Join-Path $publish 'appsettings.json') -Encoding utf8
& $node (Join-Path $PSScriptRoot 'package.mjs') stage $root $output
if ($LASTEXITCODE -ne 0) { throw 'Publish inventory validation failed.' }
$package = Join-Path $output 'package'
Copy-Item -LiteralPath (Join-Path $root 'web/package.json') -Destination (Join-Path $output 'web-package.json')
& (Join-Path $PSScriptRoot 'licenses.ps1') -RepoRoot $root -PackageRoot $package -DeadlineSeconds 120
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'SonnetDB.exe'))
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Registered MSVC installation discovery unavailable.' }
Invoke-OwnedCommand 'msvc-registration' $vswhere @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath') $root 15
$vsRoot = (Get-Content (Join-Path $evidence 'msvc-registration.stdout.log') -Raw).Trim()
$vcVersion = (Get-Content -LiteralPath (Join-Path $vsRoot 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$compiler = Join-Path $vsRoot "VC/Tools/MSVC/$vcVersion/bin/Hostx64/x64/cl.exe"
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Registered MSVC compiler unavailable.' }
$toolInfo = [ordered]@{
    powershell = $PSVersionTable.PSVersion.ToString()
    dotnetInfo = (Get-Content (Join-Path $evidence 'dotnet-info.stdout.log') -Raw).Trim()
    node = (Get-Content (Join-Path $evidence 'node-version.stdout.log') -Raw).Trim()
    npm = (Get-Content (Join-Path $evidence 'npm-version.stdout.log') -Raw).Trim()
    publishArguments = $publishArguments
    nativeToolchain = @{ runnerImage=$env:ImageVersion; visualStudio=$vsRoot; registeredMsvc=$vcVersion; compilerPath=$compiler; compilerVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($compiler).FileVersion }
    server = @{ productVersion=$versionInfo.ProductVersion; fileVersion=$versionInfo.FileVersion; assemblyVersion='4.5.0.0'; assemblyVersionEvidence='publish argument; NativeAOT has no inspectable managed assembly' }
}
$toolInfo | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $evidence 'tools.json') -Encoding utf8
& $node (Join-Path $PSScriptRoot 'package.mjs') seal $output
if ($LASTEXITCODE -ne 0) { throw 'Package sealing failed.' }
$null = New-Item -ItemType Directory -Path (Join-Path $output 'delivery')
$zip = Join-Path $output "delivery/sonnetdb-workbench-web-$Version-win-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
& (Join-Path $PSScriptRoot 'verify-archive.ps1') -Archive $zip -PackageRoot $package
& $node (Join-Path $PSScriptRoot 'package.mjs') manifest $output
if ($LASTEXITCODE -ne 0) { throw 'Manifest evidence validation failed.' }
Write-Output "P04 package complete; NOT_READY; source=$SourceCommit version=$Version output=$output"
