param([Parameter(Mandatory)][string]$SourceCommit,[Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 required'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputDirectory=[IO.Path]::GetFullPath($Output)
$null=New-Item -ItemType Directory -Path (Join-Path $outputDirectory 'api') -Force
$watch=[Diagnostics.Stopwatch]::StartNew();$requests=0
function Invoke-RestMethod {
    param([string]$Uri,[hashtable]$Headers,[int]$TimeoutSec=45)
    if($script:requests -ge 100 -or $watch.Elapsed.TotalSeconds -ge 390 -or (Test-Path -LiteralPath (Join-Path $outputDirectory 'cancel'))){throw 'P07 API wall/count/cancel budget'}
    $script:requests++
    $file=Join-Path $outputDirectory ('api/{0:d3}.json' -f $script:requests)
    try {
        $value=Microsoft.PowerShell.Utility\Invoke-RestMethod -Uri $Uri -Headers $Headers -TimeoutSec ([Math]::Min($TimeoutSec,390-[int]$watch.Elapsed.TotalSeconds))
        @{uri=$Uri;observedUtc=[DateTimeOffset]::UtcNow.ToString('o');response=$value}|ConvertTo-Json -Depth 50|Set-Content -LiteralPath $file -Encoding utf8
        return $value
    } catch {
        @{uri=$Uri;observedUtc=[DateTimeOffset]::UtcNow.ToString('o');error=$_.Exception.Message}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $file -Encoding utf8
        throw
    }
}
# Capture responses without changing the original verifier or its policy.
& (Join-Path $root 'eng/verify-release-readiness.ps1') -Repository 'IoTSharp/SonnetDB' -CommitSha $SourceCommit -Version $Version -OutputPath (Join-Path $outputDirectory 'readiness.json')
