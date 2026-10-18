# PR #324：EF Core 与 Relational 依赖更新

- 合并父节点：`56de220004f2c7fe30164c0fd880b62d7eac0eb5`；原 PR head `23a6c1c6068a6ae49fafd187493c4900f96214d5`。
- 变更：`Microsoft.EntityFrameworkCore` 与 `Microsoft.EntityFrameworkCore.Relational` 从 10.0.12 更新至 10.0.13。Dependabot 头携带的旧 HANDOFF/ROADMAP 删除没有进入整合树。
- 该项先于 #325 合并，因为 #325 的真实 Format/restore 日志显示 Design 10.0.13 依赖 Relational >=10.0.13，而旧直接版本 10.0.12 会触发 `NU1605`。
- 本地只完成依赖层整合，提交前执行完整 restore/Format；最终 EF/全套测试与同 SHA CI 仍需在后续候选核验。
