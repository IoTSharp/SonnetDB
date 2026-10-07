---
title: SonnetDB 统一管理工作台：Web Admin、Studio 与 VS Code 的共同边界
categories: SonnetDB,Workbench,VS Code,Studio
draft: false
---

SonnetDB 的管理体验正在收敛为“一套核心、三个宿主、三个发布物”：Web Admin 面向完整治理，Studio 面向桌面本地与远程管理，VS Code 面向开发者的 Remote-first 查询和结果查看。M47 已确认导航、五区外壳、九模型逻辑资源树、结果/历史/审批状态和宿主边界，并已逐切片实现和验证部分路径。当前三宿主整体仍未闭环，设计确认和局部 PASS 都不等于全量生产迁移或发布验收完成。

本文依据当前主分支的设计评审包与验证记录说明；没有在本次发布准备中重新运行浏览器、真实 Server、Studio 或 Extension Host。

## 共享的资源上下文

资源描述、能力注册表、名称和权限语义是三个宿主需要消费的共同合同，当前已有首批实现与本地消费证据；不能据此宣布每个宿主已消费全部合同。MQ 的资源身份包含 `database + topic`，逻辑权限属于 database，物理持久化作用域为 instance，路径是 `.system/mq`。单库备份不覆盖这份实例 MQ 目录，实例恢复需单独验收。Graph 保持 `beta` 标记。Explorer 的旧 key 用于路由兼容，不能改变资源真实身份。

## 结果、历史和写审批

共同交互合同要求结果面板显示预算截断、局部错误、取消和未知状态；历史恢复用于恢复草稿与上下文，不重放请求。工作台支持的写路径采用预览与确认，权限、目标或审批有效期变化要使旧审批失效。断连或服务端终态不明时保留 unknown，不自动重试或重放；有 request id 和终态查询合同的路径应保存这些信息，不能虚构所有模型都有统一查询入口。

一个完整验收旅程可以按以下顺序检查：

1. 用普通 Read 身份进入确定的数据库和模型，加载当前结果窗口，并对拍响应、界面、导出和历史上下文。
2. 用获准写身份预览并确认一次具体操作，由服务端响应和独立读取证明终态；客户端暂存和点击确认不能计为持久成功。
3. 保留一份旧审批后撤销权限，确认请求被拒绝、旧载荷和草稿按合同清理；重授 Read 或刷新 schema 不应自动重放旧操作。

已有 Web→本机真实 Server 的模型切片记录覆盖了这些旅程的部分场景，但不同模型仍有显式恢复、Int64、完整写矩阵和资源预算缺口。当前分页/Top-K 导出只证明该窗口，不能称为全库快照或通用内存上限。

## 三面发布边界

Web Admin、Studio 和 VS Code 各自需要真实 Server 旅程、权限、安装/升级和宿主证据。M47 HTML 原型、静态状态矩阵和本地兼容测试不能代替 Windows WebView2、Extension Host、安装包签名、真实数据库和 AOT 发布验收。计划中的 `sonnetdb mcp` stdio bridge 也仍属于 M47-U08，不能在工作台文章里提前宣传为已发布功能。

设计包的 `CONFIRMED_BASELINE` 表示设计基线获准实施。原型的七个一级模块、页面目录和任务标签可以帮助审查归属与状态，却不构成全部生产页面已接线的证明。实际完成项应按提交、源码、独立验证记录和未完成清单逐项判断。

参考：[`m47-unified-management-workbench/README.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/design/m47-unified-management-workbench/README.md)、[`m47-unified-management-workbench/navigation.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/design/m47-unified-management-workbench/navigation.md)、[`validation-report.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/design/m47-unified-management-workbench/validation-report.md)、[`management-tools.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/management-tools.md)。
