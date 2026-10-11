---
title: CLI 工具安装与使用：sndb 命令行指南
categories: SonnetDB,CLI,DotNet
draft: false
---

`sndb` 是 SonnetDB 的命令行入口，适合本地与远程 SQL、交互式 REPL、连接 profile 和离线备份。本文按当前 CLI 文档与命令分发表修订旧稿中的 `sndb config`、`health`、`execute` 等不适用示例，并保留可以直接使用的入门路径。

## 安装和查看版本

准备 .NET 10 环境后，可安装正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 对应的工具版本：

```bash
dotnet tool install --global SonnetDB.Cli --version 4.0.0
sndb version
sndb help
```

已有全局工具时，安装动作换为 `dotnet tool update --global SonnetDB.Cli --version 4.0.0`。使用其它版本时，以实际工具的 `help` 为准；开发文档和 `main` 可能比已发布工具更新。

也可以从仓库源码运行：

```bash
dotnet run --project src/SonnetDB.Cli -- version
```

## 本地数据库与 SQL

先保存一个本地 profile，再创建并查询 measurement：

```bash
sndb local --path ./demo-data --save-profile home --default
sndb local --profile home --command "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)"
sndb local --profile home --command "INSERT INTO cpu (time, host, usage) VALUES (1713676800000, 'server-01', 0.71)"
sndb local --profile home --command "SELECT time, host, usage FROM cpu ORDER BY time"
```

同一目录中的数据库对象会保留下来，重复执行 CREATE 时应先检查已有 schema，不要直接把脚本当成可无限重跑的操作。

## REPL 和脚本

```bash
sndb local --profile home --repl
sndb sql --connection "Data Source=./demo-data" --file ./q.sql
```

REPL 中可以连续输入 SQL。脚本执行应检查退出码和输出；取消、断连或未知服务端写结果不等于写入回滚，不能直接自动重试。

## 远程连接 profile

完成 Server 首次安装并创建 `metrics` 后，使用具有相应数据库权限的 Token：

```bash
sndb remote \
  --url http://127.0.0.1:5080 \
  --database metrics \
  --token '<your-token>' \
  --save-profile dev

sndb remote --profile dev --command "SHOW MEASUREMENTS"
sndb remote --profile dev --repl
sndb remote list
sndb connect dev --command "SHOW MEASUREMENTS"
```

默认 profile 文件位于用户目录下 `.sndb/profiles.json`。保存远程 profile 涉及连接和 Token 信息，应限制文件访问权限并避免提交到代码仓库。示例 HTTP 地址只用于本机；远程部署应使用自己的 TLS 入口。

## 离线备份与恢复

在受支持的离线/维护窗口中使用：

```bash
sndb backup create --path ./demo-data --output ./demo-backup
sndb backup verify --path ./demo-backup
sndb backup dry-run --path ./demo-backup --target ./demo-restored
sndb backup restore --path ./demo-backup --target ./demo-restored
```

恢复写入新的数据库目录，不能用“导出 CSV”替代完整目录恢复。Server 的 SonnetMQ 使用实例级 `.system/mq`，这组单库命令不能称为完整 MQ 实例灾备。

后续文档：[CLI 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/cli-reference.md)、[CLI 命令实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Cli/CliApplication.cs)、[profile 存储](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Cli/CliProfileStore.cs)和[备份恢复](https://github.com/IoTSharp/SonnetDB/blob/main/docs/backup-restore.md)。本文进行了文档/源码核对，没有在本次整理中执行安装或连接命令。
