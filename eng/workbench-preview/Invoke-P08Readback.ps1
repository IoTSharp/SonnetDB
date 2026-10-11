param([Parameter(Mandatory)][ValidateSet('download','first-run')][string]$Mode,
 [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]{0,5}$')][string]$Number,
 [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7 -or !$IsWindows){throw 'Windows PowerShell 7 required'}
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=[IO.Path]::GetFullPath($Output)
$seal=Join-Path $repository "docs/design/m47-unified-management-workbench/preview-1-distribution-$Number.json"
$evidence=Join-Path $root 'owned';$null=New-Item -ItemType Directory -Path $evidence -Force
$spec=Join-Path $evidence "$Mode.spec.json"
if(Test-Path -LiteralPath $spec){throw 'Immutable readback phase spec exists'}
$pwsh=(Get-Process -Id $PID).Path
$arguments=if($Mode -ceq 'download'){
 @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Invoke-PublicDownload.ps1'),'-Seal',$seal,'-Output',(Join-Path $root 'public-download'))
}else{
 @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Invoke-PublicFirstRun.ps1'),'-Seal',$seal,'-DownloadRoot',(Join-Path $root 'public-download'),'-RunRoot',(Join-Path $root 'public-first-run'))
}
@{name=$Mode;exe=$pwsh;arguments=$arguments;cwd=$repository;timeoutSeconds=if($Mode -ceq 'download'){660}else{1020};environment=@{}}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $spec -Encoding utf8
& (Join-Path $PSScriptRoot 'Invoke-Owned.ps1') -Spec $spec -EvidenceRoot $evidence
$receipt=Get-Content -LiteralPath (Join-Path $evidence "$Mode.result.json") -Raw|ConvertFrom-Json -DateKind String
if($receipt.status -cne 'PASS' -or $receipt.exitCode -ne 0 -or $receipt.cleanupComplete -ne $true){throw 'Public readback phase failed; retain original receipt'}
$global:LASTEXITCODE=0
