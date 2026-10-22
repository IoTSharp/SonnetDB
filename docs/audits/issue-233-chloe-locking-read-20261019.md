# Issue #233 Chloe locking-read boundary audit

This audit validates the existing SonnetDB row-lock implementation through a source-only Chloe provider candidate. No SonnetDB production source, upstream Chloe source, release, package or remote repository was changed.

The controlled SonnetDB worktree was clean at `9faced95d9adddd3e45fe8d009fbefa24a7ef8cc` (tree `f8262f5ba497e3f24869a6aab10490a476e09063`). The final remote assemblies report that exact informational version and are also bound by SHA256. A pre-delivery probe build that escaped from the nested worktree to the canonical `0986f92e` checkout was detected, excluded and replaced; it is not used as current-source evidence.

## Boundary

- `NoLock` is not mapped to a normal query or ReadCommitted. The candidate rejects it explicitly before command interception observes SQL.
- Direct single-table `UpdLock` emits `FOR UPDATE`. Without a transaction, the command reaches SonnetDB and the engine rejects it for missing transaction scope.
- Join, `DISTINCT` and grouping with `UpdLock` are rejected by the provider before SQL. The provider also rejects any non-direct source.
- A transaction-held Chloe lock blocks `FOR UPDATE NOWAIT` on the same row, permits a different row, leaves ordinary committed reads unchanged, and is released by rollback.

## Results

| Consumer | Result | Transport evidence |
|---|---|---|
| Candidate embedded lifecycle | 8/8 | Local `SndbConnection` |
| Candidate embedded boundary probe | 5/5 | NoLock and unsupported shapes sent zero SQL |
| Candidate remote REST | 6/6 | 12 SQL requests, all HTTP/1.1 |
| Candidate remote frame-http2 | 6/6 | 12 SQL requests, all HTTP/2; 2 native `/v1/frame` requests |
| Original provider | 1/8 | Ordinary query passed; seven lock cases failed at the original lock-hint guard |
| Candidate plus formal `SonnetDB 4.0.0` | expected failure | `FOR UPDATE` rejected as unsupported by the published engine |

The frame-http2 contract uses native Frame for supported query requests and HTTP/2 REST transaction endpoints for transaction lifecycle and transaction-scoped SQL. No HTTP/1 fallback was observed. The result therefore does not claim an all-Frame transaction protocol.

## Delivery state

The provider-only patch applies cleanly to Chloe `bb7ec8735bd4511c35453eb009f70cc606795703` and the applied file matches the validated candidate byte for byte. It is prepared for review but has not been submitted upstream. `Upstream=NOT_RUN`, `package publication=NOT_RUN`, and formal package acceptance remains failed on `SonnetDB 4.0.0`; Issue #233 must remain open.

The complete external evidence set is `D:/source/SonnetDB/.codex-temp/sync-20261019/issue233-consumer-evidence/manifest.json`. It preserves the positive runs, original-provider failure, formal-package failure, provider patch, probe sources and SHA256 values.
