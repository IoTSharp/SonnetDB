# Codec 专用化：BlockDecoder 为什么选择手写 fast path

SonnetDB 当前 V2 解码路径把时间戳和值直接写入 DataPoint 目标视图，减少组合解码产生的中间数组。对于有限的 FieldType/BlockEncoding 组合，手写分支让布局、边界和错误处理集中在 codec 内部。

本文依据当前 TimestampCodec、ValuePayloadCodecV2、BlockDecoder 和基准源码整理。软件正式 4.0.0 与 segment/payload 格式版本分别核对；减少中间对象不会自动改变落盘格式。

## 旧组合路径的成本

一种通用组合方式先把 timestamp 解码到 long 缓冲，复制到 DataPoint，再将 value 解码成 FieldValue 数组，最后合入同一目标。这个流程清楚，但保留了额外数组或池租借与两次填充。

当前快路径让 timestamp overload 接受 `Span<DataPoint>`，先填时间戳，再由 value codec 填字段值。

```text
timestamp payload -> DataPoint.Timestamp
value payload     -> 同一 DataPoint.Value
```

这是内部流程示意，应用正常使用 BlockDecoder，不需要直接猜测 descriptor 或 payload encoding。

## 两个直接填充入口

`TimestampCodec.ReadDeltaOfDelta(payload,Span<DataPoint>)` 负责目标时间戳。`ValuePayloadCodecV2.DecodeInto(fieldType,payload,count,destination)` 按字段类型写入已有目标，`DecodeRangeInto` 则处理逻辑切片。

当前 value 分支覆盖 Float64、Int64、Boolean、String；其他类型按其专用合同或 fallback 处理，不能只见这四个分支就宣称所有类型都完成相同专用化。

range path 限制起点、总数和返回 count；目标缓冲区不足会明确拒绝。对 delta 编码，取一个小范围也可能需要推进前缀状态，返回数组小不等于解码工作与返回数严格成正比。

## 为什么暂时不引入 generator

source generator 可以生成专用代码，但仍需要维护生成规则、构建诊断与调试入口。当前类型数量有限，手写实现可以集中 review，保持 safe-only 与核心零运行时第三方依赖。

这不意味着 source generator 必然违反零依赖或 AOT。是否使用生成器应由重复代码规模和可验证收益决定，不能把“没有选它”当作一种技术禁令。静态泛型同样不能消除 FieldValue 的类型判别与落盘 encoding 选择。

## 如何复现比较

仓库的 `CodecSpecializationBenchmark` 使用 16,384 点的 V2 Float64 数据，比较完整与范围的 composable/BlockDecoder 两条路径，并计算 checksum 防止无效测量。

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*CodecSpecializationBenchmark*'
```

这是读者的复现命令，本次未运行。历史稿中的几毫秒短跑表缺少可核对的完整环境和报告，因此不继续将其作为当前结果。报告应保留 CPU、运行时、提交、样本轮数、Mean/误差和 Allocated；尤其范围解码可能减少分配却没有同等时间收益，要分别解释。

## 语义是快路径的第一门槛

完整解码与范围解码应对 Float64、Int64、Boolean、String 逐点对拍，覆盖空/单点/边界范围、负数、极值、损坏 payload 和目标长度错误。快路径不能为了省数组跳过长度/CRC 合同。

旧数据可读由 reader 与 codec 的对应版本合同决定。优化不修改格式时，也仍需验证旧 payload；源码中的 DecodeInto 存在只证明实现入口存在，不是新的端到端恢复或性能验收。

参考：[TimestampCodec](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Segments/TimestampCodec.cs)、[Value codec](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Segments/ValuePayloadCodecV2.cs)、[BlockDecoder](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Segments/BlockDecoder.cs)、[基准源码](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/CodecSpecializationBenchmark.cs)。
