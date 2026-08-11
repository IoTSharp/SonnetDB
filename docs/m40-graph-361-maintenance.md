# M40 #361 Graph Maintenance Contract

## Scope

The Graph V1 adjacency layout remains one immutable projection key per edge and
an empty value. A supernode is never represented by one value containing its
whole edge list. On KV checkpoint and compaction, state files use KV state v5
front-coded keys with a fixed 16-entry restart interval. This compresses the
repeated graph family, direction, anchor and label bytes without changing the
Graph V1 key bytes or logical range order. KV state v1-v4 remains readable.

`GraphReadSession.Expand` and traversal consume `KvRangeCursor` pages.
`GraphCursorOptions.PageSize`, `MaxPageBytes` and `MaxResults` are hard limits:
the cursor owns only the current page and one pending KV entry, and a supernode
does not cause its complete adjacency to be materialized.

## Resumable maintenance

`GraphStore.RunMaintenance(GraphMaintenanceOptions)` executes a bounded number
of work units. Each work unit scans one range page, applies at most that page's
derived mutations, and releases the Graph commit gate before the next unit.
The current phase, continuation key, counters, unique declarations, and
operation ID are stored in `maintenance.sdbgraph` using an atomic temporary
file, CRC32, write-through flush, and directory fsync.

The phase order is:

1. rebuild vertex and edge derived entries;
2. remove stale adjacency/label/property entries;
3. collect and validate unique declarations, then rebuild unique owners;
4. remove stale unique owners;
5. checkpoint, and optionally compact.

The sidecar is saved only after the mutation batch has been applied and the WAL
has been synchronized for maintenance. A cancellation or process exit can
repeat the last page idempotently. A malformed sidecar is rejected; it is never
silently discarded or replaced with a new repair source. Supplied unique
declarations are therefore still available after a crash even when every
corresponding unique owner key was lost.

`CheckpointEveryWorkUnits` limits WAL/generation pressure during a long repair.
`CompactOnCompletion` is opt-in because compaction is an I/O hotspot. The
standalone `GraphStore.Checkpoint()` and `GraphStore.Compact()` methods expose
the same explicit maintenance boundary. `MaxMutationsPerWorkUnit` also checks a
single record's worst-case label/property projection before building mutations,
so a pathological record fails with a resumable sidecar instead of allocating
an unbounded repair batch.

## Statistics

`GraphReadSession.RefreshStatistics(GraphStatisticsRefreshOptions)` streams the
outgoing adjacency family by anchor and retains only the current degree. Zero
degree is derived from the vertex count, so degree statistics do not allocate a
dictionary entry for every vertex. A maximum scanned-entry budget and aggregate
statistic-group budget fail explicitly instead of allowing high-cardinality
value fingerprints to grow without bound.

## Evidence

- `GraphMaintenanceTests.Maintenance_CancelAndReopen_ResumesFromDurablePageAndKeepsUniqueSource`
- `GraphMaintenanceTests.Maintenance_CorruptManifest_RejectsResumeWithoutStartingOver`
- `GraphMaintenanceTests.Statistics_SupernodeDegree_IsStreamedWithExplicitGroupBudget`
- `GraphMaintenanceTests.AdjacencyCheckpoint_UsesRestartedPrefixCompression_AndRoundTrips`
- `GraphMaintenanceTests.SupernodeExpansion_OneHundredThousandEdges_ReturnsBoundedPages`
- `KvStateFileTests.OpenDiskState_V4UncompressedFile_RemainsReadable`

These are functional and recovery proofs. Fixed target hardware, seven-day
mixed workload, and the external capacity gate remain #367 evidence and are
not claimed by this document.
