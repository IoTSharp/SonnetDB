# PR #316：Diagnostics EntityFrameworkCore 依赖更新

- 原 PR head `2530482c1def5f06d1eebc3a406dfdc4de58e371`。
- `Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore` 从 10.0.12 更新至 10.0.13；冲突解析保留前序 EF/Identity 10.0.13，旧共享文档删除排除。
- 本地提交前执行完整 restore/Format；最终 CI 仍以同一整合 SHA 核验。
