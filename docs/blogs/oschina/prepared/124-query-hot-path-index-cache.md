# 查询热路径优化：索引、缓存与少一点 LINQ

相同 block 被反复解码、相同 reader 映射被反复构建，都会增加查询成本。SonnetDB 当前查询路径通过只读索引、快照关联缓存和有预算的 block 缓存减少重复工作，同时保留输出顺序、删除语义与资源生命周期。

本文依据当前 QueryEngine、SegmentReader 与 SegmentManager 实现整理；正式 4.0.0 与 main 分开核对。下面说明结构上的优化，不把源码存在当作固定硬件性能报告。

## Open 时建立候选索引

SegmentReader 为 block 建立 `SeriesId -> BlockDescriptor[]` 的 FrozenDictionary 和时间范围索引。按 series、field、时间窗找候选时，可以先缩小范围，避免每次从所有 block 开始扫描。

这些索引驻留内存，不改变 segment 文件格式。候选命中不是最终结果：仍要做字段、时间范围、payload 校验和删除过滤。多 series、多 field 与乱序写入也要求稳定语义，不能为了缩短候选扫描而改写排序。

## Reader map 与租约

QueryEngine 的 reader map 与 SegmentManager 发布的 reader/index 快照绑定。快照身份未变时可复用映射；AddSegment、SwapSegments、Dispose 等变更必须使旧映射失效或切换到新快照。

查询持有的旧 reader 还可能被正在进行的扫描使用，compaction 不能立即释放它。租约与延迟释放解决的是“新查询看新快照、旧查询完成旧快照”的生命周期问题，并不等于整个系统无锁。

## Tombstone 过滤保持闭区间语义

先按查询窗口筛选可能相关的 tombstone，再逐点判断，可以减少无效检查。手写迭代替代热路径中的 LINQ 层，也可以减少枚举器和闭包开销。

真正必须保持的是覆盖范围、输出顺序与 LIMIT 处理。删除过滤必须在决定最终返回行数的正确阶段发生，否则可能“取够 10 行后删掉 6 行”却不补齐结果。

## Block 缓存需要预算

当前缓存 key 是 `SegmentId`、`BlockIndex` 和 `Crc32`，采用 LRU 与估算字节预算。超过单项预算的 block 不缓存；累积超预算时淘汰旧项，reader 生命周期结束时清理。

缓存减少重复解码，却增加常驻内存；不能默认预算无限，也不能将 CRC key 当作对任意外部文件篡改的安全认证。segment 的不可变合同与读取校验仍需成立。

## mmap 也是一种输入方式

当前 SegmentReader 对达到配置阈值的大段可启用 safe-only mmap，小段保留 byte[] reader。mmap 通过安全 accessor 复制，不使用裸指针，也不能简单宣称所有 payload 都是零拷贝。

平台能力、文件大小和可恢复的打开失败决定实际路径。无论 mmap 还是 byte[]，都要保留同样的校验和查询结果，不能借回退隐藏真实损坏错误。

## 一个有界查询例子

假设已写入 `cpu`，比较相同查询重复执行与改变时间窗的行为：

```sql
SELECT time, host, usage FROM cpu
WHERE host = 'edge-1'
  AND time >= 1710000000000 AND time <= 1710000060000
ORDER BY time LIMIT 100
```

测试时分别记录冷读取与热读取，保持字段、时间范围、缓存预算、WAL/flush 状态一致。再让 flush/compaction 发布新段，确认查询结果与删除过滤未变化。缓存命中率、解码分配、P95 和常驻内存需要同时看，某一次热查询更快不能代表整体吞吐提升。

参考：[SegmentReader](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Segments/SegmentReader.cs)、[Block 缓存](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Segments/BlockDecodeCache.cs)、[QueryEngine](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/QueryEngine.cs)、[SegmentManager](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/SegmentManager.cs)。
