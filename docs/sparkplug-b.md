---
layout: default
title: "Sparkplug B 接入"
description: "通过内建 MQTT broker 完成 Sparkplug B 数据、生命周期、Primary Host STATE 与审批命令闭环。"
---

# Sparkplug B 接入

SonnetDB 在内建 MQTT broker 上处理 Sparkplug B v3.0 topic namespace。数据面支持 `NBIRTH`、`DBIRTH`、`NDATA` 和 `DDATA`，生命周期处理 `NDEATH`、`DDEATH`、`bdSeq` 与滚动 `seq`，Primary Host Application 发布 retained `STATE`，并可通过 `NCMD`/`DCMD` 下发经过审批的命令。protobuf codec 为手写实现，不引入 `Google.Protobuf`，强类型 metric 直接进入 `BulkIngestor`。

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
        "MaxPayloadBytes": 1048576,
        "HostId": "sonnetdb-primary",
        "PublishHostState": true,
        "AllowCommands": false
      }
    }
  }
}
```

Sparkplug topic 不携带 SonnetDB 数据库名，因此 `Database` 是必填的显式路由边界。数据库不会由设备自动创建。MQTT 客户端仍使用 SonnetDB 用户或静态 Token 登录，并且必须对目标数据库具有 `write` 权限。

## Topic

| 消息 | Topic | 行为 |
| --- | --- | --- |
| Node Birth | `spBv1.0/{group_id}/NBIRTH/{edge_node_id}` | 注册节点并原子替换节点 alias 表，同时写入可映射的标量快照 |
| Device Birth | `spBv1.0/{group_id}/DBIRTH/{edge_node_id}/{device_id}` | 注册设备并原子替换设备 alias 表，同时写入可映射的标量快照 |
| Node Data | `spBv1.0/{group_id}/NDATA/{edge_node_id}` | 按节点 BIRTH alias 解析并写入 |
| Device Data | `spBv1.0/{group_id}/DDATA/{edge_node_id}/{device_id}` | 按设备 BIRTH alias 解析并写入 |
| Node Death | `spBv1.0/{group_id}/NDEATH/{edge_node_id}` | 校验 `bdSeq`，标记节点及其设备离线 |
| Device Death | `spBv1.0/{group_id}/DDEATH/{edge_node_id}/{device_id}` | 标记设备离线 |
| Node Command | `spBv1.0/{group_id}/NCMD/{edge_node_id}` | 自动 Rebirth 或审批后的节点命令 |
| Device Command | `spBv1.0/{group_id}/DCMD/{edge_node_id}/{device_id}` | 审批后的设备命令 |
| Host State | `spBv1.0/STATE/{host_id}` | retained `ONLINE`/`OFFLINE` |

Topic 与消息类型区分大小写。订阅不接受通配符；edge node 使用具有目标数据库 `read` 权限的 MQTT 凭据订阅其精确 NCMD/DCMD 和 Primary Host STATE topic。

## 生命周期与 Rebirth

- `NBIRTH` 必须携带 `bdSeq`，并以 `seq=0` 建立 edge node 会话；缺失或非法时触发 Rebirth。
- 后续 DBIRTH/NDATA/DDATA 按 edge node 共享的 `seq` 检查，`255` 后回绕为 `0`。
- 序列缺口、节点没有有效 NBIRTH、设备没有有效 DBIRTH 或 alias-only DATA 找不到 BIRTH 上下文时，当前数据不落库，并只发送一次 `Node Control/Rebirth=true` NCMD；新的 NBIRTH 到达后解除去重状态。
- DBIRTH/NDATA/DDATA 必须携带 `seq`。NDEATH 的 `bdSeq` 必须与当前会话一致；过期或缺失 `bdSeq` 的死亡消息不会覆盖新会话。DDEATH 只关闭对应设备。
- alias 快照保存在 `DataRoot/.system/sparkplug-aliases-v1.bin`。写入采用临时文件替换；NBIRTH 会淘汰同一 edge node 上一会话的设备 alias，再由后续 DBIRTH 重建。

## Primary Host 与命令审批

`PublishHostState=true` 时，broker 启动后发布 retained `spBv1.0/STATE/{HostId}=ONLINE`，正常停止前发布 `OFFLINE`。这给配置了 Primary Host ID 的 edge node 提供明确的数据流开关。

自动 Rebirth 属于协议自愈，不受人工命令开关影响。人工 NCMD/DCMD 默认拒绝；必须同时满足：

1. `AllowCommands=true`；
2. MQTT 身份对目标数据库拥有 `Admin` 权限；
3. MQTT v5 PUBLISH 带 user property `sndb-approved=true`。

命令 payload 仍使用标准 Sparkplug protobuf，服务端先校验大小和 wire format，再交给 broker 下发；命令不会进入遥测数据库。

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
- `sonnetdb_sparkplug_lifecycle_messages_total`
- `sonnetdb_sparkplug_sequence_gaps_total`
- `sonnetdb_sparkplug_rebirth_commands_total`

结构化日志使用 `Write.SparkplugIngested` 和 `Write.SparkplugIngestFailed`。日志只记录 topic 和计数/错误原因，不记录 payload 内容。

## 运行边界

- 生命周期状态是单实例内存态；服务重启后的 retained ONLINE 会要求配置了 Primary Host 的 edge node 重新发送 BIRTH，持久 alias 用于恢复和诊断。
- 当前仍是单节点 broker，支持 QoS 0/1，不接受 QoS 2。
- Sparkplug 解码位于 Server 层，`SonnetDB.Core` 不感知 MQTT 或 Sparkplug。
- MQTT source generator 是 MQTT routing 项目的独立 analyzer；它不引用 CoAP generator，两个协议没有运行时依赖。
