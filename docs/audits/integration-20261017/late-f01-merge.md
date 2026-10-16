# 推送前发现的 M49 新提交整合（2026-10-17）

本次同步助手在实际 fetch 后发现远端 M49 分支从39437db0推进到 `aa617b1bebaeb84b2594a7cc60703918c299f72c`，因此在推送前停止，没有遗漏新提交或覆盖它。随后在含构建接线修复 `dd47abd7` 的main普通merge该提交，保留其历史、源码、测试、失败TRX与最终证据。CHANGELOG/HANDOFF两个冲突均逐段保留双方内容；其余生产代码无冲突。

新增功能是新Client通过原生HTTP/2 Frame执行单条关系SQL自动提交写入，独立Execute=2/payload v1、精确参数和类型化NULL、完整终态与未知结果无重试。不是旧ADO迁移完成，不将M49/F01全部标记完成。原切片的限制、失败修正及边界保留在 `docs/audits/m49/f01-20261017/`。

根复审了Protocol编解码预算/边界/类型、Client无重试和终态校验、Server鉴权/admission/参数绑定/取消/受支持AST分支及影响行数处理。当前Client、Protocol和新Frame处理器与aa617版本逐项Git diff无差异；该合并对Core、Data、EF和样例目录没有源改动，保留main原有#248与Identity/RSE修复。

根实际在合并树重新构建并运行：Client全项目 **211/211**、Server全项目 **1451/1451**、Core基础Frame/Span IO定向 **295/295**，均零失败、零跳过、exit0；原TRX和最终完整restore/Format回执在本机 `artifacts/integration-20261017/late-f01-results` 与 `logs`。这些计数不与此前整仓/代理窗口累加。

此前 `d65e7884597d37ef18690b4eb451717349438a2c` 的精确主CI **38940523358** 已completed/success，7个job全成功；同SHA Docker缺Protocol及Windows Rust缺导入库失败保持原记录并由dd47单独修复。新的最终merge必须重新获得其自身SHA的CI/镜像/连接器结果，不能用d65成功代替。

普通推送前再次fetch/pull与核全部远端/本地开发头祖先，实际回读main SHA、OPEN PR、stash与parity引用。定时监督仍PAUSED，不派单或继续实现#247/#245；#248在最终精确CI通过后才回复关闭。
