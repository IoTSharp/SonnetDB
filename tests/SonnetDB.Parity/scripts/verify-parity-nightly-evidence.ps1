[CmdletBinding(DefaultParameterSetName = "GitHub")]
param(
    [Parameter(ParameterSetName = "GitHub")]
    [string] $Repository = "IoTSharp/SonnetDB",

    [Parameter(Mandatory = $true, ParameterSetName = "Fixture")]
    [string] $FixturePath,

    [ValidateRange(7, 100)]
    [int] $RequiredRunCount = 7,

    [string] $OutputPath = "",

    [switch] $AllowNotReady
)

$ErrorActionPreference = "Stop"

function Test-ObjectProperty {
    param(
        [object] $InputObject,
        [string] $Name
    )

    return $null -ne $InputObject -and $null -ne $InputObject.PSObject.Properties[$Name]
}

function Test-IntegerValue {
    param([object] $Value)

    return $Value -is [byte] `
        -or $Value -is [sbyte] `
        -or $Value -is [int16] `
        -or $Value -is [uint16] `
        -or $Value -is [int32] `
        -or $Value -is [uint32] `
        -or $Value -is [int64] `
        -or $Value -is [uint64]
}

function Test-NumericValue {
    param([object] $Value)

    return (Test-IntegerValue $Value) `
        -or $Value -is [single] `
        -or $Value -is [double] `
        -or $Value -is [decimal]
}

function Test-ArrayValue {
    param([object] $Value)

    return $Value -is [array]
}

function Test-RequiredStringProperty {
    param(
        [object] $InputObject,
        [string] $Name
    )

    return (Test-ObjectProperty $InputObject $Name) `
        -and $InputObject.PSObject.Properties[$Name].Value -is [string] `
        -and -not [string]::IsNullOrWhiteSpace([string]$InputObject.PSObject.Properties[$Name].Value)
}

function Add-EvidenceIssue {
    param(
        [System.Collections.Generic.List[object]] $Issues,
        [string] $Code,
        [string] $Message
    )

    $Issues.Add([ordered]@{ code = $Code; message = $Message })
}

function ConvertTo-UtcDateTimeOffset {
    param([object] $Value)

    if ($Value -is [DateTimeOffset]) {
        return $Value.ToUniversalTime()
    }

    if ($Value -is [DateTime]) {
        return ([DateTimeOffset]$Value).ToUniversalTime()
    }

    return [DateTimeOffset]::Parse(
        [string]$Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}

function Invoke-GhApiJson {
    param([string] $Endpoint)

    $jsonLines = @(& gh api $Endpoint)
    if ($LASTEXITCODE -ne 0) {
        throw "gh api failed for '$Endpoint'."
    }

    return ($jsonLines -join [Environment]::NewLine) | ConvertFrom-Json
}

function Read-DownloadedProfile {
    param(
        [string] $RepositoryName,
        [string] $RunId,
        [string] $Profile,
        [string] $DownloadRoot
    )

    $profileRoot = Join-Path $DownloadRoot $Profile
    New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null

    $downloadOutput = @(& gh run download $RunId `
        --repo $RepositoryName `
        --name "parity-$Profile-reports" `
        --dir $profileRoot 2>&1)
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject][ordered]@{
            artifactPresent = $false
            summaryFileCount = 0
            parseError = ($downloadOutput -join [Environment]::NewLine)
            summary = $null
            rawReportFileCount = 0
            rawReports = @()
        }
    }

    $summaryFiles = @(Get-ChildItem -LiteralPath $profileRoot -Filter "summary.json" -File -Recurse)
    $summary = $null
    $summaryParseError = ""
    if ($summaryFiles.Count -eq 1) {
        if ($summaryFiles[0].Length -eq 0) {
            $summaryParseError = "summary.json is empty"
        }
        else {
            try {
                $summary = Get-Content -Raw -LiteralPath $summaryFiles[0].FullName | ConvertFrom-Json
            }
            catch {
                $summaryParseError = $_.Exception.Message
            }
        }
    }

    $rawRoot = Join-Path $profileRoot "raw"
    $rawReportFiles = if (Test-Path -LiteralPath $rawRoot) {
        @(Get-ChildItem -LiteralPath $rawRoot -Filter "report.json" -File -Recurse | Sort-Object FullName)
    }
    else {
        @()
    }
    $rawReports = New-Object System.Collections.Generic.List[object]
    foreach ($rawReportFile in $rawReportFiles) {
        $source = [IO.Path]::GetRelativePath($profileRoot, $rawReportFile.FullName).Replace(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        try {
            $rawReport = Get-Content -Raw -LiteralPath $rawReportFile.FullName | ConvertFrom-Json
            $scenarioCount = if ((Test-ObjectProperty $rawReport "scenarios") -and (Test-ArrayValue $rawReport.scenarios)) {
                @($rawReport.scenarios).Count
            }
            else {
                -1
            }
            $rawReports.Add([pscustomobject][ordered]@{
                source = $source
                parseError = ""
                runId = if (Test-ObjectProperty $rawReport "runId") { [string]$rawReport.runId } else { "" }
                scenarioCount = $scenarioCount
            })
        }
        catch {
            $rawReports.Add([pscustomobject][ordered]@{
                source = $source
                parseError = $_.Exception.Message
                runId = ""
                scenarioCount = -1
            })
        }
    }

    return [pscustomobject][ordered]@{
        artifactPresent = $true
        summaryFileCount = $summaryFiles.Count
        parseError = $summaryParseError
        summary = $summary
        rawReportFileCount = $rawReportFiles.Count
        rawReports = $rawReports.ToArray()
    }
}

function Get-GitHubScheduledRuns {
    param(
        [string] $RepositoryName,
        [int] $RunCount
    )

    if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI 'gh' is required for online evidence verification."
    }

    $endpoint = "repos/$RepositoryName/actions/workflows/parity.yml/runs?event=schedule&status=completed&per_page=$RunCount"
    $response = Invoke-GhApiJson $endpoint
    $workflowRuns = @($response.workflow_runs | Select-Object -First $RunCount)
    $downloadRoot = Join-Path ([IO.Path]::GetTempPath()) ("sonnetdb-parity-nightly-" + [Guid]::NewGuid().ToString("N"))

    try {
        $normalizedRuns = New-Object System.Collections.Generic.List[object]
        foreach ($workflowRun in $workflowRuns) {
            $runRoot = Join-Path $downloadRoot ([string]$workflowRun.id)
            $profiles = [ordered]@{}
            foreach ($profile in @("light", "full")) {
                $profiles[$profile] = Read-DownloadedProfile `
                    -RepositoryName $RepositoryName `
                    -RunId ([string]$workflowRun.id) `
                    -Profile $profile `
                    -DownloadRoot $runRoot
            }

            $normalizedRuns.Add([pscustomobject][ordered]@{
                runId = [string]$workflowRun.id
                event = [string]$workflowRun.event
                conclusion = [string]$workflowRun.conclusion
                createdAtUtc = (ConvertTo-UtcDateTimeOffset $workflowRun.created_at).ToString("o")
                commitSha = [string]$workflowRun.head_sha
                url = [string]$workflowRun.html_url
                profiles = [pscustomobject]$profiles
            })
        }

        return $normalizedRuns.ToArray()
    }
    finally {
        if (Test-Path -LiteralPath $downloadRoot) {
            Remove-Item -LiteralPath $downloadRoot -Recurse -Force
        }
    }
}

function Get-RunSortValue {
    param([object] $Run)

    try {
        return (ConvertTo-UtcDateTimeOffset $Run.createdAtUtc).UtcTicks
    }
    catch {
        return [long]::MinValue
    }
}

function Test-ParityProfileEvidence {
    param(
        [object] $Run,
        [string] $Profile,
        [string] $RepositoryName
    )

    $issues = New-Object System.Collections.Generic.List[object]
    $profileEvidence = $null
    if (Test-ObjectProperty $Run "profiles") {
        $profileProperty = $Run.profiles.PSObject.Properties[$Profile]
        if ($null -ne $profileProperty) {
            $profileEvidence = $profileProperty.Value
        }
    }

    if ($null -eq $profileEvidence) {
        $issues.Add([ordered]@{ code = "profile_artifact_missing"; message = "The $Profile artifact is missing." })
        return [pscustomobject][ordered]@{
            profile = $Profile
            valid = $false
            status = "missing"
            totalScenarios = 0
            issues = $issues.ToArray()
        }
    }

    if (-not (Test-ObjectProperty $profileEvidence "artifactPresent") -or $profileEvidence.artifactPresent -ne $true) {
        $issues.Add([ordered]@{ code = "profile_artifact_missing"; message = "The $Profile artifact could not be downloaded." })
    }

    $hasValidSummaryCount = (Test-ObjectProperty $profileEvidence "summaryFileCount") `
        -and (Test-IntegerValue $profileEvidence.summaryFileCount)
    if (-not $hasValidSummaryCount -or [long]$profileEvidence.summaryFileCount -ne 1) {
        $summaryCount = if ($hasValidSummaryCount) { [string]$profileEvidence.summaryFileCount } else { "invalid" }
        $issues.Add([ordered]@{ code = "summary_file_count_invalid"; message = "The $Profile artifact contains $summaryCount summary.json files; expected exactly one." })
    }

    if ((Test-ObjectProperty $profileEvidence "parseError") -and -not [string]::IsNullOrWhiteSpace([string]$profileEvidence.parseError)) {
        $issues.Add([ordered]@{ code = "summary_parse_failed"; message = [string]$profileEvidence.parseError })
    }

    $summary = if (Test-ObjectProperty $profileEvidence "summary") { $profileEvidence.summary } else { $null }
    if ($null -eq $summary) {
        $issues.Add([ordered]@{ code = "summary_missing"; message = "The $Profile summary is empty or unavailable." })
        return [pscustomobject][ordered]@{
            profile = $Profile
            valid = $false
            status = "missing"
            totalScenarios = 0
            issues = $issues.ToArray()
        }
    }

    if (-not (Test-ObjectProperty $summary "schemaVersion") `
        -or -not (Test-IntegerValue $summary.schemaVersion) `
        -or [long]$summary.schemaVersion -ne 2) {
        Add-EvidenceIssue $issues "summary_schema_invalid" "The $Profile summary must use numeric schemaVersion 2."
    }

    foreach ($fieldName in @(
        "label", "message", "color", "profile", "status",
        "repository", "runId", "runNumber", "commitSha", "badgeUrl")) {
        if (-not (Test-RequiredStringProperty $summary $fieldName)) {
            Add-EvidenceIssue $issues "summary_field_invalid" "The $Profile summary field '$fieldName' must be a non-empty string."
        }
    }

    if ((Test-RequiredStringProperty $summary "label") -and [string]$summary.label -ne "parity") {
        Add-EvidenceIssue $issues "summary_label_invalid" "The $Profile summary label must be 'parity'."
    }

    if ((Test-RequiredStringProperty $summary "profile") -and [string]$summary.profile -ne $Profile) {
        Add-EvidenceIssue $issues "summary_profile_mismatch" "The summary profile does not match '$Profile'."
    }

    if ((Test-RequiredStringProperty $summary "repository") -and [string]$summary.repository -ne $RepositoryName) {
        Add-EvidenceIssue $issues "summary_repository_mismatch" "The $Profile summary repository does not match '$RepositoryName'."
    }

    $summaryCommit = if (Test-RequiredStringProperty $summary "commitSha") { [string]$summary.commitSha } else { "" }
    if (-not [string]::IsNullOrWhiteSpace($summaryCommit) -and $summaryCommit -ne [string]$Run.commitSha) {
        Add-EvidenceIssue $issues "summary_commit_mismatch" "The $Profile summary commit '$summaryCommit' does not match run commit '$($Run.commitSha)'."
    }

    $summaryRunId = if (Test-RequiredStringProperty $summary "runId") { [string]$summary.runId } else { "" }
    if (-not [string]::IsNullOrWhiteSpace($summaryRunId) -and $summaryRunId -ne [string]$Run.runId) {
        Add-EvidenceIssue $issues "summary_run_mismatch" "The $Profile summary runId does not match '$($Run.runId)'."
    }

    if (-not (Test-ObjectProperty $summary "generatedAtUtc") -or $null -eq $summary.generatedAtUtc) {
        Add-EvidenceIssue $issues "summary_field_invalid" "The $Profile summary field 'generatedAtUtc' is required."
    }
    else {
        try {
            ConvertTo-UtcDateTimeOffset $summary.generatedAtUtc | Out-Null
        }
        catch {
            Add-EvidenceIssue $issues "summary_generated_at_invalid" "The $Profile summary generatedAtUtc value is invalid."
        }
    }

    $countValues = @{}
    $countsValid = $true
    foreach ($countName in @(
        "totalScenarios", "passedScenarios", "skippedScenarios", "failedScenarios", "warningOnlyScenarios")) {
        $value = if (Test-ObjectProperty $summary $countName) { $summary.PSObject.Properties[$countName].Value } else { $null }
        if (-not (Test-IntegerValue $value) -or [long]$value -lt 0) {
            Add-EvidenceIssue $issues "summary_count_invalid" "The $Profile summary field '$countName' must be a non-negative integer."
            $countsValid = $false
            $countValues[$countName] = 0L
        }
        else {
            $countValues[$countName] = [long]$value
        }
    }

    $totalScenarios = [long]$countValues["totalScenarios"]
    if ($countsValid) {
        if ($totalScenarios -le 0) {
            Add-EvidenceIssue $issues "summary_scenarios_empty" "The $Profile summary does not contain executed scenarios."
        }
        if ($countValues["passedScenarios"] + $countValues["skippedScenarios"] + $countValues["failedScenarios"] -ne $totalScenarios) {
            Add-EvidenceIssue $issues "summary_scenario_counts_inconsistent" "The $Profile passed, skipped, and failed counts do not add up to totalScenarios."
        }
        if ($countValues["warningOnlyScenarios"] -gt $totalScenarios) {
            Add-EvidenceIssue $issues "summary_warning_count_invalid" "The $Profile warningOnlyScenarios count exceeds totalScenarios."
        }
    }

    $passRate = if (Test-ObjectProperty $summary "passRate") { $summary.passRate } else { $null }
    if (-not (Test-NumericValue $passRate) -or [double]$passRate -lt 0 -or [double]$passRate -gt 100) {
        Add-EvidenceIssue $issues "summary_pass_rate_invalid" "The $Profile passRate must be numeric and between 0 and 100."
    }
    elseif ($countsValid -and $totalScenarios -gt 0) {
        $expectedPassRate = [Math]::Round(
            ($countValues["passedScenarios"] + $countValues["skippedScenarios"]) * 100.0 / $totalScenarios,
            2)
        if ([Math]::Abs([double]$passRate - $expectedPassRate) -gt 0.001) {
            Add-EvidenceIssue $issues "summary_pass_rate_mismatch" "The $Profile passRate does not match the scenario counts."
        }
    }

    $summaryStatus = if (Test-RequiredStringProperty $summary "status") { [string]$summary.status } else { "" }
    if ($summaryStatus -notin @("passing", "failing")) {
        Add-EvidenceIssue $issues "summary_status_invalid" "The $Profile summary status is missing or invalid."
    }
    if ($summaryStatus -eq "passing" -and $countsValid -and $countValues["failedScenarios"] -ne 0) {
        Add-EvidenceIssue $issues "passing_summary_has_failed_scenarios" "A passing $Profile summary contains failed scenarios."
    }
    if ($summaryStatus -eq "passing" -and (Test-RequiredStringProperty $summary "color") -and [string]$summary.color -ne "brightgreen") {
        Add-EvidenceIssue $issues "summary_color_mismatch" "A passing $Profile summary must use the brightgreen badge color."
    }
    elseif ($summaryStatus -eq "failing" -and (Test-RequiredStringProperty $summary "color") -and [string]$summary.color -ne "red") {
        Add-EvidenceIssue $issues "summary_color_mismatch" "A failing $Profile summary must use the red badge color."
    }

    $suiteByName = @{}
    $suiteAggregate = [ordered]@{ total = 0L; passed = 0L; skipped = 0L; failed = 0L }
    $suiteCountsValid = $true
    if (-not (Test-ObjectProperty $summary "suites") -or -not (Test-ArrayValue $summary.suites)) {
        Add-EvidenceIssue $issues "summary_suites_invalid" "The $Profile summary suites field must be an array."
        $suites = @()
        $suiteCountsValid = $false
    }
    else {
        $suites = @($summary.suites)
        if ($suites.Count -eq 0) {
            Add-EvidenceIssue $issues "summary_suites_empty" "The $Profile summary must contain at least one suite."
            $suiteCountsValid = $false
        }
    }

    foreach ($suite in $suites) {
        $suiteName = if (Test-RequiredStringProperty $suite "suite") { [string]$suite.suite } else { "" }
        if ([string]::IsNullOrWhiteSpace($suiteName)) {
            Add-EvidenceIssue $issues "summary_suite_field_invalid" "A $Profile suite has an invalid suite name."
            $suiteCountsValid = $false
        }
        elseif ($suiteByName.ContainsKey($suiteName)) {
            Add-EvidenceIssue $issues "summary_suite_duplicate" "The $Profile summary contains duplicate suite '$suiteName'."
            $suiteCountsValid = $false
        }
        else {
            $suiteByName[$suiteName] = $suite
        }

        if (-not (Test-RequiredStringProperty $suite "source")) {
            Add-EvidenceIssue $issues "summary_suite_field_invalid" "Suite '$suiteName' has an invalid source."
            $suiteCountsValid = $false
        }

        $suiteValues = @{}
        $currentSuiteCountsValid = $true
        foreach ($suiteCountName in @("total", "passed", "skipped", "failed")) {
            $suiteValue = if (Test-ObjectProperty $suite $suiteCountName) { $suite.PSObject.Properties[$suiteCountName].Value } else { $null }
            if (-not (Test-IntegerValue $suiteValue) -or [long]$suiteValue -lt 0) {
                Add-EvidenceIssue $issues "summary_suite_count_invalid" "Suite '$suiteName' field '$suiteCountName' must be a non-negative integer."
                $suiteCountsValid = $false
                $currentSuiteCountsValid = $false
                $suiteValues[$suiteCountName] = 0L
            }
            else {
                $suiteValues[$suiteCountName] = [long]$suiteValue
            }
        }
        if ($currentSuiteCountsValid) {
            if ($suiteValues["passed"] + $suiteValues["skipped"] + $suiteValues["failed"] -ne $suiteValues["total"]) {
                Add-EvidenceIssue $issues "summary_suite_counts_inconsistent" "Suite '$suiteName' counts do not add up to its total."
                $suiteCountsValid = $false
            }
            foreach ($suiteCountName in @("total", "passed", "skipped", "failed")) {
                $suiteAggregate[$suiteCountName] += $suiteValues[$suiteCountName]
            }
        }
    }

    if ($countsValid -and $suiteCountsValid) {
        foreach ($mapping in @(
            @("total", "totalScenarios"),
            @("passed", "passedScenarios"),
            @("skipped", "skippedScenarios"),
            @("failed", "failedScenarios"))) {
            if ($suiteAggregate[$mapping[0]] -ne $countValues[$mapping[1]]) {
                Add-EvidenceIssue $issues "summary_suite_aggregate_mismatch" "The $Profile suite aggregate '$($mapping[0])' does not match '$($mapping[1])'."
            }
        }
    }

    if (-not (Test-ObjectProperty $summary "gateFailures") -or -not (Test-ArrayValue $summary.gateFailures)) {
        Add-EvidenceIssue $issues "gate_failures_invalid" "The $Profile summary gateFailures field must be an array."
        $gateFailures = @()
    }
    else {
        $gateFailures = @($summary.gateFailures)
        foreach ($gateFailure in $gateFailures) {
            foreach ($fieldName in @("gate", "suite", "scenario", "reason")) {
                if (-not (Test-RequiredStringProperty $gateFailure $fieldName)) {
                    Add-EvidenceIssue $issues "gate_failure_field_invalid" "A $Profile gate failure has an invalid '$fieldName' field."
                }
            }
            if (-not (Test-RequiredStringProperty $gateFailure "gap_reason")) {
                Add-EvidenceIssue $issues "gate_failure_gap_reason_missing" "A $Profile gate failure does not contain gap_reason."
            }
        }
    }

    if ($summaryStatus -eq "passing" -and $gateFailures.Count -ne 0) {
        Add-EvidenceIssue $issues "passing_summary_has_gate_failures" "A passing $Profile summary contains gate failures."
    }
    elseif ($summaryStatus -eq "failing" -and $gateFailures.Count -eq 0) {
        Add-EvidenceIssue $issues "failing_summary_has_no_gate_failure" "A failing $Profile summary does not explain the failure."
    }

    if (-not (Test-ObjectProperty $summary "performanceWarnings") -or -not (Test-ArrayValue $summary.performanceWarnings)) {
        Add-EvidenceIssue $issues "performance_warnings_invalid" "The $Profile summary performanceWarnings field must be an array."
        $performanceWarnings = @()
    }
    else {
        $performanceWarnings = @($summary.performanceWarnings)
        foreach ($warning in $performanceWarnings) {
            foreach ($fieldName in @("suite", "scenario", "reason")) {
                if (-not (Test-RequiredStringProperty $warning $fieldName)) {
                    Add-EvidenceIssue $issues "performance_warning_field_invalid" "A $Profile performance warning has an invalid '$fieldName' field."
                }
            }
        }
    }
    if ($countsValid -and $performanceWarnings.Count -ne $countValues["warningOnlyScenarios"]) {
        Add-EvidenceIssue $issues "performance_warning_count_mismatch" "The $Profile performanceWarnings count does not match warningOnlyScenarios."
    }

    $rawReports = @()
    $rawReportFileCountValid = (Test-ObjectProperty $profileEvidence "rawReportFileCount") `
        -and (Test-IntegerValue $profileEvidence.rawReportFileCount) `
        -and [long]$profileEvidence.rawReportFileCount -ge 0
    if (-not $rawReportFileCountValid) {
        Add-EvidenceIssue $issues "raw_report_file_count_invalid" "The $Profile artifact rawReportFileCount must be a non-negative integer."
    }
    if (-not (Test-ObjectProperty $profileEvidence "rawReports") -or -not (Test-ArrayValue $profileEvidence.rawReports)) {
        Add-EvidenceIssue $issues "raw_reports_invalid" "The $Profile artifact rawReports field must be an array."
    }
    else {
        $rawReports = @($profileEvidence.rawReports)
    }
    if ($rawReportFileCountValid -and [long]$profileEvidence.rawReportFileCount -ne $rawReports.Count) {
        Add-EvidenceIssue $issues "raw_report_file_count_mismatch" "The $Profile rawReportFileCount does not match the normalized rawReports count."
    }

    $rawByRunId = @{}
    $rawScenarioTotal = 0L
    $rawCountsValid = $true
    foreach ($rawReport in $rawReports) {
        $rawSource = if (Test-RequiredStringProperty $rawReport "source") { [string]$rawReport.source } else { "" }
        $rawRunId = if (Test-RequiredStringProperty $rawReport "runId") { [string]$rawReport.runId } else { "" }
        if (-not (Test-ObjectProperty $rawReport "parseError") -or $rawReport.parseError -isnot [string]) {
            Add-EvidenceIssue $issues "raw_report_parse_state_invalid" "A $Profile raw report does not contain a string parseError field."
        }
        elseif (-not [string]::IsNullOrWhiteSpace([string]$rawReport.parseError)) {
            Add-EvidenceIssue $issues "raw_report_parse_failed" "Raw report '$rawSource' could not be parsed: $($rawReport.parseError)"
        }
        if ([string]::IsNullOrWhiteSpace($rawSource) -or [string]::IsNullOrWhiteSpace($rawRunId)) {
            Add-EvidenceIssue $issues "raw_report_identity_invalid" "A $Profile raw report is missing source or runId."
        }
        elseif ($rawSource -ne "raw/$rawRunId/report.json") {
            Add-EvidenceIssue $issues "raw_report_path_invalid" "Raw report '$rawSource' does not follow raw/<runId>/report.json."
        }
        elseif ($rawByRunId.ContainsKey($rawRunId)) {
            Add-EvidenceIssue $issues "raw_report_duplicate" "The $Profile artifact contains duplicate raw report runId '$rawRunId'."
        }
        else {
            $rawByRunId[$rawRunId] = $rawReport
        }

        if (-not (Test-ObjectProperty $rawReport "scenarioCount") `
            -or -not (Test-IntegerValue $rawReport.scenarioCount) `
            -or [long]$rawReport.scenarioCount -lt 0) {
            Add-EvidenceIssue $issues "raw_report_scenario_count_invalid" "Raw report '$rawSource' has an invalid scenarioCount."
            $rawCountsValid = $false
        }
        else {
            $rawScenarioTotal += [long]$rawReport.scenarioCount
        }
    }

    foreach ($suiteName in $suiteByName.Keys) {
        if (-not $rawByRunId.ContainsKey($suiteName)) {
            Add-EvidenceIssue $issues "raw_report_missing_for_suite" "Suite '$suiteName' has no matching raw report."
            continue
        }
        $suite = $suiteByName[$suiteName]
        if ((Test-ObjectProperty $suite "total") `
            -and (Test-IntegerValue $suite.total) `
            -and (Test-IntegerValue $rawByRunId[$suiteName].scenarioCount) `
            -and [long]$suite.total -ne [long]$rawByRunId[$suiteName].scenarioCount) {
            Add-EvidenceIssue $issues "raw_suite_count_mismatch" "Raw report '$suiteName' scenarioCount does not match the summary suite total."
        }
    }
    foreach ($rawRunId in $rawByRunId.Keys) {
        if (-not $suiteByName.ContainsKey($rawRunId)) {
            Add-EvidenceIssue $issues "raw_report_unexpected" "Raw report '$rawRunId' has no matching summary suite."
        }
    }
    if ($countsValid -and $rawCountsValid -and $rawScenarioTotal -ne $totalScenarios) {
        Add-EvidenceIssue $issues "raw_summary_count_mismatch" "The $Profile raw scenario total does not match summary totalScenarios."
    }

    if ($summaryStatus -ne "passing") {
        Add-EvidenceIssue $issues "summary_not_passing" "The $Profile summary status is '$summaryStatus'."
    }

    return [pscustomobject][ordered]@{
        profile = $Profile
        valid = $issues.Count -eq 0
        status = $summaryStatus
        totalScenarios = $totalScenarios
        issues = $issues.ToArray()
    }
}

function Test-ParityNightlyEvidence {
    param(
        [object[]] $InputRuns,
        [int] $RunCount,
        [string] $RepositoryName,
        [string] $Source
    )

    $runs = @($InputRuns | Sort-Object { Get-RunSortValue $_ } -Descending | Select-Object -First $RunCount)
    $runReports = New-Object System.Collections.Generic.List[object]
    $reportIssues = New-Object System.Collections.Generic.List[object]

    foreach ($run in $runs) {
        $runIssues = New-Object System.Collections.Generic.List[object]
        if ([string]$run.event -ne "schedule") {
            $runIssues.Add([ordered]@{ code = "run_event_not_scheduled"; message = "Run $($run.runId) was not triggered by schedule." })
        }
        if ([string]$run.conclusion -ne "success") {
            $runIssues.Add([ordered]@{ code = "run_conclusion_not_success"; message = "Run $($run.runId) concluded '$($run.conclusion)'." })
        }
        if ([string]::IsNullOrWhiteSpace([string]$run.commitSha)) {
            $runIssues.Add([ordered]@{ code = "run_commit_missing"; message = "Run $($run.runId) does not record a commit SHA." })
        }

        try {
            $createdAt = ConvertTo-UtcDateTimeOffset $run.createdAtUtc
        }
        catch {
            $createdAt = [DateTimeOffset]::MinValue
            $runIssues.Add([ordered]@{ code = "run_timestamp_invalid"; message = "Run $($run.runId) has an invalid createdAtUtc value." })
        }

        $profiles = @(
            Test-ParityProfileEvidence -Run $run -Profile "light" -RepositoryName $RepositoryName
            Test-ParityProfileEvidence -Run $run -Profile "full" -RepositoryName $RepositoryName
        )
        $valid = $runIssues.Count -eq 0 -and @($profiles | Where-Object { -not $_.valid }).Count -eq 0
        $runReports.Add([pscustomobject][ordered]@{
            runId = [string]$run.runId
            createdAtUtc = $createdAt.ToString("o")
            commitSha = [string]$run.commitSha
            conclusion = [string]$run.conclusion
            url = [string]$run.url
            valid = $valid
            issues = $runIssues.ToArray()
            profiles = $profiles
        })
    }

    if ($runs.Count -lt $RunCount) {
        $reportIssues.Add([ordered]@{
            code = "insufficient_scheduled_runs"
            message = "Found $($runs.Count) completed scheduled runs; $RunCount are required."
        })
    }

    if ($runs.Count -eq $RunCount) {
        for ($index = 1; $index -lt $runReports.Count; $index++) {
            $newerIndex = $index - 1
            $newerRun = $runReports[$newerIndex]
            $olderRun = $runReports[$index]
            $newerDate = (ConvertTo-UtcDateTimeOffset $newerRun.createdAtUtc).UtcDateTime.Date
            $olderDate = (ConvertTo-UtcDateTimeOffset $olderRun.createdAtUtc).UtcDateTime.Date
            $expectedOlderDate = $newerDate.AddDays(-1)
            if ($olderDate.Ticks -ne $expectedOlderDate.Ticks) {
                $reportIssues.Add([ordered]@{
                    code = "scheduled_dates_not_consecutive"
                    message = "Runs $($newerRun.runId) ($($newerDate.ToString('yyyy-MM-dd'))) and $($olderRun.runId) ($($olderDate.ToString('yyyy-MM-dd'))) are not on consecutive UTC dates."
                })
            }
        }
    }

    $validRunCount = @($runReports | Where-Object valid).Count
    $successRate = if ($runReports.Count -eq 0) { 0 } else { [Math]::Round($validRunCount * 100.0 / $runReports.Count, 2) }
    $ready = $runReports.Count -eq $RunCount `
        -and $validRunCount -eq $RunCount `
        -and $reportIssues.Count -eq 0

    if (-not $ready -and @($runReports | Where-Object { -not $_.valid }).Count -gt 0) {
        $reportIssues.Add([ordered]@{
            code = "scheduled_run_failed_validation"
            message = "At least one scheduled run or profile did not pass evidence validation."
        })
    }

    return [pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($ready) { "READY" } else { "NOT_READY" }
        repository = $RepositoryName
        source = $Source
        requiredRunCount = $RunCount
        examinedRunCount = $runReports.Count
        validRunCount = $validRunCount
        successRate = $successRate
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        issues = $reportIssues.ToArray()
        runs = $runReports.ToArray()
    }
}

if ($PSCmdlet.ParameterSetName -eq "Fixture") {
    $fixture = Get-Content -Raw -LiteralPath $FixturePath | ConvertFrom-Json
    $source = "fixture"
    $repositoryName = if (Test-ObjectProperty $fixture "repository") { [string]$fixture.repository } else { "" }
    $inputRuns = @($fixture.runs)
}
else {
    $source = "github"
    $repositoryName = $Repository
    $inputRuns = @(Get-GitHubScheduledRuns -RepositoryName $Repository -RunCount $RequiredRunCount)
}

$report = Test-ParityNightlyEvidence `
    -InputRuns $inputRuns `
    -RunCount $RequiredRunCount `
    -RepositoryName $repositoryName `
    -Source $source
$reportJson = $report | ConvertTo-Json -Depth 16

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $reportJson
}
else {
    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }
    $reportJson | Set-Content -LiteralPath $OutputPath -Encoding utf8
}

if ($report.status -ne "READY" -and -not $AllowNotReady) {
    throw "Parity nightly evidence is NOT_READY. Validated $($report.validRunCount) of $($report.requiredRunCount) required scheduled runs."
}
