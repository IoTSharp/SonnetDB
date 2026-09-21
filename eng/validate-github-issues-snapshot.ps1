[CmdletBinding()]
param(
    [string] $SnapshotPath = (Join-Path $PSScriptRoot '..\docs\audits\github-issues-20260921.json')
)

$ErrorActionPreference = 'Stop'
$resolvedSnapshotPath = [IO.Path]::GetFullPath($SnapshotPath)
if (-not [IO.File]::Exists($resolvedSnapshotPath)) {
    throw "GitHub issue snapshot was not found: $resolvedSnapshotPath"
}

$snapshot = Get-Content -Raw -LiteralPath $resolvedSnapshotPath | ConvertFrom-Json
$issues = @($snapshot.issues)
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
