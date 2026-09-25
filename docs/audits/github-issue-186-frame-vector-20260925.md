# GH-Issue #186: VECTOR ADO 与 Frame 验收（2026-09-25）

本报告基于 `IoTSharp/SonnetDB` 当前未发布源码及真实本机 Kestrel，不代表已发布 3.1.0 的行为。原 Issue 要求参数化 INSERT、UPDATE、KNN/TVF 在嵌入式、REST 和帧协议都可传 `float[]`；以下逐项划清已实现能力和阻断。

| 验收项 | 证据 | 状态 |
| --- | --- | --- |
| 参数化 INSERT | `SqlVectorParameterTests.EmbeddedAdo_VectorParametersAndResults_PreserveFloatArrayMetadata`；`RemoteVectorParameterTests.Remote_Rest_VectorParameterAndFloatArray_RoundTrip`；`RemoteAdoHttp2TransportTests.FrameHttp2_VectorParameterAndResults_UseNativeFrameForReads` 在 HTTP/2 ADO 写入时观察 `/v1/db/{db}/sql` | 嵌入式、REST 与 HTTP/2 ADO 通过；Frame SQL 原生端点只读 |
| 参数化 KNN/TVF | 嵌入式及 HTTP/2 ADO 的 `knn(..., @query, 1)`；直接向 `/v1/frame` 发送含原生 VECTOR 命名参数的 SQL Frame 请求，解码结果中 `embedding` 为 `float[]` | 通过 |
| 结果类型与空值 | REST/Frame ADO 的投影、`centroid`、异步 `ReadAsync`、`GetValue`、`GetFieldType`、`GetSchemaTable`；稀疏行省略 embedding 后与 score 联合投影得到 `DBNull.Value` | 通过 |
| 编码与边界 | `SqlFrameCodecTests.QueryRequest_VectorParameter_RoundTripsFloat32AndRejectsInvalidValues` 覆盖 4096 维往返、空数组、NaN/Infinity、畸形帧；真实 HTTP/2 用例覆盖维度不匹配 `sql_error` 和客户端非法值拒绝 | 已覆盖；Frame payload 132 MiB、SQL 文本 1 MiB；不存在独立向量维度硬上限 |
| 参数化 UPDATE VECTOR | measurement 目标现在明确抛 `NotSupportedException`；嵌入式、真实 REST 与 HTTP/2 ADO 证明拒绝后原值不变。relation VECTOR 列仍不受支持 | **未完成，Issue 保持 open** |

UPDATE 需要定义时序点的唯一定位、同时间戳多 field 的覆盖/删除规则、WAL replay 与持久向量索引的原子更新/恢复。现有 `INSERT` 写点、`DELETE` tombstone 和关系表 UPDATE 无法直接拼成该合同；本轮将 measurement UPDATE 改为明确拒绝，不把功能缺口掩盖为协议问题。原生 Frame SQL 写入也仍只读，`Protocol=frame-http2` 的 ADO 写入是 HTTP/2 REST 回落。

## 可复现的阻断

在空库执行以下 SQL，第二次 INSERT 不等同于 UPDATE：

```sql
CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3));
INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0]);
INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [0,1,0]);
SELECT embedding FROM docs WHERE source = 'a';
SELECT embedding FROM knn(docs, embedding, [1,0,0], 1);
```

`SameTimestampInsert_DoesNotReplaceOldVectorInRawOrKnn` 实测：原始 `PointQuery` 在该 `(series, field, time)` 返回 **2 条**；SQL 投影按时间戳字典的后写值返回 `[0,1,0]`；KNN 扫描旧/新两个候选，仍返回旧值 `[1,0,0]`。`BlockSourceMerger` 明确不去重；`SegmentCompactor` 也会保留两个时间戳相同的点。`Tsdb.Delete` 的 tombstone 仅按 `(series, field, time-range)` 过滤，不区分被删旧值与后来写入的新值。使用 DELETE+INSERT 会同时隐藏新值。

当前期望的参数化语句 `UPDATE docs SET embedding = @embedding WHERE source = 'a'` 返回 `NotSupportedException`，真实 REST/HTTP/2 ADO 映射为 `sql_error`。拒绝后 raw/SQL/KNN 仍能读到原值。将来验收必须使上述 UPDATE 返回稳定受影响逻辑行数，并只让更新后的向量参与 raw、SQL、聚合与 KNN；现有重复 INSERT 的行为不得被悄悄改写来伪造 UPDATE。

## 后续实现拆分

1. 定义 `(SeriesId, timestamp)` 为 measurement 逻辑行身份，明确重复写入、稀疏 FIELD、NULL（当前 FIELD 不允许 NULL）、多个匹配时间点、受影响行数及与普通 INSERT/DELETE 交错的规则；先固定嵌入式验收，包括维度、非有限数、空向量、取消和容量错误在写前全量拒绝。
2. 增加带提交边界的版本化替换记录。多行 UPDATE 必须先完整枚举并校验目标，再一次发布提交状态；WAL replay 在掉电/截断后只能恢复整批旧值或整批新值。任何新增二进制记录或段布局都须按格式版本规则处理，并提供旧格式迁移或明确拒绝。
3. 让 MemTable、Segment 与 Compaction 保留或消费可见性版本，`QueryEngine.Execute`/latest、SQL raw/聚合和 `KnnExecutor` 使用同一快照规则。KNN 索引不能返回被替换的旧候选，也不能因先取 top-k 再过滤而漏掉新候选；侧车索引重建及 tombstone 的先后关系须纳入恢复。
4. 在同一提交上验证嵌入式、真实 REST 和 HTTP/2 ADO 的参数化 UPDATE、结果元数据、受影响行数、进程重开、Flush/Compaction 后 KNN，以及故障注入的原子性。原生 Frame SQL 写入是否开放须单独协商，不能把 HTTP/2 REST 回落计为 Frame 写入。

本轮独立分支 `codex/issue-186-vector-update` 的实测：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter "FullyQualifiedName~SqlVectorParameterTests" --verbosity quiet
# 6/6 passed：明确拒绝、拒绝后原值及 KNN 不变、同时间戳 INSERT 反例

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --filter "FullyQualifiedName~Remote_Rest_VectorUpdateWithoutReplacementContract_RejectsAndPreservesValue|FullyQualifiedName~FrameHttp2_VectorParameterAndResults_UseNativeFrameForReads" --verbosity quiet
# 真实 Kestrel：2/2 passed，覆盖 REST 和 HTTP/2 ADO 写入回落

dotnet build src/SonnetDB/SonnetDB.csproj --configuration Release --verbosity quiet
# 0 warnings, 0 errors

dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 -p:SonnetDbPublishAot=true /warnaserror --verbosity quiet
# exit 0, no IL/AOT diagnostics
```

验证命令和本机结果：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter "FullyQualifiedName~SqlFrameCodecTests|FullyQualifiedName~SqlVectorParameterTests" --verbosity quiet
# 29/29 passed

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --filter "FullyQualifiedName~FrameHttp2_VectorParameterAndResults_UseNativeFrameForReads" --verbosity quiet
# 真实 Kestrel / HTTP/2，1/1 passed

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --filter "FullyQualifiedName~RemoteAdoHttp2TransportTests|FullyQualifiedName~RemoteVectorParameterTests" --verbosity quiet
# 真实 Kestrel / HTTP/2 与 REST，17/17 passed

dotnet build src/SonnetDB/SonnetDB.csproj --configuration Release --verbosity quiet
# 0 warnings, 0 errors
```

以上命令的最终树复验仍须在合入后执行；`RemoteVectorParameterTests` 是此前真实 REST Kestrel 回归，本次未修改其实现。

## 后续实验核查（未合并，Issue 仍开放）

独立工作树曾实验一个 measurement VECTOR UPDATE 切片：按 `(SeriesId, FIELD, timestamp)` 将替换值写入内部 KV 原子批次 WAL，并在 raw、SQL、聚合和 KNN 读取时覆盖旧点。实验包含每句最多 256 行、总共最多 4096 条替换记录的容量界限；超限、维度错误和 NULL 在提交前拒绝。定向 Core 测试覆盖 Flush、重开、Compaction、DELETE、备份恢复、行数上限及 WAL sync 故障。早期实验版本的完整 Core 回归为 5243/5243、Release 构建 0 警告、win-x64 NativeAOT publish 退出码 0。这些结果仅说明受测路径通过，**不代表 UPDATE 合同完成，也不是主线或发布证据**。

只读审查发现以下阻断，因此实验代码不提交、不推送，也不用于关闭 #186：

1. 替换记录在 DELETE、Retention 和 Compaction 后没有可靠的跨 WAL 清理。被删除的更新点仍占 4096 配额，`DROP MEASUREMENT` 因历史替换记录被永久拒绝。直接先删 KV 记录会在时序删除尚未持久时复活旧点；先删时序再清 KV 又必须处理崩溃与同名重建。
2. KNN 使用外层段快照、替换序列的内层查询快照，再重新查询 FIELD 填充结果。并发 UPDATE 可使 `distance` 与返回的 `embedding` 不属于同一个版本；混合搜索有同类风险。需要统一的可见性快照或明确的版本重试合同。
3. 内部 keyspace 名 `_measurement-vector-replacements` 与旧用户 keyspace 在 Windows 不区分大小写的路径上可能碰撞。仅对新建调用保留小写名称无法保护旧库；必须选择不会占用既有用户命名空间的持久位置，并规定迁移/拒绝行为。
4. KV 在 WAL 追加前的确定性预算拒绝与 WAL 追加/同步后的未知提交结果需要区分。实验中使用 KV 的 `IsWriteCommitOutcomeUnknown` 判定作了修正，但它仍需与完整提交、读取隔离及故障恢复一同复核。

安全推进顺序：先冻结 measurement 行身份与 DELETE/Retention/DROP 交错语义；确定独立且无旧库命名碰撞的持久化命名空间和跨时序/KV WAL 的恢复协议；随后统一 KNN 候选、距离和 FIELD 回填的快照。再以批次故障注入、真子进程强杀、备份恢复和嵌入式/真实 REST/HTTP2 ADO 回归作为关闭门禁。主线继续保持显式拒绝 VECTOR UPDATE，避免把实验性覆盖当作已交付。
