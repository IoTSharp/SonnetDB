# M39 SQL Trigger V2 Baseline Report

- Schema: `m39-trigger-v2-baseline-v4`
- Issue: `#333`
- Commit: `56cbcaed729914f1b1dcfa910439b208e0a96704-dirty`
- Started UTC: `2026-09-05T18:27:41.9900611+00:00`
- Finished UTC: `2026-09-05T18:27:52.0227915+00:00`
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
| 1 | Insert | NoTrigger | 1 | 19.81 | 50.48 | 95 | 0 | 127 | 40,370,176 | 902,176 | 78,736 |
| 1 | Insert | V1RowTrigger | 1 | 45.22 | 22.12 | 190 | 193 | 254 | 44,400,640 | 1,035,320 | 165,600 |
| 1 | Insert | CandidateStatementReference | 1 | 312.14 | 3.20 | 190 | 193 | 254 | 44,457,984 | 1,026,576 | 172,456 |
| 1 | Update | NoTrigger | 1 | 111.07 | 9.00 | 95 | 0 | 0 | 45,699,072 | 1,054,528 | 83,016 |
| 1 | Update | V1RowTrigger | 1 | 214.41 | 4.66 | 190 | 211 | 127 | 45,793,280 | 1,148,168 | 170,976 |
| 1 | Update | CandidateStatementReference | 1 | 243.84 | 4.10 | 190 | 211 | 127 | 46,120,960 | 1,139,952 | 178,320 |
| 1 | Delete | NoTrigger | 1 | 478.90 | 2.09 | 77 | 0 | -63 | 46,272,512 | 1,066,504 | 106,504 |
| 1 | Delete | V1RowTrigger | 1 | 211.11 | 4.74 | 172 | 211 | 64 | 46,301,184 | 1,164,632 | 170,112 |
| 1 | Delete | CandidateStatementReference | 1 | 265.44 | 3.77 | 172 | 211 | 64 | 46,469,120 | 1,152,008 | 175,384 |
| 100 | Insert | NoTrigger | 100 | 46466.24 | 2.15 | 4,352 | 0 | 5,620 | 46,526,464 | 1,249,328 | 359,000 |
| 100 | Insert | V1RowTrigger | 100 | 13114.41 | 7.63 | 8,704 | 3,559 | 11,240 | 47,280,128 | 2,061,584 | 1,120,648 |
| 100 | Insert | CandidateStatementReference | 100 | 26237.77 | 3.81 | 4,447 | 1,876 | 5,747 | 47,706,112 | 1,420,344 | 492,888 |
| 100 | Update | NoTrigger | 100 | 56837.56 | 1.76 | 4,352 | 0 | 0 | 48,250,880 | 1,690,496 | 382,680 |
| 100 | Update | V1RowTrigger | 100 | 12030.65 | 8.31 | 8,704 | 5,359 | 5,620 | 48,287,744 | 2,484,176 | 1,189,712 |
| 100 | Update | CandidateStatementReference | 100 | 21843.12 | 4.58 | 4,447 | 3,676 | 127 | 48,746,496 | 1,834,640 | 566,400 |
| 100 | Delete | NoTrigger | 100 | 40075.34 | 2.50 | 2,552 | 0 | -5,556 | 48,746,496 | 1,594,120 | 324,840 |
| 100 | Delete | V1RowTrigger | 100 | 10975.74 | 9.11 | 6,904 | 5,359 | 64 | 48,844,800 | 2,432,032 | 1,125,488 |
| 100 | Delete | CandidateStatementReference | 100 | 12639.19 | 7.91 | 2,647 | 3,676 | -5,429 | 49,025,024 | 1,784,912 | 500,104 |
| 10,000 | Insert | NoTrigger | 10,000 | 58654.05 | 170.49 | 430,052 | 0 | 556,112 | 74,510,336 | 14,070,040 | 24,787,016 |
| 10,000 | Insert | V1RowTrigger | 10,000 | 28610.54 | 349.52 | 860,104 | 340,159 | 1,112,114 | 99,557,376 | 35,241,256 | 92,541,832 |
| 10,000 | Insert | CandidateStatementReference | 10,000 | 111841.69 | 89.41 | 430,147 | 170,176 | 556,239 | 81,944,576 | 31,596,128 | 30,303,960 |
| 10,000 | Update | NoTrigger | 10,000 | 38113.90 | 262.37 | 431,104 | 0 | 1,009 | 96,972,800 | 14,355,544 | 30,240,800 |
| 10,000 | Update | V1RowTrigger | 10,000 | 23114.81 | 432.62 | 861,156 | 520,159 | 557,011 | 108,167,168 | 32,680,904 | 101,331,920 |
| 10,000 | Update | CandidateStatementReference | 10,000 | 43753.95 | 228.55 | 431,199 | 350,176 | 1,136 | 98,865,152 | 23,196,760 | 38,270,656 |
| 10,000 | Delete | NoTrigger | 10,000 | 54297.19 | 184.17 | 251,104 | 0 | -553,999 | 97,640,448 | 35,091,176 | 22,771,688 |
| 10,000 | Delete | V1RowTrigger | 10,000 | 29670.91 | 337.03 | 681,156 | 520,159 | 2,003 | 109,199,360 | 33,562,872 | 93,971,704 |
| 10,000 | Delete | CandidateStatementReference | 10,000 | 49459.38 | 202.19 | 251,199 | 350,176 | -553,872 | 108,384,256 | 48,217,488 | 31,863,392 |

## Rollback matrix

| Rows | Operation | Path | Failed as expected | Source restored | Audit restored | Elapsed ms | WAL bytes | Source after | Audit after | Failure code |
| ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| 1 | Insert | NoTrigger | True | True | True | 14.12 | 0 | 0 | 1 | constraint_violation |
| 1 | Insert | V1RowTrigger | True | True | True | 2.15 | 0 | 0 | 1 | constraint_violation |
| 1 | Insert | CandidateStatementReference | True | True | True | 0.58 | 0 | 0 | 1 | constraint_violation |
| 1 | Update | NoTrigger | True | True | True | 1.71 | 0 | 1 | 1 | constraint_violation |
| 1 | Update | V1RowTrigger | True | True | True | 0.47 | 0 | 1 | 1 | constraint_violation |
| 1 | Update | CandidateStatementReference | True | True | True | 0.59 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | NoTrigger | True | True | True | 0.88 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | V1RowTrigger | True | True | True | 0.38 | 0 | 1 | 1 | constraint_violation |
| 1 | Delete | CandidateStatementReference | True | True | True | 0.48 | 0 | 1 | 1 | constraint_violation |
| 100 | Insert | NoTrigger | True | True | True | 0.73 | 0 | 0 | 1 | constraint_violation |
| 100 | Insert | V1RowTrigger | True | True | True | 1.55 | 0 | 0 | 1 | constraint_violation |
| 100 | Insert | CandidateStatementReference | True | True | True | 0.50 | 0 | 0 | 1 | constraint_violation |
| 100 | Update | NoTrigger | True | True | True | 2.16 | 0 | 100 | 1 | constraint_violation |
| 100 | Update | V1RowTrigger | True | True | True | 2.58 | 0 | 100 | 1 | constraint_violation |
| 100 | Update | CandidateStatementReference | True | True | True | 1.87 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | NoTrigger | True | True | True | 2.16 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | V1RowTrigger | True | True | True | 1.92 | 0 | 100 | 1 | constraint_violation |
| 100 | Delete | CandidateStatementReference | True | True | True | 2.37 | 0 | 100 | 1 | constraint_violation |
| 10,000 | Insert | NoTrigger | True | True | True | 75.03 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Insert | V1RowTrigger | True | True | True | 195.51 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Insert | CandidateStatementReference | True | True | True | 59.67 | 0 | 0 | 1 | constraint_violation |
| 10,000 | Update | NoTrigger | True | True | True | 228.59 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Update | V1RowTrigger | True | True | True | 175.73 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Update | CandidateStatementReference | True | True | True | 153.18 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | NoTrigger | True | True | True | 131.78 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | V1RowTrigger | True | True | True | 202.56 | 1,052 | 10000 | 1 | constraint_violation |
| 10,000 | Delete | CandidateStatementReference | True | True | True | 111.60 | 1,052 | 10000 | 1 | constraint_violation |

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
