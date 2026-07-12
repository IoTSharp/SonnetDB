# M30 多协议接入基准

本文记录 Milestone 30 #268 的 Sparkplug B 解码与 CoAP route 热路径基线。两项都是协议层 microbenchmark，
用于版本间回归，不代表 UDP/TCP 网络、鉴权、WAL、MemTable 或磁盘持久化的端到端吞吐。

## 环境与口径

- 日期：2026-07-12
- 系统：Windows 11 25H2，`10.0.26200.8737`
- CPU：Intel Core Ultra 9 185H，16 物理核 / 22 逻辑核
- SDK / Runtime：.NET SDK `10.0.301`，.NET Runtime `10.0.9`，x64 RyuJIT
- BenchmarkDotNet：`0.15.8`，ShortRun，3 次 warmup、3 次 iteration、1 次 launch
- Sparkplug：固定 NDATA protobuf，metric 均为有名称、显式 timestamp 的 Double 标量；测量完整 payload 解码和 `Point` 映射
- CoAP：129 个 route，目标位于最后；测量 endpoint matcher 与启动期 resource tree 构建，不包含数据报收发和落库

可复现命令：

```powershell
dotnet run -c Release --project tests\SonnetDB.Benchmarks\SonnetDB.Benchmarks.csproj -- --filter "*SparkplugDecode*" --job short
dotnet run -c Release --project extensions\IoTSharp.CoAP.NET\CoAP.Benchmarks\CoAP.Benchmarks.csproj -- --job short
```

## 结果

| 场景 | Mean | 单次分配 | 派生吞吐 |
| --- | ---: | ---: | ---: |
| Sparkplug decode + map，10 metrics/payload | 6.647 us | 11.34 KB | 约 150 万 metrics/s |
| Sparkplug decode + map，100 metrics/payload | 61.022 us | 108.08 KB | 约 164 万 metrics/s |
| CoAP `MatchLastRoute`，129 routes | 1.875 us | 128 B | 约 53 万 matches/s |
| CoAP `BuildResourceTree`，129 routes | 13.282 us | 8,664 B | 启动期操作，不换算请求吞吐 |

ShortRun 每项只有 3 个样本，置信区间较宽。这组数字只用于确认热路径量级和后续版本回归；发布容量评估应在目标硬件上另跑真实 MQTT/CoAP/UDP 客户端，固定 payload、并发、WAL 和 flush 策略后报告端到端成功写入行数。

## 验收结论

- 100-metric Sparkplug payload 的解码与映射接近线性扩展，约 `0.61 us/metric`；当前主要成本包含每个 `Point` 的 tag/field 对象分配。
- CoAP route matcher 在 129 routes 且目标位于末尾时仍处于微秒级，单次仅分配 128 B；resource tree 构建只发生在服务启动/重建阶段。
- 正确性由 `SparkplugPayloadReaderTests`、`CoapEndpointTests` 与 `ProtocolIngestParitySuite` 独立保证；基准不替代协议和落库测试。
