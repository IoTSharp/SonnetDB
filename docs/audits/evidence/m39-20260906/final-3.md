# M39 SQL Trigger V2 Baseline Report

- Schema: `m39-trigger-v2-baseline-v4`
- Issue: `#333`
- Commit: `56cbcaed729914f1b1dcfa910439b208e0a96704-dirty`
- Started UTC: `2026-09-05T18:28:47.6985188+00:00`
- Finished UTC: `2026-09-05T18:28:58.1705118+00:00`
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
| 1 | Insert | NoTrigger | 1 | 29.68 | 33.70 | 95 | 0 | 127 | 40,046,592 | 889,792 | 78,736 |
| 1 | Insert | V1RowTrigger | 1 | 59.47 | 16.82 | 190 | 193 | 254 | 44,044,288 | 1,035,320 | 165,600 |
| 1 | Insert | CandidateStatementReference | 1 | 323.28 | 3.09 | 190 | 193 | 254 | 44,101,632 | 1,026,576 | 172,456 |
| 1 | Update | NoTrigger | 1 | 117.64 | 8.50 | 95 | 0 | 0 | 44,216,320 | 1,046,328 | 83,016 |
| 1 | Update | V1RowTrigger | 1 | 246.00 | 4.07 | 190 | 211 | 127 | 44,167,168 | 1,148,144 | 170,976 |
| 1 | Update | CandidateStatementReference | 1 | 222.75 | 4.49 | 190 | 211 | 127 | 44,232,704 | 1,139,928 | 178,320 |
| 1 | Delete | NoTrigger | 1 | 510.02 | 1.96 | 77 | 0 | -63 | 45,436,928 | 1,041,856 | 81,880 |
| 1 | Delete | V1RowTrigger | 1 | 277.78 | 3.60 | 172 | 211 | 64 | 45,441,024 | 1,152,344 | 170,112 |
| 1 | Delete | CandidateStatementReference | 1 | 286.86 | 3.49 | 172 | 211 | 64 | 45,576,192 | 1,139,720 | 175,384 |
| 100 | Insert | NoTrigger | 100 | 42848.57 | 2.33 | 4,352 | 0 | 5,620 | 45,883,392 | 1,237,040 | 359,000 |
| 100 | Insert | V1RowTrigger | 100 | 13286.04 | 7.53 | 8,704 | 3,559 | 11,240 | 46,669,824 | 2,049,296 | 1,120,648 |
| 100 | Insert | CandidateStatementReference | 100 | 16695.33 | 5.99 | 4,447 | 1,876 | 5,747 | 47,058,944 | 1,408,056 | 492,888 |
| 100 | Update | NoTrigger | 100 | 57283.61 | 1.75 | 4,352 | 0 | 0 | 47,190,016 | 1,678,240 | 382,680 |
| 100 | Update | V1RowTrigger | 100 | 15165.07 | 6.59 | 8,704 | 5,359 | 5,620 | 47,190,016 | 2,447,376 | 1,189,712 |
| 100 | Update | CandidateStatementReference | 100 | 13543.90 | 7.38 | 4,447 | 3,676 | 127 | 47,906,816 | 1,806,008 | 566,400 |
| 100 | Delete | NoTrigger | 100 | 81579.38 | 1.23 | 2,552 | 0 | -5,556 | 48,271,360 | 1,581,832 | 324,840 |
| 100 | Delete | V1RowTrigger | 100 | 13170.20 | 7.59 | 6,904 | 5,359 | 64 | 48,300,032 | 2,419,744 | 1,125,488 |
| 100 | Delete | CandidateStatementReference | 100 | 20830.30 | 4.80 | 2,647 | 3,676 | -5,429 | 48,513,024 | 1,772,648 | 500,104 |
| 10,000 | Insert | NoTrigger | 10,000 | 54911.84 | 182.11 | 430,052 | 0 | 556,112 | 73,777,152 | 14,070,040 | 24,811,640 |
| 10,000 | Insert | V1RowTrigger | 10,000 | 24169.07 | 413.75 | 860,104 | 340,159 | 1,112,114 | 100,122,624 | 35,168,328 | 92,542,312 |
| 10,000 | Insert | CandidateStatementReference | 10,000 | 88111.01 | 113.49 | 430,147 | 170,176 | 556,239 | 83,709,952 | 31,596,080 | 30,301,544 |
| 10,000 | Update | NoTrigger | 10,000 | 25228.81 | 396.37 | 431,104 | 0 | 1,009 | 97,501,184 | 14,349,688 | 30,240,808 |
| 10,000 | Update | V1RowTrigger | 10,000 | 19996.33 | 500.09 | 861,156 | 520,159 | 557,011 | 106,737,664 | 35,151,992 | 100,693,568 |
| 10,000 | Update | CandidateStatementReference | 10,000 | 42404.13 | 235.83 | 431,199 | 350,176 | 1,136 | 106,201,088 | 22,888,544 | 38,272,488 |
| 10,000 | Delete | NoTrigger | 10,000 | 56760.78 | 176.18 | 251,104 | 0 | -553,999 | 95,346,688 | 35,092,880 | 22,771,688 |
| 10,000 | Delete | V1RowTrigger | 10,000 | 34284.88 | 291.67 | 681,156 | 520,159 | 2,003 | 108,531,712 | 33,561,656 | 93,971,696 |
| 10,000 | Delete | CandidateStatementReference | 10,000 | 55694.45 | 179.55 | 251,199 | 350,176 | -553,872 | 107,360,256 | 45,750,400 | 31,866,296 |

## Rollback matrix

| Rows | Operation | Path | Failed as expected | Source restored | Audit restored | Elapsed ms | WAL bytes | Source after | Audit after | Failure code |
| ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| 1 | Insert | NoTrigger | True | True | True | 10.08 | 0 | 0 | 1 | constraint_violation |
| 1 | Insert | V1RowTrigger | True | True | True | 1.38 | 0 | 0 | 1 | constraint_violation |
| 1 | Insert | CandidateStatementReference | True | True | True | 0.21 | 0 | 0 | 1 | constraint_violation |
| 1 | Update | NoTrigger | True | True | True | 1.49 | 0 | 1 | 1 | constraint_violation |
| 1 | Update | V1RowTrigger | True | True | True | 0.38 | 0 | 1 | 1 | constraint_violation |
| 1 | Update | CandidateStatementReference | True | True | True | 0.52 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | NoTrigger | True | True | True | 0.89 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | V1RowTrigger | True | True | True | 0.38 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | CandidateStatementReference | True | True | True | 0.30 | 0 | 1 | 1 | constraint_violation |
| 100 | Insert | NoTrigger | True | True | True | 1.21 | 0 | 0 | 1 | constraint_violation |
| 100 | Insert | V1RowTrigger | True | True | True | 3.76 | 0 | 0 | 1 | constraint_violation |
| 100 | Insert | CandidateStatementReference | True | True | True | 0.89 | 0 | 0 | 1 | constraint_violation |
| 100 | Update | NoTrigger | True | True | True | 2.41 | 0 | 100 | 1 | constraint_violation |
| 100 | Update | V1RowTrigger | True | True | True | 4.42 | 0 | 100 | 1 | constraint_violation |
| 100 | Update | CandidateStatementReference | True | True | True | 2.50 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | NoTrigger | True | True | True | 1.49 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | V1RowTrigger | True | True | True | 2.15 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | CandidateStatementReference | True | True | True | 1.36 | 0 | 100 | 1 | constraint_violation |
| 10,000 | Insert | NoTrigger | True | True | True | 98.61 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Insert | V1RowTrigger | True | True | True | 300.57 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Insert | CandidateStatementReference | True | True | True | 95.25 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Update | NoTrigger | True | True | True | 251.43 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Update | V1RowTrigger | True | True | True | 214.47 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Update | CandidateStatementReference | True | True | True | 186.85 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | NoTrigger | True | True | True | 144.35 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | V1RowTrigger | True | True | True | 211.93 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | CandidateStatementReference | True | True | True | 146.02 | 1,052 | 10000 | 1 | constraint_violation |

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
