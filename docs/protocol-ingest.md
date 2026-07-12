---
layout: default
title: "设备与遥测协议接入"
description: "SonnetDB MQTT、Sparkplug B、CoAP、Line Protocol HTTP 与 UDP 的落库路径、安全边界和选型指南。"
---

# 设备与遥测协议接入

SonnetDB Server 提供 MQTT、Sparkplug B、CoAP、Line Protocol HTTP 与 Line Protocol UDP 五类设备或遥测入口。
这些入口只负责协议解析、路由、鉴权和传输语义，最终都进入既有时序写入内核；不会为不同协议维护独立的表、索引或存储格式。

## 接入与落库矩阵

| 入口 | 地址或路由 | payload | 共享落库路径 | 目标数据库与 measurement |
| --- | --- | --- | --- | --- |
| MQTT 内建 broker / 外部 client | `db/{db}/m/{measurement}` | Line Protocol、JSON points、BulkValues | `SonnetMqttMeasurementIngestor` → `BulkIngestEndpointHandler.IngestPayload` → 对应 reader → `BulkIngestor` | topic 指定数据库和 measurement；数据库必须已存在 |
| Sparkplug B | `spBv1.0/{group}/{type}/{edge}/[{device}]` | Sparkplug protobuf | `SparkplugPayloadReader` → `BulkIngestor` | 配置固定数据库；edge/device 映射 measurement，metric 映射 field |
| CoAP / coaps | `db/{db}/m/{measurement}` | Line Protocol、JSON points、BulkValues | `SonnetCoapMeasurementIngestor` → `BulkIngestEndpointHandler.IngestPayload` → 对应 reader → `BulkIngestor` | URI path 指定数据库和 measurement；数据库必须已存在 |
| Line Protocol HTTP | `POST /write?db=...` 或 `POST /api/v2/write?bucket=...` | Line Protocol | `LineProtocolReader` → `BulkIngestor` | query 指定数据库；每行文本指定 measurement |
| Line Protocol UDP | 配置的 UDP 端口，默认 `8089` | Line Protocol | `LineProtocolReader` → `BulkIngestor` | 配置固定数据库；每行文本指定 measurement |

MQTT 与 CoAP 的 route measurement 是平台侧路由边界，即使 LP 行携带其他 measurement，也会写入 route 指定的 measurement。
HTTP 与 UDP 没有 route measurement，measurement 必须由每行 LP 文本提供。Sparkplug metric 已是强类型数据，直接产出 `Point`，不会先回写为 LP 文本再解析。

`tests/SonnetDB.Parity/runner/ProtocolIngestParitySuite.cs` 使用同一份 Line Protocol payload，经真实 HTTP、UDP、MQTT 和 CoAP 监听口写入后比较 SQL 查询结果，作为 M30 #268 的协议落库等价性验收。Sparkplug 因 payload 是 protobuf 而不参与“同一 LP 字节”对拍，其强类型映射由专用 reader/lifecycle 测试与[M30 多协议接入基准](benchmarks/m30-protocol-ingest.md)覆盖。

## 安全、QoS 与可靠性边界

| 入口 | 鉴权与加密 | 确认 / QoS | 背压与重试 | 适用边界 |
| --- | --- | --- | --- | --- |
| MQTT | 用户或静态 token；生产环境由 TLS listener / 反向代理保护 | QoS 0/1；QoS 2 不支持 | broker 提供连接与 QoS 语义；不提供集群或跨节点 session | 设备长连接、已有 EMQX/Mosquitto、需要 topic 路由或订阅 |
| Sparkplug B | 继承 MQTT 身份与目标数据库权限 | 继承 MQTT QoS；补充 BIRTH/DEATH、`seq`、rebirth 和 Primary Host STATE | 生命周期状态机检测序列缺口；非标量 metric 跳过并计数 | 工业 SCADA、Sparkplug 设备发现和 alias 压缩 |
| CoAP | query token；可启用 DTLS PSK `coaps` | CON 有响应与重传；NON 不保证确认 | 支持 Block1 大 payload；服务端返回 CoAP 错误码 | 受约束设备、UDP REST 语义、需要轻量确认或 DTLS |
| Line Protocol HTTP | Bearer token；使用 HTTPS | HTTP `204` 或明确错误响应 | TCP/HTTP 流控；客户端可按状态码重试 | Telegraf、批量写、需要权限、错误反馈和可靠交付 |
| Line Protocol UDP | 无鉴权、无加密 | 无 ack，fire-and-forget | 无应用层背压、重传或批确认；内核接收缓冲满时可能丢包 | 可信内网、本机 agent、允许丢包的最低开销遥测 |

UDP listener 默认关闭。不要把它直接暴露到公网，也不要把“UDP 发送成功”视为数据已经持久化；发送方只能得知数据报已交给本机网络栈。需要可审计身份、逐批错误反馈或可确认交付时，应选择 HTTPS、MQTT QoS 1 或 CoAP CON/coaps。

## Line Protocol UDP 配置

```jsonc
{
  "SonnetDBServer": {
    "LineProtocolUdp": {
      "Enabled": true,
      "Port": 8089,
      "Database": "iot",
      "MaxDatagramBytes": 65507,
      "Precision": "ms"
    }
  }
}
```

- `Database` 必须是合法且已创建的数据库名，监听器不会通过数据报自动创建数据库。measurement 沿用统一写入内核的 schema 自动创建/扩展规则；生产环境仍建议预先执行 DDL 固定 tag/field 类型。
- `MaxDatagramBytes` 范围是 `1..65507`。超过限制的数据报会被完整丢弃。
- `Precision` 支持 `n/ns`、`u/us/µs`、`ms`、`s`；它只解释显式 timestamp，不改变无 timestamp 行的服务端时间盖章。
- payload 必须是严格 UTF-8。非法 UTF-8、坏 LP、schema 冲突或目标数据库不存在时，当前数据报被丢弃，监听循环继续接收后续数据报。
- 写入计入统一 `sonnetdb_rows_inserted_total` 指标。结构化日志事件包括 `Write.LineProtocolUdpStarted`、`Write.LineProtocolUdpOversized`、`Write.LineProtocolUdpInvalidUtf8`、`Write.LineProtocolUdpIngestFailed` 和 `Write.LineProtocolUdpIngested`；日志不记录 payload 内容。

Telegraf 的 `influxdb` UDP output 应把数据报大小控制在 SonnetDB 的 `MaxDatagramBytes` 以内，并使 timestamp precision 与服务端配置一致。高峰流量下应优先减小单包、增加发送端缓冲，并通过写入行数与发送行数的差值监控丢包；UDP 本身无法提供逐包成功率。

## 选型

| 需求 | 推荐入口 |
| --- | --- |
| 已有 Telegraf 或 InfluxDB Line Protocol 客户端，要求错误响应 | Line Protocol HTTP |
| 本机或可信内网的高频、可丢包指标 | Line Protocol UDP |
| 设备保持长连接、需要 QoS 1 或双向 topic | MQTT |
| 工业设备使用 Sparkplug birth/death、alias 和 rebirth | Sparkplug B |
| MCU / 受约束设备使用 UDP，仍需要确认或 DTLS PSK | CoAP CON / coaps |
| 大批量应用写入或服务间高吞吐数据面 | ADO.NET `Protocol=auto/frame-http2` 或 REST bulk；不通过设备协议绕行 |

详细配置与协议约束分别见[二进制帧协议](frame-protocol.md)和[Sparkplug B 接入](sparkplug-b.md)。
