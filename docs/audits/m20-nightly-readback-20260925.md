# M20 scheduled parity read-back (2026-09-25)

The source is the live GitHub Actions API for `IoTSharp/SonnetDB` (`event=schedule`, newest seven completed runs), queried on 2026-09-25 through the local HTTP proxy. This is a read-back of existing runs, not a new seven-day window. The gate requires seven consecutive scheduled dates with both light and full artifacts verified by `verify-parity-nightly-evidence.ps1`.

| UTC date | Run | Commit | Light | Full | Published artifacts |
| --- | ---: | --- | --- | --- | --- |
| 2026-09-24 | [35969238752](https://github.com/IoTSharp/SonnetDB/actions/runs/35969238752) | `44129260` | success | success | light + full |
| 2026-09-23 | [35831981163](https://github.com/IoTSharp/SonnetDB/actions/runs/35831981163) | `1be0b502` | success | success | light + full |
| 2026-09-22 | [35699644219](https://github.com/IoTSharp/SonnetDB/actions/runs/35699644219) | `8a03ea01` | success | success | light + full |
| 2026-09-21 | [35574361608](https://github.com/IoTSharp/SonnetDB/actions/runs/35574361608) | `b81a1fd7` | failure | failure | light + full |
| 2026-09-20 | [35497155622](https://github.com/IoTSharp/SonnetDB/actions/runs/35497155622) | `68176c63` | failure | failure | light + full |
| 2026-09-19 | [35428465977](https://github.com/IoTSharp/SonnetDB/actions/runs/35428465977) | `d78b66fe` | failure | failure | light + full |
| 2026-09-18 | [35318130333](https://github.com/IoTSharp/SonnetDB/actions/runs/35318130333) | `fb32f618` | failure | failure | light + full |

**Verdict: NOT_READY.** The rolling window has three successful dates and four failed dates. The latest three success results are real scheduled workflow results, but they cannot satisfy a seven-consecutive-day gate.

Both parity jobs and the publication job passed on 2026-09-24. The public `parity-results` branch at `4581837b` contains that run's light/full schema v2 summaries, raw suite reports and TRX files. Both summaries identify run `35969238752`, commit `44129260647f05c297da47d638ad1ac548ae4c23`, status `passing`, pass rate 100, ten suites and zero gate failures. Full: 51 total, 44 passed, 7 skipped, 2 warning-only; light: 51 total, 24 passed, 27 skipped, 2 warning-only. This is direct evidence for the latest run only. The published branch does not retain the preceding six run directories, and public artifact metadata does not supply their contents without authenticated download, so this read-back does not claim complete seven-run artifact validation.

Local contract checks on this worktree passed:

```text
pwsh -NoProfile -File tests/SonnetDB.Parity/scripts/test-verify-parity-nightly-evidence.ps1  PASS
pwsh -NoProfile -File tests/SonnetDB.Parity/scripts/test-summarize-parity.ps1                 PASS
pwsh -NoProfile -File tests/SonnetDB.Parity/scripts/test-compose-contract.ps1                  PASS
```

These deterministic checks verify the reporting and compose contracts, not the scheduled stack execution or fixed-hardware capacity. Keep M20 open until four more consecutive scheduled dates pass and the authenticated seven-run artifact verifier accepts both profiles and their raw reports.
