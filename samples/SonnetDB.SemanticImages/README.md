# SonnetDB Semantic Images Sample

该样例展示完整的工业图片语义检索流程：

- 创建数据库和对象桶；
- 显式开启 Bucket 异步摄取与 WebP 缩略图，默认值仍保持关闭；
- 上传图片并等待持久化任务完成；
- 执行无过滤文搜图，观察 `usearch` 或当前平台的 managed HNSW；
- 执行 metadata 过滤文搜图，观察 `exact-filtered` 与候选统计；
- 执行图搜图和 similar-by-id。

未指定图片目录或目录为空时，程序会生成叉车、泵组和输送线三张确定性 PNG。生产评估应通过 `--images` 指向真实现场图片集。

先启动并配置好 SigLIP2 模型的 SonnetDB Server，然后运行：

```powershell
$env:SONNETDB_TOKEN='<admin-or-database-token>'
dotnet run --project samples/SonnetDB.SemanticImages
```

使用真实图片目录和中文查询：

```powershell
dotnet run --project samples/SonnetDB.SemanticImages -- `
  --token $env:SONNETDB_TOKEN `
  --server http://127.0.0.1:5080 `
  --images D:\industrial-images `
  --text '夜间车道上的红色重型卡车'
```

也可使用环境变量 `SONNETDB_URL`、`SONNETDB_DATABASE`、`SONNETDB_BUCKET`、`SONNETDB_IMAGE_DIR`、`SONNETDB_TEXT_QUERY` 和 `SONNETDB_PROCESSING_TIMEOUT_SECONDS`。

样例不下载模型、不保存 Token，也不绕过服务端权限。项目的 JSON 合同使用 source generation，可随 Native AOT 发布：

```powershell
dotnet publish samples/SonnetDB.SemanticImages -c Release -r win-x64 -p:PublishAot=true
```
