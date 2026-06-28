# IoTSharp 兼容矩阵迁出说明

日期：2026-06-28

IoTSharp 如何把 SonnetDB 作为关系库、时序库、缓存、对象桶、向量搜索或全文搜索的可选后端，已经迁入 IoTSharp 仓库维护。

SonnetDB 仓库不再维护 IoTSharp 专属路线图、Profile、灰度、双写、回滚或长稳验收清单。本仓库只保留 SonnetDB 自身需要交付的通用数据库能力：

- ADO.NET 与 EF Core provider 能力
- SQL、事务、DDL、schema metadata 和查询翻译
- KV TTL、缓存 provider 与过期清理语义
- Object Storage API、对象生命周期、审计与配额
- 文件布局、compaction manifest、增量索引和大量 measurement 长稳
- 通用 export/import、checksum、backup/restore 等迁移与校验原语

IoTSharp 专属内容请在 IoTSharp 仓库中维护：

- `ROADMAP.md` 的 `RD-10 SonnetDB 可选数据底座生产化`
- `docs/docs/operations/sonnetdb-compat-matrix.md`

如果 SonnetDB 后续因为 IoTSharp 或其他上层项目暴露出通用数据库能力缺口，应在本仓库按 Core / Data / Server / Studio 的边界拆成独立任务，而不是把上层项目路线图搬回 SonnetDB。
