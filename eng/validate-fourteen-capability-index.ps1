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
foreach ($capability in $index.capabilities) {
    if ([string]::IsNullOrWhiteSpace($capability.id)) { throw 'Capability id must not be empty' }
    if ($ids.ContainsKey($capability.id)) { throw "Duplicate capability id: $($capability.id)" }
    $ids[$capability.id] = $true
    if ($allowedMaturity -notcontains $capability.maturity) { throw "Invalid maturity for $($capability.id): $($capability.maturity)" }
    if ($capability.roadmap.Count -lt 1) { throw "Capability has no roadmap mapping: $($capability.id)" }
    if ($capability.entryPoints.Count -lt 1) { throw "Capability has no entry point mapping: $($capability.id)" }
    if ($capability.evidence.Count -lt 1) { throw "Capability has no evidence mapping: $($capability.id)" }

    foreach ($relativePath in $capability.evidence) {
        if ($relativePath -match '^https?://') { continue }
        $candidate = [IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
        if (-not [IO.File]::Exists($candidate)) { throw "Missing evidence path for $($capability.id): $relativePath" }
    }

    foreach ($relativePath in $capability.entryPoints) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
        if (-not [IO.Directory]::Exists($candidate) -and -not [IO.File]::Exists($candidate)) {
            throw "Missing entry point for $($capability.id): $relativePath"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($index.journeyIndex)) { throw 'Capability index must reference journeyIndex' }
$journeyPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $index.journeyIndex))
if (-not [IO.File]::Exists($journeyPath)) { throw "Journey index was not found: $journeyPath" }
$journey = Get-Content -Raw -LiteralPath $journeyPath | ConvertFrom-Json
if ($journey.schemaVersion -ne '1.0') { throw "Unsupported journey schemaVersion: $($journey.schemaVersion)" }
if ($journey.journeys.Count -ne 14) { throw "Expected 14 journeys, found $($journey.journeys.Count)" }
if ($journey.stages.Count -lt 1) { throw 'Journey index must define at least one stage' }
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
