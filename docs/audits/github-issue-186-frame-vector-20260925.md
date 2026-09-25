# GH-Issue #186: VECTOR ADO 与 Frame 验收（2026-09-25）

本报告基于 `IoTSharp/SonnetDB` 当前未发布源码及真实本机 Kestrel，不代表已发布 3.1.0 的行为。原 Issue 要求参数化 INSERT、UPDATE、KNN/TVF 在嵌入式、REST 和帧协议都可传 `float[]`；以下逐项划清已实现能力和阻断。

下文的最初审计及“后续实验核查”保留当时的证据状态；本文末尾的“独立工作树完成核查”记录后续修复，不应把两次结果混为一次主线或发布验收。

| 验收项 | 证据 | 状态 |
| --- | --- | --- |
| 参数化 INSERT | `SqlVectorParameterTests.EmbeddedAdo_VectorParametersAndResults_PreserveFloatArrayMetadata`；`RemoteVectorParameterTests.Remote_Rest_VectorParameterAndFloatArray_RoundTrip`；`RemoteAdoHttp2TransportTests.FrameHttp2_VectorParameterAndResults_UseNativeFrameForReads` 在 HTTP/2 ADO 写入时观察 `/v1/db/{db}/sql` | 嵌入式、REST 与 HTTP/2 ADO 通过；Frame SQL 原生端点只读 |
| 参数化 KNN/TVF | 嵌入式及 HTTP/2 ADO 的 `knn(..., @query, 1)`；直接向 `/v1/frame` 发送含原生 VECTOR 命名参数的 SQL Frame 请求，解码结果中 `embedding` 为 `float[]` | 通过 |
| 结果类型与空值 | REST/Frame ADO 的投影、`centroid`、异步 `ReadAsync`、`GetValue`、`GetFieldType`、`GetSchemaTable`；稀疏行省略 embedding 后与 score 联合投影得到 `DBNull.Value` | 通过 |
| 编码与边界 | `SqlFrameCodecTests.QueryRequest_VectorParameter_RoundTripsFloat32AndRejectsInvalidValues` 覆盖 4096 维往返、空数组、NaN/Infinity、畸形帧；真实 HTTP/2 用例覆盖维度不匹配 `sql_error` 和客户端非法值拒绝 | 已覆盖；Frame payload 132 MiB、SQL 文本 1 MiB；不存在独立向量维度硬上限 |
| 参数化 UPDATE VECTOR | 初次审计时 measurement 目标明确拒绝；后续独立工作树的修复和边界见本文末尾。relation VECTOR 列仍不受支持 | 初次审计未完成；独立工作树已有后续实现，Issue 尚未关闭 |

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

## 独立工作树完成核查（仍未合入/发布）

`codex/issue-186-vector-update` 后续修复了上述四项阻断，测的是吸收本机 `main` 后的独立工作树，不是远程 `main` 或发布包：

1. 逻辑替换键固定为 `(SeriesId, FIELD, timestamp)`；多行目标先校验再写同一 KV WAL batch。原始时序 WAL 先同步，替换 WAL 后同步；提交结果不确定时冻结替换读写，重开按 WAL 恢复。单句最多 256 行，活替换记录最多 4096 条及 128 MiB 估算字节量（不是 CLR 堆峰值硬上限）。当前只替换已有 VECTOR 点；WHERE 只接受 TAG/time 条件，稀疏目标行、字段残差、NULL、非有限值、维度错误、超额和不支持的事务/JOIN/RETURNING/GEO 在提交前拒绝；已替换的同键 INSERT 明确拒绝。
2. `DELETE` 涉及替换记录时在写锁内先同步墓碑 WAL、再清理替换 KV；DROP 在目录持久删除后清理系列替换，清理失败则禁止同名重建直到重开；Retention 在段移除持久提交后清理。重开在完成时序 WAL/墓碑恢复后复查替换记录，仅保留仍有原始可见点的项。Compaction 保留逻辑覆盖，备份包含内部 KV 检查点。
3. 内部 KV 位于 `kv/internal/measurement-vector-replacements`，与已有 `kv/keyspaces/<用户名称>` 分离，包括 Windows 不区分大小写路径上的旧用户 keyspace。KNN 与 hybrid search 从候选评分到字段回填持有同一替换版本，避免返回的 `distance` 与 VECTOR FIELD 属于不同 UPDATE 版本。
4. 吸收当前本机 `main` 后，定向 Core `SqlVectorParameterTests` 19/19（含累计字节预算和 65 条分页恢复）、真实 REST 与 HTTP/2 ADO `RemoteAdoHttp2TransportTests|RemoteVectorParameterTests` 18/18；子进程在 UPDATE、DELETE、DROP 返回后调用 `Process.Kill()`，三种数据库重开校验均通过。备份恢复、Flush/Compaction、同步故障、预算拒绝、Retention、DROP 同名重建、旧用户 keyspace、稀疏目标拒绝与并发 KNN 均有定向测试；最终代码的 win-x64 Server NativeAOT publish `/warnaserror` 退出 0。完整 Core 首轮 5250/5251，唯一失败为 `KvAtomicRestResponseTests` 的 `set-conditional` 用例，单独复验 4/4 通过；吸收本机 `main` 后第二、三轮均为 5258/5259，唯一失败为 `KvRedirectTests.Create_WithMixedRedirectPolicies_IsolatesCachedHandlers`，单独复验 2/2 通过。这三轮均不得记为全绿；最后的 128 MiB 预算及分页恢复修改只执行了定向 Core 和 AOT，未重跑完整 Core。

原生 SQL Frame 请求仍只读；`Protocol=frame-http2` 的 ADO 写入仍走 REST 回落。轻事务、JOIN/FROM、RETURNING、GEO/字段残差谓词、稀疏目标补列、NULL VECTOR 与关系表 VECTOR UPDATE 不在本切片支持范围内。GitHub Issue 仍须以合入和线上回读状态为准。

## 合入前最终分支复验

吸收远端 `main`（`7ce5aef8`）并补上持久删除后清理失败栅栏、同快照 series/field 命中索引后，完整 Core 测试 **5284/5284 通过**，其中 VECTOR 定向用例 **21/21 通过**；真实 REST/HTTP2 ADO 定向测试 **18/18 通过**，win-x64 Server NativeAOT publish `/warnaserror` 退出码 **0**。仅包含本次修改文件的 `dotnet format --verify-no-changes` 与 `git diff --check` 均通过。此前三轮完整 Core 的单例失败仍保留在上文作为历史证据，不能改写为当时通过。

另行构建 `SonnetDB.VectorCrashWorker` 为 **0 警告、0 错误**；在最终代码上分别于 UPDATE、DELETE、DROP 返回后调用 `Process.Kill()`，三个独立数据库的重开校验均输出 `VERIFIED`。这是手工子进程验收，不计入 5284 个 Core 自动测试。PR #201 初次 CI 的 Format Check 与 Ubuntu Build & Test 为失败；其对应 `main` 提交也分别失败，Ubuntu 的 CDC 独占锁与 EF Core HTTP/2 请求记录断言是相同用例，不能把这两项写为通过。上述结果验证了受测平台与故障点，不能证明所有掉电、磁盘故障或并发交错均无风险；线上关闭 #186 仍以 PR 合入及 Issue 状态为准。

## 合入后线上回读

PR [#201](https://github.com/IoTSharp/SonnetDB/pull/201) 已 squash 合入 `main`（`e3d185cf`）；GitHub API 回读 #186 为 `closed/completed`。最终 PR CI 的三平台 connectors、三平台 NativeAOT 与 CodeQL 通过；Format Check、Windows/Ubuntu Build & Test 失败。对应合入前 `main` 提交的 Format Check 与双平台 Build & Test 也失败：Windows 均只有 EF Core HTTP/2 请求记录断言，Ubuntu 均只有 CDC 独占锁与同一 EF Core 断言。Linux Docker 的 VECTOR 定向测试 21/21 通过，完整 Core 5283/5284，唯一失败为相同 CDC 用例。这些红项保留为未通过的门禁；合入与 Issue 关闭不等于正式发布或生产无风险。
