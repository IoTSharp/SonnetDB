---
title: 首次设置向导：从零开始配置 SonnetDB
categories: SonnetDB,部署,认证
draft: false
---

SonnetDB 的首次设置用于建立当前 Server 实例的身份、管理员账户和初始 Bearer Token。它不是普通登录，也不是修改环境变量即可重置的配置。本文按当前初始化页面与 API 修订旧稿中的旧端口、多步骤 Token 设置和删除数据重置建议。

正式版本入口见 [SonnetDB 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)；本文引用的 `main` 文档和页面仍在演进，具体界面以运行中的发行物为准。

## 先让首次初始化保持在本机

Docker 示例将 HTTP 端口映射到 loopback：

```bash
docker run -d \
  --name sonnetdb \
  -p 127.0.0.1:5080:5080 \
  -v sonnetdb-data:/data \
  iotsharp/sonnetdb:latest
```

`latest` 是滚动镜像，不等于固定 4.0.0。需要可复现部署时，先确认所选镜像的真实标签/digest。浏览器访问 `http://127.0.0.1:5080/admin/`；空数据根目录会进入首次设置。

## 一次填写实例身份与管理员凭据

当前向导在一个表单中设置：

- 服务器 ID：默认使用基于主机身份生成的建议值，可在提交前确认。
- 组织名称。
- 管理员用户名。
- 管理员密码：需要手动输入。
- 初始 Bearer Token：前端生成随机建议值，可重新生成或按要求修改。

点击完成初始化后，Server 创建第一个超级管理员并保存安装元数据。Token 明文仅在初始化提交与保存时可见，服务端持久化的是摘要与哈希。应在提交前将所选 Token 保存到自己的凭据存储；不要期待重新打开页面读回完整 Token。

如果已经完成安装，初始化 API 会拒绝再次初始化，而不是覆盖现有账户。普通用户、授权和后续 Token 应通过登录后的管理入口处理。

## 用实际状态验证

```bash
curl --fail http://127.0.0.1:5080/v1/setup/status
curl --fail http://127.0.0.1:5080/healthz
```

确认 setup 返回 `needsSetup=false` 后，再验证管理员能访问受保护的数据接口：

```bash
curl --fail http://127.0.0.1:5080/v1/db \
  -H 'Authorization: Bearer <your-token>'
```

替换 `<your-token>`，并检查实际响应。健康检查成功和界面跳转都不能替代认证验证。开始远程接入之前还应确认匿名数据请求被拒绝，并按需开放指定接口、配置 TLS。

## 环境变量引导与已有实例

Docker 可以通过 `SONNETDB_USER`、`SONNETDB_PASSWORD` 和可选 `SONNETDB_DB` 引导空实例。引导仅在尚未初始化时执行；修改这些变量不会重置已有密码。静态 Token 配置也不能替代首次安装流程。

安装状态保存在数据根目录的 `.system/`，包括 `installation.json`、`users.json` 与 `grants.json`。遇到凭据问题应先核对挂载目录、setup 状态和错误日志。不要为了重新进入向导直接删除已有安全文件或数据卷，这会损害实例身份、授权和数据。

需要测试全新实例时，使用明确的新数据目录并保留原实例。数据库恢复和 Server 控制面恢复应分别规划；实例级 `.system/mq` 中的 SonnetMQ 状态也不在单库备份范围内。

参考：[开始使用](https://github.com/IoTSharp/SonnetDB/blob/main/docs/getting-started.md)、[初始化页面](https://github.com/IoTSharp/SonnetDB/blob/main/web/src/views/SetupView.vue)、[Setup API](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Endpoints/Routes/SetupEndpoints.cs)和[Docker 引导](https://github.com/IoTSharp/SonnetDB/blob/main/docs/releases/docker-image.md)。本文核对的是文档和源码，未在本次整理中重新执行首次安装。
