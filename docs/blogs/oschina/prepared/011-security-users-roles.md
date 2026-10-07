---
title: 安全机制详解：用户、角色与权限管理
categories: SonnetDB,权限,安全
draft: false
---

SonnetDB Server 将身份认证和数据库授权分开处理：用户名/密码或 Token 确认调用者身份，数据库 grant 决定其可以访问什么。本文按当前控制面 SQL 修订旧稿中的固定用户 ROLE 和 measurement 级 GRANT 示例。

正式版本见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)；开发主线仍在演进，生产使用时核对客户端、Server 和对应版本文档。

## 用户、超级管理员与数据库权限

普通动态用户的权限来自数据库授权，超级管理员承担实例控制面管理。静态配置 Token 还可以采用 `readonly`、`readwrite`、`admin` 的角色口径，它与动态用户的数据库 grant 不是可互换的配置方式。

在具有实例管理权限的 Server 控制面中执行：

```sql
CREATE USER analyst WITH PASSWORD '<set-your-own-password>';
CREATE USER collector WITH PASSWORD '<set-another-password>';
CREATE DATABASE metrics;

GRANT READ ON DATABASE metrics TO analyst;
GRANT WRITE ON DATABASE metrics TO collector;
```

密码占位符必须换成独立凭据。已有数据库时先查看列表，不要重复执行 CREATE。READ 用于数据库读取，WRITE 包含相应读写能力，ADMIN 用于数据库管理。数据库 ADMIN 与实例超级管理员的用户/Token 管理职责仍需区分。

## 检查与撤销授权

```sql
SHOW USERS;
SHOW GRANTS FOR analyst;
SHOW GRANTS FOR collector;

REVOKE ON DATABASE metrics FROM analyst;
```

当前 REVOKE 撤销该用户在指定数据库上的授权，不是旧例中的 `REVOKE SELECT ON MEASUREMENT`。如果用户还存在 `*` 通配授权，检查其有效权限时也要把通配项计算在内，不能只看一条数据库记录。

重新授予读取权限，再使用该用户的实际凭据查询并验证写操作被拒绝。权限判断由 Server 执行，前端是否显示按钮、SQL 是否经过预览或审批都不能替代授权。

## 密码与 Token 的协作

用户密码以哈希形式保存，Token 关联用户身份。Token 持有者仍受用户当前授权约束；它不是另一个独立的、永久写死权限的用户。

```sql
ISSUE TOKEN FOR analyst;
SHOW TOKENS FOR analyst;
```

签发结果一次性返回 Token 明文，应立即保存。SHOW TOKENS 只返回元数据。更换或撤销 Token 需要使用返回的 token id，而不是任意应用名称。

## 权限作用域与恢复

设备采集账户可以授予目标数据库的 WRITE，报表账户授予 READ；实例超级管理员凭据单独保存。远程通信使用自己的 TLS 入口，并定期核对实际 grant 和仍使用的 Token。

数据库资源树中的 MQ 使用数据库权限，但其持久状态位于实例级 `.system/mq`。单库恢复不代表完整 MQ 或控制面身份恢复。Graph 继续保留 Beta 状态，也不能因授予 ADMIN 就推导其生产容量已通过。

参考：[控制面 SQL](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[ControlPlane](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Auth/ControlPlane.cs)、[GrantsStore](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Auth/GrantsStore.cs)和[UserStore](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Auth/UserStore.cs)。本文进行文档/源码核对，未实际创建用户或执行授权。
