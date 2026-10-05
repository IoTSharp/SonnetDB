---
title: SonnetDB 统一管理工作台：Web Admin、Studio 与 VS Code 的共同边界
categories: SonnetDB,Workbench,VS Code,Studio
draft: false
---

SonnetDB 的管理体验正在收敛为“一套核心、三个宿主、三个发布物”：Web Admin 面向完整治理，Studio 面向桌面本地与远程管理，VS Code 面向开发者的 Remote-first 查询和结果查看。M47 已确认导航、五区外壳、九模型逻辑资源树、结果/历史/审批状态和宿主边界，但设计确认不等于全部生产迁移完成。

## 共享的资源上下文

三个宿主共享资源描述、能力注册表、名称和权限语义。MQ 的资源身份包含 `database + topic`，逻辑权限属于 database，物理持久化作用域为 instance，路径是 `.system/mq`。Graph 资源强制 `beta` 标记。Explorer 的旧 key 只用于路由兼容，不能改变资源真实身份。

## 结果、历史和写审批

结果面板需要显示预算截断、局部错误、取消和未知状态；历史恢复只恢复 SQL 草稿与上下文，不重放请求。写操作必须经过审批：权限变化、目标变化、审批过期时拒绝确认；断连或服务器未知终态时记录 request id 和服务器查询入口，不自动重试或重放。

## 三面发布边界

Web Admin、Studio 和 VS Code 各自需要真实 Server 旅程、权限、安装/升级和宿主证据。M47 HTML 原型、静态状态矩阵和本地兼容测试不能代替 Windows WebView2、Extension Host、安装包签名、真实数据库和 AOT 发布验收。计划中的 `sonnetdb mcp` stdio bridge 也仍属于 M47-U08，不能在工作台文章里提前宣传为已发布功能。

参考：[`m47-unified-management-workbench/README.md`](../design/m47-unified-management-workbench/README.md)、[`m47-unified-management-workbench/navigation.md`](../design/m47-unified-management-workbench/navigation.md)、[`management-tools.md`](../management-tools.md)。
