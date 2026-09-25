# GH-Issue #184 conditional UPSERT evidence (2026-09-25)

Repository: `https://github.com/IoTSharp/SonnetDB.git`. Worktree branch: `codex/issue-184-upsert`. This records the bounded `DO UPDATE WHERE` addition to the existing local UPSERT implementation; the external issue remains open.

## Contract

- `DO UPDATE` requires an explicit primary-key or unique-index conflict target. Composite targets retain declared index column order.
- The optional predicate reads the pre-update target row through unqualified columns and the candidate row through `excluded.column`; parameters bind through the existing AST binder. Only SQL TRUE updates. FALSE and NULL skip the update, RETURNING row and affected-row count.
- The direct and queued transaction paths validate the predicate before writing and preserve input order for successful insert/update RETURNING rows. Unsupported qualified names fail before inserting an unrelated candidate.
- Published `v3.1.0` does not include this UPSERT contract. The current branch still rejects `DO UPDATE ... RETURNING` in a remote lightweight transaction, because its preview/replay path cannot preserve update and generated-value semantics. This is not a full GH-Issue #184 closure or a FreeSql compatibility claim.

## Verification

| Command | Result | Scope |
| --- | --- | --- |
| `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -m:1 --no-restore --filter 'FullyQualifiedName~SqlOnConflictDoUpdateReviewTests' --nologo` | 19/19 passed | Parser, direct/queued execution, parameters, skipped updates, RETURNING, no-write failures |
| `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -m:1 --filter 'FullyQualifiedName~Remote_InsertOnConflictDoUpdateWhere_ReportsAffectedRowsAndReturning' --nologo` | 1/1 passed | Real Kestrel and remote ADO.NET async NDJSON request |
| `git diff --check` | passed | Whitespace and conflict markers |

After the focused runs, the combined existing/new conflict regression passed Core 25/25 (`SqlDmlReturningAndConflictTests|SqlOnConflictDoUpdateReviewTests`). The real remote conditional-update and explicit lightweight-transaction rejection tests passed together 2/2. Both used `--no-build --no-restore` against the binaries built above.

The first Server test build failed because this new worktree had uninitialized CoAP/MQTT git submodules. `git submodule update --init --recursive` checked out their pinned revisions, after which the same Server test passed. No source change was made to those submodules.

## Remote lightweight transaction protocol audit

The existing protocol cannot preserve one server transaction across separate ADO calls:

1. `RemoteConnectionImpl.BeginTransaction` in `src/SonnetDB.Data/Remote/RemoteConnectionImpl.cs` creates only a client-side `RemoteTransactionState`; no server transaction is opened.
2. A transactional `ExecuteReader[Async]` with `INSERT ... RETURNING` enters `ExecuteTransactionalInsertReturningRequestAsync` in the same file. `DO UPDATE` is rejected before a schema request, preview, or queue append. Other returning inserts preview with a separate `/sql/batch` request containing `BEGIN`, prior queued SQL, the target SQL, and `ROLLBACK` (`ExecuteTransactionPreviewAsync`).
3. `CommitTransactionAsync` sends a new `/sql/batch` request containing `BEGIN`, queued SQL, and `COMMIT`. The server's `SqlEndpointHandler` in `src/SonnetDB/Endpoints/Handlers/SqlEndpointHandler.cs` holds its `SqlTransactionContext` only inside one request and rolls back an unfinished transaction in `finally`. There is no transaction identifier or endpoint that resumes that context.

A deterministic interleaving shows the result hazard. Starting from `(id=1, value=10, rv=1)`, a preview of `INSERT ... VALUES (1, 20) ON CONFLICT (id) DO UPDATE SET value = value + excluded.value RETURNING value, rv` would return `(30, 2)` and roll back. Another connection can then set `value=100`, giving `(100, 2)`. Replaying the UPSERT at commit would write `(120, 3)`, although ADO already returned `(30, 2)`. A concurrent insert can also switch the insert/update branch; identity/default generation and trigger effects need not repeat. The existing reserved-insert rewrite is limited to its insert path and cannot preserve conditional updates or their `RETURNING` values. The #194 preview-result parser fix is independent of this transaction-lifetime gap.

Three new real-Kestrel cases verify that conflict hit, conflict miss, and generated-identity candidates all throw `NotSupportedException` before queue append. An earlier ordinary insert in the same client transaction still commits, while the rejected candidate makes no change. The combined remote filter (`RemoteTransaction_UpsertReturningRejectedBeforeQueue|RemoteTransaction_InsertOnConflictDoUpdateReturning_IsRejectedUntilParity|Remote_InsertOnConflictDoUpdateWhere_ReportsAffectedRowsAndReturning`) passed 5/5; Core conflict regression passed 25/25. No production path was opened or relabeled as supported.

## Reviewable follow-up design

1. Add a versioned server transaction resource: create returns an opaque ID, database/principal binding, isolation mode and short lease. A statement endpoint serializes calls for that ID and executes against the same `SqlTransactionContext`, returning the actual staged `RETURNING` rows. Concurrent calls to one ID fail or queue within a fixed bound; auth, statement count, row/byte budgets and lease expiry apply to every call.
2. Add idempotent commit and rollback endpoints with explicit terminal states. On lost commit response, report an unknown outcome until the server can read back the terminal decision; never retry the UPSERT as a fresh transaction. Expiry, disconnect and server restart must release or recover context without silently committing. Multi-instance deployment needs an explicit owner route or durable coordination contract.
3. Negotiate protocol capability in the remote provider. Older servers retain the current early `NotSupportedException`; the new path never uses preview/replay for `DO UPDATE`. Acceptance must cover hit/miss and conditional skip, generated identity/`ROWVERSION`, trigger results, concurrent writes between `ExecuteReader` and `Commit`, cancellation, expiry, lost commit response, restart, NativeAOT JSON metadata and real remote transport.

Remaining acceptance: this server transaction protocol, Frame write-boundary verification, deployment/restart evidence, package-version compatibility, and external issue-thread review. The current branch implements a bounded local/remote non-transaction UPSERT contract, not full remote lightweight-transaction parity.
