# GH-Issue #178 set-operation precedence (2026-09-25)

The previous implementation evaluated all set operations from left to right.
That makes `A UNION B INTERSECT C` behave as `(A UNION B) INTERSECT C`, contrary
to standard SQL precedence. The issue was reopened for this observable gap.

Execution now evaluates consecutive INTERSECT operands as a group before
applying UNION, UNION ALL or EXCEPT. Operators at the same precedence remain
left associative. Result ordering and pagination still apply after the full
set expression. The regression distinguishes both UNION and EXCEPT from the
old left-associative result and retains column-count diagnostics.

Release verification on the integrated tree:

- `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SqlSetOperationTests' --nologo`: 4/4 passed.
- `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --no-restore --nologo`: 1109/1109 passed before the precedence change; this is not a post-change full-server claim.

`INTERSECT ALL` and `EXCEPT ALL` remain unsupported, as documented in the SQL
reference. These tests are local Core evidence, not a fixed-hardware gate.

## Completion follow-up

The original issue's acceptance also requires input order for `UNION ALL`,
`NULL`/duplicate/multicolumn equality, invalid type diagnostics, and a large
input memory policy. The follow-up validates column types from branch metadata
even for empty results, inferring non-`NULL` value types when metadata is
unavailable. Differing known types fail with a column number and an explicit
`CAST` hint; this prevents lossy implicit numeric coercion. Decimal values
remain `decimal` during set equality and hashing, so distinct high-precision
values cannot collapse through a `double` conversion. The first branch
supplies output names, and the combined result keeps inferred type metadata.

Set results reserve an estimated row budget and hash-state budget through the
existing per-query/database blocking-operator resource manager. Exhaustion
rejects the query with a set-operation error; this operator does not spill.
Reservation is released on success and exception. The estimate is an admission
limit after branch execution, not a strict CLR heap-peak limit.

- Final `SqlSetOperationTests`: 11/11 passed (Release, local Core).
- Related CTE, recursive CTE, INSERT SELECT and blocking-operator spill tests:
  83/83 passed (Release, local Core).
- Final integrated Core suite: 5233/5233 passed (Release, local Core,
  `--no-build --no-restore`).
- Full Core first pass before the final decimal comparison and extra test cases:
  5229/5230 passed. The only failure was an unrelated socket-disposal race in
  `KvRedirectTests.Create_WithMixedRedirectPolicies`; both variants passed in
  the final 13-test focused run (11 set-operation cases plus 2 KV reruns).
  The full-suite count is not presented as a final-tree full pass.
