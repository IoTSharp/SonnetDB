[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BundleRoot,
    [Parameter(Mandatory)] [string] $ArtifactUrl,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $ExpectedCommitSha,
    [string] $ExpectedTargetHardwareId,
    [string] $ExpectedTargetHardwareContract,
    [switch] $AllowNotReady
)

$ErrorActionPreference = 'Stop'
$fixture = Get-Content -LiteralPath (Join-Path $BundleRoot 'aggregation-fixture.json') -Raw -Encoding utf8 | ConvertFrom-Json
& $fixture.verifier -BundleRoot $BundleRoot -ArtifactUrl $ArtifactUrl -OutputPath $OutputPath -ExpectedCommitSha $ExpectedCommitSha -ExpectedTargetHardwareId $ExpectedTargetHardwareId -ExpectedTargetHardwareContract $ExpectedTargetHardwareContract -AllowNotReady:$AllowNotReady
$rawPath = Join-Path $BundleRoot 'high-cardinality/report.md'
switch -CaseSensitive ($fixture.mode) {
    'modify' { [IO.File]::AppendAllText($rawPath, 'post-verification-fixture', [Text.UTF8Encoding]::new($false)) }
    'delete' { [IO.File]::Delete($rawPath) }
    default { throw 'Unsupported synthetic post-verification fixture mode.' }
}
