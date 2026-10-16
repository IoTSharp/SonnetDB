# 人工代码统一整合审计（2026-10-17）

用户要求提交、拉取、同步本地和线上代码。根主目录起点为 main 4d6e4f98；原有工作树、失败证据、stash 和 parity-results 均保留。

`worktree-reconciliation.json` 记录全部注册树的原始快照清单、历史匹配和人工复核。旧树多数为已交付源的冻结副本；未将其旧代码回写主线。`legacy-source-deltas.json.gz` 和 `legacy-notes.json.gz` 保存无完整 blob 匹配的旧源增量与旧交接/报告文本，作为历史审计，不接入编译或覆盖最新报告。完整原字节备份仍在本机 artifacts/integration-20261017/snapshots。

两条线上开发分支保留历史普通 merge；parity-results 是结果分支，始终排除。#248 冻结实现独立整合，#247/#245 仅保存准备草稿并保持未完成；没有自动继续新 Issue。最终测试、远端祖先、精确 CI 和关闭回读见本机同目录 artifacts 的最终回执及本次会话结果，不以旧 CI 代替新 HEAD。

定时任务继续 PAUSED。这里的代码同步不包含正式发包、上游 ORM PR 或服务部署。
