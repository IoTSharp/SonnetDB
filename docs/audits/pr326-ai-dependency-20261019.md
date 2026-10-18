# PR #326：Microsoft.Extensions.AI 依赖更新

- 合并父节点：PR #327 本地整合提交 `ee1f2a93804289f29d44908335dac8b3aee8a399`；原 PR head `5ae78a81b2b4ef433084304b4a406255696b10a6`。
- 变更：`Microsoft.Extensions.AI` 从 10.10.0 更新至 10.11.0，未带入 Dependabot 分支中对 `HANDOFF.md` 与 `ROADMAP.md` 的旧基线删除。
- 原 PR CI 38987711357 的 Windows Core 测试达到 50 分钟上限；当前 main 的后续 CI 39147045021 曾让同名 Core job 成功，但 Windows rest 仍因 CAP 启动时序测试失败。因此该历史超时不能被重跑或旧成功作业抹平，最终候选必须按同一 SHA 重新取得真实全套 CI。
- 本地依赖改动待提交前重新执行完整 restore 与规定 Format；未以跳过测试、放宽 timeout 或旧 SHA 通过替代验证。
