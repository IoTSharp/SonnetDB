[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validator = Join-Path $PSScriptRoot 'validate-fourteen-capability-index.ps1'
$source = Join-Path $root 'docs\audits\fourteen-capability-evidence-index.json'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonnetdb-capability-index-' + [Guid]::NewGuid().ToString('N'))
$repoTempRoot = Join-Path $root ('.codex-temp\capability-index-' + [Guid]::NewGuid().ToString('N'))

function Invoke-Validator {
    param([Parameter(Mandatory)] [string] $IndexPath)

    $process = $null
    $startedAt = [DateTime]::UtcNow
    $maxPolls = 200
    try {
        $process = Start-Process -FilePath $pwsh -ArgumentList @('-NoProfile', '-File', $validator, '-IndexPath', $IndexPath) -PassThru -NoNewWindow
        for ($poll = 0; $poll -lt $maxPolls -and -not $process.HasExited; $poll++) {
            Start-Sleep -Milliseconds 100
        }

        if (-not $process.HasExited) {
            $process.Kill($true)
            throw "Validator timed out after $(([DateTime]::UtcNow - $startedAt).TotalSeconds.ToString('F1')) seconds (PID $($process.Id))."
        }

        return $process.ExitCode
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(2000)
        }
        if ($null -ne $process) {
            $process.Dispose()
        }
    }
}

function Write-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Mutate
    )

    $casePath = Join-Path $tempRoot "$Name.json"
    $document = Get-Content -Raw -LiteralPath $source | ConvertFrom-Json
    & $Mutate $document
    $document | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $casePath -Encoding utf8
    return $casePath
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $repoTempRoot -Force | Out-Null

    $validExitCode = Invoke-Validator -IndexPath $source
    if ($validExitCode -ne 0) { throw "The valid capability index exited with code $validExitCode." }

    $absoluteEvidence = Write-Case -Name 'absolute-evidence' -Mutate {
        param($document)
        $document.capabilities[0].evidence[0] = [IO.Path]::GetFullPath((Join-Path $root 'README.md'))
    }
    if ((Invoke-Validator -IndexPath $absoluteEvidence) -eq 0) { throw 'Absolute evidence paths must be rejected.' }

    $escapingJourney = Write-Case -Name 'escaping-journey' -Mutate {
        param($document)
        $document.journeyIndex = '..\outside\journey.json'
    }
    if ((Invoke-Validator -IndexPath $escapingJourney) -eq 0) { throw 'Journey paths escaping the repository must be rejected.' }

    $emptyStatusDescription = Write-Case -Name 'empty-status-description' -Mutate {
        param($document)
        $document.statusContract.supported = ' '
    }
    if ((Invoke-Validator -IndexPath $emptyStatusDescription) -eq 0) { throw 'Empty status descriptions must be rejected.' }

    $duplicateStage = Join-Path $tempRoot 'duplicate-stage.json'
    $journeyPath = Join-Path $repoTempRoot 'journey.json'
    $journey = Get-Content -Raw -LiteralPath (Join-Path $root 'docs\audits\fourteen-capability-journey-index.json') | ConvertFrom-Json
    $journey.stages = @('local_contract', 'remote_parity', 'recovery', 'fixed_hardware', 'fixed_hardware')
    $journey | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $journeyPath -Encoding utf8
    $duplicateDocument = Get-Content -Raw -LiteralPath $source | ConvertFrom-Json
    $duplicateDocument.journeyIndex = [IO.Path]::GetRelativePath($root, $journeyPath)
    $duplicateDocument | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $duplicateStage -Encoding utf8
    if ((Invoke-Validator -IndexPath $duplicateStage) -eq 0) { throw 'Duplicate journey stages must be rejected.' }

    Write-Output 'Fourteen-capability index validator contract tests passed.'
}
finally {
    if ([IO.Directory]::Exists($tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
    if ([IO.Directory]::Exists($repoTempRoot)) {
        Remove-Item -LiteralPath $repoTempRoot -Recurse -Force
    }
}
