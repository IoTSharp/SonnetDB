# Shared policy and pure evaluator; the public verifier obtains evidence from GitHub.
function Select-LatestReleaseRun {
    param([object[]]$Runs)
    $Runs | Sort-Object @{ Expression = {
        $timestamps = @($_.created_at, $_.run_started_at, $_.updated_at) | Where-Object { $_ } | ForEach-Object { [DateTimeOffset]$_ }
        $timestamps | Sort-Object -Descending | Select-Object -First 1
    }; Descending = $true }, @{ Expression = { [long]$_.run_attempt }; Descending = $true }, @{ Expression = { [long]$_.id }; Descending = $true } | Select-Object -First 1
}

function Get-ReleaseWorkflowPolicy {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?\z')]
        [string]$Version
    )
    @(
        @{ File = 'ci.yml'; Jobs = @('Build & Test (ubuntu-latest)', 'Build & Test (windows-latest)', 'Format Check', 'AOT Publish (ubuntu-latest)', 'AOT Publish (windows-latest)', 'AOT Publish (ubuntu-24.04-arm)'); Artifacts = @('test-results-ubuntu-latest', 'test-results-windows-latest', 'aot-linux-x64', 'aot-linux-arm64', 'aot-win-x64') }
        @{ File = 'codeql.yml'; Jobs = @('Analyze (csharp)'); Artifacts = @() }
        @{ File = 'docs-pages.yml'; Jobs = @('Build Docs'); Artifacts = @('github-pages') }
        @{ File = 'management-workbench-smoke.yml'; Jobs = @('Server management contracts', 'Web Admin and Studio bridge', 'Studio desktop host', 'VS Code HTTP consumer', 'VS Code Extension Host'); Artifacts = @('management-contract-results', 'studio-host-results', 'sonnetdb-vscode-vsix') }
        @{ File = 'parity.yml'; Jobs = @('Parity (light, ubuntu-latest)', 'Parity (full, ubuntu-latest)'); Artifacts = @('parity-light-reports', 'parity-full-reports') }
        @{ File = 'document-soak.yml'; Jobs = @('soak'); Artifacts = @('document-soak-*-*') }
        @{ File = 'ecosystem-soak.yml'; Jobs = @('soak'); Artifacts = @('ecosystem-soak-*-*') }
        @{ File = 'm19-capacity-evidence.yml'; Jobs = @('GitHub-hosted capacity validation'); Artifacts = @('m19-hosted-*') }
        @{ File = 'm39-trigger-evidence.yml'; Jobs = @('evidence'); Artifacts = @('m39-trigger-v2-*') }
        # These names use the same resolved version passed to the package validators.
        # An unversioned artifact or a successful preflight for another version is not evidence.
        @{ File = 'publish.yml'; DispatchOnly = $true; Jobs = @('NuGet Packages', 'Release Bundles (linux-x64)', 'Release Bundles (win-x64)'); Artifacts = @("nuget-packages-$Version", "release-$Version-linux-x64", "release-$Version-win-x64", "insert-returning-contract-$Version-linux-x64", "insert-returning-contract-$Version-win-x64") }
        @{ File = 'connectors-release.yml'; DispatchOnly = $true; Jobs = @('Connectors (linux-x64)', 'Connectors (win-x64)', 'Connectors (win-x86)'); Artifacts = @("connectors-$Version-linux-x64", "connectors-$Version-win-x64", "connectors-$Version-win-x86") }
        @{ File = 'docker-publish.yml'; DispatchOnly = $true; Jobs = @('Build, Verify and Publish Docker Image'); Artifacts = @('docker-validation') }
    )
}

function Get-RequiredReleaseSteps {
    param([string]$JobName)
    switch -Wildcard ($JobName) {
        'Build & Test (*)' { @('Build', 'Test', 'Test release gate contracts') }
        'Format Check' { 'Format check' }
        'AOT Publish (*)' { @('Publish SonnetDB.Cli (Native AOT)', 'Publish SonnetDB (Native AOT)', 'Run image codec smoke (Native AOT)') }
        'Analyze (csharp)' { @('Build', 'Analyze code and upload results') }
        'Build Docs' { 'Build static site' }
        'Server management contracts' { 'Test multi-model management contracts' }
        'Web Admin and Studio bridge' { @('Build Web Admin', 'Run management workbench smoke') }
        'Studio desktop host' { 'Test Studio host contracts' }
        'VS Code HTTP consumer' { @('Test SQL language server', 'Run extension consumer smoke', 'Package VS Code extension') }
        'VS Code Extension Host' { 'Run Extension Host smoke' }
        'Parity (*' { @('Start parity stack', 'Run parity gates', 'Run reliability gates', 'Gate parity result') }
        'NuGet Packages' { 'Pack NuGet packages' }
        'Release Bundles (*)' { @('Build bundles and installers', 'Verify bundle network defaults with the NativeAOT Server', 'Verify installed INSERT RETURNING contract and published compatibility') }
        'Connectors (*)' { @('Build and package connectors', 'Verify connector package inventory and native entries') }
        'Build, Verify and Publish Docker Image' { @('Build image for local verification', 'Verify container startup, readiness and embedded Admin UI') }
        'evidence' { @('Run golden journey and commit failure tests', 'Run real process-kill replay test', 'Run REST and Frame routine contracts', 'Run M39 cost and rollback report') }
        'GitHub-hosted capacity validation' { @('Validate strict capacity verifier contracts', 'Run all four scaled specialized profiles', 'Record hosted hardware, scale and evidence boundary') }
    }
}

function Test-ReleaseWorkflowEvidence {
    param([hashtable]$Policy, [object]$Evidence, [string]$CommitSha, [string]$Repository)
    $issues = [Collections.Generic.List[string]]::new()
    $run = $Evidence.run
    if ($null -eq $run) {
        $issues.Add('No eligible workflow run exists for this commit.')
    }
    else {
        if ($run.head_sha -ne $CommitSha) { $issues.Add('Run commit does not match the release commit.') }
        if ($run.repository.full_name -ne $Repository) { $issues.Add('Run repository does not match.') }
        if ($run.path -ne ".github/workflows/$($Policy.File)") { $issues.Add('Run workflow path does not match.') }
        if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') { $issues.Add("Latest run is $($run.status)/$($run.conclusion).") }
        if ($run.event -notin @('push', 'workflow_dispatch', 'schedule')) { $issues.Add('Pull requests and external events are not release evidence.') }
        if ($Policy.DispatchOnly -and $run.event -ne 'workflow_dispatch') { $issues.Add('A successful dispatch preflight is required before publishing.') }
        foreach ($name in $Policy.Jobs) {
            $matches = @($Evidence.jobs | Where-Object name -CEQ $name)
            if ($matches.Count -ne 1 -or $matches[0].conclusion -ne 'success' -or $matches[0].status -ne 'completed') {
                $issues.Add("Required job '$name' is missing, duplicated or not successful.")
            }
            elseif ($matches.Count -eq 1) {
                $requiredSteps = @(Get-RequiredReleaseSteps $name)
                if ($name -eq 'soak') {
                    $requiredSteps = if ($Policy.File -eq 'document-soak.yml') { @('Run Document Store profile', 'Audit capacity evidence contract') } else { @('Run ecosystem profile') }
                }
                foreach ($stepName in $requiredSteps) {
                    $stepMatches = @($matches[0].steps | Where-Object name -CEQ $stepName)
                    if ($stepMatches.Count -ne 1 -or $stepMatches[0].conclusion -ne 'success') {
                        $issues.Add("Required step '$stepName' in '$name' did not execute successfully.")
                    }
                }
            }
        }
        foreach ($job in $Evidence.jobs) {
            if ($job.conclusion -notin @('success', 'skipped')) { $issues.Add("Job '$($job.name)' did not succeed.") }
            if (@($job.steps | Where-Object { $_.conclusion -in @('failure', 'cancelled', 'timed_out') }).Count -gt 0) {
                $issues.Add("Job '$($job.name)' contains a failed step.")
            }
        }
        foreach ($pattern in $Policy.Artifacts) {
            if (@($Evidence.artifacts | Where-Object {
                $_.name -like $pattern -and $_.expired -eq $false -and $_.size_in_bytes -gt 0 `
                    -and $_.created_at -and $run.run_started_at `
                    -and [DateTimeOffset]$_.created_at -ge [DateTimeOffset]$run.run_started_at
            }).Count -eq 0) {
                $issues.Add("Required artifact '$pattern' is absent, empty, expired or belongs to an earlier attempt.")
            }
        }
    }
    [pscustomobject]@{
        workflow = $Policy.File; ready = $issues.Count -eq 0
        runId = if ($run) { $run.id } else { $null }
        url = if ($run) { $run.html_url } else { $null }
        issues = $issues.ToArray()
    }
}
