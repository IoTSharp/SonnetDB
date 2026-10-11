# P07 继任候选11：许可修复与同源验收

2026-10-11，Asia/Shanghai。用户要求先收尾 P07，再完成 P08；审批人和当前发起账户均为 `maikebing`，用户报告有效 Visual Studio 2026 Community 资格。原候选10及原 P07 NOT_READY、历史失败和 UNKNOWN 保持不可变。

继任源码把 `@mapbox/jsonlint-lines-primitives` 从 2.0.2 精确升级到 2.0.3，满足现有两个上层依赖的 patch 范围。官方 npm tar 的 SHA512 与 registry metadata 一致；包内原 LICENSE 为 Zachary Carter 的 MIT 原文。保留准确 tar 差异：`lib/jsonlint.js` 也发生变化，不能说只是增加许可文件。版本11须重新进行 UI 与实物验收。

`vdirs@0.1.8` 的 registry metadata 声明 MIT，但准确旧 gitHead 不可取，当前公开仓库也没有许可正文。新代码不推断原通知：用 SonnetDB 自有 `clickoutside`、`mousemoveoutside` 和 `zindexable` 指令实现替换构建导入。最终落盘 JS 的模块清单绑定源码 hash、chunk 字节 hash，并拒绝任何实际发出的 vdirs 模块。依赖仍如实登记 `buildOnly=false`；只有包内清单和全部 chunk 校验通过，才标记其已安装但没有分发。它不是任意生产依赖的免审机制。

微软 Community 2026 原 DOCX、2026 再分发清单 HTML、V14 runtime 原条款通过无损 gzip 保存并在组包时恢复原字节。用户资格来源与构建机 MSVC/VC redist 来源分列，不冒称已有 Microsoft 权益证书。四个 DLL 仍核原路径和实际 hash；新增下游保护条款与启动前 `-AcceptThirdPartyTerms`。`-VerifyOnly` 不运行 Server，也不表示同意许可。SonnetDB MIT 源码许可保持。

P07 收集器改为按实际冻结 manifest 的依赖数量绑定许可审查，避免把候选10的 243/16 常量套到新候选；原十二项 release policy 和 live verifier 没有修改。既有五项 npm 通知继续使用准确版本、integrity 和上游原文。

本地 Web 构建、469 项 Web 合同、86 项组包/P07/P08 合同、七项许可采集正向/拒绝合同已通过；全部为源码或本地合同证据。首次错误版权断言、过早生成 chunk hash、VM 测试缺新模块依赖和 fixture 路径失败均保留。不能迁移为新包实物 PASS。

新候选版本 `4.5.0-preview.1.11` 已核未被 build 使用。使用 `codex/m47-preview11-release` 分支：直接推 main 会触发既有 Docker/Pages 外部发布，本次只推候选分支并手动取得同源 preflight。后续顺序是最终 restore/format 后源码提交及普通推送、同 SHA 干净 P04、实际 P05 全套、许可实物审查、新 P06/P07 原 artifact、十二工作流完整门禁，再准备精确 P08 授权。

当前仍 NOT_READY；同一个 maikebing 账户无法审批自己的 publish。环境保持 required reviewer、prevent_self_review=true、can_admins_bypass=false。实际 tag/Release/匿名公开下载和公开包首跑尚未执行。Workbench automation 继续 PAUSED，共享外来文件及 parity-results 保留。完整本机过程证据在 `artifacts/m47-p07-close-20261011`。
