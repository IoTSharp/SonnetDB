# 搜索与向量引擎合并路线图

> 2026-06-26 决策：DotSearch / DotVector 不再作为 SonnetDB 之外的独立产品线继续扩张。两者的算法、索引和分词能力已收编为 `SonnetDB.Core` 内部引擎；面向 `Microsoft.Extensions.VectorData` 的 SonnetDB 适配已迁移到 `SonnetDB.Data`。

## 1. 目标

SonnetDB 对外只保留一套数据库体验：

```text
SonnetDB SQL / ADO.NET / HTTP / Studio / CLI / VS Code / Copilot
  -> SonnetDB.Core
     -> FullText internal engine
     -> Vector internal engine
```

全文索引和 ANN 索引仍是可重建派生数据；table、document、measurement 和 `VECTOR` 主数据仍由 SonnetDB catalog、WAL、Segment、Compaction、Retention、Backup 和 Restore 生命周期托管。

## 2. 合并范围

迁入 `SonnetDB.Core`：

- `DotSearch.Core`：BM25、倒排索引、query parser、persistent index。
- `DotSearch.Tokenizers.Unicode` / `Cjk` / `Jieba`：分词器和 Jieba 词典资源。
- DotVector primitives：距离计算、metric、SIMD facade。
- DotVector indexing：HNSW、IVF、IVF-PQ、Vamana、量化、索引 blob 序列化。

迁入 `SonnetDB.Data`：

- `Microsoft.Extensions.VectorData` adapter。
- POCO mapper、LINQ filter translator、key converter。
- `AddSonnetDBVectorStore(...)` DI 扩展。

不迁入：

- DotVector 独立 CLI、connector、example。
- DotVector `.dvec/` 独立数据库目录和纯向量 collection API。
- DotSearch / DotVector 独立 server、gRPC、Docker 服务形态。

## 3. 阶段计划

### Phase 0：决策落档

- 更新 README / ROADMAP / CHANGELOG。
- 明确 DotSearch / DotVector 完成合并归档后的维护边界。
- 后续新全文 / 向量能力默认进入 SonnetDB，不再优先派到独立仓库。

### Phase 1：全文引擎并入 `SonnetDB.Core`

状态：已完成。

已完成：

- `SonnetDB.Core.csproj` 已移除对 `modules/DotSearch` core/tokenizer 项目的 `ProjectReference`。
- DotSearch core、Unicode / CJK / Jieba 分词器源码已物理移动到 `src/SonnetDB.Core/FullText`。
- Jieba `dict.txt` / `dict.dat` 和词典生成流程已迁入 `SonnetDB.Core.csproj`，并保留原 embedded resource logical name。
- DotSearch core/tokenizer 测试已迁移到 `tests/SonnetDB.Core.Tests/FullText`。

验收：全文索引、`match(...)`、`bm25_score()` 和 unicode / cjk / jieba 分词测试通过。

### Phase 2：向量 indexing 并入 `SonnetDB.Core`

状态：primitives / indexing 合并与内部命名空间收敛已完成。

已完成：

- 移动 DotVector primitives / indexing / ANN 算法到 `src/SonnetDB.Core/Vector`。
- 移除 `SonnetDB.Core.csproj` 对 `modules/DotVector` 的 `ProjectReference`。
- 保留 `System.Numerics.Tensors` 包引用，承接 DotVector SIMD 距离计算路径。
- 将 primitives / compute / quantization / HNSW / IVF / IVF-PQ / Vamana / local index blob 测试迁移到 `tests/SonnetDB.Core.Tests/Vector`。
- `src/SonnetDB.Core/Vector`、`VectorDistance`、Segment 向量索引 adapter、注释和错误消息已收敛到 `SonnetDB.Vector.*` / SonnetDB 内置向量引擎叙事。

归档边界：

- DotVector 独立 collection/query/filter/persistence 测试不再迁入；纯向量库语义已随旧模块归档，SonnetDB 对外以 table / document / `VECTOR` 列和 VectorData adapter 建模。

验收：`VECTOR(dim)`、`knn(...)`、HNSW / IVF / IVF-PQ / Vamana、`SDBVIDX2` blob 与重建测试通过。

### Phase 3：VectorData 迁入 `SonnetDB.Data`

状态：核心适配已完成。

已完成：

- 新增 `SonnetDBVectorStore` / `SonnetDBVectorCollection` / dynamic collection 和 DI 扩展。
- 默认把 VectorData collection 映射为 SonnetDB `DOCUMENT COLLECTION`，record key 使用文档 `id`，record 数据和向量字段存入 JSON document。
- 新增 document collection 纯向量 TVF `vector_search(...)`，供 adapter 和 SQL 用户按 JSON vector path 执行向量检索。
- 补充 package README、SQL 参考和 Core / ADO.NET adapter 测试。

边界：

- `measurement` 是时序模型，只用于显式时序 `VECTOR(N)` 列和 measurement KNN / Hybrid Search；通用 VectorData collection 不默认映射到 measurement。
- document collection 是主数据，向量索引或 ANN 加速只能作为可重建派生数据加入，不引入第二套权威向量 catalog。

验收：嵌入式和远程 `SndbConnection` 都可通过 VectorData adapter 使用 SonnetDB。

### Phase 4：删除旧模块依赖

状态：已完成。

已完成：

- 从 CI、CodeQL、Docker publish、publish workflow 和 connectors release workflow 删除 DotSearch / DotVector 递归子模块 checkout 要求。
- 从 Dockerfile 删除 `modules/DotSearch` / `modules/DotVector` 项目和源码复制步骤。
- 从 release script、SDK bundle README 和发布文档删除独立 DotSearch / DotVector NuGet 包产物。
- 移除 `SonnetDB/modules/DotSearch` 和 `SonnetDB/modules/DotVector` 子模块登记。

验收：干净 checkout 不需要拉取 DotSearch / DotVector 子模块即可 restore / build SonnetDB；发布包只包含 SonnetDB 系列包。

### Phase 5：命名空间清理

状态：已完成。

已完成：

- `src/SonnetDB.Core/FullText` 内部命名空间从 `DotSearch.*` 收敛到 `SonnetDB.FullText.*`。
- Jieba embedded resource logical name 从 `DotSearch.Tokenizers.Jieba.Resources.*` 收敛到 `SonnetDB.FullText.Tokenizers.Jieba.Resources.*`。
- 全文核心、tokenizer 测试命名空间同步收敛到 `SonnetDB.Core.Tests.FullText.*`。
- 当前 SQL 参考、parity 文档和 IoTSharp 兼容矩阵改为 SonnetDB 内置全文 / 向量引擎叙事。

验收：当前源码和测试不再包含 `DotSearch.*` / `DotVector.*` 命名空间引用；`dotnet build SonnetDB.slnx --configuration Release /warnaserror` 与 `SonnetDB.Core.Tests` 通过。

## 4. 底线

- 不引入第二套全文或向量主数据存储。
- 不引入第二套权威 catalog。
- 不引入独立搜索服务或独立向量服务。
- 索引必须可重建，备份 manifest 必须能描述索引生命周期。
