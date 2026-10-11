---
title: Token 认证机制：使用 ISSUE TOKEN 保障 API 安全
categories: SonnetDB,Token,HTTP
draft: false
---

应用调用 SonnetDB Server 时，可以在 `Authorization: Bearer ...` 中携带 Token。当前动态 Token 是服务端生成的随机秘密，关联用户并由服务端保存哈希与元数据；它不是旧稿描述的自包含 JWT，也不能由此推断多实例间自动共享认证状态。

正式版本入口见 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按当前 SQL 和 API 合同整理，开发主线变化仍应与实际发行物核对。

## 为已有用户签发 Token

先由实例管理员创建普通用户并授予目标数据库的读取权限，再签发：

```sql
CREATE USER reporting WITH PASSWORD '<set-your-own-password>';
GRANT READ ON DATABASE metrics TO reporting;
ISSUE TOKEN FOR reporting;
```

这里假定 `metrics` 已存在。签发结果包含 token id 与 Token 明文，明文只在此时返回。它应存入自己的凭据系统，避免进入文章、日志和代码仓库。

当前控制面语法是 `ISSUE TOKEN FOR user`，没有旧稿中的 `WITH ROLE ... EXPIRATION ...` 示例。用户的数据库权限通过 GRANT 管理，不能从 Token 字符串猜测其最终权限。

## 使用数据库限定的 SQL API

```bash
curl --fail -X POST http://127.0.0.1:5080/v1/db/metrics/sql \
  -H 'Authorization: Bearer <your-token>' \
  -H 'Content-Type: application/json' \
  --data '{"sql":"SELECT * FROM cpu LIMIT 10"}'
```

替换 `<your-token>`，并确认目标 measurement 存在。正确请求字段是 `sql`，入口是 `/v1/db/{db}/sql`；旧端口 `8839` 和 `/api/v1/query` 不适用。上述 HTTP 地址仅演示本机调用，远程客户端应使用部署中的 TLS 地址。

SQL 响应使用 NDJSON 的 meta、rows 和结束标记。HTTP 连接结束或前几行到达不一定意味着写操作完整成功，实际客户端还要读取终态和错误信息。

## 查看与撤销

```sql
SHOW TOKENS FOR reporting;
REVOKE TOKEN 'tok_replace_with_actual_id';
```

REVOKE 参数是签发/列表返回的 token id，不是 Token 明文，也不是自定义应用名称。SHOW TOKENS 返回元数据，不能找回明文。

轮换时先签发新 Token，更新调用者并验证访问，再撤销旧 Token。撤销后的请求应验证实际认证结果；数据库授权变化与 Token 撤销是两个不同动作。不要复制旧稿中不存在的 `REVOKE ALL TOKENS` 用法。

## 与生产实现的关系

Server 的认证状态保存在 `.system` 控制面目录中，使用 source-generated JSON 读写。文章中的 curl JSON 只是协议请求示例，不引入反射序列化路径。自己的 .NET AOT 客户端若使用 `System.Text.Json`，也应注册 DTO 到对应 `JsonSerializerContext` 并使用类型元数据重载。

参考：[SQL 与 Token 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[UserStore](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Auth/UserStore.cs)、[认证中间件](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Auth/BearerAuthMiddleware.cs)和[控制面端点](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Endpoints/Routes/ControlPlaneEndpoints.cs)。本文未执行真实签发、调用或撤销。
