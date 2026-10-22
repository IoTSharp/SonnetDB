# Issue #242 XCode BatchReplace consumer audit

This audit validates the native relational `REPLACE` implementation already present at SonnetDB `9faced95d9adddd3e45fe8d009fbefa24a7ef8cc` through a minimal NewLife.XCode provider candidate. No SonnetDB production source, upstream XCode checkout, package, release or remote repository was changed.

## Fixed inputs

- NewLife.XCode PR101 is merged. Official merge commit: `2d90912c299da72b1e1990b4577680eadb1d112e`; head: `9cc1e7e1214a1c3403bbb68d080d47554f18cb22`.
- Official source ZIP SHA256: `51600D8509F109304C06CF8D896DDA79AE70A970B4A6EE77CF4A132BA685ACD8`.
- Original provider file SHA256: `B31FFCC3F5448AB090AA0FECA79F64B2010DAFF04A3BC3A016444FBBEED93781`.
- Original XCode.dll SHA256: `3583C3E6F1B210712DEEE011E8A93E5D2ECEFCE21505CB3FF1ABA3EC51467104`.
- Candidate XCode.dll SHA256: `E1F2A7D9CC236B4B7F5BD8DC8DA323D74546757E50B2FE86CBBCC5B9A375D710`.

The XCode assembly informational version inherited an unrelated enclosing Git SHA while the downloaded source was built outside its own `.git` directory. Source identity is therefore bound to the codeload commit, source-file hash and DLL hash above, not that informational suffix.

## Provider boundary

The original provider omits `BatchCapability.Replace` and throws the #242 guard before sending SQL. The candidate changes only `XCode/DataAccessLayer/Database/SonnetDB.cs`: it advertises Replace and emits parameterized `REPLACE INTO` through the existing batch builder. It does not translate Replace to Upsert and does not simulate delete plus insert on the client.

The zero-context patch passed `git apply --check --unidiff-zero`, was actually applied to a clean official source file, and produced the exact candidate source SHA256 `94C4848D228A78C7A95008214DDEDA4C703E8EE9D5990F6F0AF160856B54EC31`.

## Consumer results

| Path | Original provider | Candidate |
|---|---:|---:|
| Embedded | 2/2 guard baseline | 7/7 |
| REST | 2/2 guard baseline; HTTP/1.1 | 7/7; 53 HTTP/1.1 SQL requests |
| frame-http2 | 2/2 guard baseline | 7/7; 53 HTTP/2 SQL requests; 10 native Frame requests |

The seven candidate checks cover:

1. `BatchCapability.Replace` is advertised.
2. One XCode `BatchReplace()` removes two different unique-key owners, allocates a fresh identity, reapplies the omitted `Value DEFAULT 7`, writes `NULL` for the omitted nullable marker and returns affected count `1` for the one new row.
3. Real AFTER DELETE and AFTER INSERT triggers run in that order; the AFTER UPDATE trigger does not run.
4. A later CHECK failure rolls back the whole XCode batch.
5. FK RESTRICT preserves parent and child.
6. FK CASCADE and SET NULL run before the parent is reinserted.
7. An outer XCode transaction rollback restores the old row.

All explicit frame-http2 requests stayed on HTTP/2. Native Frame was observed for supported operations; transaction lifecycle and other non-Frame operations used HTTP/2 REST endpoints. There was no HTTP/1 fallback and no claim that every operation is binary Frame.

## Package and upstream state

The public `SonnetDB 4.0.0` package (`6343C5740BA26CE95AAD8AAE8F897B79D7199FC2D26A67E9102F08F625BE0C33`) remains negative: the original XCode provider stops at its #242 guard, while the candidate reaches the formal engine and receives `SqlParseException` at position 0 for `REPLACE`. Formal package acceptance therefore fails.

The follow-up XCode patch has not been submitted upstream and no XCode/SonnetDB package was published. `Upstream=NOT_RUN`, `package publication=NOT_RUN`, and Issue #242 remains open. Complete machine evidence is in `D:/source/SonnetDB/.codex-temp/sync-20261019/issue242-consumer-evidence/manifest.json`.
