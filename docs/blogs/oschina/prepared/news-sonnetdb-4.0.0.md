---
title: SonnetDB 4.0.0 发布：完善 SQL 与多模型协作，强化部署和恢复边界
---

SonnetDB 4.0.0 已在 GitHub 正式发布。SonnetDB 是使用 C# / .NET 10 构建的开源多模型数据引擎，采用 MIT 许可证，支持嵌入式运行和独立 Server 部署。项目以数据库目录为持久化边界，为时序、关系表、KV、JSON 文档、全文、向量、对象、消息队列和 Graph 提供各自的原生语义。

## 本次更新

- **SQL 与关系数据**：扩展 CTE、有界递归查询、窗口函数、连接与集合运算，并完善 DECIMAL、TIME 和表达式支持。增加或完善 UPSERT、INSERT / UPDATE / DELETE RETURNING，改进 ADO.NET 元数据与远程事务结果处理。
- **向量与语义图片**：为 measurement 向量字段提供有界更新；语义图片处理迁移至 SkiaSharp 与 TiffLibrary，并增强回填过程的恢复能力。真实模型的检索质量和成本仍需要按具体模型与数据集验证。
- **多模型协作与管理**：完善 KV 分页、对象传输、MQ 去重、查询预览截断及 Studio 挂载。各模型的查询、权限与恢复合同分别定义，跨模型入口不意味着所有模型具有相同的事务语义。
- **发行与部署**：发行附件提供七个 NuGet 包、Windows / Linux SDK 和完整服务包、Windows Server 与 Studio MSI、Linux DEB / RPM，以及 C、Go、Java、Python、Rust、PureBasic、VB6 等语言连接器。

## 升级与使用边界

默认监听地址采用 loopback；远程访问应显式配置监听、认证与网络边界。升级前应备份数据，查看兼容性说明，按要求重新编译受影响的消费者，并同步升级客户端和 Server。

MQ 的逻辑权限属于数据库，其物理日志位于实例级 `.system/mq`；单库备份不能替代 MQ 实例恢复。Graph 仍为 Beta。固定硬件性能、长期负载、真实 AI 质量与成本、现场安装部署等验收继续独立跟踪，不能由功能入口或发行附件推导出生产保证。

项目与下载：

- [4.0.0 正式 Release 与下载](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)
- [4.0.0 发行说明（标签版本）](https://github.com/IoTSharp/SonnetDB/blob/v4.0.0/docs/releases/4.0.0.md)
- [SonnetDB 项目](https://github.com/IoTSharp/SonnetDB)
