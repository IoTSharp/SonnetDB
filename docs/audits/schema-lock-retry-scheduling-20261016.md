# 表目录文件锁重试测试调度核查

日期：2026-10-16。根基线为 `aa4fbffba0caf7d400cb10e7ebb1d45cc99ff5c5`，本项独立于 #250 的维护功能提交；生产保存、文件格式和重试实现均不修改。

## 已复现的问题

`TableSchemaCodecTests.Save_DestinationTemporarilyLocked_RetriesAfterHandleIsReleased` 在持有真实文件读锁时，用 `Task.Run` 执行同步保存，再等待 75ms 后释放锁。保存失败重试占用线程池线程；低核测试进程下，锁释放的异步延续可能直到全部重试结束才被调度。

#250 的 `contracts-core-test04` 为 231/232，唯一失败是此例；独跑 `contracts-lock-retry01` 仍失败。根分别运行 #251 已验收的旧程序集和 #250 当前候选，确认两者默认处理器配置均 1/1 通过，但设置 `DOTNET_PROCESSOR_COUNT=2` 后两者均 0/1（约 708ms）。因此没有将该失败归因于 #250 的新文件格式，也没有通过扩大生产重试窗口掩盖测试调度问题。

## 最小修正和验证

仅将测试的保存任务改为 `Task.Factory.StartNew`，明确 `TaskCreationOptions.LongRunning` 和 `TaskScheduler.Default`，使同步保存使用专用线程。原真实文件锁、75ms 等待、最终目录内容和临时文件清理断言全部保留。测试仍含真实时间等待，不将其称为无计时依赖的确定性测试。

根以修正后的完整 `TableSchemaCodecTests.cs` 链接原 #251 冻结 Core DLL，通过独立外部 xUnit 项目运行整类：

| 运行 | 结果 |
|---|---:|
| 原 #251 DLL，默认处理器配置，目标测试 | 1/1 |
| 原 #250 候选 DLL，默认处理器配置，目标测试 | 1/1 |
| 原 #251 DLL，两个逻辑处理器，目标测试 | 0/1 |
| 原 #250 候选 DLL，两个逻辑处理器，目标测试 | 0/1 |
| 修正测试 + 原 #251 Core，两个逻辑处理器，完整类 | 20/20 |
| 修正测试 + 原 #251 Core，一个逻辑处理器，完整类 | 20/20 |

所有新运行无跳过。该验证证明测试修正在 Windows/.NET 10 下解除已复现的调度依赖，不证明所有操作系统、任意长时间外部文件锁或断电情况下保存均成功。

原 Core SHA256 为 `3BCBF3DE4ED884098273F90938ADEBEB2F3CB064C8DE5D05933F72BEB806BCC6`，原测试程序集为 `75D0183EED0AAEF6D1BB807183A58CDC7E9EF38E9C8C72B6CCC8697EFE8B25BF`。源码、新旧测试程序集、候选 DLL 哈希及全部日志/TRX 保存在监督树 `artifacts/issue-supervision-20261016/schema-lock-retry/`，旧失败均保留。

提交前必须在最终待提交树完成全 solution restore 与规定的原 Format 检查；实际回执保存在同目录 `gate/`。只有退出码均为 0 且最终树未变化才提交。本项不单独推送，随 #252/#251/#250 批次验收后正常推送；不据此关闭任何功能 Issue。
