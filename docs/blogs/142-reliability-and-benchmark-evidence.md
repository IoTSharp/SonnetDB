---
title: SonnetDB 性能与可靠性文章怎么写：把数字和证据放在一起
categories: SonnetDB
draft: false
---

数据库性能文章需要让读者找到数字的来源，并判断它是否适用于自己的部署。SonnetDB 的嵌入式引擎、Server/HTTP、客户端和固定目标硬件是不同路径；吞吐、延迟、内存和恢复结果都应带上版本、提交、配置、语料及原始样本。

本文是证据整理教程，不新增数据库跑分，也不重新发布旧的产品对比结论。**2026-10-08 复核源码基线：`3027a1aac9a5cea2d625c94cefe2239a372dd060`。** 本次检查专题、代码和发布工具入口，没有执行数据库基准、断电演练或长期负载。GitHub `v4.0.0` 与 NuGet `SonnetDB.Core` 4.0.0 已发布；源码文档保留的“候选”字样是历史写作状态，正式发行存在也不意味着全部现场证据已齐。

## 先为结果确定证明范围

| 结果类型 | 可以证明什么 | 不能代替什么 |
|---|---|---|
| 功能测试 | 某个输入、并发或恢复场景符合合同 | 固定硬件吞吐、长稳、真实业务收益 |
| 开发机基准 | 该机器、配置和负载下的耗时、分配或吞吐 | Server 网络路径、其他机器及生产承诺 |
| 真实服务旅程 | 某个客户端与 Server 的操作路径得到实际结果 | 完整安装、升级、恢复及所有权限组合 |
| 现场门禁报告 | 指定硬件、语料、时间窗和操作的验收结果 | 未执行的模型、平台或更大容量 |

旧文章里的峰值吞吐、毫秒数或“百倍提升”，如果无法同时定位提交与原始报告，应作为历史背景或删去。合成向量可以用于算法与预算检查；真实 Recall@K、语义效果及模型成本需要真实模型、语料与标签，hash fallback 或 tiny fixture 不计入。

## 步骤一：给一次测量保存身份

在一个专用、干净的测试 checkout 中冻结提交、包版本和配置。避免在并发开发树上把中途变化的文件当作同一候选。下面是 PowerShell 7 的只读检查示例：

```powershell
$PSVersionTable.PSVersion
git rev-parse HEAD
git status --short
dotnet --info
Get-CimInstance Win32_Processor |
    Select-Object Name, NumberOfCores, NumberOfLogicalProcessors
Get-CimInstance Win32_ComputerSystem |
    Select-Object TotalPhysicalMemory
```

同时记录存储介质、OS、数据量、随机种子、schema、索引、线程与并发数、冷启动还是热缓存、是否 flush/fsync，以及客户端实际使用 embedded、REST 或 Frame。远程结果还要记录 Server 版本与网络条件。不能只保存客户端包版本。

保存原始日志、CSV、错误样本及准确命令。命令成功不表示没有跳过测试；平均值也不能推导 P95/P99。分位数需要保留请求级原始样本与计算方法，注明失败请求如何处理。

## 步骤二：选择对应的基准入口

仓库 `tests/SonnetDB.Benchmarks/Program.cs` 使用 BenchmarkDotNet，并提供部分专用 evidence 命令。若要研究编码解码的分配，可在具备 .NET 10 SDK 的隔离环境选中实际基准类：

```powershell
dotnet run -c Release `
    --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj `
    -- --filter '*CodecSpecialization*' --job Dry
```

`Dry` 适合确认入口和输出形状，样本太少，不能作为稳定性能结论。运行前限制数据规模与进程资源，单次命令设定明确的超时；首次构建、预热和测量时间分别记录。若超过预算，取消并保留超时日志，不能把不完整输出拼成成功报告。

输出应能对应到所选 benchmark、runtime、耗时及分配列。专题中的快路径与旧组合式路径，可以作为本地比较对象；已有短跑分配数字没有绑定本次机器和原始报告，就不在本文重新引用。减少分配并不自动意味着端到端吞吐同比提升，Server 序列化、网络和磁盘持久性仍可能主导延迟。

## 步骤三：用源码解释恢复机制

当前 Segment writer 写入 v6，将向量索引与部分聚合扩展整合进 `.SDBSEG` extension section；header 的 mini-footer 保存摘要信息，reader 对尾部形状进行检查，并按合同兼容旧段。摘要有助于检测与诊断损坏，不承诺任意坏文件都能自动修复。

需要把不同机制分开：

- WAL replay 检查记录并在首个坏 record 停止处理尾部；checkpoint 还要核对对应 segment 是否存在及长度是否一致。
- **Compaction 的段替换 manifest** 保存 pending/committed 状态。未提交 replacement 不应替代旧 source；已提交且 replacement 文件有效时，残留的旧 source 不应再次加载。replacement 缺失或无效仍须遵循恢复合同，不能仅看 committed 字样。
- Retention 已提交的 drop 也有恢复语义；文件残留不应让已删除段重新出现。
- MemTable 的增量统计和快照复用，以及 Segment reader/index 快照，属于查询与内存生命周期机制，不能直接推出断电零丢失。

因此不能笼统把段替换的 pending/committed marker 描述为所有 flush 的发布机制。可核查演练应限定到一个失败点：准备有限数据、记录已确认写入集合，在专用数据库上注入约定故障，重开后比较行、重复、缺失、诊断和遗留文件，再保存前后配置与原始日志。进程 kill 是一种故障模型，不能替代介质断电、OS 缓存与存储设备保证。

关闭时 final flush 失败也不能只观察“Dispose 没抛异常”。源码提供 `LastError` 与 `DiagnosticEvent` 的诊断合同，演练要读取实际错误；仅正常退出不等于持久性承诺已满足。

## 步骤四：读懂 M43 汇总的状态

`eng/verify-m43-release-evidence.ps1` 会按固定入口重新验证证据，不能在清单中直接写 `PASS`。例如，已准备干净的测试 checkout 后，可以检查仓库模板的边界：

```powershell
pwsh -NoProfile -File eng/verify-m43-release-evidence.ps1 `
    -ManifestPath eng/m43-release-evidence.template.json `
    -CommitSha (git rev-parse HEAD) -Version 4.0.0 `
    -OutputPath artifacts/m43-release-evidence/report.json -AllowNotReady
```

模板把八项证据全部声明为 deferred。它用于解释缺口与报告形状，预期汇总为 `DEFERRED`，不是“八项已经执行并通过”。本文没有运行这条命令。

| 状态 | 解释 |
|---|---|
| `PASS` | 当前 verifier 在明确身份与范围内接受原始证据 |
| `NOT_READY` | 缺项、损坏、身份错配或 verifier 失败等阻止本次汇总通过 |
| `DEFERRED` | 显式延后，附原因；不能计为已执行 |
| `NOT_RUN` | 某项检查尚未执行，按所属报告的原始字段保留 |

`NOT_READY` 优先于 `DEFERRED`，八项全部 PASS 才能汇总 PASS。`-AllowNotReady` 允许保存非 PASS 报告，不会把非 PASS 改为成功证据。SHA-256 证明文件对账一致，也不能单独证明机器身份或报告真实性。

截至所复核的合同，M19 需要四档固定目标原始报告及身份绑定；M25 的百万/千万报告还存在外部 attestation 边界；M41/M42 的固定双架构和 168 小时证据不能由局部 SQL/KV 短测代替。真实模型质量、成本、双网和干净 Windows 安装分别验收；原生属性图仍为 **Graph Beta**。

M20 连续七天 scheduled 观察按 UTC 日期核对，手动 light/full 不替代连续 scheduled。自 2026-09-27 起，该七天观察是正式发布流程的非阻断项；历史失败仍保留，M43 也继续单列其未完成状态。不能因没有七天证据就否认已经发布的 4.0.0，也不能因发行成功就补记七天 PASS。

## 公开参考与复核边界

下面的资料固定到已公开文档基线 `c785f0479686a3d6584787e1a2b40bd67b0e38c5`。源码与工具入口另以本文开头的开发提交复核；具体实验应保存自己的原始收据，不能复用旧文中的测试总数作为当前版本结论。

- [性能与可靠性变更](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/performance-reliability-updates.md)
- [M43 发布证据合同](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/m43-release-evidence.md)
- [4.0.0 发行说明历史稿](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/releases/4.0.0.md)
- [4.0.0 正式 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)
