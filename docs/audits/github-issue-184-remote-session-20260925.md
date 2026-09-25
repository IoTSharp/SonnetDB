# GH-Issue #184 remote transaction session evidence (2026-09-25)

Repository: `https://github.com/IoTSharp/SonnetDB.git`. The isolated `codex/issue-184-remote-tx` commit was integrated and pushed to `main` as `7042b6f5`. This extends the earlier [conditional UPSERT audit](github-issue-184-conditional-upsert-20260925.md). The public Issue remains open pending its release-version and deployment review.

## Contract

- New Server exposes `POST /v1/db/{db}/sql/transactions`, `POST /{id}/sql`, `POST /{id}/commit`, `POST /{id}/rollback`, and `GET /{id}` under that path. All routes require normal authentication and database permission. Session ID is opaque and checked against the database instance, database name, and a hash of the authorization header. Calls for one ID serialize through a semaphore.
- ADO creates a server session at `BeginTransaction`. Statements execute in one `SqlTransactionContext`; `DO UPDATE ... RETURNING` returns rows from its actual buffered write. Commit does not replay SQL. Conditional misses return zero rows and zero affected count. A concurrent committed update can reject commit through the engine's optimistic version check rather than silently changing already returned rows.
- A session expires after 2 minutes without an operation; expiry rolls back uncommitted work. Maximum active sessions: 128. Terminal `commit`/`rollback` states remain queryable for 2 minutes; repeated same terminal action returns 204 without executing again. Retained-state cap: 8192.
- An old Server returning 404 from the create endpoint uses the existing client preview/replay transaction path. That path still rejects `DO UPDATE ... RETURNING` before enqueue. Published `v3.1.0` does not include relational `ON CONFLICT` or this protocol. Current unpublished `main` contains both the local UPSERT and remote session. The first released version for each capability awaits release and compatibility decisions, including the `TokenKind` contract; no package-version claim is made here.

## Validation

| Command | Result | Scope |
| --- | --- | --- |
| `dotnet build src/SonnetDB/SonnetDB.csproj -m:1 --nologo` | PASS, 0 warnings | Server and Core |
| `dotnet build src/SonnetDB.Data/SonnetDB.Data.csproj -m:1 --nologo` | PASS, 0 warnings | Remote provider |
| `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -m:1 --filter 'FullyQualifiedName~SqlOnConflictDoUpdateReviewTests|FullyQualifiedName~SqlDmlReturningAndConflictTests' --nologo` | PASS, 25/25 | Core conflict and RETURNING regressions |
| `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -m:1 --no-restore --filter 'FullyQualifiedName~RemoteAdoEndToEndTests' --nologo` | PASS, 65/65 | Full real-Kestrel remote ADO class on final tree |
| `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -m:1 --no-restore --filter 'FullyQualifiedName~RemoteTransaction_' --nologo` | PASS, 9/9 | Transaction protocol including expiry, idempotency, auth binding |
| `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -m:1 --no-build --no-restore --nologo` | PASS, 1103/1103 | Complete Server test project after HTTP/2 transport assertion update |
| `dotnet build src/SonnetDB/SonnetDB.csproj -c Release -m:1 --nologo` | PASS, 0 warnings | Server Release build |
| `dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 -p:SonnetDbPublishAot=true -m:1 --nologo` | PASS, no IL/AOT warnings | Server NativeAOT publish; binary runtime not exercised |
| `dotnet publish src/SonnetDB.Cli/SonnetDB.Cli.csproj -c Release -r win-x64 -p:PublishAot=true -m:1 --nologo` | PASS, no IL/AOT warnings | Client NativeAOT publish; binary runtime not exercised |

Focused tests include conflict hit/miss, generated identity, parameter binding, conditional skip, `ROWVERSION`, earlier queued writes, transaction visibility, concurrent update before commit, rollback, credential isolation, terminal readback, and forced lease expiry. These are local real-Kestrel runs, not published-package or deployed-cluster evidence.

The first complete Server run passed 1102/1103: one HTTP/2 test still asserted the old `/sql/batch` route. Its affected-row behavior passed; the route assertion was updated to check session create, statement, and commit over HTTP/2. The focused test then passed 1/1, followed by the complete 1103/1103 run above. The initial failure is retained here rather than presented as a clean first run.

After integration with #187, #189, #191 and #194 on `main`, the related Release Core filter passed 75/75 and the complete Release Server suite passed 1106/1106. `git diff --check` passed before push to `origin/main`. These results are local build and real Kestrel tests, not deployed-cluster or package evidence.

## Remaining boundaries

- A session belongs to one Server process. A load balancer must route subsequent calls to that process; multi-instance routing has not been demonstrated. Server restart drops uncommitted buffers and the terminal cache. If a commit response and readback both fail, the outcome remains unknown; the client reports the session ID for investigation.
- Expiry is tested by forcing a session deadline in the local Kestrel harness. Wall-clock lease passage, process restart during commit, network response loss, package-version compatibility, FreeSql Provider execution, and published NativeAOT binary runtime still require separate evidence. These cannot be inferred from build or mock tests.
