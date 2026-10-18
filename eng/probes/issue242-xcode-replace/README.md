# #242 XCode BatchReplace consumer probe

This directory validates the SonnetDB native `REPLACE` implementation through the merged NewLife.XCode SonnetDB provider. It does not modify SonnetDB production code or the upstream XCode repository.

The official XCode source is fixed at merged PR101 commit `2d90912c299da72b1e1990b4577680eadb1d112e` (head `9cc1e7e1214a1c3403bbb68d080d47554f18cb22`). The codeload ZIP SHA256 is `51600D8509F109304C06CF8D896DDA79AE70A970B4A6EE77CF4A132BA685ACD8`; its `XCode/DataAccessLayer/Database/SonnetDB.cs` SHA256 is `B31FFCC3F5448AB090AA0FECA79F64B2010DAFF04A3BC3A016444FBBEED93781`.

Apply `xcode-batch-replace.patch` from that source root with:

```powershell
git apply --check --unidiff-zero xcode-batch-replace.patch
git apply --unidiff-zero xcode-batch-replace.patch
```

The zero-context patch changes only the SonnetDB provider file. It adds `BatchCapability.Replace`, routes `IDbSession.Replace` through the existing parameterized batch builder, and switches only that path to native `REPLACE INTO`. It does not use `ON CONFLICT`, ordinary Upsert or a client-side delete/insert sequence. Actual apply output matched the validated candidate SHA256 `94C4848D228A78C7A95008214DDEDA4C703E8EE9D5990F6F0AF160856B54EC31` byte for byte.

`XCodeReplaceProbe.cs.txt` performs real XCode `BatchReplace()` calls against embedded, REST and explicit frame-http2 connections. It covers multiple unique owners, omitted-column defaults, fresh identity, affected count as inserted rows, DELETE then INSERT triggers, whole-batch rollback, FK RESTRICT, FK CASCADE/SET NULL and outer transaction rollback. `XCodeFormalProbe.cs.txt` keeps the public `SonnetDB 4.0.0` failure.

Validated matrix:

| Provider / engine | Embedded | REST | frame-http2 |
|---|---:|---:|---:|
| Original XCode provider + source `9faced95` | 2/2 negative baseline | 2/2 negative baseline | 2/2 negative baseline |
| Provider-only candidate + source `9faced95` | 7/7 | 7/7 | 7/7 |

REST captured 53 SQL requests over HTTP/1.1. frame-http2 captured 53 SQL requests, all over HTTP/2, including 10 native `/v1/frame` requests. Transaction and unsupported Frame operations use the current HTTP/2 REST endpoints; no HTTP/1 fallback occurred.

The public `SonnetDB 4.0.0` package SHA256 is `6343C5740BA26CE95AAD8AAE8F897B79D7199FC2D26A67E9102F08F625BE0C33`. The original provider still stops at its #242 guard; the candidate reaches the engine and receives `SqlParseException` at position 0. Therefore package acceptance remains failed. XCode upstream submission of this follow-up patch and package publication are `NOT_RUN`; Issue #242 remains open.

Full logs, source receipts and hashes are stored outside Git at `D:/source/SonnetDB/.codex-temp/sync-20261019/issue242-consumer-evidence/`.
