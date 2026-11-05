$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'tag-publication-policy.ps1')
$commit = '455bf3b53b1e56df0064d8f574b0595cbb0b609e'
$source = @{ repository = @{ full_name = 'IoTSharp/SonnetDB' }; event = 'push';
    path = '.github/workflows/publish.yml'; head_branch = 'v5.0.0'; head_sha = $commit;
    status = 'completed'; conclusion = 'failure' }
$jobs = @('NuGet Packages', 'Release Bundles (linux-x64)', 'Release Bundles (win-x64)') |
    ForEach-Object { @{ name = $_; status = 'completed'; conclusion = 'success' } }
function Test-Source($Run, $ContractJobs, $Tag = 'v5.0.0', $Workflow = 'publish.yml') {
    Assert-TaggedPublicationSource -Run ([pscustomobject]$Run) -Jobs $ContractJobs `
        -Repository IoTSharp/SonnetDB -Tag $Tag -CommitSha $commit -Workflow $Workflow
}
function Expect-Rejection($Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Unsafe source accepted: $Name" }
    Write-Host "PASS reject $Name"
}

Test-Source $source $jobs
Write-Host 'PASS frozen tagged run with successful contracts and failed publication'
foreach ($case in @(
    @{ field = 'event'; value = 'pull_request' },
    @{ field = 'path'; value = '.github/workflows/ci.yml' },
    @{ field = 'head_branch'; value = 'main' },
    @{ field = 'head_sha'; value = ('0' * 40) },
    @{ field = 'status'; value = 'in_progress' }
)) {
    $changed = $source.Clone(); $changed[$case.field] = $case.value
    Expect-Rejection $case.field { Test-Source $changed $jobs }
}
$foreign = $source.Clone(); $foreign.repository = @{ full_name = 'untrusted/SonnetDB' }
Expect-Rejection 'foreign repository' { Test-Source $foreign $jobs }
Expect-Rejection 'missing platform contract' { Test-Source $source $jobs[0..1] }
Expect-Rejection 'duplicate contract' { Test-Source $source (@($jobs) + @($jobs[0])) }
$failed = @($jobs | ForEach-Object { $_.Clone() }); $failed[1].conclusion = 'failure'
Expect-Rejection 'failed platform contract' { Test-Source $source $failed }
# 模拟 filter=latest 的响应：旧 attempt 成功不能覆盖新 attempt 的失败。
$rerunFailed = @($jobs | ForEach-Object { $_.Clone() })
$rerunFailed[1].run_attempt = 2; $rerunFailed[1].conclusion = 'failure'
Expect-Rejection 'latest rerun contract failed' { Test-Source $source $rerunFailed }
Expect-Rejection 'malformed tag' { Test-Source $source $jobs 'v5.0.0;echo' }
$connectorRun = $source.Clone(); $connectorRun.path = '.github/workflows/connectors-release.yml'
$connectorJobs = @('Connectors (win-x64)', 'Connectors (linux-x64)', 'Connectors (win-x86)') |
    ForEach-Object { @{ name = $_; status = 'completed'; conclusion = 'success' } }
Test-Source $connectorRun $connectorJobs 'v5.0.0' 'connectors-release.yml'
Write-Host 'PASS three frozen connector platforms'
Write-Host 'Tagged publication policy: 13 checks passed.'
