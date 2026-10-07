---
title: CopilotDock 全局浮动面板：拖拽、全屏、页面感知与 50 条历史会话
categories: SonnetDB,Copilot,WebAdmin
draft: false
---

CopilotDock 是 Web Admin 中的浮动 AI 面板，可折叠、拖拽和展开，并提供页面上下文与会话入口。本篇沿用原系列标题，但其中“50 条历史会话”来自旧实现，不能作为当前主线仍使用 localStorage 或固定保留上限的保证。

## 面板与页面上下文

进入已初始化并完成认证的 Web Admin 后，可以打开 Copilot 面板、选择当前数据库、查看页面标签，再提出具体问题。例如在 SQL 控制台请求“解释当前查询的过滤条件”，先审阅提供给模型的上下文，再决定是否启用页面感知。

```sql
SELECT avg(usage) AS avg_usage
FROM cpu
WHERE host = 'server-01' AND time >= now() - 1h
GROUP BY time(1m);
```

模型回答与 SQL 草稿需要结合实际 schema、权限及执行结果验证。拖拽或全屏只是界面操作，不会改变数据库授权。

## 当前会话存储

主线 `copilotSessions.ts` 明确使用服务端作为唯一持久化来源，前端 store 只保存当前页面的内存镜像。刷新会话列表、选择会话、重命名和删除通过对应服务端 API 完成，不能再教用户去 localStorage 的旧 key 中维护会话。

服务端会话、浏览器状态与身份应一起检查。更换身份或数据库时，不能把旧会话的数据与工具结果当作新身份已经获准访问的内容；历史保留与分页以当前服务端合同为准。

## Runtime 与权限

云端账号绑定、当前本地 Chat 和 StudioNative 具有不同通道。当前本地 HTTP Chat 只读；typed MCP 的工具调用仍执行服务端 grant 和结果预算检查。UI 中出现模式标签不等于凭据拥有写权限。

StudioNative 通过受保护的本机 bridge 调用批准的公网 runtime，凭据保存在 Windows Credential Manager，不应写入页面、URL 或 localStorage。没有兼容宿主或缺少出域配置时，不应把功能不可用描述为自动切换到其它通道。

## 历史与引用的用途

可以回看用户问题、模型回答和工具观测，但引用编号只是溯源入口，不会自动证明回答正确。应核对来源版本、结果截断与实际数据库行为。

正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与持续更新的主线宿主能力分开确认。本文未进行 WebView2 实机、真实公网模型或完整多轮 UI 旅程验证。

参考：[Dock 实现](https://github.com/IoTSharp/SonnetDB/blob/main/web/src/components/CopilotDock.vue)、[当前会话 store](https://github.com/IoTSharp/SonnetDB/blob/main/web/src/stores/copilotSessions.ts)、[Provider](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-providers.md)、[StudioNative 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/studio-copilot-contract.md)。
