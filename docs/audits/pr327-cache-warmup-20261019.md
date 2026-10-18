# PR #327：缓存依赖与并行预热修复

- 整合基线：`0986f92e0d575c329a81b026e9431a16f4b8e2dd`；保留原 PR head `9b51dcd074b8ce4b00a8ff2d5d6dad294422d2a7` 的合并父节点。
- 依赖增量仅为 `Microsoft.Extensions.Caching.Abstractions` 从 10.0.12 升为 10.0.13。
- 原 Ubuntu CI 38987739858 / job 117263025271 的 Core 为 7605/7606；原失败是 `WarmUpAll_WithConcurrency_OpensDifferentTablesInParallel`。预热线程并发读写普通 `_tableLockTokens` 字典，当前 main 也存在该缺陷。
- 最小生产修复只对身份字典的查找/登记加短锁；KV 打开/恢复保持并行。不锁住由预热调用方持有的 `_sync`，避免等待工作线程时死锁。其它字典维护入口受管理器锁排他保护。
- 保留原两个线程的真实 Barrier 并行断言，追加共享锁管理器、不同表身份隔离、重复预热复用、重命名身份稳定断言。
- 隔离修复树本机 Windows 五个相关测试类 **92/92、0 fail、0 skip**，原失败方法 Passed。该结果基于依赖升级前的修复树，不能据此声称新缓存包的 Linux/完整方案或最终远端 CI 通过；完整 restore/规定 Format 在最终合并树单独执行，之后仍需最终候选 CI。
- 源增量、原失败日志、TRX、进程与清理回执在 canonical 的忽略目录 `.codex-temp/pr327-warmup-evidence-20261019/`。18 个由代理创建的 bin/obj 目录已按精确清单回收，未清理其它工作树或共享缓存。
- 本任务尚未统一推送、部署或发布正式包。失败记录和测试断言均未删除、放宽或跳过。
