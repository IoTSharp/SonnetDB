[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('core', 'rest')][string]$Shard,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$SolutionPath = (Join-Path $PSScriptRoot '../SonnetDB.slnx')
)
$ErrorActionPreference = 'Stop'
$clock = [Diagnostics.Stopwatch]::StartNew()
$source = [IO.Path]::GetFullPath($SolutionPath)
$target = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetDirectoryName($source) -cne [IO.Path]::GetDirectoryName($target) -or $source -ceq $target) { throw 'Shard solution must be a separate file beside the source solution.' }
if (Test-Path -LiteralPath $target) { throw 'Refusing to overwrite an existing shard solution.' }
[xml]$solution = [IO.File]::ReadAllText($source)
$projects = @($solution.SelectNodes('//Project'))
if ($projects.Count -gt 512 -or $projects.Count -lt 2) { throw 'Unexpected solution project count.' }
$corePath = 'tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj'
if (@($projects | Where-Object { $_.Path -ceq $corePath }).Count -ne 1) { throw 'Exactly one Core test project is required.' }
$kept = [Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $projects.Count -and $clock.Elapsed.TotalSeconds -lt 10; $index++) {
    $project = $projects[$index]
    $isCore = $project.Path -ceq $corePath
    if (($Shard -eq 'core') -eq $isCore) { $kept.Add([string]$project.Path) }
    else { [void]$project.ParentNode.RemoveChild($project) }
}
if ($index -ne $projects.Count) { throw 'Shard generation deadline exceeded.' }
if (($Shard -eq 'core' -and $kept.Count -ne 1) -or ($Shard -eq 'rest' -and $kept.Count -ne $projects.Count - 1)) { throw 'Shard coverage invariant failed.' }
$settings = [Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.NewLineChars = "`n"
$stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [Xml.XmlWriter]::Create($stream, $settings)
$saved = $false
try { $solution.Save($writer); $writer.Flush(); $saved = $true }
finally {
    $writer.Dispose(); $stream.Dispose()
    if (!$saved) { Remove-Item -LiteralPath $target -Force }
}
[pscustomobject]@{ shard = $Shard; path = $target; sourceProjectCount = $projects.Count; projectPaths = $kept.ToArray() }
