---
title: SonnetDB 路线图与未来展望：M17 可观测性、M18 VS Code 与社区共建
categories: SonnetDB,路线图,开源
draft: false
---

本文标题保留早期 M17/M18 的选题，正文按当前仓库更新。SonnetDB 已发布 4.0.0，并从时序库扩展为九模型引擎；可观测性与 VS Code 的已有实现不能再写成未来空壳功能。

## 从里程碑名称看实际证据

当前 README 描述时序、关系表、KV、JSON 文档、全文、向量、对象存储、MQ 与 Graph Beta。各模型保留自己的语义，不能因为共用进程就宣称完整 SQL 标准、全量 S3 或全部 GQL/Cypher 兼容。

旧 M17/M18 的完成代码范围在 CHANGELOG 与历史路线图追溯。新工作围绕 AI 应用、持续计算、存储性能与统一管理工作台继续推进，状态以当前 ROADMAP 的具体切片为准。

## 当前用户旅程仍需闭环

Web Admin、Studio 与 VS Code 的共同工作台已经有合同和页面切片。三宿主的真实 Server 权限、安装、外部浏览器交接和发行物验证仍需分别取得证据，不能把一个页面 PASS 写成全平台完成。

AI/MCP 已有 typed 工具和 provider 路径，下一步需要真实模型质量、成本与数据外发边界。Graph 保持 Beta，固定硬件、语义对拍与长稳门禁并未因为 UI 可操作而自动关闭。

单库备份覆盖数据库目录；Server MQ 使用实例级 `.system/mq`，不能把单库恢复写成九模型一致性恢复。

## 参与一个具体、可复核的工作项

```bash
git clone https://github.com/IoTSharp/SonnetDB.git
cd SonnetDB
dotnet restore SonnetDB.slnx
dotnet build SonnetDB.slnx -c Release --no-restore
```

先阅读 HANDOFF、AGENTS 与 ROADMAP，再选择对应 issue。长构建和测试应设置超时与取消，避免未经确认启动全量重负载任务。提交前还必须执行仓库规定的完整 Format Check。

报告问题时，提供版本、最小输入、实际输出与期望输出。性能问题附原始样本和硬件；恢复问题附具体持久化边界；AI 问题说明实际模型和 provider，合成测试不能替代真实质量证据。

## 社区贡献的价值

文档、示例、跨平台验证和真实用户旅程同样能推动项目。贡献应把“实现存在”“本机测试”“真实服务”“安装”“生产门禁”分开描述，避免重复包装已完成事项。

本文没有宣布分布式集群、自动告警 DSL 或新的发行承诺。未来方向以明确进入路线图并具有验收标准的工作项为准。

参考：[ROADMAP](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md)、[README](https://github.com/IoTSharp/SonnetDB/blob/main/README.md)、[贡献约束](https://github.com/IoTSharp/SonnetDB/blob/main/AGENTS.md)、[4.0.0 发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
