---
layout: default
title: "Sparkplug B 接入"
description: "通过内建 MQTT broker 接收 Sparkplug B NBIRTH/DBIRTH/NDATA/DDATA，解析 Protobuf metric 与 alias 并写入 SonnetDB。"
---

# Sparkplug B 接入

SonnetDB 在内建 MQTT broker 上处理 Sparkplug B v3.0 topic namespace。第一阶段支持 `NBIRTH`、`DBIRTH`、`NDATA` 和 `DDATA`，手写解析 Sparkplug protobuf payload，并把标量 metric 直接交给 `BulkIngestor`。该路径不引入 `Google.Protobuf`，也不把强类型 metric 转回 Line Protocol 或 JSON。

## 启用

先创建目标数据库，再配置服务端：

```json
{
  "SonnetDBServer": {
    "Mqtt": {
      "Enabled": true,
      "Sparkplug": {
        "Enabled": true,
        "Database": "factory",
        "MaxPayloadBytes": 1048576
      }
    }
  }
}
```

Sparkplug topic 不携带 SonnetDB 数据库名，因此 `Database` 是必填的显式路由边界。数据库不会由设备自动创建。MQTT 客户端仍使用 SonnetDB 用户或静态 Token 登录，并且必须对目标数据库具有 `write` 权限。

## Topic

| 消息 | Topic | 第一阶段行为 |
| --- | --- | --- |
| Node Birth | `spBv1.0/{group_id}/NBIRTH/{edge_node_id}` | 注册节点并原子替换节点 alias 表，同时写入可映射的标量快照 |
| Device Birth | `spBv1.0/{group_id}/DBIRTH/{edge_node_id}/{device_id}` | 注册设备并原子替换设备 alias 表，同时写入可映射的标量快照 |
| Node Data | `spBv1.0/{group_id}/NDATA/{edge_node_id}` | 按节点 BIRTH alias 解析并写入 |
| Device Data | `spBv1.0/{group_id}/DDATA/{edge_node_id}/{device_id}` | 按设备 BIRTH alias 解析并写入 |

Topic 与消息类型区分大小写。`NDEATH`、`DDEATH`、`NCMD`、`DCMD`、`STATE`、`bdSeq`/`seq` 缺口检测和 rebirth 命令属于 #264，当前不会被当作数据消息接受。

## Metric 映射

每个可写 metric 生成一个 point：

| Sparkplug | SonnetDB |
| --- | --- |
| Node metric | `measurement = edge_node_id` |
| Device metric | `measurement = device_id` |
| Identity | `group_id`、`edge_node_id`、可选 `device_id` tags |
| Metric name | field key；`/` 保留，SonnetDB 保留字符会替换为 `.` |
| Metric timestamp | point time；缺失时回退 payload timestamp，再缺失时使用接收时间 |
| Int8..UInt64、DateTime | Int64；超出 Int64 的 UInt64 跳过 |
| Float、Double | Float64；NaN/Infinity 跳过 |
| Boolean | Boolean |
| String、Text、UUID | String |

`Bytes`、`DataSet`、`Template`、`File`、PropertySet 和数组类型暂不映射。null metric、非标量 metric、没有 BIRTH 上下文的 alias-only DATA 都会跳过并计数；格式损坏的 protobuf 会以 MQTT v5 `PayloadFormatInvalid` 拒绝整条消息。

## 可观测性

`/metrics` 暴露：

- `sonnetdb_sparkplug_messages_total`
- `sonnetdb_sparkplug_metrics_skipped_total`
- `sonnetdb_sparkplug_orphan_metrics_total`
- `sonnetdb_sparkplug_unsupported_metrics_total`

结构化日志使用 `Write.SparkplugIngested` 和 `Write.SparkplugIngestFailed`。日志只记录 topic 和计数/错误原因，不记录 payload 内容。

## 运行边界

- alias 表在 #263 中是进程内状态；服务重启后需要边缘节点重新发送 BIRTH。
- 当前仍是单节点 broker，支持 QoS 0/1，不接受 QoS 2。
- Sparkplug 解码位于 Server 层，`SonnetDB.Core` 不感知 MQTT 或 Sparkplug。
- MQTT source generator 是 MQTT routing 项目的独立 analyzer；它不引用 CoAP generator，两个协议没有运行时依赖。
