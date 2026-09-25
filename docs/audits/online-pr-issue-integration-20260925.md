# Online PR and issue integration read-back (2026-09-25)

Repository: `https://github.com/IoTSharp/SonnetDB.git` (`origin`). The initial GitHub read-back found seven open dependency PRs (#164-#170) and nine open issues (#184, #187, #189, #190, #191, #194, #195, #197, #198). The implementations were pushed to `main` in `1250a617`, `b8beed14` and `d49fc87c`. Dependabot subsequently closed all seven dependency PRs without a GitHub merge commit because their exact head version changes were already on `main`; the issue acceptance gates below remain separate. See the [PR API read-back and closure timeline](github-pr-queue-20260925.md).

## Item results

| Item | Local result | Outstanding gate |
| --- | --- | --- |
| PR #164-#169 | Each OpenTelemetry 1.18.0 to 1.19.0 version change is on `main`; restore, Server build and observability tests passed. Dependabot closed the superseded PRs, `merged=false`. | Historical Ubuntu/Windows failures and post-push CI remain separate. #164's later 1.19.1 title does not match its 1.19.0 head. |
| PR #170 | Testcontainers 4.14.0 to 4.15.0 is on `main`; five-target build and Docker build-context test passed. Dependabot closed the superseded PR, `merged=false`. | Docker-backed integration and post-push CI are pending. |
| Issue #184 | Conditional `ON CONFLICT DO UPDATE` and real-server fail-closed transaction tests passed. [Evidence](github-issue-184-conditional-upsert-20260925.md). | Remote lightweight transaction `DO UPDATE ... RETURNING` needs a durable server transaction/session protocol; current preview and commit are separate requests. |
| Issue #187 | Generated-column addition and empty-table key change passed. [Evidence](github-issue-187-ddl-evolution-20260925.md). | No-key table creation and populated-table primary-key migration remain unsupported. |
| Issue #189 | Recursive CTE SQL and candidate guard passed Core and real REST/Frame tests. [Evidence](github-issue-189-recursive-cte-20260925.md). | Single-iteration JOIN/subquery peak memory has no hard bound; issue remains partial. |
| Issue #190 | Locking-read rejection and `ROWVERSION` optimistic workflow passed. [Evidence](github-issue-190-locking-read-boundary-20260925.md). | This is an explicit product boundary, not row-lock support; external client delivery is pending. |
| Issue #191 | Multi-join, trigger, constraint rollback, transaction and REST/Frame acceptance passed. [Evidence](issue-191-update-join-20260925.md). | Fixed hardware, deployed service and long-run acceptance are pending. |
| Issue #194 | UPDATE/DELETE RETURNING and concurrent DELETE pre-image validation passed. [Evidence](issue-194-update-delete-returning-20260925.md). | Deployed service and issue-thread confirmation are pending. |
| Issue #195 | INSERT SELECT, same-table snapshot, exact DECIMAL and real transport tests passed. [Evidence](insert-select-195-20260925.md). | Source SELECT peak materialization and published-package/fixed-hardware gates remain open. |
| Issue #197 | INSERT RETURNING order, generated values, metadata and stable errors passed. [Evidence](github-issue-197-insert-returning-20260925.md). | Published 3.1.0 lacks this contract; first release tag and downstream compatibility are pending. |
| Issue #198 | Signed Int64/STRING boundary, typed empty/all-NULL relation results and DATETIME/TIME/BLOB REST/Frame round trips passed. [Evidence](github-issue-198-int64-boundary-20260925.md). | Other query models and old Frame client version negotiation remain open. |

## Integrated verification

| Gate | Result | Scope |
| --- | --- | --- |
| Full Core Release test project | 5196/5196 passed | First combined run exposed eight old expectations for generic duplicate-key errors or nullable primary-key metadata; tests now assert the stable constraint code and declared key nullability. The corrected full run passed. |
| Core final protocol/metadata filter | 53/53 passed | Rebuilt after the final Frame TIME change. |
| Full Server Release test project | 1099/1099 passed | Includes real local Kestrel REST/NDJSON/HTTP2 Frame. The initial run exposed one stale REST DATETIME string assertion; it now verifies typed `DateTime` and the full rerun passed. |
| Server Release analyzer build | 0 warnings, 0 errors | Includes source-generated JSON and trim/AOT analyzers. |
| CLI NativeAOT `win-x64` publish | Passed, 0 warnings, 0 errors | RID-specific restore followed by `/warnaserror` publish to an external temporary directory; published `SonnetDB.Cli.exe --help` exited 0. |
| `git diff --check` | Passed | Whitespace integrity only. |

M20 remains `NOT_READY`: the newest seven scheduled runs were three successes and four failures, and the authenticated seven-run light/full artifact gate is not complete. See the [M20 read-back](m20-nightly-readback-20260925.md). Fixed-target x64/ARM64 reports, real provider/model quality and cost, two-network client evidence, installed-package validation and a new seven-day scheduled window require their specified external environments. Local tests and NativeAOT do not substitute for those gates.
