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
