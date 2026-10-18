# PR #325：EF Core Design 与 Tools 依赖更新

- 合并父节点：#324 本地提交 `911278be89746a2d7dd395fbd8ee9caed2e21dd2`；原 PR head `63fc152a8a2e356049aee0685ba09d89cd70a556`。
- 变更：`Microsoft.EntityFrameworkCore.Design` 与 `Microsoft.EntityFrameworkCore.Tools` 从 10.0.12 更新至 10.0.13。
- PR 原始 CI 的 NU1605 根因已由 #324 先行更新 Relational 10.0.13 消除；合并时保留 #327/#326 已有依赖版本，不接受 Dependabot 旧文档删除。
- 本地冲突按依赖合同选择三项 EF Core/Relational/Design/Tools 均为 10.0.13；提交前必须重新 restore/Format，并在最终候选核 EF Core、Format、CodeQL 和所有真实测试。
