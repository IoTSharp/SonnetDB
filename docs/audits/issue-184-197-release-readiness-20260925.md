# DRAFT: SQL DML contract release readiness (#184 and #197)

Status: **draft, not a release announcement**. Source snapshot: `a9dc71d05ad79e908a0bd6b79f00801ec017d741` on 2026-09-25. The last published GitHub release and local tag are [`v3.1.0`](https://github.com/IoTSharp/SonnetDB/releases/tag/v3.1.0), at `10ec6a44ae1c6fdfa2b3a98c40b08a9dbb6acb60`. No `v4.0.0` tag or package is asserted here.

Follow-up for #197: the [2026-09-26 package contract audit](issue-197-package-contract-20260926.md) records the expanded installed-package matrix and the official old Server bundle. The [4.0.0 candidate release notes](../releases/4.0.0.md) define the bounded INSERT RETURNING version contract. Neither follow-up closes #184's deployment gates or asserts a public release.

This single readiness note covers the version boundary shared by [GH-Issue #184](https://github.com/IoTSharp/SonnetDB/issues/184) and [GH-Issue #197](https://github.com/IoTSharp/SonnetDB/issues/197). Their implementations, tests and closure decisions remain separate. The common release question must have one answer so that an ORM does not combine capabilities from incompatible packages.

## Version decision

**Proposed first complete contract release: `4.0.0`, subject to release approval and verification.** The published `3.1.0` package cannot be relabeled with current `main` behavior. A `3.2.0` release is defensible only after restoring old public `SonnetDB.Sql.TokenKind` numeric values and missing public constructor/`Deconstruct` signatures, then passing the original package compatibility gate.

The public enum has existing members whose integral values differ between `v3.1.0` and this snapshot. For example, `KeywordFrom` is `48` in `v3.1.0` and `47` in `main`; `KeywordCheck` is `113` and `138`; `KeywordTransaction` is `138` and `135`. Enum constants can be compiled into downstream callers, so this is a compatibility change even though member names still exist. These examples come from evaluating the sequential enum declarations and explicit assignments in `src/SonnetDB.Core/Sql/TokenKind.cs` at the two commits; this is not a complete API or file-format compatibility audit. The proposed major version does not waive those checks or authorize a binary format change.

The .NET SDK package validator confirms the breaking change: with the original `3.0.1` baseline, `dotnet pack` fails with `CP0011` for changed `TokenKind` values and `CP0002` for removed public constructors/`Deconstruct` methods (including `DocumentFullTextIndexDefinition` and SQL AST records). The release-prep branch keeps `EnablePackageValidation=true` but omits this old-major baseline only for `4.0.0` and `4.0.0-*` package versions. The regular `eng/release.ps1 -Tasks nuget` path then passes for a local `4.0.0-issue184197.2` candidate. A `3.2.0-issue184197.1` pack still fails with the original API errors. This is an explicit SemVer major transition, **not** a claim of binary API compatibility. Before `4.0.1`, set `PackageValidationBaselineVersion` to the actually published `4.0.0` so later 4.x packages are compared within their own major line.

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

## Local package and process verification

| Check | Result | Boundary |
| --- | --- | --- |
| Original Core `dotnet pack -c Release -p:Version=4.0.0-issue184197.1` | FAIL: `CP0002` and `CP0011` against fixed `3.0.1` baseline | Preserved breaking-change report; no suppression file |
| `pwsh -File eng/release.ps1 -Tasks nuget -Version 4.0.0-issue184197.2 -OutputRoot artifacts/issue184197/formal-release-2` after the scoped baseline adjustment | PASS: 7/7 local NuGet packages | Local prerelease version and directory only; no publish/tag |
| Core `dotnet pack -c Release -p:Version=3.2.0-issue184197.1` after the adjustment | FAIL: same API baseline errors | Minor release remains blocked |
| `dotnet publish` Server and CLI, `Release`, `win-x64`, NativeAOT enabled | PASS: native executables produced; no IL/AOT warnings in output | Actual Server native binary subsequently exercised; CLI runtime not exercised |
| [`SonnetDB.ReleaseContractSmoke`](../../tests/SonnetDB.ReleaseContractSmoke/Program.cs) restored using only the formal-release-2 local feed | PASS: `project.assets.json` lists `SonnetDB` and `SonnetDB.Core` at `4.0.0-issue184197.2`; embedded and live NativeAOT Server both passed `INSERT RETURNING` order/generated values/affected count and transactional `DO UPDATE RETURNING` commit | No external package repository or tag |
| Same consumer compiled against published NuGet `3.1.0` and connected to the new NativeAOT Server | PASS: rows, order and affected count; before the first row, `GetFieldType(0)` did not match the new declared `long` metadata contract | This older client remains usable for its earlier result behavior; it does **not** gain the new contract merely by connecting to a new Server |
| Current package consumer connected to a Server built from published `v3.1.0` tag `10ec6a44` | PASS: old `INSERT RETURNING` row values; transactional `DO UPDATE RETURNING` raises `NotSupportedException` and leaves the original row unchanged | Old Server was compiled from the release tag locally, not installed from a published Server bundle; old Server has no new transaction-session endpoint |
| Two NativeAOT Server processes with separate data roots and the same database name | Owner A `GET session` 200; non-owner B `GET session` 404 after A staged `DO UPDATE RETURNING` (200, row 20) | Requires routing affinity; no shared session storage |
| Hard-stop owner A and restart with its same data root | Old session 404; previously committed value 10 remained; buffered value 20 absent | Confirms an uncommitted session is not replayed after this restart; does not resolve a lost response after commit |

The disposable smoke project references `SonnetDB` from NuGet, not a project reference. Its connection string is supplied at runtime, so the same executable tests embedded storage or a live Server. The local data roots and package outputs under `artifacts/issue184197/` are ignored build evidence, not release artifacts. The two test Server processes were stopped after verification.

The package consumer was restored and built with:

```powershell
dotnet restore tests/SonnetDB.ReleaseContractSmoke/SonnetDB.ReleaseContractSmoke.csproj -p:SmokePackageVersion=4.0.0-issue184197.2 --source D:/source/SonnetDB-issue184-197-release-prep/artifacts/issue184197/formal-release-2/nuget
dotnet build tests/SonnetDB.ReleaseContractSmoke/SonnetDB.ReleaseContractSmoke.csproj -c Release --no-restore -p:SmokePackageVersion=4.0.0-issue184197.2 -o artifacts/issue184197/consumer-formal-2
dotnet artifacts/issue184197/consumer-formal-2/SonnetDB.ReleaseContractSmoke.dll current 'Data Source=D:/source/SonnetDB-issue184-197-release-prep/artifacts/issue184197/embedded-formal-2'
```

For the remote run, the second argument was `Data Source=sonnetdb+http://127.0.0.1:64517/release_test;Token=release_test_token;Timeout=30`, with a local NativeAOT Server listening on that port. The old-client binary was restored/built from published `SonnetDB` NuGet `3.1.0` and used the `legacy` scenario against the same Server. A separately compiled `v3.1.0` Server on port 64519 exercised the current consumer's `old-server` scenario. NativeAOT commands were `dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 -p:SonnetDbPublishAot=true -p:BuildAdminUi=false` and `dotnet publish src/SonnetDB.Cli/SonnetDB.Cli.csproj -c Release -r win-x64 -p:PublishAot=true`; both succeeded after initializing the pinned git submodules. The initial Server publish failed before compilation because this fresh worktree had not initialized those submodules, so it is not counted as an AOT warning or code regression.

## Evidence and release gates

- [Existing #184 audit](github-issue-184-remote-session-20260925.md): integrated Release Core 75/75 and complete real-Kestrel Server 1106/1106; isolated Server/CLI win-x64 NativeAOT publish had no IL/AOT warnings. This is local source validation, not a released package or deployed cluster.
- [Existing #197 audit](github-issue-197-insert-returning-20260925.md): focused Core 36/36, real-service/HTTP2 106/106 and Release build with zero warnings on its isolated tree. Raw REST and HTTP/2 ADO tests cover generated rows, empty metadata, errors and rollback. The complete result contract was not validated against a published package.
- [ ] Select and tag an actual version after reviewing public API, wire and persisted-format compatibility, including all changed `TokenKind` values and removed public signatures. If compatibility is repaired for a minor release, revise this proposed `4.0.0` text before publication.
- [ ] Build packages, Server and clients from the same tag; run package-installed embedded, REST/NDJSON and `frame-http2` ADO UPSERT/RETURNING checks, including the published old Server bundle. Keep the local prerelease package and old-tag source-build evidence above separate from actual released-package evidence.
- [ ] For #184, specify and test affinity routing in an actual multi-instance deployment, wall-clock expiry, process restart during commit, a lost commit response with terminal readback, and the operator procedure for an unknown outcome. The current session is process-local; a restart loses pending buffers and the terminal cache, so an unresolved commit outcome must not be silently retried. The local A/B and pre-commit hard-stop checks above do not cover these scenarios.
- [ ] Check final release workflow, NativeAOT published binaries, release notes, package and GitHub release availability. Only then name the tagged version as the first **released** full contract in #184/#197 and decide each Issue closure independently.

The protocol and SQL details are documented in [`sql-reference.md`](../sql-reference.md) and [`frame-protocol.md`](../frame-protocol.md). The tests and prerelease packages above are local evidence only; no public tag, published package, external deployment or Issue closure was produced.
