#Requires -Version 7.0
<#
.SYNOPSIS
拒绝已还原依赖图中的 SixLabors 包，包括传递依赖和构建期依赖。
.DESCRIPTION
在 dotnet restore 后运行，只检查 project.assets.json 的 libraries 节点。
中央包版本元数据不是实际依赖，不参与检查。
.PARAMETER RepositoryRoot
需要检查的仓库根目录，默认使用当前脚本所在仓库。
#>
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$assetsFiles = @(Get-ChildItem -LiteralPath $resolvedRoot -Filter 'project.assets.json' -File -Recurse)
if ($assetsFiles.Count -eq 0)
{
    throw 'No project.assets.json files found. Run dotnet restore before checking dependency licenses.'
}

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($assetsFile in $assetsFiles)
{
    $assets = Get-Content -LiteralPath $assetsFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
    if ($assets['libraries'] -isnot [System.Collections.IDictionary])
    {
        throw "Invalid NuGet assets file (missing libraries object): $($assetsFile.FullName)"
    }

    foreach ($library in $assets['libraries'].Keys)
    {
        if ($library -like 'SixLabors.*')
        {
            $relativePath = [System.IO.Path]::GetRelativePath($resolvedRoot, $assetsFile.FullName)
            $violations.Add("$relativePath : $library")
        }
    }
}

if ($violations.Count -gt 0)
{
    throw "SixLabors dependencies are not permitted:`n$($violations | Sort-Object | Out-String)"
}

Write-Host "Dependency license gate passed: no SixLabors packages in $($assetsFiles.Count) restored dependency graphs."
