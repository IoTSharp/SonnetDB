# Issue #407：Int64Rle 数值聚合路径修复

Copilot 在 [PR #405 的评论](https://github.com/IoTSharp/SonnetDB/pull/405#discussion_r4479778545)指出的缺陷成立，建议采纳。[Issue #407](https://github.com/IoTSharp/SonnetDB/issues/407)独立跟踪查询回归；原 #367 的压缩功能及 v7 升级合同不重新立项。

## 根因与修复

显式设置 `SegmentWriterOptions.ValueEncoding = DeltaValue` 且 Int64 游程载荷更小时，v7 writer 选择独立的 `Int64Rle` 标志；默认未启用压缩的写入不触发本问题。`QueryEngine.CanAggregateRawBlock` 和 `CanFuseDeltaTimestampInline` 仅拒绝 `DeltaValue`。独立的 `Int64Rle` 标志通过判定后，原始时间戳路径、delta-of-delta 时间戳融合路径及 SIMD 聚合均按每点 8 字节访问游程载荷，局部范围或跨桶扫描可越界。完整块的聚合元数据快路径可掩盖问题，之前的 codec/点查询回归不能覆盖此入口。

本次仅在两个现有判定的拒绝掩码中加入 `Int64Rle`，使其进入既有 `BlockDecoder.DecodeRange`。原始值读取及 SIMD 调用点均由这两个判定控制。Count 的专用时间戳路径保持原合同；无需新增 RLE 聚合器、格式版本、依赖或序列化入口。

## 可重复验证

新增 `QueryEngineInt64RleAggregateTests` 的 16 个用例：

- 8 项单序列/批量序列聚合，交叉覆盖 raw/delta-of-delta 时间戳、SIMD 开关、局部全局聚合/500ms 分桶，检查 Count/Sum/Min/Max/Avg。
- 4 项共享扫描多聚合，分别验证每桶 count/sum/min/max。
- 2 项扩展 percentile accumulator，验证部分块不得错误使用整块 sketch 或原始值读取。
- 2 项仅第二点的极小查询：4 点 `[5,5,9,9]` 生成 18 字节双游程载荷（小于 raw 的 32 字节），旧路径在载荷内读取 offset=8 的 8 字节，会把 run 长度和下一值误读为 2306，实际应为 5。这两项直接验证静默错值，分别使用 raw/delta 时间戳。

前 14 项各写入 512 点、三个不同的 Int64 长游程，实际 `FlushNow` 后先断言 MemTable 为空、唯一物理块为 `Int64Rle`、时间戳编码符合参数且载荷小于 raw，然后与独立生成的 385 点局部范围预期比较；初次打开与两次独立重开均执行同一组断言。另两项极小查询也实际刷盘并验证块编码和精确载荷长度。

| 验证 | 结果 | 本机原始证据 |
| --- | --- | --- |
| 修复前新增回归 | 14/14 FAIL，0 跳过，退出 1；原始读取及 SIMD 路径均出现 `ArgumentOutOfRangeException` | `artifacts/review405/before-fix-complete.trx` |
| 追加静默错值回归修复前 | 2/2 FAIL，0 跳过，退出 1；两项均为期望 5、实际 2306 | `artifacts/review405/before-fix-silent-values.trx` |
| 修复后 Query 与 Int64 存储扩大回归 | 472/472 PASS，0 失败/跳过，退出 0 | `artifacts/review405/after-fix-query-storage.trx` |
| 最终 16 项回归 | 16/16 PASS，0 失败/跳过，退出 0；包含上述 14 项与追加 2 项 | `artifacts/review405/final-regressions-16.trx` |
| 本机完整 Core | TIMED_OUT，900 秒墙钟上限；未生成完成 TRX，不能计作整套通过 | `artifacts/review405/full-core-timeout-receipt.json` |
| 提交前完整 solution restore | PASS，退出 0；两个已登记子模块保持固定 commit | `dotnet restore SonnetDB.slnx` |
| 提交前规定 Format Check | PASS，退出 0；存在 workspace 加载警告，未降低检查级别 | `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` |

首次旧代码运行因 Cordonix 30KB 输出限制终止，不能作为完整测试结果；其失败输出和任务子树回收结果单独保留在 `execution-receipts.json`，随后使用已有编译物、quiet logger 和更高输出预算完成上述 14 项失败记录。没有重复任何外部写入。

测试命令均使用项目 Release、`-m:1`、TRX，进程有墙钟上限，挂起诊断为 2～3 分钟，执行器完成后回收本次进程子树。完整 Core、提交前完整 restore/Format 及精确 PR head 的远端 CI 分别记录，不由局部结果推断通过；远端 CI 按实际 PR head 继续核验。

## 边界与后续

本次修复不改变 v7 文件格式、压缩选择或 v2～v6 读取兼容；不发布 NuGet、移动 tag 或部署。公开 5.0.0 与 main 上未发布的 v7 切片分别记账。Issue #407 应在修复 PR 的适用验证通过并合入后关闭；不能只因本机定向通过提前关闭。
