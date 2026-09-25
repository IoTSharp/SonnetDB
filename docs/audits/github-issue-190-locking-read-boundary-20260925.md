# GH-Issue #190 locking-read boundary evidence (2026-09-25)

Source: `https://github.com/IoTSharp/SonnetDB/issues/190`. Repository `origin` is `https://github.com/IoTSharp/SonnetDB.git`. The request permits an explicit optimistic-concurrency product boundary. SonnetDB has no pessimistic row-lock contract: `SELECT ... FOR UPDATE`, `NOWAIT` and `SKIP LOCKED` fail with `sql_locking_read_unsupported` instead of silently running an ordinary SELECT. The documented alternative reads `ROWVERSION`, then updates/deletes with the old version in `WHERE`; callers retry the complete transaction on conflict. No lock granularity, waiting, deadlock resolution or lock-release behavior is claimed.

The Core contract tests cover two-connection competition, absent rows and behavior after commit/rollback. Real embedded/remote ADO, asynchronous read and REST error mapping are covered by the remote tests. The integrated run passed 28/28 Core cases in the combined #187/#190/#194 filter and 7/7 real Kestrel cases in the #187/#190 filter. See `docs/sql-reference.md` and `docs/ado-net.md` for the SQL and provider contract.

This is local and real-loopback evidence. No release package, deployed service or external FreeSql provider verification is implied; the GitHub issue remains open until delivery and online read-back.
