[CmdletBinding()]
param(
    [string] $IndexPath = (Join-Path $PSScriptRoot '..\docs\audits\fourteen-capability-evidence-index.json')
)

$ErrorActionPreference = 'Stop'
$resolvedIndexPath = [IO.Path]::GetFullPath($IndexPath)
if (-not [IO.File]::Exists($resolvedIndexPath)) {
    throw "Capability index was not found: $resolvedIndexPath"
}

$index = Get-Content -Raw -LiteralPath $resolvedIndexPath | ConvertFrom-Json
if ($index.schemaVersion -ne '1.0') { throw "Unsupported schemaVersion: $($index.schemaVersion)" }
if ($index.capabilities.Count -ne 14) { throw "Expected 14 capabilities, found $($index.capabilities.Count)" }

$allowedMaturity = @('supported', 'partial', 'planned', 'not_planned', 'beta')
$allowedJourneyStatus = @('PASS', 'PARTIAL', 'NOT_READY', 'DEFERRED')
$ids = @{}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRootPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $Description,
        [switch] $AllowUrl
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "$Description must not be empty"
    }

    if ($AllowUrl -and $RelativePath -match '^https?://') {
        return $null
    }

    # Recognize Windows drive/UNC paths explicitly so the contract remains strict
    # when the validator is run under PowerShell Core on a non-Windows host.
    $isRootedPath = [IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '^(?:[A-Za-z]:[\\/]|[\\/]{2})'
    if ($isRootedPath) {
        throw "$Description must be repository-relative: $RelativePath"
    }

    $candidate = [IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    if ($candidate -ne $repoRoot -and -not $candidate.StartsWith($repoRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes the repository root: $RelativePath"
    }

    return $candidate
}

if ($null -eq $index.statusContract) { throw 'Capability index must define statusContract' }
foreach ($status in $allowedMaturity) {
    $statusProperty = $index.statusContract.PSObject.Properties[$status]
    if ($null -eq $statusProperty) {
        throw "Capability statusContract is missing: $status"
    }
    if ([string]::IsNullOrWhiteSpace([string]$statusProperty.Value)) {
        throw "Capability statusContract description must not be empty: $status"
    }
}
foreach ($property in @($index.statusContract.PSObject.Properties.Name)) {
    if ($allowedMaturity -notcontains $property) {
        throw "Capability statusContract has unknown status: $property"
    }
}

foreach ($capability in $index.capabilities) {
    if ([string]::IsNullOrWhiteSpace($capability.id)) { throw 'Capability id must not be empty' }
    if ($ids.ContainsKey($capability.id)) { throw "Duplicate capability id: $($capability.id)" }
    $ids[$capability.id] = $true
    if ([string]::IsNullOrWhiteSpace($capability.name)) { throw "Capability name must not be empty: $($capability.id)" }
    if ([string]::IsNullOrWhiteSpace($capability.category)) { throw "Capability category must not be empty: $($capability.id)" }
    if ([string]::IsNullOrWhiteSpace($capability.boundary)) { throw "Capability boundary must not be empty: $($capability.id)" }
    if ($allowedMaturity -notcontains $capability.maturity) { throw "Invalid maturity for $($capability.id): $($capability.maturity)" }
    if ($capability.roadmap.Count -lt 1) { throw "Capability has no roadmap mapping: $($capability.id)" }
    if ($capability.entryPoints.Count -lt 1) { throw "Capability has no entry point mapping: $($capability.id)" }
    if ($capability.evidence.Count -lt 1) { throw "Capability has no evidence mapping: $($capability.id)" }

    foreach ($relativePath in $capability.evidence) {
        $candidate = Resolve-RepositoryPath -RelativePath ([string]$relativePath) -Description "Evidence path for $($capability.id)" -AllowUrl
        if ($null -eq $candidate) { continue }
        if (-not [IO.File]::Exists($candidate)) { throw "Missing evidence path for $($capability.id): $relativePath" }
    }

    foreach ($relativePath in $capability.entryPoints) {
        $candidate = Resolve-RepositoryPath -RelativePath ([string]$relativePath) -Description "Entry point for $($capability.id)"
        if (-not [IO.Directory]::Exists($candidate) -and -not [IO.File]::Exists($candidate)) {
            throw "Missing entry point for $($capability.id): $relativePath"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($index.journeyIndex)) { throw 'Capability index must reference journeyIndex' }
$journeyPath = Resolve-RepositoryPath -RelativePath ([string]$index.journeyIndex) -Description 'Journey index path'
if (-not [IO.File]::Exists($journeyPath)) { throw "Journey index was not found: $journeyPath" }
$journey = Get-Content -Raw -LiteralPath $journeyPath | ConvertFrom-Json
if ($journey.schemaVersion -ne '1.0') { throw "Unsupported journey schemaVersion: $($journey.schemaVersion)" }
if ($journey.journeys.Count -ne 14) { throw "Expected 14 journeys, found $($journey.journeys.Count)" }
$expectedStages = @('local_contract', 'remote_parity', 'recovery', 'fixed_hardware', 'long_run')
$stageNames = @($journey.stages | ForEach-Object { [string]$_ })
if ($stageNames.Count -ne $expectedStages.Count -or @($stageNames | Sort-Object -Unique).Count -ne $stageNames.Count) {
    throw 'Journey index must define each stage exactly once'
}
foreach ($stage in $expectedStages) {
    if ($stageNames -notcontains $stage) { throw "Journey index is missing stage: $stage" }
}
$journeyIds = @{}
foreach ($item in $journey.journeys) {
    if ([string]::IsNullOrWhiteSpace($item.id)) { throw 'Journey id must not be empty' }
    if ($journeyIds.ContainsKey($item.id)) { throw "Duplicate journey id: $($item.id)" }
    $journeyIds[$item.id] = $true
    if (-not $ids.ContainsKey($item.id)) { throw "Journey has no capability mapping: $($item.id)" }
    foreach ($stage in $journey.stages) {
        $property = $item.PSObject.Properties[$stage]
        if ($null -eq $property) { throw "Journey $($item.id) is missing stage: ${stage}" }
        if ($allowedJourneyStatus -notcontains [string]$property.Value) {
            throw "Invalid journey status for $($item.id)/${stage}: $($property.Value)"
        }
    }
}
foreach ($id in $ids.Keys) {
    if (-not $journeyIds.ContainsKey($id)) { throw "Capability has no journey mapping: $id" }
}

Write-Output "Validated 14 capabilities in $resolvedIndexPath"
