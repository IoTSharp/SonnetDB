# GH-Issue #187 bounded DDL evolution evidence (2026-09-25)

Source: `https://github.com/IoTSharp/SonnetDB/issues/187`. Repository `origin` is `https://github.com/IoTSharp/SonnetDB.git`. The external issue was open at the 2026-09-25 API read-back.

The current branch supports adding one `INT ROWVERSION` or `INT AUTO_INCREMENT` column to a relation table that already has a primary key. Existing rows receive version 1 or monotonically assigned identities; reopened tables retain generated-column metadata and the next identity. `ALTER TABLE ... ALTER PRIMARY KEY` and `ADD CONSTRAINT pk_<table> PRIMARY KEY` can redefine an empty, unreferenced table, including a composite key. A populated table, inbound foreign key, or unsupported custom primary-key name receives `table_schema_evolution_unsupported`. Existing defaults, CHECK constraints, indexes and DECIMAL metadata survive the tested transforms. Embedded and real remote ADO schema refresh is covered.

The issue's exact no-primary-key `CREATE TABLE devices (code STRING NOT NULL)` example still fails: this rowstore requires a primary key at creation. Changing a primary key with existing data also requires a separate migration path. These are explicit limits, so this report does not close the issue or claim general ORM CodeFirst migrations.

Verification on the integrated working tree:

| Command filter | Result | Scope |
| --- | --- | --- |
| `SqlSafeDdlEvolutionTests|SqlLockingReadContractTests|SqlUpdateDeleteReturning194Tests` on `SonnetDB.Core.Tests`, rebuilt | 28/28 passed | Includes #187 DDL, #190 and #194 integration checks |
| `RemoteDdlEvolutionTests|RemoteLockingReadContractTests` on `SonnetDB.Tests`, real Kestrel | 7/7 passed | Embedded/remote DDL and locking-read contract |

The first combined Core run failed 2/23 because two #187 tests expected the former `InvalidOperationException` for duplicate primary keys. The #197 stable error-code change now throws `TableConstraintException` with `UniqueViolation`; those assertions were updated and the rebuilt combined run passed. This is an integration correction, not evidence of a failed DDL mutation.
