param([Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]{0,5}$')][string]$Number,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]{0,18}$')][string]$PrepareRun,
    [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7 -or !$IsWindows){throw 'Windows PowerShell 7 required'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $out){throw 'Fresh immutable P07 output required'}
$owned=Join-Path $out 'owned';$null=New-Item -ItemType Directory -Path $owned
$spec=Join-Path $owned 'p07.spec.json'
@{name='p07';exe=(Get-Command node -CommandType Application|Select-Object -First 1).Source;
    arguments=@((Join-Path $PSScriptRoot 'collect-p07.mjs'),$root,$Number,$PrepareRun,$out);cwd=$root;timeoutSeconds=660;
    environment=@{PREVIEW_PWSH=(Get-Process -Id $PID).Path}}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $spec
& (Join-Path $PSScriptRoot 'Invoke-Owned.ps1') -Spec $spec -EvidenceRoot $owned
$ownedResult=Get-Content -LiteralPath (Join-Path $owned 'p07.result.json') -Raw|ConvertFrom-Json -DateKind String
if($ownedResult.name -cne 'p07' -or $ownedResult.status -cne 'PASS' -or $ownedResult.exitCode -ne 0 -or $ownedResult.cleanupComplete -ne $true){throw 'P07 evidence collection failed; keep original receipts'}
$global:LASTEXITCODE=0
