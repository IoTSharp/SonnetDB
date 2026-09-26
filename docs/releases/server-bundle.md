---
layout: default
title: Server Bundle
description: 下载即运行的完整服务端发布包，包含后台、CLI、文档和默认配置。
permalink: /releases/server-bundle/
---

Server Bundle 面向“下载即启动”的部署场景，默认包含：

- `SonnetDB` 可执行文件
- 管理后台前端
- `/help` 帮助文档静态站点
- `SonnetDB.Cli`
- `SonnetDB.Core` / `SonnetDB` / `SonnetDB.Cli` NuGet 包
- 启动脚本与说明文档

## 启动方式

Windows:

```powershell
.\start-sonnetdb.cmd
```

Linux:

```bash
chmod +x ./start-sonnetdb.sh ./sndb
./start-sonnetdb.sh
```

## 常用访问地址

新生成的 Server/Studio bundle 和 MSI/DEB/RPM 内置 Server 默认将 HTTP 与 Frame HTTP/2 绑定到 `127.0.0.1`。MQTT（含 WebSocket/Sparkplug 和外部客户端）、CoAP/DTLS、Line Protocol UDP 与 Modbus 均显式关闭。该设置适用于本地初始化；既有安装和 Docker/源码部署应按自身配置核查。

- 管理后台: `http://127.0.0.1:5080/admin/`
- 帮助中心: `http://127.0.0.1:5080/help/`
- 健康检查: `http://127.0.0.1:5080/healthz`
- 指标接口: `http://127.0.0.1:5080/metrics`

## 开放远程访问

Server full bundle 的 `admin` / `Admin123!` 与 `sonnetdb-admin-token` 是公开的初始化凭据。默认绑定本机不替代正式的身份、TLS 和网络策略，也不隔离同一台机器上的其他用户。

1. 先仅在本机启动，修改管理员密码。修改密码会撤销该用户已经颁发的 token；重新登录后为本次部署创建唯一凭据。
2. 从 `appsettings.json` 的 `SonnetDBServer:Tokens` 中移除 `sonnetdb-admin-token`，同时检查环境变量与启动参数中是否还有相同的静态 token。改密码或撤销用户 token 不会移除这条静态配置。
3. 重启并验证新凭据可用、旧密码和旧 token 均被拒绝，然后显式设置 `Kestrel:Endpoints`、TLS、网络访问控制与所需协议。只有 HTTP 和 Frame 使用 Kestrel URL；MQTT、CoAP、UDP 与 Modbus 必须分别检查监听与认证策略。

环境变量和命令行配置能够覆盖 bundle 默认值，升级已有部署时也应核对这些来源。发布验证只证明默认产物的监听边界，不代表部署者后续配置仍保持这个边界。

## 目录结构示意

```text
sonnetdb-full-<version>-<rid>/
├─ SonnetDB(.exe)
├─ appsettings.json
├─ cli/
├─ packages/
├─ docs/
├─ sonnetdb-data/
└─ start-sonnetdb.cmd|sh
```
