# PR #318：Identity UI 依赖更新

- 原 PR head `90788830ead02269aeaeea910e79caf44ce3ec23`，在 #319 本地提交后合入。
- `Microsoft.AspNetCore.Identity.UI` 从 10.0.12 更新至 10.0.13；冲突解析保留 #324/#325 的 EF Core 10.0.13 版本，旧共享文档删除排除。
- 本地提交前执行完整 restore/Format；RAG 取消测试 11/11 是独立缺陷修复证据，不替代最终依赖候选 CI。
