# M20 Parity nightly evidence

This report records the 2026-08-28 audit of the seven most recent completed
scheduled runs of `.github/workflows/parity.yml`. It is evidence of recovery
progress, not an M20 completion claim.

## Verdict

`NOT_READY`: 3 of 7 scheduled runs passed all evidence checks (42.86%).

The verifier requires seven consecutive UTC dates where both the `light` and
`full` artifacts contain exactly one non-empty schema v2 `summary.json`, the
summary commit and run identifiers match GitHub Actions, and the complete
schema v2 field types and scenario, pass-rate, suite, gate, and warning counts
are internally consistent. Each suite must map one-to-one to an artifact
`raw/<runId>/report.json`; its actual scenario count and the aggregate raw
count must match the summary. Every failing gate must also contain a non-empty
`gap_reason`. `RequiredRunCount` has a hard minimum of seven, so callers may
only widen the evidence window.

| UTC date | Run | Commit | Workflow | Light | Full | Evidence verdict |
|---|---:|---|---|---|---|---|
| 2026-08-27 | [33071879124](https://github.com/IoTSharp/SonnetDB/actions/runs/33071879124) | `0360873` | success | passing | passing | valid |
| 2026-08-26 | [32925204255](https://github.com/IoTSharp/SonnetDB/actions/runs/32925204255) | `e642063` | success | passing | passing | valid |
| 2026-08-25 | [32803489709](https://github.com/IoTSharp/SonnetDB/actions/runs/32803489709) | `f2cad5a` | success | passing | passing | valid |
| 2026-08-24 | [32685178665](https://github.com/IoTSharp/SonnetDB/actions/runs/32685178665) | `a0fefe1` | failure | failing | failing | invalid |
| 2026-08-23 | [32614388339](https://github.com/IoTSharp/SonnetDB/actions/runs/32614388339) | `a7fae42` | failure | failing | failing | invalid |
| 2026-08-22 | [32547559079](https://github.com/IoTSharp/SonnetDB/actions/runs/32547559079) | `b86bf7c` | failure | failing | failing | invalid |
| 2026-08-21 | [32442052820](https://github.com/IoTSharp/SonnetDB/actions/runs/32442052820) | `7546c53` | failure | failing | failing | invalid |

The three successful runs completed both profile jobs through restore, build,
host-side stack readiness, parity gates, reliability gates, schema v2 summary,
artifact upload, and result publication. The four failing runs retained
structured failing summaries with `gap_reason`, but a structured failure is
still a failed nightly and cannot count toward the seven-day gate.

At least four additional consecutive successful scheduled dates are required.
Any intervening failure keeps the rolling seven-run window `NOT_READY`.

## Reproduction

Online verification is read-only and downloads artifacts into a temporary
directory that is removed before exit:

```powershell
pwsh -NoProfile -File tests/SonnetDB.Parity/scripts/verify-parity-nightly-evidence.ps1 `
  -Repository IoTSharp/SonnetDB `
  -OutputPath artifacts/parity-nightly-evidence.json `
  -AllowNotReady
```

`-AllowNotReady` permits writing the audit report; it does not change the
report status. Without that switch, `NOT_READY` terminates with an error so a
caller cannot accidentally treat incomplete evidence as a passing gate.

The deterministic offline contract uses the checked-in fixture and does not
contact GitHub:

```powershell
pwsh -NoProfile -File tests/SonnetDB.Parity/scripts/test-verify-parity-nightly-evidence.ps1
```
