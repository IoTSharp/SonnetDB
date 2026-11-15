[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    [switch]$RunNativeAot
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or later is required.'
}

$probeRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $probeRoot '..\..\..'))
$projectPath = Join-Path $probeRoot 'SonnetDbEfAotProbe.csproj'
$providerProjectPath = Join-Path $repositoryRoot 'src/SonnetDB.EntityFrameworkCore/SonnetDB.EntityFrameworkCore.csproj'
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)

if (Test-Path -LiteralPath $evidenceRoot) {
    $existing = @(Get-ChildItem -LiteralPath $evidenceRoot -Force)
    if ($existing.Count -gt 0) {
        throw "EvidenceRoot must be new or empty: $evidenceRoot"
    }
}
else {
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
}

$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$receiptPath = Join-Path $evidenceRoot 'issue-396-ef-aot-receipt.json'
$processLedger = [Collections.Generic.List[object]]::new()

function Get-ProcessChain {
    param([int]$ProcessId)

    $chain = [Collections.Generic.List[object]]::new()
    $current = $ProcessId
    for ($depth = 0; $depth -lt 8 -and $current -gt 0; $depth++) {
        $identity = @(Get-CimInstance Win32_Process -Filter "ProcessId = $current" -OperationTimeoutSec 3 | Select-Object -First 1)
        if ($identity.Count -eq 0) {
            break
        }

        $chain.Add([ordered]@{
                pid = [int]$identity.ProcessId
                parentPid = [int]$identity.ParentProcessId
                creationDate = [string]$identity.CreationDate
                commandLine = [string]$identity.CommandLine
            })

        if ([int]$identity.ParentProcessId -eq $current) {
            break
        }
        $current = [int]$identity.ParentProcessId
    }
    return @($chain)
}

function Invoke-BoundedDotnet {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string[]]$Arguments,
        [ValidateRange(5, 600)][int]$TimeoutSeconds = 600
    )

    $stdoutPath = Join-Path $evidenceRoot "$Label.stdout.log"
    $stderrPath = Join-Path $evidenceRoot "$Label.stderr.log"
    $startUtc = [DateTimeOffset]::UtcNow
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnet
    $startInfo.WorkingDirectory = $probeRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $startInfo.Environment['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'
    $startInfo.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $timedOut = $false
    $failure = $null
    $exitCode = $null
    $stdout = ''
    $stderr = ''
    $clock = [Diagnostics.Stopwatch]::StartNew()

    try {
        if (-not $process.Start()) {
            throw "Failed to start $Label."
        }

        $processLedger.Add([ordered]@{
                label = $Label
                pid = $process.Id
                startUtc = $startUtc.ToString('o')
                parentChain = @(Get-ProcessChain -ProcessId $process.Id)
                commandLine = "$dotnet $($Arguments -join ' ')"
            })

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $pollLimit = $TimeoutSeconds * 4
        for ($poll = 0; $poll -lt $pollLimit -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds; $poll++) {
            if ($process.WaitForExit(250)) {
                break
            }
            if (($poll % 40) -eq 0) {
                Write-Host "$Label running: $([int]$clock.Elapsed.TotalSeconds)s"
            }
        }

        if (-not $process.HasExited) {
            $timedOut = $true
            $failure = "The $Label command exceeded $TimeoutSeconds seconds."
            try { $process.Kill($true) } catch { }
            try { $process.WaitForExit(5000) } catch { }
        }

        $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
        try { $stdout = $stdoutTask.GetAwaiter().GetResult() } catch { $stdout = "<stdout unavailable: $($_.Exception.Message)>" }
        try { $stderr = $stderrTask.GetAwaiter().GetResult() } catch { $stderr = "<stderr unavailable: $($_.Exception.Message)>" }
    }
    catch {
        $failure = $_.Exception.Message
    }
    finally {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
            try { $process.WaitForExit(5000) } catch { }
        }
        $process.Dispose()
    }

    [IO.File]::WriteAllText($stdoutPath, $stdout, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($stderrPath, $stderr, [Text.UTF8Encoding]::new($false))
    $allOutput = $stdout + [Environment]::NewLine + $stderr
    $warningLines = @($allOutput -split "\r?\n" | Where-Object { $_ -match '(?i)\bwarning\b' } | Sort-Object -Unique)
    $errorLines = @($allOutput -split "\r?\n" | Where-Object { $_ -match '(?i)\berror\b' } | Sort-Object -Unique)

    return [ordered]@{
        label = $Label
        arguments = $Arguments
        startUtc = $startUtc.ToString('o')
        elapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 3)
        exitCode = $exitCode
        timedOut = $timedOut
        failure = $failure
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        warningCount = $warningLines.Count
        errorCount = $errorLines.Count
        passed = (-not $timedOut -and $null -eq $failure -and 0 -eq $exitCode)
    }
}

$providerSource = [IO.File]::ReadAllText($providerProjectPath)
$probeSource = [IO.File]::ReadAllText($projectPath)
$providerAotDisabled = $providerSource -match '<IsAotCompatible>\s*false\s*</IsAotCompatible>'
$projectReferenceConsumer = $probeSource -match '<ProjectReference\s+Include="\.\./\.\./\.\./src/SonnetDB\.EntityFrameworkCore/'
$packageReferenceConsumer = $probeSource -match '<PackageReference\s+Include="SonnetDB\.EntityFrameworkCore"'
$abpReference = $probeSource -match 'Volo\.Abp|Volo\.Abp\.EntityFrameworkCore'

$stages = [Collections.Generic.List[object]]::new()
$restoreArguments = @(
    'restore', $projectPath, '--configfile', (Join-Path $probeRoot 'NuGet.Config'),
    '--nologo', '--no-cache', '-p:NuGetAudit=false', '-p:RestoreFallbackFolders='
)
$stages.Add((Invoke-BoundedDotnet -Label 'restore' -Arguments $restoreArguments))

if ($stages[0].passed) {
    $buildArguments = @(
        'build', $projectPath, '-c', 'Release', '--no-restore', '--no-incremental', '--nologo', '-v:minimal',
        '-p:ContinuousIntegrationBuild=true', '-p:IsAotCompatible=true',
        '-p:EnableTrimAnalyzer=true', '-p:EnableAotAnalyzer=true', '-p:PublishTrimmed=true',
        '-p:TreatWarningsAsErrors=false', '-p:NuGetAudit=false'
    )
    $stages.Add((Invoke-BoundedDotnet -Label 'forced-analyzer-build' -Arguments $buildArguments))
}

$nativeAotStage = $null
if ($RunNativeAot -and $stages.Count -gt 1 -and $stages[1].passed) {
    $publishPath = Join-Path $evidenceRoot 'native-aot-publish'
    $publishArguments = @(
        'publish', $projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--no-restore', '--nologo', '-o', $publishPath, '-p:PublishAot=true',
        '-p:IsAotCompatible=true', '-p:EnableTrimAnalyzer=true', '-p:EnableAotAnalyzer=true',
        '-p:NuGetAudit=false', '-p:TreatWarningsAsErrors=false'
    )
    $nativeAotStage = Invoke-BoundedDotnet -Label 'native-aot-publish' -Arguments $publishArguments
    $stages.Add($nativeAotStage)
}

$sourceManifest = @(
    $providerProjectPath,
    $projectPath,
    (Join-Path $probeRoot 'Program.cs'),
    (Join-Path $probeRoot 'Directory.Build.props'),
    (Join-Path $probeRoot 'Directory.Packages.props')
) | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($repositoryRoot, $_)
        sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$stageFailed = @($stages | Where-Object { -not $_.passed }).Count -gt 0

$receipt = [ordered]@{
    schemaVersion = 'issue-396-ef-aot-v1'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    source = [ordered]@{
        repositoryRoot = $repositoryRoot
        providerProject = [IO.Path]::GetRelativePath($repositoryRoot, $providerProjectPath)
        probeProject = [IO.Path]::GetRelativePath($repositoryRoot, $projectPath)
        files = @($sourceManifest)
    }
    consumer = [ordered]@{
        mode = if ($projectReferenceConsumer) { 'ProjectReference' } else { 'UNKNOWN' }
        projectReference = $projectReferenceConsumer
        packageReference = $packageReferenceConsumer
        abpReference = $abpReference
    }
    stages = @($stages)
    status = [ordered]@{
        providerContract = if ($providerAotDisabled) { 'NOT_SUPPORTED' } else { 'UNKNOWN' }
        forcedAnalyzerBuild = if ($stages.Count -gt 1 -and $stages[1].passed) { 'PASS' } elseif ($stages.Count -gt 1) { 'FAIL' } else { 'NOT_RUN' }
        nativeAotPublish = if ($RunNativeAot) { if ($nativeAotStage -and $nativeAotStage.passed) { 'PASS' } else { 'FAIL' } } else { 'NOT_RUN' }
        nativeAotRun = 'NOT_RUN'
        nugetOnlyConsumer = 'NOT_RUN'
        abpConsumer = 'NOT_RUN'
        overall = if ($stageFailed) { 'FAIL' } elseif ($providerAotDisabled) { 'NOT_SUPPORTED' } else { 'UNKNOWN' }
    }
    boundaries = @(
        'The provider project explicitly declares IsAotCompatible=false.',
        'The probe references the provider project and is not a NuGet-only consumer.',
        'No ABP application or official ABP BookStore/Passkey consumer is included.',
        'NativeAOT publish/run is not performed unless -RunNativeAot is explicitly supplied.'
    )
    processLedger = @($processLedger)
}

[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Host "Receipt: $receiptPath"
Write-Host "Provider contract: $($receipt.status.providerContract)"
Write-Host "Forced analyzer build: $($receipt.status.forcedAnalyzerBuild)"
Write-Host "Overall: $($receipt.status.overall)"

if (($receipt.status.overall -eq 'FAIL') -or ($receipt.status.forcedAnalyzerBuild -eq 'FAIL')) {
    exit 1
}
exit 0
