# GH-Issue #186: VECTOR ADO 与 Frame 验收（2026-09-25）

本报告基于 `IoTSharp/SonnetDB` 当前未发布源码及真实本机 Kestrel，不代表已发布 3.1.0 的行为。原 Issue 要求参数化 INSERT、UPDATE、KNN/TVF 在嵌入式、REST 和帧协议都可传 `float[]`；以下逐项划清已实现能力和阻断。

| 验收项 | 证据 | 状态 |
| --- | --- | --- |
| 参数化 INSERT | `SqlVectorParameterTests.EmbeddedAdo_VectorParametersAndResults_PreserveFloatArrayMetadata`；`RemoteVectorParameterTests.Remote_Rest_VectorParameterAndFloatArray_RoundTrip`；`RemoteAdoHttp2TransportTests.FrameHttp2_VectorParameterAndResults_UseNativeFrameForReads` 在 HTTP/2 ADO 写入时观察 `/v1/db/{db}/sql` | 嵌入式、REST 与 HTTP/2 ADO 通过；Frame SQL 原生端点只读 |
| 参数化 KNN/TVF | 嵌入式及 HTTP/2 ADO 的 `knn(..., @query, 1)`；直接向 `/v1/frame` 发送含原生 VECTOR 命名参数的 SQL Frame 请求，解码结果中 `embedding` 为 `float[]` | 通过 |
| 结果类型与空值 | REST/Frame ADO 的投影、`centroid`、异步 `ReadAsync`、`GetValue`、`GetFieldType`、`GetSchemaTable`；稀疏行省略 embedding 后与 score 联合投影得到 `DBNull.Value` | 通过 |
| 编码与边界 | `SqlFrameCodecTests.QueryRequest_VectorParameter_RoundTripsFloat32AndRejectsInvalidValues` 覆盖 4096 维往返、空数组、NaN/Infinity、畸形帧；真实 HTTP/2 用例覆盖维度不匹配 `sql_error` 和客户端非法值拒绝 | 已覆盖；Frame payload 132 MiB、SQL 文本 1 MiB；不存在独立向量维度硬上限 |
| 参数化 UPDATE VECTOR | `UPDATE docs SET embedding = @vector WHERE ...` 的 measurement 目标落入关系表 UPDATE 分派，而关系表不能定义 VECTOR 列；document UPDATE 只接受完整 JSON 文本替换 | **未完成，Issue 保持 open** |

UPDATE 需要定义时序点的唯一定位、同时间戳多 field 的覆盖/删除规则、WAL replay 与持久向量索引的原子更新/恢复。现有 `INSERT` 写点、`DELETE` tombstone 和关系表 UPDATE 无法直接拼成该合同；本轮不把功能缺口掩盖为协议问题。原生 Frame SQL 写入也仍只读，`Protocol=frame-http2` 的 ADO 写入是 HTTP/2 REST 回落。

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
