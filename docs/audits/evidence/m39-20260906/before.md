# M39 SQL Trigger V2 Baseline Report

- Schema: `m39-trigger-v2-baseline-v3`
- Issue: `#333`
- Commit: `56cbcaed729914f1b1dcfa910439b208e0a96704-dirty`
- Started UTC: `2026-09-05T17:32:44.5283537+00:00`
- Finished UTC: `2026-09-05T17:33:57.6511226+00:00`
- Row counts: `1, 100, 10,000`
- Operations: `Insert, Update, Delete`
- Crash/replay tests verified in this flow: `False`
- Runtime: `.NET 10.0.11` / `Microsoft Windows 10.0.26200`
- Architecture: `X64`
- CPU count: `22`
- Available memory bytes: `68,137,205,760`

## Golden journeys

- `audit_outbox`: `test_reference_not_run` (`SqlTriggerV2BaselineTests.GoldenJourney_AuditOutbox_EmitsDurableEventsForEveryRowMutation`)
- `derived_aggregate`: `test_reference_not_run` (`SqlTriggerV2BaselineTests.GoldenJourney_DerivedAggregate_TracksInsertUpdateAndDeleteDeltas`)
- `state_transition_protection`: `test_reference_not_run` (`SqlTriggerV2BaselineTests.GoldenJourney_StateTransitionProtection_RollsBackForbiddenTransition`)

## Cost matrix

| Rows | Operation | Path | Rows affected | Rows/sec | Elapsed ms | WAL bytes | Rowstore bytes delta | Working set | Managed | Allocated |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | Insert | NoTrigger | 1 | 35.31 | 28.32 | 95 | 127 | 38,510,592 | 888,304 | 78,544 |
| 1 | Insert | V1RowTrigger | 1 | 204.24 | 4.90 | 190 | 254 | 43,196,416 | 1,018,280 | 152,480 |
| 1 | Insert | CandidateStatementReference | 1 | 3019.32 | 0.33 | 190 | 254 | 43,229,184 | 1,012,640 | 159,760 |
| 1 | Update | NoTrigger | 1 | 89.78 | 11.14 | 95 | 0 | 44,425,216 | 1,043,896 | 82,704 |
| 1 | Update | V1RowTrigger | 1 | 2128.11 | 0.47 | 190 | 127 | 44,453,888 | 1,135,544 | 157,864 |
| 1 | Update | CandidateStatementReference | 1 | 1819.51 | 0.55 | 190 | 127 | 44,568,576 | 1,129,216 | 165,296 |
| 1 | Delete | NoTrigger | 1 | 317.24 | 3.15 | 77 | -63 | 45,342,720 | 1,039,336 | 81,696 |
| 1 | Delete | V1RowTrigger | 1 | 1412.03 | 0.71 | 172 | 64 | 45,355,008 | 1,139,680 | 157,008 |
| 1 | Delete | CandidateStatementReference | 1 | 2498.13 | 0.40 | 172 | 64 | 45,383,680 | 1,133,328 | 162,368 |
| 100 | Insert | NoTrigger | 100 | 54498.88 | 1.83 | 4,352 | 5,620 | 45,432,832 | 1,232,920 | 357,224 |
| 100 | Insert | V1RowTrigger | 100 | 13078.39 | 7.65 | 8,704 | 11,240 | 46,964,736 | 2,758,528 | 1,834,760 |
| 100 | Insert | CandidateStatementReference | 100 | 25842.46 | 3.87 | 4,447 | 5,747 | 47,616,000 | 1,660,952 | 748,040 |
| 100 | Update | NoTrigger | 100 | 54755.52 | 1.83 | 4,352 | 0 | 47,693,824 | 1,661,432 | 371,304 |
| 100 | Update | V1RowTrigger | 100 | 15721.79 | 6.36 | 8,704 | 5,620 | 47,833,088 | 2,967,368 | 1,714,800 |
| 100 | Update | CandidateStatementReference | 100 | 33041.47 | 3.03 | 4,447 | 127 | 48,017,408 | 1,862,880 | 630,152 |
| 100 | Delete | NoTrigger | 100 | 62605.65 | 1.60 | 2,552 | -5,556 | 48,463,872 | 1,555,928 | 323,864 |
| 100 | Delete | V1RowTrigger | 100 | 14345.76 | 6.97 | 6,904 | 64 | 48,816,128 | 2,917,960 | 1,651,376 |
| 100 | Delete | CandidateStatementReference | 100 | 29563.06 | 3.38 | 2,647 | -5,429 | 48,963,584 | 1,813,472 | 564,656 |
| 10,000 | Insert | NoTrigger | 10,000 | 68271.18 | 146.47 | 430,052 | 556,112 | 72,183,808 | 13,935,432 | 24,626,824 |
| 10,000 | Insert | V1RowTrigger | 10,000 | 1965.78 | 5087.04 | 860,104 | 1,112,114 | 86,282,240 | 32,070,512 | 7,690,780,200 |
| 10,000 | Insert | CandidateStatementReference | 10,000 | 3730.24 | 2680.79 | 430,147 | 556,239 | 79,360,000 | 19,534,024 | 3,225,143,800 |
| 10,000 | Update | NoTrigger | 10,000 | 52717.90 | 189.69 | 431,104 | 1,009 | 82,493,440 | 21,821,464 | 28,434,808 |
| 10,000 | Update | V1RowTrigger | 10,000 | 1514.22 | 6604.07 | 861,156 | 557,011 | 91,287,552 | 22,877,000 | 6,095,841,816 |
| 10,000 | Update | CandidateStatementReference | 10,000 | 3177.61 | 3147.02 | 431,199 | 1,136 | 84,312,064 | 21,536,856 | 1,630,207,704 |
| 10,000 | Delete | NoTrigger | 10,000 | 42152.63 | 237.23 | 251,104 | -553,999 | 100,929,536 | 34,641,016 | 22,691,512 |
| 10,000 | Delete | V1RowTrigger | 10,000 | 1309.87 | 7634.37 | 681,156 | 2,003 | 91,672,576 | 20,493,928 | 6,089,524,384 |
| 10,000 | Delete | CandidateStatementReference | 10,000 | 3290.56 | 3039.00 | 251,199 | -553,872 | 96,739,328 | 22,040,216 | 1,623,885,400 |

## Rollback matrix

| Rows | Operation | Path | Failed as expected | Source restored | Audit restored | Elapsed ms | WAL bytes | Source after | Audit after | Failure code |
| ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| 1 | Insert | NoTrigger | True | True | True | 9.08 | 0 | 0 | 1 | constraint_violation |
| 1 | Insert | V1RowTrigger | True | True | True | 1.86 | 0 | 0 | 1 | constraint_violation |
| 1 | Insert | CandidateStatementReference | True | True | True | 0.33 | 0 | 0 | 1 | constraint_violation |
| 1 | Update | NoTrigger | True | True | True | 1.82 | 0 | 1 | 1 | constraint_violation |
| 1 | Update | V1RowTrigger | True | True | True | 0.70 | 0 | 1 | 1 | constraint_violation |
| 1 | Update | CandidateStatementReference | True | True | True | 0.58 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | NoTrigger | True | True | True | 1.25 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | V1RowTrigger | True | True | True | 0.53 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | CandidateStatementReference | True | True | True | 0.42 | 0 | 1 | 1 | constraint_violation |
| 100 | Insert | NoTrigger | True | True | True | 2.99 | 0 | 0 | 1 | constraint_violation |
| 100 | Insert | V1RowTrigger | True | True | True | 6.35 | 0 | 0 | 1 | constraint_violation |
| 100 | Insert | CandidateStatementReference | True | True | True | 2.78 | 0 | 0 | 1 | constraint_violation |
| 100 | Update | NoTrigger | True | True | True | 2.68 | 0 | 100 | 1 | constraint_violation |
| 100 | Update | V1RowTrigger | True | True | True | 5.58 | 0 | 100 | 1 | constraint_violation |
| 100 | Update | CandidateStatementReference | True | True | True | 2.79 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | NoTrigger | True | True | True | 5.23 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | V1RowTrigger | True | True | True | 6.44 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | CandidateStatementReference | True | True | True | 2.89 | 0 | 100 | 1 | constraint_violation |
| 10,000 | Insert | NoTrigger | True | True | True | 3050.45 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Insert | V1RowTrigger | True | True | True | 5611.66 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Insert | CandidateStatementReference | True | True | True | 2293.05 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Update | NoTrigger | True | True | True | 2060.76 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Update | V1RowTrigger | True | True | True | 8021.45 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Update | CandidateStatementReference | True | True | True | 3085.47 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | NoTrigger | True | True | True | 3169.14 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | V1RowTrigger | True | True | True | 7648.27 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | CandidateStatementReference | True | True | True | 2906.92 | 1,052 | 10000 | 1 | constraint_violation |

## Crash and replay evidence

- `trigger_action_failure_midway`: `test_reference_not_run` (`SqlTriggerV2BaselineTests.CrashEvidence_TriggerActionFailureMidBatch_RollsBackEarlierRows`)
- `commit_failure`: `test_reference_not_run` (`SqlTriggerV2BaselineTests.CrashEvidence_CommitFailure_RollsBackAllTablesAndMarksTriggerFailed`)
- `process_termination_between_table_wals`: `test_reference_not_run` (`SonnetDB.CrashTests.crash_kill9_betweenTriggerTableCommits_ReopenReportsMeasuredPartialPair`)
- `restart_wal_replay`: `test_reference_not_run` (`SqlTriggerV2BaselineTests.CrashEvidence_RestartReplay_PreservesCommittedTriggerOutbox`)

## Validated

- 当前 V1 AFTER ROW 在关系表上可执行；本次命令实际执行了成本/回滚矩阵，golden journey 的 Core 断言未在本命令中运行。
- 1、100、10,000 行的 INSERT、UPDATE、DELETE 成功与回滚矩阵覆盖无触发器、V1 行触发器和客户端事务参考路径。
- 报告仅登记提交失败、真进程终止和重启 replay 的测试入口，本命令未执行这些测试；跨 keyspace 掉电原子性不被宣称。

## Not Proven

- CandidateStatementReference 是显式事务与汇总写入的客户端参考，不构成产品 statement trigger 的实现或语义证明。
- 成本与回滚数据只代表报告所记录的本次运行环境，不构成固定硬件容量、生产吞吐或 SLO 声明。
- Document/measurement、BEFORE、transition table、deferred 和 exactly-once 语义仍未准入。
