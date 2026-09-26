$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $Condition,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$composePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../docker-compose.parity.yml"))
$compose = Get-Content -Raw -LiteralPath $composePath

# Both formerly public registries reject anonymous pulls. Keep the reference
# version by building its upstream commit and checking the archive digest.
$expectedImage = "sonnetdb-parity-minio:RELEASE.2024-09-22T00-33-43Z"
Assert-True ($compose -match "(?m)^\s*image:\s+$([regex]::Escape($expectedImage))\s*$") `
    "Parity compose must use the locally built pinned MinIO reference."
Assert-True ($compose -notmatch "(?m)^\s*image:\s+(quay.io/)?minio/minio:") `
    "Parity compose must not use the retired public MinIO image namespaces."
$dockerfile = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../docker/minio/Dockerfile')
Assert-True ($dockerfile.Contains('03e996320ebb887112fb2a15c6f27936e5f124a0')) `
    "The MinIO source revision must match the existing reference version."
Assert-True ($dockerfile.Contains('23783181b83d426a01dad69524b0fccc19796fd475f40cb9144237c6ce515c22') -and $dockerfile.Contains('sha256sum --check')) `
    "The upstream source archive must be verified before building."

$clickHouse = [regex]::Match($compose, '(?ms)^  clickhouse:\r?\n(?<service>.*?)(?=^  [a-z][a-z0-9-]*:|\z)').Groups['service'].Value
Assert-True ($clickHouse.Contains('CLICKHOUSE_USER: ${PARITY_CLICKHOUSE_USER:-parity}') -and $clickHouse.Contains('CLICKHOUSE_PASSWORD: ${PARITY_CLICKHOUSE_PASSWORD:-parity-clickhouse}')) `
    "ClickHouse must initialize the credentials used by host and compose clients."
Assert-True ($clickHouse.Contains("--query 'SELECT 1'") -and $clickHouse.Contains('$$CLICKHOUSE_PASSWORD')) `
    "ClickHouse health must verify an authenticated query, not just the public ping endpoint."

Write-Host "Parity compose image contract passed."
