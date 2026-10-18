# #233 Chloe locking-read consumer probe

This directory preserves the source-only Chloe provider candidate and the real consumer probes used against SonnetDB locking reads. It does not modify SonnetDB production code or the upstream Chloe checkout.

The upstream Chloe source is fixed at `bb7ec8735bd4511c35453eb009f70cc606795703` (ZIP SHA256 `D9472014BE634DDE773696C8F891BBC96E40F1616B2E8239B36C74DBD575BBCE`). Apply the zero-context patch from that source root with `git apply --unidiff-zero chloe-locking-read.patch`. The patch changes only `src/Chloe.SonnetDB/SqlGenerator.cs`:

- `LockType.NoLock` is explicitly rejected before any SQL is sent. An ordinary ReadCommitted query is not treated as NoLock.
- A direct single-table `LockType.UpdLock` query emits `FOR UPDATE`.
- Join, `DISTINCT`, grouping, derived-source and other lock shapes are explicitly rejected.

The patch was generated with `git diff --unified=0`, checked with `git apply --check --unidiff-zero`, actually applied with `git apply --unidiff-zero` to a clean copy of the fixed upstream file, and produced the exact candidate SHA256 `CFB9744B5395D46B16154AE8626CB586C3A6C5B4290A714C90C0104A885527EE`.

`LockingChloe.cs.txt` is the original eight-case consumer source. `BoundaryProbe.cs.txt` adds the provider boundary checks. `RemoteChloeProbe.cs.txt` starts an owned random-port Server and validates REST plus explicit frame-http2 while recording the actual HTTP version and `/v1/frame`. `FormalPackageProbe.cs.txt` retains the public `SonnetDB 4.0.0` failure.

Build the current source projects first, build the fixed Chloe core and either the original or patched provider, then pass their output paths explicitly:

```powershell
dotnet build LockingChloe.csproj -c Release `
  -p:ChloeCoreBin=D:/path/to/Chloe/bin/Release/net10.0 `
  -p:ChloeProviderBin=D:/path/to/Chloe.SonnetDB/bin/Release/net10.0
```

The validated results were:

| Path | Result |
|---|---|
| Candidate embedded lifecycle | 8/8 |
| Candidate embedded boundaries | 5/5 |
| Candidate REST | 6/6; HTTP/1.1 |
| Candidate frame-http2 | 6/6; HTTP/2; native `/v1/frame` observed |
| Original provider | 1/8; all UpdLock paths rejected |
| Candidate with formal SonnetDB 4.0.0 | expected failure: locking reads unsupported |

The frame-http2 run observed native frame requests. Transaction creation, transaction SQL and rollback use the current HTTP/2 REST transaction endpoints; no HTTP/1 fallback occurred. This is not a claim that every operation uses the binary Frame endpoint.

Full logs, process receipts, hashes and the evidence manifest are outside Git at `D:/source/SonnetDB/.codex-temp/sync-20261019/issue233-consumer-evidence/`. Upstream submission, package publication and formal package acceptance remain `NOT_RUN`.
