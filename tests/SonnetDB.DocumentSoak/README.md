# Document Store Soak Profiles

本工具是 Milestone 25 / #174 的发布验收 runner，不进入主 CI gate。它依次执行：批量写入、索引创建、索引查询、在线 rebuild、TTL 清理、热重开、独立子进程冷启动、子进程异常退出恢复、备份恢复，以及工作集 / private bytes / managed heap 采样。冷启动表示新进程首次打开，runner 不尝试用高权限命令清空 OS page cache，报告会显式记录该边界。

## Profiles

| Profile | 文档数 | 用途 |
|---|---:|---|
| `quick` | 10,000 | 开发机冒烟与 runner 回归 |
| `million` | 1,000,000 | 发布候选容量验收 |
| `ten-million` | 10,000,000 | 专用长测机边界验证 |

```powershell
dotnet run --project tests/SonnetDB.DocumentSoak/SonnetDB.DocumentSoak.csproj -c Release -- `
  --profile million `
  --output artifacts/document-soak/million
```

可用 `--documents N` 做自定义规模，`--work-root PATH --keep-data` 保留数据库、备份和恢复目录供取证。输出固定包含 `report.json` 和 `report.md`。运行失败时仍写报告，并以非零退出码结束。

百万 / 千万档必须在专用磁盘和固定硬件上执行，报告需与 commit SHA、OS、.NET runtime、CPU 数及磁盘型号一起归档。不得把 quick profile 数字线性外推成容量承诺。
