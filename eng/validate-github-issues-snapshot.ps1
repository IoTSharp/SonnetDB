[CmdletBinding()]
param(
    [string] $SnapshotPath = (Join-Path $PSScriptRoot '..\docs\audits\github-issues-20260923.json')
)

$ErrorActionPreference = 'Stop'
$resolvedSnapshotPath = [IO.Path]::GetFullPath($SnapshotPath)
if (-not [IO.File]::Exists($resolvedSnapshotPath)) {
    throw "GitHub issue snapshot was not found: $resolvedSnapshotPath"
}

$snapshot = Get-Content -Raw -LiteralPath $resolvedSnapshotPath | ConvertFrom-Json
$issues = @($snapshot.issues)
if ($snapshot.schemaVersion -eq '2.0') {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    if ($issues.Count -lt 1 -or $issues.Count -gt 100) { throw 'Expected 1..100 tracked issue records.' }
    if ($snapshot.repository -ne 'IoTSharp/SonnetDB' -or $snapshot.deliveredCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Snapshot must identify the repository and full delivered commit.'
    }
    if ($snapshot.query.excludesPullRequests -ne $true -or $snapshot.query.trackedCount -ne $issues.Count) {
        throw 'Tracked count or pull request exclusion is invalid.'
    }
    $expectedIds = @(@(89, 91) + @(171..198) | Sort-Object)
    if (Compare-Object $expectedIds @($issues.id | Sort-Object)) { throw 'Tracked issue IDs are incomplete or duplicated.' }
    if (Compare-Object $expectedIds @($snapshot.query.trackedIds | Sort-Object)) { throw 'Query issue IDs do not match the records.' }
    $open = @($issues | Where-Object state -eq 'open')
    $closed = @($issues | Where-Object state -eq 'closed')
    if ($open.Count + $closed.Count -ne $issues.Count -or $open.Count -ne $snapshot.query.openCount -or $closed.Count -ne $snapshot.query.closedCount) {
        throw 'Live state counts do not match the issue records.'
    }
    foreach ($issue in $issues) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Snapshot validation exceeded 30 seconds.' }
        if ($issue.url -ne "https://github.com/IoTSharp/SonnetDB/issues/$($issue.id)" -or [string]::IsNullOrWhiteSpace($issue.title) -or @($issue.roadmap).Count -lt 1) {
            throw "Issue $($issue.id) lacks its URL, title or roadmap mapping."
        }
        if ($issue.state -eq 'closed' -and ($issue.stateReason -ne 'completed' -or -not $issue.closedAt)) {
            throw "Closed issue $($issue.id) has no completion timestamp/reason."
        }
        if ($issue.state -eq 'open' -and $null -ne $issue.closedAt) { throw "Open issue $($issue.id) has a closure timestamp." }
    }
    $completed = @($snapshot.completedThisUpdate)
    if ($completed.Count -lt 3 -or $completed.Count -gt $issues.Count) { throw 'Invalid completed scope count.' }
    $seen = @{}
    foreach ($delivery in $completed) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Snapshot validation exceeded 30 seconds.' }
        if ($seen.ContainsKey($delivery.id)) { throw "Duplicate completed scope $($delivery.id)." }
        $seen[$delivery.id] = $true
        $issue = $issues | Where-Object id -eq $delivery.id
        if ($issue.state -ne 'closed' -or $delivery.implementationCommit -notmatch '^[0-9a-f]{8,40}$' -or $delivery.integrationPassed -lt 1) {
            throw "Completed scope $($delivery.id) lacks closed state, commit or integration evidence."
        }
        $evidencePath = Join-Path (Join-Path $PSScriptRoot '..') $delivery.evidence
        if (-not [IO.File]::Exists($evidencePath)) { throw "Missing evidence for issue $($delivery.id)." }
    }
    Write-Output "Validated $($issues.Count) live issue records: $($closed.Count) closed, $($open.Count) open in $resolvedSnapshotPath"
    return
}
if ($snapshot.schemaVersion -ne '1.0') { throw "Unsupported schemaVersion: $($snapshot.schemaVersion)" }
if ($snapshot.query.openCount -ne 25 -or $snapshot.query.closedCount -ne 0) {
    throw "Unexpected GitHub issue counts: open=$($snapshot.query.openCount), closed=$($snapshot.query.closedCount)"
}
if ($issues.Count -ne 25) { throw "Expected 25 issue records, found $($issues.Count)" }

$expectedIds = @((89, 91) + (171..193) | Sort-Object)
$actualIds = @($issues.id | Sort-Object)
if ((Compare-Object -ReferenceObject $expectedIds -DifferenceObject $actualIds).Count -ne 0) {
    throw "Issue IDs do not match the observed 89, 91 and 171..193 set."
}
$updatedIds = @($snapshot.issueMetadata.updatedAtById.psobject.Properties.Name | ForEach-Object { [int] $_ } | Sort-Object)
if ((Compare-Object -ReferenceObject $expectedIds -DifferenceObject $updatedIds).Count -ne 0) {
    throw 'Issue metadata does not contain one updatedAt value for every issue.'
}
if ($snapshot.currentState.commit -ne '9c4de6e9') { throw 'Current issue state must reference commit 9c4de6e9.' }
if ($snapshot.currentState.closedCount -ne 17 -or $snapshot.currentState.openCount -ne 8) {
    throw "Unexpected current issue counts: closed=$($snapshot.currentState.closedCount), open=$($snapshot.currentState.openCount)"
}
$expectedClosed = @(89, 91, 171, 172, 173, 174, 175, 176, 178, 179, 181, 182, 183, 185, 186, 188, 192 | Sort-Object)
$expectedCurrentOpen = @(177, 180, 184, 187, 189, 190, 191, 193 | Sort-Object)
$actualClosed = @($snapshot.currentState.closedImplementedScope | Sort-Object)
$actualCurrentOpen = @($snapshot.currentState.open | Sort-Object)
if ((Compare-Object -ReferenceObject $expectedClosed -DifferenceObject $actualClosed).Count -ne 0) {
    throw 'Current closedImplementedScope does not match the verified closed issue set.'
}
if ((Compare-Object -ReferenceObject $expectedCurrentOpen -DifferenceObject $actualCurrentOpen).Count -ne 0) {
    throw 'Current open issue set does not match the remaining unimplemented issue set.'
}

$seen = @{}
foreach ($issue in $issues) {
    if ($seen.ContainsKey($issue.id)) { throw "Duplicate issue id: $($issue.id)" }
    $seen[$issue.id] = $true
    if ($issue.status -notin @('open', 'implemented_pending_verification', 'closed_implemented_scope')) {
        throw "Unexpected status for issue $($issue.id): $($issue.status)"
    }
    if ([string]::IsNullOrWhiteSpace($issue.title) -or $issue.roadmap.Count -lt 1) {
        throw "Issue $($issue.id) must have a title and roadmap mapping."
    }
}
if (@($snapshot.issueMetadata.labels).Count -ne 0) { throw 'The snapshot expected no labels.' }
if ($null -ne $snapshot.issueMetadata.milestone) { throw 'The snapshot expected no milestone.' }

Write-Output "Validated $($issues.Count) GitHub issue records in $resolvedSnapshotPath"
