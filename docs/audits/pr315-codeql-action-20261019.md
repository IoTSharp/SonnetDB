# PR #315：CodeQL Action 更新

- 原 PR head `033c58592e352a7f488bc6f09c304e923c9a71c9`。
- `github/codeql-action` 从 4.38.2 更新至 4.38.3；不改变扫描范围、权限、failure 策略或 required checks。
- 本地提交前执行完整 restore/Format；此前十项依赖按普通 merge 保留各自父节点，最终同 SHA CI 仍待统一推送后核验。
