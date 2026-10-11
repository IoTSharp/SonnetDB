---
title: SonnetDB VS Code 扩展预览：连接管理、SQL 编辑器与结果可视化
categories: SonnetDB,VSCode,开发工具
draft: false
---

SonnetDB 的 VS Code 扩展已有连接管理、schema Explorer、SQL 编辑和结果视图实现。本文沿用“预览”选题，介绍当前仓库功能与使用路径；main 的后续工作台切片不等于所有功能已经在安装版本中完整发布。

## 连接到 Server

当前扩展要求 VS Code 1.100 或更高版本。Server 地址使用基地址，例如 `http://127.0.0.1:5080`，不再使用旧示例中的固定 3260 端口。

1. 在活动栏打开 SonnetDB，选择 Add Connection。
2. 输入名称、Server 基地址和需要的令牌。
3. 运行 SonnetDB: Test Connection。
4. 选择数据库，再打开查询编辑器。

名称和 URL 保存在 global state；Bearer 令牌使用 VS Code SecretStorage，不能按旧稿放进 workspace `settings.json` 的 `authToken`。

## 编辑和执行 SQL

在练习数据库执行完整示例：

```sql
CREATE MEASUREMENT vscode_demo (host TAG, usage FIELD FLOAT);
INSERT INTO vscode_demo (time, host, usage) VALUES
    (1700000000000, 'server-01', 42.5),
    (1700000001000, 'server-01', 61.2);

SELECT time, host, usage FROM vscode_demo
WHERE host = 'server-01'
ORDER BY time;
```

Windows/Linux 的 `Ctrl+Enter` 执行光标所在语句，`Ctrl+Shift+Enter` 执行选区。新增或删除等操作还受 Server 凭据权限约束。

扩展提供补全、hover、函数签名和诊断。完整 C# parser sidecar 需要 .NET 10；缺少它时轻量 TypeScript 诊断仍可用，不等于所有功能完全失效。

## 结果视图与 Explorer

结果可以在 Table、Raw、Chart 与 Trajectory 视图检查，CSV 与 JSON 导出对应当前文档合同。旧稿中的百万行虚拟滚动、Parquet/Excel 导出与任意统计能力没有本次证据，不作承诺。

Explorer 包含多模型资源入口，并提供适用的只读预览；Graph 仍按 Beta 范围理解。连接、数据库和原始对象身份应保持明确，不能把客户端预览行数理解为 Server 全链路资源上限。

## Copilot 和本地托管

Copilot 默认只读，切换读写需要显式确认，Server 仍校验 grant。Managed Local Server 通过扩展命令启动、查看输出与停止；配置明确的 Server 路径，避免把外部已有进程当作扩展自有进程。

当前 main 的统一管理工作台仍按宿主切片验证，Extension Host、真实 Server、VSIX 与外部浏览器交接需分开验收。

参考：[扩展说明](https://github.com/IoTSharp/SonnetDB/blob/main/extensions/sonnetdb-vscode/README.md)、[Marketplace](https://marketplace.visualstudio.com/items?itemName=iotsharp.sonnetdb-vscode)、[路线图](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md)。
