---
title: 六大内置技能：从聚合查询到批量导入的专家知识
categories: SonnetDB,Copilot,技能
draft: false
---

SonnetDB Copilot 的技能库以 Markdown 保存专题规则，通过 YAML front matter 描述名称、关键词与建议工具。当前目录有 24 份资料，本篇继续介绍原稿的六个主题。技能是检索上下文，不是工具权限授权，也不保证其中所有历史示例已经通过当前版本验证。

## 查看实际技能

```bash
sndb copilot skills list --endpoint http://127.0.0.1:5080
sndb copilot skills show query-aggregation --endpoint http://127.0.0.1:5080
```

服务端应已配置 Copilot；按端点要求提供自己的凭据。定义形式例如：

```yaml
---
name: query-aggregation
description: 时间窗口聚合查询的最佳实践
triggers:
  - aggregation
  - group by
  - 聚合
requires_tools:
  - query_sql
  - describe_measurement
---
```

## 六个主题怎样使用

`query-aggregation` 引导先核对 schema，再添加设备和时间过滤、明确桶宽与输出别名。

```sql
SELECT avg(temperature) AS avg_temp
FROM reactor
WHERE device = 'r1' AND time >= now() - 1h
GROUP BY time(1m);
```

`pid-control-tuning` 提供整定思路。实际 SQL 是 `pid_series(field,setpoint,kp,ki,kd)` 与六参 `pid_estimate`，不能沿用旧稿未核实的 pid_step/pid_replay 或把分析函数当 PLC 闭环。

`forecast-howto` 说明 `FROM forecast(measurement,field,horizon,'linear')` 等表值函数；预测质量要另行评估。

`troubleshoot-slow-query` 帮助检查时间范围、TAG 过滤、返回行数和扫描路径。优化建议应通过目标数据的执行计划与基准验证。

`schema-design` 区分维度 TAG 与采样 FIELD。基数和资源上限应结合真实负载测量，不能把“百万 series 必须拆表”当作通用合同。

`bulk-ingest` 组织批量摄取策略。实际接口是按数据库隔离的 `/v1/db/{db}/...`，批大小与并发受请求字节、服务预算与机器容量约束，不采用无依据的固定 5万–50万行或 4–8 路通用保证。

## 加载与权限

技能检索由已配置 embedding 路径完成。确定性 hash 回退不等于语义召回质量。`requires_tools` 是资料声明，当前 typed MCP 仍只读并受凭据 grant、schema 和结果预算限制。

正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与主线新增规则分开核对。本文未执行 CLI、模型调用或数据库写入。

参考：[技能目录](https://github.com/IoTSharp/SonnetDB/tree/main/copilot/skills)、[CLI 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/cli-reference.md)、[typed MCP](https://github.com/IoTSharp/SonnetDB/blob/main/docs/mcp-contract.md)、[批量摄取](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)。
