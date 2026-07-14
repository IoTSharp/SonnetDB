---
layout: default
title: "使用 SonnetDB 构建工业 AI 应用"
description: "SonnetDB 多模型数据引擎在工业边缘和 Industrial Data Agent 场景中的架构、用法与路线。"
permalink: /industrial-ai-applications/
---

SonnetDB 的核心产品定位是：

> **八种数据模型，一套引擎。**

工业边缘是 SonnetDB 的应用场景之一，而不是产品类别。在工厂局域网、边缘网关、Windows 工控机、离线采集程序和私有化部署场景里，可以用一个 SonnetDB 承接设备指标、配置、状态、文档、检索、对象和消息，并通过授权的 Copilot / MCP / Agent 接口使用这些数据。

## 为什么工业 AI 需要本地数据引擎

工业 AI 应用通常不只是调用一个大模型。真正的现场链路更像这样：

```text
设备报警
  -> Agent 查看当前数据库 schema
  -> 自动生成 SonnetDB SQL
  -> 查询过去一段时间的设备指标、状态和维护记录
  -> 判断异常模式
  -> 生成排查报告和维修建议
  -> 写操作必须由用户确认
```

这类系统对数据底座有几个要求：

- **本地优先**：现场网络不稳定，设备数据不能完全依赖云端。
- **低运维**：中小工业项目很难长期维护 PostgreSQL、InfluxDB、Redis、对象存储、搜索服务和消息队列一整套组件。
- **.NET 原生**：大量上位机、采集程序、MES / SCADA 周边系统仍然基于 C#、ASP.NET Core、WPF、WinForms 或 Windows 服务。
- **Agent 可调用**：AI 需要稳定的 schema、SQL、MCP 工具、权限边界和写入审批，而不是只拿到一个聊天框。

SonnetDB 的目标是在这些约束下提供一个“先解决 80% 本地数据需求”的工程底座。

## 参考架构

```text
PLC / Sensor / Gateway / MQTT
  -> Collector or ASP.NET Core service
  -> SonnetDB embedded mode or SonnetDB Server
  -> SQL / ADO.NET / EF Core / HTTP / MCP
  -> CopilotDock or external Industrial Data Agent
  -> report, diagnosis, maintenance suggestion, approved write-back
```

小型项目可以直接在采集进程中使用：

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./edge-data",
});

SqlExecutor.Execute(db, """
CREATE MEASUREMENT device_temperature (
    device_id TAG,
    line TAG,
    value FIELD FLOAT
)
""");
```

需要多用户、Web Admin、HTTP API、CopilotDock 或 MCP 时，可以运行 SonnetDB Server：

```bash
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data iotsharp/sonnetdb:latest
```

## Industrial Data Agent 场景

一个典型的 SonnetDB Agent 不应该只回答“如何写 SQL”，而应该围绕工业数据闭环工作：

1. 用户提问：哪台设备今天最异常？
2. Agent 调用 `list_measurements` 和 `describe_measurement` 读取 schema。
3. Agent 起草 SonnetDB SQL，查询最近 24 小时的温度、电流、振动或报警数据。
4. Agent 通过 `query_sql` / `explain_sql` 分析结果和查询代价。
5. Agent 给出异常设备、时间范围、可能原因和下一步检查建议。
6. 如果需要创建 measurement、插入标注或执行维护 SQL，必须进入写入审批。

示例提问：

```text
分析过去 24 小时温度异常的设备，按异常程度排序，并给出可能原因。
```

```text
查询 pump-03 今天 08:00 之后电流和温度的变化，判断是否可能堵转。
```

```text
根据最近 7 天 vibration 的 p95 和 temperature 的趋势，列出需要优先巡检的设备。
```

## AI-ready 文档与示例

为了让开发者和 AI Agent 更容易理解 SonnetDB，仓库维护以下入口：

- `llms.txt`：给模型和 Agent 读取的项目定位、适用场景和关键链接。
- `README.md` / `README.en.md`：项目门面，统一说明多模型数据引擎及其能力边界。
- `docs/sql-reference.md`：SonnetDB SQL 方言的事实来源。
- `docs/sql-cookbook.md`：可直接复制的查询模板。
- `docs/web-workbench.md`：Studio、CopilotDock、上下文感知和写入审批说明。
- `copilot/skills/`：Copilot 技能模板。

后续示例应优先覆盖：

- MQTT / HTTP ingest -> SonnetDB -> Copilot 问答。
- 设备异常检测和维修建议。
- 轻量 MES / SCADA 本地数据侧车。
- 上层工业平台 + SonnetDB 的边缘数据底座组合；具体 IoTSharp 联合样例在 IoTSharp 仓库维护。
- 私有化部署中的本地模型 / 云模型切换。

## 产品路线

短期重点是合同和可发现性：

- README、文档首页和 `llms.txt` 统一使用“八种数据模型，一套引擎”的核心定位。
- 工业 AI 文档和示例明确自身是重点场景，不替代 SonnetDB 的通用引擎定位。
- 示例、文档和 Copilot starter prompt 优先使用工业设备、边缘网关、诊断和运维场景。

中期重点是 Industrial Data Agent：

- Copilot 从 SQL 助手升级为面向设备数据的 Agent 工作流。
- MCP 工具围绕 schema、sample rows、SQL draft、read-only query、explain、诊断报告和写入审批稳定化。
- 会话历史、页面上下文、权限选择、模型选择和 Copilot 指标继续产品化。

长期重点是 provider-neutral 和受治理的集成：

- Chat / embedding provider 不绑定单一模型供应商。
- 支持 OpenAI-compatible 网关、云端模型、本地 Ollama / vLLM 和私有化模型部署。
- 与上层工业平台、Windows 服务安装器、离线激活、审计、备份、监控和企业支持路线协同。
