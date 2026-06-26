param(
    [string]$Version,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$FinalOutputDir,
    [switch]$RunTests,
    [switch]$SkipAdminUi,
    [switch]$SkipInstaller,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot))
{
    $OutputRoot = Join-Path $RepoRoot 'artifacts\windows'
}
if ([string]::IsNullOrWhiteSpace($FinalOutputDir))
{
    $FinalOutputDir = Join-Path $OutputRoot 'final'
}

$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'true'
$env:NUGET_XMLDOC_MODE = 'skip'

function Get-DefaultVersion
{
    $propsPath = Join-Path $RepoRoot 'Directory.Build.props'
    [xml]$props = Get-Content -LiteralPath $propsPath
    $versionPrefix = [string]$props.Project.PropertyGroup.VersionPrefix

    if ([string]::IsNullOrWhiteSpace($versionPrefix))
    {
        return '0.0.0-dev'
    }

    return $versionPrefix
}

function Write-Section
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    Write-Host ''
    Write-Host "==> $Message"
}

function Assert-LastExitCode
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if ($LASTEXITCODE -ne 0)
    {
        throw "$Context failed with exit code $LASTEXITCODE."
    }
}

function Ensure-Directory
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path))
    {
        $null = New-Item -ItemType Directory -Path $Path -Force
    }
}

if ([string]::IsNullOrWhiteSpace($Version))
{
    $Version = Get-DefaultVersion
}

$solutionPath = Join-Path $RepoRoot 'SonnetDB.slnx'

Write-Section "Restore"
& dotnet restore $solutionPath
Assert-LastExitCode 'dotnet restore'

Write-Section "Build $Configuration"
& dotnet build $solutionPath --configuration $Configuration --no-restore /warnaserror
Assert-LastExitCode 'dotnet build'

if ($RunTests)
{
    $testResultsDir = Join-Path $OutputRoot 'test-results'
    Ensure-Directory $testResultsDir

    Write-Section "Test $Configuration"
    & dotnet test $solutionPath --configuration $Configuration --no-build --logger 'trx;LogFileName=windows-release.trx' --results-directory $testResultsDir
    Assert-LastExitCode 'dotnet test'
}

# -Installer is kept for compatibility; Windows installers are produced by default.
$releaseTasks = @('nuget', 'bundles')
if (-not $SkipInstaller)
{
    $releaseTasks += 'installers'
}

$releaseScript = Join-Path $PSScriptRoot 'release.ps1'
$releaseArgs = @{
    Tasks = [string[]]$releaseTasks
    Version = $Version
    Rid = 'win-x64'
    Configuration = $Configuration
    OutputRoot = $OutputRoot
    FinalOutputDir = $FinalOutputDir
    CleanIntermediate = $true
}

if (-not $SkipAdminUi)
{
    $releaseArgs['BuildAdminUi'] = $true
}

Write-Section "Package Windows artifacts"
& $releaseScript @releaseArgs
Assert-LastExitCode 'eng/release.ps1'

Write-Host ''
Write-Host "Windows release outputs are available under: $OutputRoot"
Write-Host "Final artifacts: $FinalOutputDir"
