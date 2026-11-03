# WorkBuddy 0.2.0当前能力合同收口

本修订续接端口归属修复57deb851，仍在 `codex/workbuddy-port-review-20261104` 独立分支。连接器、Skill、表单、元信息、校验器和联调说明原先把旧九工具只读合同描述为当前能力，与本次已授权的九模型MCP43工具矛盾。现在以SonnetDB 5.0现场 `tools/list`、input/output schema为依据，默认Read，用户已有明确写授权且目标库具备Write grant时使用独立写工具；发现工具不表示写授权，query_sql继续只读，写入未知先回读、不盲重试。部署、升级、备份恢复及服务重启另有授权。

连接器和Skill版本统一0.2.0，HTTPS模板与五文件白名单不变。仅生成本地审核包，不上传平台；HTTPS、平台解析、宿主和审核仍未验证。本次没有改生产源码或重跑MCP118/89、NativeAOT43/36；这些证据继续绑定冻结的49731955/PR391，不能由此文档修订继承成正式发布门禁。

实际验证为连接器六组静态检查、七项离线合同和端口归属六组子场景。离线合同覆盖旧九工具服务拒绝、43名称及版本一致、写注解/缺schema拒绝、未来新增工具兼容、43工具脱敏回执/拒绝覆盖历史schema、九模型读取探针无正向写调用，以及重新引入旧只读声明的拒绝。联调脚本现在要求43工具、默认用Read凭据读九模型/验证写拒绝；本次未连接真实Server或Docker，不生成新版在线PASS报告。

历史2026-11-03 r2 JSON/schema/报告保持原字节及revision455bf3b5身份，未改为43工具通过。当前Compose固定镜像仍是历史九工具基线，已加说明，未改镜像或启动容器。M51仅更新当前合同与已验生产切片状态，不扩做#392—#401后续范围。

包位于 `outputs/sonnetdb-workbuddy-0.2.0-review.zip`，仅含connector-meta.json、mcp.json、token-schema.json、icon.svg、skills/sonnetdb-skill/SKILL.md；SHA256及逐文件校验见本机 `artifacts/workbuddy-contract-20261104/package-manifest.json`。提交前必须在最终树通过完整restore与CI同等Format，最终SHA及远端回读见交付回执。
