# DRAFT: SQL DML contract release readiness (#184 and #197)

Status: **draft, not a release announcement**. Source snapshot: `a9dc71d05ad79e908a0bd6b79f00801ec017d741` on 2026-09-25. The last published GitHub release and local tag are [`v3.1.0`](https://github.com/IoTSharp/SonnetDB/releases/tag/v3.1.0), at `10ec6a44ae1c6fdfa2b3a98c40b08a9dbb6acb60`. No `v4.0.0` tag or package is asserted here.

This single readiness note covers the version boundary shared by [GH-Issue #184](https://github.com/IoTSharp/SonnetDB/issues/184) and [GH-Issue #197](https://github.com/IoTSharp/SonnetDB/issues/197). Their implementations, tests and closure decisions remain separate. The common release question must have one answer so that an ORM does not combine capabilities from incompatible packages.

## Version decision

**Proposed first complete contract release: `4.0.0`, subject to release approval and verification.** The published `3.1.0` package cannot be relabeled with current `main` behavior. A `3.2.0` release is defensible only after restoring the old public `SonnetDB.Sql.TokenKind` numeric values and passing a broader public API and package compatibility check.

The public enum has existing members whose integral values differ between `v3.1.0` and this snapshot. For example, `KeywordFrom` is `48` in `v3.1.0` and `47` in `main`; `KeywordCheck` is `113` and `138`; `KeywordTransaction` is `138` and `135`. Enum constants can be compiled into downstream callers, so this is a compatibility change even though member names still exist. These examples come from evaluating the sequential enum declarations and explicit assignments in `src/SonnetDB.Core/Sql/TokenKind.cs` at the two commits; this is not a complete API or file-format compatibility audit. The proposed major version does not waive those checks or authorize a binary format change.

| Capability | Published `v3.1.0` | Current unpublished `main` | Candidate `4.0.0` claim after gates pass |
| --- | --- | --- | --- |
| Relation `ON CONFLICT` | No relation UPSERT contract | `DO NOTHING` and `DO UPDATE SET ... [WHERE] ... [RETURNING]` implemented | First packaged relation UPSERT contract, if package and deployment verification pass |
| `INSERT ... RETURNING` | Early execution and generated-key return | Full tested result/metadata contract in embedded and remote ADO | First packaged full cross-connection contract, if package verification passes |
| Native SQL Frame writes | Read-only | Read-only; writes return `bad_request` | Remains read-only; do not advertise native Frame DML |
| `Protocol=frame-http2` ADO writes | No claim for this complete result contract | Uses REST SQL over the HTTP/2 connection and NDJSON result metadata | Describe as HTTP/2 REST fallback, never as native Frame write support |
| Generated values in this contract | Early `AUTO_INCREMENT`/`ROWVERSION` support | `AUTO_INCREMENT`, `ROWVERSION` and column defaults visible in returned rows | Same bounded claim; arbitrary computed generated columns remain unsupported |

## Draft release text

> **DRAFT for proposed SonnetDB 4.0.0; do not publish verbatim before the release gates pass.** Relation-table UPSERT now supports `INSERT ... ON CONFLICT (primary_or_unique_key) DO UPDATE SET ... [WHERE ...] [RETURNING ...]` alongside `DO NOTHING`. Conflict targets must match a primary key or unique index, including declared composite-key order. `excluded.column` reads the candidate row. A false or NULL condition skips the write, returned row and affected count. Multi-row results preserve successful input order; defaults and engine-generated `AUTO_INCREMENT`/`ROWVERSION` values are available in returned rows. Embedded and remote ADO transactions use the actual pending write for `DO UPDATE ... RETURNING`; the new Server session protocol avoids preview/replay of this statement.
>
> `INSERT ... RETURNING` provides stable column names, declared types, nullability, schema, affected counts and duplicate-key error codes across embedded ADO and remote REST/NDJSON. `ExecuteScalar` reads the first value, `ExecuteReader` reads all rows and reports the actual affected count, and `ExecuteNonQuery` returns that count; synchronous, asynchronous and parameterized paths share this behavior. An empty source returns no rows and zero affected count. `Protocol=frame-http2` ADO writes use the REST SQL endpoint over HTTP/2; native SQL Frame remains read-only. "Generated columns" here means `AUTO_INCREMENT` and `ROWVERSION`, plus ordinary column defaults, not computed generated expressions.
>
> Published SonnetDB 3.1.0 contains an earlier `INSERT ... RETURNING` implementation but does not contain this complete cross-connection contract or relation-table UPSERT. Do not enable an ORM provider's full `InsertOrUpdate` or rely on this complete `RETURNING` contract when its runtime package/server is 3.1.0. This candidate version also changes public `TokenKind` numeric values relative to 3.1.0; compiled consumers of those enum values require compatibility review and likely recompilation.

## Evidence and release gates

- [Existing #184 audit](github-issue-184-remote-session-20260925.md): integrated Release Core 75/75 and complete real-Kestrel Server 1106/1106; isolated Server/CLI win-x64 NativeAOT publish had no IL/AOT warnings. This is local source validation, not a released package or deployed cluster.
- [Existing #197 audit](github-issue-197-insert-returning-20260925.md): focused Core 36/36, real-service/HTTP2 106/106 and Release build with zero warnings on its isolated tree. Raw REST and HTTP/2 ADO tests cover generated rows, empty metadata, errors and rollback. The complete result contract was not validated against a published package.
- [ ] Select and tag an actual version after reviewing public API, wire and persisted-format compatibility, including all changed `TokenKind` values. If compatibility is repaired for a minor release, revise this proposed `4.0.0` text before publication.
- [ ] Build packages, Server and clients from the same tag; run package-installed embedded, REST/NDJSON and `frame-http2` ADO UPSERT/RETURNING checks, including old/new client-server combinations and documented failure modes. Keep tests against source `main` separate from package evidence.
- [ ] For #184, verify Server affinity across instances, expiry and restart behavior, a lost commit response with terminal readback, and the resulting operator procedure on an actual deployment. The current session is process-local; a restart loses pending buffers and the terminal cache, so an unresolved commit outcome must not be silently retried.
- [ ] Check final release workflow, NativeAOT published binaries, release notes, package and GitHub release availability. Only then name the tagged version as the first **released** full contract in #184/#197 and decide each Issue closure independently.

The protocol and SQL details are documented in [`sql-reference.md`](../sql-reference.md) and [`frame-protocol.md`](../frame-protocol.md). No test, package, tag, external deployment or Issue closure was produced by this documentation commit.
