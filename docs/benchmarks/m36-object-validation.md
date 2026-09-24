---
layout: default
title: "M36 #323 对象分页与文件流现场验证前置"
description: "对象高变更率分页本机预检、固定硬件容量报告与文件传输现场验证的边界。"
permalink: /benchmarks/m36-object-validation/
---

# M36 #323 对象分页与文件流现场验证前置

## 当前状态

对象分页的实现和本地回归见 [OBJECT-001 验证记录](../audits/object-pagination-20260906.md)。本页只提供固定硬件、高变更率和容量报告之前的可重复本机预检。它不是正式容量报告，也不能关闭 #323、#322 或 M36。

仓库的 `ObjectStorageModelBenchmark` 测量固定 256 个 64 KiB 对象的完整读取与 Range 读取；它不覆盖分页、交错写入/删除、高变更率、Server/SDK/CLI、multipart、resume 或 checksum。因此不能用其 BenchmarkDotNet 数字宣称本条验收通过。

## 本机预检

`m36-object-validation-evidence` 在独占临时数据库中建立固定对象集合。每个样本先覆盖一个对象，每第八个样本额外删除并重建该对象，随后请求 `ListObjects(prefix: "objects/", maxKeys: 64)`。报告保留每次分页的原始耗时、当前线程分配和返回数，并检查第一页的 ordinal 排序与完整页数。

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- `
  --m36-object-validation-evidence --quick --output artifacts/m36-object-validation

dotnet run -c Release --project tests/SonnetDB.Benchmarks -- `
  --m36-verify-object-validation artifacts/m36-object-validation/m36-object-validation.json
```

quick 使用 256 个对象和 64 个样本；未指定 `--quick` 使用 8,192 个对象和 1,024 个样本。两者均为单进程嵌入式预检，不能用缩规模或本机结果替代受保护固定硬件容量证据。

每份此 runner 写出的报告都固定为：

| 字段 | 值 | 含义 |
| --- | --- | --- |
| `localPrecheck` | `PASS` | 当前受控分页预检通过。 |
| `fixedHardware` | `NOT_READY` | 未在受保护固定目标机执行。 |
| `capacity` | `NOT_RUN` | 没有正式容量结论。 |
| `transferValidation` | `DEFERRED` | 文件流恢复、校验和与并发传输未由分页预检替代。 |
| `releaseDecision` | `DEFERRED` | 任何本机结果都不形成发布放行。 |

verifier 会拒绝这些字段被改写为本机通过状态，并复核原始分页样本数、变更数和基本测量不变量。

## 现场阻塞与正式证据

正式报告仍缺少受保护的固定目标硬件、冻结 workload 合同、容量档位、真实 Server/SDK/CLI 路径和可追溯 artifact。本机 Windows 或笔记本硬件，以及缩规模压力，不能成为替代证据。

在目标环境具备前，必须保持 `NOT_READY`。目标机采集至少应单独记录：固定的 commit 和硬件/存储清单、对象数与对象大小分布、写入/覆盖/删除与分页 reader 的并发比例及持续时间、分页 P50/P95/P99、吞吐、RSS/托管内存/GC/进程 I/O、分页排序/重复/漏项对账，以及重开后的数据与分页恢复。文件流还必须以真实大文件记录流式内存上界、multipart、checksum、取消、断点续传、重试和逐对象错误；不能从对象元数据分页结果推导。
