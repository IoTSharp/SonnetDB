# 依赖、分支与线上事项审计（2026-11-15）

本记录对应 `main` 起点 `d90fed9cd1d19d7b66fd7b5734ed762e5fd17ed9`。审计先拉取 `origin` 与 `gitee`，再按提交、文件范围、路线归属和可复现验证判断是否并入；`parity-results`、发布资产和旧 detached worktree 不属于主线合并对象。

## 依赖版本决策

| 来源 | 结果 | 归属 | 依据 |
| --- | --- | --- | --- |
| Dependabot #426 | 保留为当前 `Directory.Packages.props` 的 SkiaSharp 候选，未在本批采用 | 依赖维护 | 4.156 需要独立 Linux native/图片探针，当前批次没有对应证据 |
| #427～#431、#434～#435 | 已合并为统一 10.0.15 基线 | .NET 10 依赖维护 | `restore`、Release build 和格式门禁绑定同一工作树 |
| #432、#436 | 不合并 11.0.0；改为 10.0.15 | .NET 10 依赖维护 | 用户明确要求只使用 .NET 10；#436 的 EF Tools 11.0.0 已由 10.0.15 取代 |

上述变更只改 `Directory.Packages.props` 与 `[Unreleased]` 记录，没有把 .NET 11 资产带入仓库。

## CoAP 与 MQTT 子模块

| 组件 | 当前 gitlink | 路线/issue | 验证与边界 |
| --- | --- | --- | --- |
| IoTSharp.CoAP.NET | `1d6fa2c58777140af39e2c5101a76f1e32b5d0ed` | CoAP C0～C11/C13 已在子库；本批接入 #33/#34/#35 对应的 token、unsigned option、Observe 修复 | 根仓 CoAP focused 6/6；CoAP 核心/测试项目按目标框架验证。完整 solution 的旧 net40/net45 Proxy 项目仍报告 NU1201，C12 性能/AOT、peer interoperability 和 field/soak 继续 UNKNOWN/NOT_RUN |
| MQTTnet.AspNetCore.Routing | `1f1ec475ff3a364ab75f669467c1e2720fdcc73a` | MQTT R0～R5/R7；R6 性能/AOT 仍未闭合 | 子库 restore/build/test 57/57；6 条 MSTEST0017 警告，未修改运行时 routing |

CoAP 当前公开 issue 仍有 #23、#24、#25、#26、#27、#30、#31、#33、#37，公开 PR 为 0；MQTT routing 当前公开 issue/PR 为 0。依赖 open issue 不因 gitlink 更新而自动关闭。

## 历史分支与工作树

- 10 个 GitHub Dependabot head 都以当前 `main` 为父提交；10.0.15 变更已统一收敛，11.0.0 变更保留为不合并候选。
- prunable 的旧 `.codex/worktrees` 与 WorkBuddy detached HEAD 均已是 `main` 祖先；其中的 review zip/外部证据保持独立。
- `.codex-temp` 的 issue227/232/233/236/242 detached HEAD 含过期唯一提交，但当前 `main` 已有后续等价实现；整支合并会回退当前代码，故 KEEP_SEPARATE。未追踪 probe/审计材料没有复制或删除。
- `parity-results` 本地与两个远端引用继续独立保留，未合并、重置或覆盖。

## SonnetDB 线上 issue 队列

GitHub 当前 7 个开放 issue 按创建时间为 #396、#397、#398、#399、#401、#402、#425；开放 Dependabot PR 为 #426～#432、#434～#436。#396/#397/#402 可做受限代码切片；#398/#399 属于外部安装/固定硬件资格，#401 需要先冻结时序精度合同，#425 缺少 IoTSharp 消费者业务模型，均不能用本地构建冒称完成。后续智能体只能领取不重叠文件范围，主智能体负责接口冻结、集成、最终验证和逐项远端状态回读。

## 本批验证

- `dotnet restore SonnetDB.slnx`：PASS。
- `dotnet build SonnetDB.slnx --no-restore -c Release /m:1`：PASS，0 warning/0 error。
- `SonnetDB.Tests` CoAP focused filter：PASS，6/6。
- MQTT 子库：PASS，57/57；保留 6 条 MSTEST0017 警告。
- CoAP 完整 solution：FAIL/边界明确，旧 net40/net45 `CoAP.Proxy` 与 net10 CoAP.NET 的目标框架引用不兼容；不把该失败归因于本批协议修复，也不跳过它。
- NativeAOT 实际发布/运行、公开 NuGet 消费、固定硬件、现场、长稳和远端新提交 CI：本批 NOT_RUN/UNKNOWN。
