# RAG HTTP 取消点与持久续跑回归（2026-10-19）

- 旧 main CI `38945251979` 的 Windows Core 测试 `Ingest_CancelledHttpCall_LeavesTaskResumable` 失败于空审计缺少 `cancelled`。旧测试在输入读取和数据库打开前就启动一秒总期限，因此不能保证取消落在 HTTP 调用内。
- 内部 CLI 入口接收可选调用方令牌，与原有总期限、Ctrl+C 取消链接。测试在真实 handler 已收到正确 `/embeddings` URI 后取消，不改生产总期限或默认配置。
- 保留取消异常及 `cancelled` 断言，增加一次请求、`started/cancelled` 且无 `succeeded`、无活动 generation、持久 resume 一次请求与 revision 1。额外验证预先取消不产生数据库、审计或 HTTP 请求。
- 独立树原测试实际 11/11、零失败/跳过；整合树以全部11个依赖PR更新后的组合重新构建并验证11/11、零失败/跳过。PR #326 的另一历史 50 分钟整体超时仍为独立 UNKNOWN，本修复不能证明其根因。
- 审计与 TRX 保留在 canonical 忽略目录 `.codex-temp/rag-cancel-evidence-20261019/`。本提交也将本批六条依赖记录移动到 `[Unreleased] / Changed`，未改变其实现。
- 未推送、正式发布或部署；最终同 SHA CI 仍待全部任务候选完成后验证。
