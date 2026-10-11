param(
    [Parameter(Mandatory)][ValidateSet('prepare','gate','publish')][string]$Mode,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]{0,5}$')][string]$Number,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) { throw 'Windows PowerShell 7 required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence = Join-Path ([IO.Path]::GetFullPath($Output)) 'owned'
$null = New-Item -ItemType Directory -Path $evidence -Force
$node = (Get-Command node -CommandType Application | Select-Object -First 1).Source
$pwsh = (Get-Process -Id $PID).Path
$spec = Join-Path $evidence "$Mode.spec.json"
if (Test-Path -LiteralPath $spec) { throw 'Immutable operation spec exists.' }
@{ name=$Mode; exe=$node; arguments=@((Join-Path $PSScriptRoot 'dispatch.mjs'),$Mode,$Number,$root,[IO.Path]::GetFullPath($Output)); cwd=$root; timeoutSeconds=660; environment=@{ PREVIEW_PWSH=$pwsh } } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $spec -Encoding utf8
& (Join-Path $PSScriptRoot 'Invoke-Owned.ps1') -Spec $spec -EvidenceRoot $evidence
if ($LASTEXITCODE -ne 0) { throw 'Distribution operation failed; preserve result and reconcile any UNKNOWN without retry.' }
