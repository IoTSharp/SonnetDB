# PR #317：Identity EntityFrameworkCore 依赖更新

- 原 PR head `9ea315bf056b0c701c8141cd82d2c67e348ba12e`。
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 从 10.0.12 更新至 10.0.13；冲突解析保留其它 EF/Identity 10.0.13，旧共享文档删除排除。
- 原 Ubuntu warmup failure 与 PR #327 的 `_tableLockTokens` 并发竞态一致；#327 已添加短锁和 92/92 定向回归，最终同 SHA CI 仍需核验。
