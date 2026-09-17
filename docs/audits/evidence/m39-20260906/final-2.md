# M39 SQL Trigger V2 Baseline Report

- Schema: `m39-trigger-v2-baseline-v4`
- Issue: `#333`
- Commit: `56cbcaed729914f1b1dcfa910439b208e0a96704-dirty`
- Started UTC: `2026-09-05T18:28:18.9917410+00:00`
- Finished UTC: `2026-09-05T18:28:29.1679042+00:00`
- Row counts: `1, 100, 10,000`
- Operations: `Insert, Update, Delete`
- Crash/replay tests verified in this flow: `True`
- Runtime: `.NET 10.0.11` / `Microsoft Windows 10.0.26200`
- Architecture: `X64`
- CPU count: `22`
- Available memory bytes: `68,137,205,760`

## Golden journeys

- `audit_outbox`: `validated_by_core_test` (`SqlTriggerV2BaselineTests.GoldenJourney_AuditOutbox_EmitsDurableEventsForEveryRowMutation`)
- `derived_aggregate`: `validated_by_core_test` (`SqlTriggerV2BaselineTests.GoldenJourney_DerivedAggregate_TracksInsertUpdateAndDeleteDeltas`)
- `state_transition_protection`: `validated_by_core_test` (`SqlTriggerV2BaselineTests.GoldenJourney_StateTransitionProtection_RollsBackForbiddenTransition`)

## Cost matrix

| Rows | Operation | Path | Rows affected | Rows/sec | Elapsed ms | WAL bytes | Journal bytes | Rowstore bytes delta | Working set | Managed | Allocated |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | Insert | NoTrigger | 1 | 22.83 | 43.80 | 95 | 0 | 127 | 40,341,504 | 889,792 | 78,736 |
| 1 | Insert | V1RowTrigger | 1 | 39.53 | 25.30 | 190 | 193 | 254 | 44,466,176 | 1,035,320 | 165,600 |
| 1 | Insert | CandidateStatementReference | 1 | 227.04 | 4.40 | 190 | 193 | 254 | 44,523,520 | 1,026,576 | 172,456 |
| 1 | Update | NoTrigger | 1 | 100.49 | 9.95 | 95 | 0 | 0 | 45,662,208 | 1,054,528 | 83,016 |
| 1 | Update | V1RowTrigger | 1 | 212.35 | 4.71 | 190 | 211 | 127 | 45,682,688 | 1,148,168 | 170,976 |
| 1 | Update | CandidateStatementReference | 1 | 227.69 | 4.39 | 190 | 211 | 127 | 46,084,096 | 1,139,952 | 178,320 |
| 1 | Delete | NoTrigger | 1 | 494.83 | 2.02 | 77 | 0 | -63 | 46,358,528 | 1,041,856 | 81,880 |
| 1 | Delete | V1RowTrigger | 1 | 274.60 | 3.64 | 172 | 211 | 64 | 46,362,624 | 1,152,344 | 170,112 |
| 1 | Delete | CandidateStatementReference | 1 | 222.59 | 4.49 | 172 | 211 | 64 | 46,366,720 | 1,139,720 | 175,384 |
| 100 | Insert | NoTrigger | 100 | 55626.63 | 1.80 | 4,352 | 0 | 5,620 | 46,788,608 | 1,237,040 | 359,000 |
| 100 | Insert | V1RowTrigger | 100 | 16446.29 | 6.08 | 8,704 | 3,559 | 11,240 | 47,587,328 | 2,076,248 | 1,145,272 |
| 100 | Insert | CandidateStatementReference | 100 | 22830.53 | 4.38 | 4,447 | 1,876 | 5,747 | 47,955,968 | 1,420,344 | 492,888 |
| 100 | Update | NoTrigger | 100 | 46296.30 | 2.16 | 4,352 | 0 | 0 | 47,988,736 | 1,690,528 | 382,680 |
| 100 | Update | V1RowTrigger | 100 | 14481.00 | 6.91 | 8,704 | 5,359 | 5,620 | 48,041,984 | 2,459,664 | 1,189,712 |
| 100 | Update | CandidateStatementReference | 100 | 15832.30 | 6.32 | 4,447 | 3,676 | 127 | 48,611,328 | 1,818,296 | 566,400 |
| 100 | Delete | NoTrigger | 100 | 70397.75 | 1.42 | 2,552 | 0 | -5,556 | 48,611,328 | 1,577,776 | 324,840 |
| 100 | Delete | V1RowTrigger | 100 | 10630.38 | 9.41 | 6,904 | 5,359 | 64 | 48,656,384 | 2,415,688 | 1,125,488 |
| 100 | Delete | CandidateStatementReference | 100 | 15904.32 | 6.29 | 2,647 | 3,676 | -5,429 | 48,771,072 | 1,784,912 | 500,104 |
| 10,000 | Insert | NoTrigger | 10,000 | 44590.70 | 224.26 | 430,052 | 0 | 556,112 | 75,345,920 | 14,070,040 | 24,787,016 |
| 10,000 | Insert | V1RowTrigger | 10,000 | 28997.95 | 344.85 | 860,104 | 340,159 | 1,112,114 | 102,109,184 | 35,192,136 | 92,540,032 |
| 10,000 | Insert | CandidateStatementReference | 10,000 | 82856.49 | 120.69 | 430,147 | 170,176 | 556,239 | 83,251,200 | 24,694,752 | 30,308,984 |
| 10,000 | Update | NoTrigger | 10,000 | 25589.41 | 390.79 | 431,104 | 0 | 1,009 | 98,267,136 | 14,354,232 | 30,240,792 |
| 10,000 | Update | V1RowTrigger | 10,000 | 23896.35 | 418.47 | 861,156 | 520,159 | 557,011 | 107,622,400 | 35,120,688 | 100,611,896 |
| 10,000 | Update | CandidateStatementReference | 10,000 | 46585.60 | 214.66 | 431,199 | 350,176 | 1,136 | 101,216,256 | 23,753,304 | 38,267,544 |
| 10,000 | Delete | NoTrigger | 10,000 | 67271.89 | 148.65 | 251,104 | 0 | -553,999 | 96,976,896 | 35,092,248 | 22,771,688 |
| 10,000 | Delete | V1RowTrigger | 10,000 | 31313.85 | 319.35 | 681,156 | 520,159 | 2,003 | 109,527,040 | 33,586,232 | 93,971,696 |
| 10,000 | Delete | CandidateStatementReference | 10,000 | 61765.65 | 161.90 | 251,199 | 350,176 | -553,872 | 108,650,496 | 48,153,320 | 31,868,312 |

## Rollback matrix

| Rows | Operation | Path | Failed as expected | Source restored | Audit restored | Elapsed ms | WAL bytes | Source after | Audit after | Failure code |
| ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| 1 | Insert | NoTrigger | True | True | True | 18.51 | 0 | 0 | 1 | constraint_violation |
| 1 | Insert | V1RowTrigger | True | True | True | 2.30 | 0 | 0 | 1 | constraint_violation |
| 1 | Insert | CandidateStatementReference | True | True | True | 0.29 | 0 | 0 | 1 | constraint_violation |
| 1 | Update | NoTrigger | True | True | True | 1.45 | 0 | 1 | 1 | constraint_violation |
| 1 | Update | V1RowTrigger | True | True | True | 0.46 | 0 | 1 | 1 | constraint_violation |
| 1 | Update | CandidateStatementReference | True | True | True | 0.60 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | NoTrigger | True | True | True | 1.29 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | V1RowTrigger | True | True | True | 0.37 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | CandidateStatementReference | True | True | True | 0.31 | 0 | 1 | 1 | constraint_violation |
| 100 | Insert | NoTrigger | True | True | True | 1.06 | 0 | 0 | 1 | constraint_violation |
| 100 | Insert | V1RowTrigger | True | True | True | 1.96 | 0 | 0 | 1 | constraint_violation |
| 100 | Insert | CandidateStatementReference | True | True | True | 1.25 | 0 | 0 | 1 | constraint_violation |
| 100 | Update | NoTrigger | True | True | True | 1.54 | 0 | 100 | 1 | constraint_violation |
| 100 | Update | V1RowTrigger | True | True | True | 2.90 | 0 | 100 | 1 | constraint_violation |
| 100 | Update | CandidateStatementReference | True | True | True | 1.39 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | NoTrigger | True | True | True | 1.30 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | V1RowTrigger | True | True | True | 4.09 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | CandidateStatementReference | True | True | True | 2.28 | 0 | 100 | 1 | constraint_violation |
| 10,000 | Insert | NoTrigger | True | True | True | 113.48 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Insert | V1RowTrigger | True | True | True | 214.57 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Insert | CandidateStatementReference | True | True | True | 79.34 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Update | NoTrigger | True | True | True | 290.59 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Update | V1RowTrigger | True | True | True | 172.00 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Update | CandidateStatementReference | True | True | True | 116.87 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | NoTrigger | True | True | True | 114.91 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | V1RowTrigger | True | True | True | 212.92 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | CandidateStatementReference | True | True | True | 92.27 | 1,052 | 10000 | 1 | constraint_violation |

## Crash and replay evidence

- `trigger_action_failure_midway`: `validated_by_core_test` (`SqlTriggerV2BaselineTests.CrashEvidence_TriggerActionFailureMidBatch_RollsBackEarlierRows`)
- `commit_failure`: `validated_by_core_test` (`SqlTriggerV2BaselineTests.CrashEvidence_CommitFailure_RollsBackAllTablesAndMarksTriggerFailed`)
- `process_termination_between_table_wals`: `validated_by_process_kill_test` (`CrashReliabilityTests.crash_kill9_betweenTriggerTableCommits_ReopenRollsBackBothTables`)
- `process_termination_before_and_after_completion`: `validated_by_process_kill_test` (`CrashReliabilityTests.crash_kill9_triggerCompletionBoundary_ReopenSeesConsistentPair`)
- `restart_wal_replay`: `validated_by_core_test` (`SqlTriggerV2BaselineTests.CrashEvidence_RestartReplay_PreservesCommittedTriggerOutbox`)

## Validated

- 当前 V1 AFTER ROW 在关系表上可执行，三条 golden journey 的行级合同与进程内回滚已由 Core 测试验证。
- 1、100、10,000 行的 INSERT、UPDATE、DELETE 成功与回滚矩阵覆盖无触发器、V1 行触发器和客户端事务参考路径。
- 提交失败、真进程终止和重启 replay 已由同一证据流程的自动化测试验证；跨 keyspace 掉电原子性不被宣称。

## Not Proven

- CandidateStatementReference 是显式事务与汇总写入的客户端参考，不构成产品 statement trigger 的实现或语义证明。
- 多表提交的恢复日志与每表 WAL 都同步到磁盘，journalBytes 单独计量；单表路径仍沿用所配置的 KV WAL 策略。
- 成本与回滚数据只代表报告所记录的本次运行环境，不构成固定硬件容量、生产吞吐或 SLO 声明。
- Document/measurement、BEFORE、transition table、deferred 和 exactly-once 语义仍未准入。
