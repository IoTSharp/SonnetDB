# GH-Issue #174 position acceptance (2026-09-25)

The original request includes `position('ab' IN name)` alongside trim, replace,
substring and length. The earlier closure covered the latter functions but left
position unsupported. The issue was reopened for this gap.

The shared scalar registry now implements standard `position(search IN value)`.
It compares strings ordinally and returns a one-based UTF-16 offset as Int64,
zero when absent, one for an empty search string, and NULL when either input is
NULL. Non-string inputs receive the existing scalar argument diagnostic.
Relation, Document and measurement execution use the same scalar registry;
the acceptance test executes all three model paths. `substring` remains
one-based; trim defaults to .NET whitespace and accepts an optional character
set. The SQL reference records these boundaries.

Release verification on the integrated tree:

- `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SqlStringFunctionTests|FullyQualifiedName~FunctionRegistryTests' --nologo`: 128/128 passed.
- `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SqlStringFunctionTests' --nologo`: 4/4 passed, including Document and measurement queries.

This is local Core evidence. It does not assert a published client package or
deployed remote parity.
