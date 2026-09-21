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
}

Write-Output "Validated 14 capabilities in $resolvedIndexPath"
