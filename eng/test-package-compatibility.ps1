[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projects = @('src/SonnetDB.Core/SonnetDB.Core.csproj', 'src/SonnetDB.Protocol/SonnetDB.Protocol.csproj',
    'connectors/dotnet/SonnetDB.Client/SonnetDB.Client.csproj')
$versions = @('4.0.0', '4.0.0-preview.1', '4.5.0', '4.5.0-preview.1', '5.0.0-preview.1', '5.0.0', '5.0.1', '5.1.0-preview.1')
$passed = 0
foreach ($project in $projects) {
    foreach ($version in $versions) {
        $properties = & dotnet msbuild (Join-Path $repoRoot $project) "-p:PackageVersion=$version" `
            '-getProperty:EnablePackageValidation,PackageValidationBaselineVersion'
        if ($LASTEXITCODE -ne 0) { throw "Cannot evaluate $project ($version)." }
        $actual = ($properties -join "`n" | ConvertFrom-Json).Properties
        $expected = if ($version -in @('5.0.1', '5.1.0-preview.1')) { '5.0.0' }
            elseif ($project -like '*/SonnetDB.Core.csproj' -and $version -in @('4.5.0', '4.5.0-preview.1')) { '4.0.0' }
            else { '' }
        if ($actual.EnablePackageValidation -cne 'true' -or $actual.PackageValidationBaselineVersion -cne $expected) {
            throw "Invalid compatibility policy for $project ($version): $($actual | ConvertTo-Json -Compress)"
        }
        $passed++
    }
}
Write-Host "Package compatibility policy passed: $passed. Actual package validation remains part of dotnet pack."
