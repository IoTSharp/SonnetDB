# M35 RAG writer 真子进程硬杀恢复切片（2026-10-01）

本切片补上 RAG 持久摄取在真子进程 `Process.Kill(true)` 后的恢复回归。子进程在第一个 chunk 已写入 staging、第二个 chunk 的 embedding 调用挂起时被终止；父进程重新打开同一数据库目录并调用 `RagIngestionWriter.ResumeAsync`，必须复用第一个 durable chunk，只调用 provider 生成第二个向量，然后一次发布完整 generation。

覆盖合同：

- active generation 在硬杀期间保持不可见，重开后只发布一次完整版本；
- 已持久化 chunk 不重复调用 embedding provider；
- 未完成 chunk 继续生成，Document、FullText 可读，Vector 索引通过一致性校验；
- 任务发布后 checkpoint 与 generation 绑定；重复 `ResumeAsync` 只返回已发布版本，不再调用 provider。

验证入口：`tests/SonnetDB.CrashTests/M43CrashReliabilityTests.cs` 中的 M35 测试和 child process 场景 `crash_kill9_rag_writer_mid_embedding`。该证据只覆盖本机进程终止与 WAL/staging 恢复，不代表固定硬件掉电、真实模型质量或 M35 容量门禁已完成。
