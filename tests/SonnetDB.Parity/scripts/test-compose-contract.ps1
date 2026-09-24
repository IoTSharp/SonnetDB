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

# MinIO moved the pinned image out of Docker Hub. Keep the version immutable
# and ensure the compose stack uses the upstream Quay registry explicitly.
$expectedImage = "quay.io/minio/minio:RELEASE.2024-09-22T00-33-43Z"
Assert-True ($compose -match "(?m)^\s*image:\s+$([regex]::Escape($expectedImage))\s*$") `
    "Parity compose must use the pinned MinIO image from Quay."
Assert-True ($compose -notmatch "(?m)^\s*image:\s+minio/minio:") `
    "Parity compose must not use the retired Docker Hub MinIO namespace."

Write-Host "Parity compose image contract passed."
