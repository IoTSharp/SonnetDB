---
layout: default
title: Document Store 容量与长稳报告
description: Document Store 发布前容量 profile、验收维度、实测基线和规模建议。
permalink: /benchmarks/document-store-capacity/
---

# Document Store 容量与长稳报告

本页是 Milestone 25 / #174 的发布治理入口。性能数字只用于容量判断和版本间回归，不进入主 CI 红绿门槛，也不构成跨硬件性能承诺。

## 可复现入口

`tests/SonnetDB.DocumentSoak` 提供 `quick`（1 万）、`million`（100 万）和 `ten-million`（1,000 万）三档。每档使用同一执行序列，输出机器可读 `report.json` 与可读 `report.md`：

1. 批量写入并采样 working set、private bytes 和 managed heap。
2. 创建 JSON path index，执行索引查询和在线 rebuild。
3. 插入过期文档并验证 TTL 清理后的主数据/索引一致性。
4. 关闭后先做同进程热重开，再用独立子进程做进程冷启动；两者都验证文档数和索引一致性。runner 不强制清空 OS page cache，报告会标记 `os_page_cache=not_flushed`。
5. 子进程写入后以退出码 23 异常终止，不执行 Dispose；父进程重开并验证恢复。
6. 创建一致性备份，离线恢复到新目录并复验文档数和索引。

运行方式见 [Document soak README](../../tests/SonnetDB.DocumentSoak/README.md)。GitHub 的 `Document Store Soak` workflow 支持手动触发和 artifact 归档；大档超过 hosted runner 时间/磁盘上限时必须转到固定规格专用长测机。

## 当前实测基线

2026-07-11 在 Windows 开发机、.NET 10.0.9、默认 `FlushWalToOsOnWrite=true` 下完成 `quick` profile，10,000 文档全部验收项 PASS。此结果用于验证 runner 与发布流程，不用于外推百万档：

| Phase | Duration | Throughput |
|---|---:|---:|
| write | 17.137 s | 583.52 docs/s |
| index create | 5.276 s | 1,895.23 docs/s |
| indexed query（20 次，每次 limit 100） | 77.71 ms | 257.36 queries/s |
| index rebuild | 9.080 s | 1,101.36 docs/s |
| TTL cleanup（100 条） | 353.25 ms | 283.08 docs/s |
| backup | 377.73 ms | 26,474.22 docs/s |
| hot reopen + consistency | 25.572 s | 391.05 docs/s |
| cold process start + consistency | 20.725 s | 482.51 docs/s（OS page cache 未清空） |
| crash worker + recovery（32 条） | 48.537 s | 0.66 docs/s（含重开/全索引校验） |
| backup restore + consistency | 18.395 s | 545.37 docs/s |

工作集从写入 1,000 条时约 47.8 MiB 增至 10,000 条热态约 91.9 MiB，完整恢复验收结束约 92.5 MiB。原始本地报告生成在 `artifacts/document-soak/quick-local/`，该目录属于运行产物，不纳入源码提交。

## 百万 / 千万档状态

仓库已经提供两档真实数据量 profile，但本次开发机验收没有把 quick 结果伪装成百万/千万实测。按 quick profile 已观测到的冷启动与恢复成本，`million` / `ten-million` 必须在专用长测窗口执行，发布记录应附 artifact URL 和 commit SHA。没有对应 PASS 报告时，对外只能声明“profile 可执行，规模尚未在目标硬件验证”。

## 推荐规模

- 当前发布证据支持单集合 **1 万文档级**完整治理闭环；这是已验证基线，不是引擎硬上限。
- 计划进入 **10 万以上**单集合时，应先在目标硬件运行自定义 `--documents N`，重点检查冷启动、rebuild、异常恢复和恢复后的峰值磁盘/内存，而不只看热查询。
- **100 万**文档必须以 `million` profile PASS 作为上线前置；**1,000 万**仅作为专用长测边界，不是当前默认推荐生产规模。
- 需要持续高写入、低秒级恢复、在线多副本或分片的场景不应仅依赖当前单节点 Document Store；应拆分集合/数据库，或选用具备相应分布式能力的系统。

## 发布门禁

发布负责人必须归档 profile、commit、机器规格、磁盘、持续时间、所有 phase 状态和完整内存曲线。任何 SKIP/FAIL 都必须给出 `gap_reason` 或故障说明；性能回退先作为 warning 审核，数据丢失、恢复后数量错误或索引不一致直接阻止发布。
