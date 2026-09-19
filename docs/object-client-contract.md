# Object 客户端取消与目标合同

本页对应 M36 #311 的 Object SDK 切片。复用既有 `SndbObjectStorageClient`、对象列表与 multipart 接口，不实现 #322 Transfer Manager，也不把完整九模型合同标为完成。

## 取消、超时与响应资源

- 所有公开异步入口在访问嵌入式存储或发送请求前检查预取消。嵌入式同步治理操作仍只保证入口检查；这不表示不可中断的同步存储操作已支持中途取消。
- REST JSON 成功与错误响应均从响应头后读取流，用 source-generated `JsonTypeInfo` 反序列化。连接字符串 `Timeout` 的同一期限覆盖请求和正文读取；调用方取消继续传播，错误体中的取消或 IO 异常不会被转换成普通 HTTP 错误。
- 成功、损坏 JSON、取消和 IO 失败都释放元数据响应；请求 helper 在错误体解析抛异常时也释放响应。非 JSON 错误返回稳定 `http_error`，不把代理 HTML 等任意正文复制到异常消息。
- `OpenReadAsync` / `OpenThumbnailAsync` 的取消与超时只覆盖打开操作。返回流之后，调用方必须为 `ReadAsync` / `CopyToAsync` 传入取消令牌，并用 `using` / `await using` 释放流和它拥有的响应。整段传输的重试、恢复、进度和取消生命周期仍归 #322。
- 元数据按单次响应反序列化；对象页验证返回数量，但此切片没有新增通用响应字节上限，也没有把未分页的版本列表改成有界 cursor。

## 原目标、分页与批量结果

对象列表校验响应的 bucket、移除前导 `/` 后的 prefix、delimiter、请求 continuation token 和服务端有效 `maxKeys`。REST 已有上限为 10,000，继续接受这一既有上限。响应的空 continuation token 与缺失 token 等价，兼容 Server 首页返回空字符串。对象与公共前缀总数不可超过页限；截断页必须提供推进的 token，末页不得保留后续 token。页内对象和公共前缀分别保持 ordinal 排序、无重复，且属于指定桶和前缀。页仍读取调用时状态，不新增跨页快照保证。

`DeleteObjectsAsync` 在第一个 await 前复制输入 key 列表，校验响应桶、条目数和逐项 key 顺序，并保留各项错误。成功条目必须带 delete marker 与版本；不完整或错配响应抛 `InvalidDataException`，调用方应核对原目标，不可据此假定写入未发生。批次不是跨对象事务，也不自动补发缺失项。

multipart 的 upload ID 必须属于请求 bucket/key。嵌入式 SDK 和 REST handler 在上传分片、完成、终止前核对既有会话；错配返回 `multipart_not_found`，不消费或覆盖原会话的分片。SDK 还核对创建会话响应的 bucket/key/upload ID、分片响应的 part number 和完成响应的对象目标。没有新增客户端 session registry。

## 写入不自动重放

SDK 创建的共享及独立 HTTP 连接池均关闭自动跳转。Frame PUT 复用既有 `allowFallback: false` 和关联响应检查：一旦请求发送，连接丢失、坏帧或非成功响应不能触发 REST 重放。发送前已确定使用 REST、不可 seek 或超过单帧限制的内容继续走既有 REST 路径；旧服务端不支持 Frame 时调用方应显式选择 `Protocol=rest`。

取消、超时或响应损坏不承诺撤销已发送写入。SDK 不自动重试这些操作；确认原 bucket/key/版本后再决定后续动作。

## 本地验证与剩余门禁

- Core ObjectStorage 定向 Release 回归 96/96 通过，包含新增 42 项合同案例：真实嵌入式对象存储，以及 HTTP handler/stream 替身的全部入口预取消、正文取消/超时/IO、资源释放、分页目标/limit/token、批量部分错误、输入变化和 Frame 禁止重放。替身测试不是远程服务证据。
- Data 显式启用 `EnableAotAnalyzer=true` / `EnableTrimAnalyzer=true` 的 Release Rebuild 通过，0 warning / 0 error；普通 Data Release 构建默认关闭 AOT 标记，不能单独作为这项证据。
- Server 新增真实 Kestrel 回归源码，覆盖三页 delimiter 分页、共享/独立连接池的 307 禁止重放，以及错配 bucket/key 的 multipart 上传/完成/终止后原会话仍可完成。首次运行因环境缺少 Six Labors 构建许可证被阻挡，这些新增 Server 回归及 Server AOT 构建目前尚未取得通过证据，不能计入已完成验证。
- 完整工作台/SDK 矩阵、九模型 golden journey、现场服务、固定硬件容量、断电/备份恢复与 M20 nightly 均单独验收。

已执行的本地验证命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~ObjectStorage'
dotnet build src/SonnetDB.Data/SonnetDB.Data.csproj -c Release -t:Rebuild -p:EnableAotAnalyzer=true -p:EnableTrimAnalyzer=true
```
