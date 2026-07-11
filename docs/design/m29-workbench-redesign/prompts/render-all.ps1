param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\renders'),
    [string]$ImageScript = 'C:\Users\mysti\.codex\skills\gpt-image-design\scripts\gpt_image_design.py',
    [string[]]$FrameNumbers = @(),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
    throw 'OPENAI_API_KEY is required.'
}

if ([string]::IsNullOrWhiteSpace($env:OPENAI_BASE_URL)) {
    $env:OPENAI_BASE_URL = 'https://sonnet.vip/v1'
}

$referenceImage = Join-Path $PSScriptRoot '..\concept-framework-microsoft365-light.png'
$sharedPrompt = Get-Content -Raw (Join-Path $PSScriptRoot '00-shared-framework.txt')
$frames = @(
    @{ File = '01-sql-and-shared.txt'; Number = '01'; Output = 'prototype-01-sql-timeseries.png' }
    @{ File = '01-sql-and-shared.txt'; Number = '02'; Output = 'prototype-02-write-approval.png' }
    @{ File = '02-relational.txt'; Number = '03'; Output = 'prototype-03-relational-data.png' }
    @{ File = '02-relational.txt'; Number = '04'; Output = 'prototype-04-relational-designer.png' }
    @{ File = '02-relational.txt'; Number = '05'; Output = 'prototype-05-relational-import-er.png' }
    @{ File = '03-document.txt'; Number = '06'; Output = 'prototype-06-document.png' }
    @{ File = '03-document.txt'; Number = '07'; Output = 'prototype-07-document-validator.png' }
    @{ File = '04-kv.txt'; Number = '08'; Output = 'prototype-08-kv.png' }
    @{ File = '05-mq.txt'; Number = '09'; Output = 'prototype-09-mq-overview.png' }
    @{ File = '05-mq.txt'; Number = '10'; Output = 'prototype-10-mq-messages.png' }
    @{ File = '05-mq.txt'; Number = '11'; Output = 'prototype-11-mq-consumers.png' }
    @{ File = '06-search.txt'; Number = '12'; Output = 'prototype-12-vector.png' }
    @{ File = '06-search.txt'; Number = '13'; Output = 'prototype-13-fulltext.png' }
    @{ File = '07-object-storage.txt'; Number = '14'; Output = 'prototype-14-objects.png' }
    @{ File = '07-object-storage.txt'; Number = '15'; Output = 'prototype-15-object-governance.png' }
    @{ File = '08-delivery-surfaces.txt'; Number = '16'; Output = 'prototype-16-studio-bridge.png' }
    @{ File = '08-delivery-surfaces.txt'; Number = '17'; Output = 'prototype-17-vscode.png' }
)
$selectedFrameNumbers = @($FrameNumbers | ForEach-Object { ([int]$_).ToString('D2') })

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

foreach ($frame in $frames) {
    if ($selectedFrameNumbers.Count -gt 0 -and $frame.Number -notin $selectedFrameNumbers) {
        continue
    }

    $outputPath = Join-Path $OutputDirectory $frame.Output
    if ((Test-Path $outputPath) -and -not $Force) {
        Write-Host "Skip $($frame.Output)"
        continue
    }

    $specificPrompt = Get-Content -Raw (Join-Path $PSScriptRoot $frame.File)
    $pattern = "(?s)(FRAME $($frame.Number)\b.*?)(?=FRAME \d+|$)"
    $match = [regex]::Match($specificPrompt, $pattern)
    if (-not $match.Success) {
        throw "FRAME $($frame.Number) was not found in $($frame.File)."
    }

    $mode = if ($frame.Number -eq '17') { 'generate' } else { 'edit' }
    $prompt = if ($frame.Number -eq '17') {
        "Use case: ui-mockup`nAsset type: production-ready VS Code extension screen, desktop 1536x1024`n" + $match.Groups[1].Value
    } else {
        $sharedPrompt + "`n" + $match.Groups[1].Value
    }
    $arguments = @(
        $ImageScript
        $mode
    )
    if ($mode -eq 'edit') {
        $arguments += @('--image', $referenceImage)
    }
    $arguments += @(
        '--prompt', $prompt
        '--out', $outputPath
        '--size', '1536x1024'
        '--quality', 'high'
        '--timeout', '360'
        '--retries', '1'
    )
    if ($Force) {
        $arguments += '--force'
    }

    & python @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Image generation failed for $($frame.Output)."
    }
}
