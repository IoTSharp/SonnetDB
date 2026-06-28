param(
    [switch]$RunNativePublish,
    [switch]$RunJavaCompile,
    [switch]$RunJavaFfmQuickstart,
    [switch]$RunCMake,
    [string]$VisualStudioGenerator = "Visual Studio 18 2026"
)

$ErrorActionPreference = "Stop"

function Write-Section {
    param([string]$Name)
    Write-Host ""
    Write-Host "== $Name =="
}

function Get-CommandSource {
    param([string]$Name)
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }

    return $command.Source
}

function Write-ToolFound {
    param(
        [string]$Name,
        [string]$Source,
        [string]$Origin = "PATH"
    )

    if ($Origin -eq "PATH") {
        Write-Host "found:   $Name -> $Source"
    }
    else {
        Write-Host "found:   $Name ($Origin) -> $Source"
    }
}

function Write-ToolMissing {
    param([string]$Name)
    Write-Host "missing: $Name"
}

function Find-VsWhere {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\Installer\vswhere.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

function Get-VisualStudioInstances {
    $vswhere = Find-VsWhere
    if ($null -eq $vswhere) {
        return @()
    }

    $json = & $vswhere -all -products * -format json
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        return @()
    }

    return @($json | ConvertFrom-Json)
}

function Find-VisualStudioToolchain {
    $instances = @(Get-VisualStudioInstances)
    if ($instances.Count -eq 0) {
        return $null
    }

    $preferred = @(
        $instances | Where-Object { $_.installationVersion -like "18.*" } | Sort-Object -Property installationVersion -Descending
        $instances | Where-Object { $_.installationVersion -notlike "18.*" } | Sort-Object -Property installationVersion -Descending
    )

    foreach ($instance in $preferred) {
        $root = [string]$instance.installationPath
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root)) {
            continue
        }

        $cmake = Join-Path $root "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
        if (-not (Test-Path -LiteralPath $cmake)) {
            $cmake = $null
        }

        $cl = $null
        $msvcRoot = Join-Path $root "VC\Tools\MSVC"
        if (Test-Path -LiteralPath $msvcRoot) {
            $cl = Get-ChildItem -LiteralPath $msvcRoot -Directory |
                Sort-Object -Property Name -Descending |
                ForEach-Object { Join-Path $_.FullName "bin\Hostx64\x64\cl.exe" } |
                Where-Object { Test-Path -LiteralPath $_ } |
                Select-Object -First 1
        }

        return [pscustomobject]@{
            DisplayName = [string]$instance.displayName
            Version = [string]$instance.installationVersion
            Path = $root
            CMake = $cmake
            Cl = $cl
        }
    }

    return $null
}

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Body
    )

    Write-Section $Name
    & $Body
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..\..\..")
Set-Location $repoRoot

Write-Section "Toolchain"
$vsToolchain = Find-VisualStudioToolchain
if ($null -ne $vsToolchain) {
    Write-Host "found:   Visual Studio -> $($vsToolchain.DisplayName) $($vsToolchain.Version) at $($vsToolchain.Path)"
}
else {
    Write-Host "missing: Visual Studio instance discoverable by vswhere"
}

$dotnetCommand = Get-CommandSource dotnet
$javaCommand = Get-CommandSource java
$javacCommand = Get-CommandSource javac
$cmakeCommand = Get-CommandSource cmake
$clCommand = Get-CommandSource cl
$gccCommand = Get-CommandSource gcc
$clangCommand = Get-CommandSource clang

if ($null -eq $cmakeCommand -and $null -ne $vsToolchain -and -not [string]::IsNullOrWhiteSpace($vsToolchain.CMake)) {
    $cmakeCommand = $vsToolchain.CMake
    Write-ToolFound "cmake" $cmakeCommand "VS bundled"
}
elseif ($null -ne $cmakeCommand) {
    Write-ToolFound "cmake" $cmakeCommand
}
else {
    Write-ToolMissing "cmake"
}

if ($null -eq $clCommand -and $null -ne $vsToolchain -and -not [string]::IsNullOrWhiteSpace($vsToolchain.Cl)) {
    $clCommand = $vsToolchain.Cl
    Write-ToolFound "cl" $clCommand "VS bundled"
}
elseif ($null -ne $clCommand) {
    Write-ToolFound "cl" $clCommand
}
else {
    Write-ToolMissing "cl"
}

foreach ($tool in @(
    @{ Name = "dotnet"; Source = $dotnetCommand },
    @{ Name = "java"; Source = $javaCommand },
    @{ Name = "javac"; Source = $javacCommand },
    @{ Name = "gcc"; Source = $gccCommand },
    @{ Name = "clang"; Source = $clangCommand }
)) {
    if ($null -ne $tool.Source) {
        Write-ToolFound $tool.Name $tool.Source
    }
    else {
        Write-ToolMissing $tool.Name
    }
}

$hasDotnet = $null -ne $dotnetCommand
$hasJava = $null -ne $javaCommand
$hasJavac = $null -ne $javacCommand
$hasCmake = $null -ne $cmakeCommand
$hasCl = $null -ne $clCommand
$hasGcc = $null -ne $gccCommand
$hasClang = $null -ne $clangCommand

if ($hasJava) {
    & java -version
}

if ($hasJavac) {
    & javac -version
}

if ($hasCmake) {
    & $cmakeCommand --version
}

if ($RunNativePublish) {
    if (-not $hasDotnet) {
        throw "dotnet is required for -RunNativePublish."
    }

    Invoke-Step "C NativeAOT publish fallback" {
        & dotnet publish "connectors/c/native/SonnetDB.Native/SonnetDB.Native.csproj" `
            --configuration Release `
            --runtime win-x64 `
            /p:SelfContained=true `
            --output "artifacts/connectors/c/dotnet-publish-win-x64"
    }
}

if ($RunJavaCompile) {
    if (-not ($hasJava -and $hasJavac)) {
        throw "java and javac are required for -RunJavaCompile."
    }

    Invoke-Step "Java source compile fallback" {
        $base = "artifacts/connectors/java/manual-check"
        $classes = Join-Path $base "classes"
        $ffmClasses = Join-Path $base "ffm-classes"
        $exampleClasses = Join-Path $base "example-classes"

        Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $classes, $ffmClasses, $exampleClasses | Out-Null

        $mainSources = @(Get-ChildItem -Path "connectors/java/src/main/java" -Recurse -Filter "*.java" | ForEach-Object { $_.FullName })
        & javac --release 8 -Xlint:-options -d $classes $mainSources
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        $ffmSources = @(Get-ChildItem -Path "connectors/java/src/ffm/java" -Recurse -Filter "*.java" | ForEach-Object { $_.FullName })
        & javac --release 21 --enable-preview -cp $classes -d $ffmClasses $ffmSources
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        & javac --release 8 -Xlint:-options -cp $classes -d $exampleClasses "connectors/java/examples/Quickstart.java"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

if ($RunJavaFfmQuickstart) {
    if (-not ($hasJava -and $hasJavac)) {
        throw "java and javac are required for -RunJavaFfmQuickstart."
    }

    $nativeDll = "artifacts/connectors/c/dotnet-publish-win-x64/SonnetDB.Native.dll"
    if (-not (Test-Path $nativeDll)) {
        throw "Missing $nativeDll. Run with -RunNativePublish first."
    }

    if (-not (Test-Path "artifacts/connectors/java/manual-check/classes")) {
        throw "Missing manual Java classes. Run with -RunJavaCompile first."
    }

    Invoke-Step "Java FFM quickstart fallback" {
        $cp = "artifacts/connectors/java/manual-check/ffm-classes;artifacts/connectors/java/manual-check/classes;artifacts/connectors/java/manual-check/example-classes"
        & java "--enable-preview" "--enable-native-access=ALL-UNNAMED" `
            "-Dsonnetdb.java.backend=ffm" `
            "-Dsonnetdb.native.path=$nativeDll" `
            -cp $cp `
            com.sonnetdb.examples.Quickstart
    }
}

if ($RunCMake) {
    if (-not $hasCmake) {
        throw "cmake is required for -RunCMake."
    }

    $isVisualStudioGenerator = $VisualStudioGenerator.StartsWith("Visual Studio", [System.StringComparison]::Ordinal)
    if (-not $isVisualStudioGenerator -and -not ($hasCl -or $hasGcc -or $hasClang)) {
        throw "A C compiler/linker is required for -RunCMake. Open a VS Developer PowerShell or install VS Build Tools C++ workload."
    }

    $suffix = if ($VisualStudioGenerator -match "18 2026") { "vs2026" } elseif ($VisualStudioGenerator -match "17 2022") { "vs2022" } else { "manual" }
    $cBuild = "artifacts/connectors/c/win-x64-$suffix"
    $javaBuild = "artifacts/connectors/java/windows-x64-$suffix"

    Invoke-Step "C connector CMake configure" {
        & $cmakeCommand -S "connectors/c" `
            -B $cBuild `
            -G $VisualStudioGenerator `
            -A x64 `
            -DSONNETDB_C_RID=win-x64
    }

    Invoke-Step "C connector CMake build" {
        & $cmakeCommand --build $cBuild --config Release
    }

    $nativeLibraryPath = Join-Path (Resolve-Path $cBuild) "Release/SonnetDB.Native.dll"
    if (-not (Test-Path $nativeLibraryPath)) {
        throw "CMake build did not produce $nativeLibraryPath."
    }

    Invoke-Step "Java connector CMake configure" {
        & $cmakeCommand -S "connectors/java" `
            -B $javaBuild `
            -G $VisualStudioGenerator `
            -A x64 `
            -DSONNETDB_JAVA_BUILD_FFM=ON `
            "-DSONNETDB_JAVA_NATIVE_LIBRARY=$nativeLibraryPath"
    }

    Invoke-Step "Java connector CMake build" {
        & $cmakeCommand --build $javaBuild --config Release
    }

    Invoke-Step "Java JNI quickstart" {
        & $cmakeCommand --build $javaBuild --target run_sonnetdb_java_quickstart --config Release
    }

    Invoke-Step "Java FFM quickstart" {
        & $cmakeCommand --build $javaBuild --target run_sonnetdb_java_quickstart_ffm --config Release
    }
}

Write-Section "Done"
Write-Host "Connector check completed."
